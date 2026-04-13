using System;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Tabs;

/// <summary>
/// Visual picker for Snap Layouts zones. Renders a miniature of the
/// current monitor with a clickable Button per zone, styled to
/// resemble the Windows 11 maximize-button hover flyout.
///
/// The picker does NOT query DisplayArea itself: the caller passes
/// the work-area width and height so this control stays unit-testable
/// in principle and lets the detach flow resolve the monitor on the
/// source window side (so the new window lands on the same monitor).
/// </summary>
internal sealed partial class SnapZonePicker : UserControl
{
    /// <summary>Raised when the user clicks a zone. Caller must
    /// close the owning Flyout and run the detach-with-placement
    /// flow. Never raised more than once per picker instance.</summary>
    public event EventHandler<SnapZone>? ZoneSelected;

    // Miniature dimensions. The Canvas Width and Height are reset
    // from Render() so the miniature's aspect ratio matches the real
    // monitor's aspect. These defaults are only the initial XAML
    // values so Blend/design-time rendering has something to draw.
    private const double MaxMiniatureWidth = 220.0;
    private const double MaxMiniatureHeight = 140.0;

    public SnapZonePicker()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Populate the canvas with one Button per zone returned by
    /// <see cref="SnapZoneCatalog"/>. Must be called before the
    /// picker is added to the visual tree.
    /// </summary>
    public void Render(int workAreaWidth, int workAreaHeight)
    {
        // Guard against degenerate monitors (DisplayArea fallback).
        if (workAreaWidth <= 0 || workAreaHeight <= 0)
        {
            workAreaWidth = 1920;
            workAreaHeight = 1080;
        }

        // Fit the monitor into the miniature bounds while preserving
        // aspect ratio. Landscape monitors use the full width; portrait
        // monitors use the full height. Ultra-wide monitors pick the
        // smaller scale factor so neither dimension exceeds the max.
        double scaleW = MaxMiniatureWidth / workAreaWidth;
        double scaleH = MaxMiniatureHeight / workAreaHeight;
        double scale = Math.Min(scaleW, scaleH);

        double miniW = workAreaWidth * scale;
        double miniH = workAreaHeight * scale;

        PickerCanvas.Width = miniW;
        PickerCanvas.Height = miniH;
        PickerCanvas.Children.Clear();

        var zones = SnapZoneCatalog.ZonesFor(workAreaWidth, workAreaHeight);
        foreach (var zone in zones)
        {
            // Use a synthetic 0-origin work area so the rect fractions
            // map linearly to the miniature. The real window placement
            // uses the true work-area origin later.
            var rect = SnapZoneMath.RectFor(zone, 0, 0, workAreaWidth, workAreaHeight);
            var btn = MakeZoneButton(zone, rect, scale);
            PickerCanvas.Children.Add(btn);
        }
    }

    private Button MakeZoneButton(SnapZone zone, SnapZoneRect rect, double scale)
    {
        var btn = new Button
        {
            Width = Math.Max(1.0, rect.Width * scale),
            Height = Math.Max(1.0, rect.Height * scale),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(1),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = (Brush)Application.Current.Resources[
                "SystemControlForegroundBaseMediumBrush"],
            BorderThickness = new Thickness(1),
        };
        AutomationProperties.SetName(btn, ReadableName(zone));
        ToolTipService.SetToolTip(btn, ReadableName(zone));

        Canvas.SetLeft(btn, rect.X * scale);
        Canvas.SetTop(btn, rect.Y * scale);

        btn.Click += (_, _) => ZoneSelected?.Invoke(this, zone);
        return btn;
    }

    private static string ReadableName(SnapZone zone) => zone switch
    {
        SnapZone.Maximize => "Maximize",
        SnapZone.LeftHalf => "Left half",
        SnapZone.RightHalf => "Right half",
        SnapZone.TopHalf => "Top half",
        SnapZone.BottomHalf => "Bottom half",
        SnapZone.TopLeftQuarter => "Top-left quarter",
        SnapZone.TopRightQuarter => "Top-right quarter",
        SnapZone.BottomLeftQuarter => "Bottom-left quarter",
        SnapZone.BottomRightQuarter => "Bottom-right quarter",
        SnapZone.LeftThird => "Left third",
        SnapZone.MiddleThird => "Middle third",
        SnapZone.RightThird => "Right third",
        SnapZone.LeftTwoThirds => "Left two-thirds",
        SnapZone.RightTwoThirds => "Right two-thirds",
        SnapZone.TopThird => "Top third",
        SnapZone.MiddleThirdHorizontal => "Middle third",
        SnapZone.BottomThird => "Bottom third",
        _ => throw new ArgumentOutOfRangeException(nameof(zone), zone, null),
    };
}
