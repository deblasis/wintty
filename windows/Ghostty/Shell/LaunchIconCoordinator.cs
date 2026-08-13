using System;
using System.Diagnostics;
using Ghostty.Core.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Ghostty.Shell;

/// <summary>
/// Owns the cold-start launch icon: the bitmap assignment, the
/// minimum-on-screen stopwatch, the watchdog timer, and the fade
/// Storyboard. <see cref="LaunchIconPolicy"/> makes every timing
/// decision; this type only carries them out.
///
/// Lifted out of MainWindow for the same reason as
/// <see cref="LayoutCoordinator"/>: the window is a composition root,
/// not an animation host.
/// </summary>
internal sealed class LaunchIconCoordinator
{
    public const int FadeDurationMs = 250;

    // The icon grows slightly as it dissolves. A straight opacity fade
    // reads as the icon being switched off; the drift outward reads as
    // it giving way to the content behind it, which is the Windows 11
    // splash-to-content feel we are copying.
    private const double ExitScale = 1.08;

    private readonly Microsoft.UI.Xaml.Controls.Image _icon;
    private readonly DispatcherQueue _dispatcher;
    private readonly LaunchIconPolicy _policy = new();
    private readonly Stopwatch _onScreen = new();

    private DispatcherQueueTimer? _watchdog;
    private DispatcherQueueTimer? _deferred;
    private Storyboard? _fade;
    private bool _armed;
    // True once the compositor has produced a frame, which is the first
    // moment the icon is actually on screen. Until then the clocks are
    // not running and a ready signal is held rather than acted on.
    private bool _onScreenYet;
    private bool _readyWhileHidden;

    public LaunchIconCoordinator(
        Microsoft.UI.Xaml.Controls.Image icon, DispatcherQueue dispatcher)
    {
        _icon = icon;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Show the icon and wait for the compositor to put it on screen.
    /// Idempotent: a second call is ignored, so re-entrancy during
    /// window construction cannot restart the timing.
    /// </summary>
    public void Arm()
    {
        if (_armed) return;
        _armed = true;

        // Decoded here rather than in XAML so warm windows, which never
        // arm, never pay for the bitmap.
        _icon.Source = new BitmapImage(Ghostty.Branding.AppIconSource.Splash);

        // Full opacity from the first frame. The window itself is
        // appearing at this moment, so fading the icon in would only
        // soften the window's own entrance.
        _icon.Opacity = 1;
        _icon.Visibility = Visibility.Visible;

        // Arm() runs during the window constructor, and WinUI shows the
        // HWND well before it composes a first XAML frame -- measured at
        // roughly two seconds on a cold Debug start. Counting the dwell
        // and the watchdog from here would spend most of both budgets
        // while the icon is not yet on screen, so the icon could fade
        // out before the user ever saw it. Start the clocks on the first
        // composed frame instead, which is the first moment the icon is
        // genuinely visible.
        CompositionTarget.Rendering += OnFirstComposedFrame;
    }

    /// <summary>
    /// The first surface reported that it has rendered. Safe to call
    /// before <see cref="Arm"/> (does nothing), before the icon is on
    /// screen (held until it is), and more than once (the policy
    /// latches).
    /// </summary>
    public void NotifyReady()
    {
        if (!_armed) return;
        if (!_onScreenYet)
        {
            _readyWhileHidden = true;
            return;
        }
        Apply(_policy.Ready((int)_onScreen.ElapsedMilliseconds));
    }

    private void OnFirstComposedFrame(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnFirstComposedFrame;
        _onScreenYet = true;
        _onScreen.Start();
        _watchdog = RunAfter(LaunchIconPolicy.WatchdogMs, () => Apply(_policy.Timeout()));

        // The surface beat us to the compositor. Zero elapsed, so the
        // policy grants the full minimum dwell from this frame.
        if (_readyWhileHidden) Apply(_policy.Ready(0));
    }

    private void Apply(LaunchIconDecision decision)
    {
        switch (decision.Outcome)
        {
            case LaunchIconOutcome.FadeNow:
                StartFade();
                break;
            case LaunchIconOutcome.FadeAfter:
                StopTimers();
                _deferred = RunAfter(decision.DelayMs, StartFade);
                break;
            case LaunchIconOutcome.Ignore:
            default:
                break;
        }
    }

    private void StartFade()
    {
        StopTimers();
        _onScreen.Stop();

        var scale = EnsureScale(_icon);
        var sb = new Storyboard();
        sb.Children.Add(Tween(_icon, "Opacity", 1, 0));
        sb.Children.Add(Tween(scale, "ScaleX", 1, ExitScale));
        sb.Children.Add(Tween(scale, "ScaleY", 1, ExitScale));

        sb.Completed += (_, _) =>
        {
            // Collapse rather than leaving a transparent element in the
            // tree: the icon is never shown again for this window's
            // lifetime, and a collapsed element costs nothing in layout.
            _icon.Visibility = Visibility.Collapsed;
            _icon.Opacity = 0;
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            _fade = null;
        };

        _fade = sb;
        sb.Begin();
    }

    private void StopTimers()
    {
        _watchdog?.Stop();
        _watchdog = null;
        _deferred?.Stop();
        _deferred = null;
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

    // Scale about the centre so the icon grows in place instead of
    // drifting toward the bottom-right.
    private static ScaleTransform EnsureScale(FrameworkElement fe)
    {
        if (fe.RenderTransform is ScaleTransform existing) return existing;
        var scale = new ScaleTransform();
        fe.RenderTransform = scale;
        fe.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        return scale;
    }

    private static DoubleAnimation Tween(DependencyObject target, string path, double from, double to)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(FadeDurationMs)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, path);
        return anim;
    }
}
