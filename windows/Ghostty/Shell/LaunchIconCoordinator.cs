using System;
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

    private DispatcherQueueTimer? _timer;
    private bool _armed;
    private bool _composed;
    private bool _surfacePresented;
    private bool _dismissed;

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
    /// Stop watching, because the window this was arming for is going away.
    /// </summary>
    /// <remarks>
    /// Unsubscribing matters because <see cref="CompositionTarget.Rendering"/>
    /// is a static event: an armed coordinator that is never torn down keeps
    /// this object and its window alive, later fires against some other
    /// window's frame, and schedules work on a dead dispatcher.
    ///
    /// It dismisses on the way out rather than just unsubscribing. This
    /// coordinator is the only thing that can take the splash down, so
    /// dropping a pending dismissal without taking it down would strand an
    /// opaque topmost window over the remaining windows until the watchdog
    /// fires seconds later.
    /// </remarks>
    public void Cancel()
    {
        if (!_armed) return;
        _armed = false;
        CompositionTarget.Rendering -= OnFirstComposedFrame;
        StopTimer();
        Dismiss();
    }

    /// <summary>
    /// The seed surface reported its first render. Safe before
    /// <see cref="Arm"/>, safe before composition, and safe more than
    /// once.
    /// </summary>
    public void NotifyReady()
    {
        // Recorded whether or not this is armed yet, so the two signals are
        // order-independent as the summary above promises. Arming happens
        // first today and nothing enforces it; the cost of getting that
        // wrong is a full grace period spent over a window that already has
        // content.
        _surfacePresented = true;
        // No _armed check: composition can only have been observed through
        // a subscription this made, and the one path that disarms latches
        // the dismissal first.
        if (_composed) Dismiss();
    }

    private void OnFirstComposedFrame(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnFirstComposedFrame;
        _composed = true;

        if (_surfacePresented)
        {
            Dismiss();
            return;
        }

        _timer = RunAfter(SurfaceGraceMs, Dismiss);
    }

    /// <summary>
    /// Take the splash down, from any of the three things that end it: the
    /// terminal reported content, the grace expired waiting for content
    /// that is not coming, or the window it was covering is going away.
    ///
    /// <para>Nothing is held back on the way out. The splash exists to
    /// cover a black window, and once the reason for it has resolved --
    /// either way -- every further millisecond it stays up is spent
    /// covering the app the user is waiting for.</para>
    /// </summary>
    private void Dismiss()
    {
        // Latches: composition and the surface can both resolve in the same
        // frame, and Cancel can arrive on top of either.
        if (_dismissed) return;
        _dismissed = true;

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
