using System.Linq;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The join gesture's target picker and its commit.
///
/// The commit half is pinned by EXECUTING it against a real manager and
/// asserting final membership and final order, never by reading the
/// formula back: this is the fourth strip-private mapping in the drag
/// stack, and each of the first three could have shipped a transposed
/// boundary past every source scan and every wiring pin.
///
/// The picker half is pinned against the geometry a strip actually
/// feeds -- evenly pitched centers with the dragged row's own center
/// among them -- because "which neighbour" is exactly the question a
/// sign error answers confidently and wrongly.
/// </summary>
public class TabJoinDropTests
{
    private static TabManager NewManager(int count)
    {
        var mgr = new TabManager((_) => new FakePaneHost());
        for (int i = 0; i < count; i++) mgr.NewTab();
        return mgr;
    }

    // Four rows on a 40px pitch, the vertical strip's own.
    private static readonly double[] Centers = { 20, 60, 100, 140 };

    private const double Band = TabStripMotion.JoinBandFraction;

    // ---- the picker ---------------------------------------------------

    [Fact]
    public void A_row_resting_in_its_own_slot_targets_nothing()
    {
        // A full pitch from either neighbour, which is outside every band
        // under one: a drag that is going nowhere must not ring.
        Assert.Equal(-1, TabJoinDrop.PickTarget(Centers, 1, 60, Band));
    }

    [Fact]
    public void A_row_sitting_on_the_next_slot_targets_it()
    {
        Assert.Equal(2, TabJoinDrop.PickTarget(Centers, 1, 100, Band));
    }

    [Fact]
    public void A_row_sitting_on_the_previous_slot_targets_it()
    {
        // The other direction, spelled out: a sign error picks the same
        // slot in both and this is the test that sees it.
        Assert.Equal(0, TabJoinDrop.PickTarget(Centers, 1, 20, Band));
    }

    [Fact]
    public void A_row_halfway_between_two_slots_targets_neither()
    {
        // Half a pitch from each: exactly the band edge, and the rule is
        // strictly inside it. Between rows is where a reorder is aimed,
        // not a join.
        Assert.Equal(-1, TabJoinDrop.PickTarget(Centers, 1, 80, Band));
        Assert.Equal(-1, TabJoinDrop.PickTarget(Centers, 1, 40, Band));
    }

    [Fact]
    public void A_row_mostly_over_its_neighbour_targets_it()
    {
        Assert.Equal(2, TabJoinDrop.PickTarget(Centers, 1, 95, Band));
        Assert.Equal(0, TabJoinDrop.PickTarget(Centers, 1, 25, Band));
    }

    [Fact]
    public void Only_neighbours_are_ever_targeted()
    {
        // Sitting squarely on slot 3 while dragging slot 0: the crossings
        // the engine commits live mean this cannot happen in the strip,
        // and if it ever did, a ring two rows away would promise a join
        // over a row the hand never reached.
        Assert.Equal(-1, TabJoinDrop.PickTarget(Centers, 0, 140, Band));
    }

    [Fact]
    public void The_ends_of_the_strip_have_one_neighbour_each()
    {
        Assert.Equal(1, TabJoinDrop.PickTarget(Centers, 0, 60, Band));
        Assert.Equal(2, TabJoinDrop.PickTarget(Centers, 3, 100, Band));
    }

    [Fact]
    public void An_unmeasured_row_is_skipped_rather_than_targeted()
    {
        // A hidden or unarranged row has no center. NaN comparisons are
        // all false, so an unguarded band test would silently never
        // match -- or, with the comparison written the other way, always.
        double[] centers = { 20, double.NaN, 100 };
        Assert.Equal(-1, TabJoinDrop.PickTarget(centers, 0, double.NaN, Band));
        Assert.Equal(-1, TabJoinDrop.PickTarget(centers, 2, 60, Band));
    }

    [Fact]
    public void Slots_stacked_on_one_center_target_nothing()
    {
        // Zero pitch is a strip mid-arrange. The band would be zero, and
        // every row would be inside it.
        double[] centers = { 60, 60, 60 };
        Assert.Equal(-1, TabJoinDrop.PickTarget(centers, 1, 60, Band));
    }

    [Fact]
    public void A_slot_outside_the_strip_targets_nothing()
    {
        Assert.Equal(-1, TabJoinDrop.PickTarget(Centers, -1, 60, Band));
        Assert.Equal(-1, TabJoinDrop.PickTarget(Centers, 4, 60, Band));
    }

    // ---- what a join is allowed to be ---------------------------------

    [Fact]
    public void Two_loose_tabs_can_join()
    {
        var mgr = NewManager(2);
        Assert.True(TabJoinDrop.CanJoin(mgr, mgr.Tabs[0], mgr.Tabs[1]));
    }

    [Fact]
    public void A_tab_cannot_join_itself()
    {
        var mgr = NewManager(2);
        Assert.False(TabJoinDrop.CanJoin(mgr, mgr.Tabs[0], mgr.Tabs[0]));
    }

    [Fact]
    public void A_pinned_row_on_either_side_refuses()
    {
        // Groups are never pinned: the manager's own ops skip a pinned
        // member and refuse a pinned tab outright, so a ring drawn here
        // would promise a join the release could not keep.
        var mgr = NewManager(3);
        mgr.SetPinned(mgr.Tabs[0], true);
        Assert.False(TabJoinDrop.CanJoin(mgr, mgr.Tabs[0], mgr.Tabs[1]));
        Assert.False(TabJoinDrop.CanJoin(mgr, mgr.Tabs[1], mgr.Tabs[0]));
    }

    [Fact]
    public void Two_members_of_one_group_refuse()
    {
        var mgr = NewManager(3);
        var group = mgr.CreateGroup(mgr.Tabs[0])!;
        mgr.JoinGroup(mgr.Tabs[1], group);
        Assert.False(TabJoinDrop.CanJoin(mgr, mgr.Tabs[0], mgr.Tabs[1]));
    }

    [Fact]
    public void A_loose_tab_can_join_a_member_of_a_group()
    {
        var mgr = NewManager(3);
        var group = mgr.CreateGroup(mgr.Tabs[0])!;
        mgr.JoinGroup(mgr.Tabs[1], group);
        Assert.True(TabJoinDrop.CanJoin(mgr, mgr.Tabs[2], mgr.Tabs[0]));
    }

    [Fact]
    public void A_tab_the_manager_does_not_own_refuses()
    {
        var mgr = NewManager(2);
        var other = NewManager(1);
        Assert.False(TabJoinDrop.CanJoin(mgr, other.Tabs[0], mgr.Tabs[0]));
    }

    // ---- the commit ---------------------------------------------------

    [Fact]
    public void Joining_a_loose_target_mints_a_group_around_the_pair()
    {
        // The gesture's whole point: two loose tabs held together become
        // a group, which is state neither of them had before.
        var mgr = NewManager(3);
        var dragged = mgr.Tabs[2];
        var target = mgr.Tabs[0];
        var bystander = mgr.Tabs[1];
        var group = TabJoinDrop.Join(mgr, dragged, target);
        Assert.NotNull(group);
        Assert.Same(group, dragged.Group);
        Assert.Same(group, target.Group);
        Assert.Single(mgr.Groups);
        // The gather is the manager's, and it is what makes the join
        // visible: the pair ends up side by side, the bystander outside
        // the run. Membership alone would pass with the two still at
        // opposite ends of the strip.
        Assert.Equal(1, System.Math.Abs(mgr.IndexOf(dragged) - mgr.IndexOf(target)));
        Assert.Null(bystander.Group);
    }

    [Fact]
    public void Joining_a_grouped_target_uses_the_group_it_already_has()
    {
        var mgr = NewManager(3);
        var existing = mgr.CreateGroup(mgr.Tabs[0])!;
        mgr.JoinGroup(mgr.Tabs[1], existing);
        var dragged = mgr.Tabs[2];

        var group = TabJoinDrop.Join(mgr, dragged, mgr.Tabs[0]);
        Assert.Same(existing, group);
        Assert.Same(existing, dragged.Group);
        Assert.Single(mgr.Groups);
        Assert.Equal(3, mgr.MembersOf(existing).Count);
    }

    [Fact]
    public void A_join_into_a_collapsed_group_expands_it()
    {
        // Inherited from JoinGroup, which is the manager's own
        // auto-expanding join; the gesture never carries the bit itself.
        var mgr = NewManager(3);
        var existing = mgr.CreateGroup(mgr.Tabs[0])!;
        existing.IsCollapsed = true;
        Assert.NotNull(TabJoinDrop.Join(mgr, mgr.Tabs[2], mgr.Tabs[0]));
        Assert.False(existing.IsCollapsed);
    }

    [Fact]
    public void A_refused_pair_joins_nothing_and_registers_no_group()
    {
        var mgr = NewManager(2);
        mgr.SetPinned(mgr.Tabs[0], true);
        var pinned = mgr.Tabs[0];
        Assert.Null(TabJoinDrop.Join(mgr, mgr.Tabs[1], pinned));
        Assert.Null(pinned.Group);
        Assert.Null(mgr.Tabs[1].Group);
        Assert.Empty(mgr.Groups);
    }

    [Fact]
    public void A_pinned_dragged_row_refuses_before_a_group_is_minted()
    {
        // The refusal has to come BEFORE the mint, or a gesture that
        // joins nothing still leaves a one-member group behind on the
        // row it was aimed at -- state the user never asked for, from a
        // gesture that visibly did nothing.
        var mgr = NewManager(2);
        mgr.SetPinned(mgr.Tabs[0], true);
        Assert.Null(TabJoinDrop.Join(mgr, mgr.Tabs[0], mgr.Tabs[1]));
        Assert.Null(mgr.Tabs[0].Group);
        Assert.Null(mgr.Tabs[1].Group);
        Assert.Empty(mgr.Groups);
    }
}
