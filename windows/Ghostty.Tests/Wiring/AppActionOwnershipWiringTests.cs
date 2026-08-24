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
/// Who subscribes to the bootstrap host's app-targeted actions.
///
/// libghostty sends OpenConfig and ReloadConfig with target=app, so they are
/// raised on the one host that owns the ghostty_app_t and never on a
/// per-window host. MainWindow used to subscribe, behind a guard that read as
/// "only the primary window does" and was in fact "every window that was not
/// adopting a tab": cold start, quake, each restored session window, each
/// reopened one. Two consequences, both of which this pins against coming
/// back. The action ran once per open window -- two Reloads, two settings
/// windows -- and each closure captured its window, so a host that lives as
/// long as the process kept every window and its XAML tree alive, unbounded
/// over an open/close loop.
///
/// The fix is ownership rather than a gate. A gate would not have helped: the
/// handlers are safe to run after a close, because they touch only
/// process-global state, and detaching per window would leave the last window
/// to close taking the handler away from the windows still open. So the shape
/// to hold is exactly one subscriber, taken out by App.OnLaunched, for the
/// life of the process.
///
/// What this cannot prove: that no window observes the action through some
/// further indirection. A relay -- App raising an event of its own that
/// per-window code subscribes to -- reintroduces the whole defect while naming
/// none of these events, and no source scan can enumerate the ways to write
/// one. <see cref="TheHandlersActOnTheActionRatherThanRebroadcastingIt"/>
/// closes the one shape that is cheap to write and impossible to spot in
/// review; the rest is left to the reader.
/// </summary>
public class AppActionOwnershipWiringTests
{
    /// <summary>The events libghostty raises with target=app.</summary>
    private static readonly string[] AppActions = { "OpenConfigRequested", "ReloadConfigRequested" };

    /// <summary>
    /// The file that owns the one subscription.
    /// </summary>
    private const string App = ".Ghostty.App.xaml.cs";

    /// <summary>
    /// The file that declares and raises the events.
    /// </summary>
    private const string Host = ".Ghostty.Hosting.GhosttyHost.cs";

    /// <summary>
    /// The only two files that may name these events at all. Deliberately not
    /// an extensible allow-list -- a third entry here would be the bug this
    /// test exists to catch. Both are exempt from the text rule below and both
    /// are therefore checked by parse instead, which is why
    /// <see cref="OwnerFilesHideNothingInAConditionalRegion"/> exists.
    /// </summary>
    private static readonly string[] Owners = { Host, App };

    /// <summary>
    /// Every embedded C# source, not one of the blocks the test project
    /// happens to embed. Two shell files (NativeMethods.cs,
    /// LibGhosttyBuildInfo.cs) are excluded from the Interop.Sources wildcard
    /// and re-embedded under Interop.Imports, so a corpus keyed on the
    /// Sources prefix silently never reads them.
    /// </summary>
    private const string Corpus = "Ghostty.Tests.";

    private static ShellSource AppSource() => ShellSource.Load("Ghostty.App.xaml.cs");

    private sealed record Wire(string Event, string Receiver, string Handler);

    /// <summary>
    /// Every subscription or detach whose left-hand side names one of the app
    /// actions, read off the parsed tree so a mention in a comment or a string
    /// is not one. Matched on the event's own name rather than on the
    /// receiver: a subscription taken out through some other spelling of the
    /// host is the same subscription.
    /// </summary>
    private static List<Wire> Wires(SyntaxNode scope, SyntaxKind kind) =>
        Assignments(scope, kind)
            .Select(x => new Wire(
                x.Name,
                x.Assignment.Left is MemberAccessExpressionSyntax m ? Receiver(m.Expression) : "this",
                x.Assignment.Right.ToString()))
            .ToList();

    private static List<(AssignmentExpressionSyntax Assignment, string Name)> Assignments(
        SyntaxNode scope, SyntaxKind kind) =>
        scope.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(kind))
            .Select(a => (Assignment: a, Name: EventName(a.Left)))
            .Where(x => x.Name is not null && AppActions.Contains(x.Name, StringComparer.Ordinal))
            .Select(x => (x.Assignment, Name: x.Name!))
            .ToList();

    /// <summary>
    /// The receiver as written, with a null-forgiving operator dropped: `x!`
    /// and `x` are the same field, and an assertion that told them apart would
    /// be pinning punctuation.
    /// </summary>
    private static string Receiver(ExpressionSyntax expression) =>
        expression is PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } bang
            ? bang.Operand.ToString()
            : expression.ToString();

    /// <summary>The event's own identifier, receiver stripped.</summary>
    private static string? EventName(ExpressionSyntax left) => left switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax name => name.Identifier.ValueText,
        _ => null,
    };

    [Fact]
    public void AppSubscribesExactlyOnceToEachAppAction()
    {
        var app = AppSource();
        var subscribes = Wires(app.Root, SyntaxKind.AddAssignmentExpression);

        foreach (var action in AppActions)
        {
            var wires = subscribes.Where(w => w.Event == action).ToList();
            Assert.True(
                wires.Count == 1,
                $"expected exactly one `+= {action}` in App; found {wires.Count}. One subscriber per "
                + "process is the whole point: a second one runs the action twice.");
            Assert.Equal("_bootstrapHost", wires[0].Receiver);

            // A method group, so the detach below has something to name. An
            // anonymous lambda cannot be unsubscribed at all, and this
            // subscription lasts as long as the process.
            Assert.True(
                SyntaxFactory.ParseExpression(wires[0].Handler) is IdentifierNameSyntax,
                $"{action} must be handled by a named method, not `{wires[0].Handler}`: a lambda "
                + "cannot be detached, and the teardown has to take this one back.");
        }
    }

    [Fact]
    public void TheSubscriptionsAreUnconditionalAndRunOnceAtStartup()
    {
        var app = AppSource();

        var subscriptions = Assignments(app.Root, SyntaxKind.AddAssignmentExpression);
        Assert.Equal(AppActions.Length, subscriptions.Count);

        // OnLaunched by name, not by whatever member happens to build the
        // host. Anchoring on a landmark ("the member that assigns
        // BootstrapHost") proves only that the subscription sits beside that
        // landmark, and both move together for free: hoist the pair and the
        // assignment into one helper, call the helper from a per-window path,
        // and every count in this file still reads one while a subscription
        // accumulates per window. OnLaunched is the framework's entry point,
        // called once per process by something no refactor in this repo owns.
        var startup = app.Method("OnLaunched");
        Assert.Contains(
            startup.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.IsKind(SyntaxKind.SimpleAssignmentExpression)
                 && a.Left.ToString() == "BootstrapHost"
                 && a.Right.ToString() == "_bootstrapHost");

        foreach (var (assignment, _) in subscriptions)
        {
            Assert.Same(startup, assignment.FirstAncestorOrSelf<MemberDeclarationSyntax>());

            // Unconditional within it, too. The defect was a subscription
            // under a condition that read as "the primary window only" and
            // selected almost every window; a guarded subscription on
            // OnLaunched is the same defect wearing App's name, and it
            // satisfies a count of one whenever the branch is taken once.
            var nested = assignment.Ancestors()
                .TakeWhile(n => n != startup)
                .FirstOrDefault(n => n is IfStatementSyntax or SwitchStatementSyntax or ElseClauseSyntax
                    or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax
                    or DoStatementSyntax or TryStatementSyntax or LambdaExpressionSyntax
                    or LocalFunctionStatementSyntax);

            Assert.True(
                nested is null,
                $"`{assignment}` is inside a {nested?.Kind()}. These have to be taken out once, "
                + "unconditionally, on the one path that builds the bootstrap host.");
        }
    }

    [Fact]
    public void TheProcessLifetimeSubscriptionsAreTakenBackBeforeTheHostIsFreed()
    {
        var app = AppSource();
        var subscribes = Wires(app.Root, SyntaxKind.AddAssignmentExpression);
        var detaches = Wires(app.Root, SyntaxKind.SubtractAssignmentExpression);

        // The (event, handler) pair, not the event: two handlers on one event
        // would otherwise answer for each other, and a detach of null
        // satisfies a match on the event alone.
        foreach (var wire in subscribes)
        {
            Assert.True(
                detaches.Contains(wire),
                $"`{wire.Receiver}.{wire.Event} += {wire.Handler}` is never taken back. It is attached "
                + "for the life of the process, so nothing else will.");
        }

        // Nothing else detaches them: the host is freed once, and a detach on
        // some other path would leave a live session with no handler.
        Assert.Equal(subscribes.Count, detaches.Count);

        // Order, not presence. The detach has to happen before the host frees
        // the ghostty app, or an action arriving during teardown reloads config
        // into freed native state -- the same crash shape as issue #208.
        var shutdown = app.Root.DescendantNodes().OfType<BlockSyntax>()
            .Where(b => b.Calls("_bootstrapHost?.Dispose").Count == 1)
            .OrderBy(b => b.Span.Length)
            .First()
            .Statements;

        var dispose = shutdown.TakeWhile(s => !s.Calls("_bootstrapHost?.Dispose").Any()).Count();
        Assert.True(dispose < shutdown.Count, "expected the shutdown block to dispose the bootstrap host");

        foreach (var wire in detaches)
        {
            var at = shutdown.TakeWhile(s => !Detaches(s, wire, app)).Count();
            Assert.True(
                at < shutdown.Count,
                $"{wire.Event} is detached somewhere other than the shutdown that disposes the host");
            Assert.True(
                at < dispose,
                $"{wire.Event} must be detached before _bootstrapHost.Dispose frees the ghostty app");
        }
    }

    /// <summary>
    /// Whether a statement takes <paramref name="wire"/> back, either inline
    /// or through a method of App it calls.
    ///
    /// One level, not zero: hoisting the pair of detaches into a named helper
    /// changes nothing about when they run, and a rule that reddened on it
    /// would be rejecting correct code -- which is the failure that gets a
    /// guard deleted rather than fixed. One level, not arbitrarily many:
    /// following a chain needs a semantic model, and there is none here.
    /// </summary>
    private static bool Detaches(StatementSyntax statement, Wire wire, ShellSource app)
    {
        if (Matches(statement)) return true;

        return statement.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(call => call.Expression)
            .OfType<IdentifierNameSyntax>()
            .SelectMany(name => app.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.ValueText == name.Identifier.ValueText))
            .Any(Matches);

        bool Matches(SyntaxNode scope) =>
            Assignments(scope, SyntaxKind.SubtractAssignmentExpression)
                .Any(x => x.Name == wire.Event && x.Assignment.Right.ToString() == wire.Handler);
    }

    [Fact]
    public void EachHandlerIsOneRealMethodOnApp()
    {
        var app = AppSource();

        var wires = Wires(app.Root, SyntaxKind.AddAssignmentExpression);
        Assert.NotEmpty(wires);

        foreach (var wire in wires)
        {
            // Method() asserts there is exactly one. Without it a detach could
            // name a method that no longer exists on the subscribing side, or
            // two overloads could make "the handler" ambiguous.
            var method = app.Method(wire.Handler);
            Assert.True(
                method.Body is not null || method.ExpressionBody is not null,
                $"{wire.Handler} has no body");
        }
    }

    /// <summary>
    /// The handlers do the action. They do not raise an event of their own.
    ///
    /// The cheapest way to put the defect back without naming either event is
    /// a relay: App keeps the one real subscription and re-broadcasts through
    /// a static event, and per-window code subscribes to that instead. Every
    /// other rule in this file passes, because the event names never leave
    /// App. Refusing a delegate invocation inside these two handlers is not a
    /// proof against indirection in general -- nothing here can be -- but it
    /// is the difference between writing the relay by accident and having to
    /// route around a test that says not to.
    /// </summary>
    [Fact]
    public void TheHandlersActOnTheActionRatherThanRebroadcastingIt()
    {
        var app = AppSource();

        foreach (var wire in Wires(app.Root, SyntaxKind.AddAssignmentExpression))
        {
            var raised = app.Method(wire.Handler)
                .DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(call => call.CalleeText().EndsWith("Invoke", StringComparison.Ordinal))
                .ToList();

            Assert.True(
                raised.Count == 0,
                $"{wire.Handler} raises {string.Join(", ", raised.Select(r => r.CalleeText()))}. "
                + "An app-targeted action handled once per process and then fanned out to a "
                + "per-window subscriber is the defect this file exists to prevent, wearing a "
                + "different event's name.");
        }
    }

    /// <summary>
    /// The two owner files are exempt from the text rule below, so a parse is
    /// all that reads them -- and a parse cannot see inside a disabled
    /// conditional region. Wrapping a subscription in `#if !DEMO` inside
    /// GhosttyHost therefore hid it from both rules at once, while shipping in
    /// every non-DEMO build.
    ///
    /// <see cref="ShellSource.Load"/> refuses a file that still has disabled
    /// text after DEMO is defined, which is the check that closes it. So both
    /// owners go through Load, not through the corpus scan's relaxed parse.
    /// </summary>
    [Fact]
    public void OwnerFilesHideNothingInAConditionalRegion()
    {
        // App itself is loaded by every other test here. The host is not, and
        // it is the one file where a per-window host could reach the
        // bootstrap host's events without looking out of place.
        var host = ShellSource.Load("Ghostty.Hosting.GhosttyHost.cs");

        var wires = Wires(host.Root, SyntaxKind.AddAssignmentExpression);
        Assert.True(
            wires.Count == 0,
            "GhosttyHost subscribes to " + string.Join(", ", wires.Select(w => w.Event))
            + ". It declares and raises these; subscribing to another host's copy is how a "
            + "per-window host becomes a second subscriber.");

        // Load-bearing: Load is what makes the above meaningful, and it is
        // silently satisfied by a file that parses to nothing.
        Assert.NotEmpty(host.Root.DescendantNodes().OfType<MethodDeclarationSyntax>());
    }

    /// <summary>
    /// The corpus rule, and the one that actually answers the issue: no file
    /// other than the host and App may so much as name these events.
    ///
    /// Text rather than syntax, deliberately. A parse cannot see inside a
    /// disabled conditional region, and a subscription reintroduced there is a
    /// live subscription in the configuration that defines the symbol.
    /// Matching the token in the raw source has no such blind spot. The cost
    /// is that a comment naming either event also fails -- including a comment
    /// explaining why a file must not subscribe. Reword it; the rule is worth
    /// more than the sentence.
    /// </summary>
    [Fact]
    public void NoOtherFileSubscribesToTheAppActions()
    {
        var corpus = ShellSource.AllUnder(Corpus);

        // Load-bearing: a prefix that stopped matching would pass this test
        // while reading nothing at all.
        Assert.True(
            corpus.Count > 100,
            $"expected the shell source corpus under '{Corpus}', got {corpus.Count} files");
        foreach (var owner in Owners)
        {
            Assert.Single(corpus.Where(f => f.Tail.EndsWith(owner, StringComparison.Ordinal)));
        }

        // And the two files that live outside the Interop.Sources wildcard are
        // in it, because a corpus keyed on that prefix alone silently skipped
        // them and they are shell sources like any other.
        Assert.Contains(corpus, f => f.Tail.EndsWith(".NativeMethods.cs", StringComparison.Ordinal));

        var pattern = new Regex(@"\b(" + string.Join("|", AppActions) + @")\b", RegexOptions.Compiled);
        var offenders = corpus
            .Where(f => !Owners.Any(o => f.Tail.EndsWith(o, StringComparison.Ordinal)) && pattern.IsMatch(f.Text))
            .Select(f => f.Tail)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "these files name an app-targeted action event: " + string.Join(", ", offenders)
            + ". These are raised once per process on the bootstrap host, and App owns the one "
            + "subscription; a second subscriber runs the action twice and roots whatever its "
            + "closure captured on a host that lives as long as the process. If the name is only "
            + "in a comment, reword the comment.");

        // And the parsed view agrees. It reads the owner files too, which the
        // text rule exempts -- so this is not a second copy of the rule above,
        // it is the half that covers what the exemption opens up.
        foreach (var file in corpus.Where(f => !f.Tail.EndsWith(App, StringComparison.Ordinal)))
        {
            var wires = Wires(ShellSource.ParseForCorpusScan(file.Text).Root, SyntaxKind.AddAssignmentExpression);
            Assert.True(
                wires.Count == 0,
                $"{file.Tail} subscribes to {string.Join(", ", wires.Select(w => w.Event))}");
        }
    }
}
