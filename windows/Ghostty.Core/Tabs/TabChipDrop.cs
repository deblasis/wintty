using System;
using System.Collections.Generic;

namespace Ghostty.Core.Tabs;

/// <summary>
/// The horizontal drop's one mapping from "where the user aimed" to a
/// manager index: where a completed drag's commit lands. At completion
/// the strip holds TabView's own reorder, which is the only arrangement
/// that describes the aim, while the manager has not moved yet. A raw
/// strip slot is not a manager index there -- chips occupy slots, and
/// the members a collapsed run hides occupy none -- so the host hands
/// this map IDENTITIES (the tab or chip the drop came to rest beside or
/// upon, read off the strip's own arrangement) and the map answers in
/// manager space only.
///
/// Two drag shapes share the map. A chip drag moves the whole run
/// (<see cref="GroupTarget"/>): the target is the left neighbour's run
/// edge, minus the dragged run's own size when it started left of that
/// edge, because <see cref="TabManager.MoveGroup"/>'s target counts the
/// tabs that STAY ahead of the run. A tab dropped at a chip's slot
/// either joins the run -- the caller decides that half by geometry --
/// or positions beside it (<see cref="MemberTargetBefore"/> /
/// <see cref="MemberTargetAfter"/>); a tab dropped at an ordinary slot
/// needs no arithmetic here at all,
/// <see cref="TabStripProjection.VisibleIndexToModelIndex"/> already
/// names that tab.
///
/// This is the third strip-private mapping, and it gets the treatment
/// the first two earned: the targets are pinned by executing them
/// against a real manager and asserting final order, because a
/// transposed boundary passes every source scan and every wiring pin.
/// </summary>
internal static class TabChipDrop
{
    /// <summary>
    /// The <see cref="TabManager.MoveGroup"/> target when the chip of
    /// <paramref name="dragged"/> came to rest after the strip element
    /// named by (<paramref name="leftTab"/>, <paramref name="leftChip"/>)
    /// -- the left neighbour as the strip ARRANGED it at rest, never a
    /// slot re-read through the projection: between a chip's origin and
    /// a downward rest, TabView shifts every strip slot left by one, so
    /// a projector read there names the dragged run itself. Both nulls
    /// name the strip head, whose landing <see cref="TabManager.MoveGroup"/>'s
    /// clamp lifts clear of the pinned prefix.
    /// </summary>
    public static int GroupTarget(
        TabManager manager, TabGroup dragged, TabModel? leftTab, TabGroup? leftChip)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(dragged);
        var run = manager.MembersOf(dragged);
        if (run.Count == 0) return -1;

        int edge;
        if (leftChip is { } chip)
        {
            // A chip stands for its whole hidden run: the edge is past
            // the last member, not past the chip the strip shows.
            edge = manager.IndexOf(manager.MembersOf(chip)[^1]) + 1;
        }
        else if (leftTab is { } tab)
        {
            // A tab row runs to its run's end too: a member's hidden
            // neighbours behind it are room the landing has to clear.
            edge = manager.IndexOf(manager.RunOf(tab)[^1]) + 1;
        }
        else
        {
            // The strip head: no kept tab sits ahead.
            return 0;
        }

        // The target counts tabs that stay ahead, and the dragged run's
        // own members do not: when the run started left of the edge, its
        // departure is room the landing does not wait for. A run coming
        // from the right subtracts nothing -- the edge did not move.
        int start = manager.IndexOf(run[0]);
        return start < edge ? edge - run.Count : edge;
    }

    /// <summary>
    /// The manager index a tab dropped at a chip's slot lands at when
    /// the drop positions BEFORE the run instead of joining it: the
    /// run's first member, hidden or not.
    /// </summary>
    public static int MemberTargetBefore(TabManager manager, TabGroup chip)
    {
        var run = Members(manager, chip);
        return manager.IndexOf(run[0]);
    }

    /// <summary>
    /// The manager index a tab dropped at a chip's slot lands at when
    /// the drop positions AFTER the run instead of joining it: past the
    /// run's last member, hidden or not.
    /// </summary>
    public static int MemberTargetAfter(TabManager manager, TabGroup chip)
    {
        var run = Members(manager, chip);
        return manager.IndexOf(run[^1]) + 1;
    }

    private static IReadOnlyList<TabModel> Members(TabManager manager, TabGroup chip)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(chip);
        return manager.MembersOf(chip);
    }
}
