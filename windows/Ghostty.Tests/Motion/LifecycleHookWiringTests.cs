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
        var close = Method("Panes.PaneHost.cs", "CloseLeaf", 2);

        var changing = Hook(close.Body!, Changing, "CloseLeaf");
        var changed = Hook(close.Body, Changed, "CloseLeaf");
        AssertGuardedWithRecordInside(changing, "CloseLeaf");
        AssertGuardedWithRecordInside(changed, "CloseLeaf");
        AssertChangeKind(changing, "Close");
        AssertChangeKind(changed, "Close");

        // Before: `_root = newRoot;` is the model commit of the close.
        var rootSwap = close.Body.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_root" && a.Right.ToString() == "newRoot");
        AssertBefore(changing, rootSwap, "the Close pre-state");

        // After: the incremental splice, falling back to a full Rebuild(),
        // is the visual commit.
        var rebuild = close.Body.Calls("TryIncrementalCloseRebuild").Single();
        AssertAfter(changed, rebuild, "the Close post-state");
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
