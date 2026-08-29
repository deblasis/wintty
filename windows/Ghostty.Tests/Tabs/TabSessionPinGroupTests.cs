using System;
using System.Collections.Generic;
using Ghostty.Core.Session;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// Session surface for the pin/group fields of section 6 of the
/// tab-reorder spec. The fields survive the source-generated round trip,
/// the capture side fills them from a live manager, and the restore side
/// -- RestoreGroup plus the pin-then-gather seeding order -- reproduces
/// saved pins, groups, ids, and collapse bits, repairing corrupt saved
/// state instead of crashing. The app-side orchestrator
/// (SessionRestorer/MainWindow) is pinned by the wiring guard; the Core
/// ops it drives are tested here, where managers can run.
/// </summary>
public class TabSessionPinGroupTests
{
    private static TabSession Tab(bool pinned = false, Guid? groupId = null)
        => new TabSession { Tree = new LeafDto { ProfileId = "pwsh" }, IsPinned = pinned, GroupId = groupId };

    private static TabManager NewManager(int extraTabs)
    {
        var mgr = new TabManager((_) => new FakePaneHost());
        for (int i = 0; i < extraTabs; i++) mgr.NewTab();
        return mgr;
    }

    // The capture call the way MainWindow makes it, per live tab.
    private static TabSession Capture(TabModel tab)
        => SessionCapture.CaptureTab(
            tab.PaneHost.RootNode,
            tab.PaneHost.ActiveLeaf,
            tab.PaneHost.ZoomedLeaf,
            tab.ProfileId,
            tab.UserOverrideTitle,
            tab.IsPinned,
            tab.Group?.Id);

    // The restore the way SessionRestorer drives it: flags first (the
    // manager's Normalize folds pins into the prefix as tabs land, in
    // saved order), then groups by saved id once every member is in.
    private static TabManager Restore(SessionState state)
    {
        var window = Assert.Single(state.Windows);
        var pairs = new List<(TabSession Source, TabModel Tab)>();
        TabModel? seed = null;
        var built = new List<TabModel>();
        foreach (var dto in window.Tabs)
        {
            var tab = new TabModel(new FakePaneHost())
            {
                IsPinned = dto.IsPinned,
                ProfileId = dto.ProfileId, // as BuildTab carries it
            };
            pairs.Add((dto, tab));
            if (seed is null) seed = tab;
            else built.Add(tab);
        }
        var mgr = new TabManager((_) => new FakePaneHost(), seed: seed);
        foreach (var tab in built) mgr.AdoptTab(tab);
        foreach (var groupDto in window.Groups)
        {
            var members = new List<TabModel>();
            foreach (var (source, tab) in pairs)
                if (source.GroupId == groupDto.Id)
                    members.Add(tab);
            mgr.RestoreGroup(groupDto.Id, groupDto.Title, groupDto.Color,
                groupDto.Collapsed, members);
        }
        if (window.ActiveTabIndex >= 0 && window.ActiveTabIndex < mgr.Tabs.Count)
            mgr.ActivateIndex(window.ActiveTabIndex);
        return mgr;
    }

    [Fact]
    public void RoundTrip_preserves_pins_group_refs_and_collapse_state()
    {
        var groupId = Guid.NewGuid();
        var state = new SessionState
        {
            CleanShutdown = true,
            Windows =
            {
                new WindowSession
                {
                    ActiveTabIndex = 2,
                    Groups =
                    {
                        new GroupSession
                        {
                            Id = groupId,
                            Title = "work",
                            Color = TabColor.Green,
                            Collapsed = true,
                        },
                    },
                    Tabs =
                    {
                        Tab(pinned: true),
                        Tab(groupId: groupId),
                        Tab(),
                    },
                },
            },
        };

        var back = SessionSerializer.Deserialize(SessionSerializer.Serialize(state));

        Assert.NotNull(back);
        var window = Assert.Single(back!.Windows);
        var group = Assert.Single(window.Groups);
        Assert.Equal(groupId, group.Id);
        Assert.Equal("work", group.Title);
        Assert.Equal(TabColor.Green, group.Color);
        Assert.True(group.Collapsed);

        Assert.True(window.Tabs[0].IsPinned);
        Assert.Null(window.Tabs[0].GroupId);
        Assert.False(window.Tabs[1].IsPinned);
        Assert.Equal(groupId, window.Tabs[1].GroupId);
        Assert.False(window.Tabs[2].IsPinned);
        Assert.Null(window.Tabs[2].GroupId);
    }

    [Fact]
    public void RoundTrip_of_a_session_without_pins_or_groups_stays_clean()
    {
        var state = new SessionState
        {
            Windows =
            {
                new WindowSession
                {
                    Tabs = { Tab(), Tab() },
                },
            },
        };

        var back = SessionSerializer.Deserialize(SessionSerializer.Serialize(state));

        Assert.NotNull(back);
        var window = Assert.Single(back!.Windows);
        Assert.Empty(window.Groups);
        Assert.All(window.Tabs, t =>
        {
            Assert.False(t.IsPinned);
            Assert.Null(t.GroupId);
        });
    }

    // --- Capture side ---

    [Fact]
    public void CaptureTab_carries_the_pin_and_group_fields()
    {
        var mgr = NewManager(1);
        var group = mgr.CreateGroup(mgr.Tabs[0]);
        Assert.NotNull(group);
        var groupedTab = mgr.Tabs[0];
        var otherTab = mgr.Tabs[1];
        mgr.SetPinned(otherTab, true); // relocates: refs, not indices

        var grouped = Capture(groupedTab);
        var pinned = Capture(otherTab);

        Assert.Equal(group!.Id, grouped.GroupId);
        Assert.False(grouped.IsPinned);
        Assert.True(pinned.IsPinned);
        Assert.Null(pinned.GroupId); // pinning ungrouped it; capture says so

        // The defaults are the "no pin/group state" of the older format:
        // a capture that does not ask for the fields stays clean.
        var bare = SessionCapture.CaptureTab(
            mgr.Tabs[0].PaneHost.RootNode, mgr.Tabs[0].PaneHost.ActiveLeaf,
            null, "pwsh", null);
        Assert.False(bare.IsPinned);
        Assert.Null(bare.GroupId);
    }

    [Fact]
    public void CaptureGroups_maps_identity_title_color_and_collapse()
    {
        var mgr = NewManager(2); // [A, B, C]
        var first = mgr.CreateGroup(mgr.Tabs[0])!;
        first.Title = "work";
        first.Color = TabColor.Green;
        var second = mgr.CreateGroup(mgr.Tabs[2])!;
        mgr.CollapseGroup(second, true);

        var captured = SessionCapture.CaptureGroups(mgr.Groups);

        Assert.Equal(2, captured.Count);
        Assert.Equal(first.Id, captured[0].Id);
        Assert.Equal("work", captured[0].Title);
        Assert.Equal(TabColor.Green, captured[0].Color);
        Assert.False(captured[0].Collapsed);
        Assert.Equal(second.Id, captured[1].Id);
        Assert.True(captured[1].Collapsed);
    }

    // --- Restore side ---

    [Fact]
    public void RestoreGroup_recreates_the_saved_identity_and_gathers_members()
    {
        var mgr = NewManager(3); // [A, B, C, D]
        mgr.SetPinned(mgr.Tabs[0], true);
        var savedId = Guid.NewGuid();
        var members = new[] { mgr.Tabs[3], mgr.Tabs[1] }; // saved order: D then B

        var group = mgr.RestoreGroup(savedId, "work", TabColor.Teal,
            collapsed: true, members);

        // The saved id IS the live id -- group identity survives restart.
        Assert.Equal(savedId, group.Id);
        Assert.Equal("work", group.Title);
        Assert.Equal(TabColor.Teal, group.Color);
        Assert.True(group.IsCollapsed); // the saved bit, not an auto-expand
        Assert.Contains(group, mgr.Groups);
        AssertRunContiguous(mgr, group);
        Assert.Same(group, mgr.Tabs[1].Group);
        Assert.Same(group, mgr.Tabs[2].Group);
    }

    [Fact]
    public void RestoreGroup_survives_a_saved_pin_and_group_collision()
    {
        var mgr = NewManager(2); // [A, B, C]
        mgr.SetPinned(mgr.Tabs[0], true);
        var pinned = mgr.Tabs[0]; // saved as BOTH pinned and grouped: corrupt

        var group = mgr.RestoreGroup(Guid.NewGuid(), "corrupt", TabColor.Red,
            collapsed: false, new[] { pinned, mgr.Tabs[1] });

        // Membership yields to the prefix; nothing throws.
        Assert.True(pinned.IsPinned);
        Assert.Null(pinned.Group);
        Assert.Same(group, mgr.Tabs[1].Group);
        AssertRunContiguous(mgr, group);
    }

    [Fact]
    public void RestoreGroup_with_no_restorable_member_registers_nothing()
    {
        var mgr = NewManager(1);
        mgr.SetPinned(mgr.Tabs[0], true);

        // Every member pinned: the group never registers, so no orphan
        // header or chip can reach a strip.
        var group = mgr.RestoreGroup(Guid.NewGuid(), "lost", TabColor.Blue,
            collapsed: true, new[] { mgr.Tabs[0] });

        Assert.Empty(mgr.Groups);
        Assert.DoesNotContain(group, mgr.Groups);
        Assert.Null(mgr.Tabs[0].Group);

        // Neither does a member this manager does not own.
        var stranger = mgr.RestoreGroup(Guid.NewGuid(), "lost", TabColor.Blue,
            collapsed: false, new[] { new TabModel(new FakePaneHost()) });
        Assert.Empty(mgr.Groups);
        Assert.DoesNotContain(stranger, mgr.Groups);
    }

    [Fact]
    public void A_saved_GroupId_matching_no_group_restores_ungrouped()
    {
        var real = Guid.NewGuid();
        var phantom = Guid.NewGuid(); // carried by a tab, absent from Groups
        var state = new SessionState
        {
            Windows =
            {
                new WindowSession
                {
                    Groups =
                    {
                        new GroupSession { Id = real, Title = "real", Color = TabColor.Blue },
                    },
                    Tabs =
                    {
                        Tab(groupId: phantom),
                        Tab(groupId: real),
                        Tab(groupId: real),
                    },
                },
            },
        };

        var dst = Restore(state);

        // The orphan reference repairs to ungrouped; the group whose id
        // IS listed still forms around its own members.
        Assert.Null(dst.Tabs[0].Group);
        var group = Assert.Single(dst.Groups);
        Assert.Equal(real, group.Id);
        Assert.Same(group, dst.Tabs[1].Group);
        Assert.Same(group, dst.Tabs[2].Group);
        AssertRunContiguous(dst, group);
    }

    [Fact]
    public void A_capture_restore_cycle_reproduces_pins_groups_and_collapse()
    {
        var src = NewManager(5); // [T0 .. T5]
        src.SetPinned(src.Tabs[0], true);
        src.SetPinned(src.Tabs[2], true); // two pins; the saved list breaks them apart below
        var work = src.CreateGroup(src.Tabs[3])!;
        src.JoinGroup(src.Tabs[4], work);
        work.Title = "work";
        work.Color = TabColor.Green;
        var solo = src.CreateGroup(src.Tabs[5])!;
        src.CollapseGroup(solo, true); // collapsed, holding the active tab
        src.Activate(src.Tabs[5]);

        // Identity the round trip carries per tab: pin/GroupId patterns
        // are identical for two same-group members that swap, so only a
        // per-tab value can catch that class of reordering.
        for (int i = 0; i < src.Tabs.Count; i++)
            src.Tabs[i].ProfileId = "profile-" + i;

        var window = new WindowSession();
        foreach (var tab in src.Tabs) window.Tabs.Add(Capture(tab));
        window.Groups.AddRange(SessionCapture.CaptureGroups(src.Groups));
        var soloDto = window.Tabs[5];

        // A capture is always normalized (pins are a prefix), so the saved
        // list is perturbed to the legacy shape an older file can carry:
        // the second pin filed after an unpinned tab, [P, U, P, ...]. The
        // restore must fold that back into the prefix without losing the
        // saved relative order of the pins.
        (window.Tabs[1], window.Tabs[2]) = (window.Tabs[2], window.Tabs[1]);
        window.ActiveTabIndex = window.Tabs.IndexOf(soloDto);

        var state = new SessionState { CleanShutdown = true };
        state.Windows.Add(window);
        var back = SessionSerializer.Deserialize(SessionSerializer.Serialize(state));
        Assert.NotNull(back);
        var dst = Restore(back!);

        // Same arrangement, count for count. The per-index comparison is
        // against the source arrangement: the fold has to land the pins
        // back as T0 then T2 even though T2's saved slot sat after T1.
        Assert.Equal(src.Tabs.Count, dst.Tabs.Count);
        Assert.Equal(src.PinCount, dst.PinCount);
        for (int i = 0; i < src.Tabs.Count; i++)
        {
            Assert.Equal(src.Tabs[i].ProfileId, dst.Tabs[i].ProfileId);
            Assert.Equal(src.Tabs[i].IsPinned, dst.Tabs[i].IsPinned);
            Assert.Equal(src.Tabs[i].Group?.Id, dst.Tabs[i].Group?.Id);
        }
        // The collapse bit came back exactly as saved, and the ids are
        // the saved ids, not fresh ones.
        var dstSolo = Assert.Single(dst.Groups, g => g.Id == solo.Id);
        Assert.True(dstSolo.IsCollapsed);
        Assert.Equal(solo.Title, dstSolo.Title);
        var dstWork = Assert.Single(dst.Groups, g => g.Id == work.Id);
        Assert.Equal(work.Title, dstWork.Title);
        Assert.Equal(work.Color, dstWork.Color);
        Assert.False(dstWork.IsCollapsed);
        // The saved active tab is active again: a collapsed group holding
        // it is the Edge-135 shape the strips project.
        Assert.Same(dst.Tabs[5], dst.ActiveTab);
        Assert.True(dst.HoldsActiveTab(dstSolo));
    }

    [Fact]
    public void A_pin_only_session_restores_into_the_prefix_with_no_groups()
    {
        var state = new SessionState
        {
            Windows =
            {
                new WindowSession { Tabs = { Tab(), Tab(), Tab(pinned: true) } },
            },
        };

        var dst = Restore(state);

        Assert.Empty(dst.Groups);
        Assert.Equal(3, dst.Tabs.Count);
        Assert.Equal(1, dst.PinCount);
        Assert.All(dst.Tabs, t => Assert.Null(t.Group));
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
