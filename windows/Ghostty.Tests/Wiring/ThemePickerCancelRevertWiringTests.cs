using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// That a cancelled inline theme picker still reaches the revert, and that
/// the snapshot it reverts to belongs to the process rather than to a window.
///
/// The decision itself is not here. It lives in
/// <c>Ghostty.Core.Themes.InlineThemePreviewSession</c> and is unit-tested
/// against the real type, which is worth far more than any assertion about
/// syntax. What no unit test can see is whether MainWindow still asks it, and
/// whether it asks the one the whole process shares: the window is WinUI and
/// cannot be loaded into a test host, so the source is all there is, and a
/// session nobody consults is a green test suite over a feature that reverted
/// nothing.
///
/// Four joints carry it, and each was already wrong once:
///
///   - The picker's callback reports a browse and a choice through the same
///     entry point, distinguished by one bool. The window took the name and
///     dropped the bool -- that IS the defect -- so the flag reaching a
///     window method that branches on it is the first thing to pin.
///   - The callback is also stamped with the picker it was made for. A
///     callback from a closed picker that is still queued when the next one
///     opens finds a live handle again, and is recorded against a browse it
///     has nothing to do with.
///   - A cancel is silent once the selection has moved. Escape and ^C set
///     should_quit and fall through to a notify that fires only on a change,
///     so after any arrow key nothing arrives to act on and only
///     <c>ClosePicker</c> -- which every ending funnels through -- can put
///     the colours back. On the very first key the notify has never run, so
///     that one cancel does fire a preview; it is dropped by the guard or
///     undone by the revert, which is why the close still decides.
///   - A preview applied without being recorded is unrevertible, and looks
///     exactly like a recorded one until somebody cancels.
///
/// Presence is asserted separately from reachability throughout. Every query
/// below walks DescendantNodes, which sees a call wrapped in a dead branch, or
/// standing under an early return that always fires, exactly as it sees a live
/// one.
///
/// What this cannot prove: that the revert lands, that ConfigService is
/// willing to apply it, or that the colours it restores are the ones on
/// screen. Those are observable only on a running window.
/// </summary>
public class ThemePickerCancelRevertWiringTests
{
    /// <summary>The window that opens and closes the picker.</summary>
    private const string MainWindowFile = "Ghostty.MainWindow.xaml.cs";

    /// <summary>The corpus prefix every embedded shell source is under.</summary>
    private const string Corpus = "Ghostty.Tests.";

    /// <summary>The file that owns the one session, as a corpus tail.</summary>
    private const string AppFile = ".Ghostty.App.xaml.cs";

    /// <summary>The file that drives the same session from the pipe.</summary>
    private const string ServiceFile = ".Ghostty.Services.ThemePreviewService.cs";

    /// <summary>The field holding the delegate libghostty calls back through.</summary>
    private const string CallbackField = "_inlineThemeCb";

    /// <summary>The window method that installs a picker.</summary>
    private const string OpenMethod = "ShowInlineThemePicker";

    /// <summary>The window method the callback funnels into.</summary>
    private const string ApplyMethod = "ApplyPickerTheme";

    /// <summary>The single exit every picker ending goes through.</summary>
    private const string CloseMethod = "ClosePicker";

    /// <summary>The live picker's handle, zero when none is installed.</summary>
    private const string HandleField = "_pickerHandle";

    /// <summary>Counts the pickers this window has opened.</summary>
    private const string RunField = "_pickerRun";

    /// <summary>The type holding the one snapshot slot.</summary>
    private const string SessionType = "InlineThemePreviewSession";

    /// <summary>The session, as MainWindow spells it: App's, not its own.</summary>
    private const string Session = "Ghostty.App.ThemePreviewSession";

    /// <summary>The verb that ends a browse and hands back what to restore.</summary>
    private const string EndCall = Session + ".End";

    /// <summary>The two verbs that record what a callback meant.</summary>
    private const string ConfirmCall = Session + ".NoteConfirm";
    private const string PreviewCall = Session + ".NotePreview";

    /// <summary>The apply that puts a snapshot back.</summary>
    private const string RestoreCall = "_configService.ApplyThemeColors";

    /// <summary>The process-wide preview apply's member name.</summary>
    private const string PreviewApply = "ApplyThemePreview";

    private static ShellSource Window() => ShellSource.Load(MainWindowFile);

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

    /// <summary>Anything that can decide whether the code after it runs.</summary>
    private static bool IsBranch(SyntaxNode node) =>
        node is IfStatementSyntax
            or ConditionalExpressionSyntax
            or SwitchStatementSyntax
            or SwitchExpressionSyntax
            or WhileStatementSyntax
            or ForStatementSyntax
            or ForEachStatementSyntax
            or DoStatementSyntax;

    /// <summary>
    /// Whether <paramref name="node"/> is at least not nested inside a branch
    /// within <paramref name="scope"/>.
    ///
    /// The weaker of the two, and used only where a guard on the callback's
    /// own input legitimately stands in front of the call. It says nothing
    /// about what runs before the node in the same block.
    /// </summary>
    private static bool NotInsideABranch(SyntaxNode node, SyntaxNode scope) =>
        !node.Ancestors().TakeWhile(a => a != scope).Any(IsBranch);

    /// <summary>
    /// Whether <paramref name="node"/> is reached unconditionally from
    /// <paramref name="scope"/>: no branch of any kind stands between them,
    /// containing it or preceding it.
    ///
    /// Both halves are needed and only one of them is obvious. A call nested
    /// in an `if (false)` is the escape everyone thinks of; the cheaper one is
    /// a call left exactly where every query looks for it, under a guard that
    /// always fires:
    ///
    ///     DispatcherQueue.TryEnqueue(() =>
    ///     {
    ///         if (_isClosed) return;   // always true on the close path
    ///         _configService.ApplyThemeColors(...);
    ///     });
    ///
    /// There the apply is a sibling of the `if`, not inside it, so an
    /// ancestor walk finds nothing wrong while the revert is disabled
    /// outright. So a preceding branch in any block up to the scope counts
    /// too. Deliberately blunt -- a harmless `if` in front of the call fails
    /// this as well -- because the alternative is deciding which early exits
    /// are benign, and the disabling one always looks benign.
    ///
    /// A lambda in between is fine, and has to be: the calls this is asked
    /// about are dispatched rather than made inline.
    /// </summary>
    private static bool ReachedUnconditionallyFrom(SyntaxNode node, SyntaxNode scope)
    {
        if (!NotInsideABranch(node, scope)) return false;

        foreach (var step in node.AncestorsAndSelf().TakeWhile(a => a != scope))
        {
            if (step.Parent is not BlockSyntax block) continue;
            if (block.Statements.TakeWhile(s => s != step).Any(IsBranch)) return false;
        }

        return true;
    }

    /// <summary>The last dotted segment of a type name, `?` dropped.</summary>
    private static string LastSegment(string type) =>
        type.Split('.').Last().Trim().TrimEnd('?');

    /// <summary>
    /// Every construction of the session type under <paramref name="root"/>,
    /// in both spellings. A target-typed `new()` never writes the type beside
    /// the `new`, so it is found through the declaration that names it.
    /// </summary>
    private static List<SyntaxNode> Constructions(SyntaxNode root)
    {
        var written = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(n => LastSegment(n.Type.ToString()) == SessionType)
            .Cast<SyntaxNode>();

        var inferred = root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>()
            .Where(n => n.FirstAncestorOrSelf<VariableDeclarationSyntax>() is { } declaration
                && LastSegment(declaration.Type.ToString()) == SessionType)
            .Cast<SyntaxNode>();

        return written.Concat(inferred).ToList();
    }

    /// <summary>
    /// One session for the process, owned by App.
    ///
    /// This is the rule that carries an ownership defect, and it has to be a
    /// count of constructions rather than of readers. What a preview
    /// overwrites is a single palette that ConfigService fans out to every
    /// window, and browses genuinely overlap: a theme request goes to the last
    /// window the user activated, and the pipe is released as soon as the
    /// request is read, so a second invocation lands in a second window while
    /// the first picker is still installed. A session per window therefore had
    /// the second window snapshotting the first window's preview -- a theme
    /// nobody chose -- and the first window's Escape then reverting over a
    /// theme the second had just confirmed. Every rule in this file about the
    /// callback, the arms and the close was green while that was true.
    ///
    /// Checked over the whole corpus twice, by parse and by text, because a
    /// construction parked in a disabled conditional region is invisible to
    /// the parse and ships in the configuration that defines the symbol.
    /// </summary>
    [Fact]
    public void TheProcessHasOneThemePreviewSessionAndAppOwnsIt()
    {
        var corpus = ShellSource.AllUnder(Corpus);

        // Load-bearing: a prefix that stopped matching would pass this test
        // while reading nothing at all.
        Assert.True(
            corpus.Count > 100,
            $"expected the shell source corpus under '{Corpus}', got {corpus.Count} files");
        Assert.Single(corpus, f => f.Tail.EndsWith(AppFile, StringComparison.Ordinal));

        var built = corpus
            .Where(f => Constructions(ShellSource.ParseForCorpusScan(f.Text).Root).Count > 0)
            .Select(f => f.Tail)
            .ToList();

        Assert.True(
            built.Count == 1 && built[0].EndsWith(AppFile, StringComparison.Ordinal),
            $"expected exactly one file to construct {SessionType}, App; found "
            + (built.Count == 0 ? "none" : string.Join(", ", built))
            + ". A second instance is a second snapshot of one palette, and the two then "
            + "revert over each other: the window that never confirmed puts back colours the "
            + "window that did had already replaced.");

        var pattern = new Regex(
            @"\bnew\s+(?:[\w]+\s*\.\s*)*" + SessionType + @"\b"
            + @"|\b" + SessionType + @"\b\??\s+\w+\s*=\s*new\s*\(",
            RegexOptions.Compiled);
        var mentions = corpus
            .SelectMany(f => pattern.Matches(f.Text).Select(_ => f.Tail))
            .ToList();
        Assert.True(
            mentions.Count == 1 && mentions[0].EndsWith(AppFile, StringComparison.Ordinal),
            $"expected one `new {SessionType}` in the whole corpus, found "
            + mentions.Count + ": " + string.Join(", ", mentions));
    }

    /// <summary>
    /// And the pipe path is handed that same session rather than keeping
    /// saved colours of its own.
    ///
    /// The two paths really do overlap. Only a lone theme-list request routes
    /// to the inline picker; every other argument form drives PREVIEW and
    /// CONFIRM over the pipe, against the same palette. When the service kept
    /// its own snapshot, a TUI client could confirm a theme while a picker was
    /// mid-browse and the picker's Escape would then revert over it. So the
    /// service must take the session as a dependency and must not have gone
    /// back to fields of its own.
    /// </summary>
    [Fact]
    public void ThePipePathSharesTheSameSession()
    {
        var service = ShellSource.Load("Ghostty.Services.ThemePreviewService.cs");

        var injected = service.Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .SelectMany(c => c.ParameterList.Parameters)
            .Any(p => p.Type is not null && LastSegment(p.Type.ToString()) == SessionType);
        Assert.True(
            injected,
            $"the pipe server must take a {SessionType} rather than snapshotting the palette "
            + "itself; two snapshotters of one palette is the defect, not an optimisation");

        // And nothing of its own left behind. A revived private snapshot would
        // sit alongside the shared one and silently win on the paths it
        // covers.
        var strays = service.Root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .Select(v => v.Identifier.ValueText)
            .Where(n => n.StartsWith("_saved", StringComparison.Ordinal))
            .ToList();
        Assert.True(
            strays.Count == 0,
            "the pipe server still keeps its own saved colours: "
            + string.Join(", ", strays)
            + ". They are a second snapshot of the same process-wide palette, and they do not "
            + "know the inline picker exists.");

        // And each protocol line records against the session before it
        // applies. The corpus sweep below exempts this file, because this is
        // where the apply is implemented -- so an apply here that recorded
        // nothing would be exactly the unrecorded apply that sweep is about,
        // in the one place it cannot see.
        var loop = service.Method("RunOneServerSession");
        foreach (var (marker, recorder) in new[]
                 { ("PREVIEW:", "NotePreview"), ("CONFIRM:", "NoteConfirm") })
        {
            var arms = loop.DescendantNodes().OfType<IfStatementSyntax>()
                .Where(s => s.Condition.ToString()
                    .Contains("\"" + marker + "\"", StringComparison.Ordinal))
                .ToList();
            Assert.True(
                arms.Count == 1,
                $"expected one branch on a {marker} line in the pipe loop, found {arms.Count}");

            var records = arms[0].Statement.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(c => c.CalleeText().EndsWith(recorder, StringComparison.Ordinal))
                .ToList();
            Assert.True(
                records.Count == 1,
                $"the {marker} branch must record through {recorder} before it applies; without "
                + "it the pipe path drives the palette with nothing tracking what it overwrote, "
                + "and a picker cancel in another window reverts across it");
            Assert.True(
                ReachedUnconditionallyFrom(records[0], arms[0].Statement),
                $"the {recorder} in the {marker} branch must be reached whenever the branch is");

            var applies = arms[0].Statement.Calls("ApplyThemePreview");
            Assert.True(
                applies.Count == 1,
                $"expected one ApplyThemePreview in the {marker} branch, found {applies.Count}");
            Assert.True(
                records[0].SpanStart < applies[0].SpanStart,
                $"{recorder} must come before the apply in the {marker} branch: what the session "
                + "holds has to be what was on screen before this theme went on it");
        }
    }

    /// <summary>
    /// The callback hands the confirm flag on rather than dropping it.
    ///
    /// This is the defect itself. libghostty fires the same callback for a
    /// browse and for a choice and tells them apart with the second argument;
    /// the window read only the name and applied the theme either way, so
    /// arrowing through the list and pressing Escape left the last previewed
    /// theme applied for good. Drop the argument again and every other rule
    /// here still passes: the session is still consulted, the close still
    /// ends the browse -- it just never learns that anything was confirmed,
    /// and now the picker reverts the theme the user chose instead.
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

        // The weaker reachability test, deliberately: the lambda's own
        // `if (name is null) return;` is a guard on the callback's input, not
        // a decision about whether to act on it.
        Assert.True(
            NotInsideABranch(handoffs[0], lambda),
            $"the {ApplyMethod} call must not sit behind a branch inside the callback; the count "
            + "above is satisfied by one wrapped in a condition that never holds, and the picker "
            + "would then apply nothing at all while this file still read as wired");
    }

    /// <summary>
    /// And the callback is stamped with the picker it was made for.
    ///
    /// The handle test in <c>ApplyPickerTheme</c> drops a callback pumped
    /// after the close, but it cannot see the sharper case: a callback from
    /// picker one, still queued when picker two opens, finds a non-zero handle
    /// again and is recorded against the wrong browse. A stale browse makes
    /// the new one snapshot colours that are already a preview's; a stale
    /// confirm empties the slot before the new one has previewed at all, so
    /// its cancel puts nothing back -- the original defect, reproduced through
    /// the fix.
    ///
    /// Pinned end to end: the opener bumps a counter once and outside the
    /// callback, the callback closes over that value, and the apply compares
    /// it. A stamp that is generated inside the callback, or read back off the
    /// field when the callback runs, is always current and proves nothing.
    /// </summary>
    [Fact]
    public void TheCallbackIsStampedWithThePickerItBelongsTo()
    {
        var source = Window();
        var opener = source.Method(OpenMethod);

        var bumps = opener.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
            .Where(u => u.IsKind(SyntaxKind.PreIncrementExpression)
                && u.Operand.ToString() == RunField)
            .ToList();
        Assert.True(
            bumps.Count == 1,
            $"expected exactly one `++{RunField}` in {OpenMethod}, found {bumps.Count}: every "
            + "picker this window opens has to get its own number, and only opening one may "
            + "hand out a new one");

        Assert.True(
            !bumps[0].Ancestors().TakeWhile(a => a != opener).Any(a => a is LambdaExpressionSyntax),
            $"the `++{RunField}` must run when the picker is installed, not inside a lambda that "
            + "runs per callback; a number minted at callback time is never stale");

        var declared = bumps[0].FirstAncestorOrSelf<VariableDeclaratorSyntax>();
        Assert.True(
            declared is not null,
            $"the value of `++{RunField}` must be taken into a local the callback closes over. "
            + $"Reading {RunField} back inside the callback reads whatever the newest picker set "
            + "it to, which is exactly the comparison this is meant to fail");
        var stamp = declared!.Identifier.ValueText;

        var handoff = Assert.Single(CallbackLambda(source).Calls(ApplyMethod));
        Assert.True(
            handoff.ArgumentList.Arguments.Count == 3,
            $"expected {ApplyMethod}(name, confirmed, {stamp}), found "
            + $"{handoff.ArgumentList.Arguments.Count} arguments");
        Assert.True(
            handoff.ArgumentList.Arguments[2].ToString() == stamp,
            $"the callback must pass the captured `{stamp}`, found "
            + $"`{handoff.ArgumentList.Arguments[2]}`");

        // And the apply refuses anything that is not the current picker,
        // before it records or applies anything.
        var method = source.Method(ApplyMethod);
        var numbers = method.ParameterList.Parameters
            .Where(p => p.Type?.ToString() == "int")
            .ToList();
        Assert.True(
            numbers.Count == 1,
            $"expected {ApplyMethod} to take exactly one int, found {numbers.Count}");

        var guard = Assert.IsType<IfStatementSyntax>(method.Body!.Statements[0]);
        Assert.True(
            guard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any(),
            $"the first statement of {ApplyMethod} must be a guard that returns");

        var tested = guard.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Select(id => id.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(
            tested.Contains(HandleField),
            $"the guard must still drop callbacks pumped after the close, by testing {HandleField}");
        Assert.True(
            tested.Contains(RunField) && tested.Contains(numbers[0].Identifier.ValueText),
            $"the guard must compare the callback's `{numbers[0].Identifier.ValueText}` against "
            + $"{RunField}. {HandleField} alone is non-zero again as soon as the next picker is "
            + "installed, so a straggler from the last one walks straight through it");
    }

    /// <summary>
    /// And the window branches on the confirm flag, recording a confirm as a
    /// confirm and a browse as a browse.
    ///
    /// Both arms matter and for different reasons. Lose the confirm arm and a
    /// theme the user accepted is reverted out from under them on close. Lose
    /// the preview arm and no snapshot is ever taken, so the close has
    /// nothing to restore and the original defect is back untouched. Pinned
    /// against the method's own parameter, so replacing the condition with a
    /// constant -- the cheapest way to disable one arm -- fails here rather
    /// than passing as "still branches on something". And each arm's call has
    /// to be reached from that arm, not merely present inside it.
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
        var confirms = branch.Statement.Calls(ConfirmCall);
        Assert.True(
            confirms.Count == 1,
            $"the true arm of `if ({flag})` must latch the confirm through {ConfirmCall}, so a "
            + "theme the user accepted survives the close -- in every window, not just this one");
        Assert.True(
            ReachedUnconditionallyFrom(confirms[0], branch.Statement),
            $"the {ConfirmCall} must be reached whenever the arm is; a branch or an early return "
            + "in front of it disables the confirm while leaving the call exactly where this "
            + "test looks for it");

        Assert.True(
            branch.Else is not null,
            $"`if ({flag})` must have an else arm recording the browse");
        var previews = branch.Else!.Statement.Calls(PreviewCall);
        Assert.True(
            previews.Count == 1,
            $"the false arm must record the browse through {PreviewCall}; that call is what takes "
            + "the snapshot the close restores, and without it a cancel has nothing to put back");
        Assert.True(
            ReachedUnconditionallyFrom(previews[0], branch.Else.Statement),
            $"the {PreviewCall} must be reached whenever the arm is, for the same reason");

        // The apply itself stays out of both arms. Moved inside one, the
        // picker would stop previewing (or stop confirming) while every
        // assertion above still held.
        var applies = method.Body.Statements
            .Where(s => s.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(id => id.Identifier.ValueText == PreviewApply))
            .ToList();
        Assert.True(
            applies.Count == 1,
            $"expected {ApplyMethod} to apply the theme through {PreviewApply} as a top-level "
            + $"statement, found {applies.Count} such statements");
    }

    /// <summary>
    /// The close ends the browse and puts back whatever it hands over.
    ///
    /// A cancel does not announce itself once the selection has moved: Escape
    /// and ^C set should_quit and fall through to a notify that fires only on
    /// a change, so after any arrow key the picker simply goes quiet and there
    /// is no callback to hang the revert off. (On the very first key the
    /// notify has never run, so that one cancel does fire a preview -- which
    /// the guard drops or the revert undoes, so it changes nothing here.)
    /// <c>ClosePicker</c> is the one thing every ending reaches -- the poll
    /// seeing should_quit, the surface being freed under it, a second picker
    /// opening, the window closing -- which is why the revert belongs there
    /// and nowhere else.
    ///
    /// The `is`-pattern shape is pinned, not just the presence of the call:
    /// `if (false &amp;&amp; ... .End() is { } r)` short-circuits, so the
    /// browse is never ended, the snapshot is held across the next picker, and
    /// the next cancel reverts to colours two themes old.
    /// </summary>
    [Fact]
    public void TheCloseEndsTheRunAndRestoresTheSnapshot()
    {
        var source = Window();
        var statements = source.Method(CloseMethod).Body!.Statements;

        Assert.True(
            source.Root.Calls(EndCall).Count == 1,
            $"expected exactly one {EndCall} in the window: it both reports the revert and clears "
            + "the slot, so a second caller either steals the snapshot or drops it");

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

        // And the restore is reached from that body. The revert is dispatched,
        // so the lambda in between is expected and permitted -- but a guard
        // inside it that always holds on the close path disables the whole
        // feature while every count above still reads one.
        var body = ((IfStatementSyntax)reverts[0].Statement).Statement;
        var restore = Assert.Single(body.Calls(RestoreCall));
        Assert.True(
            ReachedUnconditionallyFrom(restore, body),
            $"{RestoreCall} must be reached whenever {EndCall} hands something back. An early "
            + "return inside the dispatched lambda leaves the call present, unnested and "
            + "unreachable, which is the cheapest way to green this file over a revert that "
            + "never runs");

        var bail = statements
            .Select((s, i) => (Statement: s, Index: i))
            .Where(x => x.Statement is IfStatementSyntax guard
                        && guard.Condition.ToString().Contains(HandleField, StringComparison.Ordinal)
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
            + "that never opened a picker would end a browse another window is still in the "
            + "middle of, and revert its snapshot out from under it");
    }

    /// <summary>
    /// Nothing reaches the preview apply outside the two files that record
    /// against the session first.
    ///
    /// An apply that is not recorded is an apply the close cannot undo, and
    /// it is invisible until somebody cancels: the theme appears, the picker
    /// behaves, and the revert quietly restores the wrong thing or nothing at
    /// all. Written as a sweep of the whole shell rather than of MainWindow,
    /// because the entry point is a static on App and any file can reach it
    /// -- which is what makes a second call site cheap to add and impossible
    /// to notice.
    ///
    /// Matched on every mention of the member, not on calls to it. A rule that
    /// looked for invocations whose callee text named it was walked around by
    /// one local:
    ///
    ///     var apply = Ghostty.App.ApplyThemePreview;
    ///     apply?.Invoke(name);
    ///
    /// The callee there is `apply`, and the delegate is a field like any other
    /// -- so the thing to forbid is naming it at all, anywhere but the owner,
    /// the implementation, and the one window method that records first.
    /// </summary>
    [Fact]
    public void EveryPreviewApplyGoesThroughTheRecordedPath()
    {
        var offenders = new List<string>();
        var found = 0;

        foreach (var (resource, root) in ShellSource.AllShellSources())
        {
            // App declares the static and assigns it from the service; the
            // service declares the method and drives it from the pipe loop,
            // recording against the same session as it goes. Those two are
            // the path, not users of it.
            if (resource.EndsWith(AppFile, StringComparison.Ordinal)) continue;
            if (resource.EndsWith(ServiceFile, StringComparison.Ordinal)) continue;

            foreach (var mention in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (mention.Identifier.ValueText != PreviewApply) continue;

                found++;
                var method = mention.FirstAncestorOrSelf<MethodDeclarationSyntax>();
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
            $"no mention of {PreviewApply} found outside its owner files; this rule stopped "
            + "matching and would pass over any number of unrecorded applies");
        Assert.True(
            offenders.Count == 0,
            $"{PreviewApply} may only be named by App, by the service that implements it, and by "
            + $"{MainWindowFile}'s {ApplyMethod}, which records the apply against the session "
            + "first. Found: " + string.Join(", ", offenders));
    }
}
