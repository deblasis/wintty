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
    // NavigationView LeftCompact default; keep in sync with
    // VerticalTabStrip NavigationView.CompactPaneLength.
    public const double VerticalStripCollapsedWidth = 48;
    public const int SwitchDurationMs = 340;

    // Vertical-mode title bar height. The horizontal host slides this
    // far vertically during the cross-fade so the swap feels like the
    // strip lifting away. Must match VerticalTitleBar.Height in
    // MainWindow.xaml.
    public const double VerticalTitleBarHeight = TabChromeMetrics.TitleRowHeight;

    private static readonly TimeSpan SwitchDuration =
        TimeSpan.FromMilliseconds(SwitchDurationMs);

    // Stagger: outgoing fades first, incoming follows so the morph reads
    // as one continuous motion instead of a flat dissolve.
    private const double IncomingFadeDelay = 0.16;
    private const double OutgoingFadeEnd = 0.78;
    private const double TitleBarSlideDistance = 10;

    // Feel constants for the icon spin/pop, tuned by eye. The spin is one
    // full turn; the pop dips the scale partway through and springs back
    // past 1.0 as it lands.
    private const double IconSpinOvershoot = 0.25; // BackEase amplitude on the rotation settle
    private const double IconPopMidpoint = 0.45;   // fraction of the switch where the scale dip bottoms out
    private const double IconPopDipScale = 0.78;   // smallest scale at the dip
    private const double IconPopOvershoot = 0.6;   // BackEase amplitude springing the scale back past 1.0

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

        // Pin toggle snaps immediately -- NavView needs the full column width
        // before IsPaneOpen sticks; a tween left MUXC auto-closing the pane.
        _verticalTabHost.StripWidthChangeRequested += (_, width) =>
            SnapStripColumn(width);
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
        var w = verticalTabs ? _verticalTabHost.CurrentStripTarget : 0;
        _stripColumn.Width = new GridLength(w);
        _titleBarStripMirror.Width = new GridLength(w);
        _verticalTabHost.SetInternalStripWidth(
            verticalTabs ? _verticalTabHost.CurrentStripTarget : VerticalStripCollapsedWidth);

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

        // Reset any dangling transform offsets so future switches start
        // from origin. Snap is the single end-state authority, so it
        // also returns the spun icons to identity -- without this, a
        // switch interrupted by SetStripHidden / SuppressVerticalTitleBar
        // (which call Snap directly) could leave an icon rotated or
        // scaled once the strip is shown again.
        GetOrCreateTranslate(_verticalHost).X = 0;
        GetOrCreateTranslate(_verticalHost).Y = 0;
        GetOrCreateTranslate(_horizontalHost).X = 0;
        GetOrCreateTranslate(_horizontalHost).Y = 0;
        ResetIconTransform(_horizontalIcon);
        ResetIconTransform(_verticalIcon);

        if (!_verticalTitleBarSuppressed)
        {
            GetOrCreateTranslate(_verticalTitleBar).X = 0;
            GetOrCreateTranslate(_verticalTitleBar).Y = 0;
        }

        if (verticalTabs)
            _verticalTabHost.ConfigureTitleBarIconMode(_verticalTitleBarSuppressed);

        if (verticalTabs)
            _verticalTabHost.SyncSelectionFromManager();

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
    /// layouts. Chrome transforms run in a compositor <see cref="Storyboard"/>;
    /// strip column width tweens in parallel because WinUI 3 has no native
    /// GridLengthAnimation.
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

        var targetColWidth = verticalTabs ? _verticalTabHost.CurrentStripTarget : 0;
        var fromColWidth = _stripColumn.Width.Value;

        if (verticalTabs)
            _verticalTabHost.ConfigureTitleBarIconMode(_verticalTitleBarSuppressed);

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
        outgoing.IsHitTestVisible = false;
        var incomingTx = GetOrCreateTranslate(incoming);
        incomingTx.X = incomingOffset.X;
        incomingTx.Y = incomingOffset.Y;
        incoming.Opacity = 0;

        _columnTimer?.Stop();
        _columnTimer = null;

        var sb = new Storyboard();
        sb.Children.Add(MakeStaggeredFadeIn(incoming));
        sb.Children.Add(MakeStaggeredFadeOut(outgoing, outgoing.Opacity));

        if (!_verticalTitleBarSuppressed)
            AddTitleBarAnimations(sb, verticalTabs);

        sb.Children.Add(MakeIncomingSlideAnim(incoming, "X", incomingTx.X, 0));
        sb.Children.Add(MakeIncomingSlideAnim(incoming, "Y", incomingTx.Y, 0));
        var outgoingTx = GetOrCreateTranslate(outgoing);
        sb.Children.Add(MakeOutgoingSlideAnim(outgoing, "X", outgoingTx.X, outgoingOffset.X));
        sb.Children.Add(MakeOutgoingSlideAnim(outgoing, "Y", outgoingTx.Y, outgoingOffset.Y));

        var spin = verticalTabs ? -360.0 : 360.0;
        var incomingIcon = verticalTabs ? _verticalIcon : _horizontalIcon;
        var outgoingIcon = verticalTabs ? _horizontalIcon : _verticalIcon;
        SpinIconInPlace(sb, incomingIcon, spin,
            -incomingOffset.X, -incomingOffset.Y, 0, 0, incoming: true);
        SpinIconInPlace(sb, outgoingIcon, spin,
            0, 0, -outgoingOffset.X, -outgoingOffset.Y, incoming: false);

        TweenStripColumn(fromColWidth, targetColWidth, onTick: w =>
        {
            if (w > 0.5)
                _verticalTabHost.SetInternalStripWidth(w);
        });

        sb.Completed += (_, _) => FinishSwitch(verticalTabs, onCompleted);

        // If Begin throws, Completed never fires and _switching stays
        // latched -- every later layout toggle (keybind, palette, settings)
        // would silently no-op for the life of the window. Land the switch
        // without the animation instead.
        try
        {
            sb.Begin();
        }
        catch (Exception)
        {
            FinishSwitch(verticalTabs, onCompleted);
        }
    }

    private void FinishSwitch(bool verticalTabs, Action? onCompleted)
    {
        if (!_switching) return;
        _columnTimer?.Stop();
        _columnTimer = null;
        Snap(verticalTabs);
        _switching = false;
        onCompleted?.Invoke();
    }

    /// <summary>
    /// Snap the vertical strip column to <paramref name="width"/> with no
    /// animation. Used by the pane-toggle button so NavigationView and the
    /// outer shell stay in lockstep.
    /// </summary>
    public void SnapStripColumn(double width)
    {
        _columnTimer?.Stop();
        _columnTimer = null;
        _stripColumn.Width = new GridLength(width);
        _titleBarStripMirror.Width = new GridLength(width);
        _verticalTabHost.SetInternalStripWidth(width);
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
            var eased = EaseInOutCubic(t);
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
        double fromX, double fromY, double toX, double toY,
        bool incoming)
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
        sb.Children.Add(MakeIconCounterSlideAnim(translate, "X", fromX, toX, incoming));
        sb.Children.Add(MakeIconCounterSlideAnim(translate, "Y", fromY, toY, incoming));
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

    private void AddTitleBarAnimations(Storyboard sb, bool verticalTabs)
    {
        var titleTx = GetOrCreateTranslate(_verticalTitleBar);
        if (verticalTabs)
        {
            _verticalTitleBar.Opacity = 0;
            titleTx.Y = -TitleBarSlideDistance;
            sb.Children.Add(MakeStaggeredFadeIn(_verticalTitleBar));
            sb.Children.Add(MakeIncomingSlideAnim(_verticalTitleBar, "Y", titleTx.Y, 0));
        }
        else
        {
            sb.Children.Add(MakeStaggeredFadeOut(_verticalTitleBar, _verticalTitleBar.Opacity));
            sb.Children.Add(MakeOutgoingSlideAnim(
                _verticalTitleBar, "Y", titleTx.Y, -TitleBarSlideDistance));
        }
    }

    private static double EaseInOutCubic(double t)
        => t < 0.5
            ? 4.0 * t * t * t
            : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0;

    private static DoubleAnimationUsingKeyFrames MakeStaggeredFadeIn(FrameworkElement target)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 0,
        });
        anim.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(
                TimeSpan.FromMilliseconds(SwitchDurationMs * IncomingFadeDelay)),
            Value = 0,
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(SwitchDuration),
            Value = 1,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        return anim;
    }

    private static DoubleAnimationUsingKeyFrames MakeStaggeredFadeOut(
        FrameworkElement target, double fromOpacity)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = fromOpacity,
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(
                TimeSpan.FromMilliseconds(SwitchDurationMs * OutgoingFadeEnd)),
            Value = 0,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        });
        anim.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(SwitchDuration),
            Value = 0,
        });
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
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
            Duration = new Duration(SwitchDuration),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = IconSpinOvershoot },
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
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(SwitchDurationMs * IconPopMidpoint)),
            Value = IconPopDipScale,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(SwitchDuration),
            Value = 1.0,
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = IconPopOvershoot },
        });
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, axis);
        return anim;
    }

    private static DoubleAnimation MakeIconCounterSlideAnim(
        TranslateTransform target, string axis, double from, double to, bool incoming)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(SwitchDuration),
            EasingFunction = incoming
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.12 }
                : new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, axis);
        return anim;
    }

    private static DoubleAnimation MakeIncomingSlideAnim(
        FrameworkElement target, string axis, double from, double to)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(SwitchDuration),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.12 },
        };
        Storyboard.SetTarget(anim, target.RenderTransform);
        Storyboard.SetTargetProperty(anim, axis);
        return anim;
    }

    private static DoubleAnimation MakeOutgoingSlideAnim(
        FrameworkElement target, string axis, double from, double to)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(SwitchDuration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(anim, target.RenderTransform);
        Storyboard.SetTargetProperty(anim, axis);
        return anim;
    }
}
