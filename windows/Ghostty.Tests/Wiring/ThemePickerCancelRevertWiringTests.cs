using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// That a cancelled inline theme picker still reaches the revert.
///
/// The decision itself is not here. It lives in
/// <c>Ghostty.Core.Themes.InlineThemePreviewSession</c> and is unit-tested
/// against the real type, which is worth far more than any assertion about
/// syntax. What no unit test can see is whether MainWindow still asks it:
/// the window is WinUI and cannot be loaded into a test host, so the source
/// is all there is, and a session nobody consults is a green test suite over
/// a feature that reverted nothing.
///
/// Three joints carry it, and each was already wrong once:
///
///   - The picker's callback reports a browse and a choice through the same
///     entry point, distinguished by one bool. The window took the name and
///     dropped the bool -- that IS the defect -- so the flag reaching a
///     window method that branches on it is the first thing to pin.
///   - A cancel is silent. Escape and ^C set should_quit with no final
///     callback at all, so nothing arrives to act on and only
///     <c>ClosePicker</c> -- which every ending funnels through -- can put
///     the colours back.
///   - A preview applied without being recorded is unrevertible, and looks
///     exactly like a recorded one until somebody cancels.
///
/// What this cannot prove: that the revert lands, that ConfigService is
/// willing to apply it, or that the colours it restores are the ones on
/// screen. Those are observable only on a running window.
/// </summary>
public class ThemePickerCancelRevertWiringTests
{
    /// <summary>The window that opens and closes the picker.</summary>
    private const string MainWindowFile = "Ghostty.MainWindow.xaml.cs";

    /// <summary>The field holding the delegate libghostty calls back through.</summary>
    private const string CallbackField = "_inlineThemeCb";

    /// <summary>The window method the callback funnels into.</summary>
    private const string ApplyMethod = "ApplyPickerTheme";

    /// <summary>The single exit every picker ending goes through.</summary>
    private const string CloseMethod = "ClosePicker";

    /// <summary>The run whose snapshot the close restores.</summary>
    private const string SessionField = "_pickerPreview";

    /// <summary>The verb that ends a run and hands back what to restore.</summary>
    private const string EndCall = SessionField + ".End";

    /// <summary>The two verbs that record what a callback meant.</summary>
    private const string ConfirmCall = SessionField + ".NoteConfirm";
    private const string PreviewCall = SessionField + ".NotePreview";

    /// <summary>The apply that puts a snapshot back.</summary>
    private const string RestoreCall = "_configService.ApplyThemeColors";

    /// <summary>The process-wide preview apply, as MainWindow reaches it.</summary>
    private const string PreviewApply = "App.ApplyThemePreview";

    private static ShellSource Window() => ShellSource.Load(MainWindowFile);

    /// <summary>
    /// Whether anything under <paramref name="node"/> invokes the process-wide
    /// preview apply. Matched on a substring of the callee, because the
    /// property is a static reached through a null-conditional and the
    /// qualification a file uses is not the fact being pinned.
    /// </summary>
    private static bool AppliesPreview(SyntaxNode node) =>
        node.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(c => c.CalleeText().Contains(PreviewApply, StringComparison.Ordinal));

    /// <summary>
    /// The lambda assigned to the callback field. Filtered on the right-hand
    /// side being a lambda, because the same field is also assigned null on
    /// three cleanup paths.
    /// </summary>
    private static ParenthesizedLambdaExpressionSyntax CallbackLambda(ShellSource source)
    {
        var lambdas = source.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression))
            .Where(a => a.Left.ToString() == CallbackField)
            .Select(a => a.Right)
            .OfType<ParenthesizedLambdaExpressionSyntax>()
            .ToList();

        Assert.True(
            lambdas.Count == 1,
            $"expected exactly one lambda assigned to {CallbackField}, found {lambdas.Count}; "
            + "everything below is measured against it");
        return lambdas[0];
    }

    /// <summary>
    /// Whether <paramref name="node"/> is reached unconditionally from
    /// <paramref name="scope"/>: no branch of any kind stands between them.
    ///
    /// The queries this file is built out of walk DescendantNodes, so every
    /// one of them sees a statement wrapped in an `if (false)` exactly as it
    /// sees a live one. Presence is not reachability and has to be asked for
    /// separately.
    /// </summary>
    private static bool ReachedUnconditionallyFrom(SyntaxNode node, SyntaxNode scope) =>
        !node.Ancestors()
            .TakeWhile(a => a != scope)
            .Any(a => a is IfStatementSyntax
                        or ConditionalExpressionSyntax
                        or SwitchStatementSyntax
                        or SwitchExpressionSyntax
                        or WhileStatementSyntax
                        or ForStatementSyntax);

    /// <summary>
    /// The callback hands the confirm flag on rather than dropping it.
    ///
    /// This is the defect itself. libghostty fires the same callback for a
    /// browse and for a choice and tells them apart with the second argument;
    /// the window read only the name and applied the theme either way, so
    /// arrowing through the list and pressing Escape left the last previewed
    /// theme applied for good. Drop the argument again and every other rule
    /// here still passes: the session is still consulted, the close still
    /// ends the run -- it just never learns that anything was confirmed, and
    /// now the picker reverts the theme the user chose instead.
    /// </summary>
    [Fact]
    public void TheCallbackCarriesTheConfirmFlagIntoTheWindow()
    {
        var source = Window();
        var lambda = CallbackLambda(source);

        Assert.True(
            lambda.ParameterList.Parameters.Count == 2,
            $"expected the {CallbackField} lambda to take (name, confirmed), found "
            + $"{lambda.ParameterList.Parameters.Count} parameters");
        var flag = lambda.ParameterList.Parameters[1].Identifier.ValueText;

        var handoffs = lambda.Calls(ApplyMethod);
        Assert.True(
            handoffs.Count == 1,
            $"expected the {CallbackField} lambda to funnel into one {ApplyMethod} call, found "
            + $"{handoffs.Count}; the flag has nowhere else to go and a second call site would "
            + "be a second policy");

        var carried = handoffs[0].ArgumentList.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Any(id => id.Identifier.ValueText == flag);
        Assert.True(
            carried,
            $"{ApplyMethod} must be passed the callback's `{flag}` parameter. Without it nothing "
            + "downstream can tell a theme the user browsed past from one they chose, which is "
            + "exactly how a cancelled picker used to leave its last preview applied");

        Assert.True(
            ReachedUnconditionallyFrom(handoffs[0], lambda),
            $"the {ApplyMethod} call must not sit behind a branch inside the callback; the count "
            + "above is satisfied by one wrapped in a condition that never holds, and the picker "
            + "would then apply nothing at all while this file still read as wired");
    }

    /// <summary>
    /// And the window branches on that flag, recording a confirm as a confirm
    /// and a browse as a browse.
    ///
    /// Both arms matter and for different reasons. Lose the confirm arm and a
    /// theme the user accepted is reverted out from under them on close. Lose
    /// the preview arm and no snapshot is ever taken, so the close has
    /// nothing to restore and the original defect is back untouched. Pinned
    /// against the method's own parameter, so replacing the condition with a
    /// constant -- the cheapest way to disable one arm -- fails here rather
    /// than passing as "still branches on something".
    /// </summary>
    [Fact]
    public void TheWindowRecordsWhatEachCallbackMeant()
    {
        var source = Window();
        var method = source.Method(ApplyMethod);

        var flags = method.ParameterList.Parameters
            .Where(p => p.Type?.ToString() == "bool")
            .ToList();
        Assert.True(
            flags.Count == 1,
            $"expected {ApplyMethod} to take exactly one bool, found {flags.Count}; the rule "
            + "below has to know which parameter the branch is about");
        var flag = flags[0].Identifier.ValueText;

        var branches = method.Body!.Statements
            .OfType<IfStatementSyntax>()
            .Where(s => s.Condition is IdentifierNameSyntax id && id.Identifier.ValueText == flag)
            .ToList();
        Assert.True(
            branches.Count == 1,
            $"expected one top-level `if ({flag})` in {ApplyMethod}, found {branches.Count}. A "
            + "top-level statement deliberately: nested inside another branch it is a decision "
            + "that may never be reached, and every Calls() query below would still find both arms");

        var branch = branches[0];
        Assert.True(
            branch.Statement.Calls(ConfirmCall).Count == 1,
            $"the true arm of `if ({flag})` must latch the confirm through {ConfirmCall}, so a "
            + "theme the user accepted survives the close");
        Assert.True(
            branch.Else is { } otherwise && otherwise.Statement.Calls(PreviewCall).Count == 1,
            $"the false arm must record the browse through {PreviewCall}; that call is what takes "
            + "the snapshot the close restores, and without it a cancel has nothing to put back");

        // The apply itself stays out of both arms. Moved inside one, the
        // picker would stop previewing (or stop confirming) while every
        // assertion above still held.
        var applies = method.Body.Statements.Where(AppliesPreview).ToList();
        Assert.True(
            applies.Count == 1,
            $"expected {ApplyMethod} to apply the theme through {PreviewApply} as a top-level "
            + $"statement, found {applies.Count} such statements");
    }

    /// <summary>
    /// The close ends the run and puts back whatever it hands over.
    ///
    /// A cancel never announces itself: Escape and ^C set should_quit and the
    /// picker goes quiet, so there is no callback to hang the revert off.
    /// <c>ClosePicker</c> is the one thing every ending reaches -- the poll
    /// seeing should_quit, the surface being freed under it, a second picker
    /// opening, the window closing -- which is why the revert belongs there
    /// and nowhere else.
    ///
    /// The `is`-pattern shape is pinned, not just the presence of the call:
    /// `if (false &amp;&amp; ... .End() is { } r)` short-circuits, so the run
    /// is never ended, the snapshot is held across the next picker, and the
    /// second run's cancel reverts to colours two themes old.
    /// </summary>
    [Fact]
    public void TheCloseEndsTheRunAndRestoresTheSnapshot()
    {
        var source = Window();
        var statements = source.Method(CloseMethod).Body!.Statements;

        Assert.True(
            source.Root.Calls(EndCall).Count == 1,
            $"expected exactly one {EndCall} in the window: it both reports the revert and resets "
            + "the run, so a second caller either steals the snapshot or clears it");

        var reverts = statements
            .Select((s, i) => (Statement: s, Index: i))
            .Where(x => x.Statement is IfStatementSyntax guard
                        && guard.Condition is IsPatternExpressionSyntax pattern
                        && pattern.Expression is InvocationExpressionSyntax call
                        && call.CalleeText() == EndCall
                        && guard.Statement.Calls(RestoreCall).Count == 1)
            .ToList();
        Assert.True(
            reverts.Count == 1,
            $"expected one top-level `if ({EndCall}() is ...)` in {CloseMethod} whose body calls "
            + $"{RestoreCall}, found {reverts.Count}. Top-level, so the end runs on every close "
            + "rather than only on the branch that still has a live terminal; and the condition "
            + "must be the pattern test itself, because anything ANDed in front of it can "
            + "short-circuit the end away while the call is still right there in the source");

        var bail = statements
            .Select((s, i) => (Statement: s, Index: i))
            .Where(x => x.Statement is IfStatementSyntax guard
                        && guard.Condition.ToString().Contains("_pickerHandle", StringComparison.Ordinal)
                        && guard.Statement.DescendantNodesAndSelf()
                            .OfType<ReturnStatementSyntax>().Any())
            .Select(x => x.Index)
            .DefaultIfEmpty(-1)
            .First();
        Assert.True(
            bail >= 0,
            $"expected {CloseMethod} to return early when no picker is open; without that "
            + "statement there is nothing to measure the revert against");
        Assert.True(
            bail < reverts[0].Index,
            $"the revert must sit below the no-picker return. Above it, every close of a window "
            + "that never opened a picker would run an end -- harmless today, and exactly the "
            + "kind of thing that stops being harmless when the run starts holding more");
    }

    /// <summary>
    /// Nothing applies a preview behind the session's back.
    ///
    /// An apply that is not recorded is an apply the close cannot undo, and
    /// it is invisible until somebody cancels: the theme appears, the picker
    /// behaves, and the revert quietly restores the wrong thing or nothing at
    /// all. Written as a sweep of the whole shell rather than of MainWindow,
    /// because the entry point is a static on App and any file can reach it
    /// -- which is what makes a second call site cheap to add and impossible
    /// to notice.
    /// </summary>
    [Fact]
    public void EveryPreviewApplyGoesThroughTheRecordedPath()
    {
        var offenders = new List<string>();
        var found = 0;

        foreach (var (resource, root) in ShellSource.AllShellSources())
        {
            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var callee = call.CalleeText();
                if (!callee.Contains(PreviewApply, StringComparison.Ordinal)) continue;

                found++;
                var method = call.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                if (resource.EndsWith("." + MainWindowFile, StringComparison.Ordinal)
                    && method?.Identifier.ValueText == ApplyMethod)
                {
                    continue;
                }

                offenders.Add($"{resource}:{method?.Identifier.ValueText ?? "<no method>"}");
            }
        }

        Assert.True(
            found > 0,
            $"no call to {PreviewApply} found anywhere in the shell; this rule stopped matching "
            + "and would pass over any number of unrecorded applies");
        Assert.True(
            offenders.Count == 0,
            $"{PreviewApply} may only be invoked from {MainWindowFile}'s {ApplyMethod}, which "
            + "records the apply against the run first. Found: " + string.Join(", ", offenders));
    }
}
