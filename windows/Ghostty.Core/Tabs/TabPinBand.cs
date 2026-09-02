using System;

namespace Ghostty.Core.Tabs;

/// <summary>
/// The pinned zone's shape: a band of icon squares that wraps.
///
/// A pinned tab is an icon square, not a row. Three pins spend one band
/// row where three rows used to go, and that change of shape is what
/// separates the zones -- the pinned band and the body list are visibly
/// different kinds of thing, so the zone needs no rule drawn between
/// them. (The 1.09:1 boundary stroke this retires was drawing a line
/// where the structure now speaks.)
///
/// Pure arithmetic, in Core next to the zone grammar, so the band's
/// geometry is pinnable by tests without a WinUI host: the panel that
/// arranges the band, the drop preview that promises the next slot, and
/// the harness that measures the result all read the same numbers.
/// Coordinates are the band's own space, origin at its top-left.
/// </summary>
public static class TabPinBand
{
    /// <summary>
    /// The icon square's edge. Sized so the 48px compact rail fits one
    /// square with the rows' own 4px inset on each side, and so a square
    /// keeps the 40px pitch the pinned rows had before they collapsed --
    /// a band row is exactly as tall as a body row.
    /// </summary>
    public const double ChipSize = 40;

    /// <summary>
    /// The gutter between squares, on both axes. Equal to twice the
    /// rows' 2px vertical inset, so a one-column band is pitch-identical
    /// to the stack of rows it replaces and a pin that lands in a
    /// compact rail does not shift the list under it.
    /// </summary>
    public const double ChipGap = 4;

    /// <summary>
    /// How many squares fit across <paramref name="availableWidth"/>.
    ///
    /// Never zero: a band narrower than one square still renders that
    /// square (clipped by the pane, as every too-narrow row is), because
    /// a zero-column band would divide by zero in every slot query and
    /// report a height of zero for pins that exist.
    /// </summary>
    public static int ColumnsFor(double availableWidth)
    {
        // Infinity is a real measure input -- a vertical StackPanel offers
        // its children unbounded height and some parents offer unbounded
        // width -- and it is called out because the arithmetic below does
        // NOT degrade gracefully to it: (int) of an infinite double is
        // undefined in C# and lands on int.MinValue in practice, which
        // Math.Max would then quietly turn into a one-column band. A band
        // offered unbounded width puts every square on one row.
        if (double.IsPositiveInfinity(availableWidth)) return int.MaxValue;
        if (double.IsNaN(availableWidth) || availableWidth < ChipSize) return 1;
        // The last column spends no trailing gutter, so lend one to the
        // width before dividing by the full pitch.
        return Math.Max(1, (int)Math.Floor((availableWidth + ChipGap) / (ChipSize + ChipGap)));
    }

    /// <summary>How many band rows <paramref name="chipCount"/> squares occupy.</summary>
    public static int RowsFor(int chipCount, int columns)
    {
        if (chipCount <= 0) return 0;
        if (columns < 1)
            throw new ArgumentOutOfRangeException(
                nameof(columns), columns, "A band has at least one column.");
        // Before the ceiling division, and not only as a shortcut: that
        // division adds `columns` to `chipCount`, which overflows for the
        // unbounded count ColumnsFor answers with when a parent offers
        // infinite width. Unchecked arithmetic wraps it negative and the
        // band reports zero rows for squares that exist.
        if (columns >= chipCount) return 1;
        return (chipCount + columns - 1) / columns;
    }

    /// <summary>
    /// Which band row and column the square at <paramref name="index"/>
    /// takes. Reading order: left to right, then down, which is the order
    /// the pinned prefix itself is in, so the band never reorders what
    /// the manager holds.
    /// </summary>
    public static (int Row, int Column) SlotOf(int index, int columns)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(
                nameof(index), index, "A band slot index is never negative.");
        if (columns < 1)
            throw new ArgumentOutOfRangeException(
                nameof(columns), columns, "A band has at least one column.");
        return (index / columns, index % columns);
    }

    /// <summary>The top-left of the square at <paramref name="index"/>.</summary>
    public static (double X, double Y) OriginOf(int index, int columns)
    {
        var (row, column) = SlotOf(index, columns);
        return (column * (ChipSize + ChipGap), row * (ChipSize + ChipGap));
    }

    /// <summary>
    /// The band's width: the columns actually IN USE, so the box hugs the
    /// squares rather than claiming the rest of the pane.
    ///
    /// Safe to hug because the band is laid out from its left edge and its
    /// left edge is fixed by its margin -- shrinking the box moves no
    /// square, and the harness that asserts every square is inside the
    /// band's own box is asserting something real rather than something a
    /// wider box would make true for free.
    ///
    /// It is NOT where the column count comes from. Feeding this width back
    /// through <see cref="ColumnsFor"/> answers "as many columns as there
    /// are squares", which is the pane's capacity only when the band is
    /// full; the panel keeps the width the pane offered for that.
    /// </summary>
    public static double BandWidth(int chipCount, int columns)
    {
        if (chipCount <= 0) return 0;
        if (columns < 1)
            throw new ArgumentOutOfRangeException(
                nameof(columns), columns, "A band has at least one column.");
        var used = Math.Min(chipCount, columns);
        return used * ChipSize + (used - 1) * ChipGap;
    }

    /// <summary>The band's height for <paramref name="chipCount"/> squares.</summary>
    public static double BandHeight(int chipCount, int columns)
    {
        var rows = RowsFor(chipCount, columns);
        return rows == 0 ? 0 : rows * ChipSize + (rows - 1) * ChipGap;
    }
}
