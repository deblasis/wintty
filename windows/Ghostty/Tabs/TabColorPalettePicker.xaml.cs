using System;
using Ghostty.Core.Tabs;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Ghostty.Tabs;

/// <summary>
/// Small 2-row, 5-column swatch grid used to pick a preset
/// <see cref="TabColor"/>. Hosted inside a secondary
/// <see cref="Flyout"/> anchored to the target <c>TabViewItem</c>.
///
/// This is NOT a <c>MenuFlyoutItem</c>-with-templated-content hack.
/// WinAppSDK 1.6 has known hit-testing quirks when hosting arbitrary
/// UI inside a menu item; hosting in a separate Flyout sidesteps them
/// at the cost of one extra click on color change.
/// </summary>
internal sealed partial class TabColorPalettePicker : UserControl
{
    /// <summary>
    /// Raised when the user clicks a swatch. The parent flyout is
    /// responsible for closing itself (the picker does not know about
    /// its host).
    /// </summary>
    public event EventHandler<TabColor>? ColorSelected;

    private readonly TabColor _initial;

    public TabColorPalettePicker(TabColor initial)
    {
        _initial = initial;
        InitializeComponent();
        BuildSwatches();
    }

    private void BuildSwatches()
    {
        // Render the two rows declared in TabColorPalette.PaletteRows.
        // We intentionally read the macOS-derived layout from
        // Ghostty.Core so platform divergence stays in one file.
        var rows = TabColorPalette.PaletteRows;
        AddRow(Row0, rows[0]);
        AddRow(Row1, rows[1]);
    }

    private void AddRow(StackPanel host, TabColor[] row)
    {
        foreach (var color in row)
            host.Children.Add(BuildSwatch(color));
    }

    private Border BuildSwatch(TabColor color)
    {
        // Each swatch is a 20x20 DIP Ellipse wrapped in a Border that
        // owns the selection ring (2 DIP, SystemAccentColor). Pointer
        // input goes on the Border so the whole tile is clickable, not
        // only the ellipse interior.
        var ellipse = new Ellipse { Width = 20, Height = 20 };

        if (color == TabColor.None)
        {
            // Hollow circle with a diagonal slash, matching the macOS
            // .circle.slash symbol. Implemented as an Ellipse with
            // Stroke plus a Line inside a Grid.
            var secondaryBrush = GetBrushResource("TextFillColorSecondaryBrush");
            ellipse.Fill = new SolidColorBrush(Colors.Transparent);
            ellipse.Stroke = secondaryBrush;
            ellipse.StrokeThickness = 1;

            var slash = new Line
            {
                X1 = 3, Y1 = 17, X2 = 17, Y2 = 3,
                StrokeThickness = 1.5,
                Stroke = secondaryBrush,
            };

            var grid = new Grid { Width = 20, Height = 20 };
            grid.Children.Add(ellipse);
            grid.Children.Add(slash);

            return WrapSwatch(grid, color);
        }
        else
        {
            var drawing = TabColorPalette.Colors[color];
            ellipse.Fill = new SolidColorBrush(
                Windows.UI.Color.FromArgb(255, drawing.R, drawing.G, drawing.B));
            return WrapSwatch(ellipse, color);
        }
    }

    private Border WrapSwatch(FrameworkElement content, TabColor color)
    {
        // The border paints the selection ring when this swatch matches
        // the currently-applied TabColor. 2 DIP ring in SystemAccentColor,
        // inset so the visual ring sits outside the 20x20 circle.
        bool selected = color == _initial;

        var border = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(selected ? 2 : 0),
            BorderBrush = selected
                ? GetBrushResource("SystemControlHighlightAccentBrush")
                : new SolidColorBrush(Colors.Transparent),
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(2),
            Child = content,
        };

        ToolTipService.SetToolTip(border, TabColorPalette.LocalizedName(color));
        border.Tapped += (_, e) =>
        {
            e.Handled = true;
            ColorSelected?.Invoke(this, color);
        };
        return border;
    }

    /// <summary>
    /// Look up a theme brush resource with a fallback. WinUI 3 theme
    /// resources are not always <see cref="SolidColorBrush"/>; a raw
    /// cast would throw on unexpected types or missing keys.
    /// </summary>
    private static SolidColorBrush GetBrushResource(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
            return brush;
        return new SolidColorBrush(Colors.Gray);
    }
}
