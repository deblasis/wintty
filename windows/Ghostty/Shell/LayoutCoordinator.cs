using System;
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
    public const double VerticalStripCollapsedWidth = 40;
    public const int SwitchDurationMs = 220;

    private readonly ColumnDefinition _stripColumn;
    private readonly FrameworkElement _horizontalHost;
    private readonly VerticalTabHost _verticalTabHost;
    private readonly FrameworkElement _verticalHost;
    private readonly Grid _verticalTitleBar;

    private bool _switching;
    private Storyboard? _activeColumnSb;

    public LayoutCoordinator(
        ColumnDefinition stripColumn,
        FrameworkElement horizontalHost,
        VerticalTabHost verticalTabHost,
        Grid verticalTitleBar)
    {
        _stripColumn = stripColumn;
        _horizontalHost = horizontalHost;
        _verticalTabHost = verticalTabHost;
        _verticalHost = verticalTabHost;
        _verticalTitleBar = verticalTitleBar;

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
        _stripColumn.Width = new GridLength(verticalTabs ? VerticalStripCollapsedWidth : 0);
        _verticalTabHost.SetInternalStripWidth(VerticalStripCollapsedWidth);

        _verticalHost.Opacity = verticalTabs ? 1 : 0;
        _verticalHost.Visibility = verticalTabs ? Visibility.Visible : Visibility.Collapsed;
        _verticalHost.IsHitTestVisible = verticalTabs;

        _horizontalHost.Opacity = verticalTabs ? 0 : 1;
        _horizontalHost.Visibility = verticalTabs ? Visibility.Collapsed : Visibility.Visible;
        _horizontalHost.IsHitTestVisible = !verticalTabs;

        _verticalTitleBar.Visibility = verticalTabs ? Visibility.Visible : Visibility.Collapsed;
        _verticalTitleBar.Opacity = verticalTabs ? 1 : 0;

        // Reset any dangling translate offsets so future switches
        // start from origin. Safe to overwrite: Snap is only called
        // when no transform animation is in flight.
        GetOrCreateTranslate(_verticalHost).X = 0;
        GetOrCreateTranslate(_verticalHost).Y = 0;
        GetOrCreateTranslate(_horizontalHost).X = 0;
        GetOrCreateTranslate(_horizontalHost).Y = 0;
    }

    /// <summary>
    /// Cross-fade + slide animation between horizontal and vertical
    /// layouts. Runs the chrome transforms (Opacity, RenderTransform)
    /// AND the strip column width inside a single
    /// <see cref="Storyboard"/> so all the animations share one
    /// timeline and one Completed handler.
    ///
    /// The column width is animated through
    /// <see cref="GridLengthAnimator"/>, a small DependencyObject
    /// proxy that converts <c>double</c> ticks into
    /// <see cref="GridLength"/> writes — WinUI 3 has no native
    /// GridLengthAnimation.
    /// </summary>
    public void Animate(bool verticalTabs)
    {
        if (_switching) return;
        _switching = true;

        var targetColWidth = verticalTabs ? VerticalStripCollapsedWidth : 0;

        _verticalTitleBar.Visibility = Visibility.Visible;
        _verticalHost.Visibility = Visibility.Visible;
        _horizontalHost.Visibility = Visibility.Visible;

        var incoming = verticalTabs ? _verticalHost : _horizontalHost;
        var outgoing = verticalTabs ? _horizontalHost : _verticalHost;
        var incomingOffset = verticalTabs
            ? new Windows.Foundation.Point(-VerticalStripCollapsedWidth, 0)
            : new Windows.Foundation.Point(0, -32);
        var outgoingOffset = verticalTabs
            ? new Windows.Foundation.Point(0, -32)
            : new Windows.Foundation.Point(-VerticalStripCollapsedWidth, 0);

        incoming.IsHitTestVisible = true;
        var incomingTx = GetOrCreateTranslate(incoming);
        incomingTx.X = incomingOffset.X;
        incomingTx.Y = incomingOffset.Y;
        incoming.Opacity = 0;

        // Cancel any column tween from a previous chevron click so
        // the storyboard owns the column width for its full duration.
        _activeColumnSb?.Stop();
        _activeColumnSb = null;

        var sb = new Storyboard();
        sb.Children.Add(MakeDoubleAnim(incoming, "Opacity", 0, 1));
        sb.Children.Add(MakeDoubleAnim(outgoing, "Opacity", outgoing.Opacity, 0));
        sb.Children.Add(MakeDoubleAnim(_verticalTitleBar, "Opacity",
            verticalTabs ? 0 : 1, verticalTabs ? 1 : 0));

        sb.Children.Add(MakeTransformAnim(incoming, "X", incomingTx.X, 0));
        sb.Children.Add(MakeTransformAnim(incoming, "Y", incomingTx.Y, 0));
        var outgoingTx = GetOrCreateTranslate(outgoing);
        sb.Children.Add(MakeTransformAnim(outgoing, "X", outgoingTx.X, outgoingOffset.X));
        sb.Children.Add(MakeTransformAnim(outgoing, "Y", outgoingTx.Y, outgoingOffset.Y));

        // Strip column width: bridge GridLength via the proxy.
        // Snap the inner strip column once at the start — during a
        // layout switch the chevron pinned-state never changes, so
        // there is no per-frame onTick callback to drive.
        _verticalTabHost.SetInternalStripWidth(VerticalStripCollapsedWidth);
        var widthProxy = new GridLengthAnimator(_stripColumn, _stripColumn.Width.Value);
        sb.Children.Add(MakeDoubleAnim(widthProxy, "Value", _stripColumn.Width.Value, targetColWidth));

        sb.Completed += (_, _) =>
        {
            Snap(verticalTabs);
            _switching = false;
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
        _activeColumnSb?.Stop();

        var proxy = new GridLengthAnimator(_stripColumn, from) { OnTick = onTick };
        var sb = new Storyboard();
        sb.Children.Add(MakeDoubleAnim(proxy, "Value", from, to));
        sb.Completed += (s, _) =>
        {
            if (ReferenceEquals(_activeColumnSb, s))
                _activeColumnSb = null;
        };
        _activeColumnSb = sb;
        sb.Begin();
    }

    private static TranslateTransform GetOrCreateTranslate(FrameworkElement fe)
    {
        if (fe.RenderTransform is TranslateTransform t) return t;
        var nt = new TranslateTransform();
        fe.RenderTransform = nt;
        return nt;
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
