using System;
using Ghostty.Core.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Shell;

/// <summary>
/// Decides when the pre-XAML <see cref="SplashWindow"/> may be taken
/// down, and takes it down.
///
/// <para>Two things must both be true before the splash can go, and
/// neither alone is enough:</para>
///
/// <list type="number">
/// <item>WinUI has composed a frame. Before this the window is not
/// drawing at all.</item>
/// <item>The terminal's swap chain has presented. WinUI composes for
/// roughly two seconds before the DX12 surface presents anything, and
/// the surface reads as pure black for that whole stretch -- so
/// dismissing on composition alone uncovers exactly the black gap the
/// splash exists to hide.</item>
/// </list>
///
/// <para>The second condition arrives as libghostty's
/// <c>first_render</c>. That signal is gated on the terminal having
/// produced content, so a surface whose shell never writes anything
/// will never raise it. Hence the grace period: once composition has
/// happened, the splash waits a bounded time for the surface and then
/// gives up rather than outstaying its welcome.</para>
///
/// <para>Lifted out of MainWindow for the same reason as
/// <see cref="LayoutCoordinator"/>: the window is a composition root,
/// not a scheduler.</para>
/// </summary>
internal sealed class LaunchIconCoordinator
{
    /// <summary>
    /// How long to wait for the surface to present after WinUI has
    /// composed. Sized to cover the observed compose-to-present gap with
    /// headroom; past this the splash comes down regardless, on the
    /// assumption the surface has nothing to draw.
    /// </summary>
    private const int SurfaceGraceMs = 2500;

    private readonly DispatcherQueue _dispatcher;
    private readonly LaunchIconPolicy _policy = new();

    private DispatcherQueueTimer? _timer;
    private bool _armed;
    private bool _composed;
    private bool _surfacePresented;

    public LaunchIconCoordinator(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Start watching for the first composed frame. Idempotent, so
    /// re-entrancy during window construction cannot subscribe twice.
    /// </summary>
    public void Arm()
    {
        if (_armed) return;
        _armed = true;
        CompositionTarget.Rendering += OnFirstComposedFrame;
    }

    /// <summary>
    /// Stop watching, without dismissing. For a window that closes before
    /// it ever composed a frame: <see cref="CompositionTarget.Rendering"/>
    /// is a static event, so an armed coordinator that is never torn down
    /// keeps this object and its window alive, and later fires against some
    /// other window's frame and schedules work on a dead dispatcher.
    /// </summary>
    public void Cancel()
    {
        if (!_armed) return;
        _armed = false;
        CompositionTarget.Rendering -= OnFirstComposedFrame;
        StopTimer();
    }

    /// <summary>
    /// The seed surface reported its first render. Safe before
    /// <see cref="Arm"/>, safe before composition, and safe more than
    /// once.
    /// </summary>
    public void NotifyReady()
    {
        if (!_armed) return;
        _surfacePresented = true;
        if (_composed) DismissNow();
    }

    private void OnFirstComposedFrame(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnFirstComposedFrame;
        _composed = true;

        if (_surfacePresented)
        {
            DismissNow();
            return;
        }

        _timer = RunAfter(SurfaceGraceMs, DismissNow);
    }

    private void DismissNow()
    {
        // Elapsed is measured from when the splash went up, not from this
        // window's construction. On a cold start it has been on screen for
        // seconds by now, so the minimum-dwell clause is already satisfied
        // and dismissal is immediate. That clause only bites on a startup
        // fast enough that the splash would otherwise flash.
        Apply(_policy.Ready(SplashWindow.VisibleForMs));
    }

    private void Apply(LaunchIconDecision decision)
    {
        switch (decision.Outcome)
        {
            case LaunchIconOutcome.FadeNow:
                Dismiss();
                break;
            case LaunchIconOutcome.FadeAfter:
                StopTimer();
                _timer = RunAfter(decision.DelayMs, Dismiss);
                break;
            case LaunchIconOutcome.Ignore:
            default:
                break;
        }
    }

    private void Dismiss()
    {
        StopTimer();
        SplashWindow.Dismiss();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    private DispatcherQueueTimer RunAfter(int delayMs, Action action)
    {
        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(delayMs);
        timer.IsRepeating = false;
        timer.Tick += (t, _) => { t.Stop(); action(); };
        timer.Start();
        return timer;
    }
}
