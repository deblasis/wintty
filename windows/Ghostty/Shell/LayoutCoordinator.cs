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
    // Wider than the 40px icon cell so the new-tab split button at
    // the bottom of the vertical strip can render its dropdown
    // chevron. Must stay in sync with the StripColumn Width in
    // VerticalTabHost.xaml.
    public const double VerticalStripCollapsedWidth = 56;
    public const int SwitchDurationMs = 220;

    private readonly ColumnDefinition _stripColumn;
    private readonly ColumnDefinition _titleBarStripMirror;
    private readonly FrameworkElement _horizontalHost;
    private readonly VerticalTabHost _verticalTabHost;
    private readonly FrameworkElement _verticalHost;
    private readonly Grid _verticalTitleBar;

    private bool _switching;
    private DispatcherTimer? _columnTimer;

    public LayoutCoordinator(
        ColumnDefinition stripColumn,
        ColumnDefinition titleBarStripMirror,
        FrameworkElement horizontalHost,
        VerticalTabHost verticalTabHost,
        Grid verticalTitleBar)
    {
        _stripColumn = stripColumn;
        _titleBarStripMirror = titleBarStripMirror;
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
    /// inside a <see cref="Storyboard"/> while snapping the strip
    /// column width immediately. WinUI 3 has no native
    /// GridLengthAnimation, and custom DependencyObjects not in the
    /// visual tree are rejected by Storyboard.Begin, so the column
    /// width is set directly — the crossfade hides the snap.
    /// </summary>
    public void Animate(bool verticalTabs, Action? onCompleted = null)
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

        // Cancel any in-flight column tween from a previous chevron
        // click so the layout switch owns the column width.
        _columnTimer?.Stop();
        _columnTimer = null;

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

        // Snap the strip column width immediately. The crossfade
        // hides the jump; see the Animate summary for rationale.
        _stripColumn.Width = new GridLength(targetColWidth);
        _titleBarStripMirror.Width = new GridLength(targetColWidth);
        _verticalTabHost.SetInternalStripWidth(VerticalStripCollapsedWidth);

        sb.Completed += (_, _) =>
        {
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
