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

    /// <summary>
    /// The pane that is TOLD to take focus and the pane CloseLeaf REPORTS as
    /// focused have to be the same one, so the focus call is enqueued after
    /// the zoom re-entry that can still move the active leaf.
    ///
    /// Enqueued before it, the call named the tree's first leaf; re-entering
    /// zoom then parked that leaf off-screen, the dispatcher drained the
    /// stale call afterwards, and OnTerminalGotFocus took it for a real
    /// focus change -- raising a second, contradicting LeafFocused and
    /// painting the active border on a pane nobody could see.
    /// </summary>
    [Fact]
    public void CloseLeaf_EnqueuesOneFocus_AfterTheZoomDecision()
    {
        var close = ShellSource.Load("Panes.PaneHost.cs").Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "CloseLeaf" && m.ParameterList.Parameters.Count == 2);
        var statements = close.Body!.DescendantNodes().OfType<StatementSyntax>().ToList();

        // One enqueued focus call in THIS method. The zoom re-entry's own
        // ToggleSplitZoom enqueues a second, but that one targets the leaf
        // this method has already made active, so it drains as a no-op on an
        // already-focused control. Two here would be two different panes.
        var focusEnqueues = statements.OfType<ExpressionStatementSyntax>()
            .Where(s => s.ToString().Contains("DispatcherQueue.TryEnqueue")
                        && s.ToString().Contains(".Focus("))
            .ToList();
        Assert.Single(focusEnqueues);
        // A top-level statement of the method, not one branch's. Nested
        // inside the zoom re-entry it would still sort after every
        // assignment below while leaving the ordinary close -- no zoom in
        // play at all -- focusing nothing.
        Assert.Same(close.Body, focusEnqueues[0].Parent);

        // It comes after the last thing that can move the active leaf --
        // which is the zoom re-entry's assignment.
        var lastAssign = statements.Last(s =>
            s is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.Text: "_activeLeaf" } } });
        Assert.True(
            statements.IndexOf(focusEnqueues[0]) > statements.IndexOf(lastAssign),
            "the focus call is enqueued before the zoom re-entry can move the active leaf, so it names the wrong pane");

        // And it focuses the leaf that decision settled on, not a copy taken
        // before it. The capture is a local so the lambda cannot read a
        // field that moves again before the dispatcher drains.
        var captured = statements.OfType<LocalDeclarationStatementSyntax>()
            .Last(s => s.Declaration.Variables.Count == 1
                       && s.Declaration.Variables[0].Initializer?.Value.ToString() == "_activeLeaf");
        Assert.Same(close.Body, captured.Parent);
        Assert.True(
            statements.IndexOf(captured) > statements.IndexOf(lastAssign),
            "the focus target is captured before the zoom re-entry, so it is the pre-zoom leaf");
        Assert.Contains(
            captured.Declaration.Variables[0].Identifier.Text,
            focusEnqueues[0].ToString());
    }
}
