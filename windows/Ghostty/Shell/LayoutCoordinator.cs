using System;
using System.Diagnostics;
using Ghostty.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Ghostty.Shell;

/// <summary>
/// Owns every piece of state that drives the runtime switch
/// between the horizontal and vertical tab layouts: the cross-fade
/// Storyboard, the strip-column width tween, the snap-to-end-state
/// helper, and the concurrent-tween guard.
///
/// Lifted out of MainWindow so the window itself stays a thin
/// composition root. The two tab hosts and the vertical title bar
/// are owned by MainWindow's XAML and passed in via the ctor; this
/// type only animates and toggles them.
///
/// Why the column width tween is code-driven: WinUI 3 has no native
/// GridLengthAnimation. Star/Auto refactoring is a separate piece
/// of work tracked in the deferred review items list.
/// </summary>
internal sealed class LayoutCoordinator
{
    // Width of the icon cell (chevron, tab rows, new-tab control).
    // The new-tab split button stacks its dropdown chevron
    // vertically in this mode (Orientation=Vertical on
    // NewTabSplitButton), so the column can stay narrow. Must stay
    // in sync with the StripColumn Width in VerticalTabHost.xaml.
    public const double VerticalStripCollapsedWidth = 40;
    public const int SwitchDurationMs = 220;

    // Vertical-mode title bar height. The horizontal host slides this
    // far vertically during the cross-fade so the swap feels like the
    // strip lifting away. Must match VerticalTitleBar.Height in
    // MainWindow.xaml.
    private const double VerticalTitleBarHeight = 34;

    private readonly ColumnDefinition _stripColumn;
    private readonly ColumnDefinition _titleBarStripMirror;
    private readonly FrameworkElement _horizontalHost;
    private readonly VerticalTabHost _verticalTabHost;
    private readonly FrameworkElement _verticalHost;
    private readonly Grid _verticalTitleBar;
    // The wintty icon in each layout. Only the incoming one is spun on a
    // switch (see Animate), so the icon looks like the surrounding chrome
    // is shoving it into its new home.
    private readonly FrameworkElement _horizontalIcon;
    private readonly FrameworkElement _verticalIcon;

    private bool _switching;
    // When true (quake window with a single tab), the strip + vertical
    // title bar are forced hidden regardless of layout mode, leaving only
    // the pane host. Snap and Animate both honor it so the layout toggle
    // cannot resurrect the strip.
    private bool _stripHidden;
    // Quake window: the top VerticalTitleBar (layout-toggle chevron +
    // window title) is suppressed entirely. The wintty icon at the top of
    // the vertical strip becomes the topmost element above the tabs. Layout
    // switching still works via the keyboard chord.
    private bool _verticalTitleBarSuppressed;
    private DispatcherTimer? _columnTimer;

    public LayoutCoordinator(
        ColumnDefinition stripColumn,
        ColumnDefinition titleBarStripMirror,
        FrameworkElement horizontalHost,
        VerticalTabHost verticalTabHost,
        Grid verticalTitleBar,
        FrameworkElement horizontalIcon)
    {
        _stripColumn = stripColumn;
        _titleBarStripMirror = titleBarStripMirror;
        _horizontalHost = horizontalHost;
        _verticalTabHost = verticalTabHost;
        _verticalHost = verticalTabHost;
        _verticalTitleBar = verticalTitleBar;
        _horizontalIcon = horizontalIcon;
        _verticalIcon = verticalTabHost.IconBadge;

        // The chevron toggle inside VerticalTabHost asks the outer
        // shell to widen the strip column. Forward through the same
        // tween path so chevron + layout switch can never race.
        _verticalTabHost.StripWidthChangeRequested += (_, width) =>
        {
            TweenStripColumn(_stripColumn.Width.Value, width,
                onTick: v => _verticalTabHost.SetInternalStripWidth(v));
        };
    }

    public bool IsSwitching => _switching;

    /// <summary>
    /// Snap both hosts and the vertical title bar to the end state
    /// for <paramref name="verticalTabs"/>. Used at construction
    /// (no animation needed) and from the Storyboard Completed
    /// handler to guarantee a consistent end state regardless of
    /// mid-flight cancellation.
    /// </summary>
    public void Snap(bool verticalTabs)
    {
        var w = verticalTabs ? VerticalStripCollapsedWidth : 0;
        _stripColumn.Width = new GridLength(w);
        _titleBarStripMirror.Width = new GridLength(w);
        _verticalTabHost.SetInternalStripWidth(VerticalStripCollapsedWidth);

        _verticalHost.Opacity = verticalTabs ? 1 : 0;
        _verticalHost.Visibility = verticalTabs ? Visibility.Visible : Visibility.Collapsed;
        _verticalHost.IsHitTestVisible = verticalTabs;

        _horizontalHost.Opacity = verticalTabs ? 0 : 1;
        _horizontalHost.Visibility = verticalTabs ? Visibility.Collapsed : Visibility.Visible;
        _horizontalHost.IsHitTestVisible = !verticalTabs;

        if (_verticalTitleBarSuppressed)
        {
            _verticalTitleBar.Visibility = Visibility.Collapsed;
            _verticalTitleBar.Opacity = 0;
        }
        else
        {
            _verticalTitleBar.Visibility = verticalTabs ? Visibility.Visible : Visibility.Collapsed;
            _verticalTitleBar.Opacity = verticalTabs ? 1 : 0;
        }

        // Reset any dangling translate offsets so future switches
        // start from origin. Safe to overwrite: Snap is only called
        // when no transform animation is in flight.
        GetOrCreateTranslate(_verticalHost).X = 0;
        GetOrCreateTranslate(_verticalHost).Y = 0;
        GetOrCreateTranslate(_horizontalHost).X = 0;
        GetOrCreateTranslate(_horizontalHost).Y = 0;

        if (_stripHidden)
        {
            // Collapse everything in the strip/title row + column,
            // leaving only the pane host (row 1, col 1) visible.
            _stripColumn.Width = new GridLength(0);
            _titleBarStripMirror.Width = new GridLength(0);
            _horizontalHost.Visibility = Visibility.Collapsed;
            _horizontalHost.IsHitTestVisible = false;
            _verticalHost.Visibility = Visibility.Collapsed;
            _verticalHost.IsHitTestVisible = false;
            _verticalTitleBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Force the tab strip (and vertical title bar) hidden or restore it
    /// to the normal end-state for the current layout. Used by the quake
    /// window to hide tab chrome when only one tab is open.
    /// </summary>
    public void SetStripHidden(bool hidden, bool verticalTabs)
    {
        _stripHidden = hidden;
        Snap(verticalTabs);
    }

    public void SuppressVerticalTitleBar(bool suppress, bool verticalTabs)
    {
        _verticalTitleBarSuppressed = suppress;
        Snap(verticalTabs);
    }

    /// <summary>
    /// Cross-fade + slide animation between horizontal and vertical
    /// layouts. Runs the chrome transforms (Opacity, RenderTransform)
    /// inside a <see cref="Storyboard"/> while snapping the strip
    /// column width immediately. WinUI 3 has no native
    /// GridLengthAnimation, and custom DependencyObjects not in the
    /// visual tree are rejected by Storyboard.Begin, so the column
    /// width is set directly — the crossfade hides the snap.
    /// </summary>
    public void Animate(bool verticalTabs, Action? onCompleted = null)
    {
        if (_stripHidden)
        {
            Snap(verticalTabs);
            onCompleted?.Invoke();
            return;
        }
        if (_switching) return;
        _switching = true;

        var targetColWidth = verticalTabs ? VerticalStripCollapsedWidth : 0;

        if (!_verticalTitleBarSuppressed)
            _verticalTitleBar.Visibility = Visibility.Visible;
        _verticalHost.Visibility = Visibility.Visible;
        _horizontalHost.Visibility = Visibility.Visible;

        var incoming = verticalTabs ? _verticalHost : _horizontalHost;
        var outgoing = verticalTabs ? _horizontalHost : _verticalHost;
        var incomingOffset = verticalTabs
            ? new Windows.Foundation.Point(-VerticalStripCollapsedWidth, 0)
            : new Windows.Foundation.Point(0, -VerticalTitleBarHeight);
        var outgoingOffset = verticalTabs
            ? new Windows.Foundation.Point(0, -VerticalTitleBarHeight)
            : new Windows.Foundation.Point(-VerticalStripCollapsedWidth, 0);

        incoming.IsHitTestVisible = true;
        var incomingTx = GetOrCreateTranslate(incoming);
        incomingTx.X = incomingOffset.X;
        incomingTx.Y = incomingOffset.Y;
        incoming.Opacity = 0;

        // Cancel any in-flight column tween from a previous chevron
        // click so the layout switch owns the column width.
        _columnTimer?.Stop();
        _columnTimer = null;

        var sb = new Storyboard();
        sb.Children.Add(MakeDoubleAnim(incoming, "Opacity", 0, 1));
        sb.Children.Add(MakeDoubleAnim(outgoing, "Opacity", outgoing.Opacity, 0));
        if (!_verticalTitleBarSuppressed)
            sb.Children.Add(MakeDoubleAnim(_verticalTitleBar, "Opacity",
                verticalTabs ? 0 : 1, verticalTabs ? 1 : 0));

        sb.Children.Add(MakeTransformAnim(incoming, "X", incomingTx.X, 0));
        sb.Children.Add(MakeTransformAnim(incoming, "Y", incomingTx.Y, 0));
        var outgoingTx = GetOrCreateTranslate(outgoing);
        sb.Children.Add(MakeTransformAnim(outgoing, "X", outgoingTx.X, outgoingOffset.X));
        sb.Children.Add(MakeTransformAnim(outgoing, "Y", outgoingTx.Y, outgoingOffset.Y));

        // Spin + pop the wintty icon while keeping it pinned in place.
        // Each icon counter-translates its own host's slide, so it stays
        // glued to the same anchor point while the strip/title-bar chrome
        // slides around it -- the icon reads as a fixed pivot the layout
        // rotates about, not something that flies in with the rest.
        //
        // Both icons spin the same way so the cross-fade between them
        // (one per layout, sitting at the same spot) looks like a single
        // steady, turning ghost. Direction follows the chrome sweep:
        // going vertical (top bar -> left rail) is counter-clockwise,
        // going horizontal (left rail -> top bar) clockwise. One full
        // turn lands it upright.
        var spin = verticalTabs ? -360.0 : 360.0;
        var incomingIcon = verticalTabs ? _verticalIcon : _horizontalIcon;
        var outgoingIcon = verticalTabs ? _horizontalIcon : _verticalIcon;
        SpinIconInPlace(sb, incomingIcon, spin,
            -incomingOffset.X, -incomingOffset.Y, 0, 0);
        SpinIconInPlace(sb, outgoingIcon, spin,
            0, 0, -outgoingOffset.X, -outgoingOffset.Y);

        // Snap the strip column width immediately. The crossfade
        // hides the jump; see the Animate summary for rationale.
        _stripColumn.Width = new GridLength(targetColWidth);
        _titleBarStripMirror.Width = new GridLength(targetColWidth);
        _verticalTabHost.SetInternalStripWidth(VerticalStripCollapsedWidth);

        sb.Completed += (_, _) =>
        {
            // 360 lands upright, but reset to 0 so the next spin starts
            // from a clean angle rather than accumulating.
            ResetIconTransform(incomingIcon);
            ResetIconTransform(outgoingIcon);
            Snap(verticalTabs);
            _switching = false;
            onCompleted?.Invoke();
        };
        sb.Begin();
    }

    /// <summary>
    /// Tween <see cref="ColumnDefinition.Width"/> from its current
    /// value to <paramref name="to"/>. Used by the chevron expand
    /// path inside <see cref="VerticalTabHost"/>; the runtime layout
    /// switch above bundles its column tween into the cross-fade
    /// Storyboard instead.
    ///
    /// Cancels any in-flight column tween so the chevron toggle and
    /// the layout switch cannot race on the same column.
    /// </summary>
    public void TweenStripColumn(double from, double to, Action<double>? onTick = null)
    {
        _columnTimer?.Stop();
        var sw = Stopwatch.StartNew();
        var duration = TimeSpan.FromMilliseconds(SwitchDurationMs);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(sw.Elapsed / duration, 1.0);
            // Quadratic ease-out: 1 - (1 - t)^2
            var eased = 1.0 - (1.0 - t) * (1.0 - t);
            var value = from + (to - from) * eased;
            _stripColumn.Width = new GridLength(value);
            _titleBarStripMirror.Width = new GridLength(value);
            onTick?.Invoke(value);
            if (t >= 1.0)
            {
                timer.Stop();
                if (ReferenceEquals(_columnTimer, timer))
                    _columnTimer = null;
            }
        };
        _columnTimer = timer;
        timer.Start();
    }

    private static TranslateTransform GetOrCreateTranslate(FrameworkElement fe)
    {
        if (fe.RenderTransform is TranslateTransform t) return t;
        var nt = new TranslateTransform();
        fe.RenderTransform = nt;
        return nt;
    }

    // Spin + pop one icon while holding it at a fixed anchor. The
    // translate runs from/to the negative of the host's slide so it
    // exactly cancels it (same easing + duration as the host slide),
    // leaving only the in-place rotate and scale visible.
    private static void SpinIconInPlace(
        Storyboard sb, FrameworkElement? icon, double spin,
        double fromX, double fromY, double toX, double toY)
    {
        if (icon is null) return;
        var (scale, rotate, translate) = EnsureIconTransform(icon);
        rotate.Angle = 0;
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = fromX;
        translate.Y = fromY;
        sb.Children.Add(MakeRotateAnim(rotate, 0, spin));
        sb.Children.Add(MakePopAnim(scale, "ScaleX"));
        sb.Children.Add(MakePopAnim(scale, "ScaleY"));
        sb.Children.Add(MakeIconTranslateAnim(translate, "X", fromX, toX));
        sb.Children.Add(MakeIconTranslateAnim(translate, "Y", fromY, toY));
    }

    // Give the icon a Scale+Rotate+Translate group: scale and rotate
    // pivot on its centre (RenderTransformOrigin), the translate (applied
    // last, so it stays axis-aligned) cancels the host slide.
    private static (ScaleTransform Scale, RotateTransform Rotate, TranslateTransform Translate) EnsureIconTransform(FrameworkElement fe)
    {
        if (fe.RenderTransform is TransformGroup g
            && g.Children.Count == 3
            && g.Children[0] is ScaleTransform existingScale
            && g.Children[1] is RotateTransform existingRotate
            && g.Children[2] is TranslateTransform existingTranslate)
            return (existingScale, existingRotate, existingTranslate);

        var scale = new ScaleTransform();
        var rotate = new RotateTransform();
        var translate = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(rotate);
        group.Children.Add(translate);
        fe.RenderTransform = group;
        fe.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        return (scale, rotate, translate);
    }

    private static void ResetIconTransform(FrameworkElement? fe)
    {
        if (fe is null) return;
        var (scale, rotate, translate) = EnsureIconTransform(fe);
        rotate.Angle = 0;
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;
    }

    private static DoubleAnimation MakeIconTranslateAnim(TranslateTransform target, string axis, double from, double to)
    {
        // Match the host slide's easing + duration so the cancellation
        // holds at every frame, not just the endpoints.
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(SwitchDurationMs)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, axis);
        return anim;
    }

    private static DoubleAnimation MakeRotateAnim(RotateTransform target, double from, double to)
    {
        // A touch of back-ease overshoot at the end sells the "shoved
        // and settling" feel rather than a mechanical stop.
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(SwitchDurationMs)),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 },
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Angle");
        return anim;
    }

    // Dip the icon's scale mid-spin then spring back past 1.0, so it
    // pops as it lands.
    private static DoubleAnimationUsingKeyFrames MakePopAnim(ScaleTransform target, string axis)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 1.0,
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(SwitchDurationMs * 0.45)),
            Value = 0.78,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(SwitchDurationMs)),
            Value = 1.0,
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 },
        });
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, axis);
        return anim;
    }

    private static DoubleAnimation MakeDoubleAnim(DependencyObject target, string path, double from, double to)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(SwitchDurationMs)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, path);
        return anim;
    }

    private static DoubleAnimation MakeTransformAnim(FrameworkElement target, string axis, double from, double to)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(SwitchDurationMs)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, target.RenderTransform);
        Storyboard.SetTargetProperty(anim, axis);
        return anim;
    }
}
