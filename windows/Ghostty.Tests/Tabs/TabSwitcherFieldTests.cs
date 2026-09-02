using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The switcher's cell plan: which cells carry a group field, and which of
/// them carry its head and its tail. Driven against a real
/// <see cref="TabManager"/> through <see cref="TabStripProjection"/>, the
/// same two hops the popup makes, so the rows under test are the rows the
/// strips render rather than a hand-built list that cannot go stale.
/// </summary>
public class TabSwitcherFieldTests
{
    private static TabManager NewManager(int tabs)
    {
        var mgr = new TabManager((_) => new FakePaneHost());
        for (int i = 1; i < tabs; i++) mgr.NewTab();
        return mgr;
    }

    private static IReadOnlyList<SwitcherCell> Plan(TabManager mgr)
        => TabSwitcherField.Plan(TabStripProjection.HorizontalRows(mgr));

    private static TabGroup GroupOf(TabManager mgr, params int[] indices)
    {
        var group = mgr.CreateGroup(mgr.Tabs[indices[0]])!;
        Assert.NotNull(group);
        for (int i = 1; i < indices.Length; i++)
            mgr.JoinGroup(mgr.Tabs[indices[i]], group);
        return group;
    }

    [Fact]
    public void An_ungrouped_card_carries_no_field_at_all()
    {
        var mgr = NewManager(3);

        var plan = Plan(mgr);

        Assert.Equal(3, plan.Count);
        Assert.All(plan, cell =>
        {
            Assert.Null(cell.Group);
            Assert.False(cell.IsHead);
            Assert.False(cell.IsTail);
        });
    }

    [Fact]
    public void A_run_is_one_field_headed_at_its_first_cell_and_tailed_at_its_last()
    {
        var mgr = NewManager(5);
        var group = GroupOf(mgr, 1, 2, 3);

        var plan = Plan(mgr);
        var members = plan.Where(c => c.Group is not null).ToList();

        Assert.Equal(3, members.Count);
        Assert.All(members, cell => Assert.Same(group, cell.Group));
        // Exactly one of each end: two heads would paint two fields over
        // one run, and the header would be drawn twice.
        Assert.Single(members.Where(c => c.IsHead));
        Assert.Single(members.Where(c => c.IsTail));
        Assert.True(members[0].IsHead);
        Assert.False(members[0].IsTail);
        Assert.False(members[1].IsHead);
        Assert.False(members[1].IsTail);
        Assert.True(members[^1].IsTail);
        Assert.False(members[^1].IsHead);

        // The cells outside the run stay ungrouped: a field that leaked one
        // cell either way would claim a tab the strip does not show as
        // grouped.
        Assert.All(plan.Where(c => c.Group is null), cell =>
        {
            Assert.False(cell.IsHead);
            Assert.False(cell.IsTail);
        });
    }

    [Fact]
    public void A_lone_member_is_both_ends_of_its_own_field()
    {
        var mgr = NewManager(3);
        var group = GroupOf(mgr, 1);

        var cell = Plan(mgr).Single(c => c.Group is not null);

        Assert.Same(group, cell.Group);
        Assert.True(cell.IsHead);
        Assert.True(cell.IsTail);
    }

    [Fact]
    public void A_collapsed_runs_chip_is_a_field_of_one_cell_and_carries_no_tab()
    {
        var mgr = NewManager(5);
        var group = GroupOf(mgr, 2, 3);
        // Activate outside the run so the Edge-135 rule does not keep a
        // member visible: this is the shape where the chip stands alone.
        mgr.Activate(mgr.Tabs[0]);
        mgr.CollapseGroup(group, true);

        var plan = Plan(mgr);
        var chip = plan.Single(c => c.Tab is null);

        Assert.Same(group, chip.Group);
        Assert.True(chip.IsHead);
        Assert.True(chip.IsTail);
        // The hidden members contribute no cells at all -- the projection
        // suppressed them, and a plan that invented cells for them would be
        // showing the run twice.
        Assert.Equal(4, plan.Count);
    }

    [Fact]
    public void The_visible_member_of_a_collapsed_run_still_names_its_group()
    {
        var mgr = NewManager(5);
        var group = GroupOf(mgr, 2, 3);
        mgr.Activate(mgr.Tabs[2]);
        mgr.CollapseGroup(group, true);

        var plan = Plan(mgr);

        // No chip: the walk already projects the active member, and a chip
        // beside it would draw the run twice.
        Assert.DoesNotContain(plan, c => c.Tab is null);
        var member = plan.Single(c => c.Group is not null);
        Assert.Same(mgr.Tabs[2], member.Tab);
        // The fact the popup used to withhold: the tab you are about to
        // land on belongs to this group.
        Assert.True(member.IsHead);
        Assert.True(member.IsTail);
    }

    [Fact]
    public void Two_runs_of_different_groups_never_merge_into_one_field()
    {
        var mgr = NewManager(5);
        var first = GroupOf(mgr, 0, 1);
        var second = GroupOf(mgr, 2, 3);

        var plan = Plan(mgr);
        var firstCells = plan.Where(c => ReferenceEquals(c.Group, first)).ToList();
        var secondCells = plan.Where(c => ReferenceEquals(c.Group, second)).ToList();

        Assert.Equal(2, firstCells.Count);
        Assert.Equal(2, secondCells.Count);
        // Adjacent fields: the first run's tail must close before the
        // second run's head opens, or the two wash into one band.
        Assert.True(firstCells[^1].IsTail);
        Assert.True(secondCells[0].IsHead);
    }

    [Fact]
    public void A_field_is_gathered_by_adjacency_so_a_stranger_splits_a_run()
    {
        // Contiguity is a manager invariant, so this shape is reached by
        // handing the plan rows directly -- which is the point: if the
        // invariant ever broke, the plan must under-claim (two fields of
        // one colour) rather than paint a field across the stranger.
        var mgr = NewManager(3);
        var group = mgr.CreateGroup(mgr.Tabs[0])!;
        mgr.JoinGroup(mgr.Tabs[1], group);
        var stranger = mgr.Tabs[2];

        var rows = new List<TabStripProjection.HorizontalRow>
        {
            new TabStripProjection.HorizontalRow.Item(mgr.Tabs[0]),
            new TabStripProjection.HorizontalRow.Item(stranger),
            new TabStripProjection.HorizontalRow.Item(mgr.Tabs[1]),
        };

        var plan = TabSwitcherField.Plan(rows);

        Assert.True(plan[0].IsHead);
        Assert.True(plan[0].IsTail);
        Assert.Null(plan[1].Group);
        Assert.True(plan[2].IsHead);
        Assert.True(plan[2].IsTail);
    }

    [Fact]
    public void A_chip_never_joins_a_neighbouring_field_of_its_own_group()
    {
        // The chip stands for the hidden members; a field spanning it and a
        // visible member of the same group would draw the run twice.
        var mgr = NewManager(3);
        var group = mgr.CreateGroup(mgr.Tabs[0])!;
        mgr.JoinGroup(mgr.Tabs[1], group);

        var rows = new List<TabStripProjection.HorizontalRow>
        {
            new TabStripProjection.HorizontalRow.Chip(group),
            new TabStripProjection.HorizontalRow.Item(mgr.Tabs[0]),
        };

        var plan = TabSwitcherField.Plan(rows);

        Assert.True(plan[0].IsHead);
        Assert.True(plan[0].IsTail);
        Assert.True(plan[1].IsHead);
        Assert.True(plan[1].IsTail);
    }

    [Fact]
    public void The_plan_refuses_a_null_row_list_rather_than_rendering_an_empty_card()
        => Assert.Throws<ArgumentNullException>(() => TabSwitcherField.Plan(null!));

    [Fact]
    public void Motion_off_collapses_every_switcher_transition_to_a_cut()
    {
        Assert.Equal(TimeSpan.Zero, TabSwitcherShape.HighlightDuration(motionOn: false));
        Assert.Equal(TimeSpan.Zero, TabSwitcherShape.EnterDuration(motionOn: false));
        Assert.True(TabSwitcherShape.HighlightDuration(motionOn: true) > TimeSpan.Zero);
        Assert.True(TabSwitcherShape.EnterDuration(motionOn: true) > TimeSpan.Zero);
    }

    [Fact]
    public void An_idle_tile_is_dimmed_and_the_active_one_is_lifted()
    {
        // The two cues the ring alone could not carry. Pinned as an
        // ordering, not as literals: what matters is that idle is dimmer
        // than active and active is larger than idle, and a swap of the two
        // constants is exactly the edit that would still "look animated".
        Assert.True(TabSwitcherShape.IdleTileOpacity < 1);
        Assert.True(TabSwitcherShape.ActiveTileScale > 1);
    }
}
