using System;

namespace Ghostty.Core.Tabs;

/// <summary>What one drag crossing means at the pin boundary.</summary>
public enum TabPinZoneOp
{
    /// <summary>Both slots are in the same zone; a plain Move commits it.</summary>
    None,

    /// <summary>An unpinned row crossed up into the prefix and must pin.</summary>
    Pin,

    /// <summary>A pinned row crossed down out of the prefix and must unpin.</summary>
    Unpin,
}

/// <summary>One crossing's zone meaning and its target slot.</summary>
public readonly record struct TabPinZoneCrossing(TabPinZoneOp Op, int To);

/// <summary>
/// Classifies a drag crossing against the pinned prefix: does this
/// crossing stay inside one zone, or does it carry the dragged row
/// across the boundary, and if so in which direction.
///
/// The pinned zone is a PREFIX of the one list (spec 4.1), so a crossing
/// is zone-crossing purely by index arithmetic: a crossing whose target
/// slot sits on the other side of <see cref="TabManager.PinCount"/> from
/// the dragged row cannot be committed by <see cref="TabManager.Move"/>
/// alone -- Move clamps to the row's own zone and would silently no-op.
/// The drag layer calls this first and turns a zone crossing into the
/// manager's Move + <see cref="TabManager.SetPinned"/> pair, one commit
/// at the call site.
///
/// Pure so the boundary grammar is unit-testable without a WinUI host;
/// the strip applies the answer through the manager and reads the truth
/// back, exactly as it does for a plain move.
/// </summary>
public static class TabPinBoundary
{
    /// <summary>
    /// Classify one machine crossing. <paramref name="to"/> is the
    /// machine's crossing slot (the same space <see cref="TabManager.Move"/>
    /// takes). An out-of-range slot classifies as <see cref="TabPinZoneOp.None"/>
    /// rather than a zone change: the machine never emits one, and a
    /// malformed slot must not be promoted into a pin toggle -- the move
    /// it rides with will refuse and the read-back catches it.
    /// </summary>
    public static TabPinZoneCrossing Classify(
        bool draggedIsPinned, int pinCount, int rowCount, int to)
    {
        if (pinCount < 0 || pinCount > rowCount)
            throw new ArgumentOutOfRangeException(
                nameof(pinCount), pinCount,
                "PinCount outside [0, rowCount] is corrupt state, not a zone.");
        if (to < 0 || to >= rowCount)
            return new TabPinZoneCrossing(TabPinZoneOp.None, to);

        if (draggedIsPinned)
            return to >= pinCount
                ? new TabPinZoneCrossing(TabPinZoneOp.Unpin, to)
                : new TabPinZoneCrossing(TabPinZoneOp.None, to);

        return to < pinCount
            ? new TabPinZoneCrossing(TabPinZoneOp.Pin, to)
            : new TabPinZoneCrossing(TabPinZoneOp.None, to);
    }
}
