using System.Collections.Generic;
using System.Drawing;

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
}
