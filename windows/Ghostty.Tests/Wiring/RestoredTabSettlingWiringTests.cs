using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// A tab rebuilt from a snapshot -- session restore, reopen-closed-tab,
/// Duplicate Tab -- gets fresh shells, so it is genuinely starting and
/// should say so. It is ADOPTED rather than opened, though, and the
/// adopter deliberately begins nothing: adoption is also where a tab
/// dragged in from another window lands, and that host has already
/// painted and already disposed its cap timer, so a begin there would
/// strand every moved tab reading "Starting…" for good.
///
/// So the begin belongs at the three sites that know the tab is a rebuild
/// AND is about to be shown. Restore flags only the tab it brings to the
/// front: the others go in collapsed, never paint, and would sit starting
/// until their caps expired.
/// </summary>
public class RestoredTabSettlingWiringTests
{
    private static SyntaxNode Window() => ShellSource.Load("MainWindow.xaml.cs").Root;

    /// <summary>
    /// It asks the manager which tab is active rather than indexing the
    /// restored list. A saved tab whose tree no longer rebuilds shortens
    /// that list, so the saved index can fall off the end -- and the
    /// fallback would then flag a collapsed tab that never paints while the
    /// adopt loop's last tab is the one on screen. Even in range, the saved
    /// index counts the list as saved and the manager has since normalized
    /// pins and runs.
    /// </summary>
    [Fact]
    public void TheRestore_BeginsSettlingOnTheTabTheManagerMadeActive()
    {
        // The restore lives in a constructor, so this finds the statement
        // rather than a named method.
        var begin = Assert.Single(Window().DescendantNodes().OfType<ExpressionStatementSyntax>()
            .Where(s => s.ToString().Contains("BeginSettling")
                        && s.ToString().Contains("_tabManager.ActiveTab")));

        // On the manager's own answer, not on a subscript of the built list.
        Assert.DoesNotContain("restoredTabs[", begin.ToString());

        // And after the activation that decides that answer, in the same
        // block, or it names whichever tab the adopt loop left active.
        var block = Assert.IsType<BlockSyntax>(begin.Parent);
        var activate = block.Statements.Last(s => s.ToString().Contains("ActivateIndex"));
        Assert.True(
            block.Statements.IndexOf(begin) > block.Statements.IndexOf(activate),
            "the restore begins settling before it activates, so it flags the wrong tab");
    }

    [Theory]
    [InlineData("ReopenClosedTab", "tab")]
    [InlineData("DuplicateTab", "clone")]
    public void ARebuiltTab_StartsBeforeItIsAdopted(string method, string local)
    {
        var body = ShellSource.Load("MainWindow.xaml.cs").Method(method);
        var statements = body.Body!.Statements;

        int IndexOf(string call) => statements.IndexOf(statements.Single(s =>
            s.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Any(i => i.Expression.ToString() == call)));

        var begin = IndexOf($"{local}.BeginSettling");
        var adopt = IndexOf("_tabManager.AdoptTab");
        Assert.True(begin < adopt,
            $"{method} adopts before it begins settling, so the strip draws the tab once with the wrong icon");
    }

    /// <summary>
    /// No begin sits inside a loop. The whole point of picking one tab is
    /// that a restore of twenty tabs does not flag twenty of them; a
    /// foreach over the restored list would read as a reasonable
    /// generalisation and undo it.
    ///
    /// The other half of this -- that the ADOPTER itself begins nothing, so
    /// a tab dragged in from another window is not stranded starting -- is a
    /// behaviour, not a shape, and
    /// <c>TabStripPolishTests.ANewTabFromTheManager_StartsSettling_AndAnAdoptedOneDoesNot</c>
    /// holds it against the real TabManager.
    /// </summary>
    [Fact]
    public void NoBeginSettling_SitsInsideALoop()
    {
        // EndsWith, not a MemberAccess pattern: the restore's call is
        // null-conditional, which parses as a member BINDING and would slip
        // a pattern match silently.
        var begins = Window().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString().EndsWith("BeginSettling", System.StringComparison.Ordinal))
            .ToList();

        // Three sites: the restore, reopen-closed-tab, Duplicate Tab. An
        // empty query would make the loop below say nothing at all.
        Assert.Equal(3, begins.Count);

        foreach (var begin in begins)
        {
            // CommonForEachStatementSyntax covers the deconstructing form
            // too, which is its own node type.
            Assert.DoesNotContain(begin.Ancestors(),
                a => a is CommonForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax);
        }
    }
}
