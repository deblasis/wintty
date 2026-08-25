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
/// Who owns the +list-themes pipe server.
///
/// ThemePreviewService serves a named pipe whose name carries the process id,
/// created with FirstPipeInstance. There is exactly one of that name in the
/// process, and the second service to ask for it does not get a degraded
/// server -- it gets an IOException at construction, which the retry policy
/// correctly calls permanent, and its accept loop exits before it has ever
/// accepted anything. Constructing one per window therefore left windows 2..N
/// with a service that was never going to serve, and left the whole feature
/// riding on window 1. When window 1's close later learned to dispose its
/// service, the pipe name was released with nothing reclaiming it and
/// `wintty +list-themes` went dead for the rest of the session instead.
///
/// So the shape to hold is one service for the process, built where the
/// bootstrap host is built and torn down where it is torn down, with the
/// request routed to whichever window can currently show a picker.
///
/// The load-bearing difference from <see cref="AppActionOwnershipWiringTests"/>
/// is that CONSTRUCTION is the ownership fact here, not subscription. There,
/// a service per window was fine and only the subscription had to be unique.
/// Here the singleton is the pipe, which is claimed by the constructor: a
/// rule that counted only subscriptions would pass a second
/// `new ThemePreviewService` that subscribes to nothing, and that is exactly
/// what windows 2..N were.
///
/// What this cannot prove: that no window reaches the request through some
/// further indirection. <see cref="TheHandlerRoutesTheRequestRatherThanRebroadcastingIt"/>
/// closes the one shape -- App re-raising an event of its own -- that is cheap
/// to write and invisible in review; the rest is left to the reader.
/// </summary>
public class ThemePreviewOwnershipWiringTests
{
    /// <summary>The event the pipe server raises when a LIST_THEMES arrives.</summary>
    private const string ListThemes = "ListThemesRequested";

    /// <summary>The service whose constructor claims the pipe name.</summary>
    private const string ServiceType = "ThemePreviewService";

    /// <summary>The file that owns the one construction and the one subscription.</summary>
    private const string App = ".Ghostty.App.xaml.cs";

    /// <summary>The file that declares the service and raises the event.</summary>
    private const string Service = ".Ghostty.Services.ThemePreviewService.cs";

    /// <summary>
    /// The only two files that may name the event at all. Deliberately not an
    /// extensible allow-list -- a third entry is the bug this file exists to
    /// catch. Both are exempt from the text rule below and therefore read by
    /// parse instead, which is why
    /// <see cref="OwnerFilesHideNothingInAConditionalRegion"/> exists.
    /// </summary>
    private static readonly string[] Owners = { App, Service };

    /// <summary>
    /// Every embedded C# source, not one of the blocks the test project
    /// happens to embed. Two shell files (NativeMethods.cs,
    /// LibGhosttyBuildInfo.cs) sit outside the Interop.Sources wildcard and
    /// are re-embedded under Interop.Imports, so a corpus keyed on the
    /// Sources prefix silently never reads them.
    /// </summary>
    private const string Corpus = "Ghostty.Tests.";

    private static ShellSource AppSource() => ShellSource.Load("Ghostty.App.xaml.cs");

    private static ShellSource ServiceSource() =>
        ShellSource.Load("Services.ThemePreviewService.cs");

    private sealed record Wire(string Event, string Receiver, string Handler);

    /// <summary>
    /// Every subscription or detach whose left-hand side names the event, read
    /// off the parsed tree so a mention in a comment or a string is not one.
    /// Matched on the event's own name rather than on the receiver: a
    /// subscription taken out through some other spelling of the service is
    /// the same subscription.
    /// </summary>
    private static List<Wire> Wires(SyntaxNode scope, SyntaxKind kind) =>
        Assignments(scope, kind)
            .Select(a => new Wire(
                ListThemes,
                a.Left is MemberAccessExpressionSyntax m ? Receiver(m.Expression) : "this",
                a.Right.ToString()))
            .ToList();

    private static List<AssignmentExpressionSyntax> Assignments(SyntaxNode scope, SyntaxKind kind) =>
        scope.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(kind) && EventName(a.Left) == ListThemes)
            .ToList();

    /// <summary>
    /// The receiver as written, with a null-forgiving operator dropped: `x!`
    /// and `x` are the same field, and telling them apart would be pinning
    /// punctuation.
    /// </summary>
    private static string Receiver(ExpressionSyntax expression) =>
        expression is PostfixUnaryExpressionSyntax
            { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } bang
            ? bang.Operand.ToString()
            : expression.ToString();

    /// <summary>The event's own identifier, receiver stripped.</summary>
    private static string? EventName(ExpressionSyntax left) => left switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax name => name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>
    /// Every `new ThemePreviewService(...)` under <paramref name="scope"/>,
    /// matched on the simple type name so a fully-qualified spelling counts.
    /// </summary>
    private static List<ObjectCreationExpressionSyntax> Constructions(SyntaxNode scope) =>
        scope.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(n => n.Type.ToString().Split('.').Last() == ServiceType)
            .ToList();

    /// <summary>
    /// The rule the subscription-shaped guard next door cannot express: the
    /// pipe is claimed by the constructor, so a second construction is a
    /// second claim on it whether or not anybody subscribes to the result.
    ///
    /// Checked over the whole corpus twice, by parse and by text, because the
    /// two owner files are exempt from nothing here and every other file is
    /// read through a parse that cannot see a disabled conditional region.
    /// </summary>
    [Fact]
    public void TheProcessConstructsExactlyOneThemePreviewService()
    {
        var corpus = ShellSource.AllUnder(Corpus);

        // Load-bearing: a prefix that stopped matching would pass this test
        // while reading nothing at all.
        Assert.True(
            corpus.Count > 100,
            $"expected the shell source corpus under '{Corpus}', got {corpus.Count} files");
        Assert.Single(corpus.Where(f => f.Tail.EndsWith(App, StringComparison.Ordinal)));
        Assert.Single(corpus.Where(f => f.Tail.EndsWith(Service, StringComparison.Ordinal)));

        var built = corpus
            .Where(f => Constructions(ShellSource.ParseForCorpusScan(f.Text).Root).Count > 0)
            .Select(f => f.Tail)
            .ToList();

        Assert.True(
            built.Count == 1 && built[0].EndsWith(App, StringComparison.Ordinal),
            "expected exactly one file to construct " + ServiceType + ", App; found "
            + (built.Count == 0 ? "none" : string.Join(", ", built))
            + ". The constructor is what claims the per-process pipe name, so a second "
            + "construction gets an IOException and a server loop that stands down before it "
            + "has served anything -- which is what a service per window was.");

        Assert.Single(Constructions(AppSource().Root));

        // And by text, which is the half that sees a construction parked in a
        // disabled `#if` region. That region ships in the configuration that
        // defines the symbol, and no parse in this file can read it.
        var pattern = new Regex(@"\bnew\s+(?:[\w]+\s*\.\s*)*" + ServiceType + @"\b", RegexOptions.Compiled);
        var mentions = corpus
            .SelectMany(f => pattern.Matches(f.Text).Select(_ => f.Tail))
            .ToList();
        Assert.True(
            mentions.Count == 1 && mentions[0].EndsWith(App, StringComparison.Ordinal),
            "expected one `new " + ServiceType + "` in the whole corpus, found "
            + mentions.Count + ": " + string.Join(", ", mentions));
    }

    /// <summary>
    /// The one construction runs once per process, on the framework's entry
    /// point, unconditionally.
    ///
    /// OnLaunched by name, not by whatever member happens to build the
    /// bootstrap host. Anchoring on a landmark proves only that the
    /// construction sits beside that landmark, and both move together for
    /// free: hoist the pair into one helper, call it from a per-window path,
    /// and every count here still reads one while a service accumulates per
    /// window.
    /// </summary>
    [Fact]
    public void TheServiceIsBuiltOnTheStartupPathAndNotUnderAnyBranch()
    {
        var app = AppSource();
        var startup = app.Method("OnLaunched");
        var construction = Assert.Single(Constructions(app.Root));

        Assert.Same(startup, construction.FirstAncestorOrSelf<MemberDeclarationSyntax>());

        // Unconditional within it, too. A guarded construction on OnLaunched
        // satisfies a count of one whenever the branch is taken once, and a
        // condition that reads as "the first window only" selecting more than
        // one window is the defect this file is about.
        var nested = construction.Ancestors()
            .TakeWhile(n => n != startup)
            .FirstOrDefault(n => n is IfStatementSyntax or SwitchStatementSyntax or ElseClauseSyntax
                or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax
                or DoStatementSyntax or TryStatementSyntax or LambdaExpressionSyntax
                or LocalFunctionStatementSyntax);

        Assert.True(
            nested is null,
            $"the {ServiceType} construction is inside a {nested?.Kind()}. It has to happen once, "
            + "unconditionally, on the one path that builds the bootstrap host.");
    }

    [Fact]
    public void AppSubscribesExactlyOnceWithANamedMethod()
    {
        var app = AppSource();
        var wires = Wires(app.Root, SyntaxKind.AddAssignmentExpression);

        Assert.True(
            wires.Count == 1,
            $"expected exactly one `+= {ListThemes}` in App; found {wires.Count}. A second "
            + "subscriber opens a second picker on the same request.");
        Assert.Equal("_themePreview", wires[0].Receiver);

        // A method group, so the teardown has something to name. An anonymous
        // lambda cannot be unsubscribed at all, and this subscription lasts as
        // long as the process.
        Assert.True(
            SyntaxFactory.ParseExpression(wires[0].Handler) is IdentifierNameSyntax,
            $"{ListThemes} must be handled by a named method, not `{wires[0].Handler}`: a lambda "
            + "cannot be detached, and the shutdown has to take this one back.");

        // Method() asserts there is exactly one, so a detach cannot name a
        // method that no longer exists and two overloads cannot make "the
        // handler" ambiguous.
        var handler = app.Method(wires[0].Handler);
        Assert.True(
            handler.Body is not null || handler.ExpressionBody is not null,
            $"{wires[0].Handler} has no body");
    }

    /// <summary>
    /// Both halves of the teardown, before the bootstrap host is freed.
    ///
    /// Detach and dispose are separate claims. The detach is what stops a
    /// LIST_THEMES that beat the cancel from reaching a window that is already
    /// tearing down; the dispose is what ends the accept loop and releases the
    /// pipe name. Ordering them before `_bootstrapHost?.Dispose` is the same
    /// requirement the app-action handlers have: work started during teardown
    /// must not reach native state the host is about to free.
    /// </summary>
    [Fact]
    public void TheSubscriptionAndTheServiceAreTornDownBeforeTheHostIsFreed()
    {
        var app = AppSource();
        var subscribes = Wires(app.Root, SyntaxKind.AddAssignmentExpression);
        var detaches = Wires(app.Root, SyntaxKind.SubtractAssignmentExpression);

        // The (event, receiver, handler) triple, not the event: a detach of
        // null, or of some other handler, satisfies a match on the event
        // alone.
        Assert.Equal(subscribes, detaches);

        var shutdown = app.Root.DescendantNodes().OfType<BlockSyntax>()
            .Where(b => b.Calls("_bootstrapHost?.Dispose").Count == 1)
            .OrderBy(b => b.Span.Length)
            .First()
            .Statements;

        var free = shutdown.TakeWhile(s => !s.Calls("_bootstrapHost?.Dispose").Any()).Count();
        Assert.True(free < shutdown.Count, "expected the shutdown block to dispose the bootstrap host");

        var detach = shutdown
            .TakeWhile(s => Assignments(s, SyntaxKind.SubtractAssignmentExpression).Count == 0)
            .Count();
        Assert.True(
            detach < shutdown.Count,
            $"{ListThemes} is detached somewhere other than the shutdown that disposes the host. "
            + "It is attached for the life of the process, so nothing else will.");
        Assert.True(detach < free, $"{ListThemes} must be detached before the ghostty app is freed");

        var dispose = shutdown.TakeWhile(s => !s.Calls("_themePreview.Dispose").Any()).Count();
        Assert.True(
            dispose < shutdown.Count,
            "the shutdown must dispose _themePreview: detaching alone leaves the accept loop "
            + "running and the per-process pipe name held with nobody listening on it");
        Assert.True(dispose < free, "the pipe server must be stopped before the ghostty app is freed");
    }

    /// <summary>
    /// The handler routes the request. It does not raise an event of its own.
    ///
    /// The cheapest way to put the defect back without naming the event is a
    /// relay: App keeps the one real subscription and re-broadcasts through a
    /// static event that per-window code subscribes to. Every other rule here
    /// passes, because the event name never leaves App. Refusing a delegate
    /// invocation inside the handler is not a proof against indirection in
    /// general -- nothing here can be -- but it is the difference between
    /// writing the relay by accident and having to route around a test.
    /// </summary>
    [Fact]
    public void TheHandlerRoutesTheRequestRatherThanRebroadcastingIt()
    {
        var app = AppSource();
        var handler = app.Method(Wires(app.Root, SyntaxKind.AddAssignmentExpression).Single().Handler);

        var raised = handler.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.CalleeText().EndsWith("Invoke", StringComparison.Ordinal))
            .ToList();
        Assert.True(
            raised.Count == 0,
            handler.Identifier.ValueText + " raises "
            + string.Join(", ", raised.Select(r => r.CalleeText()))
            + ". A process-wide request handled once and then fanned out to a per-window "
            + "subscriber is the defect this file exists to prevent, wearing another name.");

        // And it picks the window through the decision that has real unit
        // tests rather than re-deriving one inline, where it would be
        // exercised only by a person with two windows open.
        var chosen = handler.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.CalleeText().EndsWith("ActiveWindowTarget.Choose", StringComparison.Ordinal))
            .ToList();
        Assert.True(
            chosen.Count == 1,
            handler.Identifier.ValueText + " must route through ActiveWindowTarget.Choose; found "
            + chosen.Count + " call(s).");
    }

    /// <summary>
    /// The owner files are exempt from the text rule below, so a parse is all
    /// that reads them -- and a parse cannot see inside a disabled conditional
    /// region. Wrapping a second construction or a subscription in `#if !DEMO`
    /// would hide it from both rules at once while shipping in every non-DEMO
    /// build.
    ///
    /// <see cref="ShellSource.Load"/> refuses a file that still has disabled
    /// text after DEMO is defined, which is the check that closes it. Both
    /// owners go through Load rather than through the corpus scan's relaxed
    /// parse.
    /// </summary>
    [Fact]
    public void OwnerFilesHideNothingInAConditionalRegion()
    {
        // App is loaded by every other test here. The service is not, and it
        // is the one file where subscribing to its own event would not look
        // out of place.
        var service = ServiceSource();

        var wires = Wires(service.Root, SyntaxKind.AddAssignmentExpression);
        Assert.True(
            wires.Count == 0,
            ServiceType + " subscribes to its own " + ListThemes
            + ". It declares and raises this; a subscription here is a handler nobody detaches.");
        Assert.Empty(Constructions(service.Root));

        // Load-bearing: Load is what makes the above meaningful, and it is
        // silently satisfied by a file that parses to nothing.
        Assert.NotEmpty(service.Root.DescendantNodes().OfType<MethodDeclarationSyntax>());
    }

    /// <summary>
    /// The corpus rule: no file other than the service and App may so much as
    /// name the event.
    ///
    /// Text rather than syntax, deliberately. A parse cannot see inside a
    /// disabled conditional region, and a subscription reintroduced there is
    /// live in the configuration that defines the symbol. Matching the token
    /// in the raw source has no such blind spot. The cost is that a comment
    /// naming the event also fails -- including a comment explaining why a
    /// file must not subscribe. Reword it; the rule is worth more than the
    /// sentence.
    /// </summary>
    [Fact]
    public void NoOtherFileNamesTheThemePickerRequest()
    {
        var corpus = ShellSource.AllUnder(Corpus);

        Assert.True(
            corpus.Count > 100,
            $"expected the shell source corpus under '{Corpus}', got {corpus.Count} files");

        // The two files that live outside the Interop.Sources wildcard are in
        // the corpus, because a corpus keyed on that prefix alone silently
        // skipped them and they are shell sources like any other.
        Assert.Contains(corpus, f => f.Tail.EndsWith(".NativeMethods.cs", StringComparison.Ordinal));

        var pattern = new Regex(@"\b" + ListThemes + @"\b", RegexOptions.Compiled);
        var offenders = corpus
            .Where(f => !Owners.Any(o => f.Tail.EndsWith(o, StringComparison.Ordinal)) && pattern.IsMatch(f.Text))
            .Select(f => f.Tail)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "these files name " + ListThemes + ": " + string.Join(", ", offenders)
            + ". One service serves the whole process and App owns the one subscription; a "
            + "per-window subscriber is a window rooted on a service that outlives it, and a "
            + "per-window service is one that never gets the pipe. If the name is only in a "
            + "comment, reword the comment.");

        // And the parsed view agrees. It reads the owner files too, which the
        // text rule exempts, so this is not a second copy of the rule above:
        // it is the half that covers what the exemption opens up.
        foreach (var file in corpus.Where(f => !f.Tail.EndsWith(App, StringComparison.Ordinal)))
        {
            var wires = Wires(
                ShellSource.ParseForCorpusScan(file.Text).Root, SyntaxKind.AddAssignmentExpression);
            Assert.True(wires.Count == 0, $"{file.Tail} subscribes to {ListThemes}");
        }
    }
}
