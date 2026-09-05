using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Closing a pane hands the tab the surviving pane's title and directory.
///
/// CloseLeaf reassigns the active leaf before focus lands on it, so the
/// GotFocus handler sees a leaf that is already active and raises no
/// LeafFocused. Everything that follows the active leaf -- the tab's
/// title, its directory, progress and bell -- rebinds on that event, so
/// the tab went on naming the pane that had just closed until the survivor's
/// next prompt. CloseLeaf raises the event itself, after the last
/// reassignment.
/// </summary>
public class PaneCloseFocusWiringTests
{
    [Fact]
    public void CloseLeaf_RaisesLeafFocused_AfterItsLastActiveLeafAssignment()
    {
        // Two overloads; the one-arg form forwards to this one.
        var close = ShellSource.Load("Panes.PaneHost.cs").Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "CloseLeaf" && m.ParameterList.Parameters.Count == 2);
        var statements = close.Body!.DescendantNodes().OfType<StatementSyntax>().ToList();

        // `LeafFocused?.Invoke(this, _activeLeaf)`: a conditional access whose
        // invocation binds `.Invoke` and hands over the live field, not a
        // copy taken before the reassignment.
        var raise = statements.OfType<ExpressionStatementSyntax>().SingleOrDefault(s =>
            s.Expression is ConditionalAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.Text: "LeafFocused" },
                WhenNotNull: InvocationExpressionSyntax
                {
                    Expression: MemberBindingExpressionSyntax { Name.Identifier.Text: "Invoke" },
                } invoke,
            }
            && invoke.ArgumentList.Arguments.Count == 2
            && invoke.ArgumentList.Arguments[1].Expression.ToString() == "_activeLeaf");
        Assert.NotNull(raise);
        // A top-level statement of the method, not one branch's: a raise
        // inside the zoom re-entry block would fire on that path alone.
        Assert.Same(close.Body, raise!.Parent);

        var lastAssign = statements.Last(s =>
            s is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.Text: "_activeLeaf" } } });
        Assert.True(
            statements.IndexOf(raise!) > statements.IndexOf(lastAssign),
            "LeafFocused is raised before the active leaf's last reassignment, so subscribers rebind to the wrong pane");
    }
}
