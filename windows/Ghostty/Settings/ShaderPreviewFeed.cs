using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Ghostty.Settings;

/// <summary>
/// Drives the shader picker's preview terminal with a canned session: a
/// scripted MS-DOS box that types itself, forever, with human-ish pacing.
/// The preview surface runs a silent placeholder child, so these VT bytes
/// are the only thing that ever reaches the grid: the content is
/// deterministic, it starts playing the moment the picker opens, and it
/// survives every shader flip (the feed never rebuilds the surface).
/// Mirrors the wintty.io/shaders demo: same DOS flavor, same pacing, same
/// cursor-shape flips that exercise the mode-change cursor shaders.
/// </summary>
internal sealed partial class ShaderPreviewFeed : IDisposable
{
    // SGR foregrounds only, never a background: the terminal theme is the
    // only background, so fullscreen shaders light up where text is drawn
    // instead of stopping at a palette-resolved bg cell (website lesson).
    private const string FgGray = "\x1b[37m";
    private const string FgBright = "\x1b[1;37m";
    private const string FgBlue = "\x1b[34;1m";

    // DECSCUSR: cursor shape flips are the exact event the mode-change
    // cursor shaders (ripple, boom) animate on.
    private const string CursorBlock = "\x1b[2 q";
    private const string CursorBar = "\x1b[5 q";
    private const string CursorUnderline = "\x1b[4 q";

    private const string Prompt = FgBlue + "C:\\>" + FgGray + " ";

    private const string Banner =
        "\r\n" +
        "Starting MS-DOS...\r\n" +
        "\r\n" +
        FgBright + "Microsoft(R) MS-DOS(R) Version 6.22\r\n" + FgGray +
        "(C)Copyright Microsoft Corp 1981-1994.\r\n" +
        "\r\n" +
        "WINTTY Shader Lab Extension v1.0 installed.\r\n" +
        "\r\n" +
        "Type HELP for the command list.\r\n" +
        "\r\n";

    private const string DirListing =
        "\r\n Volume in drive C is WINTTY\r\n" +
        "\r\n" +
        " IO       SYS      40,766  06-22-94\r\n" +
        " MSDOS    SYS      38,138  06-22-94\r\n" +
        " COMMAND  COM      54,619  06-22-94\r\n" +
        " AUTOEXEC BAT        214  06-22-94\r\n" +
        " CONFIG   SYS        168  06-22-94\r\n" +
        " WINTTY      <DIR>          06-22-94\r\n" +
        " CRT      GLS      1,842  06-22-94\r\n" +
        " SCANLINE GLS        916  06-22-94\r\n" +
        " SNOWFALL GLS      1,024  06-22-94\r\n" +
        " AURORA   GLS      2,048  06-22-94\r\n" +
        " PIPBOY   GLS      1,536  06-22-94\r\n" +
        "        10 file(s)     141,271 bytes\r\n" +
        "         2 dir(s)   33,554,432 bytes free\r\n" +
        "\r\n";

    private const string Autoexec =
        "\r\n" +
        "@ECHO OFF\r\n" +
        "PROMPT $p$g\r\n" +
        "SET SHADER=CRT.GLS\r\n" +
        "LH C:\\WINTTY\\SHADERLAB.EXE /GALLERY\r\n" +
        "\r\n";

    private const string VerReply =
        "\r\nMS-DOS Version 6.22\r\nwintty shader gallery, live preview\r\n\r\n";

    private const string EchoReply =
        "\r\nshaders make terminals fun\r\n\r\n";

    /// <summary>
    /// One beat of the loop: the command to type at the prompt (null for a
    /// bare cursor flip) and the VT bytes to emit once Enter lands. The
    /// echo of the typed characters is part of the beat.
    /// </summary>
    private readonly record struct Beat(string? Command, string Response);

    // Same shape as the website's demo script: a couple of listings, then
    // repeated cursor-shape flips (each one fires the cursor shaders), a
    // MODE pair that walks underline to block, and a closing echo.
    private static readonly Beat[] Script =
    {
        new("dir", DirListing),
        new("type autoexec.bat", Autoexec),
        new(null, CursorBar),
        new(null, CursorBlock),
        new(null, CursorBar),
        new("ver", VerReply),
        new(null, CursorBlock),
        new("mode cursor=underline", CursorUnderline),
        new("mode cursor=block", CursorBlock),
        new("echo shaders make terminals fun", EchoReply),
        new(null, CursorBar),
    };

    private readonly Controls.TerminalControl _terminal;
    // Fixed seed so the demo plays identically every time (matching the
    // website's per-character jitter without its nondeterminism).
    private readonly Random _random = new(1009);

    private CancellationTokenSource? _cts;

    public ShaderPreviewFeed(Controls.TerminalControl terminal)
    {
        _terminal = terminal;
    }

    /// <summary>Begin autoplay. Safe to call once per feed.</summary>
    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Boot text lands at once (it is a machine booting, not a
            // person), then the first command comes after a beat so the
            // window has settled and the shader is already visible.
            Write(Banner);
            Write(Prompt);
            await Task.Delay(1200, ct);

            while (true)
            {
                foreach (var beat in Script)
                {
                    if (beat.Command is { } command)
                    {
                        await TypeAsync(command, ct);
                        await Task.Delay(350, ct);
                        Write("\r\n" + beat.Response + Prompt);
                    }
                    else
                    {
                        // A flip is a keypress: pause before and after so
                        // the cursor-shape animation has time to read.
                        await Task.Delay(650, ct);
                        Write(beat.Response);
                    }
                }

                // Breathe between passes, then keep scrolling: the session
                // grows exactly like the website demo, and scrollback
                // bounds it (the configured scrollback limit).
                await Task.Delay(4000, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The picker closed; nothing to clean up beyond stopping.
        }
        catch (Exception ex)
        {
            // A feed glitch must never take the picker down; the preview
            // just stops playing. Log it so it is diagnosable.
            Logging.StaticLoggers.SettingsConfigWriter.LogInformation(
                "shader preview feed stopped: {Message}", ex.Message);
        }
    }

    // Type one character at a time so it looks hand-keyed, with the same
    // jitter band the website uses.
    private async Task TypeAsync(string text, CancellationToken ct)
    {
        foreach (var ch in text)
        {
            Write(ch.ToString());
            await Task.Delay(55 + _random.Next(70), ct);
        }
    }

    private void Write(string vt)
    {
        // WriteVt is UI-thread-only: its disposed guard is a non-volatile
        // field read followed by a native call on a surface DisposeSurface
        // frees from the UI thread. Today every write lands on the UI thread
        // because Start is driven from FirstRender (raised through
        // GhosttyHost's dispatcher) and every continuation below resumes on
        // that context. Assert it so a future change that moves the feed off
        // the UI thread fails loudly in Debug instead of silently racing a
        // free in Release.
        Debug.Assert(
            _terminal.DispatcherQueue.HasThreadAccess,
            "ShaderPreviewFeed must write from the UI thread; WriteVt is not thread-safe.");
        _terminal.WriteVt(Encoding.UTF8.GetBytes(vt));
    }
}
