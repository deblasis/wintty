using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The Ctrl+Tab switcher's chip wiring: the popup reads the strip
/// projection's rows, a collapsed group renders as ONE chip row carrying
/// the strip chip's four-part anatomy, and a chip activation expands
/// through the same command door the strip's own chip selection uses.
/// The shell cannot load into this test host, so these parse it; the
/// projection's row semantics are tested outright in
/// TabStripProjectionTests.
/// </summary>
public sealed class TabSwitcherChipWiringTests
{
    private const string PopupSource = "Tabs.TabSwitcherPopup.xaml.cs";
    private const string MainWindowSource = "MainWindow.xaml.cs";
    private const string TabHostSource = "Tabs.TabHost.xaml.cs";

    [Fact]
    public void The_switcher_rows_come_from_the_strips_projection()
    {
        var popup = ShellSource.Load(PopupSource);
        var show = popup.Method("Show");

        // The popup takes the manager and reads the projection at call
        // time, so a cycle step that expanded a group is rendered expanded
        // in step with the strip -- never from a stale row list captured
        // before the step.
        Assert.Equal("TabManager", show.ParameterList.Parameters[0].Type.ToString());
        Assert.True(
            show.Calls("TabStripProjection.HorizontalRows").Count == 1,
            "Show must read the projection once; a second walk is a parallel reading.");

        // One case per row kind, each landing on its own builder: the chip
        // row builds no pane preview, because the members it stands for
        // were suppressed by the projection -- a preview here would be a
        // second decision about what a collapsed run shows.
        var chip = popup.Case("Show", "HorizontalRow.Chip");
        var item = popup.Case("Show", "HorizontalRow.Item");
        Assert.True(chip.Calls("BuildGroupChip").Count == 1,
            "the chip case must build the chip card.");
        Assert.Empty(chip.Calls("BuildTabTile"));
        Assert.True(item.Calls("BuildTabTile").Count == 1,
            "the item case must build the tab tile.");

        // The preview belongs to the tile builder alone: a pane layout on a
        // chip row would be a second decision about what a collapsed run
        // shows, and the projection already made it.
        var tile = popup.Method("BuildTabTile");
        Assert.True(tile.Calls("renderer.BuildMiniLayout").Count == 1,
            "the tab tile's preview is the pane layout.");
        var groupChip = popup.Method("BuildGroupChip");
        Assert.Empty(groupChip.Calls("renderer.BuildMiniLayout"));
    }

    [Fact]
    public void A_chip_row_carries_the_strips_chip_anatomy()
    {
        var build = ShellSource.Load(PopupSource).Method("BuildGroupChip");

        // The strip chip's four parts (TabHost.AddGroupChip): color dot,
        // title, member count, chevron. Two TextBlocks -- title and count;
        // one FontIcon -- the chevron; two Borders -- the dot and the card
        // the anatomy sits on.
        var creations = build.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().ToList();
        Assert.Equal(2, creations.Count(o => o.Type.ToString().Contains("Border")));
        Assert.Equal(2, creations.Count(o => o.Type.ToString().Contains("TextBlock")));
        Assert.Equal(1, creations.Count(o => o.Type.ToString().Contains("FontIcon")));

        // The chevron points right (the strip's collapsed glyph) and its
        // font is pinned, or the glyph can render as nothing. Spelled at
        // the source level so an escape that lost its slash fails here.
        var chevron = creations.Single(o => o.Type.ToString().Contains("FontIcon"));
        Assert.Contains(chevron.DescendantNodes().OfType<LiteralExpressionSyntax>(),
            l => l.ToString() == "\"\\uE76C\"");
        Assert.Contains(build.DescendantNodes().OfType<LiteralExpressionSyntax>(),
            l => l.Token.ValueText == "SymbolThemeFontFamily");

        // The count answers the manager, not the rows: the projection
        // suppressed the members, so only MembersOf can say how many the
        // chip stands for.
        Assert.True(
            build.Calls("manager.MembersOf").Count == 1,
            "the chip's member count comes from the manager.");
        var textSets = build.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "Text")
            .Select(a => a.Right.ToString())
            .ToList();
        Assert.Equal(2, textSets.Count);
        Assert.Contains("group.Title", textSets);
        Assert.Contains("manager.MembersOf(group).Count.ToString()", textSets);

        // The card takes the tile's outer width so both row kinds share
        // the wrap grid's column math, tinted like the popup's colored
        // tiles: translucent preset wash over a preset border ring.
        var widths = build.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "Width")
            .Select(a => a.Right.ToString())
            .ToList();
        Assert.Contains("CellOuterWidth", widths);
        Assert.Contains("10", widths);
        Assert.Contains(build.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "TabColorPalette.Background");
        Assert.Equal(2, build.Calls("TabColorPalette.Border").Count); // dot + card ring
    }

    [Fact]
    public void The_highlight_never_parks_on_a_chip()
    {
        var popup = ShellSource.Load(PopupSource);

        // Chips are cycle stops, never the cycle's target -- a chip
        // activation lands on manager truth -- so there is no cell map a
        // group could be highlighted through. A chip-keyed map here is how
        // a ring ends up parked on a group nobody can activate.
        Assert.DoesNotContain(
            popup.Root.DescendantNodes().OfType<GenericNameSyntax>().ToList(),
            g => g.Identifier.ValueText == "Dictionary"
                 && g.TypeArgumentList.ToString().Contains("TabGroup"));

        // Highlight walks the tab-keyed map alone.
        var highlight = popup.Method("Highlight");
        Assert.Contains(highlight.DescendantNodes().OfType<IdentifierNameSyntax>(),
            i => i.Identifier.ValueText == "_cellByTab");
        Assert.DoesNotContain(
            highlight.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList(),
            c => c.CalleeText().EndsWith(".Activate"));
    }

    [Fact]
    public void A_chip_activation_expands_through_the_shared_command_and_lands_on_manager_truth()
    {
        var window = ShellSource.Load(MainWindowSource);
        var cycle = window.Method("CycleTab");

        // The cycle walks the projection's rows, not the raw tab list.
        Assert.True(
            cycle.Calls("TabStripProjection.HorizontalRows").Count == 1,
            "the cycle must walk the projection once.");

        // A chip is the expand gesture, through the same command path the
        // strip's own chip selection uses -- and the polarity is expand.
        // collapsed: true here would fold the run the user aimed at.
        var chip = window.Case("CycleTab", "HorizontalRow.Chip");
        var expand = chip.Call("_router.RequestCollapseGroup");
        Assert.Contains("collapsed: false", expand.ArgumentList.ToString());
        Assert.DoesNotContain(
            chip.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList(),
            c => c.CalleeText().EndsWith(".Activate"));

        // A tab row activates through the manager, and it is the only
        // Activate in the method: no second door outside the switch.
        var item = window.Case("CycleTab", "HorizontalRow.Item");
        Assert.True(item.Calls("_tabManager.Activate").Count == 1,
            "the item case activates through the manager.");
        Assert.True(cycle.Calls("_tabManager.Activate").Count == 1,
            "the manager Activate is the method's only one.");

        // The popup is shown the manager's active tab -- never the row the
        // step landed on -- so after a chip's expansion the ring sits on
        // manager truth.
        Assert.Equal("_tabManager.ActiveTab",
            cycle.Call("TabSwitcherPopupUI.Show").Arg(1));

        // The active row's slot is found by matching item rows by identity:
        // chips are slots, never the active row, so the lookup must not
        // answer a chip's slot for the active tab.
        var indexOf = ShellSource.Load(MainWindowSource).Method("CycleRowsIndexOf");
        Assert.Contains(indexOf.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "ReferenceEquals");
        Assert.Contains("HorizontalRow.Item", indexOf.Body!.Statements.ToString());

        // The door is shared: the strip's own chip selection expands
        // through the same command with the same polarity.
        var stripSelection = ShellSource.Load(TabHostSource).Method("OnSelectionChanged");
        var stripExpand = stripSelection.Call("_router.RequestCollapseGroup");
        Assert.Contains("collapsed: false", stripExpand.ArgumentList.ToString());
    }
}
