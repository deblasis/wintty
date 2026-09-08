using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// A transient notice must actually leave on its own, and Enter or Space
/// must dismiss a focused one; both live in AddBar, the only place a bar is
/// born.
/// </summary>
public sealed class NotificationHostWiringTests
{
    private static ShellSource Host() => ShellSource.Load("Controls.Notifications.NotificationHost.xaml.cs");

    /// <summary>
    /// WinUI folds a StackPanel's own Margin into its DesiredSize even with
    /// zero children, so leaving the host Visible while empty would keep the
    /// Auto dock row -- and the host's own opaque background -- permanently
    /// open (fix round 1, finding 1). Starting Collapsed is what makes "no
    /// notices" actually mean zero height.
    /// </summary>
    [Fact]
    public void Constructor_StartsCollapsed()
    {
        var ctor = Host().Root.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "NotificationHost");
        var setsCollapsed = ctor.Body!.Statements
            .OfType<ExpressionStatementSyntax>()
            .Any(s => s.Expression is AssignmentExpressionSyntax
            {
                Left: IdentifierNameSyntax { Identifier.ValueText: "Visibility" },
            } assignment && assignment.Right.ToString() == "Visibility.Collapsed");
        Assert.True(
            setsCollapsed,
            "the constructor must start the host Collapsed, or an empty StackPanel's own Margin "
            + "keeps the Auto dock row permanently open");
    }

    /// <summary>
    /// Every path that changes <see cref="_bars"/> -- adding a notice,
    /// removing one, and the two bulk-clear paths -- has to agree on the
    /// same collapse-when-empty decision, found by shape (a method that sets
    /// Visibility from _bars.Count) rather than by a hardcoded name, so this
    /// does not just re-assert one method's name back at it.
    /// </summary>
    [Fact]
    public void VisibilityTracksWhetherAnyBarIsActive()
    {
        var src = Host();

        // Not restricted to block-bodied methods: UpdateVisibility itself is
        // an expression-bodied one, whose assignment lives under
        // ExpressionBody rather than Body -- a Body-only search would find
        // zero methods and fail this fact for a reason that has nothing to
        // do with what it is meant to pin.
        var updater = src.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left is IdentifierNameSyntax { Identifier.ValueText: "Visibility" }
                    && a.Right.ToString().Contains("_bars.Count")))
            .ToList();
        Assert.True(
            updater.Count == 1,
            $"expected exactly one method that sets Visibility from _bars.Count, found {updater.Count}");
        var helperName = updater[0].Identifier.ValueText;

        foreach (var caller in new[] { "AddBar", "RemoveBar", "OnUnloaded", "OnActiveChanged" })
        {
            var calls = src.Method(caller).Body!.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(i => i.Expression.ToString());
            Assert.Contains(
                calls,
                c => c == helperName);
        }
    }

    /// <summary>
    /// Tightened from a substring census (fix round 1, finding 7): the old
    /// version accepted "AutoDismissAfter" and "CreateTimer" appearing
    /// anywhere in AddBar's text, which a timer that is created and stored
    /// but never started would still satisfy. This pins the actual call:
    /// the local the AutoDismissAfter branch creates via CreateTimer() must
    /// itself be the receiver of a .Start() in that same branch.
    /// </summary>
    [Fact]
    public void AddBar_ArmsAndStartsTheAutoDismissTimer_WhenTheNoticeAsksForOne()
    {
        var body = Host().Method("AddBar").Body!;
        var guard = body.Statements.OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("AutoDismissAfter"))
            .ToList();
        Assert.True(
            guard.Count == 1,
            $"expected exactly one AutoDismissAfter guard in AddBar, found {guard.Count}");
        var block = Assert.IsType<BlockSyntax>(guard[0].Statement);

        var timerVar = block.Statements
            .OfType<LocalDeclarationStatementSyntax>()
            .SelectMany(d => d.Declaration.Variables)
            .FirstOrDefault(v => v.Initializer?.Value is InvocationExpressionSyntax inv
                && inv.Expression.ToString().EndsWith("CreateTimer"));
        Assert.True(
            timerVar is not null,
            "expected AddBar's AutoDismissAfter branch to create a timer via CreateTimer()");

        var started = block.Statements
            .OfType<ExpressionStatementSyntax>()
            .Any(s => s.Expression is InvocationExpressionSyntax inv
                && inv.Expression.ToString() == timerVar!.Identifier.ValueText + ".Start");
        Assert.True(
            started,
            "the timer AddBar creates for AutoDismissAfter must actually be started: a bare "
            + "Contains(\"CreateTimer\") check would still pass an armed-but-never-started timer, "
            + "and a transient notice that never starts its timer never dismisses itself");
    }

    /// <summary>
    /// Tightened from a substring census (fix round 1, finding 7): the old
    /// version accepted "VirtualKey.Enter", "VirtualKey.Space" and
    /// "FocusOnShow" appearing anywhere in AddBar's text, which a handler
    /// that sets e.Handled and does nothing else would still satisfy. This
    /// pins that the Enter-or-Space branch inside the FocusOnShow guard's
    /// KeyDown handler actually calls Dismiss.
    /// </summary>
    [Fact]
    public void AddBar_EnterOrSpace_ActuallyDismissesTheFocusedNotice()
    {
        var body = Host().Method("AddBar").Body!;
        var guard = body.Statements.OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("FocusOnShow"))
            .ToList();
        Assert.True(
            guard.Count == 1,
            $"expected exactly one FocusOnShow guard in AddBar, found {guard.Count}");

        var keyDownHandlers = guard[0].DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression) && a.Left.ToString().EndsWith("KeyDown"))
            .Select(a => a.Right)
            .ToList();
        Assert.True(
            keyDownHandlers.Count == 1,
            $"expected exactly one KeyDown += inside the FocusOnShow branch, found {keyDownHandlers.Count}");

        var keyGuard = keyDownHandlers[0].DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("VirtualKey.Enter")
                && i.Condition.ToString().Contains("VirtualKey.Space"))
            .ToList();
        Assert.True(keyGuard.Count == 1, "expected one Enter-or-Space guard inside the KeyDown handler");

        var dismisses = keyGuard[0].DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression.ToString().EndsWith(".Dismiss"));
        Assert.True(
            dismisses,
            "the Enter/Space branch must actually call Dismiss on the notice: a handler that only "
            + "sets e.Handled = true would still contain every token this fact used to look for");
    }

    /// <summary>
    /// Tightened from a substring census (fix round 1, finding 7): the old
    /// version accepted "Stop()" and "FocusReturn" appearing anywhere in
    /// RemoveBar's text, which an unconditional FocusReturn?.Invoke() --
    /// stealing focus away on every dismissal, including a notice that was
    /// never focused or one the user has since focused elsewhere -- would
    /// still satisfy. This pins that the call sits behind an `if` whose
    /// condition is exactly the local that reads notice.FocusOnShow.
    /// </summary>
    [Fact]
    public void RemoveBar_StopsTheTimer_AndReturnsFocusOnlyWhenTheBarStillHasIt()
    {
        var body = Host().Method("RemoveBar").Body!;
        var removeIf = body.Statements.OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains("_bars.Remove"))
            .ToList();
        Assert.True(
            removeIf.Count == 1,
            $"expected exactly one `_bars.Remove` guard in RemoveBar, found {removeIf.Count}");
        var block = Assert.IsType<BlockSyntax>(removeIf[0].Statement);

        var stopped = block.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression.ToString().EndsWith(".Stop"));
        Assert.True(stopped, "RemoveBar must stop the notice's timer, if it had one");

        // The decision that gates FocusReturn, wherever it is declared and
        // whatever it is named: it has to actually read notice.FocusOnShow,
        // not merely exist as a bool that happens to be true whenever a bar
        // had one.
        var decision = block.Statements.OfType<LocalDeclarationStatementSyntax>()
            .SelectMany(d => d.Declaration.Variables)
            .FirstOrDefault(v => v.Initializer is not null
                && v.Initializer.Value.ToString().Contains("FocusOnShow"));
        Assert.True(
            decision is not null,
            "expected a local in RemoveBar that reads notice.FocusOnShow to decide whether focus returns");

        // FocusReturn?.Invoke() is a conditional-access expression: the
        // receiver ("FocusReturn") belongs to the ConditionalAccessExpression,
        // not to the nested InvocationExpression (whose own Expression is
        // just the bound ".Invoke"), so the receiver has to be matched at
        // that outer node.
        var focusCall = block.Statements.OfType<IfStatementSyntax>()
            .Where(i => i.DescendantNodes().OfType<ConditionalAccessExpressionSyntax>()
                .Any(c => c.Expression.ToString() == "FocusReturn"
                    && c.WhenNotNull is InvocationExpressionSyntax invoke
                    && invoke.Expression.ToString() == ".Invoke"))
            .ToList();
        Assert.True(
            focusCall.Count == 1,
            "expected exactly one `if (...) FocusReturn?.Invoke();` in RemoveBar");
        Assert.Equal(
            decision!.Identifier.ValueText,
            focusCall[0].Condition.ToString());
    }
}
