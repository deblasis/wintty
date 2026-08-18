using System;
using System.Collections.Generic;
using System.Drawing;
using Ghostty.Core.Windows;

namespace Ghostty.Core.Tabs;

/// <summary>
/// Preset tint for a <see cref="TabModel"/>. Ten entries matching
/// macOS <c>TerminalTabColor</c> one-for-one so multi-platform users
/// see the same palette. <see cref="None"/> is the default and clears
/// the tint.
///
/// Lives in Ghostty.Core (pure net9.0) so Ghostty.Tests consumes it
/// directly via ProjectReference without pulling WinUI.
/// </summary>
internal enum TabColor
{
    None = 0,
    Blue,
    Purple,
    Pink,
    Red,
    Orange,
    Yellow,
    Green,
    Teal,
    Graphite,
}

/// <summary>
/// sRGB color values for <see cref="TabColor"/>. Keyed in enum order;
/// <see cref="TabColor.None"/> has no entry (callers check for it
/// explicitly and paint transparent).
///
/// Hex values picked to match the macOS system* color rendering in
/// sRGB (approx, since macOS system colors shift slightly across
/// OS versions). Our values are fixed by design: terminal tab tints
/// should not drift under the user's feet. Alpha is set by the
/// painter (see <c>TabHost.AddItem</c>), not here.
///
/// macOS source: macos/Sources/Features/Terminal/TerminalTabColor.swift
/// </summary>
internal static class TabColorPalette
{
    // 2-row x 5-column layout matching macOS TabColorMenuView.paletteRows.
    // Row 1: None, Blue, Purple, Pink, Red
    // Row 2: Orange, Yellow, Green, Teal, Graphite
    public static readonly TabColor[][] PaletteRows =
    {
        new[] { TabColor.None, TabColor.Blue, TabColor.Purple, TabColor.Pink, TabColor.Red },
        new[] { TabColor.Orange, TabColor.Yellow, TabColor.Green, TabColor.Teal, TabColor.Graphite },
    };

    // sRGB values approximating macOS NSColor.system* at standard
    // contrast. Source per entry documented inline. Alpha fixed at 255;
    // callers blend as needed.
    public static readonly IReadOnlyDictionary<TabColor, Color> Colors =
        new Dictionary<TabColor, Color>
        {
            // NSColor.systemBlue     approx #007AFF
            [TabColor.Blue]     = Color.FromArgb(255, 0x00, 0x7A, 0xFF),
            // NSColor.systemPurple   approx #AF52DE
            [TabColor.Purple]   = Color.FromArgb(255, 0xAF, 0x52, 0xDE),
            // NSColor.systemPink     approx #FF2D55
            [TabColor.Pink]     = Color.FromArgb(255, 0xFF, 0x2D, 0x55),
            // NSColor.systemRed      approx #FF3B30
            [TabColor.Red]      = Color.FromArgb(255, 0xFF, 0x3B, 0x30),
            // NSColor.systemOrange   approx #FF9500
            [TabColor.Orange]   = Color.FromArgb(255, 0xFF, 0x95, 0x00),
            // NSColor.systemYellow   approx #FFCC00
            [TabColor.Yellow]   = Color.FromArgb(255, 0xFF, 0xCC, 0x00),
            // NSColor.systemGreen    approx #34C759
            [TabColor.Green]    = Color.FromArgb(255, 0x34, 0xC7, 0x59),
            // NSColor.systemTeal     approx #30B0C7
            [TabColor.Teal]     = Color.FromArgb(255, 0x30, 0xB0, 0xC7),
            // NSColor.systemGray     approx #8E8E93
            [TabColor.Graphite] = Color.FromArgb(255, 0x8E, 0x8E, 0x93),
        };

    /// <summary>
    /// Human-readable label. Used for the swatch tooltip (<c>ToolTipService.ToolTip</c>).
    /// Matches macOS <c>TerminalTabColor.localizedName</c>.
    /// </summary>
    public static string LocalizedName(TabColor color) => color switch
    {
        TabColor.None     => "None",
        TabColor.Blue     => "Blue",
        TabColor.Purple   => "Purple",
        TabColor.Pink     => "Pink",
        TabColor.Red      => "Red",
        TabColor.Orange   => "Orange",
        TabColor.Yellow   => "Yellow",
        TabColor.Green    => "Green",
        TabColor.Teal     => "Teal",
        TabColor.Graphite => "Graphite",
        _                 => "None",
    };

    /// <summary>Selected tab/header fill uses the full preset color.</summary>
    public const byte SelectedBackgroundAlpha = 255;

    /// <summary>Inactive tabs stay translucent so the strip chrome shows through.</summary>
    public const byte UnselectedBackgroundAlpha = 89;

    /// <summary>
    /// Tab strip background for a preset color. <see cref="TabColor.None"/> is invalid.
    /// </summary>
    public static Color Background(TabColor color, bool selected)
    {
        var rgb = Colors[color];
        var alpha = selected ? SelectedBackgroundAlpha : UnselectedBackgroundAlpha;
        return Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B);
    }

    /// <summary>Opaque preset color for the active pane border.</summary>
    public static Color Border(TabColor color) => Colors[color];

    /// <summary>
    /// sRGB backdrop after compositing a preset tint over the strip fill.
    /// Selected rows use the full preset; inactive rows alpha-blend over
    /// <paramref name="stripBackdropRgb"/> (0x00RRGGBB).
    /// </summary>
    public static uint EffectiveBackgroundRgb(
        TabColor color, bool selected, uint stripBackdropRgb)
    {
        var preset = Colors[color];
        if (selected)
            return PackRgb(preset.R, preset.G, preset.B);

        var alpha = UnselectedBackgroundAlpha / 255.0;
        var inv = 1.0 - alpha;
        var br = (stripBackdropRgb >> 16) & 0xFF;
        var bg = (stripBackdropRgb >> 8) & 0xFF;
        var bb = stripBackdropRgb & 0xFF;
        return PackRgb(
            (byte)Math.Clamp(preset.R * alpha + br * inv, 0, 255),
            (byte)Math.Clamp(preset.G * alpha + bg * inv, 0, 255),
            (byte)Math.Clamp(preset.B * alpha + bb * inv, 0, 255));
    }

    /// <summary>
    /// Foreground sRGB (0x00RRGGBB) readable on the effective tab tint.
    /// </summary>
    public static uint ForegroundRgb(
        TabColor color, bool selected, uint stripBackdropRgb)
    {
        var bg = EffectiveBackgroundRgb(color, selected, stripBackdropRgb);
        return ThemeResolution.EnsureReadableForeground(bg, bg);
    }

    private static uint PackRgb(byte r, byte g, byte b)
        => ((uint)r << 16) | ((uint)g << 8) | b;
}
