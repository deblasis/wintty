using Ghostty.Core.Tabs;
using Ghostty.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Shell;

/// <summary>
/// Stand-in for the active tab while the layout switches.
///
/// The two hosts render the active tab as different controls at different
/// sizes: a TabViewItem roughly 240x32 in the header, a NavigationViewItem
/// on a rail that is 220 wide pinned but only 48 collapsed. Cross-fading
/// the hosts therefore shows the active tab twice, at two sizes, drifting
/// apart. This element is the only active-tab visual on screen during the
/// switch: both real ones are hidden, and it travels from the outgoing
/// rect to the incoming one.
///
/// The ghost paints the active row's real fill, preset color or not. Both
/// real elements are hidden for the flight, so a content-only ghost would
/// drop the tab's chrome for the length of the switch and hand it back on
/// arrival -- the tab would appear to lose its selection while in the air.
/// </summary>
internal sealed partial class TabMorphGhost : Grid
{
    private readonly TextBlock _label;

    /// <summary>Label opacity, animated separately from the ghost body.</summary>
    internal UIElement Label => _label;

    /// <summary>
    /// The corner geometry is the DESTINATION's, not a blend: the eye
    /// follows the ghost to its landing, so it should arrive already in the
    /// shape it hands over to, and the mismatch at departure is masked by
    /// the switch starting around it.
    /// </summary>
    internal TabMorphGhost(
        TabModel tab, Brush? fill, Brush? foreground, CornerRadius cornerRadius)
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        RenderTransform = new TranslateTransform();
        Background = fill;
        CornerRadius = cornerRadius;

        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = TabIconElementFactory.Create(tab.TabIcon);
        if (icon is not null)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(6, 0, 6, 0);
            SetColumn(icon, 0);
            Children.Add(icon);
        }

        _label = new TextBlock
        {
            Text = tab.EffectiveTitle,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.Clip,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 0, 6, 0),
        };
        if (foreground is not null)
            _label.Foreground = foreground;
        SetColumn(_label, 1);
        Children.Add(_label);

        // Clip to the animated bounds so the label is cut off by the
        // shrinking rect rather than spilling over the rail while it fades.
        var clip = new RectangleGeometry();
        Clip = clip;
        SizeChanged += (_, e) =>
            clip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
    }

    internal TranslateTransform Translate => (TranslateTransform)RenderTransform;
}
