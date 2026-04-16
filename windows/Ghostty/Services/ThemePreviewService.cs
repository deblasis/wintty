using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace Ghostty.Services;

/// <summary>
/// Named pipe server that receives theme preview requests from the
/// +list-themes TUI process. When the TUI callback writes a theme
/// name to the pipe, this service loads the theme file and updates
/// <see cref="ShellThemeService"/> so the app chrome previews the
/// selected theme live.
///
/// Protocol (UTF-8 text, newline-delimited):
///   "PREVIEW:ThemeName\n"  -- transient preview while browsing
///   "CONFIRM:ThemeName\n"  -- user accepted the theme
///   (pipe closed)          -- user cancelled, revert to original
/// </summary>
internal sealed class ThemePreviewService : IAsyncDisposable, IDisposable
{
    // Path.GetInvalidFileNameChars() allocates a fresh char[] on each
    // call (defensive copy); SearchValues caches the set once and picks
    // an optimal scanner for the 41 invalid chars on Windows.
    private static readonly SearchValues<char> InvalidFileNameChars =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    private readonly ConfigService _configService;
    private readonly DispatcherQueue _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private Task? _serverTask;

    /// <summary>
    /// Raised on the UI thread when a CLI process sends LIST_THEMES
    /// over the pipe, requesting the in-process theme picker.
    /// </summary>
    public event EventHandler? ListThemesRequested;

    // Saved palette so we can revert on cancel.
    private uint _savedBg, _savedFg;
    private uint? _savedCursor, _savedCursorText;
    private uint[]? _savedPalette;

    public static string PipeName { get; } =
        $"ghostty-theme-preview-{Environment.ProcessId}";

    public ThemePreviewService(
        ConfigService configService,
        DispatcherQueue dispatcher)
    {
        _configService = configService;
        _dispatcher = dispatcher;
        _serverTask = Task.Run(() => RunServer(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_serverTask is not null)
        {
            try { await _serverTask; }
            catch (OperationCanceledException) { }
            catch (Exception) { /* server loop handles its own errors */ }
        }
        _cts.Dispose();
    }

    public void Dispose()
    {
        _cts.Cancel();
        // Best-effort synchronous wait -- prefer DisposeAsync.
        try { _serverTask?.GetAwaiter().GetResult(); }
        catch { /* expected OCE or pipe error */ }
        _cts.Dispose();
    }

    private async Task RunServer(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1, // single instance
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);

                Debug.WriteLine($"[theme-preview] pipe server waiting: \\\\.\\pipe\\{PipeName}");
                await server.WaitForConnectionAsync(ct);
                Debug.WriteLine("[theme-preview] client connected");

                // Snapshot current colors for revert on cancel.
                SaveCurrentColors();

                using var reader = new StreamReader(server);
                var confirmed = false;

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break; // pipe closed

                    if (line == "LIST_THEMES")
                    {
                        // The CLI process wants us to run the in-process
                        // theme picker on the focused surface.
                        _dispatcher.TryEnqueue(() =>
                            ListThemesRequested?.Invoke(this, EventArgs.Empty));
                        confirmed = true; // don't revert on close
                        break;
                    }
                    else if (line.StartsWith("PREVIEW:", StringComparison.Ordinal))
                    {
                        var themeName = line[8..];
                        ApplyThemePreview(themeName);
                    }
                    else if (line.StartsWith("CONFIRM:", StringComparison.Ordinal))
                    {
                        var themeName = line[8..];
                        ApplyThemePreview(themeName);
                        confirmed = true;
                    }
                }

                if (!confirmed)
                {
                    Debug.WriteLine("[theme-preview] cancelled, reverting");
                    RevertColors();
                }
                else
                {
                    Debug.WriteLine("[theme-preview] confirmed");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                // Pipe broken, client disconnected. Loop back to accept
                // next connection.
                Debug.WriteLine($"[theme-preview] pipe error: {ex.Message}");
            }
        }
    }

    private void SaveCurrentColors()
    {
        _savedBg = _configService.BackgroundColor;
        _savedFg = _configService.ForegroundColor;
        _savedCursor = _configService.CursorColor;
        _savedCursorText = _configService.CursorTextColor;
        _savedPalette = (uint[])_configService.AnsiPalette.Clone();
    }

    private void RevertColors()
    {
        if (_savedPalette is null) return;
        _dispatcher.TryEnqueue(() =>
        {
            _configService.ApplyThemeColors(
                _savedFg, _savedBg, _savedCursor, _savedCursorText, _savedPalette);
        });
    }

    internal void ApplyThemePreview(string themeName)
    {
        // Validate: theme names are filenames, reject anything suspicious.
        if (themeName.Length > 255 ||
            themeName.Contains("..") ||
            themeName.AsSpan().IndexOfAny(InvalidFileNameChars) >= 0)
        {
            Debug.WriteLine($"[theme-preview] rejected invalid name: {themeName}");
            return;
        }

        var configDir = Path.GetDirectoryName(_configService.ConfigFilePath);
        if (configDir is null) return;
        var themePath = Path.Combine(configDir, "themes", themeName);
        if (!File.Exists(themePath)) return;

        var (palette, fg, bg, cursor, cursorText) = ParseThemeFile(themePath);

        // If called from the UI thread (inline picker dispatch), apply
        // directly. If called from a background thread (pipe server),
        // dispatch to UI thread.
        if (_dispatcher.HasThreadAccess)
            _configService.ApplyThemeColors(fg, bg, cursor, cursorText, palette);
        else
            _dispatcher.TryEnqueue(() =>
                _configService.ApplyThemeColors(fg, bg, cursor, cursorText, palette));
    }

    private static (uint[] palette, uint fg, uint bg, uint? cursor, uint? cursorText) ParseThemeFile(string path)
    {
        uint[] palette = new uint[16];
        uint[] defaults =
        [
            0x000000, 0xCC0000, 0x00CC00, 0xCCCC00,
            0x0000CC, 0xCC00CC, 0x00CCCC, 0xCCCCCC,
            0x666666, 0xFF0000, 0x00FF00, 0xFFFF00,
            0x0000FF, 0xFF00FF, 0x00FFFF, 0xFFFFFF,
        ];
        Array.Copy(defaults, palette, 16);
        uint fg = 0xCCCCCC, bg = 0x000000;
        uint? cursor = null;
        uint? cursorText = null;

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) continue;
            var eq = trimmed.IndexOf('=');
            if (eq < 0) continue;
            var key = trimmed[..eq].Trim();
            var val = trimmed[(eq + 1)..].Trim();

            if (key == "foreground") { if (TryParseHex(val, out var c)) fg = c; }
            else if (key == "background") { if (TryParseHex(val, out var c)) bg = c; }
            else if (key == "cursor-color") { if (TryParseHex(val, out var c)) cursor = c; }
            else if (key == "cursor-text") { if (TryParseHex(val, out var c)) cursorText = c; }
            else if (key == "palette")
            {
                var peq = val.IndexOf('=');
                if (peq < 0) continue;
                if (int.TryParse(val[..peq].Trim(), out var idx) && idx is >= 0 and < 16)
                    if (TryParseHex(val[(peq + 1)..].Trim(), out var c)) palette[idx] = c;
            }
        }

        return (palette, fg, bg, cursor, cursorText);
    }

    private static bool TryParseHex(string s, out uint color)
    {
        color = 0;
        if (string.IsNullOrEmpty(s)) return false;
        if (s.StartsWith('#')) s = s[1..];
        return s.Length == 6 &&
            uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out color);
    }
}
