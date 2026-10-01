using System;
using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Motion;

/// <summary>
/// The pane lifecycle wiring census.
///
/// Every pane-tree transition the shell commits -- a split, a close, an
/// equalize, a zoom toggle, an undo/redo restore, a focus move between
/// tabs -- must be reported to the pane motion surface
/// (<see cref="PaneMotion"/>'s coordinator), and must be reported while
/// it happens: Changing before the tree mutation, Changed after the
/// rebuild that commits it, the focus transfer beside the focus call it
/// reports. Nothing observable may change when no coordinator is
/// registered, so every report sits behind the same guard,
/// <c>if (PaneMotion.Active)</c>, with the report record built inside the
/// guard -- an unobserved shell pays a static-bool branch and allocates
/// nothing.
///
/// The shell assembly cannot be loaded into a test host (PaneHost needs
/// WinUI and the native host), so the site half of that contract is
/// pinned here, on the parsed source, the way the animation census and
/// the other wiring guards work: parse, not substring, so a mutation
/// that keeps the words and drops the wiring changes the tree and reds.
/// The runtime half -- that a registered coordinator receives exactly
/// one Changing/Changed pair, in order, with per-leaf stable ids -- is
/// pinned by PaneMotionLifecycleDispatchTests against the live
/// registration and id seams; the two halves compose into the whole
/// claim, and neither alone is it.
/// </summary>
public class LifecycleHookWiringTests
{
    // The one guard shape a notification call may sit behind. Spelled as
    // an exact condition match, not a contains: `if (!PaneMotion.Active)
    // return;` around the wrong half, or an inverted test, must red.
    private const string Guard = "PaneMotion.Active";

    private const string Changing = "PaneMotion.Current!.OnPaneTreeChanging";
    private const string Changed = "PaneMotion.Current!.OnPaneTreeChanged";
    private const string FocusTransfer = "PaneMotion.Current!.OnFocusTransfer";

    // -- Loaders -----------------------------------------------------------

    private static MethodDeclarationSyntax Method(string file, string name, int paramCount)
    {
        var found = ShellSource.Load(file).Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == name && m.ParameterList.Parameters.Count == paramCount)
            .ToList();
        Assert.True(
            found.Count == 1,
            $"expected one {paramCount}-parameter '{name}' in {file}, found {found.Count}");
        return found[0];
    }

    // -- Shape assertions --------------------------------------------------

    /// <summary>The one call to <paramref name="callee"/> in the body,
    /// with its failure naming the method so a vanished hook reds with a
    /// site, not a count.</summary>
    private static InvocationExpressionSyntax Hook(SyntaxNode body, string callee, string site)
    {
        var found = body.Calls(callee);
        Assert.True(
            found.Count == 1,
            $"{site}: expected exactly one call to '{callee}', found {found.Count}");
        return found[0];
    }

    /// <summary>The hook must sit inside `if (PaneMotion.Active)`: an
    /// unobserved shell pays only a static-bool branch.</summary>
    private static IfStatementSyntax AssertGuarded(InvocationExpressionSyntax hook, string site)
    {
        var guard = hook.Ancestors().OfType<IfStatementSyntax>()
            .FirstOrDefault(i => i.Condition.ToString() == Guard);
        Assert.True(
            guard is not null,
            $"{site}: '{hook.CalleeText()}' is not inside an `if ({Guard})` guard; "
            + "an unobserved shell must pay only a static-bool branch");
        return guard!;
    }

    /// <summary>The guard, plus the report record constructed inside it:
    /// outside the guard the record would allocate on every unobserved
    /// transition, which is the cost this surface promised not to add.</summary>
    private static void AssertGuardedWithRecordInside(InvocationExpressionSyntax hook, string site)
    {
        var guard = AssertGuarded(hook, site);
        var creation = guard.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(c => hook.Span.Contains(c.Span))
            .ToList();
        Assert.True(
            creation.Count > 0,
            $"{site}: the record passed to '{hook.CalleeText()}' is not constructed "
            + "inside the guard; build it inside so an inactive shell allocates nothing");
    }

    /// <summary>The record the hook hands over must be anchored to
    /// <paramref name="changeKind"/>: a copy-pasted hook reporting the
    /// wrong transition kind is exactly the silent wrongness this census
    /// exists to catch, so the kind is read out of the call's own
    /// argument, not assumed from the method it sits in.</summary>
    private static void AssertChangeKind(InvocationExpressionSyntax hook, string changeKind)
    {
        var creation = Assert.IsType<ObjectCreationExpressionSyntax>(hook.ArgExpression(0));
        Assert.True(
            creation.Type.ToString() == "PaneTreeChange",
            $"'{hook.CalleeText()}' must hand over a PaneTreeChange, found '{creation.Type}'");

        // The record's primary constructor takes the kind first, by
        // position: `new PaneTreeChange(PaneTreeChangeKind.Split, ...)`.
        var kind = creation.ArgumentList?.Arguments.FirstOrDefault();
        Assert.True(
            kind is not null,
            $"'{hook.CalleeText()}' builds a PaneTreeChange with no change kind at all");
        Assert.True(
            kind.Expression.ToString() == $"PaneTreeChangeKind.{changeKind}",
            $"'{hook.CalleeText()}' reports kind "
            + $"'{kind.Expression}', expected PaneTreeChangeKind.{changeKind}");
    }

    private static void AssertBefore(SyntaxNode hook, SyntaxNode mutation, string what)
    {
        Assert.True(
            hook.SpanStart < mutation.SpanStart,
            what + " must be reported BEFORE the mutation it precedes");
    }

    private static void AssertAfter(SyntaxNode hook, SyntaxNode commit, string what)
    {
        Assert.True(
            hook.SpanStart > commit.SpanStart,
            what + " must be reported AFTER the commit it follows");
    }

    // -- The sites ---------------------------------------------------------

    [Fact]
    public void Split_ReportsChangingBeforeTheTreeSplit_AndChangedAfterTheVisualCommit()
    {
        var split = Method("Panes.PaneHost.cs", "Split", 2);

        var changing = Hook(split.Body!, Changing, "Split");
        var changed = Hook(split.Body, Changed, "Split");
        AssertGuardedWithRecordInside(changing, "Split");
        AssertGuardedWithRecordInside(changed, "Split");
        AssertChangeKind(changing, "Split");
        AssertChangeKind(changed, "Split");

        // Before: the model mutation is the `_root = PaneTree.Split(...)`
        // assignment; Changing must still see the outgoing tree.
        var treeSplit = split.Body.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Right is InvocationExpressionSyntax invoke
                         && invoke.CalleeText() == "PaneTree.Split");
        AssertBefore(changing, treeSplit, "the Split pre-state");

        // After: the visual commit is the if/else whose branches are the
        // full Rebuild() and the in-place splice -- the last Rebuild() in
        // the method is inside it, so "after that call" is "after the
        // commit". And before the deferred focus: the report is part of
        // the transition, not something the next dispatcher turn carries.
        var lastRebuild = split.Body.Calls("Rebuild")
            .OrderBy(c => c.SpanStart).Last();
        var enqueue = split.Body.Calls("DispatcherQueue.TryEnqueue").Single();
        AssertAfter(changed, lastRebuild, "the Split post-state");
        Assert.True(
            changed.SpanStart < enqueue.SpanStart,
            "the Split post-state is reported after the deferred focus was queued; "
            + "the report belongs to the commit, not to the next dispatcher turn");
    }

    [Fact]
    public void CloseLeaf_ReportsChangingBeforeTheTreeSwap_AndChangedAfterTheRebuild()
    {
        // The close's notifications ride the deferred tail: both close
        // paths (hard and gate-off soft inline, gated soft from the fade's
        // Completed) run FinishClose, and the hooks live there. The link is
        // pinned first, so these hooks cannot detach from the close.
        var close = Method("Panes.PaneHost.cs", "CloseLeaf", 2);
        Assert.Single(close.Calls("FinishClose"));

        var tail = Method("Panes.PaneHost.cs", "FinishClose", 1);
        var changing = Hook(tail.Body!, Changing, "the close tail (FinishClose)");
        var changed = Hook(tail.Body, Changed, "the close tail (FinishClose)");
        AssertGuardedWithRecordInside(changing, "the close tail (FinishClose)");
        AssertGuardedWithRecordInside(changed, "the close tail (FinishClose)");
        AssertChangeKind(changing, "Close");
        AssertChangeKind(changed, "Close");

        // Before: `_root = newRoot;` is the model commit of the close.
        var rootSwap = tail.Body.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_root" && a.Right.ToString() == "newRoot");
        AssertBefore(changing, rootSwap, "the Close pre-state");

        // After: the incremental splice, falling back to a full Rebuild(),
        // is the visual commit.
        var rebuild = tail.Body.Calls("TryIncrementalCloseRebuild").Single();
        AssertAfter(changed, rebuild, "the Close post-state");
    }

    /// <summary>One named argument of the record the hook hands over,
    /// read out of the call's own construction: the facts a site reports
    /// must be spelled at the site, so this pin reads them there.</summary>
    private static ExpressionSyntax NamedArg(InvocationExpressionSyntax hook, string name)
    {
        var creation = Assert.IsType<ObjectCreationExpressionSyntax>(hook.ArgExpression(0));
        var found = creation.ArgumentList!.Arguments
            .Where(a => a.NameColon?.Name.Identifier.ValueText == name)
            .ToList();
        Assert.True(
            found.Count == 1,
            $"'{hook.CalleeText()}' must name '{name}' on the record it builds, "
            + $"found {found.Count}");
        return found[0].Expression;
    }

    [Fact]
    public void SoftClose_ReportsChangingAtThePreVisualPoint_BeforeItsFadeArms()
    {
        var close = Method("Panes.PaneHost.cs", "CloseLeaf", 2);
        var changing = Hook(close.Body!, Changing, "CloseLeaf");
        AssertGuardedWithRecordInside(changing, "CloseLeaf");
        AssertChangeKind(changing, "Close");

        // The report carries what the close's own classifier computed,
        // not a re-derivation: the two named facts are the locals the
        // method holds.
        Assert.Equal("undoable", NamedArg(changing, "Undoable").ToString());
        Assert.Equal("softClose", NamedArg(changing, "SoftClose").ToString());

        // Before the fade arms: this is the pre-visual point. The pane
        // still shows content when the observer is called, and everything
        // that takes it away -- the fade, or the inline cut that replaces
        // the fade when an observer owns the exit -- starts after the
        // report.
        var fade = Assert.Single(close.Calls("StartPaneFade"));
        Assert.True(
            changing.Span.Start < fade.Span.Start,
            "the soft close's Changing must be reported before its fade arms");

        // After the model decision: the classifier's values exist by then.
        // Reporting before PaneTree.Close would name facts nobody computed.
        var modelClose = close.Call("PaneTree.Close");
        Assert.True(
            modelClose.Span.End < changing.Span.Start,
            "the soft close's Changing must be reported after the close is classified");

        // And before the tail: the tree moves in FinishClose, so the
        // pre-visual point cannot be inside it.
        var finish = close.Call("FinishClose");
        Assert.True(
            changing.Span.Start < finish.Span.Start,
            "the soft close's Changing must be reported before the cut runs");
    }

    [Fact]
    public void FinishClose_ReportsTheHardCloseOnly_AndCarriesTheFactsOnBothRecords()
    {
        var tail = Method("Panes.PaneHost.cs", "FinishClose", 1);
        var changing = Hook(tail.Body!, Changing, "FinishClose");
        AssertGuardedWithRecordInside(changing, "FinishClose");
        AssertChangeKind(changing, "Close");

        // The tail's report is the hard close's alone: a soft close
        // reported at its pre-visual point, so the tail's report stands
        // down for a close that already reported. The skip sits around the
        // guarded report, not inside it -- an inner gate would still
        // re-report every soft close.
        var suppression = changing.Ancestors().OfType<IfStatementSyntax>()
            .FirstOrDefault(i => i.Condition.ToString() == "!pending.ChangingRaised");
        Assert.True(
            suppression is not null,
            "the close tail's Changing must be skipped for a close that "
            + "already reported at its pre-visual point");

        // Both records carry the facts the pending held -- the tail reads
        // the classification the close captured, it does not re-derive it.
        var changed = Hook(tail.Body, Changed, "FinishClose");
        AssertChangeKind(changed, "Close");
        Assert.Equal("pending.Undoable", NamedArg(changing, "Undoable").ToString());
        Assert.Equal("pending.SoftClose", NamedArg(changing, "SoftClose").ToString());
        Assert.Equal("pending.Undoable", NamedArg(changed, "Undoable").ToString());
        Assert.Equal("pending.SoftClose", NamedArg(changed, "SoftClose").ToString());
    }

    [Fact]
    public void TheOtherTransitions_NameNeitherCloseFact_OnTheRecord()
    {
        // Every PaneTreeChange construction in the shell names the close
        // facts only on a Close record: the facts describe a close, and a
        // Split or Zoom carrying them tells the observer a close happened
        // that did not. The defaults are half of that: a raiser that does
        // not name them keeps building the record exactly as before.
        var all = ShellSource.AllShellSources()
            .SelectMany(f => f.Root.DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .Where(c => c.Type.ToString() == "PaneTreeChange"))
            .ToList();

        // The sweep enumerates the raisers that exist: a new or removed
        // one has to come through here, not slip past a stale count.
        Assert.True(
            all.Count == 13,
            $"expected 13 PaneTreeChange constructions in the shell (the close's "
            + "pre-visual report, the close tail's pair, and the other "
            + $"transitions' pair), found {all.Count}");

        var namingFacts = all.Where(c => c.ArgumentList!.Arguments.Any(
                a => a.NameColon?.Name.Identifier.ValueText is "Undoable" or "SoftClose"))
            .ToList();
        Assert.True(
            namingFacts.Count == 3,
            $"expected exactly 3 constructions naming the close facts (the "
            + $"close's three records), found {namingFacts.Count}");
        foreach (var creation in namingFacts)
        {
            var kind = creation.ArgumentList!.Arguments.FirstOrDefault()?.ToString();
            Assert.True(
                kind == "PaneTreeChangeKind.Close",
                $"a {kind} record names a close fact; the facts describe a "
                + "close, and no other transition carries them");
        }
    }

    [Fact]
    public void TheDeferredClose_CarriesWhetherThePreVisualReportRan()
    {
        var close = Method("Panes.PaneHost.cs", "CloseLeaf", 2);

        // Both PendingClose constructions carry the local that answers
        // "did this close report". The fade route is reached only while no
        // observer stands -- where the pre-visual report provably did not
        // run -- so a literal there would strand the pair the moment a
        // coordinator registered inside the fade's life: the tail would
        // skip its Changing and deliver Changed alone.
        var pendings = close.Body!.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(c => c.Type.ToString() == "PendingClose")
            .ToList();
        Assert.True(
            pendings.Count == 2,
            $"expected both close paths' PendingClose constructions, found {pendings.Count}");
        foreach (var pending in pendings)
        {
            var flag = pending.ArgumentList!.Arguments
                .Single(a => a.NameColon?.Name.Identifier.ValueText == "ChangingRaised")
                .Expression;
            Assert.Equal("changingRaised", flag.ToString());
        }
    }

    [Fact]
    public void EqualizeSplits_ReportsThePair_AroundTheRatioReset()
    {
        var equalize = Method("Panes.PaneHost.cs", "EqualizeSplits", 0);

        var changing = Hook(equalize.Body!, Changing, "EqualizeSplits");
        var changed = Hook(equalize.Body, Changed, "EqualizeSplits");
        AssertGuardedWithRecordInside(changing, "EqualizeSplits");
        AssertGuardedWithRecordInside(changed, "EqualizeSplits");
        AssertChangeKind(changing, "Equalize");
        AssertChangeKind(changed, "Equalize");

        // The pure model op is the mutation; the pair brackets it so a
        // zoomed equalize (which defers the visual apply) still reports a
        // closed pair instead of a Changing that never commits.
        var modelOp = equalize.Body.Calls("PaneTree.Equalize").Single();
        AssertBefore(changing, modelOp, "the Equalize pre-state");
        AssertAfter(changed, modelOp, "the Equalize post-state");
    }

    [Fact]
    public void ToggleSplitZoom_ReportsOneChanging_AndOneChangedPerExitPath()
    {
        var toggle = Method("Panes.PaneHost.cs", "ToggleSplitZoom", 0);

        var changing = Hook(toggle.Body!, Changing, "ToggleSplitZoom");
        AssertGuardedWithRecordInside(changing, "ToggleSplitZoom");
        AssertChangeKind(changing, "Zoom");

        // Before the branch dispatch: both directions of the toggle are
        // still in front of the method at that point.
        var dispatch = toggle.Body.Calls("CaptureForUndo").Single();
        AssertBefore(changing, dispatch, "the Zoom pre-state");

        // Three exits commit a zoom transition: the lost-slot fallback
        // (full Rebuild, then return), the unzoom end and the zoom end.
        // Each must carry its own Changed -- an exit without one leaves a
        // Changing permanently uncommitted.
        var changed = toggle.Body.Calls(Changed);
        Assert.True(
            changed.Count == 3,
            $"ToggleSplitZoom: expected exactly 3 '{Changed}' calls (one per exit path), "
            + $"found {changed.Count}");
        foreach (var call in changed)
        {
            AssertGuardedWithRecordInside(call, "ToggleSplitZoom");
            AssertChangeKind(call, "Zoom");
        }

        // The fallback's Changed comes after that path's Rebuild(). The
        // comparison walks past the guard's own block: the hook lives
        // inside `if (PaneMotion.Active)`, and the block that matters is
        // the one holding that guard statement.
        var fallbackRebuild = toggle.Body.Calls("Rebuild").Single();
        var fallbackChanged = changed.Single(c =>
            EnclosingBlock(AssertGuarded(c, "ToggleSplitZoom")) == EnclosingBlock(fallbackRebuild));
        AssertAfter(fallbackChanged, fallbackRebuild, "the zoom fallback post-state");
    }

    [Fact]
    public void RestoreFrom_ReportsChangingBeforeTheRestoredTree_AndChangedAfterItsRebuild()
    {
        var restore = Method("Panes.PaneHost.cs", "RestoreFrom", 1);

        var changing = Hook(restore.Body!, Changing, "RestoreFrom");
        var changed = Hook(restore.Body, Changed, "RestoreFrom");
        AssertGuardedWithRecordInside(changing, "RestoreFrom");
        AssertGuardedWithRecordInside(changed, "RestoreFrom");
        AssertChangeKind(changing, "Restore");
        AssertChangeKind(changed, "Restore");

        // Before: `_root = snapshot.Root;` installs the restored model.
        var install = restore.Body.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_root" && a.Right.ToString() == "snapshot.Root");
        AssertBefore(changing, install, "the Restore pre-state");

        // After: the full visual rebuild from the restored tree -- and
        // before the re-zoom decision, which reports its own Zoom pair.
        var rebuild = restore.Body.Calls("Rebuild").Single();
        AssertAfter(changed, rebuild, "the Restore post-state");
        var rezoom = restore.Body.Calls("ToggleSplitZoom").Single();
        Assert.True(
            changed.SpanStart < rezoom.SpanStart,
            "the Restore post-state is reported after the re-zoom ran; the re-zoom "
            + "reports its own Zoom pair and the Restore pair must close first");
    }

    [Fact]
    public void FocusActiveLeaf_ReportsTheTransfer_BesideTheFocusCallItReports()
    {
        var focus = Method("MainWindow.xaml.cs", "FocusActiveLeaf", 0);

        var transfer = Hook(focus.Body!, FocusTransfer, "FocusActiveLeaf");
        AssertGuardedWithRecordInside(transfer, "FocusActiveLeaf");

        // The notification lives in the deferred focus lambda, not beside
        // the enqueue: the transfer happens where focus is set, one
        // dispatcher turn later, and reporting it anywhere earlier would
        // describe a move that has not happened yet.
        var focusCall = focus.Body.Calls("leaf.Terminal().Focus").Single();
        Assert.True(
            transfer.SpanStart > focusCall.SpanStart,
            "the focus transfer must be reported after the Focus call it reports");

        // The reported leaf is the focused leaf -- the same identifier the
        // Focus receiver uses, so the two cannot drift apart unnoticed.
        var toLeaf = Assert.IsType<ObjectCreationExpressionSyntax>(transfer.ArgExpression(0))
            .ArgumentList.Arguments
            .Single(a => a.NameColon is { Name.Identifier.Text: "ToLeafId" })
            .Expression.ToString();
        Assert.True(
            toLeaf.Contains("PaneMotionIds.Of(leaf)", StringComparison.Ordinal),
            $"the FocusTransfer reports ToLeafId as '{toLeaf}', not the focused leaf "
            + "'PaneMotionIds.Of(leaf)'; the notified move and the requested "
            + "Focus must name the same pane");
    }

    [Fact]
    public void LayoutCoordinatorCtor_ConsultsTheSurface_BesideTheMotionEnabledRead()
    {
        var ctors = ShellSource.Load("Shell.LayoutCoordinator.cs").Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Where(c => c.Identifier.ValueText == "LayoutCoordinator")
            .ToList();
        Assert.True(ctors.Count == 1, $"expected one LayoutCoordinator ctor, found {ctors.Count}");
        var ctor = ctors[0];

        // Non-vacuity: today's motion read is still there beside the hook.
        var motionRead = ctor.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_motionEnabled");

        var consult = Hook(ctor.Body!, "PaneMotion.Current!.ResolvePolicy", "LayoutCoordinator ctor");
        AssertGuarded(consult, "LayoutCoordinator ctor");
        Assert.True(
            consult.Arg(0) == "MotionSurfaceClass.PaneGeometry",
            $"the ctor consults the surface for '{consult.Arg(0)}', expected "
            + "MotionSurfaceClass.PaneGeometry");
        AssertAfter(consult, motionRead, "the surface consultation");
    }

    // -- The scanner's own teeth --------------------------------------------

    /// <summary>
    /// The ordering pins above are span comparisons, and a span comparison
    /// that can never fail is the guard class this repo keeps having to
    /// bury. Fed the exact defect they exist for -- a pair reported in the
    /// wrong order around the mutation -- the same helper must fail, and
    /// must pass on the correctly ordered twin.
    /// </summary>
    [Fact]
    public void TheOrderingPin_CatchesAHookPairReportedInTheWrongOrder()
    {
        const string reversed = """
            using Ghostty.Motion;
            public sealed class Probe
            {
                private int _root;
                public void Go()
                {
                    if (PaneMotion.Active)
                    {
                        PaneMotion.Current!.OnPaneTreeChanged(new PaneTreeChange(
                            PaneTreeChangeKind.Split));
                    }
                    _root = Mutate();
                    if (PaneMotion.Active)
                    {
                        PaneMotion.Current!.OnPaneTreeChanging(new PaneTreeChange(
                            PaneTreeChangeKind.Split));
                    }
                }
                private int Mutate() => 0;
            }
            """;

        var method = ShellSource.ParseForCorpusScan(reversed).Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "Go");
        var changing = Hook(method.Body!, Changing, "Probe");
        var changed = Hook(method.Body, Changed, "Probe");
        var mutation = method.Body.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_root");

        // The reversed pair: Changed claims "before" (it is not), Changing
        // claims "after" (it is not). Both must trip.
        var beforeFailure = Record.Exception(new Action(() => AssertBefore(changing, mutation, "probe")));
        var afterFailure = Record.Exception(new Action(() => AssertAfter(changed, mutation, "probe")));
        Assert.NotNull(beforeFailure);
        Assert.NotNull(afterFailure);

        // And the correctly ordered twin passes the same two asserts.
        const string ordered = """
            using Ghostty.Motion;
            public sealed class Probe
            {
                private int _root;
                public void Go()
                {
                    if (PaneMotion.Active)
                    {
                        PaneMotion.Current!.OnPaneTreeChanging(new PaneTreeChange(
                            PaneTreeChangeKind.Split));
                    }
                    _root = Mutate();
                    if (PaneMotion.Active)
                    {
                        PaneMotion.Current!.OnPaneTreeChanged(new PaneTreeChange(
                            PaneTreeChangeKind.Split));
                    }
                }
                private int Mutate() => 0;
            }
            """;
        var orderedMethod = ShellSource.ParseForCorpusScan(ordered).Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "Go");
        var orderedChanging = Hook(orderedMethod.Body!, Changing, "Probe");
        var orderedChanged = Hook(orderedMethod.Body, Changed, "Probe");
        var orderedMutation = orderedMethod.Body.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_root");
        AssertBefore(orderedChanging, orderedMutation, "probe");
        AssertAfter(orderedChanged, orderedMutation, "probe");
    }

    private static BlockSyntax EnclosingBlock(SyntaxNode node) =>
        node.Ancestors().OfType<BlockSyntax>().First();
}
