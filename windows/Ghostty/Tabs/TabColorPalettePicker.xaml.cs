using System;
using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Ghostty.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Ghostty.Tabs;

/// <summary>
/// Small 2-row, 5-column swatch grid used to pick a preset
/// <see cref="TabColor"/>. Hosted inside a secondary
/// <see cref="Flyout"/> anchored to the right-clicked tab.
///
/// This is NOT a <c>MenuFlyoutItem</c>-with-templated-content hack.
/// WinAppSDK 1.6 had hit-testing quirks when hosting arbitrary UI inside
/// a menu item (not re-checked since; we are on 2.2 now), and hosting in
/// a separate Flyout sidesteps them at the cost of one extra click on
/// color change.
///
/// Swatches are items of a single-selection <see cref="GridView"/> so
/// UI Automation sees focusable, selectable elements: the applied color
/// is reported through SelectionItem rather than implied by a drawn
/// ring, and Invoke activates one.
/// </summary>
internal sealed partial class TabColorPalettePicker : UserControl
{
    /// <summary>Theme brush for the <see cref="TabColor.None"/> outline.</summary>
    private const string NoneStrokeKey = "TextFillColorSecondaryBrush";

    /// <summary>
    /// Raised when the user picks a swatch. The parent flyout is
    /// responsible for closing itself (the picker does not know about
    /// its host). Never raised more than once per picker instance.
    /// </summary>
    public event EventHandler<TabColor>? ColorSelected;

    /// <summary>
    /// Swatch element to color, mirroring <c>TabOverviewControl</c>'s
    /// tile map. The elements carry no payload of their own, and the
    /// color cannot be recovered from the geometry on the way back out.
    /// </summary>
    private readonly Dictionary<FrameworkElement, TabColor> _colors = new();

    /// <summary>
    /// The two shapes making up the <see cref="TabColor.None"/> swatch.
    /// Their stroke cannot be resolved until the control has a theme.
    /// </summary>
    private readonly List<Shape> _noneStrokes = new();

    /// <summary>
    /// <c>TabContextMenuBuilder.ShowColorPicker</c> builds one picker per
    /// invocation, hides the flyout on the first notification and never
    /// reuses the picker, so the event is single-shot by construction.
    /// This keeps that true even if a second click lands before the
    /// flyout finishes hiding.
    /// </summary>
    private bool _picked;

    /// <summary>
    /// Loaded fires whenever the control re-enters the tree, and focusing
    /// again would throw away wherever the user had arrowed to.
    /// </summary>
    private bool _openedFocus;

    public TabColorPalettePicker(TabColor initial)
    {
        InitializeComponent();

        // The grid is the palette's automation element, so it takes its
        // name from the visible heading rather than repeating the string.
        // Set rather than LabeledBy: the heading is out of the automation
        // tree (see the XAML), and LabeledBy would point into nothing.
        AutomationProperties.SetName(Swatches, PaletteLabel.Text);

        BuildSwatches(initial);

        Loaded += (_, _) =>
        {
            ApplyNoneStroke();
            if (_openedFocus) return;
            _openedFocus = true;

            // Open on the applied color. ListViewBase is not itself a tab
            // stop, so Focus() hands off to the selected container.
            Swatches.Focus(FocusState.Programmatic);
        };
    }

    private void BuildSwatches(TabColor initial)
    {
        // TabColorPalette.PaletteRows is the macOS-derived layout, kept in
        // Ghostty.Core so platform divergence stays in one file. Flattening
        // it here and letting the panel wrap keeps that ordering authoritative.
        foreach (var row in TabColorPalette.PaletteRows)
        {
            foreach (var color in row)
            {
                var swatch = BuildSwatch(color);
                _colors[swatch] = color;
                Swatches.Items.Add(swatch);
                if (color == initial)
                    Swatches.SelectedItem = swatch;
            }
        }

        // Only reachable if TabColor gains a member PaletteRows does not
        // list. Opening on None misreports the tab by one swatch; opening
        // with nothing selected and nothing focused strands the keyboard.
        Swatches.SelectedItem ??= Swatches.Items[0];
    }

    private FrameworkElement BuildSwatch(TabColor color)
    {
        // Plain content, NOT a GridViewItem: an item that arrives as its own
        // container suppresses ItemClick entirely, and every activation route
        // here (click, Space, Enter, UIA Invoke) is that one event. The
        // container, its size and its ring come from TabColorSwatchStyle.
        //
        // The 20 DIP circle sits on a transparent tile the size of the
        // container so the tooltip and the click target are the same region:
        // an Ellipse hit-tests to its geometry, which would leave the corners
        // and the ring gap clickable but silent on hover.
        // No explicit size: the container style stretches its content, so the
        // tile takes the container's bounds without restating them here.
        var tile = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        var ellipse = new Ellipse
        {
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (color == TabColor.None)
        {
            // Hollow circle with a diagonal slash, matching the macOS
            // .circle.slash symbol. Implemented as an Ellipse with
            // Stroke plus a Line inside a Grid.
            ellipse.Fill = new SolidColorBrush(Colors.Transparent);
            ellipse.StrokeThickness = 1;

            var slash = new Line
            {
                X1 = 3, Y1 = 17, X2 = 17, Y2 = 3,
                StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 20,
                Height = 20,
            };

            _noneStrokes.Add(ellipse);
            _noneStrokes.Add(slash);
            tile.Children.Add(ellipse);
            tile.Children.Add(slash);
        }
        else
        {
            var drawing = TabColorPalette.Colors[color];
            ellipse.Fill = new SolidColorBrush(
                Windows.UI.Color.FromArgb(255, drawing.R, drawing.G, drawing.B));
            tile.Children.Add(ellipse);
        }

        var label = TabColorPalette.LocalizedName(color);
        ToolTipService.SetToolTip(tile, label);
        AutomationProperties.SetName(tile, label);
        return tile;
    }

    /// <summary>
    /// Paint the None outline from the theme this control actually renders
    /// under. Application.Current.Resources picks its theme dictionary by
    /// Application.RequestedTheme, and the tab hosts pin their subtree to
    /// Dark, so the app-theme lookup hands a light-theme stroke to a dark
    /// flyout whenever the OS theme is light (issue # 325).
    /// </summary>
    private void ApplyNoneStroke()
    {
        var stroke = ThemedResources.TryFindBrush(
            Application.Current.Resources, NoneStrokeKey, ActualTheme, out var themed)
            ? themed
            : Ghostty.Controls.ThemeResources.Get<Brush>(
                NoneStrokeKey, new SolidColorBrush(Colors.Gray));

        foreach (var shape in _noneStrokes)
            shape.Stroke = stroke;
    }

    private void OnSwatchClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FrameworkElement swatch)
            Pick(swatch);
    }

    private void Pick(FrameworkElement swatch)
    {
        if (_picked) return;
        if (!_colors.TryGetValue(swatch, out var color)) return;
        _picked = true;
        ColorSelected?.Invoke(this, color);
    }
}
