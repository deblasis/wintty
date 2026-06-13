using Ghostty.Core.Panes;
using Ghostty.Core.Session;
using Xunit;

namespace Ghostty.Tests.Session;

public class SessionSerializerTests
{
    private static SessionState Sample()
    {
        var tree = new SplitDto
        {
            Orientation = PaneOrientation.Vertical,
            Ratio = 0.4,
            Child1 = new LeafDto { ProfileId = "pwsh", Fallback = new LeafCommand { ResolvedCommand = "pwsh.exe", DisplayName = "PowerShell" } },
            Child2 = new LeafDto { ProfileId = "cmd", Fallback = new LeafCommand { ResolvedCommand = "cmd.exe", DisplayName = "Command Prompt" } },
        };
        return new SessionState
        {
            CleanShutdown = true,
            Windows =
            {
                new WindowSession
                {
                    Geometry = new WindowGeometry { X = 10, Y = 20, Width = 800, Height = 600, Maximized = false },
                    ActiveTabIndex = 1,
                    Tabs =
                    {
                        new TabSession { ProfileId = "pwsh", UserTitle = "work", Tree = tree, ActiveLeafPath = new[] { true }, ZoomedLeafPath = new[] { false } },
                    },
                },
            },
        };
    }

    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var json = SessionSerializer.Serialize(Sample());
        var back = SessionSerializer.Deserialize(json);

        Assert.NotNull(back);
        Assert.True(back!.CleanShutdown);
        var w = Assert.Single(back.Windows);
        Assert.Equal(800, w.Geometry.Width);
        Assert.Equal(1, w.ActiveTabIndex);
        var t = Assert.Single(w.Tabs);
        Assert.Equal("work", t.UserTitle);
        Assert.Equal(new[] { true }, t.ActiveLeafPath);
        Assert.Equal(new[] { false }, t.ZoomedLeafPath);
        var split = Assert.IsType<SplitDto>(t.Tree);
        Assert.Equal(0.4, split.Ratio, precision: 6);
        Assert.Equal("pwsh", Assert.IsType<LeafDto>(split.Child1).ProfileId);
        Assert.Equal("cmd.exe", Assert.IsType<LeafDto>(split.Child2).Fallback!.ResolvedCommand);
    }

    [Fact]
    public void Deserialize_Garbage_ReturnsNull()
    {
        Assert.Null(SessionSerializer.Deserialize("{ not json"));
        Assert.Null(SessionSerializer.Deserialize(""));
    }
}
