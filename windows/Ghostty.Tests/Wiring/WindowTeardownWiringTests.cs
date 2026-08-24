using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Every event MainWindow subscribes to, checked against what its close
/// takes back.
///
/// Four separate callbacks accumulated behind this gap -- an OS colour-scheme
/// handler calling into libghostty through the app pointer the close is about
/// to free, and three dispatcher-driven timers nothing stopped -- and each was
/// found by hand, one at a time, because nothing enumerated the subscriptions
/// against the unsubscribe list. Sixty-odd `+=` against a handful of `-=` is
/// not a list anyone re-reads.
///
/// The discrimination that matters is who owns the event source:
///
///   - A source that outlives the window -- the OS, an app-level service, a
///     static event, a timer the window created but the dispatcher drives --
///     keeps calling after Window.Closed, and keeps the closed window alive
///     while it does. It must be unsubscribed in OnClosedAsync, or its handler
///     must return early on _isClosed.
///   - A source the window itself owns and that dies with it -- its own XAML
///     elements, its per-window services, its pane tree -- needs neither.
///
/// There is no semantic model here (see ShellSource), so ownership cannot be
/// inferred; it has to be declared. <see cref="WindowOwned"/> is that
/// declaration, and it is an allow-list rather than a list of sources known to
/// need proof, because the failure mode being defended against is a
/// subscription NOBODY classified. A deny-list is silent on the receiver
/// introduced next week, which is precisely how these four arrived. An
/// allow-list makes the new receiver fail until someone writes down which side
/// of the line it falls on, and that sentence is the whole product of this
/// test.
///
/// What it cannot see: whether a gated handler's gate covers the turns after
/// an await, whether an unsubscribe runs on every path through OnClosedAsync,
/// and every subscription made outside this file. It is a census, not a proof
/// of safety. The per-case guards below carry the reasoning those need.
/// </summary>
public class WindowTeardownWiringTests
{
    private static ShellSource Window() => ShellSource.Load("Ghostty.MainWindow.xaml.cs");

    /// <summary>
    /// Receivers whose events die with the window, and why. Adding a line here
    /// is meant to cost a decision: the claim is that when this window is gone
    /// nothing can raise the event again, so neither an unsubscribe nor a gate
    /// buys anything.
    /// </summary>
    private static readonly (string Receiver, string Why)[] WindowOwned =
    {
        ("this", "the Window's own events; the window raises them and stops when it does"),
        ("fe", "this window's XAML content root"),
        ("CommandPalettePopup", "named element of MainWindow.xaml"),
        ("TabOverviewHost", "named element of MainWindow.xaml"),
        ("TabOverviewUI", "named element of MainWindow.xaml"),
        ("_commandPaletteVm", "per-window view model, constructed and held only by this window"),
        ("_router", "per-window keybind router, constructed by this window"),
        // Judged against when the surfaces stop being routed to, not against
        // Dispose: GhosttyHost.Dispose is the last statement of the close, so
        // these stay attached across the whole teardown. What makes them safe
        // is that libghostty routes them from surface input, which a closing
        // window no longer receives.
        ("_host", "this window's own GhosttyHost; its events are raised from surface input, "
                  + "which a closing window no longer receives"),
        ("_tabManager", "this window's own TabManager"),
        ("_themeManager", "per-window; OnClosedAsync disposes it before the first await, "
                          + "and WindowThemeManager.Dispose nulls ThemeChanged"),
        ("seedHost.ActiveLeaf.Terminal()", "a leaf of this window's pane tree; "
                                           + "OnLaunchSurfaceFirstRender detaches itself on the first fire"),
        ("paneHost", "a pane host of this window; RemovePaneHost detaches it"),
        ("tab.PaneHost", "a pane host of this window; DetachProcessTracking detaches it"),
        ("((INotifyPropertyChanged)tab)", "a tab model of this window; UnwireTabColor detaches it"),
        ("newWindow", "another window's Closed, handled by App, holding nothing of this window"),
        ("aboutWin", "the About window, which OnClosedAsync closes before it tears anything down"),
        ("window", "the inspector window, which OnClosedAsync closes; its Closed handler "
                   + "detaches the two subscriptions taken out alongside it"),
    };

    /// <summary>
    /// Individual events on sources that DO outlive the window and are still
    /// left attached, each with the reason detaching or gating would be wrong.
    /// Spelled as whole events, not receivers, so the rest of the receiver
    /// stays under the rule.
    /// </summary>
    private static readonly (string Event, string Why)[] Exempt =
    {
        ("AppWindow.Closing", "runs during the close by design: it is what turns a quake close "
                              + "into a hide. _isClosed is still false when it fires"),
        // These two are exempt because a gate is the wrong instrument, not
        // because they are harmless. The subscription is guarded on seedTab
        // being null, and only the tab-adoption path passes one, so the
        // cold-start window, the quake window and every restored or reopened
        // window all subscribe: the action runs once per window, and each
        // closure captures this window and keeps it rooted on the bootstrap
        // host for the life of the process. Detaching on close would leave a
        // multi-window session with nobody handling the action at all. The fix
        // is to own these where the bootstrap host lives rather than per
        // window, which is a behaviour change and is filed separately.
        ("appHost.OpenConfigRequested", "every non-adopted window subscribes; duplicate execution "
                                        + "and a per-window leak, fixed by moving ownership to App "
                                        + "rather than by a gate"),
        ("appHost.ReloadConfigRequested", "same ownership problem as OpenConfigRequested"),
    };

    private sealed record Subscription(string Event, string Receiver, string Handler, ExpressionSyntax Rhs);

    /// <summary>All whitespace removed, so an event or handler wrapped across
    /// lines compares equal to the same one written inline.</summary>
    private static string Flatten(SyntaxNode node) =>
        new string(node.ToString().Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>
    /// The receiver an event is reached through, as the source spells it, or
    /// "this" for a bare event name.
    /// </summary>
    private static string ReceiverOf(ExpressionSyntax left) =>
        left is MemberAccessExpressionSyntax member ? Flatten(member.Expression) : "this";

    /// <summary>
    /// Compound assignment is also arithmetic, so the right-hand side decides:
    /// a handler is a lambda, a method group, a qualified method group, or an
    /// explicitly constructed delegate, and nothing else is counted.
    /// </summary>
    private static bool IsHandler(ExpressionSyntax rhs) =>
        rhs is LambdaExpressionSyntax
            or IdentifierNameSyntax
            or MemberAccessExpressionSyntax
            or ObjectCreationExpressionSyntax
        || (rhs is ParenthesizedExpressionSyntax paren && IsHandler(paren.Expression));

    private static List<Subscription> Assignments(SyntaxNode scope, SyntaxKind kind) =>
        scope.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(kind) && IsHandler(a.Right))
            .Select(a => new Subscription(Flatten(a.Left), ReceiverOf(a.Left), Flatten(a.Right), a.Right))
            .ToList();

    private static List<Subscription> Subscriptions() =>
        Assignments(Window().Root, SyntaxKind.AddAssignmentExpression);

    private static List<Subscription> Unsubscribes() =>
        Assignments(Window().Method("OnClosedAsync"), SyntaxKind.SubtractAssignmentExpression);

    /// <summary>
    /// <c>if (_isClosed) ... return;</c> and nothing that merely mentions the
    /// field: an inverted or dead condition is a different syntax shape and
    /// does not match. Deliberately the same shape TabLayoutSwitchWiringTests
    /// pins on the layout-switch completion -- the two have to agree on what
    /// counts as a gate, or the file grows two kinds.
    /// </summary>
    private static bool IsClosedGuard(StatementSyntax statement) =>
        statement is IfStatementSyntax { Condition: IdentifierNameSyntax { Identifier.Text: "_isClosed" } } guard
        && guard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any();

    /// <summary>
    /// Whether the handler bails once teardown has started, looking only at
    /// the handler's own top-level statements. A gate buried inside another
    /// branch guards that branch, not the handler, and a gate one call deeper
    /// is a fact about the callee that this cannot see.
    /// </summary>
    private static bool IsGated(Subscription subscription, ShellSource source)
    {
        var body = subscription.Rhs switch
        {
            // An expression-bodied lambda has no statement to gate on.
            LambdaExpressionSyntax lambda => lambda.Block,
            // A method group: read the method it names. Overloaded or absent,
            // there is no single body to read and nothing is proven.
            IdentifierNameSyntax name => Bodies(source, name.Identifier.ValueText) is [var only]
                ? only
                : null,
            _ => null,
        };
        if (body is null) return false;

        // Position matters, not mere presence: a gate below the work it is
        // meant to prevent reads as present while the handler still runs
        // against freed state on every tick, and that shape satisfies an
        // Any() check.
        //
        // Requiring it to be statement zero is too strong, though. Stopping
        // the timer first, or bailing out because this tick belongs to a
        // superseded one, are both legitimate and neither can touch anything
        // the gate protects. So the rule is that the gate must come before
        // the first statement that does work, where a preceding early-return
        // guard or a Stop call is not work.
        var statements = body.Statements;
        var gate = statements.Select((st, i) => (st, i)).FirstOrDefault(x => IsClosedGuard(x.st));
        if (gate.st is null) return false;

        var firstWorking = statements.TakeWhile(IsPrelude).Count();
        return gate.i <= firstWorking;
    }

    /// <summary>
    /// A statement that cannot touch what the teardown gate protects: an
    /// early-return guard, or stopping a timer.
    /// </summary>
    private static bool IsPrelude(StatementSyntax statement)
    {
        if (statement is IfStatementSyntax guard)
            return guard.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any();

        return statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call }
            && call.CalleeText().EndsWith(".Stop", StringComparison.Ordinal);
    }

    private static List<BlockSyntax?> Bodies(ShellSource source, string method)
    {
        return source.Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == method)
            .Select(m => m.Body)
            .ToList();
    }

    [Fact]
    public void EverySubscriptionIsOwnedByTheWindowOrSurvivesItsClose()
    {
        var source = Window();
        var subscriptions = Subscriptions();

        // Load-bearing. Every assertion below is over this list, so a query
        // that stops matching passes the whole test while reading nothing.
        Assert.True(
            subscriptions.Count > 40,
            $"expected MainWindow's event subscriptions to be found, got {subscriptions.Count}");

        var owned = WindowOwned.Select(x => x.Receiver).ToHashSet(StringComparer.Ordinal);
        var exempt = Exempt.Select(x => x.Event).ToHashSet(StringComparer.Ordinal);
        var detached = Unsubscribes()
            .Select(u => (u.Event, u.Handler))
            .ToHashSet();

        // The pair, not the event alone: MainWindow attaches two different
        // handlers to _configService.ConfigChanged, and matching on the event
        // would let either `-=` answer for both.
        var mustProve = subscriptions
            .Where(s => !owned.Contains(s.Receiver) && !exempt.Contains(s.Event))
            .ToList();
        var unproven = mustProve
            .Where(s => !detached.Contains((s.Event, s.Handler)) && !IsGated(s, source))
            .Select(s => s.Event)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unproven.Count == 0,
            "these subscriptions are to sources that outlive the window and are neither "
            + "unsubscribed in OnClosedAsync nor gated on _isClosed, so they keep firing at, and "
            + "keep alive, a window that is tearing down: "
            + string.Join(", ", unproven)
            + ". Unsubscribe it, gate the handler on _isClosed, or -- if the receiver really does "
            + "die with the window -- add it to WindowOwned with the reason.");

        // Both ways of satisfying the rule have to be live. Without this, a
        // regression that classified everything as detached (or everything as
        // gated) would leave the other branch untested and silently rotten.
        Assert.Contains(mustProve, s => detached.Contains((s.Event, s.Handler)));
        Assert.Contains(mustProve, s => IsGated(s, source));
    }

    /// <summary>
    /// The allow-list has to stay a description of the file. An entry whose
    /// receiver no longer subscribes to anything is a claim nobody is checking,
    /// and the next receiver to be spelled that way inherits an exemption
    /// nobody granted it.
    /// </summary>
    [Fact]
    public void TheOwnershipDeclarationsStillDescribeTheFile()
    {
        var subscriptions = Subscriptions();
        var receivers = subscriptions.Select(s => s.Receiver).ToHashSet(StringComparer.Ordinal);
        var events = subscriptions.Select(s => s.Event).ToHashSet(StringComparer.Ordinal);

        var staleOwners = WindowOwned.Where(x => !receivers.Contains(x.Receiver)).Select(x => x.Receiver);
        var staleExempt = Exempt.Where(x => !events.Contains(x.Event)).Select(x => x.Event);
        var stale = staleOwners.Concat(staleExempt).ToList();

        Assert.True(
            stale.Count == 0,
            "these entries no longer match any subscription in MainWindow and should be removed, "
            + "so the list keeps meaning what it says: " + string.Join(", ", stale));

        Assert.All(WindowOwned, x => Assert.False(string.IsNullOrWhiteSpace(x.Why)));
        Assert.All(Exempt, x => Assert.False(string.IsNullOrWhiteSpace(x.Why)));
    }

    /// <summary>
    /// The OS colour-scheme handler, pinned on its own because the census above
    /// is satisfied by either half and this one needs both.
    ///
    /// Unsubscribing is what stops UISettings calling and what stops it holding
    /// the closed window. It is not enough on its own: the handler hops to the
    /// dispatcher, so a flip that arrives just before the close leaves the real
    /// work queued for a later turn, and by then _host.Dispose has freed the app
    /// pointer AppSetColorScheme is handed. The gate inside the enqueued body is
    /// the only thing that turns that one away.
    /// </summary>
    [Fact]
    public void TheSystemColorSchemeHandlerIsBothDetachedAndGated()
    {
        var source = Window();
        const string Event = "_systemUiSettings.ColorValuesChanged";
        const string Handler = "OnSystemColorValuesChanged";

        // Named, not inline: an anonymous lambda cannot be unsubscribed at all.
        var subscribed = Subscriptions().Where(s => s.Event == Event).ToList();
        Assert.True(
            subscribed.Count == 1 && subscribed[0].Handler == Handler,
            $"expected one `{Event} += {Handler}`; a lambda here cannot be detached");
        Assert.Contains((Event, Handler), Unsubscribes().Select(u => (u.Event, u.Handler)));

        var method = source.Method(Handler);
        Assert.True(
            method.Body!.Statements.Any(IsClosedGuard),
            $"{Handler} must bail on _isClosed before it reads anything");

        // The enqueued body is the half that runs late, and it is a separate
        // scope: the entry gate above says nothing about the turn it runs on.
        var enqueued = method.Call("DispatcherQueue.TryEnqueue").ArgumentList.Arguments[0].Expression;
        var queued = Assert.IsType<ParenthesizedLambdaExpressionSyntax>(enqueued).Block!.Statements;

        var gateIndex = queued.TakeWhile(s => !IsClosedGuard(s)).Count();
        var callIndex = queued
            .TakeWhile(s => !s.Calls("Ghostty.Interop.NativeMethods.AppSetColorScheme").Any())
            .Count();

        // Both have to exist, or TakeWhile silently returns the full count and
        // the comparison passes while pinning nothing.
        Assert.True(
            gateIndex < queued.Count,
            "the dispatched body must re-check _isClosed; it runs a turn after the handler that "
            + "queued it, and the close disposes _host in between");
        Assert.True(callIndex < queued.Count, "expected the dispatched body to set the app colour scheme");
        Assert.True(gateIndex < callIndex, "the gate must precede the call through _host.App");
    }

    /// <summary>
    /// The three dispatcher-driven timers.
    ///
    /// Stopping them is what the gates cannot do. A gated tick still arrives
    /// every 50ms, 500ms or 1200ms for as long as the timer runs, and the timer
    /// runs against the DispatcherQueue, which outlives the window: the theme
    /// picker's poll ends only when the picker reports should_quit, which no
    /// key can ever set once the surfaces are freed, so it would poll for the
    /// life of the process.
    ///
    /// ClosePicker is the one that must also come before the teardown rather
    /// than merely inside it -- its deinit writes through the picker's surface,
    /// which DisposeAllLeaves frees -- so its position is pinned too.
    /// </summary>
    [Fact]
    public void TheDispatcherDrivenTimersAreStoppedByTheClose()
    {
        var statements = Window().Method("OnClosedAsync").Body!.Statements;

        int IndexOfCall(string call) => statements.TakeWhile(s => !s.Calls(call).Any()).Count();

        var picker = IndexOfCall("ClosePicker");
        var popup = IndexOfCall("_cyclePopupTimer?.Stop");
        var detach = IndexOfCall("DetachProcessTracking");
        var leaves = IndexOfCall("t.PaneHost.DisposeAllLeaves");

        Assert.True(picker < statements.Count, "OnClosedAsync must stop and hand back the theme picker");
        Assert.True(popup < statements.Count, "OnClosedAsync must stop the Ctrl+Tab popup timer");
        Assert.True(
            detach < statements.Count,
            "OnClosedAsync must detach process tracking for every tab; teardown frees the leaves "
            + "without removing a tab, so TabRemoved -- the only other caller -- never fires");
        Assert.True(leaves < statements.Count, "expected OnClosedAsync to free the panes");

        Assert.True(
            picker < leaves,
            "ClosePicker must run before the leaves are freed: the deinit restores the surface's "
            + "input redirects and writes to its pty, and DisposeAllLeaves has freed that surface");
        Assert.True(
            detach < leaves,
            "the shell-pid polls must stop before the leaves they query are freed");

        // Each tick is gated as well, for the one already queued when the stop
        // lands. Read off the subscriptions rather than by searching the file,
        // so a tick handler moved elsewhere is still the one being checked.
        var source = Window();
        var subscriptions = Subscriptions();
        // The picker poll subscribes through the local it captures rather than
        // through the field, so that a tick queued by a previous poll cannot
        // stop the one that replaced it. That is why this names poll.Tick.
        foreach (var timer in new[] { "poll.Tick", "_cyclePopupTimer.Tick", "pidPoll.Tick", "startTimer.Tick" })
        {
            var ticks = subscriptions.Where(s => s.Event == timer).ToList();
            Assert.True(ticks.Count == 1, $"expected one {timer} subscription, found {ticks.Count}");
            Assert.True(
                IsGated(ticks[0], source),
                $"{timer}'s handler must return early on _isClosed: Stop does not recall a tick "
                + "already queued on the dispatcher");
        }
    }
}
