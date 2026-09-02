using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The horizontal pin zone's chrome: a pinned tab is an icon square, and
/// the zone's edge is that change of shape rather than a stroke. The
/// anatomy has one writer, one predicate, and one pass every drag exit
/// runs. The vertical band's drag-machine facts live in
/// TabPinZoneWiringTests; this is the horizontal edition's paint, which
/// the shell cannot load into this test host to check -- so these parse
/// it.
/// </summary>
public sealed class TabPinZoneChromeWiringTests
{
    private const string TabHostSource = "Tabs.TabHost.xaml.cs";

    /// <summary>
    /// The pinned tab collapses to an icon square, and the square is the
    /// zone mark. The pushpin glyph that used to carry that job is gone:
    /// it existed because equal-width kept a pinned tab full-size, so
    /// nothing about the slot said "pinned" and an inline marker had to.
    /// A glyph reintroduced beside the icon it captions is the double
    /// statement the shape replaced -- so its absence is pinned here, not
    /// merely left to taste.
    /// </summary>
    [Fact]
    public void A_pinned_tab_is_an_icon_square_and_carries_no_pushpin()
    {
        var tabHost = ShellSource.Load(TabHostSource);
        var source = tabHost.Root.ToString();

        // The pushpin is gone from the class, not just from the build.
        Assert.DoesNotContain("pinGlyph", source, StringComparison.Ordinal);
        Assert.DoesNotContain("E718", source, StringComparison.Ordinal);

        // Both bounds, not one: TabView's Equal mode writes Width on every
        // item it holds, and only the Min/Max clamp survives that pass.
        // Setting one alone leaves the strip's own sizing free to win.
        var anatomy = tabHost.Method("ApplyPinnedTabAnatomy");
        var min = anatomy.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "item.MinWidth");
        var max = anatomy.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "item.MaxWidth");
        Assert.Equal("pinned ? PinnedTabWidth : 0", min.Right.ToString());
        Assert.Equal(
            "pinned ? PinnedTabWidth : double.PositiveInfinity", max.Right.ToString());

        // The same square the vertical band spends, from the same number:
        // the two layouts collapse a pin to one size, or the grammar is
        // two grammars that happen to rhyme.
        var width = tabHost.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "PinnedTabWidth");
        Assert.Contains(
            "TabPinBand.ChipSize", width.Initializer!.Value.ToString(),
            StringComparison.Ordinal);

        // The title the square gives up rides the tooltip, and only while
        // pinned: an unpinned tab wears its title and is owed nothing.
        var tip = anatomy.Call("ToolTipService.SetToolTip");
        Assert.Equal("item", tip.Arg(0));
        Assert.Equal("pinned ? tab.EffectiveTitle : null", tip.Arg(1));

        // No close button in a 48px slot. Closing a pinned tab stays a
        // decision the context menu and the keybind take.
        var closable = anatomy.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "item.IsClosable");
        Assert.Equal("!pinned", closable.Right.ToString());
    }

    /// <summary>
    /// One writer owns the anatomy, and every drag exit runs it. A tab's
    /// shape follows its OWN pin flag rather than the prefix length: a
    /// drag mid-flight has the strip showing TabView's preview order, and
    /// a shape derived from "which slot is last in the prefix" would
    /// collapse whichever tab the preview currently has there.
    /// </summary>
    [Fact]
    public void The_pinned_anatomy_has_one_writer_and_every_drag_exit_runs_it()
    {
        var tabHost = ShellSource.Load(TabHostSource);
        var anatomy = tabHost.Method("ApplyPinnedTabAnatomy");

        // The predicate: the tab's own flag, and no read of the strip's
        // inventory or of the prefix length.
        var pinned = anatomy.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "pinned");
        Assert.Equal("tab.IsPinned", pinned.Initializer!.Value.ToString());
        Assert.DoesNotContain("PinCount", anatomy.Body!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("TabItems", anatomy.Body!.ToString(), StringComparison.Ordinal);

        // The leak census: the width clamp has exactly one writer, so a
        // tab stuck in the wrong shape has one place to have come from.
        var stray = tabHost.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => (a.Left.ToString().EndsWith(".MinWidth", StringComparison.Ordinal)
                    || a.Left.ToString().EndsWith(".MaxWidth", StringComparison.Ordinal)
                    || a.Left.ToString().EndsWith(".IsClosable", StringComparison.Ordinal))
                && !anatomy.FullSpan.Contains(a.Span))
            .ToList();
        Assert.Empty(stray);

        // The sweep pass fans the writer over every item, and both drag
        // exits run it: a gesture can pin or unpin mid-flight, and a
        // crossing the manager refused raised no event at all.
        Assert.Single(tabHost.Method("ApplyPinZoneChrome").Calls("ApplyPinnedTabAnatomy"));
        Assert.Single(tabHost.Method("FinishHorizontalDrag").Calls("ApplyPinZoneChrome"));
        Assert.Single(tabHost.Method("CancelHorizontalDrag").Calls("ApplyPinZoneChrome"));

        // After the flag drops, not before -- the ordering the stroke's
        // brighten/dim needed is still the ordering the sweep wants: a
        // pass ordered ahead of the drop reads a drag that is over.
        var dragEnd = tabHost.Method("FinishHorizontalDrag");
        var flagDrop = dragEnd.AssignsTo("_stripDragActive")
            .First(a => a.Right.ToString() == "false");
        Assert.True(
            flagDrop.SpanStart < dragEnd.Call("ApplyPinZoneChrome").SpanStart,
            "the anatomy sweep must follow the drag flag dropping.");

        // The build takes the shape too, and only after the registries:
        // the pass reads the icon row back out of _iconRowByModel, so a
        // tab that arrives already pinned has to be findable first.
        //
        // THREE calls in AddItem, and the count is pinned because each is a
        // different reason: the INPC lambda's title branch, the INPC
        // lambda's IsPinned branch -- both declared early, inside a lambda
        // that runs much later -- and the build's own, which is the one
        // that has to follow the registries and so is the last of the
        // three. The title branch is there because a pinned tab's title
        // TextBlock is collapsed: the tooltip is the only visible carrier
        // of the title, and this pass is its only writer.
        var addItem = tabHost.Method("AddItem");
        var takes = addItem.Calls("ApplyPinnedTabAnatomy");
        Assert.Equal(3, takes.Count);
        var register = addItem.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_iconRowByModel[tab]");
        Assert.True(
            register.SpanStart < takes.Last().SpanStart,
            "the anatomy pass must follow the icon row being registered.");

        // And the flag change carries it: SetPinned relocates to the zone
        // boundary and skips TabMoved when from == to, so a
        // relocation-path refresh alone leaves a tab in the wrong shape.
        Assert.Contains(
            addItem.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Condition.ToString().Contains("IsPinned")
                && i.Statement.ToString().Contains("ApplyPinnedTabAnatomy"));

        // ...and so does the TITLE change, which is the branch a count
        // alone would not pin to any particular place. On a pinned tab the
        // header TextBlock this branch writes is collapsed, so without the
        // anatomy pass the square's tooltip kept saying whatever the tab
        // was called when it was pinned.
        Assert.Contains(
            addItem.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Condition.ToString().Contains("EffectiveTitle")
                && i.Statement.ToString().Contains("ApplyPinnedTabAnatomy"));
    }

    /// <summary>
    /// Nothing draws a rule between the zones. The horizontal stroke went
    /// with the vertical one for the same reason: the shapes divide, and
    /// a line beside a structural division states it twice.
    /// </summary>
    [Fact]
    public void No_stroke_is_drawn_between_the_zones()
    {
        var tabHost = ShellSource.Load(TabHostSource);

        // Parsed, not scanned. `Root.ToString()` is the file's TEXT, comments
        // included, and the join ring's own comment explains why it does not
        // borrow this brush -- so the guard went red on prose describing the
        // very absence it was asserting. An identifier sweep sees declarations
        // and references and nothing else; trivia is not a use.
        Assert.Empty(tabHost.Root.DescendantNodes()
            .Where(n => n is IdentifierNameSyntax { Identifier.ValueText: "PinBoundaryBrush" }
                     or MethodDeclarationSyntax { Identifier.ValueText: "PinBoundaryBrush" }));
        // The border was the stroke's only expression on a TabViewItem;
        // nothing in the strip writes one now.
        Assert.Empty(tabHost.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString().EndsWith(".BorderBrush", StringComparison.Ordinal)
                || a.Left.ToString().EndsWith(".BorderThickness", StringComparison.Ordinal)));
    }
}
