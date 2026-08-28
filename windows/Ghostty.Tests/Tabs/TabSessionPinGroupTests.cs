using System;
using Ghostty.Core.Session;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// Serialization surface for the pin/group fields of section 6 of the
/// tab-reorder spec. Restore-side repair is wired by a later PR; this
/// only proves the fields survive the source-generated round trip.
/// </summary>
public class TabSessionPinGroupTests
{
    private static TabSession Tab(bool pinned = false, Guid? groupId = null)
        => new TabSession { Tree = new LeafDto { ProfileId = "pwsh" }, IsPinned = pinned, GroupId = groupId };

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
}
