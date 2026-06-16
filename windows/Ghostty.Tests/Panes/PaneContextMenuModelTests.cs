using System.Linq;
using Ghostty.Core.Panes;
using Xunit;

namespace Ghostty.Tests.Panes;

public class PaneContextMenuModelTests
{
    [Fact]
    public void Build_ProducesExpectedCommandOrder()
    {
        var items = PaneContextMenuModel.Build(hasSelection: true, isZoomed: false);

        var commands = items
            .Where(i => i.Kind == PaneMenuItemKind.Action)
            .Select(i => i.Command)
            .ToArray();

        Assert.Equal(new[]
        {
            PaneMenuCommand.Copy,
            PaneMenuCommand.Paste,
            PaneMenuCommand.SelectAll,
            PaneMenuCommand.SplitRight,
            PaneMenuCommand.SplitDown,
            PaneMenuCommand.ZoomPane,
            PaneMenuCommand.CommandPalette,
            PaneMenuCommand.ResetTerminal,
            PaneMenuCommand.ToggleInspector,
            PaneMenuCommand.ChangeTabTitle,
            PaneMenuCommand.ChangeTerminalTitle,
            PaneMenuCommand.ClosePane,
        }, commands);
    }

    [Fact]
    public void Build_InsertsSeparatorsBetweenGroups()
    {
        var items = PaneContextMenuModel.Build(hasSelection: true, isZoomed: false);

        Assert.Equal(4, items.Count(i => i.Kind == PaneMenuItemKind.Separator));
        Assert.NotEqual(PaneMenuItemKind.Separator, items[0].Kind);
        Assert.NotEqual(PaneMenuItemKind.Separator, items[^1].Kind);
        for (var i = 1; i < items.Count; i++)
            Assert.False(items[i].Kind == PaneMenuItemKind.Separator
                && items[i - 1].Kind == PaneMenuItemKind.Separator);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_CopyEnabledTracksSelection(bool hasSelection)
    {
        var items = PaneContextMenuModel.Build(hasSelection, isZoomed: false);
        var copy = items.Single(i => i.Command == PaneMenuCommand.Copy);
        Assert.Equal(hasSelection, copy.IsEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_ZoomCheckedTracksZoomState(bool isZoomed)
    {
        var items = PaneContextMenuModel.Build(hasSelection: false, isZoomed);
        var zoom = items.Single(i => i.Command == PaneMenuCommand.ZoomPane);
        Assert.Equal(isZoomed, zoom.IsChecked);
    }

    [Fact]
    public void Build_NonCopyItemsAlwaysEnabled()
    {
        var items = PaneContextMenuModel.Build(hasSelection: false, isZoomed: false);
        foreach (var item in items.Where(i =>
                     i.Kind == PaneMenuItemKind.Action && i.Command != PaneMenuCommand.Copy))
            Assert.True(item.IsEnabled, $"{item.Command} should be enabled");
    }

    [Fact]
    public void Build_EveryActionHasNonEmptyLabel()
    {
        var items = PaneContextMenuModel.Build(hasSelection: true, isZoomed: true);
        foreach (var item in items.Where(i => i.Kind == PaneMenuItemKind.Action))
            Assert.False(string.IsNullOrWhiteSpace(item.Label));
    }

    [Fact]
    public void Build_EveryActionHasIcon()
    {
        var items = PaneContextMenuModel.Build(hasSelection: true, isZoomed: false);
        foreach (var item in items.Where(i => i.Kind == PaneMenuItemKind.Action))
            Assert.False(string.IsNullOrEmpty(item.Icon), $"{item.Command} should have an icon");
    }

    [Fact]
    public void Build_SeparatorsHaveNoIcon()
    {
        var items = PaneContextMenuModel.Build(hasSelection: true, isZoomed: false);
        foreach (var item in items.Where(i => i.Kind == PaneMenuItemKind.Separator))
            Assert.Null(item.Icon);
    }

    [Fact]
    public void Build_MapsKnownGlyphs()
    {
        var items = PaneContextMenuModel.Build(hasSelection: true, isZoomed: false);
        Assert.Equal("\uE8C8", items.Single(i => i.Command == PaneMenuCommand.Copy).Icon);
        Assert.Equal("\uE777", items.Single(i => i.Command == PaneMenuCommand.ResetTerminal).Icon);
    }

    [Fact]
    public void Build_ZoomIconReflectsZoomState()
    {
        var notZoomed = PaneContextMenuModel.Build(hasSelection: false, isZoomed: false)
            .Single(i => i.Command == PaneMenuCommand.ZoomPane);
        var zoomed = PaneContextMenuModel.Build(hasSelection: false, isZoomed: true)
            .Single(i => i.Command == PaneMenuCommand.ZoomPane);
        Assert.Equal("\uE740", notZoomed.Icon);
        Assert.Equal("\uE73F", zoomed.Icon);
    }
}
