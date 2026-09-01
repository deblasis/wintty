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

    /// <param name="allowNone">
    /// Whether the None swatch is offered. False for a group: a group has no
    /// "no color" state, and offering None let the UI ask for a value the
    /// model refuses, which read to the user as a swatch that does nothing.
    ///
    /// No single-argument overload defaulting this to true, deliberately.
    /// Every caller has to decide, because the permissive answer is the one
    /// that was wrong, and a defaulted parameter is how a fourth entry point
    /// would get it without anyone thinking about it.
    /// </param>
    public TabColorPalettePicker(TabColor initial, bool allowNone)
    {
        InitializeComponent();

        // The grid is the palette's automation element, so it takes its
        // name from the visible heading rather than repeating the string.
        // Set rather than LabeledBy: the heading is out of the automation
        // tree (see the XAML), and LabeledBy would point into nothing.
        //
        // The heading reads "Tab Color", which is wrong for the group picker:
        // a screen reader would announce a tab while the user recolours a
        // group. The control knows which it is, so it says so.
        if (!allowNone) PaletteLabel.Text = "Group Color";
        AutomationProperties.SetName(Swatches, PaletteLabel.Text);

        BuildSwatches(initial, allowNone);

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

    private void BuildSwatches(TabColor initial, bool allowNone)
    {
        // TabColorPalette.PaletteRows is the macOS-derived layout, kept in
        // Ghostty.Core so platform divergence stays in one file. Flattening
        // it here and letting the panel wrap keeps that ordering authoritative.
        //
        // That equivalence holds for the TAB picker only. Dropping None
        // shortens the flat sequence to nine, and the panel still wraps at
        // five, so the group picker reads 5 + 4 and Orange moves up a row --
        // the macOS 2x5 grid is a claim about the full palette, not about
        // every picker built from it.
        foreach (var row in TabColorPalette.PaletteRows)
        {
            foreach (var color in row)
            {
                // Skipped rather than disabled: a disabled first swatch is
                // still a tab stop the keyboard lands on, and the row would
                // open with a hole where the palette's first entry belongs.
                if (color == TabColor.None && !allowNone) continue;

                var swatch = BuildSwatch(color);
                _colors[swatch] = color;
                Swatches.Items.Add(swatch);
                if (color == initial)
                    Swatches.SelectedItem = swatch;
            }
        }

        // Reachable two ways: a TabColor that PaletteRows does not list, or
        // None on a picker that skipped it. Opening on the wrong swatch
        // misreports by one; opening with nothing selected and nothing
        // focused strands the keyboard, which is worse. Items[0] is the
        // palette's first offered colour, which for a group is
        // DefaultGroupColor -- the value the model would have coerced to.
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
            // Border, not Colors[...]: same opaque preset, but through the one
            // door that refuses an unpaintable value by name.
            var drawing = TabColorPalette.Border(color);
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
