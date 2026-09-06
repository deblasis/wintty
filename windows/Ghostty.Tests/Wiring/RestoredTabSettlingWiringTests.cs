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

    [Fact]
    public void TheRestore_BeginsSettlingOnlyTheTabItBringsToTheFront()
    {
        var begins = Window().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "BeginSettling",
                Expression: ElementAccessExpressionSyntax,
            })
            .ToList();

        // Exactly one, and it indexes the restored list rather than looping
        // it -- a foreach here is the flicker this guard exists to stop.
        var begin = Assert.Single(begins);
        var target = (ElementAccessExpressionSyntax)
            ((MemberAccessExpressionSyntax)begin.Expression).Expression;
        Assert.Equal("restoredTabs", target.Expression.ToString());
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
        foreach (var begin in Window().DescendantNodes().OfType<InvocationExpressionSyntax>()
                     .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "BeginSettling" }))
        {
            Assert.DoesNotContain(begin.Ancestors(),
                a => a is ForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax);
        }
    }
}
