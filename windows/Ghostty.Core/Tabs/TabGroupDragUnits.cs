using System.Collections.Generic;

namespace Ghostty.Core.Tabs;

/// <summary>
/// One slot in a group drag's unit space: an ungrouped tab, or a whole
/// group represented by its header. The run is the atom -- a crossing
/// swaps the dragged run past an entire neighbouring unit, never past
/// one row of it, because a run that landed between another group's
/// header and its members would split a run the projector cannot
/// render (a header is always in front of all of its members).
/// </summary>
internal sealed class GroupDragUnit
{
    /// <summary>
    /// The lone tab, or the run's first member. The run is found by
    /// identity through this tab; index arithmetic into the manager is
    /// how a direction bug hides, so nothing keys off it but First.
    /// </summary>
    internal required TabModel Rep;

    /// <summary>The run's group, null for a lone tab.</summary>
    internal required TabGroup? Group;

    /// <summary>Manager index of the run's first member.</summary>
    internal required int First;

    /// <summary>Manager size of the run: one for a lone tab.</summary>
    internal required int Count;
}

/// <summary>
/// The unit space a vertical group drag speaks: body runs in manager
/// order, one unit per run, with the MoveGroup targets a crossing maps
/// to. The pinned prefix contributes nothing -- groups cannot be pinned
/// and a run that crossed into the prefix would be clamped right back,
/// so the units never offer a crossing the commit would refuse (the
/// same no-false-promise rule the pin ghost obeys). MoveGroup's clamp
/// stays as the backstop.
///
/// Hidden members change nothing here: collapse hides rows, and a run
/// is one unit at its first member whether every member is visible or
/// only the active one (Edge-135) or none. Geometry is the strip's to
/// measure; this class is pure manager reading so the mapping and both
/// target directions are unit-testable without a host.
///
/// This is the second strip-private mapping, and it gets the same
/// treatment the first one earned: the target formulas are pinned by
/// executing them against a real manager, because a flipped direction
/// passes every source scan and every wiring pin.
/// </summary>
internal static class TabGroupDragUnits
{
    /// <summary>
    /// The units, in manager order. Emitted at a run's first member only;
    /// the walk is identity-driven (a HashSet, not an index comparison),
    /// so contiguity is assumed, not enforced.
    /// </summary>
    internal static IReadOnlyList<GroupDragUnit> Build(TabManager manager)
    {
        var units = new List<GroupDragUnit>();
        var seen = new HashSet<TabGroup>();
        var tabs = manager.Tabs;
        for (int i = 0; i < tabs.Count; i++)
        {
            var tab = tabs[i];
            if (tab.IsPinned) continue;
            if (tab.Group is { } group)
            {
                if (!seen.Add(group)) continue;
                units.Add(new GroupDragUnit
                {
                    Rep = tab,
                    Group = group,
                    First = i,
                    Count = manager.MembersOf(group).Count,
                });
            }
            else
            {
                units.Add(new GroupDragUnit
                {
                    Rep = tab,
                    Group = null,
                    First = i,
                    Count = 1,
                });
            }
        }
        return units;
    }

    /// <summary>
    /// The MoveGroup target when the dragged run swaps DOWN past
    /// <paramref name="pivot"/>: the run lands after the pivot's last
    /// member, so the head's final index is the pivot's span plus the
    /// room the dragged run's own departure leaves. Executing this is
    /// the oracle -- the tests assert final manager order, not the
    /// formula.
    /// </summary>
    internal static int TargetAfter(IReadOnlyList<GroupDragUnit> units, GroupDragUnit dragged, int pivot)
        => units[pivot].First + units[pivot].Count - dragged.Count;

    /// <summary>
    /// The MoveGroup target when the dragged run swaps UP past
    /// <paramref name="pivot"/>: the run lands before the pivot's first
    /// member, whose index the dragged run's departure does not move.
    /// </summary>
    internal static int TargetBefore(IReadOnlyList<GroupDragUnit> units, int pivot)
        => units[pivot].First;
}
