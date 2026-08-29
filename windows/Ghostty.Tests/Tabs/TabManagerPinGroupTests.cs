using System;
using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class TabManagerPinGroupTests
{
    private static TabManager NewManager()
        => new TabManager((_) => new FakePaneHost());

    private static TabManager NewManager(int extraTabs)
    {
        var mgr = NewManager();
        for (int i = 0; i < extraTabs; i++) mgr.NewTab();
        return mgr;
    }

    private static void Title(TabModel tab, string title) => tab.UserOverrideTitle = title;

    // --- PinCount / SetPinned ---

    [Fact]
    public void PinCount_is_derived_from_the_order()
    {
        var mgr = NewManager(2);
        Assert.Equal(0, mgr.PinCount);
        mgr.SetPinned(mgr.Tabs[2], true);
        Assert.Equal(1, mgr.PinCount);
        mgr.SetPinned(mgr.Tabs[1], true); // relocation moved the first pin to the front
        Assert.Equal(2, mgr.PinCount);
        mgr.SetPinned(mgr.Tabs[0], false);
        Assert.Equal(1, mgr.PinCount);
    }

    [Fact]
    public void SetPinned_true_relocates_to_end_of_prefix()
    {
        var mgr = NewManager(2); // [A, B, C]
        var c = mgr.Tabs[2];
        mgr.SetPinned(c, true);
        Assert.Same(c, mgr.Tabs[0]);
        Assert.Equal(1, mgr.PinCount);
    }

    [Fact]
    public void SetPinned_false_relocates_to_first_unpinned_slot()
    {
        var mgr = NewManager(2); // [A, B, C]
        mgr.SetPinned(mgr.Tabs[0], true);
        mgr.SetPinned(mgr.Tabs[1], true); // [A, B, C] all pinned
        var a = mgr.Tabs[0];
        (TabModel tab, int from, int to)? evt = null;
        mgr.TabMoved += (_, e) => evt = e;

        mgr.SetPinned(a, false);

        Assert.Same(a, mgr.Tabs[1]); // first slot after the remaining pin
        Assert.Equal((a, 0, 1), evt);
    }

    [Fact]
    public void SetPinned_lands_after_the_existing_prefix()
    {
        var mgr = NewManager(2); // [A, B, C]
        mgr.SetPinned(mgr.Tabs[0], true); // prefix = [A]
        var c = mgr.Tabs[2];
        (TabModel tab, int from, int to)? evt = null;
        mgr.TabMoved += (_, e) => evt = e;

        mgr.SetPinned(c, true);

        Assert.Same(c, mgr.Tabs[1]); // right after A, not the front
        Assert.Equal((c, 2, 1), evt);
    }

    [Fact]
    public void SetPinned_to_the_current_state_is_a_noop_without_events()
    {
        var mgr = NewManager(1);
        mgr.SetPinned(mgr.Tabs[0], true);
        int moved = 0;
        mgr.TabMoved += (_, _) => moved++;
        mgr.SetPinned(mgr.Tabs[0], true);
        mgr.SetPinned(mgr.Tabs[1], false);
        Assert.Equal(0, moved);
    }

    [Fact]
    public void SetPinned_in_a_one_tab_window_pins_without_moving()
    {
        var mgr = NewManager(0);
        var only = mgr.ActiveTab;
        mgr.SetPinned(only, true);
        Assert.True(only.IsPinned);
        Assert.Equal(1, mgr.PinCount);
        Assert.Same(only, mgr.Tabs[0]);
        mgr.SetPinned(only, false);
        Assert.False(only.IsPinned);
        Assert.Equal(0, mgr.PinCount);
    }

    [Fact]
    public void Pinning_a_grouped_tab_removes_it_from_its_group()
    {
        var mgr = NewManager(2); // [A, B, C]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1], mgr.Tabs[2] }, group);
        var b = mgr.Tabs[1];

        mgr.SetPinned(b, true);

        Assert.Null(b.Group);
        Assert.Same(group, mgr.Tabs[2].Group);
        Assert.Contains(group, mgr.Groups);
        Assert.Same(b, mgr.Tabs[0]);
    }

    [Fact]
    public void NewTab_appends_after_the_pinned_prefix()
    {
        var mgr = NewManager(0);
        mgr.SetPinned(mgr.Tabs[0], true);
        var fresh = mgr.NewTab();
        Assert.Same(fresh, mgr.Tabs[1]);
        Assert.False(fresh.IsPinned);
        Assert.Equal(1, mgr.PinCount);
    }

    // --- Move clamping at the pin boundary ---

    [Fact]
    public void Move_clamps_a_pinned_tab_inside_the_prefix()
    {
        var mgr = NewManager(2); // [A, B, C]
        mgr.SetPinned(mgr.Tabs[0], true);
        mgr.SetPinned(mgr.Tabs[1], true); // [A, B, C], prefix = A, B
        var a = mgr.Tabs[0];
        (TabModel tab, int from, int to)? evt = null;
        mgr.TabMoved += (_, e) => evt = e;

        mgr.Move(0, 2); // intent: into the unpinned zone

        Assert.True(a.IsPinned);
        Assert.Same(a, mgr.Tabs[1]); // clamped to the last prefix slot
        Assert.Equal((a, 0, 1), evt);
        Assert.Equal(2, mgr.PinCount);
    }

    [Fact]
    public void Move_clamps_an_unpinned_tab_out_of_the_prefix()
    {
        var mgr = NewManager(2); // [A, B, C]
        mgr.SetPinned(mgr.Tabs[0], true);
        var b = mgr.Tabs[1];
        int moved = 0;
        mgr.TabMoved += (_, _) => moved++;

        mgr.Move(1, 0); // intent: into the pinned zone

        Assert.False(b.IsPinned);
        Assert.Equal(0, moved);
        Assert.Same(b, mgr.Tabs[1]);
        Assert.Equal(1, mgr.PinCount);
    }

    // --- The drag layer's zone-crossing commit: SetPinned + Move, one call site ---
    //
    // A drag that carries a row across the pin boundary cannot commit with
    // Move alone (the clamps above are exactly why), so the caller pairs
    // SetPinned with Move. These pin the pairing's observable contract:
    // the drop lands at the crossing's slot, never at the zone boundary
    // SetPinned relocates to.

    [Fact]
    public void Crossing_up_pins_and_lands_at_the_crossing_slot()
    {
        var mgr = NewManager(3); // [A, B, C, D]
        NameTabs(mgr);
        mgr.SetPinned(mgr.Tabs[0], true);
        mgr.SetPinned(mgr.Tabs[1], true); // prefix = A, B
        var c = mgr.Tabs[2];

        // C dragged up to slot 1: pin first (relocates to the prefix end,
        // slot 2), then move into the slot the crossing asked for.
        mgr.SetPinned(c, true);
        mgr.Move(mgr.IndexOf(c), 1);

        Assert.True(c.IsPinned);
        Assert.Equal(3, mgr.PinCount);
        Assert.Equal(new[] { "A", "C", "B", "D" }, Titles(mgr));
    }

    [Fact]
    public void Crossing_down_unpins_and_lands_at_the_crossing_slot()
    {
        var mgr = NewManager(2); // [A, B, C]
        NameTabs(mgr);
        mgr.SetPinned(mgr.Tabs[0], true); // prefix = A
        var a = mgr.Tabs[0];

        // A dragged down to slot 2: unpin first (relocates to the first
        // unpinned slot, which is slot 1 here), then move to the slot.
        mgr.SetPinned(a, false);
        mgr.Move(mgr.IndexOf(a), 2);

        Assert.False(a.IsPinned);
        Assert.Equal(0, mgr.PinCount);
        Assert.Equal(new[] { "B", "C", "A" }, Titles(mgr));
    }

    [Fact]
    public void Unpinning_to_the_boundary_crossing_relocates_then_lands()
    {
        var mgr = NewManager(3); // [A, B, C, D]
        NameTabs(mgr);
        mgr.SetPinned(mgr.Tabs[0], true);
        mgr.SetPinned(mgr.Tabs[1], true); // prefix = A, B
        var a = mgr.Tabs[0];
        int moved = 0;
        mgr.TabMoved += (_, _) => moved++;

        // Crossing slot == PinCount is the first unpinned slot in the
        // pre-commit frame. The unpin relocates A to the boundary (slot 1
        // in the new frame), and the paired Move carries it the rest of
        // the way to the crossing's slot.
        mgr.SetPinned(a, false);
        mgr.Move(mgr.IndexOf(a), 2);

        Assert.False(a.IsPinned);
        Assert.Equal(1, mgr.PinCount);
        Assert.Equal(new[] { "B", "C", "A", "D" }, Titles(mgr));
        Assert.Equal(2, moved); // the relocation, then the placement
    }

    private static void NameTabs(TabManager mgr)
    {
        string[] names = ["A", "B", "C", "D", "E", "F", "G", "H"];
        for (int i = 0; i < mgr.Tabs.Count && i < names.Length; i++)
            Title(mgr.Tabs[i], names[i]);
    }

    private static string[] Titles(TabManager mgr)
    {
        var titles = new string[mgr.Tabs.Count];
        for (int i = 0; i < mgr.Tabs.Count; i++)
            titles[i] = mgr.Tabs[i].EffectiveTitle;
        return titles;
    }

    // --- Group contiguity ---

    [Fact]
    public void Move_pulling_a_member_out_midrun_repairs_contiguity()
    {
        var mgr = NewManager(3); // [A, B, C, D]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[0], mgr.Tabs[1], mgr.Tabs[2] }, group);
        var b = mgr.Tabs[1];
        (TabModel tab, int from, int to)? evt = null;
        mgr.TabMoved += (_, e) => evt = e;

        mgr.Move(1, 3); // pull B out past the run end

        // The op's own indices are what TabMoved reports; Normalize then
        // re-gathers the run around its first member.
        Assert.Equal((b, 1, 3), evt);
        AssertRunContiguous(mgr, group);
        Assert.Equal(4, mgr.Tabs.Count);
    }

    [Fact]
    public void GroupTabs_gathers_scattered_members_into_one_run()
    {
        var mgr = NewManager(3); // [A, B, C, D]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[0], mgr.Tabs[2] }, group);

        AssertRunContiguous(mgr, group);
        Assert.Contains(group, mgr.Groups);
    }

    [Fact]
    public void GroupTabs_skips_pinned_members()
    {
        var mgr = NewManager(2); // [A, B, C]
        mgr.SetPinned(mgr.Tabs[0], true);
        var group = new TabGroup();
        var a = mgr.Tabs[0];

        mgr.GroupTabs(new[] { a, mgr.Tabs[1] }, group);

        Assert.Null(a.Group);
        Assert.Same(group, mgr.Tabs[1].Group);
        Assert.True(a.IsPinned);
    }

    [Fact]
    public void RunOf_returns_the_group_run_or_a_singleton()
    {
        var mgr = NewManager(3); // [A, B, C, D]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[0], mgr.Tabs[2] }, group);
        var gathered = mgr.RunOf(mgr.Tabs[0]);

        Assert.Equal(2, gathered.Count);
        Assert.Contains(mgr.Tabs[0], gathered);
        Assert.Contains(mgr.Tabs[1], gathered);

        var lone = mgr.RunOf(mgr.Tabs[3]);
        Assert.Single(lone);
        Assert.Same(mgr.Tabs[3], lone[0]);
    }

    // --- Empty groups dissolve ---

    [Fact]
    public void Ungrouping_the_last_member_dissolves_the_group()
    {
        var mgr = NewManager(1);
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[0] }, group);
        mgr.CollapseGroup(group, true);

        mgr.Ungroup(mgr.Tabs[0]);

        Assert.Null(mgr.Tabs[0].Group);
        Assert.DoesNotContain(group, mgr.Groups);
        Assert.Empty(mgr.Groups);
    }

    [Fact]
    public void Closing_group_members_dissolves_the_group()
    {
        var mgr = NewManager(2); // [A, B, C]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[0], mgr.Tabs[1] }, group);
        mgr.CollapseGroup(group, true);

        mgr.CloseTab(mgr.Tabs[1]);
        Assert.Contains(group, mgr.Groups); // still one member left
        mgr.CloseTab(mgr.Tabs[0]);

        Assert.DoesNotContain(group, mgr.Groups); // collapse state died with it
    }

    [Fact]
    public void GroupTabs_with_no_eligible_member_registers_nothing()
    {
        var mgr = NewManager(0);
        var group = new TabGroup();
        mgr.SetPinned(mgr.Tabs[0], true);

        mgr.GroupTabs(new[] { mgr.Tabs[0] }, group);

        Assert.Empty(mgr.Groups);
        Assert.Null(mgr.Tabs[0].Group);
    }

    // --- Group lifecycle ops ---

    [Fact]
    public void CreateGroup_makes_the_tab_the_sole_member_in_place()
    {
        var mgr = NewManager(2); // [A, B, C]
        var order = new List<TabModel>(mgr.Tabs);
        int moved = 0;
        mgr.TabMoved += (_, _) => moved++;

        var group = mgr.CreateGroup(mgr.Tabs[1]);

        Assert.NotNull(group);
        Assert.Same(group, mgr.Tabs[1].Group);
        Assert.Contains(group!, mgr.Groups);
        Assert.Equal(order, mgr.Tabs); // the seed keeps its position
        Assert.Equal(0, moved);
        Assert.Equal("New group", group!.Title);
        Assert.Equal(TabColor.Blue, group.Color);
        Assert.False(group.IsCollapsed);
    }

    [Fact]
    public void CreateGroup_refuses_a_pinned_tab_and_registers_nothing()
    {
        var mgr = NewManager(1);
        mgr.SetPinned(mgr.Tabs[0], true);

        Assert.Null(mgr.CreateGroup(mgr.Tabs[0]));
        Assert.Null(mgr.Tabs[0].Group);
        Assert.Empty(mgr.Groups);
    }

    [Fact]
    public void CreateGroup_refuses_a_tab_the_manager_does_not_own()
    {
        var mgr = NewManager(0);
        var stranger = new TabModel(new FakePaneHost());

        Assert.Null(mgr.CreateGroup(stranger));
        Assert.Empty(mgr.Groups);
    }

    [Fact]
    public void JoinGroup_moves_the_tab_into_the_run()
    {
        var mgr = NewManager(3); // [A, B, C, D]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1] }, group);
        var a = mgr.Tabs[0];
        var b = mgr.Tabs[1];
        var c = mgr.Tabs[2];
        var d = mgr.Tabs[3];

        mgr.JoinGroup(d, group);

        // The gather re-forms the run at the first member's position, in
        // the members' relative order -- the same grammar GroupTabs uses.
        Assert.Equal(new[] { a, b, d, c }, mgr.Tabs.ToArray());
        AssertRunContiguous(mgr, group);
    }

    [Fact]
    public void JoinGroup_auto_expands_a_collapsed_group()
    {
        var mgr = NewManager(2); // [A, B, C]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[0] }, group);
        mgr.CollapseGroup(group, true);

        mgr.JoinGroup(mgr.Tabs[2], group);

        Assert.False(group.IsCollapsed); // the join is visible (2.9 row 17)
        AssertRunContiguous(mgr, group);
    }

    [Fact]
    public void JoinGroup_keeps_the_collapse_bit_when_nothing_joined()
    {
        var mgr = NewManager(2); // [A, B, C]
        mgr.SetPinned(mgr.Tabs[0], true);
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1] }, group);
        mgr.CollapseGroup(group, true);

        // A refused join (pinned) must not expand...
        mgr.JoinGroup(mgr.Tabs[0], group);
        Assert.True(group.IsCollapsed);
        Assert.Null(mgr.Tabs[0].Group);

        // ...and neither may re-joining a member.
        mgr.JoinGroup(mgr.Tabs[1], group);
        Assert.True(group.IsCollapsed);
    }

    [Fact]
    public void DissolveGroup_ungroups_every_member_in_place()
    {
        var mgr = NewManager(3); // [A, B, C, D]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1], mgr.Tabs[2] }, group);
        mgr.CollapseGroup(group, true);
        var order = new List<TabModel>(mgr.Tabs);

        mgr.DissolveGroup(group);

        Assert.Equal(order, mgr.Tabs);
        Assert.All(mgr.Tabs, t => Assert.Null(t.Group));
        Assert.Empty(mgr.Groups);
    }

    [Fact]
    public void DissolveGroup_of_an_unregistered_group_is_a_noop()
    {
        var mgr = NewManager(1);
        var order = new List<TabModel>(mgr.Tabs);
        int moved = 0;
        mgr.TabMoved += (_, _) => moved++;

        mgr.DissolveGroup(new TabGroup());

        Assert.Equal(order, mgr.Tabs);
        Assert.Equal(0, moved);
        Assert.Empty(mgr.Groups);
    }

    [Fact]
    public void HoldsActiveTab_flips_with_activation()
    {
        var mgr = NewManager(2); // [A, B, C]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1], mgr.Tabs[2] }, group);
        mgr.Activate(mgr.Tabs[0]); // start outside the group

        Assert.False(mgr.HoldsActiveTab(group));
        mgr.CollapseGroup(group, true);
        mgr.Activate(mgr.Tabs[1]);

        // The Edge-135 predicate the strips project from: a collapsed
        // group holding the active tab is never rendered fully collapsed.
        Assert.True(mgr.HoldsActiveTab(group));

        mgr.Activate(mgr.Tabs[0]);
        Assert.False(mgr.HoldsActiveTab(group));
    }

    [Fact]
    public void GroupHoldsEveryTab_gates_the_close_group_command()
    {
        var mgr = NewManager(1);
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[0], mgr.Tabs[1] }, group);

        Assert.True(mgr.GroupHoldsEveryTab(group)); // nothing would survive

        mgr.NewTab();
        Assert.False(mgr.GroupHoldsEveryTab(group));
    }

    // --- Collapse ---

    [Fact]
    public void CollapseGroup_sets_the_bit_without_touching_the_list()
    {
        var mgr = NewManager(2);
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[0], mgr.Tabs[1] }, group);
        var order = new List<TabModel>(mgr.Tabs);
        int moved = 0;
        mgr.TabMoved += (_, _) => moved++;

        mgr.CollapseGroup(group, true);

        Assert.True(group.IsCollapsed);
        Assert.Equal(0, moved);
        Assert.Equal(order, mgr.Tabs);
        mgr.CollapseGroup(group, false);
        Assert.False(group.IsCollapsed);
    }

    [Fact]
    public void CollapseGroup_never_changes_the_active_tab()
    {
        var mgr = NewManager(2);
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1], mgr.Tabs[2] }, group);
        mgr.Activate(mgr.Tabs[2]);
        var active = mgr.ActiveTab;
        int changes = 0;
        mgr.ActiveTabChanged += (_, _) => changes++;

        mgr.CollapseGroup(group, true);
        mgr.CollapseGroup(group, false);

        Assert.Same(active, mgr.ActiveTab);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Activating_a_member_of_a_collapsed_group_does_not_expand_it()
    {
        var mgr = NewManager(2);
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1], mgr.Tabs[2] }, group);
        mgr.CollapseGroup(group, true);

        // No accordion: activation swaps which member is visible; the
        // user's collapse bit stays as they set it.
        mgr.Activate(mgr.Tabs[2]);
        Assert.Same(mgr.Tabs[2], mgr.ActiveTab);
        Assert.True(group.IsCollapsed);
    }

    [Fact]
    public void Normalize_repairs_a_collapsed_run_and_keeps_its_members()
    {
        var mgr = NewManager(3); // [A, B, C, D]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1], mgr.Tabs[2] }, group);
        mgr.CollapseGroup(group, true);
        var a = mgr.Tabs[0];
        var b = mgr.Tabs[1];
        var c = mgr.Tabs[2];
        var d = mgr.Tabs[3];

        mgr.Move(1, 3); // pull B out past the run end

        // Collapse is a bit, never a reordering: the repair re-gathers
        // the run like any other, ejects no member, and leaves the bit.
        AssertRunContiguous(mgr, group);
        Assert.Equal(new[] { a, c, b, d }, mgr.Tabs.ToArray());
        Assert.True(group.IsCollapsed);
    }

    [Fact]
    public void Next_and_Prev_walk_linearly_through_a_collapsed_group()
    {
        var mgr = NewManager(2); // [A, B, C]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1], mgr.Tabs[2] }, group);
        mgr.CollapseGroup(group, true);
        mgr.Activate(mgr.Tabs[0]);

        // Navigation is linear in list order; collapse never makes the
        // cycle skip members (the Chrome complaint Next/Prev exists to
        // avoid).
        mgr.Next();
        Assert.Same(mgr.Tabs[1], mgr.ActiveTab);
        mgr.Next();
        Assert.Same(mgr.Tabs[2], mgr.ActiveTab);
        mgr.Next();
        Assert.Same(mgr.Tabs[0], mgr.ActiveTab);
        mgr.Prev();
        Assert.Same(mgr.Tabs[2], mgr.ActiveTab);
    }

    // --- Group change notification ---

    /// <summary>
    /// The group is the change carrier for its own chrome: the registry
    /// has no collection event and membership rides TabModel.Group, so a
    /// strip learning a header's label, swatch, or collapse bit changed
    /// has nothing else to listen to. Same-value silence is half the
    /// contract: a raising no-op write churns the strip for nothing.
    /// </summary>
    [Fact]
    public void Group_property_changes_raise_and_same_value_writes_do_not()
    {
        var group = new TabGroup();
        var raised = new List<string>();
        group.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        group.Title = "build";
        group.Color = TabColor.Red;
        group.IsCollapsed = true;
        Assert.Equal(
            new[] { nameof(TabGroup.Title), nameof(TabGroup.Color), nameof(TabGroup.IsCollapsed) },
            raised);

        group.Title = "build";
        group.Color = TabColor.Red;
        group.IsCollapsed = true;
        Assert.Equal(3, raised.Count);

        // Membership rides TabModel.Group, never the group.
        new TabModel(new FakePaneHost()).Group = group;
        Assert.Equal(3, raised.Count);
    }

    // --- MoveGroup / MoveRun rotations ---

    [Fact]
    public void MoveGroup_moves_the_run_as_a_unit_preserving_member_order()
    {
        var mgr = NewManager(4); // [A, B, C, D, E]
        mgr.SetPinned(mgr.Tabs[0], true); // prefix = [A]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1], mgr.Tabs[2], mgr.Tabs[3] }, group);
        var a = mgr.Tabs[0];
        var e = mgr.Tabs[4];
        var b = mgr.Tabs[1];
        var c = mgr.Tabs[2];
        var d = mgr.Tabs[3];

        mgr.MoveGroup(group, 2); // one past the run start

        Assert.Equal(new[] { a, e, b, c, d }, mgr.Tabs.ToArray());
        AssertRunContiguous(mgr, group);
        Assert.Equal(2, mgr.IndexOf(b));
    }

    [Fact]
    public void MoveGroup_clamps_out_of_the_pinned_prefix()
    {
        var mgr = NewManager(3); // [A, B, C, D]
        mgr.SetPinned(mgr.Tabs[0], true);
        mgr.SetPinned(mgr.Tabs[1], true);
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[2], mgr.Tabs[3] }, group);
        var order = new List<TabModel>(mgr.Tabs);

        mgr.MoveGroup(group, 0); // intent: the run at the very front

        Assert.Equal(order, mgr.Tabs); // already flush against the prefix
    }

    [Fact]
    public void MoveRun_rotates_the_run_around_the_grabbed_member()
    {
        var mgr = NewManager(4); // [A, B, C, D, E]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[0], mgr.Tabs[1], mgr.Tabs[2], mgr.Tabs[3], mgr.Tabs[4] }, group);
        var a = mgr.Tabs[0];
        var b = mgr.Tabs[1];
        var c = mgr.Tabs[2];
        var d = mgr.Tabs[3];
        var e = mgr.Tabs[4];

        mgr.MoveRun(2, 4); // grab C, drag it to the run's end

        Assert.Equal(new[] { d, e, a, b, c }, mgr.Tabs.ToArray());
        AssertRunContiguous(mgr, group);
    }

    [Fact]
    public void MoveRun_clamps_into_the_run_span()
    {
        var mgr = NewManager(3); // [A, B, C, D]
        var group = new TabGroup();
        mgr.GroupTabs(new[] { mgr.Tabs[1], mgr.Tabs[2] }, group); // run [B, C]
        var a = mgr.Tabs[0];
        var b = mgr.Tabs[1];
        var c = mgr.Tabs[2];
        var d = mgr.Tabs[3];

        mgr.MoveRun(1, 3); // intent: B past the run end

        Assert.Equal(new[] { a, c, b, d }, mgr.Tabs.ToArray()); // stopped at the run's last slot
        AssertRunContiguous(mgr, group);
    }

    [Fact]
    public void MoveRun_on_an_ungrouped_tab_is_a_plain_move()
    {
        var mgr = NewManager(2); // [A, B, C]
        var b = mgr.Tabs[1];
        (TabModel tab, int from, int to)? evt = null;
        mgr.TabMoved += (_, e) => evt = e;

        mgr.MoveRun(1, 2); // to the last slot, like Move

        Assert.Same(b, mgr.Tabs[2]);
        Assert.Equal((b, 1, 2), evt);
    }

    // --- SortPinned ---

    [Fact]
    public void SortPinned_orders_the_prefix_by_effective_title()
    {
        var mgr = NewManager(3); // [A, B, C, D]
        Title(mgr.Tabs[0], "cocoa");
        Title(mgr.Tabs[1], "alpha");
        Title(mgr.Tabs[2], "bravo");
        Title(mgr.Tabs[3], "zulu");
        mgr.SetPinned(mgr.Tabs[0], true);
        mgr.SetPinned(mgr.Tabs[1], true);
        mgr.SetPinned(mgr.Tabs[2], true);
        var alpha = mgr.Tabs[1];
        var bravo = mgr.Tabs[2];
        var cocoa = mgr.Tabs[0];
        var zulu = mgr.Tabs[3];

        mgr.SortPinned();

        Assert.Equal(new[] { alpha, bravo, cocoa, zulu }, mgr.Tabs.ToArray());
        Assert.Equal(3, mgr.PinCount);
    }

    [Fact]
    public void SortPinned_is_stable_for_equal_titles()
    {
        var mgr = NewManager(2); // [A, B, C]
        Title(mgr.Tabs[0], "same");
        Title(mgr.Tabs[1], "same");
        Title(mgr.Tabs[2], "ahead");
        mgr.SetPinned(mgr.Tabs[0], true);
        mgr.SetPinned(mgr.Tabs[1], true);
        mgr.SetPinned(mgr.Tabs[2], true);
        var ahead = mgr.Tabs[2];
        var first = mgr.Tabs[0];
        var second = mgr.Tabs[1];

        mgr.SortPinned();

        // Only "ahead" moves up; the two equal titles keep their order.
        Assert.Equal(new[] { ahead, first, second }, mgr.Tabs.ToArray());
    }

    [Fact]
    public void SortPinned_is_idempotent()
    {
        var mgr = NewManager(2);
        Title(mgr.Tabs[0], "b");
        Title(mgr.Tabs[1], "a");
        mgr.SetPinned(mgr.Tabs[0], true);
        mgr.SetPinned(mgr.Tabs[1], true);

        mgr.SortPinned();
        var order = new List<TabModel>(mgr.Tabs);
        int moved = 0;
        mgr.TabMoved += (_, _) => moved++;

        mgr.SortPinned();

        Assert.Equal(0, moved);
        Assert.Equal(order, mgr.Tabs);
    }

    // --- MRU and activation are orthogonal to strip order ---

    [Fact]
    public void Pin_group_and_reorder_mutators_never_touch_mru_order()
    {
        var mgr = NewManager(3);
        mgr.Activate(mgr.Tabs[0]); // MRU: [0, 3, 2, 1]
        var expected = mgr.MruOrder.ToArray();
        var group = new TabGroup();

        mgr.SetPinned(mgr.Tabs[1], true);
        mgr.Move(0, mgr.Tabs.Count - 1);
        mgr.GroupTabs(new[] { mgr.Tabs[2], mgr.Tabs[3] }, group);
        mgr.CollapseGroup(group, true);
        mgr.MoveGroup(group, 0);
        mgr.MoveRun(1, 3);
        mgr.SortPinned();
        mgr.SetPinned(mgr.Tabs[0], false);
        mgr.Ungroup(mgr.Tabs[2]);

        Assert.Equal(expected, mgr.MruOrder.ToArray());
    }

    [Fact]
    public void Pin_group_and_reorder_mutators_never_change_the_active_tab()
    {
        var mgr = NewManager(3);
        mgr.Activate(mgr.Tabs[2]);
        var active = mgr.ActiveTab;
        int changes = 0;
        mgr.ActiveTabChanged += (_, _) => changes++;
        var group = new TabGroup();

        mgr.SetPinned(mgr.Tabs[1], true);
        mgr.Move(0, mgr.Tabs.Count - 1);
        mgr.GroupTabs(new[] { mgr.Tabs[0], mgr.Tabs[3] }, group);
        mgr.MoveGroup(group, 0);
        mgr.MoveRun(2, 3);
        mgr.SortPinned();
        mgr.Ungroup(mgr.Tabs[0]);

        Assert.Same(active, mgr.ActiveTab);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void AdoptTab_folds_a_pinned_adoptee_into_the_prefix()
    {
        var src = NewManager(1); // [s0, s1]
        var dest = NewManager(2); // [a, b, c]
        dest.SetPinned(dest.Tabs[1], true); // [b*, a, c]
        src.SetPinned(src.Tabs[1], true); // [s1*, s0]

        // A detached tab keeps its pin; the adopter honors it instead of
        // letting the tab sink into the unpinned zone.
        var adoptee = src.DetachTab(src.Tabs[0]);

        dest.AdoptTab(adoptee);

        Assert.Equal(2, dest.PinCount);
        // Adoption appends, so Normalize lifts the adoptee to the END of
        // the existing prefix rather than the front of it.
        Assert.Same(adoptee, dest.Tabs[1]);
        Assert.True(adoptee.IsPinned);
        Assert.Null(adoptee.Group);
        Assert.False(dest.Tabs[2].IsPinned);
    }

    [Fact]
    public void An_adopted_tab_arrives_ungrouped_and_the_group_stays_home()
    {
        var src = NewManager(1); // [s0, s1]
        var dest = NewManager(0); // [a]
        var group = new TabGroup();
        src.GroupTabs(new[] { src.Tabs[0], src.Tabs[1] }, group);

        var adoptee = src.DetachTab(src.Tabs[1]);
        dest.AdoptTab(adoptee);

        // Membership does not travel: the group belongs to the source
        // window's registry, so the adoptee arrives ungrouped while the
        // source run survives with its remaining member.
        Assert.Null(adoptee.Group);
        Assert.DoesNotContain(adoptee, src.Tabs);
        AssertRunContiguous(src, group);
        Assert.Contains(group, src.Groups);
        Assert.Empty(dest.Groups);
    }

    // --- The invariants hold across arbitrary op sequences ---

    [Fact]
    public void Random_op_sequences_keep_the_invariants()
    {
        var mgr = NewManager(2);
        var orphans = new List<TabModel>();
        var random = new Random(20260828);

        for (int step = 0; step < 300; step++)
        {
            switch (random.Next(15))
            {
                case 0:
                    mgr.SetPinned(mgr.Tabs[random.Next(mgr.Tabs.Count)], random.Next(2) == 0);
                    break;
                case 1:
                    mgr.Move(random.Next(mgr.Tabs.Count), random.Next(mgr.Tabs.Count));
                    break;
                case 2:
                    mgr.NewTab();
                    break;
                case 3:
                    if (mgr.Tabs.Count > 1)
                        mgr.CloseTab(mgr.Tabs[random.Next(mgr.Tabs.Count)]);
                    break;
                case 4:
                    var members = new List<TabModel>();
                    for (int n = random.Next(1, 4); n > 0; n--)
                        members.Add(mgr.Tabs[random.Next(mgr.Tabs.Count)]);
                    mgr.GroupTabs(members, new TabGroup());
                    break;
                case 5:
                    if (mgr.Groups.Count > 0)
                        mgr.Ungroup(mgr.Tabs[random.Next(mgr.Tabs.Count)]);
                    break;
                case 6:
                    if (mgr.Groups.Count > 0)
                        mgr.MoveGroup(mgr.Groups[random.Next(mgr.Groups.Count)], random.Next(mgr.Tabs.Count + 1));
                    break;
                case 7:
                    mgr.MoveRun(random.Next(mgr.Tabs.Count), random.Next(mgr.Tabs.Count + 1));
                    break;
                case 8:
                    mgr.SortPinned();
                    break;
                case 9:
                    // The tab leaves this window and may come back later.
                    if (mgr.Tabs.Count > 1)
                        orphans.Add(mgr.DetachTab(mgr.Tabs[random.Next(mgr.Tabs.Count)]));
                    break;
                case 10:
                    if (orphans.Count > 0)
                    {
                        int pick = random.Next(orphans.Count);
                        var adoptee = orphans[pick];
                        orphans.RemoveAt(pick);
                        mgr.AdoptTab(adoptee);
                    }
                    break;
                case 11:
                    mgr.CreateGroup(mgr.Tabs[random.Next(mgr.Tabs.Count)]);
                    break;
                case 12:
                    if (mgr.Groups.Count > 0)
                        mgr.JoinGroup(mgr.Tabs[random.Next(mgr.Tabs.Count)],
                            mgr.Groups[random.Next(mgr.Groups.Count)]);
                    break;
                case 13:
                    if (mgr.Groups.Count > 0)
                        mgr.DissolveGroup(mgr.Groups[random.Next(mgr.Groups.Count)]);
                    break;
                case 14:
                    if (mgr.Groups.Count > 0)
                        mgr.CollapseGroup(
                            mgr.Groups[random.Next(mgr.Groups.Count)],
                            random.Next(2) == 0);
                    break;
            }

            AssertInvariantsHold(mgr);
        }
    }

    private static void AssertInvariantsHold(TabManager mgr)
    {
        // Invariant 1: the pinned tabs are exactly the prefix.
        for (int i = 0; i < mgr.Tabs.Count; i++)
            Assert.Equal(i < mgr.PinCount, mgr.Tabs[i].IsPinned);

        // Invariant 2: every registered group's members form one
        // contiguous run. Invariant 3 falls out of the membership check:
        // a group with no members cannot appear here, and no tab points
        // at an unregistered group.
        foreach (var group in mgr.Groups)
        {
            var positions = new List<int>();
            for (int i = 0; i < mgr.Tabs.Count; i++)
                if (ReferenceEquals(mgr.Tabs[i].Group, group))
                    positions.Add(i);
            Assert.NotEmpty(positions);
            Assert.Equal(positions[^1] - positions[0] + 1, positions.Count);
        }
        foreach (var tab in mgr.Tabs)
        {
            if (tab.Group is not null)
                Assert.Contains(tab.Group, mgr.Groups);
            // Groups never pin: unreachable through the manager (SetPinned
            // ungroups, GroupTabs skips pinned), so this is checker
            // defense-in-depth for the fuzz.
            Assert.False(tab.IsPinned && tab.Group is not null);
        }
    }

    private static void AssertRunContiguous(TabManager mgr, TabGroup group)
    {
        var positions = new List<int>();
        for (int i = 0; i < mgr.Tabs.Count; i++)
            if (ReferenceEquals(mgr.Tabs[i].Group, group))
                positions.Add(i);
        Assert.NotEmpty(positions);
        Assert.Equal(positions[^1] - positions[0] + 1, positions.Count);
    }
}
