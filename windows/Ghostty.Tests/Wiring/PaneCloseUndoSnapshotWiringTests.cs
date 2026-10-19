using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The undo snapshot of a close is taken BEFORE the model closes.
///
/// <c>PaneTree.Close</c> rewires the live tree in place whenever the closed
/// leaf's parent has a grandparent, so a snapshot taken after it records a
/// tree that already lacks the pane and undo cannot bring it back (three or
/// more panes). <c>CloseLeaf</c> therefore decides whether a pane survives
/// without calling Close, captures while the leaf is still in the tree, and
/// only then closes. The shell assembly cannot be loaded into a test host,
/// so the ordering is pinned on the parsed source.
/// </summary>
public class PaneCloseUndoSnapshotWiringTests
{
    private static MethodDeclarationSyntax CloseLeaf()
    {
        var found = ShellSource.Load("Panes.PaneHost.cs").Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == "CloseLeaf" && m.ParameterList.Parameters.Count == 2)
            .ToList();
        Assert.True(found.Count == 1, $"expected one two-parameter CloseLeaf, found {found.Count}");
        return found[0];
    }

    private static InvocationExpressionSyntax CloseCapture(MethodDeclarationSyntax close)
    {
        var captures = close.Body!.Calls("CaptureForUndo")
            .Where(c => c.ArgExpression(0).ToString().EndsWith("PaneOpKind.Close"))
            .ToList();
        Assert.True(
            captures.Count == 1,
            $"CloseLeaf: expected exactly one CaptureForUndo(PaneOpKind.Close), found {captures.Count}");
        return captures[0];
    }

    [Fact]
    public void CloseLeaf_CapturesTheUndoSnapshot_BeforePaneTreeClose()
    {
        var close = CloseLeaf();
        var capture = CloseCapture(close);
        var modelClose = close.Call("PaneTree.Close");

        Assert.True(
            capture.SpanStart < modelClose.SpanStart,
            "CloseLeaf must call CaptureForUndo(Close) BEFORE PaneTree.Close: Close rewires the "
            + "live tree in place when the parent has a grandparent, so a later snapshot "
            + "no longer holds the closed pane and undo cannot restore it");
    }

    [Fact]
    public void CloseLeaf_CallsPaneTreeCloseExactlyOnce()
    {
        // A second Close call (or one hidden in a helper the capture precedes)
        // would let the ordering test above pass while the real mutation
        // happens elsewhere.
        var close = CloseLeaf();
        Assert.Single(close.Body!.Calls("PaneTree.Close"));
    }

    [Fact]
    public void CloseLeaf_DecidesSoftClose_WithoutTheClosedTree()
    {
        // softClose must be known before Close runs (the capture is gated on
        // it), so its initializer cannot read the result of Close.
        var close = CloseLeaf();
        var decl = close.Body!.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "softClose");
        var modelClose = close.Call("PaneTree.Close");

        Assert.True(
            decl.Span.End <= modelClose.SpanStart,
            "softClose must be decided before PaneTree.Close runs");
        Assert.DoesNotContain("newRoot", decl.Initializer!.Value.ToString());
    }

    [Fact]
    public void CloseLeaf_CaptureIsGatedOnSoftClose_AndSitsOutsideLaterBranches()
    {
        var close = CloseLeaf();
        var capture = CloseCapture(close);

        var gate = capture.Ancestors().OfType<IfStatementSyntax>()
            .FirstOrDefault(i => i.Condition.ToString() == "softClose");
        Assert.True(gate is not null, "CaptureForUndo(Close) must sit inside `if (softClose)`");
        // The gate is the capture's own block, not the later soft-close
        // branch that runs after Close: a capture that moved into that
        // branch would be after the mutation.
        var modelClose = close.Call("PaneTree.Close");
        Assert.True(
            gate!.Span.End <= modelClose.SpanStart,
            "the `if (softClose)` that captures must end before PaneTree.Close");
    }

    [Fact]
    public void CloseLeaf_NoLongerCallsTheClosePureOrClaimsTheSnapshotHasTheLeaf()
    {
        // The two comments that hid the defect. Close is documented, and
        // behaves, as a mutating op; the call site must not say otherwise.
        var text = CloseLeaf().ToFullString();
        Assert.DoesNotContain("pure model op", text);
    }
}
