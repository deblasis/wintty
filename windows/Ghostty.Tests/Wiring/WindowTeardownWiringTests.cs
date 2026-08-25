using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Every event each window subscribes to, checked against what its close
/// takes back.
///
/// Four separate callbacks accumulated behind this gap in MainWindow -- an OS
/// colour-scheme handler calling into libghostty through the app pointer the
/// close is about to free, and three dispatcher-driven timers nothing stopped
/// -- and each was found by hand, one at a time, because nothing enumerated
/// the subscriptions against the unsubscribe list. Sixty-odd `+=` against a
/// handful of `-=` is not a list anyone re-reads.
///
/// The discrimination that matters is who owns the event source:
///
///   - A source that outlives the window -- the OS, an app-level service, a
///     static event, a timer the window created but the dispatcher drives --
///     keeps calling after Window.Closed, and keeps the closed window alive
///     while it does. It must be unsubscribed on the close path, or its
///     handler must return early on the window's teardown latch.
///   - A source the window itself owns and that dies with it -- its own XAML
///     elements, its per-window services, its pane tree -- needs neither.
///
/// There is no semantic model here (see ShellSource), so ownership cannot be
/// inferred; it has to be declared. <see cref="Censused.Owned"/> is that
/// declaration, and it is an allow-list rather than a list of sources known to
/// need proof, because the failure mode being defended against is a
/// subscription NOBODY classified. A deny-list is silent on the receiver
/// introduced next week, which is precisely how these four arrived. An
/// allow-list makes the new receiver fail until someone writes down which side
/// of the line it falls on, and that sentence is the whole product of this
/// test.
///
/// The same argument applies one level up, to the files. This census covered
/// MainWindow alone until ShaderPickerWindow -- a window that owns a native
/// surface, a child process and an autoplay feed -- turned out to be outside
/// it entirely. So the windows are not named here: they are found, by looking
/// for every class in the shell that derives from Window, and each one has to
/// appear in <see cref="Windows"/> or the census fails. Window number four is
/// covered the day it is written.
///
/// What it cannot see: whether a gated handler's gate covers the turns after
/// an await, whether an unsubscribe runs on every path through the close, and
/// every subscription made outside the window's own file. It is a census, not
/// a proof of safety. The per-case guards below carry the reasoning those
/// need.
/// </summary>
public class WindowTeardownWiringTests
{
    /// <summary>
    /// One window under census.
    /// </summary>
    /// <param name="Class">Class name, which is what discovery matches on.</param>
    /// <param name="File">Dotted tail for <see cref="ShellSource.Load"/>.</param>
    /// <param name="ClosedFlag">
    /// The field a handler reads to bail out once teardown has started, or
    /// null when the window has no such latch -- in which case only an
    /// unsubscribe can prove a subscription safe here.
    /// </param>
    /// <param name="ClosePath">
    /// Methods the Closed handler delegates teardown to, whose unsubscribes
    /// count as the close's. Declared rather than followed automatically: an
    /// unsubscribe that moved into a helper the close reaches only on one
    /// branch is a weaker claim than one written in the close itself, and
    /// somebody has to say so.
    /// </param>
    /// <param name="AtLeast">
    /// How many subscriptions the query must still find. Load-bearing: every
    /// assertion is over that list, so a query that stops matching would pass
    /// the whole census while reading nothing.
    /// </param>
    /// <param name="Owned">Receivers whose events die with the window, and why.</param>
    /// <param name="Exempt">
    /// Individual events on sources that DO outlive the window and are still
    /// left attached, each with the reason detaching or gating would be wrong.
    /// Spelled as whole events, not receivers, so the rest of the receiver
    /// stays under the rule.
    /// </param>
    private sealed record Censused(
        string Class,
        string File,
        string? ClosedFlag,
        string[] ClosePath,
        int AtLeast,
        (string Receiver, string Why)[] Owned,
        (string Event, string Why)[] Exempt);

    /// <summary>
    /// Adding a line to one of these is meant to cost a decision: the claim is
    /// that when this window is gone nothing can raise the event again, so
    /// neither an unsubscribe nor a gate buys anything.
    /// </summary>
    private static readonly (string Receiver, string Why)[] MainWindowOwned =
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

    private static readonly (string Event, string Why)[] MainWindowExempt =
    {
        ("AppWindow.Closing", "runs during the close by design: it is what turns a quake close "
                              + "into a hide. _isClosed is still false when it fires"),
    };

    private static readonly (string Receiver, string Why)[] ShaderPickerOwned =
    {
        ("this", "the Window's own events; the window raises them and stops when it does"),
        ("RootGrid", "named element of ShaderPickerWindow.xaml"),
        // The one that is not obvious. FirstRender is raised through the
        // shared bootstrap host's dispatcher, which outlives this window, so
        // the subscription is only safe because of what the close does to the
        // control: DisposePreview calls DisposeSurface, and DisposeSurface
        // nulls FirstRender along with the control's other events.
        ("control", "this window's own preview TerminalControl; the close calls DisposePreview, "
                    + "whose DisposeSurface nulls FirstRender with the rest of the control's events"),
    };

    private static readonly (string Receiver, string Why)[] SettingsWindowOwned =
    {
        ("this", "the Window's own events; the window raises them and stops when it does"),
        ("ctrlF", "a KeyboardAccelerator this window creates and hands to its own NavView"),
        ("root", "a settings page's own element, passed into a static helper; the handler "
                 + "detaches itself on the first fire"),
    };

    private static readonly (string Receiver, string Why)[] AboutWindowOwned =
    {
        ("this", "the Window's own events; the window raises them and stops when it does"),
    };

    private static readonly (string Receiver, string Why)[] InspectorWindowOwned =
    {
        ("this", "the Window's own events; the window raises them and stops when it does"),
        ("Panel", "named element of InspectorWindow.xaml"),
    };

    private static readonly (string Event, string Why)[] NoneExempt =
        Array.Empty<(string Event, string Why)>();

    private static readonly Censused MainWindow = new(
        Class: "MainWindow",
        File: "Ghostty.MainWindow.xaml.cs",
        ClosedFlag: "_isClosed",
        ClosePath: Array.Empty<string>(),
        AtLeast: 40,
        Owned: MainWindowOwned,
        Exempt: MainWindowExempt);

    private static readonly Censused ShaderPickerWindow = new(
        Class: "ShaderPickerWindow",
        File: "Settings.ShaderPickerWindow.xaml.cs",
        ClosedFlag: "_closed",
        // The Closed handler latches and delegates; DisposePreview is the
        // teardown.
        ClosePath: new[] { "DisposePreview" },
        AtLeast: 4,
        Owned: ShaderPickerOwned,
        Exempt: NoneExempt);

    private static readonly Censused SettingsWindow = new(
        Class: "SettingsWindow",
        File: "Settings.SettingsWindow.xaml.cs",
        ClosedFlag: null,
        ClosePath: Array.Empty<string>(),
        AtLeast: 5,
        Owned: SettingsWindowOwned,
        Exempt: NoneExempt);

    private static readonly Censused AboutWindow = new(
        Class: "AboutWindow",
        File: "Dialogs.AboutWindow.xaml.cs",
        ClosedFlag: null,
        ClosePath: Array.Empty<string>(),
        AtLeast: 2,
        Owned: AboutWindowOwned,
        Exempt: NoneExempt);

    private static readonly Censused InspectorWindow = new(
        Class: "InspectorWindow",
        File: "InspectorWindow.xaml.cs",
        ClosedFlag: "_closed",
        ClosePath: Array.Empty<string>(),
        AtLeast: 11,
        Owned: InspectorWindowOwned,
        Exempt: NoneExempt);

    private static readonly Censused[] Windows =
    {
        MainWindow,
        ShaderPickerWindow,
        SettingsWindow,
        AboutWindow,
        InspectorWindow,
    };

    /// <summary>The census cases, named by file so xunit can address one.</summary>
    public static IEnumerable<object[]> CensusedWindows() =>
        Windows.Select(w => new object[] { w.File });

    private static Censused Find(string file) => Windows.Single(w => w.File == file);

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

    private static List<Subscription> Subscriptions(ShellSource source) =>
        Assignments(source.Root, SyntaxKind.AddAssignmentExpression);

    /// <summary>
    /// The window's close path: whatever it attaches to its OWN Closed event
    /// (`newWindow.Closed += ...` is somebody else's), plus the methods
    /// declared in <see cref="Censused.ClosePath"/>.
    ///
    /// Read off the subscription rather than from a method name, so a window
    /// that renames or inlines its close handler is still measured against the
    /// handler it actually installs.
    /// </summary>
    private static List<SyntaxNode> CloseScopes(Censused window, ShellSource source)
    {
        var handlers = Subscriptions(source).Where(s => s.Event == "Closed").ToList();
        Assert.True(
            handlers.Count == 1,
            $"{window.Class}: expected exactly one `Closed +=` on the window itself, found "
            + $"{handlers.Count}; the close path is what every claim below is measured against");

        SyntaxNode? entry = handlers[0].Rhs switch
        {
            LambdaExpressionSyntax lambda => lambda.Body,
            IdentifierNameSyntax name => source.Method(name.Identifier.ValueText).Body,
            _ => null,
        };
        Assert.True(
            entry is not null,
            $"{window.Class}: its Closed handler is neither a lambda nor a method in this file, "
            + "so nothing here can read what the close takes back");

        var scopes = new List<SyntaxNode> { entry! };
        scopes.AddRange(window.ClosePath.Select(m => (SyntaxNode)source.Method(m).Body!));
        return scopes;
    }

    private static List<Subscription> Unsubscribes(Censused window, ShellSource source) =>
        CloseScopes(window, source)
            .SelectMany(scope => Assignments(scope, SyntaxKind.SubtractAssignmentExpression))
            .ToList();

    /// <summary>
    /// <c>if (_isClosed) ... return;</c> and nothing that merely mentions the
    /// field: an inverted or dead condition is a different syntax shape and
    /// does not match. Deliberately the same shape TabLayoutSwitchWiringTests
    /// pins on the layout-switch completion -- the two have to agree on what
    /// counts as a gate, or the file grows two kinds.
    /// </summary>
    private static bool IsClosedGuard(StatementSyntax statement, string? flag) =>
        flag is not null
        && statement is IfStatementSyntax { Condition: IdentifierNameSyntax condition } guard
        && condition.Identifier.Text == flag
        && guard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any();

    /// <summary>
    /// Whether the handler bails once teardown has started, looking only at
    /// the handler's own top-level statements. A gate buried inside another
    /// branch guards that branch, not the handler, and a gate one call deeper
    /// is a fact about the callee that this cannot see.
    /// </summary>
    private static bool IsGated(Subscription subscription, ShellSource source, string? flag)
    {
        if (flag is null) return false;

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
        var gate = statements.Select((st, i) => (st, i)).FirstOrDefault(x => IsClosedGuard(x.st, flag));
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

    /// <summary>
    /// Every class in the shell that derives from Window, found rather than
    /// named. The base type is matched as written, so a window declared
    /// through an alias would be missed; that is the residual hole, and it is
    /// a smaller one than a hand-kept list of files.
    /// </summary>
    private static List<(string Resource, string Class)> WindowClasses() =>
        ShellSource.AllShellSources()
            .SelectMany(file => file.Root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Where(IsWindowClass)
                .Select(cls => (file.Resource, Class: cls.Identifier.ValueText)))
            .ToList();

    private static bool IsWindowClass(ClassDeclarationSyntax cls) =>
        cls.BaseList is { } bases
        && bases.Types.Any(t => t.Type.ToString() is "Window" or "Microsoft.UI.Xaml.Window");

    [Theory]
    [MemberData(nameof(CensusedWindows))]
    public void EverySubscriptionIsOwnedByTheWindowOrSurvivesItsClose(string file)
    {
        var window = Find(file);
        var source = ShellSource.Load(file);
        var subscriptions = Subscriptions(source);

        Assert.True(
            subscriptions.Count >= window.AtLeast,
            $"expected at least {window.AtLeast} event subscriptions in {window.Class}, got "
            + $"{subscriptions.Count}; every assertion below is over that list, so a query that "
            + "stops matching passes the whole test while reading nothing");

        var owned = window.Owned.Select(x => x.Receiver).ToHashSet(StringComparer.Ordinal);
        var exempt = window.Exempt.Select(x => x.Event).ToHashSet(StringComparer.Ordinal);
        var detached = Unsubscribes(window, source)
            .Select(u => (u.Event, u.Handler))
            .ToHashSet();

        // The pair, not the event alone: MainWindow attaches two different
        // handlers to _configService.ConfigChanged, and matching on the event
        // would let either `-=` answer for both.
        var mustProve = subscriptions
            .Where(s => !owned.Contains(s.Receiver) && !exempt.Contains(s.Event))
            .ToList();
        var unproven = mustProve
            .Where(s => !detached.Contains((s.Event, s.Handler)) && !IsGated(s, source, window.ClosedFlag))
            .Select(s => s.Event)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unproven.Count == 0,
            $"these {window.Class} subscriptions are to sources that outlive the window and are "
            + "neither unsubscribed on its close path nor gated on "
            + (window.ClosedFlag ?? "a teardown latch (it has none)")
            + ", so they keep firing at, and keep alive, a window that is tearing down: "
            + string.Join(", ", unproven)
            + ". Unsubscribe it, gate the handler, or -- if the receiver really does die with the "
            + "window -- add it to that window's Owned list with the reason.");
    }

    /// <summary>
    /// Both ways of satisfying the census have to stay live. Without this, a
    /// regression that classified everything as detached (or everything as
    /// gated) would leave the other branch untested and silently rotten.
    ///
    /// Asserted on MainWindow because it is the window that uses both. A
    /// window whose every receiver dies with it proves neither, and demanding
    /// it there would be demanding teardown code with nothing to tear down.
    /// </summary>
    [Fact]
    public void BothWaysOfProvingASubscriptionSafeAreExercised()
    {
        var source = ShellSource.Load(MainWindow.File);
        var owned = MainWindow.Owned.Select(x => x.Receiver).ToHashSet(StringComparer.Ordinal);
        var exempt = MainWindow.Exempt.Select(x => x.Event).ToHashSet(StringComparer.Ordinal);
        var detached = Unsubscribes(MainWindow, source).Select(u => (u.Event, u.Handler)).ToHashSet();

        var mustProve = Subscriptions(source)
            .Where(s => !owned.Contains(s.Receiver) && !exempt.Contains(s.Event))
            .ToList();

        Assert.Contains(mustProve, s => detached.Contains((s.Event, s.Handler)));
        Assert.Contains(mustProve, s => IsGated(s, source, MainWindow.ClosedFlag));
    }

    /// <summary>
    /// Every window in the shell is under census.
    ///
    /// This is the assertion that stops the file from silently narrowing to
    /// whatever it happened to cover on the day it was written, which is
    /// exactly what happened between #694 and #710: MainWindow was censused,
    /// ShaderPickerWindow -- native surface, child process, autoplay feed --
    /// was not, and nothing said so.
    /// </summary>
    [Fact]
    public void EveryWindowInTheShellIsUnderCensus()
    {
        var found = WindowClasses();
        Assert.True(
            found.Count > 0,
            "no Window-derived class found in the shell sources; this scan has stopped matching "
            + "and every window would now pass by not being seen");

        var declared = Windows.Select(w => w.Class).ToHashSet(StringComparer.Ordinal);
        var uncensused = found
            .Where(f => !declared.Contains(f.Class))
            .Select(f => $"{f.Class} ({f.Resource})")
            .ToList();
        Assert.True(
            uncensused.Count == 0,
            "these windows are outside the teardown census, so nothing checks that what they "
            + "subscribe to is either theirs to lose or taken back on close: "
            + string.Join(", ", uncensused)
            + ". Add a Censused entry naming its close path and who owns its event sources.");

        var vanished = Windows
            .Where(w => found.All(f => f.Class != w.Class))
            .Select(w => w.Class)
            .ToList();
        Assert.True(
            vanished.Count == 0,
            "these censused classes no longer derive from Window, so their entries describe "
            + "nothing: " + string.Join(", ", vanished));
    }

    /// <summary>
    /// The declarations have to stay a description of the file. An entry whose
    /// receiver no longer subscribes to anything is a claim nobody is checking,
    /// and the next receiver to be spelled that way inherits an exemption
    /// nobody granted it.
    /// </summary>
    [Theory]
    [MemberData(nameof(CensusedWindows))]
    public void TheOwnershipDeclarationsStillDescribeTheFile(string file)
    {
        var window = Find(file);
        var source = ShellSource.Load(file);
        var subscriptions = Subscriptions(source);
        var receivers = subscriptions.Select(s => s.Receiver).ToHashSet(StringComparer.Ordinal);
        var events = subscriptions.Select(s => s.Event).ToHashSet(StringComparer.Ordinal);

        var staleOwners = window.Owned.Where(x => !receivers.Contains(x.Receiver)).Select(x => x.Receiver);
        var staleExempt = window.Exempt.Where(x => !events.Contains(x.Event)).Select(x => x.Event);
        var stale = staleOwners.Concat(staleExempt).ToList();

        Assert.True(
            stale.Count == 0,
            $"these entries no longer match any subscription in {window.Class} and should be "
            + "removed, so the list keeps meaning what it says: " + string.Join(", ", stale));

        Assert.All(window.Owned, x => Assert.False(string.IsNullOrWhiteSpace(x.Why)));
        Assert.All(window.Exempt, x => Assert.False(string.IsNullOrWhiteSpace(x.Why)));

        // A latch that no longer exists would silently turn every gated
        // handler into an ungated one that still reads as gated here.
        if (window.ClosedFlag is { } flag) source.Field(flag);

        // Same for the declared close-path helpers.
        foreach (var method in window.ClosePath) source.Method(method);
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
        var source = ShellSource.Load(MainWindow.File);
        const string Event = "_systemUiSettings.ColorValuesChanged";
        const string Handler = "OnSystemColorValuesChanged";

        // Named, not inline: an anonymous lambda cannot be unsubscribed at all.
        var subscribed = Subscriptions(source).Where(s => s.Event == Event).ToList();
        Assert.True(
            subscribed.Count == 1 && subscribed[0].Handler == Handler,
            $"expected one `{Event} += {Handler}`; a lambda here cannot be detached");
        Assert.Contains(
            (Event, Handler),
            Unsubscribes(MainWindow, source).Select(u => (u.Event, u.Handler)));

        var method = source.Method(Handler);
        Assert.True(
            method.Body!.Statements.Any(s => IsClosedGuard(s, MainWindow.ClosedFlag)),
            $"{Handler} must bail on {MainWindow.ClosedFlag} before it reads anything");

        // The enqueued body is the half that runs late, and it is a separate
        // scope: the entry gate above says nothing about the turn it runs on.
        var enqueued = method.Call("DispatcherQueue.TryEnqueue").ArgumentList.Arguments[0].Expression;
        var queued = Assert.IsType<ParenthesizedLambdaExpressionSyntax>(enqueued).Block!.Statements;

        var gateIndex = queued.TakeWhile(s => !IsClosedGuard(s, MainWindow.ClosedFlag)).Count();
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
        var source = ShellSource.Load(MainWindow.File);
        var statements = source.Method("OnClosedAsync").Body!.Statements;

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
        var subscriptions = Subscriptions(source);
        // The picker poll subscribes through the local it captures rather than
        // through the field, so that a tick queued by a previous poll cannot
        // stop the one that replaced it. That is why this names poll.Tick.
        foreach (var timer in new[] { "poll.Tick", "_cyclePopupTimer.Tick", "pidPoll.Tick", "startTimer.Tick" })
        {
            var ticks = subscriptions.Where(s => s.Event == timer).ToList();
            Assert.True(ticks.Count == 1, $"expected one {timer} subscription, found {ticks.Count}");
            Assert.True(
                IsGated(ticks[0], source, MainWindow.ClosedFlag),
                $"{timer}'s handler must return early on _isClosed: Stop does not recall a tick "
                + "already queued on the dispatcher");
        }
    }

    private static readonly string[] PickerFields =
        { "_pickerSurface", "_pickerTerminal", "_inlineThemeCb" };

    /// <summary>
    /// <c>_pickerHandle == IntPtr.Zero</c>, either way round.
    /// </summary>
    private static bool TestsPickerHandleAgainstZero(ExpressionSyntax condition)
    {
        if (condition is not BinaryExpressionSyntax equality) return false;
        if (!equality.IsKind(SyntaxKind.EqualsExpression)) return false;

        var sides = new[] { Flatten(equality.Left), Flatten(equality.Right) };
        return sides.Contains("_pickerHandle") && sides.Contains("IntPtr.Zero");
    }

    /// <summary>
    /// The fields <paramref name="scope"/> assigns null or default to. Read
    /// off the right-hand side, because an assignment that puts something
    /// back is not a clear.
    /// </summary>
    private static HashSet<string> ClearedIn(SyntaxNode scope) =>
        scope.DescendantNodesAndSelf()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression))
            .Where(a => a.Right.IsKind(SyntaxKind.NullLiteralExpression)
                        || a.Right.IsKind(SyntaxKind.DefaultLiteralExpression)
                        || a.Right is DefaultExpressionSyntax)
            .Select(a => Flatten(a.Left))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The picker open, on the path where libghostty declines to open one.
    ///
    /// The three fields the close reads -- the surface copy, the control it
    /// was opened on, and the callback field that is the only GC root for the
    /// delegate whose function pointer goes across -- are assigned before the
    /// native call, and have to be: a local would be collectable while the
    /// call is still running. So a null return leaves all three set with no
    /// picker behind them, and ClosePicker returns on the zero handle before
    /// it reaches the clears. The window is then holding a raw pointer to a
    /// surface that whoever owns it may free, plus a strong reference to a
    /// control and its whole pane subtree, until a later picker succeeds.
    /// ghostty_surface_list_themes returns null on four separate paths, so
    /// this is not a branch that needs a fault to reach.
    /// </summary>
    [Fact]
    public void AFailedPickerOpenClearsWhatTheCloseWillNotReach()
    {
        var source = ShellSource.Load(MainWindow.File);
        var statements = source.Method("OnListThemesRequested").Body!.Statements;

        var opened = statements
            .TakeWhile(s => !s.Calls("NativeMethods.SurfaceListThemes").Any())
            .Count();
        Assert.True(
            opened < statements.Count,
            "expected OnListThemesRequested to open the picker through "
            + "NativeMethods.SurfaceListThemes; without that call there is no failure path "
            + "here and this test would pass while reading nothing");

        // After the call only: the same condition written before it would be
        // testing the previous picker's handle, which is a different claim.
        var cleared = statements
            .Skip(opened + 1)
            .OfType<IfStatementSyntax>()
            .Where(guard => TestsPickerHandleAgainstZero(guard.Condition))
            .Where(guard => guard.Statement.DescendantNodesAndSelf()
                .OfType<ReturnStatementSyntax>().Any())
            .Where(guard => ClearedIn(guard.Statement).IsSupersetOf(PickerFields))
            .ToList();

        Assert.True(
            cleared.Count == 1,
            "OnListThemesRequested must clear " + string.Join(", ", PickerFields)
            + " and return when SurfaceListThemes hands back a zero handle. Nothing else will: "
            + "ClosePicker bails on the zero handle before it reaches those same clears, so the "
            + "stale surface pointer and the pane subtree behind _pickerTerminal are held until "
            + "the next successful picker or the window's close.");
    }

    /// <summary>
    /// ThemePreviewService, the one gap #694 named and did not close.
    ///
    /// It is not an event source the census can see: it owns a named-pipe
    /// server running on a background task, and the close only detached the
    /// window from its ListThemesRequested. The task then ran for the life of
    /// the process, holding the per-process pipe name with nobody left
    /// listening to what arrived on it. Disposing cancels the loop, waits for
    /// it, and drops the subscribers, so the window is neither rooted by it
    /// nor outlived by it.
    /// </summary>
    [Fact]
    public void TheThemePreviewServiceIsDisposedByTheClose()
    {
        var source = ShellSource.Load(MainWindow.File);
        var statements = source.Method("OnClosedAsync").Body!.Statements;

        var disposed = statements.TakeWhile(s => !s.Calls("_themePreview.Dispose").Any()).Count();
        Assert.True(
            disposed < statements.Count,
            "OnClosedAsync must dispose _themePreview: unsubscribing alone leaves its pipe server "
            + "task running for the life of the process");
    }

    /// <summary>
    /// The shader picker's close, in the two places the census cannot reach.
    ///
    /// The picker is the only other window that owns a native surface, and it
    /// owns a placeholder child process and an autoplay feed with it. None of
    /// the three is freed by the visual tree going away, and the order matters
    /// twice over: the feed writes into the surface, so it has to stop before
    /// the surface is freed; and the latch has to be set before the teardown,
    /// because a SelectionChanged arriving during the close reaches
    /// EnsurePreview, and a surface built after DisposePreview has run would be
    /// owned by nobody at all.
    /// </summary>
    [Fact]
    public void TheShaderPickerCloseFreesTheSurfaceTheChildAndTheFeed()
    {
        var source = ShellSource.Load(ShaderPickerWindow.File);

        var closing = Assert.IsType<BlockSyntax>(CloseScopes(ShaderPickerWindow, source)[0]).Statements;
        var latch = closing.TakeWhile(s => !SetsLatch(s, ShaderPickerWindow.ClosedFlag)).Count();
        var handoff = closing.TakeWhile(s => !s.Calls("DisposePreview").Any()).Count();
        Assert.True(latch < closing.Count, "the Closed handler must latch _closed");
        Assert.True(handoff < closing.Count, "the Closed handler must call DisposePreview");
        Assert.True(latch < handoff, "the latch is what EnsurePreview reads, so it has to be set first");

        var teardown = source.Method("DisposePreview").Body!.Statements;
        int IndexOfCall(string call) => teardown.TakeWhile(s => !s.Calls(call).Any()).Count();
        var feed = IndexOfCall("_feed?.Dispose");
        var surface = IndexOfCall("_preview?.DisposeSurface");
        Assert.True(feed < teardown.Count, "DisposePreview must dispose the autoplay feed");
        Assert.True(
            surface < teardown.Count,
            "DisposePreview must free the preview surface; the placeholder child process goes with it");
        Assert.True(
            feed < surface,
            "the feed writes VT into the preview, so it has to stop before the surface is freed");

        var ensure = source.Method("EnsurePreview").Body!.Statements;
        var gate = ensure.TakeWhile(s => !IsClosedGuard(s, ShaderPickerWindow.ClosedFlag)).Count();
        var creation = ensure
            .TakeWhile(s => !s.DescendantNodesAndSelf()
                .OfType<ObjectCreationExpressionSyntax>()
                .Any(o => o.Type.ToString().EndsWith("TerminalControl", StringComparison.Ordinal)))
            .Count();
        Assert.True(gate < ensure.Count, "EnsurePreview must return early once the window has closed");
        Assert.True(creation < ensure.Count, "expected EnsurePreview to construct the preview terminal");
        Assert.True(
            gate < creation,
            "the latch must be read before a native surface and its child process are created: "
            + "DisposePreview has already run, so nothing would own them");
    }

    /// <summary>
    /// <c>_closed = true;</c>, as a statement of its own.
    /// </summary>
    private static bool SetsLatch(StatementSyntax statement, string? flag) =>
        flag is not null
        && statement is ExpressionStatementSyntax
        {
            Expression: AssignmentExpressionSyntax
            {
                Left: IdentifierNameSyntax left,
                Right: LiteralExpressionSyntax right
            }
        }
        && left.Identifier.Text == flag
        && right.IsKind(SyntaxKind.TrueLiteralExpression);
}
