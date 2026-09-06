using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Three things a screen reader could not reach, pinned in source because
/// WinUI cannot load headlessly.
///
/// A pinned square is a plain element in a custom panel, so the framework
/// computes no set position for it and two squares called "Home" were
/// indistinguishable. A completed session restore said nothing at all. And
/// the accessible name followed the shell's directory only through a
/// fan-out in TabModel that no strip mentioned.
/// </summary>
public class TabA11yReachableWiringTests
{
    private static ShellSource Strip() => ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

    // --- the pinned band publishes a position ---

    /// <summary>
    /// Stamped over the PANEL's children, in order, one-based. The rows
    /// dictionary has no order, so a stamp driven from it compiles and is
    /// silently wrong -- which is the mutation this exists to catch.
    /// </summary>
    [Fact]
    public void ThePinnedBand_StampsEachSquaresPositionFromThePanel()
    {
        var stamp = Strip().Method("StampPinnedPositions");

        var position = Assert.Single(stamp.Calls("AutomationProperties.SetPositionInSet"));
        var size = Assert.Single(stamp.Calls("AutomationProperties.SetSizeOfSet"));

        // One-based: UIA counts from 1, and a 0-based stamp reads as an
        // off-by-one to every client rather than as an obvious break.
        var loop = Assert.Single(stamp.DescendantNodes().OfType<ForStatementSyntax>());
        var index = loop.Declaration!.Variables[0].Identifier.Text;
        Assert.Equal($"{index} + 1", position.Arg(1));

        // The size is the panel's own child count, whether that is read
        // inline or hoisted into a local first.
        var sizeArg = size.Arg(1);
        var sizeSource = stamp.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.Text == sizeArg)?.Initializer?.Value.ToString()
            ?? sizeArg;
        Assert.Equal("_pinnedPanel.Children.Count", sizeSource);

        // Each writes to the child at the LOOP INDEX, not to a fixed one:
        // `Children[0]` reads the same in a substring match and gives every
        // square the first one's position.
        Assert.All([position, size], c => Assert.Equal($"_pinnedPanel.Children[{index}]", c.Arg(0)));

        // And the walk is over the panel, never the unordered dictionary.
        Assert.Contains("_pinnedPanel.Children", stamp.ToString());
        Assert.DoesNotContain("_pinnedRows", stamp.ToString());
    }

    /// <summary>
    /// It rides the pass that already runs after every mutation of the
    /// band -- add, remove, reorder, and the rebuild the reconcile falls
    /// back to on skew -- rather than any one of them, which is how a
    /// position goes stale on the path nobody remembered.
    /// </summary>
    [Fact]
    public void ThePositionStamp_RidesTheChromePass()
    {
        var strip = Strip();
        Assert.Single(strip.Method("UpdatePinnedShelfChrome").Calls("StampPinnedPositions"));

        // Exactly one caller: a second would mean the pass is being driven
        // from a mutation site again.
        Assert.Single(strip.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString() == "StampPinnedPositions"));

        // The two passes that must reach it.
        Assert.NotEmpty(strip.Method("ReconcileRowOrder").Calls("UpdatePinnedShelfChrome"));
        Assert.NotEmpty(strip.Method("RebuildAllItems").Calls("UpdatePinnedShelfChrome"));
    }

    /// <summary>
    /// A position is only meaningful inside a set, and the squares report
    /// themselves as list items -- so the band they sit in has to BE a list
    /// and has to have a name. Without it a listener hears "Home, list
    /// item, 1 of 2" with nothing saying which two, and the per-square
    /// "Pinned" cannot fill the gap because ItemStatus is the property this
    /// codebase measured as unread on a list item.
    /// </summary>
    [Fact]
    public void ThePinnedBand_IsAListWithAName()
    {
        var build = Strip().Method("BuildPinnedShelf");

        var role = Assert.Single(build.Calls("AutomationProperties.SetAutomationControlType"));
        Assert.Equal("_pinnedPanel", role.Arg(0));
        Assert.Contains("AutomationControlType.List", role.Arg(1));

        var name = Assert.Single(build.Calls("AutomationProperties.SetName"));
        Assert.Equal("_pinnedPanel", name.Arg(0));
        var text = Assert.IsType<LiteralExpressionSyntax>(name.ArgExpression(1)).Token.ValueText;
        Assert.False(string.IsNullOrWhiteSpace(text), "the band's name says nothing");
    }

    /// <summary>
    /// NOT stamped from the row's own refresh: the drag's drop preview is
    /// built from that same class and belongs to no set, so a stamp there
    /// would place a ghost at "3 of 5".
    /// </summary>
    [Fact]
    public void TheRowItself_ClaimsNoPosition()
    {
        var row = ShellSource.Load("Tabs.VerticalTabPinnedRow.cs").Root.ToString();
        Assert.DoesNotContain("SetPositionInSet", row);
        Assert.DoesNotContain("SetSizeOfSet", row);
    }

    // --- a restore says so ---

    /// <summary>
    /// Raised on the window's FIRST ACTIVATION, and unsubscribed there, so
    /// it speaks once and only once the window is foreground with focus
    /// somewhere inside it.
    ///
    /// Not from the constructor, where the restore happens: no tab hosts,
    /// no XamlRoot, no UIA tree. And not from the content's Loaded, which
    /// was the first attempt -- the tree exists there, but the window can
    /// still be behind the splash with focus nowhere, which is the state
    /// BellAnnouncementSource records a notification being measured as
    /// dropped in. Neither mistake is visible to a test that only checks
    /// the call exists, so this checks where it is raised from.
    /// </summary>
    [Fact]
    public void ARestore_AnnouncesItself_OnceTheWindowIsUp()
    {
        var window = ShellSource.Load("MainWindow.xaml.cs");
        var announce = window.Method("AnnounceSessionRestored");

        var call = Assert.Single(announce.Calls("UiaAnnouncer.Announce"));
        // Its own activity id: notifications coalesce per source, so a bell
        // in the same breath would otherwise discard this one.
        Assert.Equal("\"session-restore\"", call.Arg(2));
        // The wording lives in Core, where it can be tested as text.
        Assert.Contains("TabAccessibleText.SessionRestoredAnnouncement", announce.ToString());

        // Raised from exactly one place.
        var from = Assert.Single(window.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString() == "AnnounceSessionRestored"));

        // That place is a handler SUBSCRIBED to Activated -- matched on the
        // subscription, not on the handler's name, which a rename would
        // defeat while leaving the wiring correct and which a function
        // called "OnLoadedAnything" would satisfy while being wired to
        // nothing at all.
        var handler = from.Ancestors().OfType<LocalFunctionStatementSyntax>().FirstOrDefault();
        Assert.NotNull(handler);
        var subscription = Assert.Single(window.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                        && a.Right.ToString() == handler!.Identifier.Text));
        Assert.Equal("Activated", subscription.Left.ToString());

        // ...and it takes itself off again, or a restored window announces
        // on every activation for the rest of its life.
        Assert.Contains(handler!.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.IsKind(SyntaxKind.SubtractAssignmentExpression)
                 && a.Left.ToString() == "Activated"
                 && a.Right.ToString() == handler.Identifier.Text);
    }

    /// <summary>
    /// The announcement dereferences the XamlRoot to find a focused element
    /// to carry it, from inside an event handler, where a throw is an
    /// unhandled exception on the UI thread. It checks first.
    /// </summary>
    [Fact]
    public void TheAnnouncement_GuardsTheRootItDereferences()
    {
        var announce = ShellSource.Load("MainWindow.xaml.cs").Method("AnnounceSessionRestored");
        Assert.Contains(announce.Body!.Statements.OfType<IfStatementSyntax>(),
            s => s.Condition.ToString().Contains("XamlRoot")
                 && s.Statement.ToString().Contains("return"));
    }

    /// <summary>
    /// The count is captured where the restore knows it. By the time there
    /// is a tree to announce into, the manager has normalized pins and
    /// gathered runs, so its own count no longer answers the question.
    /// </summary>
    [Fact]
    public void TheRestoredCount_IsTakenFromTheRestoredList()
    {
        var window = ShellSource.Load("MainWindow.xaml.cs");
        var write = Assert.Single(window.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_restoredTabCount"));
        Assert.Equal("restoredTabs.Count", write.Right.ToString());
    }

    // --- the name follows the directory, provably ---

    /// <summary>
    /// Both strips name the directory properties they depend on, even
    /// though EffectiveTitle already carries them. The redundancy IS the
    /// fix: without it the accessible name followed the shell's directory
    /// only through TabModel's fan-out, and narrowing that fan-out would
    /// have frozen every tab's name at its birth value with nothing between
    /// the change and the defect.
    /// </summary>
    [Fact]
    public void BothVerticalRowKinds_NameTheDirectoryTheyFollow()
    {
        // Matched on the ARGUMENT, not on a substring of the whole list:
        // the name can also appear inside a callback lambda, which says
        // nothing about what the binding watches.
        var methods = Strip().Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(i => i.Expression.ToString() == "AotBinding.Create"
                          && i.ArgumentList.Arguments.Any(a =>
                              a.ToString() == "nameof(TabModel.ShellReportedCwd)")))
            .Select(m => m.Identifier.Text)
            .ToList();

        // The body row and the pinned square -- two DIFFERENT builders, not
        // two bindings that happen to sit in the same one.
        Assert.Equal(2, methods.Distinct().Count());
    }

    [Fact]
    public void TheHorizontalStrip_NamesTheDirectoryItFollows()
    {
        var add = ShellSource.Load("Tabs.TabHost.xaml.cs").Method("AddItem");
        var arm = Assert.Single(add.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Statement.ToString().Contains("ApplyItemAccessibleText")
                        && i.Statement.ToString().Contains("headerText.Text")));
        Assert.Contains("nameof(TabModel.ShellReportedCwd)", arm.Condition.ToString());
    }

    /// <summary>
    /// HomeDirectory reaches the accessible name exactly as the directory
    /// does, and is deliberately absent from all three lists: it is written
    /// once, before any strip has a row to subscribe with, so naming it
    /// would document a dependency that can never fire -- and would then be
    /// held there by a test.
    /// </summary>
    [Fact]
    public void NoStrip_SubscribesToTheHomeDirectory()
    {
        foreach (var source in new[] { "Tabs.VerticalTabStrip.xaml.cs", "Tabs.TabHost.xaml.cs" })
        {
            Assert.DoesNotContain(
                "nameof(TabModel.HomeDirectory)",
                ShellSource.Load(source).Root.ToString());
        }
    }
}
