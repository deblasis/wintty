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
/// to hold is exactly one subscriber, on App, for the life of the process.
/// </summary>
public class AppActionOwnershipWiringTests
{
    /// <summary>The events libghostty raises with target=app.</summary>
    private static readonly string[] AppActions = { "OpenConfigRequested", "ReloadConfigRequested" };

    /// <summary>
    /// The two files these names may appear in at all: the host that declares
    /// and raises them, and the one subscriber. Deliberately not an extensible
    /// allow-list -- a third file named here would be the bug this test exists
    /// to catch.
    /// </summary>
    private static readonly string[] Owners = { "Ghostty.Hosting.GhosttyHost.cs", "Ghostty.App.xaml.cs" };

    private const string Corpus = "Ghostty.Tests.Interop.Sources.";

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
        scope.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(kind))
            .Select(a => (Assignment: a, Name: EventName(a.Left)))
            .Where(x => x.Name is not null && AppActions.Contains(x.Name, StringComparer.Ordinal))
            .Select(x => new Wire(
                x.Name!,
                x.Assignment.Left is MemberAccessExpressionSyntax m ? Receiver(m.Expression) : "this",
                x.Assignment.Right.ToString()))
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

        var subscriptions = app.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                        && AppActions.Contains(EventName(a.Left)!, StringComparer.Ordinal))
            .ToList();
        Assert.Equal(AppActions.Length, subscriptions.Count);

        var startup = StartupPath(app);

        foreach (var assignment in subscriptions)
        {
            // The defect was a subscription under a condition that read as
            // "the primary window only" and selected almost every window. A
            // conditional or repeated subscription on App is the same defect
            // wearing App's name, and it satisfies a count of one whenever the
            // branch happens to be taken once.
            var enclosing = assignment.FirstAncestorOrSelf<MemberDeclarationSyntax>();
            var nested = assignment.Ancestors()
                .TakeWhile(n => n != enclosing)
                .FirstOrDefault(n => n is IfStatementSyntax or SwitchStatementSyntax or ElseClauseSyntax
                    or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax
                    or DoStatementSyntax or TryStatementSyntax or LambdaExpressionSyntax
                    or LocalFunctionStatementSyntax);

            Assert.True(
                nested is null,
                $"`{assignment}` is inside a {nested?.Kind()}. These have to be taken out once, "
                + "unconditionally, on the one path that builds the bootstrap host; a guarded "
                + "subscription is how the per-window version read as one window while nearly "
                + "every window subscribed.");

            // And on THAT path, not merely on some unconditional statement
            // somewhere: a subscription sitting in a method that runs per
            // window, or per settings-window open, is the original defect with
            // App's name on it, and it satisfies every count above.
            Assert.Same(startup, enclosing);
        }
    }

    /// <summary>
    /// The member that builds the bootstrap host, identified by the
    /// assignment that publishes it. That statement runs exactly once per
    /// process, which is the property the subscriptions need and the one no
    /// count of syntax can establish on its own.
    /// </summary>
    private static MemberDeclarationSyntax StartupPath(ShellSource app)
    {
        var published = app.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        && a.Left.ToString() == "BootstrapHost"
                        && a.Right.ToString() == "_bootstrapHost")
            .ToList();
        Assert.True(
            published.Count == 1,
            $"expected one `BootstrapHost = _bootstrapHost;` in App, found {published.Count}");
        return published[0].FirstAncestorOrSelf<MemberDeclarationSyntax>()!;
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

        int IndexOf(Func<StatementSyntax, bool> match) => shutdown.TakeWhile(s => !match(s)).Count();

        var dispose = IndexOf(s => s.Calls("_bootstrapHost?.Dispose").Any());
        Assert.True(dispose < shutdown.Count, "expected the shutdown block to dispose the bootstrap host");

        foreach (var wire in detaches)
        {
            var at = IndexOf(s => s.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression)
                          && EventName(a.Left) == wire.Event
                          && a.Right.ToString() == wire.Handler));
            Assert.True(
                at < shutdown.Count,
                $"{wire.Event} is detached somewhere other than the shutdown that disposes the host");
            Assert.True(
                at < dispose,
                $"{wire.Event} must be detached before _bootstrapHost.Dispose frees the ghostty app");
        }
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
    /// The corpus rule, and the one that actually answers the issue: no file
    /// other than the host and App may so much as name these events.
    ///
    /// Text rather than syntax, deliberately. A parse cannot see inside a
    /// disabled conditional region, and a subscription reintroduced there is a
    /// live subscription in the configuration that defines the symbol.
    /// Matching the token in the raw source has no such blind spot, and the
    /// cost -- that a comment naming the event also fails -- is worth paying
    /// for a rule whose whole content is that this name lives in two files.
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
        Assert.All(Owners, owner => Assert.Contains(corpus, f => f.Tail == owner));

        var pattern = new Regex(@"\b(" + string.Join("|", AppActions) + @")\b", RegexOptions.Compiled);
        var offenders = corpus
            .Where(f => !Owners.Contains(f.Tail, StringComparer.Ordinal) && pattern.IsMatch(f.Text))
            .Select(f => f.Tail)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "these files name an app-targeted action event: " + string.Join(", ", offenders)
            + ". These are raised once per process on the bootstrap host, and App owns the one "
            + "subscription; a second subscriber runs the action twice and roots whatever its "
            + "closure captured on a host that lives as long as the process.");

        // And the parsed view agrees. Anything it caught would have failed the
        // text rule first, which is what makes this a cross-check on the
        // matcher rather than a second copy of the same rule.
        foreach (var file in corpus.Where(f => f.Tail != "Ghostty.App.xaml.cs"))
        {
            var wires = Wires(ShellSource.ParseForCorpusScan(file.Text).Root, SyntaxKind.AddAssignmentExpression);
            Assert.True(
                wires.Count == 0,
                $"{file.Tail} subscribes to {string.Join(", ", wires.Select(w => w.Event))}");
        }
    }
}
