using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.Settings;

/// <summary>
/// Drives the shader picker's preview terminal with the website's fake
/// MS-DOS session (wintty.io/shaders): an autoplay loop types the demo
/// script forever with human-ish pacing, and the user can click into the
/// preview and type freely. Both go through one <see cref="DosShellCore"/>,
/// so scripted text and human text are indistinguishable to the surface.
/// The preview surface runs a silent placeholder child, so these VT bytes
/// are the only thing that ever reaches the grid: the content stays
/// deterministic in shape, it starts playing the moment the picker opens,
/// and it survives every shader flip (the feed never rebuilds the
/// surface). Every user keystroke pauses the autoplay for a quiet window
/// (see <see cref="UserQuietWindow"/>); the loop resumes where it stopped
/// once the user goes quiet.
/// </summary>
/// <remarks>
/// UI-free and dependency-free, like <c>CustomShaderNoticeSource</c>: the
/// feed writes to a <see cref="VtSink"/> delegate and paces itself through a
/// <see cref="PacingDelay"/>, and reads the clock through a delegate so the
/// pause logic unit-tests without sleeping. The WinUI side supplies
/// <c>TerminalControl.WriteVt</c> and <c>Task.Delay</c>.
///
/// Not thread-safe, and the sink it is given generally is not either:
/// <c>WriteVt</c> is UI-thread-only. Start the feed on the UI thread so
/// every continuation resumes there, and route user keystrokes (WinUI
/// input events, same thread) through <see cref="KeyDown"/> and
/// <see cref="Character"/>; nothing here needs a lock because all of it
/// runs on that one thread.
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

    /// <summary>
    /// How long the demo holds still after the most recent user
    /// keystroke before autoplay resumes (the website's 10s quiet
    /// window).
    /// </summary>
    private static readonly TimeSpan UserQuietSpan = TimeSpan.FromSeconds(10);

    /// <summary>How often a held demo re-checks the quiet window.</summary>
    private const int UserQuietPollMs = 400;

    // Same shape as the website's demo script: a couple of listings, then
    // repeated Insert presses (each one fires the cursor shaders), a MODE
    // pair that walks underline to block, and a closing echo. A null
    // entry is a bare Insert press; the shell owns every reply.
    private static readonly string?[] Script =
    [
        "dir",
        "type autoexec.bat",
        null,
        null,
        null,
        "ver",
        null,
        null,
        "mode cursor=underline",
        "mode cursor=block",
        "echo shaders make terminals fun",
        null,
    ];

    private readonly VtSink _sink;
    private readonly PacingDelay _delay;
    private readonly ILogger<ShaderPreviewFeed> _logger;
    private readonly Func<DateTime> _clock;
    private readonly DosShellCore _core;
    private readonly UserQuietWindow _quiet;

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
        _clock = clock ?? DefaultClock;
        // One shell for both writers: the demo script and the user's own
        // keystrokes type into the same input line, recall the same
        // history, and flip the same cursor.
        _core = new DosShellCore(_clock);
        _quiet = new UserQuietWindow(_clock, UserQuietSpan);
    }

    private static DateTime DefaultClock() => DateTime.Now;

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

    // User input (IPreviewInputSink) -------------------------------------

    /// <summary>
    /// A non-printable key the user pressed into the preview. Same shell
    /// as the autoplay script (so her keys echo and execute exactly like
    /// the demo's), and every keystroke re-arms the quiet window that
    /// holds autoplay off.
    /// </summary>
    public bool KeyDown(DosShellKey key)
    {
        // After Dispose (the picker closing) a keystroke still in flight
        // must be dropped, not thrown into the UI thread.
        if (_disposed) return false;
        _quiet.Arm();
        Write(Vt(_core.SendKey(key)));
        return true;
    }

    /// <summary>
    /// Any key press into the preview, whether or not the shell maps it.
    /// The website stamps its idle clock on every keydown, consumed or
    /// not, so holding a key the fake shell ignores (Left, Right) pauses
    /// the demo exactly like typing does.
    /// </summary>
    public void NoteKeyDown()
    {
        if (_disposed) return;
        _quiet.Arm();
    }

    /// <summary>A character the user typed into the preview.</summary>
    public void Character(char ch)
    {
        if (_disposed) return;
        _quiet.Arm();
        Write(Vt(_core.SendChar(ch)));
    }

    // Autoplay ------------------------------------------------------------

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Boot text lands at once (it is a machine booting, not a
            // person), then the first command comes after a beat so the
            // window has settled and the shader is already visible.
            // 1500ms, the website's opening beat.
            Write(Vt(_core.Boot()));
            Write(Vt(_core.NewPrompt()));
            await _delay(1500, ct);

            while (true)
            {
                foreach (var step in Script)
                {
                    // Observe cancellation here rather than waiting for the
                    // next _delay to throw it. Without these checks the loop
                    // can still push a Write past Cancel(), which leans on
                    // the sink's own disposed guard for correctness; one
                    // guard carrying that weight is enough.
                    ct.ThrowIfCancellationRequested();
                    if (step is { } command)
                    {
                        await TypeAsync(command, ct);
                        ct.ThrowIfCancellationRequested();
                        await WaitForQuietAsync(ct);
                        // One write: Enter returns the newline, the shell's
                        // reply, and the next prompt together, which is
                        // exactly what one keyed Enter produces through the
                        // core.
                        Write(Vt(_core.SendKey(DosShellKey.Enter)));
                        // The website dwells AFTER the reply, before the
                        // next step: the reply sits on screen while the
                        // pause runs, not before its own newline.
                        await _delay(350, ct);
                    }
                    else
                    {
                        ct.ThrowIfCancellationRequested();
                        await WaitForQuietAsync(ct);
                        Write(Vt(_core.SendKey(DosShellKey.Insert)));
                        // The website dwells AFTER the flip: the pause is
                        // when the cursor-shape animation plays, which is
                        // the whole reason a flip is in the script at all.
                        await _delay(650, ct);
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
    // jitter band the website uses. The quiet window is checked per
    // character, so a keystroke lands between any two characters, not
    // between commands.
    private async Task TypeAsync(string text, CancellationToken ct)
    {
        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            await WaitForQuietAsync(ct);
            Write(Vt(_core.SendChar(ch)));
            await _delay(55 + _random.Next(70), ct);
        }
    }

    // Hold the demo while the user is typing: poll the quiet window until
    // it expires. Polling rather than sleeping the remaining span is what
    // makes a keystroke landing mid-hold extend it.
    private async Task WaitForQuietAsync(CancellationToken ct)
    {
        while (!_quiet.Expired)
        {
            ct.ThrowIfCancellationRequested();
            await _delay(UserQuietPollMs, ct);
        }
    }

    private void Write(ReadOnlySpan<byte> vt) => _sink(vt);

    private static byte[] Vt(string text) => Encoding.UTF8.GetBytes(text);

    // Its own category, not a borrowed one: raising the config writer's
    // category to Debug to chase a config bug must not also turn on shader
    // preview noise. Warning with the exception object, so the stack trace
    // survives; a feed that stopped is a preview that silently froze.
    [LoggerMessage(EventId = LogEvents.ShaderPreview.FeedStopped,
                   Level = LogLevel.Warning,
                   Message = "[ShaderPreviewFeed] shader preview feed stopped")]
    private partial void LogFeedStopped(Exception ex);
}
