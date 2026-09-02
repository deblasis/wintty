using System;
using System.Collections.Generic;

namespace Ghostty.Core.Tabs;

/// <summary>
/// One cell of the Ctrl+Tab switcher's card, and the field it belongs to.
///
/// The switcher shows tiles; what it could not show was which tiles belong
/// together. A group reads in the strips as a FIELD -- a tinted band with a
/// header at its start and an end bar closing it -- and the switcher takes
/// the same grammar so a user who has learned it in one place has learned it
/// in the other. This record is the seam: the plan says which cells carry a
/// field and which of them carry its two ends, and the shell paints them.
/// </summary>
/// <param name="Tab">The tab this cell renders, or null when the cell is a group chip.</param>
/// <param name="Group">
/// The field this cell sits in, or null when the cell is ungrouped and so
/// paints no field at all.
/// </param>
/// <param name="IsHead">First cell of its field: the one that carries the header.</param>
/// <param name="IsTail">Last cell of its field: the one that carries the end bar.</param>
internal readonly record struct SwitcherCell(
    TabModel? Tab,
    TabGroup? Group,
    bool IsHead,
    bool IsTail);

/// <summary>
/// The switcher card's cell plan, derived from the rows the strips render.
///
/// Lives in Core, beside <see cref="TabStripProjection"/>, for the same
/// reason <see cref="TabRunLabelShape"/> does: the rule about which cells a
/// field spans is worth executing in a test, and a test host cannot load
/// the shell.
/// </summary>
internal static class TabSwitcherField
{
    /// <summary>
    /// Plan the cells for <paramref name="rows"/> -- the same reading the
    /// horizontal strip renders, so the switcher can never draw a group the
    /// strip is not drawing.
    ///
    /// Fields are gathered by ADJACENCY, not by membership. Contiguity is a
    /// manager invariant, so in practice the two agree; but a plan that
    /// gathered by membership would, the one time they did not, paint a
    /// single field across a stranger's cell sitting between two members.
    /// Adjacency can only ever under-claim, and an under-claimed field
    /// reads as two fields of the same colour rather than as a lie about
    /// what is grouped.
    ///
    /// A chip is a field of exactly one cell -- head and tail both. It
    /// already stands for a whole collapsed run and already carries the
    /// header anatomy (dot, title, count, chevron), so wrapping it in the
    /// same field grammar costs one cell and makes the collapsed and
    /// expanded readings of a run the same shape.
    ///
    /// The active member of a collapsed run reaches this as an ordinary
    /// item row (the Edge-135 rule keeps selection visible), so it plans as
    /// a one-cell field of its own group: the switcher says which group the
    /// tab you are about to land on belongs to, which is exactly the fact
    /// the popup used to withhold.
    /// </summary>
    public static IReadOnlyList<SwitcherCell> Plan(
        IReadOnlyList<TabStripProjection.HorizontalRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var cells = new List<SwitcherCell>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            switch (rows[i])
            {
                case TabStripProjection.HorizontalRow.Chip { Group: { } chipGroup }:
                    cells.Add(new SwitcherCell(null, chipGroup, IsHead: true, IsTail: true));
                    break;
                case TabStripProjection.HorizontalRow.Item { Tab: { } tab }:
                    var group = tab.Group;
                    if (group is null)
                    {
                        cells.Add(new SwitcherCell(tab, null, IsHead: false, IsTail: false));
                        break;
                    }
                    cells.Add(new SwitcherCell(
                        tab, group,
                        IsHead: !SharesFieldWith(rows, i - 1, group),
                        IsTail: !SharesFieldWith(rows, i + 1, group)));
                    break;
            }
        }
        return cells;
    }

    /// <summary>
    /// Whether the row at <paramref name="index"/> is an item row of
    /// <paramref name="group"/>. A chip row never joins a neighbouring
    /// field even when it names the same group: the chip stands for the
    /// hidden members, and a field spanning both would draw the run twice.
    /// </summary>
    private static bool SharesFieldWith(
        IReadOnlyList<TabStripProjection.HorizontalRow> rows, int index, TabGroup group)
    {
        if (index < 0 || index >= rows.Count) return false;
        return rows[index] is TabStripProjection.HorizontalRow.Item { Tab: { } neighbour }
            && ReferenceEquals(neighbour.Group, group);
    }
}

/// <summary>
/// The switcher card's geometry and motion, host-free so the numbers are
/// pinnable without a WinUI host -- <see cref="TabRunLabelShape"/>'s split.
///
/// Durations come from the same signature-experiences table
/// <see cref="TabStripMotion"/> reads; the gate is the same
/// <see cref="TabStripMotion.Enabled"/>, asked by the window and passed in.
/// </summary>
internal static class TabSwitcherShape
{
    /// <summary>
    /// The band above every tile in a card that shows at least one field.
    /// Only a field's head paints into it; the rest reserve it empty so a
    /// run's tiles share one baseline. Reserved on ungrouped cells too, for
    /// the same reason: a card whose rows are half a header taller than
    /// their neighbours reads as broken, not as grouped.
    ///
    /// A card with no fields at all reserves nothing -- the band would be
    /// 16px of empty spent on a question nobody asked.
    /// </summary>
    public const double HeaderHeightPx = 16;

    /// <summary>
    /// The bar that closes a field on its trailing edge. Thicker than a
    /// hairline on purpose: it is the mark that says the run ENDS here,
    /// and at 1px it reads as an artifact of the wash rather than as
    /// punctuation.
    /// </summary>
    public const double EndBarWidthPx = 3;

    /// <summary>
    /// Wash bleed around a field's tile: how far the tint extends past the
    /// tile card on the field's own edges. Small -- the field is a ground,
    /// not a frame.
    /// </summary>
    public const double FieldPadPx = 4;

    /// <summary>
    /// The dimming an idle tile's PANE PREVIEW takes so the active one is
    /// unmistakable. The switcher's whole job in a fast cycle is to answer
    /// "which one am I on", and a 2px ring alone loses that answer at a
    /// glance on a card where several tabs already carry preset colours of
    /// their own.
    ///
    /// The preview and not the whole tile, and that is an accessibility
    /// rule rather than a taste: the tile's subtree has the tab's TITLE in
    /// it, and an opacity on an ancestor composites text along with
    /// everything else. At this value a caption over a light card measured
    /// 4.01:1 against WCAG AA's floor of 4.5. The preview carries no text,
    /// is most of a tile's area, and dims without costing anything a reader
    /// needs.
    ///
    /// The lightest dim that still reads at a glance, not the heaviest one
    /// that works. The pane previews are the reason the card is made of
    /// tiles at all, and a dim deep enough to be the whole answer on its own
    /// takes them with it; this one is spent alongside the ring and the
    /// lift, and each of the three is allowed to be subtle because there are
    /// three.
    /// </summary>
    public const double IdleTileOpacity = 0.7;

    /// <summary>The active tile's lift. Small enough not to reflow the grid.</summary>
    public const double ActiveTileScale = 1.05;

    /// <summary>
    /// The highlight move: the ring, the dim, and the lift all cross on one
    /// clock, so the eye reads one thing moving rather than three things
    /// changing.
    /// </summary>
    public const int HighlightMs = 150;

    /// <summary>The card's entrance, on the same table's Fade token family.</summary>
    public const int EnterMs = 120;

    /// <summary>The distance the card rises through its entrance.</summary>
    public const double EnterRisePx = 8;

    /// <summary>
    /// One transition's duration. Motion off is zero -- a cut -- and the
    /// caller lands the end state in the same pass rather than waiting a
    /// dispatcher tick for a zero-length storyboard: the same rule
    /// <see cref="TabRunLabelShape.FadeDuration"/> keeps.
    /// </summary>
    public static TimeSpan HighlightDuration(bool motionOn)
        => TimeSpan.FromMilliseconds(motionOn ? HighlightMs : 0);

    /// <summary>The entrance's duration, under the same gate.</summary>
    public static TimeSpan EnterDuration(bool motionOn)
        => TimeSpan.FromMilliseconds(motionOn ? EnterMs : 0);
}
