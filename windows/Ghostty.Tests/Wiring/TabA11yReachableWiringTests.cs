using System;
using System.Linq;
using Microsoft.CodeAnalysis;
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
        Assert.Equal("i + 1", position.Arg(1));

        // The size is the panel's own child count, whether that is read
        // inline or hoisted into a local first.
        var sizeArg = size.Arg(1);
        var sizeSource = stamp.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.Text == sizeArg)?.Initializer?.Value.ToString()
            ?? sizeArg;
        Assert.Equal("_pinnedPanel.Children.Count", sizeSource);

        // Both write to a child of the panel, not to some other element.
        Assert.All([position, size], c => Assert.Contains("_pinnedPanel.Children", c.Arg(0)));

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
    /// Raised from the content's one-shot Loaded, not from the constructor
    /// where the restore happens. There the tab hosts do not exist, the
    /// XamlRoot is null so there is no focused element to raise from, and
    /// there is no UIA tree -- the notification would be built from nothing
    /// and dropped, which no test that only checked the call existed would
    /// notice.
    /// </summary>
    [Fact]
    public void ARestore_AnnouncesItself_OnceTheTreeExists()
    {
        var window = ShellSource.Load("MainWindow.xaml.cs");
        var announce = window.Method("AnnounceSessionRestored");

        var call = Assert.Single(announce.Calls("UiaAnnouncer.Announce"));
        // Its own activity id: notifications coalesce per source, so a bell
        // in the same breath would otherwise discard this one.
        Assert.Equal("\"session-restore\"", call.Arg(2));
        // The wording lives in Core, where it can be tested as text.
        Assert.Contains("TabAccessibleText.SessionRestoredAnnouncement", announce.ToString());
        // And it is raised from a real element, never a bare null.
        Assert.NotEqual("null", call.Arg(0));

        // Called from the Loaded one-shot, and from nowhere else.
        var invocations = window.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString() == "AnnounceSessionRestored")
            .ToList();
        var from = Assert.Single(invocations);
        var handler = from.Ancestors().OfType<LocalFunctionStatementSyntax>().FirstOrDefault();
        Assert.NotNull(handler);
        Assert.Contains("Loaded", handler!.Identifier.Text, StringComparison.OrdinalIgnoreCase);
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
    [Theory]
    [InlineData("ShellReportedCwd")]
    [InlineData("HomeDirectory")]
    public void BothVerticalRowKinds_NameTheDirectoryTheyFollow(string property)
    {
        var bindings = Strip().Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString() == "AotBinding.Create"
                        && i.ArgumentList.ToString().Contains($"nameof(TabModel.{property})"))
            .ToList();

        // The body row and the pinned square, both.
        Assert.Equal(2, bindings.Count);
    }

    [Theory]
    [InlineData("ShellReportedCwd")]
    [InlineData("HomeDirectory")]
    public void TheHorizontalStrip_NamesTheDirectoryItFollows(string property)
    {
        var add = ShellSource.Load("Tabs.TabHost.xaml.cs").Method("AddItem");
        var arm = Assert.Single(add.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Statement.ToString().Contains("ApplyItemAccessibleText")
                        && i.Statement.ToString().Contains("headerText.Text")));
        Assert.Contains($"nameof(TabModel.{property})", arm.Condition.ToString());
    }
}
