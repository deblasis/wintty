using System.Collections.Generic;
using Ghostty.Core.Shell;
using Ghostty.Core.Windows;

namespace Ghostty.Core.Tabs;

/// <summary>
/// One contiguous stretch of slots a group owns, in the slot space of
/// whichever strip asked. <see cref="First"/> and <see cref="Last"/> are
/// inclusive, so a run that renders as a single slot -- a collapsed group's
/// header alone, or its chip -- has First == Last rather than a zero span.
/// </summary>
internal readonly record struct GroupFieldRun(TabGroup Group, int First, int Last)
{
    public int SlotCount => Last - First + 1;
}

/// <summary>
/// The group FIELD: the one grammar both strips draw a group with.
///
/// A group used to be inferable only by counting. Vertically it was a
/// header plus a 14px content indent, which moves content and not the
/// container, so a member's chrome was identical to a stranger's;
/// horizontally it was a 2px rail on each member, which says "these are
/// alike" and never says where the run stops. Neither had an end marker,
/// so a run's extent was a thing you worked out rather than a thing you
/// saw.
///
/// The field is a container instead of a decoration: the members sit on a
/// tinted ground that begins at a cap and finishes at an end bar, so the
/// run has a visible beginning and a visible end. Vertically the cap sits
/// along the header row's leading edge; horizontally there is no header
/// row, so the cap is the run's leading edge itself. Same three parts
/// either way -- ground, cap, end -- which is what keeps the two layouts
/// one language rather than two dialects that happen to agree.
///
/// The tint is an INK WASH, never a colour. A colour cannot be chosen
/// here: under Mica the strip is not a surface anyone picked, it is the
/// desktop, and a tint mixed for one wallpaper is invisible over the next.
/// A wash is white or black at low alpha, and whichever pole has headroom
/// moves the ground in a direction that survives whatever is behind it.
/// The pole is scored with <see cref="ThemeResolution.PreferLightForegroundAtAlpha"/>
/// at the alpha it is actually painted at, for the reason that helper
/// documents: at 8% both candidates are pulled most of the way to the
/// ground and the luminance split answers for the wrong one either side of
/// mid grey.
///
/// The cap and the end bar are the one place the group's own colour is
/// spent, because they are the parts that carry identity rather than
/// extent: WHICH group, not HOW FAR. They go through
/// <see cref="ChromeSeparator.EnsureVisible"/>, so a preset that lands
/// within a hair of the field it sits on comes back as a lighter or darker
/// version of itself rather than as the 1.57:1 chip that shipped.
///
/// Everything here is arithmetic over packed 0x00RRGGBB and index spans,
/// so the grammar is pinnable without a WinUI host and BOTH strips can be
/// tested against the same rules.
/// </summary>
internal static class TabGroupField
{
    /// <summary>
    /// The wash: 8% ink over the strip's own ground.
    ///
    /// Picked against the whole grey ramp rather than against one theme.
    /// Below this the field stops separating from the strip on the mid
    /// grounds a translucent frame makes of the chrome, which is exactly
    /// where a wash is needed most; above it the field reads as a surface
    /// of its own and starts competing with the selected row's fill, which
    /// is the one thing in the strip allowed to be a fill.
    /// </summary>
    public const byte WashAlpha = 20;

    /// <summary>
    /// The cap and the end bar, in device-independent pixels. Two, the
    /// same weight the pin boundary stroke and the horizontal group rail
    /// already use: a terminal that reads heavier than the strip's other
    /// rules would claim more than "the run stops here".
    /// </summary>
    public const double TerminalThicknessPx = 2;

    /// <summary>
    /// The field's corner radius. Enough to read as a container edge and
    /// not as a clipped rectangle; the selected row keeps its square
    /// corners, so the two never compete for the same reading.
    /// </summary>
    public const double CornerRadiusPx = 4;

    /// <summary>
    /// WCAG's non-text floor, which the terminals are held to against the
    /// field they sit on -- not against the strip. The field is what is
    /// behind them once the wash has landed, and scoring against the strip
    /// is how a bar ends up marginal on precisely the grounds where the
    /// wash moved the most.
    /// </summary>
    public const double TerminalMinContrast = ChromeSeparator.DefaultMinContrast;

    /// <summary>
    /// The field's own motion, in milliseconds. It follows the rows rather
    /// than leading them, so it borrows the strip's Existing Elements
    /// glide (<see cref="TabStripMotion.GapGlideMs"/>) for a field that
    /// grows, shrinks or slides, and the Fade token for one that arrives
    /// or leaves. Sharing the numbers is the point: a field on a clock of
    /// its own separates from the rows it is drawn around, which is the
    /// one way this can look worse than no field at all.
    /// </summary>
    public const double GlideMs = TabStripMotion.GapGlideMs;

    public const double FadeMs = TabStripMotion.FadeMs;

    /// <summary>
    /// The glide's easing, as the two control points of a cubic bezier --
    /// the strip's Existing Elements curve, written once so the composition
    /// path and the Storyboard path cannot drift apart.
    /// </summary>
    public const double GlideEaseX1 = 0.55;
    public const double GlideEaseY1 = 0.55;
    public const double GlideEaseX2 = 0.0;
    public const double GlideEaseY2 = 1.0;

    /// <summary>
    /// White or black, whichever moves <paramref name="groundRgb"/> the
    /// furthest at <see cref="WashAlpha"/>. Packed 0x00RRGGBB.
    /// </summary>
    public static uint WashInkRgb(uint groundRgb)
        => ThemeResolution.PreferLightForegroundAtAlpha(groundRgb, WashAlpha)
            ? 0xFFFFFFu
            : 0x000000u;

    /// <summary>
    /// What the compositor leaves on screen where the field is painted:
    /// the wash composited over <paramref name="groundRgb"/>. Every
    /// contrast question about something drawn ON the field is asked
    /// against this, never against the strip.
    /// </summary>
    public static uint FieldGroundRgb(uint groundRgb)
        => ThemeResolution.CompositeOver(WashInkRgb(groundRgb), WashAlpha, groundRgb);

    /// <summary>
    /// The wash as an ARGB a brush can be built from directly, 0xAARRGGBB.
    ///
    /// The field is painted TRANSLUCENT, never as the composite
    /// <see cref="FieldGroundRgb"/> names. That composite is what the wash
    /// lands as over the ground the strip believes it has, and it is the
    /// right thing to score contrast against -- but painting it would put
    /// an opaque patch over Mica, which is the colour this design rejected
    /// wearing a different hat. Ink at 8% is the same wash over whatever is
    /// really back there.
    /// </summary>
    public static uint WashArgb(uint groundRgb)
        => WashArgbAt(groundRgb, WashAlpha);

    /// <summary>
    /// The same wash at a different strength, for the two states a member
    /// still owes the pointer.
    ///
    /// MUXC gives an unstyled tab its own pointer-over and pressed brushes,
    /// and washing a run wrote ONE value into all three states -- so every
    /// member of a group, and the chip that stands for a folded one, stopped
    /// answering the pointer at all. The chip is a click target: it expands
    /// the run. Deepening the ink the field already chose keeps that feedback
    /// inside the field's grammar, where a second hue on top of the wash
    /// would be one more colour in a strip whose whole argument is that runs
    /// are grounds rather than colours.
    /// </summary>
    public static uint WashArgbAt(uint groundRgb, byte alpha)
        => ((uint)alpha << 24) | WashInkRgb(groundRgb);

    /// <summary>The wash under a pointer. Same ink, enough deeper to read.</summary>
    public const byte WashHoverAlpha = 34;

    /// <summary>The wash under a press: one more step along the same ramp.</summary>
    public const byte WashPressedAlpha = 46;

    /// <summary>
    /// The cap and end bar colour for a group coloured
    /// <paramref name="color"/>, over a strip whose ground is
    /// <paramref name="groundRgb"/>: the preset itself where it already
    /// clears the floor against the field, and a lightness-shifted version
    /// of itself where it does not.
    ///
    /// The preset goes in opaque (<see cref="TabColorPalette.Border"/>),
    /// not at the swatch's 89/255. The alpha is what put the header chip
    /// at 1.57:1 against its own row in both themes, and a terminal is a
    /// 2px line -- there is no thickness here to spend on being faint.
    /// </summary>
    public static uint TerminalRgb(uint groundRgb, TabColor color)
        => TerminalRgbOn(FieldGroundRgb(groundRgb), color);

    /// <summary>
    /// The same bar, scored against the surface it is actually PAINTED on.
    ///
    /// The vertical field is a Border the bars are edges of, so its ground is
    /// the field's and <see cref="TerminalRgb"/> answers. Horizontally there is
    /// no Border: the cap is drawn on the run's first slot and the end bar on
    /// its last, and neither slot is guaranteed to be wearing the wash. The
    /// selected tab keeps the terminal background, and a member with a preset
    /// of its own keeps that preset -- so a Blue cap on a selected tab over a
    /// blue-ish terminal lands near 2.2:1, and a Red end bar on a Red member is
    /// very nearly invisible, while a rule scored against the field reports
    /// both as clearing the floor.
    /// </summary>
    public static uint TerminalRgbOn(uint paintedOnRgb, TabColor color)
    {
        var preset = TabColorPalette.Border(color);
        var groupRgb = ((uint)preset.R << 16) | ((uint)preset.G << 8) | preset.B;
        return ChromeSeparator.EnsureVisible(
            paintedOnRgb, groupRgb, TerminalMinContrast);
    }

    /// <summary>
    /// The group owning each slot of the vertical strip's projection, or
    /// null for a slot no group owns. A header slot counts as its group's:
    /// the header is the field's cap, so the field starts at its top edge
    /// and not at the first member's.
    /// </summary>
    public static IReadOnlyList<TabGroup?> SlotGroups(
        IReadOnlyList<TabStripProjection.ProjectedRow> rows)
    {
        var owners = new List<TabGroup?>(rows.Count);
        foreach (var row in rows)
        {
            owners.Add(row switch
            {
                TabStripProjection.ProjectedRow.Header { Group: { } group } => group,
                TabStripProjection.ProjectedRow.Item { Tab: { } tab } => tab.Group,
                _ => null,
            });
        }
        return owners;
    }

    /// <summary>
    /// The same reading for the horizontal strip. A chip is its group's
    /// slot -- it IS the collapsed run, so the field is drawn around it
    /// exactly as it is drawn around an expanded run's members, and a
    /// collapsed group does not lose its field for being folded.
    /// </summary>
    public static IReadOnlyList<TabGroup?> SlotGroups(
        IReadOnlyList<TabStripProjection.HorizontalRow> rows)
    {
        var owners = new List<TabGroup?>(rows.Count);
        foreach (var row in rows)
        {
            owners.Add(row switch
            {
                TabStripProjection.HorizontalRow.Chip { Group: { } group } => group,
                TabStripProjection.HorizontalRow.Item { Tab: { } tab } => tab.Group,
                _ => null,
            });
        }
        return owners;
    }

    /// <summary>
    /// The fields to draw for a slot sequence: one run per stretch of
    /// consecutive slots owned by the same group.
    ///
    /// A group that somehow held two separate stretches would get two
    /// fields rather than one field swallowing the stranger between them.
    /// Contiguity is a manager invariant (Normalize re-gathers a run a move
    /// split), so this is not a shape that should arrive -- but drawing one
    /// container around a tab that is not a member is a lie about
    /// membership, and the honest reading of a broken invariant is two
    /// fields that visibly do not add up.
    /// </summary>
    public static IReadOnlyList<GroupFieldRun> Runs(IReadOnlyList<TabGroup?> slotGroups)
    {
        var runs = new List<GroupFieldRun>();
        var at = 0;
        while (at < slotGroups.Count)
        {
            if (slotGroups[at] is not { } group) { at++; continue; }
            var last = at;
            while (last + 1 < slotGroups.Count
                   && ReferenceEquals(slotGroups[last + 1], group))
                last++;
            runs.Add(new GroupFieldRun(group, at, last));
            at = last + 1;
        }
        return runs;
    }
}
