using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.Settings;

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
/// <remarks>
/// UI-free and dependency-free, like <c>CustomShaderNoticeSource</c>: the
/// feed writes to a <see cref="VtSink"/> delegate and paces itself through a
/// <see cref="PacingDelay"/>, so the script, the ordering, and the
/// cancellation unit-test without a WinUI runtime or a UI thread. The WinUI
/// side supplies <c>TerminalControl.WriteVt</c> and <c>Task.Delay</c>.
///
/// Not thread-safe, and the sink it is given generally is not either:
/// <c>WriteVt</c> is UI-thread-only. Start the feed on the UI thread so
/// every continuation resumes there.
/// </remarks>
internal sealed partial class ShaderPreviewFeed : IDisposable, IPreviewInputSink
{
    /// <summary>
    /// Where the feed's VT bytes go: the preview terminal in the app, a
    /// recorder in tests. A bespoke delegate rather than
    /// <c>Action&lt;ReadOnlySpan&lt;byte&gt;&gt;</c> because a ref struct
    /// cannot be a type argument to the BCL's <c>Action&lt;T&gt;</c>.
    /// </summary>
    internal delegate void VtSink(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// The pacing hook, awaited for every keystroke gap and beat pause.
    /// Production passes <see cref="Task.Delay(int, CancellationToken)"/>;
    /// tests pass a completed task so a full pass of the script runs without
    /// spending wall-clock time on it.
    /// </summary>
    internal delegate Task PacingDelay(int milliseconds, CancellationToken ct);

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

    // The loop is endless, so every constant it writes is encoded once at
    // type load rather than re-encoded on every pass forever.
    //
    // "..."u8 literals are not available here: these constants are built by
    // const string concatenation, and u8 literals are neither const nor
    // concatenable, so static readonly byte[] is the shape that exists.
    private static readonly byte[] CrLfVt = Vt("\r\n");
    private static readonly byte[] PromptVt = Vt(Prompt);
    private static readonly byte[] BannerVt = Vt(Banner);
    private static readonly byte[] DirListingVt = Vt(DirListing);
    private static readonly byte[] AutoexecVt = Vt(Autoexec);
    private static readonly byte[] VerReplyVt = Vt(VerReply);
    private static readonly byte[] EchoReplyVt = Vt(EchoReply);
    private static readonly byte[] CursorBlockVt = Vt(CursorBlock);
    private static readonly byte[] CursorBarVt = Vt(CursorBar);
    private static readonly byte[] CursorUnderlineVt = Vt(CursorUnderline);

    private static byte[] Vt(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// One beat of the loop: the command to type at the prompt (null for a
    /// bare cursor flip) and the VT bytes to emit once Enter lands. The
    /// echo of the typed characters is part of the beat.
    /// </summary>
    private readonly record struct Beat(string? Command, byte[] Response);

    // Same shape as the website's demo script: a couple of listings, then
    // repeated cursor-shape flips (each one fires the cursor shaders), a
    // MODE pair that walks underline to block, and a closing echo.
    private static readonly Beat[] Script =
    [
        new("dir", DirListingVt),
        new("type autoexec.bat", AutoexecVt),
        new(null, CursorBarVt),
        new(null, CursorBlockVt),
        new(null, CursorBarVt),
        new("ver", VerReplyVt),
        new(null, CursorBlockVt),
        new("mode cursor=underline", CursorUnderlineVt),
        new("mode cursor=block", CursorBlockVt),
        new("echo shaders make terminals fun", EchoReplyVt),
        new(null, CursorBarVt),
    ];

    private readonly VtSink _sink;
    private readonly PacingDelay _delay;
    private readonly ILogger<ShaderPreviewFeed> _logger;
    // Fixed seed so the typing jitter is reproducible instead of a fresh
    // random walk per window. Reproducible within a runtime version, not
    // across them: Random(int) makes no cross-version stability promise, and
    // nothing here needs one.
    private readonly Random _random = new(1009);

    private CancellationTokenSource? _cts;
    private bool _disposed;

    internal ShaderPreviewFeed(
        VtSink sink,
        ILogger<ShaderPreviewFeed> logger,
        PacingDelay? delay = null,
        Func<DateTime>? clock = null)
    {
        _sink = sink;
        _logger = logger;
        _delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
        _clock = clock;
    }

    private readonly Func<DateTime>? _clock;

    public bool KeyDown(DosShellKey key) => throw new NotImplementedException();

    public void Character(char ch) => throw new NotImplementedException();

    /// <summary>
    /// Begin autoplay. Idempotent, and a no-op after <see cref="Dispose"/>:
    /// Dispose nulls the token source, so without the disposed flag a late
    /// Start (a FirstRender arriving during teardown) would hand a torn-down
    /// feed a fresh session to play into a sink that is going away.
    /// </summary>
    public void Start()
    {
        if (_disposed || _cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _disposed = true;
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
            Write(BannerVt);
            Write(PromptVt);
            await _delay(1200, ct);

            while (true)
            {
                foreach (var beat in Script)
                {
                    // Observe cancellation here rather than waiting for the
                    // next _delay to throw it. Without these checks the loop
                    // can still push a Write past Cancel(), which leans on
                    // the sink's own disposed guard for correctness; one
                    // guard carrying that weight is enough.
                    ct.ThrowIfCancellationRequested();
                    if (beat.Command is { } command)
                    {
                        await TypeAsync(command, ct);
                        await _delay(350, ct);
                        ct.ThrowIfCancellationRequested();
                        // Three writes rather than a concatenation: the
                        // newline, the response and the prompt are already
                        // encoded, so this beat allocates nothing.
                        Write(CrLfVt);
                        Write(beat.Response);
                        Write(PromptVt);
                    }
                    else
                    {
                        // A flip is a keypress: pause before and after so
                        // the cursor-shape animation has time to read.
                        await _delay(650, ct);
                        ct.ThrowIfCancellationRequested();
                        Write(beat.Response);
                    }
                }

                // Breathe between passes, then keep scrolling: the session
                // grows exactly like the website demo, and scrollback
                // bounds it (the configured scrollback limit).
                await _delay(4000, ct);
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
            LogFeedStopped(ex);
        }
    }

    // Type one character at a time so it looks hand-keyed, with the same
    // jitter band the website uses.
    private async Task TypeAsync(string text, CancellationToken ct)
    {
        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            WriteChar(ch);
            await _delay(55 + _random.Next(70), ct);
        }
    }

    private void Write(ReadOnlySpan<byte> vt) => _sink(vt);

    // One keystroke, encoded onto the stack: the typing path runs a few times
    // a second forever, and a string plus a byte[] per character is two
    // allocations for at most four bytes. Four is the ceiling for one char: a
    // BMP scalar encodes to at most three, and a lone surrogate encodes as
    // U+FFFD, which is three.
    private void WriteChar(char ch)
    {
        Span<byte> buffer = stackalloc byte[4];
        var written = Encoding.UTF8.GetBytes(new ReadOnlySpan<char>(in ch), buffer);
        _sink(buffer[..written]);
    }

    // Its own category, not a borrowed one: raising the config writer's
    // category to Debug to chase a config bug must not also turn on shader
    // preview noise. Warning with the exception object, so the stack trace
    // survives; a feed that stopped is a preview that silently froze.
    [LoggerMessage(EventId = LogEvents.ShaderPreview.FeedStopped,
                   Level = LogLevel.Warning,
                   Message = "[ShaderPreviewFeed] shader preview feed stopped")]
    private partial void LogFeedStopped(Exception ex);
}
