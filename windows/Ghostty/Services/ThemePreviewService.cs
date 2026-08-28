using System;
using System.Buffers;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Pipes;
using Microsoft.Extensions.Logging;
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
internal sealed partial class ThemePreviewService : IDisposable
{
    // Path.GetInvalidFileNameChars() allocates a fresh char[] on each
    // call (defensive copy); SearchValues caches the set once and picks
    // an optimal scanner for the 41 invalid chars on Windows.
    private static readonly SearchValues<char> InvalidFileNameChars =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    private readonly ConfigService _configService;
    private readonly DispatcherQueue _dispatcher;
    private readonly Ghostty.Core.Themes.InlineThemePreviewSession _session;
    private readonly ILogger<ThemePreviewService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _serverTask;
    private bool _disposed;

    /// <summary>
    /// Raised on the UI thread when a CLI process sends LIST_THEMES
    /// over the pipe, requesting the in-process theme picker.
    /// </summary>
    public event EventHandler? ListThemesRequested;

    public static string PipeName { get; } =
        $"ghostty-theme-preview-{Environment.ProcessId}";

    /// <param name="session">
    /// The process's theme browse, shared with the inline picker. This
    /// service used to keep saved colours of its own, which made it a second
    /// snapshotter of the same process-wide palette that did not know the
    /// picker existed: a TUI client could snapshot a theme the picker was
    /// only browsing, and the picker's cancel could then revert over a theme
    /// the TUI had confirmed.
    /// </param>
    public ThemePreviewService(
        ConfigService configService,
        DispatcherQueue dispatcher,
        Ghostty.Core.Themes.InlineThemePreviewSession session,
        ILogger<ThemePreviewService> logger)
    {
        _configService = configService;
        _dispatcher = dispatcher;
        _session = session;
        _logger = logger;
    }

    /// <summary>
    /// Begin serving the pipe. Idempotent, and a no-op once
    /// <see cref="Dispose"/> has run. UI thread only, like every other
    /// call into this service from the shell.
    /// </summary>
    // Separate from construction because the pipe's existence is the
    // readiness signal the CLI reads: `wintty +list-themes` probes for
    // \\.\pipe\ghostty-theme-preview-{pid} with File.Exists, and treats a
    // successful write to it as delivery -- it never waits for an answer. So
    // a pipe opened before any window is able to draw a picker turns the
    // CLI's fallback (libghostty's own TUI picker, which it runs when it
    // finds no pipe) into a silent exit 0 that does nothing at all. The
    // caller starts this when the first window registers.
    internal void Start()
    {
        if (_disposed || _serverTask is not null) return;
        _serverTask = Task.Run(() => RunServer(_cts.Token));
    }

    /// <summary>
    /// Ends the accept loop and drops subscribers. Called once, from the
    /// process shutdown that runs after the last window closes.
    /// </summary>
    // Synchronous, and there is no DisposeAsync to prefer: the shutdown that
    // calls this is a `void` event handler with nothing to await it, so an
    // async variant would have had no caller able to use it.
    //
    // Waiting on the server task from the UI thread cannot deadlock. Every
    // await in the loop -- WaitForConnectionAsync, ReadLineAsync, the backoff
    // Delay -- takes the token, so the cancel below completes all three, and
    // each is ConfigureAwait(false), so no continuation wants the thread that
    // is blocked here. The loop also hands UI work over with TryEnqueue and
    // does not wait for it.
    //
    // Bounded, but not instantly: the token reaches none of the synchronous
    // work between those awaits, and the long piece of it is reading the
    // theme file (File.Exists plus ParseThemeFile's File.ReadLines) for a
    // PREVIEW that arrived just before the cancel. That is one small file
    // from the config directory, which can be a redirected or UNC path, so
    // the real bound is a file read that may have to time out. A timeout on
    // the wait would not improve on that: it would return with the accept
    // loop still running into the state this shutdown is freeing.
    public void Dispose()
    {
        // Callers get a Dispose that can be called twice, per the BCL
        // contract. Without this the second call cancels a disposed
        // CancellationTokenSource and throws ObjectDisposedException.
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        try { _serverTask?.GetAwaiter().GetResult(); }
        catch { /* expected OCE or pipe error */ }
        _cts.Dispose();

        // Drop subscribers. The loop is over by now, but the event is a root
        // like any other and this service is only ever disposed on the way out
        // of the process.
        ListThemesRequested = null;
    }

    // Decides what to do after each server-loop iteration. Extracted to
    // Ghostty.Core so the retry/stand-down logic is unit-tested: the bug
    // that filled the disk was this loop treating a server-creation failure
    // ("All pipe instances are busy") like a client disconnect and retrying
    // it instantly, forever.
    private readonly PipeServerRetryPolicy _retryPolicy = new();

    private async Task RunServer(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var outcome = await RunOneServerSession(ct).ConfigureAwait(false);
            switch (_retryPolicy.Decide(outcome))
            {
                case PipeLoopDecision.Stop:
                case PipeLoopDecision.StandDown:
                    return;
                case PipeLoopDecision.RetryAfterBackoff:
                    try { await Task.Delay(_retryPolicy.Backoff, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    break;
                case PipeLoopDecision.RetryImmediately:
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Runs one accept-serve cycle of the named-pipe server and classifies
    /// how it ended. Never throws -- cancellation and every fault, from
    /// creating the server through serving it, are mapped to outcomes; the
    /// caller's policy decides whether to retry, back off, or stand down.
    /// </summary>
    private async Task<PipeLoopOutcome> RunOneServerSession(CancellationToken ct)
    {
        // Creating the server is its own failure mode. FirstPipeInstance is
        // what makes the name exclusive, so the ctor throws "All pipe
        // instances are busy" when anything else already holds it -- another
        // process squatting the name, or a handle from this process that has
        // not been released yet. Neither clears by retrying on this loop, so
        // the policy stands it down rather than spinning.
        NamedPipeServerStream server;
        try
        {
            server = new NamedPipeServerStream(
                PipeName,
                PipeDirection.In,
                1, // single instance
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
        }
        catch (IOException ex)
        {
            _logger.LogPipeServerUnavailable(ex);
            return PipeLoopOutcome.ServerCreationFailed;
        }
        catch (Exception ex)
        {
            // Everything else the ctor can raise -- an ACL that denies the
            // create, a disposal race during teardown, an allocation failure
            // -- is a creation failure too, and this try sits outside the
            // broad catch below, so without this it escapes into the
            // fire-and-forget server task instead. A faulted task there is
            // silent: nothing observes it, .NET Core no longer tears the
            // process down for it, and +list-themes is dead for the rest of
            // the session with nothing in the log to say why. Logged through
            // the general pipe-error message rather than the stand-down one,
            // which describes the collision case and would misreport this.
            _logger.LogPipeError(ex);
            return PipeLoopOutcome.ServerCreationFailed;
        }

        try
        {
            using (server)
            {
                _logger.LogPipeWaiting(PipeName);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _logger.LogClientConnected();

                // No snapshot on connect. The session takes one before the
                // first preview and not before, so a client that connects and
                // sends nothing but LIST_THEMES leaves the slot alone -- and,
                // more to the point, a client that connects while a picker is
                // already browsing does not overwrite what that browse has to
                // put back.
                using var reader = new StreamReader(server);
                var confirmed = false;

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
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
                        NotePreview();
                        ApplyThemePreview(themeName);
                    }
                    else if (line.StartsWith("CONFIRM:", StringComparison.Ordinal))
                    {
                        var themeName = line[8..];
                        NoteConfirm();
                        ApplyThemePreview(themeName);
                        confirmed = true;
                    }
                }

                if (!confirmed)
                {
                    _logger.LogPreviewCancelled();
                    RevertColors();
                }
                else
                {
                    _logger.LogPreviewConfirmed();
                }
            }

            return PipeLoopOutcome.SessionEnded;
        }
        catch (OperationCanceledException)
        {
            return PipeLoopOutcome.Cancelled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A connected client dropped, the pipe broke mid-session, or the
            // stream was disposed under a teardown race. Map every such fault
            // to an outcome so this method honors its "never throws" contract
            // and the policy's fault bound always applies -- an escaping
            // exception would otherwise propagate into the fire-and-forget
            // server task and skip the bounded retry entirely. A cancellation
            // that surfaces as a non-OCE here (it can, depending on pipe
            // state) is treated as a fault: harmless, since the next loop turn
            // observes the token and the backoff delay cancels immediately.
            _logger.LogPipeError(ex);
            return PipeLoopOutcome.SessionFaulted;
        }
    }

    /// <summary>
    /// The live colours, as a snapshot a cancel can restore.
    /// </summary>
    private Ghostty.Core.Themes.ThemePreviewColors CaptureColors() => new(
        _configService.ForegroundColor,
        _configService.BackgroundColor,
        _configService.CursorColor,
        _configService.CursorTextColor,
        _configService.AnsiPalette);

    // The three session verbs, each dispatched. The session is UI-thread
    // state -- the inline picker drives it from the keystroke that opened the
    // picker -- and this loop runs on a pipe thread, so touching it here would
    // race the picker rather than share a slot with it. Ordering survives the
    // hop: ApplyThemePreview enqueues its own apply from this thread too, so
    // a record queued just before it still lands first, and the snapshot is
    // taken before the colours it describes are overwritten.

    private void NotePreview() =>
        _dispatcher.TryEnqueue(() => _session.NotePreview(CaptureColors));

    private void NoteConfirm() => _dispatcher.TryEnqueue(_session.NoteConfirm);

    private void RevertColors() =>
        _dispatcher.TryEnqueue(() =>
        {
            if (_session.End() is not { } restore) return;
            _configService.ApplyThemeColors(
                restore.Foreground,
                restore.Background,
                restore.Cursor,
                restore.CursorText,
                restore.Palette);
        });

    internal void ApplyThemePreview(string themeName)
    {
        // Validate: theme names are filenames, reject anything suspicious.
        if (themeName.Length > 255 ||
            themeName.Contains("..") ||
            themeName.AsSpan().IndexOfAny(InvalidFileNameChars) >= 0)
        {
            _logger.LogInvalidThemeName(themeName);
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

internal static partial class ThemePreviewServiceLogExtensions
{
    // Message keeps the pipe name as a structured parameter; the UNC
    // prefix (\\.\pipe\) is documented here rather than in the text so
    // the Microsoft.Extensions.Logging source generator does not have
    // to re-emit a backslash-heavy format string (CS1009 escape errors).
    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.ThemePreview.PipeWaiting,
                   Level = LogLevel.Debug,
                   Message = "[theme-preview] pipe server waiting (pipe name={PipeName})")]
    internal static partial void LogPipeWaiting(
        this ILogger<ThemePreviewService> logger, string pipeName);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.ThemePreview.ClientConnected,
                   Level = LogLevel.Debug,
                   Message = "[theme-preview] client connected")]
    internal static partial void LogClientConnected(
        this ILogger<ThemePreviewService> logger);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.ThemePreview.PreviewCancelled,
                   Level = LogLevel.Debug,
                   Message = "[theme-preview] cancelled, reverting")]
    internal static partial void LogPreviewCancelled(
        this ILogger<ThemePreviewService> logger);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.ThemePreview.PreviewConfirmed,
                   Level = LogLevel.Debug,
                   Message = "[theme-preview] confirmed")]
    internal static partial void LogPreviewConfirmed(
        this ILogger<ThemePreviewService> logger);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.ThemePreview.PipeError,
                   Level = LogLevel.Warning,
                   Message = "[theme-preview] pipe error")]
    internal static partial void LogPipeError(
        this ILogger<ThemePreviewService> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.ThemePreview.PipeServerUnavailable,
                   Level = LogLevel.Information,
                   Message = "[theme-preview] pipe server unavailable; standing down (another instance owns it)")]
    internal static partial void LogPipeServerUnavailable(
        this ILogger<ThemePreviewService> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.ThemePreview.InvalidThemeName,
                   Level = LogLevel.Warning,
                   Message = "[theme-preview] rejected invalid name: {ThemeName}")]
    internal static partial void LogInvalidThemeName(
        this ILogger<ThemePreviewService> logger, string themeName);
}
