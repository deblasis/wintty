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

    private static InvocationExpressionSyntax ModelClose(MethodDeclarationSyntax close)
    {
        // Any spelling that ends in PaneTree.Close (qualified or not) counts,
        // so a namespace-qualified call cannot hide from the ordering checks.
        var closes = close.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("PaneTree.Close", System.StringComparison.Ordinal))
            .ToList();
        Assert.True(
            closes.Count == 1,
            $"CloseLeaf: expected exactly one call ending in PaneTree.Close, found {closes.Count}");
        return closes[0];
    }

    [Fact]
    public void CloseLeaf_CapturesTheUndoSnapshot_BeforePaneTreeClose()
    {
        var close = CloseLeaf();
        var capture = CloseCapture(close);
        var modelClose = ModelClose(close);

        Assert.True(
            capture.SpanStart < modelClose.SpanStart,
            "CloseLeaf must call CaptureForUndo(Close) BEFORE PaneTree.Close: Close rewires the "
            + "live tree in place when the parent has a grandparent, so a later snapshot "
            + "no longer holds the closed pane and undo cannot restore it");
    }

    [Fact]
    public void CloseLeaf_RunsNothingThatCouldCloseTheModel_AheadOfTheCapture()
    {
        // Pins the prefix, not one callee: a close hidden behind a helper,
        // another spelling, a constructor, a property or an extension before
        // the capture would restore the defect while the later
        // PaneTree.Close stays a harmless no-op. The top-level statements
        // ahead of the capture's `if (softClose)` are listed exactly; any
        // new statement there is a deliberate decision, not an accident.
        var close = CloseLeaf();
        var capture = CloseCapture(close);
        var gate = capture.Ancestors().OfType<IfStatementSyntax>().First();

        var before = close.Body!.Statements
            .TakeWhile(s => !ReferenceEquals(s, gate))
            .Select(s => string.Concat(s.ToString().Where(ch => !char.IsWhiteSpace(ch))))
            .ToList();

        var expected = new[]
        {
            "SettlePaneFades();",
            "if(!Core.Panes.PaneTree.Leaves(_root).Contains(leaf))return;",
            "varzoomedBefore=_zoomedLeaf;",
            "varpaneSurvives=!ReferenceEquals(_root,leaf);",
            "varsoftClose=undoable&&paneSurvives&&_undoEnabled;",
        };
        Assert.True(
            ReferenceEquals(gate.Parent, close.Body),
            "the capture's `if (softClose)` must be a top-level statement of CloseLeaf");
        Assert.Equal(expected, before);

        // And belt and braces on the call names, by full receiver: nothing
        // else may be invoked ahead of the capture.
        var offenders = close.Body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.SpanStart < capture.SpanStart)
            .Select(c => c.CalleeText())
            .Where(name => name != "SettlePaneFades"
                           && name != "ReferenceEquals"
                           && name != "Core.Panes.PaneTree.Leaves"
                           && name != "Core.Panes.PaneTree.Leaves(_root).Contains")
            .ToList();
        Assert.True(
            offenders.Count == 0,
            "calls ahead of CaptureForUndo(Close) in CloseLeaf that are not on the allowlist: "
            + string.Join(", ", offenders));
        Assert.Empty(close.Body.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()
            .Where(c => c.SpanStart < capture.SpanStart));
    }

    private static MethodDeclarationSyntax HostMethod(string name, int paramCount)
    {
        var found = ShellSource.Load("Panes.PaneHost.cs").Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == name && m.ParameterList.Parameters.Count == paramCount)
            .ToList();
        Assert.True(found.Count == 1, $"expected one {paramCount}-parameter {name}, found {found.Count}");
        return found[0];
    }

    [Fact]
    public void Snapshot_CopiesTheLiveTree_ItDoesNotAliasIt()
    {
        // PaneTree.Close rewires the live tree in place, so a snapshot that
        // held _root itself would be rewritten by the close it precedes.
        var snapshot = HostMethod("Snapshot", 1);
        var creation = Assert.Single(snapshot.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>());
        var first = Assert.IsType<InvocationExpressionSyntax>(
            creation.ArgumentList!.Arguments[0].Expression);
        Assert.EndsWith("PaneTree.Clone", first.CalleeText());
        Assert.Equal("_root", first.Arg(0));
    }

    [Fact]
    public void CaptureForUndo_PushesTheSnapshotSynchronously()
    {
        // Deferring the push (a dispatcher callback, a lambda) would take the
        // snapshot after the close has run.
        var capture = HostMethod("CaptureForUndo", 1);
        var push = Assert.Single(capture.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "_history.Push");
        Assert.Equal("Snapshot(kind)", push.Arg(0));
        Assert.Empty(push.Ancestors().TakeWhile(n => !ReferenceEquals(n, capture))
            .Where(n => n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));
        Assert.DoesNotContain(capture.Body.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText().Contains("TryEnqueue"));
    }
    [Fact]
    public void CloseLeaf_DecidesSoftClose_FromTheTreeAsItStands_AndUndoable()
    {
        // A pane survives unless the closed leaf IS the root. The decision
        // must read the live tree without closing it, and softClose must be
        // exactly undoable && survives && undo enabled: `paneSurvives = true`,
        // dropping it, or inverting it each change what gets captured or torn
        // down and must red here.
        var close = CloseLeaf();
        var decls = close.Body!.DescendantNodes().OfType<VariableDeclaratorSyntax>().ToList();
        var survives = decls.Single(v => v.Identifier.ValueText == "paneSurvives");
        var soft = decls.Single(v => v.Identifier.ValueText == "softClose");
        var modelClose = ModelClose(close);

        Assert.Equal("!ReferenceEquals(_root, leaf)", survives.Initializer!.Value.ToString());
        Assert.Equal("undoable && paneSurvives && _undoEnabled", soft.Initializer!.Value.ToString());
        Assert.True(
            soft.Span.End <= modelClose.SpanStart,
            "softClose must be decided before PaneTree.Close runs");
    }

    [Fact]
    public void CloseLeaf_CaptureIsADirectStatementOfTheSoftCloseThenBody()
    {
        var close = CloseLeaf();
        var capture = CloseCapture(close);

        // The capture is an expression statement (not a lambda body or an
        // argument), directly in the then-body of `if (softClose)`: not its
        // else, and not deferred.
        var stmt = Assert.IsType<ExpressionStatementSyntax>(capture.Parent);
        IfStatementSyntax? gate = stmt.Parent switch
        {
            IfStatementSyntax i when ReferenceEquals(i.Statement, stmt) => i,
            BlockSyntax b when b.Parent is IfStatementSyntax i && ReferenceEquals(i.Statement, b) => i,
            _ => null,
        };
        Assert.True(
            gate is not null && gate.Condition.ToString() == "softClose",
            "CaptureForUndo(Close) must be a direct statement of the then-body of `if (softClose)`");
        Assert.Null(gate!.Else);
        Assert.True(
            gate.Span.End <= ModelClose(close).SpanStart,
            "the `if (softClose)` that captures must end before PaneTree.Close");
    }

    [Fact]
    public void CloseLeaf_DoesNotDescribeTheModelCloseAsPure()
    {
        // The comment that hid the defect. Close is documented, and behaves,
        // as a mutating op; the call site must not say otherwise.
        Assert.DoesNotContain("pure model op", CloseLeaf().ToFullString());
    }
}
