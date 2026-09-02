using System;
using System.Collections.Generic;

namespace Ghostty.Core.Tabs;

/// <summary>
/// The join gesture's two questions that are not the clock's: WHICH row
/// the ring is drawn over, and what a release over it commits.
///
/// Both strips ask them. The vertical strip measures arranged row centers
/// down its axis, the horizontal one measures equal-width tab centers
/// across its own; the rule is the same either way and lives here once,
/// so a gesture that means one thing in a sidebar cannot come to mean
/// another in a tab strip.
///
/// This is the fourth strip-private mapping, and it gets the treatment
/// the first three earned: the targets and the commit are pinned by
/// executing them against a real manager and asserting final membership,
/// because a neighbour picked on the wrong side passes every source scan
/// and every wiring pin.
/// </summary>
internal static class TabJoinDrop
{
    /// <summary>
    /// The slot the ring belongs over, or -1 for none: the ADJACENT slot
    /// whose arranged center the dragged row has come to sit on.
    ///
    /// Neighbours only, and deliberately. The dragged row's own slot
    /// tracks the pointer through the crossings the engine commits live,
    /// so by construction the only thing the row can be sitting on top of
    /// is the slot it has not yet crossed into. A wider search would name
    /// a row two slots away that the drag is merely pointed at, and the
    /// ring would promise a join over a row the hand never reached.
    ///
    /// The band is a fraction of the pitch to that neighbour rather than
    /// a pixel count, because the two strips measure in nothing alike --
    /// a 40px row against an equal-width tab -- and the rule is the same
    /// one in both: the dragged row is over its neighbour once it has
    /// travelled most of the way there. At rest in its own slot the
    /// distance is a full pitch, which is outside every band under 1,
    /// so a drag that is going nowhere rings nothing.
    /// </summary>
    public static int PickTarget(
        IReadOnlyList<double> centers, int draggedSlot, double draggedCenter,
        double bandFraction)
    {
        ArgumentNullException.ThrowIfNull(centers);
        if (draggedSlot < 0 || draggedSlot >= centers.Count) return -1;
        if (double.IsNaN(draggedCenter) || double.IsNaN(centers[draggedSlot])) return -1;

        int best = -1;
        double bestDistance = double.MaxValue;
        for (int step = -1; step <= 1; step += 2)
        {
            int slot = draggedSlot + step;
            if (slot < 0 || slot >= centers.Count) continue;
            double center = centers[slot];
            if (double.IsNaN(center)) continue;
            // The pitch is measured to THIS neighbour, so a strip whose
            // rows are not all one height still bands each one honestly.
            double band = Math.Abs(center - centers[draggedSlot]) * bandFraction;
            if (band <= 0) continue;
            double distance = Math.Abs(center - draggedCenter);
            // Strictly inside the band. On the edge -- the exact midpoint
            // between two rows at half a pitch -- the dragged row overlaps
            // both neighbours equally, and picking one of them would be
            // arithmetic deciding what the hand did not. Between rows is
            // where a reorder is aimed anyway.
            if (distance >= band || distance >= bestDistance) continue;
            bestDistance = distance;
            best = slot;
        }
        return best;
    }

    /// <summary>
    /// Whether a release over <paramref name="target"/> would actually
    /// group the two. The strips ask BEFORE they draw the ring: the ring
    /// is a promise, and a promise the commit would refuse is the one
    /// failure this gesture cannot afford -- the same no-false-promise
    /// rule the pin ghost obeys.
    ///
    /// Pinned rows on either side refuse, because the prefix outranks
    /// membership (<see cref="TabManager.GroupTabs"/> skips a pinned
    /// member and <see cref="TabManager.CreateGroup"/> refuses a pinned
    /// tab outright), and two rows already sharing a group refuse because
    /// there is nothing left to join.
    /// </summary>
    public static bool CanJoin(TabManager manager, TabModel dragged, TabModel target)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(dragged);
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(dragged, target)) return false;
        if (dragged.IsPinned || target.IsPinned) return false;
        if (manager.IndexOf(dragged) < 0 || manager.IndexOf(target) < 0) return false;
        if (dragged.Group is { } group && ReferenceEquals(group, target.Group)) return false;
        return true;
    }

    /// <summary>
    /// The commit behind a held release: the dragged tab joins the
    /// target's group, and a target with no group gets one minted around
    /// it first -- that second half is the whole point of the gesture,
    /// since two loose tabs becoming a group is what a user is reaching
    /// for when they hold one over the other.
    ///
    /// The join runs through <see cref="TabManager.JoinGroup"/>, so it
    /// inherits the auto-expand a collapsed target owes and the gather
    /// that puts the two side by side. Answers the group the pair ended
    /// up in, or null when nothing was joined -- read the answer rather
    /// than assuming it: the manager clamps and refuses, and a caller
    /// that traced a join it did not get would be narrating a lie.
    /// </summary>
    public static TabGroup? Join(TabManager manager, TabModel dragged, TabModel target)
    {
        if (!CanJoin(manager, dragged, target)) return null;
        var group = target.Group ?? manager.CreateGroup(target);
        if (group is null) return null;
        manager.JoinGroup(dragged, group);
        return ReferenceEquals(dragged.Group, group) ? group : null;
    }
}
