using Ghostty.Core.Input;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Input;

public class ApprtActionMapTests
{
    [Fact] public void NewTab_Maps() =>
        Assert.Equal(PaneAction.NewTab, ApprtActionMap.Map(GhosttyActionTag.NewTab, 0));

    [Theory]
    [InlineData((int)GhosttySplitDirection.Right, PaneAction.SplitVertical)]
    [InlineData((int)GhosttySplitDirection.Down,  PaneAction.SplitHorizontal)]
    public void NewSplit_MapsByDirection(int dir, PaneAction expected) =>
        Assert.Equal(expected, ApprtActionMap.Map(GhosttyActionTag.NewSplit, dir));

    [Theory]
    [InlineData((int)GhosttyGotoSplit.Previous, PaneAction.GotoSplitPrevious)]
    [InlineData((int)GhosttyGotoSplit.Next,     PaneAction.GotoSplitNext)]
    [InlineData((int)GhosttyGotoSplit.Up,       PaneAction.FocusUp)]
    [InlineData((int)GhosttyGotoSplit.Down,     PaneAction.FocusDown)]
    [InlineData((int)GhosttyGotoSplit.Left,     PaneAction.FocusLeft)]
    [InlineData((int)GhosttyGotoSplit.Right,    PaneAction.FocusRight)]
    public void GotoSplit_Maps(int v, PaneAction expected) =>
        Assert.Equal(expected, ApprtActionMap.Map(GhosttyActionTag.GotoSplit, v));

    [Theory]
    [InlineData(-1, PaneAction.PrevTab)]
    [InlineData(-2, PaneAction.NextTab)]
    [InlineData(-3, PaneAction.JumpTabLast)]
    [InlineData(1,  PaneAction.JumpTab1)]
    [InlineData(8,  PaneAction.JumpTab8)]
    public void GotoTab_Maps(int v, PaneAction expected) =>
        Assert.Equal(expected, ApprtActionMap.Map(GhosttyActionTag.GotoTab, v));

    [Theory]
    [InlineData((int)GhosttyResizeSplitDirection.Up,    PaneAction.ResizeSplitUp)]
    [InlineData((int)GhosttyResizeSplitDirection.Down,  PaneAction.ResizeSplitDown)]
    [InlineData((int)GhosttyResizeSplitDirection.Left,  PaneAction.ResizeSplitLeft)]
    [InlineData((int)GhosttyResizeSplitDirection.Right, PaneAction.ResizeSplitRight)]
    public void ResizeSplit_Maps(int v, PaneAction expected) =>
        Assert.Equal(expected, ApprtActionMap.Map(GhosttyActionTag.ResizeSplit, v));

    [Fact] public void EqualizeSplits_Maps() =>
        Assert.Equal(PaneAction.EqualizeSplits, ApprtActionMap.Map(GhosttyActionTag.EqualizeSplits, 0));
    [Fact] public void ToggleSplitZoom_Maps() =>
        Assert.Equal(PaneAction.ToggleSplitZoom, ApprtActionMap.Map(GhosttyActionTag.ToggleSplitZoom, 0));
    [Fact] public void ToggleFullscreen_Maps() =>
        Assert.Equal(PaneAction.ToggleFullscreen, ApprtActionMap.Map(GhosttyActionTag.ToggleFullscreen, 0));
    [Fact] public void CloseTab_MapsToProgressive() =>
        Assert.Equal(PaneAction.CloseActiveProgressive, ApprtActionMap.Map(GhosttyActionTag.CloseTab, 0));

    [Theory]
    [InlineData(1,  PaneAction.MoveTabRight)]
    [InlineData(-1, PaneAction.MoveTabLeft)]
    public void MoveTab_MapsBySign(int amount, PaneAction expected) =>
        Assert.Equal(expected, ApprtActionMap.Map(GhosttyActionTag.MoveTab, amount));

    [Fact] public void UnknownTag_ReturnsNull() =>
        Assert.Null(ApprtActionMap.Map((GhosttyActionTag)9999, 0));

    [Theory]
    [InlineData(GhosttyActionTag.GotoTab, 0)]   // below the 1..8 index range
    [InlineData(GhosttyActionTag.GotoTab, 9)]   // above the 1..8 index range
    [InlineData(GhosttyActionTag.MoveTab, 0)]   // zero amount has no direction
    [InlineData(GhosttyActionTag.NewSplit, (int)GhosttySplitDirection.Left)] // not represented in PaneAction
    [InlineData(GhosttyActionTag.NewSplit, (int)GhosttySplitDirection.Up)]   // not represented in PaneAction
    internal void UnrepresentedValue_ReturnsNull(GhosttyActionTag tag, int value) =>
        Assert.Null(ApprtActionMap.Map(tag, value));
}
