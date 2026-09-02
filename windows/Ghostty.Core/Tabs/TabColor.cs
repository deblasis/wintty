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

    /// <summary>
    /// The swatch a group falls back to. A group has no "no color" state, so
    /// its paint sites index <see cref="Colors"/> with no guard -- swatch,
    /// chip ink, run label, vertical header row, and the switcher's card,
    /// ring and dot. <see cref="TabGroup"/> coerces to this rather than
    /// leaving the invariant to whoever happens to write the property.
    /// </summary>
    public const TabColor DefaultGroupColor = TabColor.Blue;

    /// <summary>
    /// A color a group can actually be painted in. <see cref="TabColor.None"/>
    /// is a tab state, not a group state: on a tab it means "no tint" and the
    /// tab's paint sites each choose a different brush, but a group's swatch
    /// has nothing to fall back to.
    ///
    /// The test is "has a preset", not "is not None", and the difference is
    /// the whole point. The invariant the paint sites rely on is that the
    /// colour can be looked up, and every value outside the enum's declared
    /// members fails that too. A group colour is persisted as a NUMBER (see
    /// <c>GroupSession.Color</c>; the session context defines no string
    /// converter) and System.Text.Json does not check that a numeric enum is
    /// a defined member, so a hand-edited or forward-version session can hand
    /// <c>RestoreGroup</c> a <c>(TabColor)42</c>. Guarding None alone let that
    /// through to the same mid-paint crash under a different integer.
    ///
    /// Coercing here means the value is also PERSISTED coerced:
    /// <c>SessionCapture</c> reads the resolved property, so a session written
    /// by a future build carrying an extra swatch comes back as the default
    /// and is written back that way on the next save. That is a repair for a
    /// corrupt value and a downgrade loss for a forward one, and it is the
    /// deliberate choice: keeping the raw value and coercing at the paint
    /// boundary would preserve the colour, but it would give up the property
    /// every group paint site depends on -- that the colour in hand can
    /// always be looked up. wintty ships one lineage and has no downgrade
    /// story, so the invariant is worth more than the swatch.
    /// </summary>
    public static TabColor EnsureGroupColor(TabColor color)
        => Colors.ContainsKey(color) ? color : DefaultGroupColor;

    /// <summary>
    /// The preset behind a color. <see cref="TabColor.None"/> has no entry by
    /// design -- it means "no tint", and only the caller knows what to paint
    /// in its place -- and neither has any value outside the enum's declared
    /// members. Indexing <see cref="Colors"/> directly raised a bare
    /// <c>KeyNotFoundException</c> from inside a paint pass, naming neither
    /// the value nor the rule.
    /// </summary>
    private static Color Preset(TabColor color)
        => Colors.TryGetValue(color, out var rgb)
            ? rgb
            : throw new ArgumentOutOfRangeException(
                nameof(color), color,
                // The message must not name None: this fires for any value
                // with no preset, and an out-of-range integer arriving from a
                // persisted session is the case most in need of being read
                // literally.
                color == TabColor.None
                    ? "TabColor.None has no preset: it means \"no tint\", so the caller "
                      + "chooses what to paint instead. A group cannot be None -- use "
                      + nameof(EnsureGroupColor) + "."
                    // States the rule, not a diagnosis: Preset serves the tab
                    // path too, and a message naming sessions and groups would
                    // send a reader the wrong way for a tab. The type and
                    // ActualValue already carry the argument.
                    : "not a declared TabColor: only declared members have presets.");

    /// <summary>Selected tab/header fill uses the full preset color.</summary>
    public const byte SelectedBackgroundAlpha = 255;

    /// <summary>Inactive tabs stay translucent so the strip chrome shows through.</summary>
    public const byte UnselectedBackgroundAlpha = 89;

    /// <summary>
    /// Tab strip background for a preset color. <see cref="TabColor.None"/> is invalid.
    /// </summary>
    public static Color Background(TabColor color, bool selected)
    {
        var rgb = Preset(color);
        var alpha = selected ? SelectedBackgroundAlpha : UnselectedBackgroundAlpha;
        return Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B);
    }

    /// <summary>Opaque preset color for the active pane border.</summary>
    public static Color Border(TabColor color) => Preset(color);

    /// <summary>
    /// A group FIELD's wash. Lighter than a tab's own tint because a field
    /// is a GROUND: it sits behind whole tiles, several of which carry
    /// preset tints of their own, and a field at the tab alpha turns the
    /// run into one block of colour with the tiles lost inside it.
    /// </summary>
    public const byte FieldWashAlpha = 46;

    /// <summary>
    /// sRGB backdrop after compositing a preset tint over the strip fill.
    /// Selected rows use the full preset; inactive rows alpha-blend over
    /// <paramref name="stripBackdropRgb"/> (0x00RRGGBB).
    /// </summary>
    public static uint EffectiveBackgroundRgb(
        TabColor color, bool selected, uint stripBackdropRgb)
    {
        if (!selected)
            return Composite(color, UnselectedBackgroundAlpha, stripBackdropRgb);
        var preset = Preset(color);
        return PackRgb(preset.R, preset.G, preset.B);
    }

    /// <summary>
    /// A group field's fill, as an OPAQUE sRGB value (0x00RRGGBB) rather
    /// than a translucent brush.
    ///
    /// The difference is not cosmetic. A translucent wash handed to XAML is
    /// composited by the system over whatever material is behind the
    /// surface, and over Mica that is a backdrop the app does not control:
    /// the tint comes back desaturated by an amount that changes with the
    /// user's wallpaper. Compositing here against the ground the window
    /// reports produces one colour the painter can commit to, which is what
    /// makes a field readable as the SAME field across every cell of a run.
    /// </summary>
    public static uint FieldBackgroundRgb(TabColor color, uint groundRgb)
        => Composite(color, FieldWashAlpha, groundRgb);

    /// <summary>
    /// Foreground sRGB (0x00RRGGBB) readable on a field's wash -- the
    /// header's title and count ink.
    /// </summary>
    public static uint FieldForegroundRgb(TabColor color, uint groundRgb)
    {
        var bg = FieldBackgroundRgb(color, groundRgb);
        return ThemeResolution.EnsureReadableForeground(bg, bg);
    }

    /// <summary>One preset alpha-blended over an opaque ground.</summary>
    private static uint Composite(TabColor color, byte tintAlpha, uint groundRgb)
    {
        // Preset, not Colors[color]: this method is the one place every tint
        // composite now funnels through, so an indexer here would put the bare
        // KeyNotFoundException back on the paint path for the whole family --
        // fields and switcher cards included, which is more callers than the
        // indexer ever had.
        var preset = Preset(color);
        var alpha = tintAlpha / 255.0;
        var inv = 1.0 - alpha;
        var br = (groundRgb >> 16) & 0xFF;
        var bg = (groundRgb >> 8) & 0xFF;
        var bb = groundRgb & 0xFF;
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
