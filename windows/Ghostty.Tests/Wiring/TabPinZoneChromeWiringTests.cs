using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The horizontal pin zone's chrome: the glyph rides the header's
/// existing chrome build, and the boundary stroke has one writer, one
/// predicate, and the vertical stroke's brighten-only-while-live polarity
/// (4b-1). The vertical stroke's drag-machine facts live in
/// TabPinZoneWiringTests; this is the horizontal edition's paint, which
/// the shell cannot load into this test host to check -- so these parse
/// it.
/// </summary>
public sealed class TabPinZoneChromeWiringTests
{
    private const string TabHostSource = "Tabs.TabHost.xaml.cs";

    [Fact]
    public void The_pin_glyph_rides_the_chrome_build_not_a_parallel_path()
    {
        var tabHost = ShellSource.Load(TabHostSource);
        var addItem = tabHost.Method("AddItem");

        // Built in the header's build loop, exactly once: a glyph minted
        // by a second path (a refresh method, a template) drifts from the
        // chrome the rest of the header rides. The rail's twin lesson.
        var pinIcons = addItem.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(o => o.Type.ToString().Contains("FontIcon"))
            .Where(o => o.Initializer.ToString().Contains("\\uE718"))
            .ToList();
        Assert.True(
            pinIcons.Count == 1,
            $"AddItem builds the pin glyph exactly once; found {pinIcons.Count}.");

        // Leading slot of the icon row: the pin is read before the
        // profile icon and the title, which is what makes it a zone mark
        // rather than an attachment.
        var adds = addItem.Calls("iconRow.Children.Add");
        var glyphAdd = adds.FirstOrDefault(a => a.Arg(0) == "pinGlyph");
        var iconAdd = adds.FirstOrDefault(a => a.Arg(0) == "iconHost");
        Assert.True(
            glyphAdd is not null && iconAdd is not null
                && glyphAdd.SpanStart < iconAdd.SpanStart,
            "the pin glyph takes the icon row's leading slot.");

        // The IsPinned INPC branch is the only thing that shows it:
        // SetPinned can skip TabMoved (the boundary tab pinning up, the
        // last pinned tab unpinning both relocate without one), so the
        // flag change itself must carry the toggle.
        var toggle = addItem.DescendantNodes().OfType<IfStatementSyntax>()
            .FirstOrDefault(i => i.Condition.ToString().Contains("IsPinned")
                && i.Statement.ToString().Contains("pinGlyph.Visibility"));
        Assert.True(
            toggle is not null,
            "the IsPinned INPC branch must toggle the glyph.");

        // And nothing else in the shell writes it: one writer, or two
        // pin states that can disagree.
        var stray = tabHost.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "pinGlyph.Visibility"
                && !addItem.FullSpan.Contains(a.Span))
            .ToList();
        Assert.Empty(stray);
    }

    [Fact]
    public void The_boundary_stroke_cleans_up_on_every_drag_exit()
    {
        var tabHost = ShellSource.Load(TabHostSource);
        var dragEnd = tabHost.Method("FinishHorizontalDrag");

        // The release is the one pass every completed drag runs, so it is
        // where the dim has to live -- a cleanup on any narrower event is
        // a stroke a cancelled or off-strip release can outlive. The leak
        // census is the shape: ApplyPinZoneChrome is the border's ONLY
        // writer in the shell, so any assignment outside it is a stroke
        // this pass does not own.
        var apply = tabHost.Method("ApplyPinZoneChrome");
        Assert.True(
            dragEnd.Calls("ApplyPinZoneChrome").Count == 1,
            "FinishHorizontalDrag must run the boundary pass: the dim is " +
            "the cleanup, and the release is the pass every drag exits by.");
        // The other exit: CancelHorizontalDrag runs the same pass for a
        // canceled, captured-away, or stale session, so those ends dim
        // the stroke too.
        var stray = tabHost.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => (a.Left.ToString().EndsWith(".BorderBrush")
                    || a.Left.ToString().EndsWith(".BorderThickness"))
                && !apply.FullSpan.Contains(a.Span))
            .ToList();
        Assert.Empty(stray);

        // After the flag drops, not before: the pass reads the flag, so a
        // cleanup ordered ahead of the drop would repaint the bright
        // stroke it exists to remove.
        var flagDrop = dragEnd.AssignsTo("_stripDragActive")
            .First(a => a.Right.ToString() == "false");
        var cleanup = dragEnd.Call("ApplyPinZoneChrome");
        Assert.True(
            flagDrop.SpanStart < cleanup.SpanStart,
            "the boundary cleanup must follow the drag flag dropping.");
    }

    [Fact]
    public void The_stroke_brightens_only_while_a_drag_is_live()
    {
        var tabHost = ShellSource.Load(TabHostSource);
        var brush = tabHost.Method("PinBoundaryBrush");

        // Polarity, by arm and not by substring: bright 0xE6 under the
        // live flag, dim 0x59 otherwise. A hover, an armed-but-undragged
        // session, and an idle strip all read the dim branch -- the exact
        // match on the condition is what a `!` or a `!=` fails.
        var branch = brush.DescendantNodes().OfType<ConditionalExpressionSyntax>()
            .ToList();
        Assert.True(
            branch.Count == 1,
            $"PinBoundaryBrush branches once on drag liveness; found {branch.Count}.");
        Assert.Equal("_stripDragActive", branch[0].Condition.ToString());
        Assert.Contains("0xE6", branch[0].WhenTrue.ToString());
        Assert.Contains("0x59", branch[0].WhenFalse.ToString());

        // And the brighten pass itself reads the flag already live: the
        // raise precedes the pass in the engine's begin pass, so the
        // first boundary brush of a drag is the bright one, not a frame
        // late.
        var dragStart = tabHost.Method("BeginHorizontalDragVisual");
        var flagRaise = dragStart.AssignsTo("_stripDragActive")
            .First(a => a.Right.ToString() == "true");
        var brighten = dragStart.Call("ApplyPinZoneChrome");
        Assert.True(
            flagRaise.SpanStart < brighten.SpanStart,
            "the drag begin must raise the flag before its boundary pass.");
    }

    [Fact]
    public void The_stroke_marks_the_last_pinned_tab_and_nothing_else()
    {
        var tabHost = ShellSource.Load(TabHostSource);
        var apply = tabHost.Method("ApplyPinZoneChrome");

        // The predicate is the manager's prefix length minus one: the zone
        // edge is manager truth, and during a drag the strip's order is
        // TabView's preview -- no TabItems read here, ever.
        var edge = apply.DescendantNodes().OfType<ElementAccessExpressionSyntax>()
            .FirstOrDefault(e => e.Expression.ToString() == "_manager.Tabs");
        Assert.True(
            edge is not null,
            "the zone edge reads the manager's tab list.");
        Assert.Contains("PinCount - 1", edge.ArgumentList.ToString());
        var guard = apply.DescendantNodes().OfType<ConditionalExpressionSyntax>()
            .ToList();
        Assert.True(
            guard.Count == 1 && guard[0].WhenFalse.ToString() == "null",
            "an empty pin zone draws nothing: the edge is null, not tab 0.");
        Assert.DoesNotContain("TabItems", apply.Body!.ToString());

        // And the neighbour carries nothing: every non-boundary item gets
        // the default thickness and no brush, which is what keeps the
        // stroke an edge instead of a fence around the zone.
        var fork = apply.DescendantNodes().OfType<IfStatementSyntax>()
            .FirstOrDefault(i => i.Condition.ToString()
                .Contains("ReferenceEquals(model, boundary)"));
        Assert.True(
            fork is not null,
            "the stroke forks on the boundary identity.");
        Assert.Contains(
            "BorderThickness = default", fork.Else?.Statement.ToString() ?? string.Empty);
        Assert.Contains(
            "BorderBrush = null", fork.Else?.Statement.ToString() ?? string.Empty);
    }
}
