using System;
using Ghostty.Controls;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Ghostty.Tabs;

/// <summary>
/// The horizontal strip's group run label: color dot, group title,
/// member count, floating above an expanded run. Code-built and hosted on
/// the morph layer's canvas by MainWindow -- the TabSwitcherPopup fit, not
/// the ToolTip fit: a ToolTip attaches to one element and cannot span a
/// run, and Flyout/TeachingTip take focus. This element is
/// IsHitTestVisible=false, so it cannot take focus, never joins the focus
/// chain, and clicks pass through to the strip beneath; it carries no
/// AutomationProperties on purpose -- screen readers get the group title
/// from the member items, never from hover. It hides by rule (the strip
/// and the window call Hide), never by light-dismiss plumbing.
/// </summary>
internal sealed partial class TabRunLabel : Grid
{
    private readonly Border _dot;
    private readonly TextBlock _title;
    private readonly TextBlock _count;
    private readonly Border _card;
    private readonly Storyboard _fade = new();
    private bool _shown;

    /// <summary>
    /// The motion gate, supplied by the window: TabStripMotion.Enabled
    /// over the OS animation read and the composed High Contrast state.
    /// Null means motion on -- the label is chrome, not state, and a gate
    /// that cannot be read must not block it.
    /// </summary>
    internal Func<bool>? MotionEnabled { get; set; }

    public TabRunLabel()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        Height = TabRunLabelShape.HeightPx;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;

        _dot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0),
        };
        _title = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _count = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 8, 0),
            Opacity = 0.7,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dot);
        row.Children.Add(_title);
        row.Children.Add(_count);

        var card = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0),
            Child = row,
        };
        _card = card;
        Children.Add(card);

        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.Zero),
        };
        Storyboard.SetTarget(animation, this);
        Storyboard.SetTargetProperty(animation, "Opacity");
        _fade.Children.Add(animation);
        _fade.Completed += (_, _) =>
        {
            if (!_shown) Visibility = Visibility.Collapsed;
        };
        Opacity = 0;
    }

    /// <summary>
    /// Show the label for <paramref name="group"/> above the run whose
    /// first and last member elements are given. The run's rect is read
    /// in this element's own space -- the morph canvas, the one
    /// coordinate space both strips are already measured in -- so the
    /// label and the run cannot disagree about where anything is. A run
    /// with no arranged bounds is refused rather than placed from a
    /// stale offset, the same rule the seam cover keeps.
    /// </summary>
    internal void ShowFor(TabGroup group, int members,
        FrameworkElement runHead, FrameworkElement runTail)
    {
        if (runHead.ActualWidth <= 0 || runTail.ActualWidth <= 0) return;

        var head = runHead.TransformToVisual(this).TransformBounds(
            new Rect(0, 0, runHead.ActualWidth, runHead.ActualHeight));
        var tail = runTail.TransformToVisual(this).TransformBounds(
            new Rect(0, 0, runTail.ActualWidth, runTail.ActualHeight));
        var (left, top, width) = TabRunLabelShape.Place(
            Math.Min(head.Left, tail.Left),
            Math.Min(head.Top, tail.Top),
            Math.Max(head.Right, tail.Right) - Math.Min(head.Left, tail.Left));
        if (width <= 0) return;

        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        Width = width;

        // The dot paints unconditionally: a group has no "no color" state,
        // the same rule the chip's swatch keeps. The card's ink re-resolves
        // per show rather than per construction, so a theme switch between
        // openings is honored -- the TabSwitcherPopup's rule.
        _dot.Background = TabColorBrush.From(
            TabColorPalette.Background(group.Color, selected: false));
        _card.Background = ThemeResources.Get<Brush>(
            "CardBackgroundFillColorDefaultBrush",
            new SolidColorBrush(Windows.UI.Color.FromArgb(0xF2, 0x2B, 0x2B, 0x2B)));
        _card.BorderBrush = ThemeResources.Get<Brush>(
            "SurfaceStrokeColorDefaultBrush",
            new SolidColorBrush(Windows.UI.Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)));
        _card.BorderThickness = new Thickness(1);
        var ink = ThemeResources.Get<Brush>(
            "TextFillColorPrimaryBrush",
            new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)));
        _title.Foreground = ink;
        _count.Foreground = ink;
        _title.Text = group.Title;
        _title.MaxWidth = TabRunLabelShape.TitleMaxWidthPx;
        _count.Text = members.ToString();

        _shown = true;
        Visibility = Visibility.Visible;
        RunFade(TabRunLabelShape.FadeDuration(MotionEnabled?.Invoke() ?? true), toVisible: true);
    }

    /// <summary>
    /// Hide by rule. A drag-start hide is a cut regardless of the motion
    /// gate: the label must be gone in the same pass that lifts the drag
    /// ghost, and an 83ms overlap is exactly the overlap the rule exists
    /// to forbid.
    /// </summary>
    internal void Hide(bool cut)
    {
        if (!_shown) return;
        _shown = false;
        RunFade(cut ? TimeSpan.Zero
            : TabRunLabelShape.FadeDuration(MotionEnabled?.Invoke() ?? true), toVisible: false);
    }

    private void RunFade(TimeSpan duration, bool toVisible)
    {
        _fade.Stop();
        var animation = (DoubleAnimation)_fade.Children[0];
        animation.From = toVisible ? 0 : 1;
        animation.To = toVisible ? 1 : 0;
        animation.Duration = new Duration(duration);
        if (duration == TimeSpan.Zero)
        {
            // A cut: land the end state in the same pass. The storyboard
            // would take a dispatcher tick to do it, and the cut exists
            // because that tick is too late.
            Opacity = toVisible ? 1 : 0;
            if (!toVisible) Visibility = Visibility.Collapsed;
            return;
        }
        _fade.Begin();
    }
}
