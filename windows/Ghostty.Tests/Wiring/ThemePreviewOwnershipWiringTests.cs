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
/// further indirection. <see cref="TheHandlerShowsThePickerOnTheOneWindowItChose"/>
/// closes the shape that is cheap to write and invisible in review -- App
/// handling the request once and then reaching more than one window with it --
/// by pinning what the handler must do rather than listing what it must not.
/// The rest is left to the reader.
/// </summary>
public class ThemePreviewOwnershipWiringTests
{
    /// <summary>The event the pipe server raises when a LIST_THEMES arrives.</summary>
    private const string ListThemes = "ListThemesRequested";

    /// <summary>The service whose constructor claims the pipe name.</summary>
    private const string ServiceType = "ThemePreviewService";

    /// <summary>The field App holds the one service in.</summary>
    private const string ServiceField = "_themePreview";

    /// <summary>The member that puts a window into the registry.</summary>
    private const string Registration = "NoteRegularWindowRegistered";

    /// <summary>The registry a window is registered into.</summary>
    private const string Registry = "WindowsByRoot";

    /// <summary>The one verb a routed request performs on its window.</summary>
    private const string Picker = "ShowInlineThemePicker";

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
    /// Every construction of the service under <paramref name="scope"/>,
    /// matched on the simple type name so a fully-qualified spelling counts.
    ///
    /// Both spellings of `new`. A target-typed `new(...)` parses as
    /// ImplicitObjectCreationExpressionSyntax, which is a sibling of
    /// ObjectCreationExpressionSyntax under BaseObjectCreationExpressionSyntax
    /// rather than a subtype of it, so a filter on the latter reads a file
    /// full of `= new()` -- which this codebase writes freely -- as
    /// constructing nothing at all. It carries no type of its own either, so
    /// the type has to come from what it initialises: a declaration that
    /// names it, or a field or property in the same file that does.
    /// </summary>
    private static List<BaseObjectCreationExpressionSyntax> Constructions(SyntaxNode scope)
    {
        var typed = MembersDeclaredAsTheService(scope);

        return scope.DescendantNodes()
            .OfType<BaseObjectCreationExpressionSyntax>()
            .Where(n => n is ObjectCreationExpressionSyntax spelled
                ? SimpleName(spelled.Type.ToString()) == ServiceType
                : BuildsTheService(n, typed))
            .ToList();
    }

    /// <summary>The type name with any qualifier and `?` dropped.</summary>
    private static string SimpleName(string type) =>
        type.Trim().TrimEnd('?').Split('.').Last();

    /// <summary>
    /// The fields and properties in this file declared as the service, which
    /// is what gives `_themePreview = new(...)` a type to be read against.
    /// </summary>
    private static HashSet<string> MembersDeclaredAsTheService(SyntaxNode scope)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in scope.DescendantNodes().OfType<FieldDeclarationSyntax>())
            if (SimpleName(field.Declaration.Type.ToString()) == ServiceType)
                foreach (var variable in field.Declaration.Variables)
                    names.Add(variable.Identifier.ValueText);

        foreach (var property in scope.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            if (SimpleName(property.Type.ToString()) == ServiceType)
                names.Add(property.Identifier.ValueText);

        return names;
    }

    /// <summary>
    /// Whether a target-typed `new(...)` is building the service: either the
    /// declaration it initialises names the type, or it is assigned to a
    /// member of this file that was declared with it.
    /// </summary>
    private static bool BuildsTheService(SyntaxNode creation, HashSet<string> typed)
    {
        if (creation.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }
            && declarator.Parent is VariableDeclarationSyntax declaration)
            return SimpleName(declaration.Type.ToString()) == ServiceType;

        if (creation.Parent is AssignmentExpressionSyntax assignment)
            return typed.Contains(LastIdentifier(assignment.Left));

        return false;
    }

    /// <summary>The last identifier of a dotted expression, or empty.</summary>
    private static string LastIdentifier(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax name => name.Identifier.ValueText,
        _ => string.Empty,
    };

    /// <summary>The last dotted segment of a callee, `?` dropped.</summary>
    private static string LastSegment(string callee)
    {
        var dot = callee.LastIndexOf('.');
        return (dot < 0 ? callee : callee[(dot + 1)..]).Trim();
    }

    /// <summary>
    /// The identifier a call is made on. `target.Show()` and `target?.Show()`
    /// are the same receiver; a call on anything that is not a plain
    /// identifier comes back as written, which is enough to fail a
    /// comparison against one.
    /// </summary>
    private static string ReceiverIdentifier(InvocationExpressionSyntax call)
    {
        var callee = call.CalleeText();
        var dot = callee.LastIndexOf('.');
        return dot < 0 ? string.Empty : callee[..dot].TrimEnd('?', ' ');
    }

    /// <summary>
    /// Every call of <paramref name="verb"/> on something that names the
    /// service. Matched on the receiver rather than on the one field, so an
    /// accessor handing the service back out is covered by the same rule.
    /// </summary>
    private static List<InvocationExpressionSyntax> ServiceCalls(SyntaxNode scope, string verb) =>
        scope.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => LastSegment(call.CalleeText()) == verb
                && ReceiverIdentifier(call).Contains("themePreview", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// The same rule by text, which is the half that sees a call parked in a
    /// disabled conditional region. Returns the files that match.
    /// </summary>
    private static List<string> ServiceCallText(
        string verb, IReadOnlyList<(string Tail, string Text)> corpus)
    {
        var pattern = new Regex(
            @"[Tt]hemePreview\w*\s*\??\.\s*" + verb + @"\s*\(", RegexOptions.Compiled);
        return corpus.Where(f => pattern.IsMatch(f.Text)).Select(f => f.Tail).ToList();
    }

    /// <summary>
    /// How many times <paramref name="scope"/> writes the window registry:
    /// an insert through the indexer, or a mutating dictionary call on it.
    /// </summary>
    private static int RegistryWrites(SyntaxNode scope)
    {
        var inserts = scope.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Count(a => a.Left is ElementAccessExpressionSyntax element
                && LastIdentifier(element.Expression) == Registry);

        var mutations = scope.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Count(call => LastSegment(call.CalleeText())
                    is "Add" or "TryAdd" or "Remove" or "Clear"
                && ReceiverIdentifier(call).Split('.').Last().TrimEnd('!') == Registry);

        return inserts + mutations;
    }

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
        //
        // Three spellings, because a target-typed `new(...)` never writes the
        // type next to the `new`: the explicit form, a declaration that names
        // the type and defers, and an assignment to the field App holds it
        // in. The alternatives cannot both match one construction, so a count
        // stays a count.
        var pattern = new Regex(
            @"\bnew\s+(?:[\w]+\s*\.\s*)*" + ServiceType + @"\b"
            + @"|\b" + ServiceType + @"\b\??\s+\w+\s*=\s*new\s*\("
            + @"|\b" + ServiceField + @"\s*=\s*new\s*\(",
            RegexOptions.Compiled);
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
        Assert.Equal(ServiceField, wires[0].Receiver);

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
    /// The handler shows the picker on the one window it chose, and on no
    /// other.
    ///
    /// Stated as a shape the handler must have rather than as a list of
    /// shapes it must not. The defect wearing another name is a fan-out: App
    /// keeps the one real subscription and reaches every window anyway, by
    /// re-broadcasting through an event of its own, by calling a delegate it
    /// stashed, or by looping the registry next to the one legitimate call.
    /// A blacklist catches whichever spelling it was written against and none
    /// of the others -- a delegate invoked through a local reads as
    /// `relay(e)`, which is not the word "Invoke" anywhere. Pinning the
    /// positive shape (one picker call, on the identifier Choose's result
    /// went into) refuses the fan-out without having to have anticipated how
    /// it was spelled, because a second recipient is a second call site by
    /// construction.
    /// </summary>
    [Fact]
    public void TheHandlerShowsThePickerOnTheOneWindowItChose()
    {
        var app = AppSource();
        var handler = app.Method(Wires(app.Root, SyntaxKind.AddAssignmentExpression).Single().Handler);

        // It picks the window through the decision that has real unit tests
        // rather than re-deriving one inline, where it would be exercised
        // only by a person with two windows open.
        var chosen = handler.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.CalleeText().EndsWith("ActiveWindowTarget.Choose", StringComparison.Ordinal))
            .ToList();
        Assert.True(
            chosen.Count == 1,
            handler.Identifier.ValueText + " must route through ActiveWindowTarget.Choose; found "
            + chosen.Count + " call(s).");

        // Into a local, which is what the picker call then has to name. A
        // Choose whose result is used inline leaves nothing for the call
        // below to be checked against.
        var target = chosen[0].FirstAncestorOrSelf<VariableDeclaratorSyntax>();
        Assert.True(
            target is not null,
            "the ActiveWindowTarget.Choose result must be held in a local, so the picker call "
            + "can be checked against it.");

        var shown = handler.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => LastSegment(call.CalleeText()) == Picker)
            .ToList();
        Assert.True(
            shown.Count == 1,
            handler.Identifier.ValueText + " makes " + shown.Count + " " + Picker
            + " call(s), expected one: " + string.Join(", ", shown.Select(c => c.CalleeText()))
            + ". One process-wide request opens one picker; a second call site is the fan-out "
            + "this file exists to prevent.");

        Assert.Equal(target!.Identifier.ValueText, ReceiverIdentifier(shown[0]));
    }

    /// <summary>
    /// App is the only place that disposes the service.
    ///
    /// The inverse of the census entry that came out with the per-window
    /// field. Disposing this from a window's close is one line, it compiles,
    /// and every other rule in this file stays green -- but it ends the
    /// accept loop and drops the subscription for every window still open,
    /// which is the defect the ownership move removed. Written against any
    /// receiver that names the service rather than against the one field, so
    /// re-exposing the service through an accessor and disposing that is
    /// caught too.
    /// </summary>
    [Fact]
    public void OnlyAppDisposesTheService()
    {
        var corpus = ShellSource.AllUnder(Corpus);
        Assert.True(
            corpus.Count > 100,
            $"expected the shell source corpus under '{Corpus}', got {corpus.Count} files");

        var disposers = corpus
            .Where(f => ServiceCalls(ShellSource.ParseForCorpusScan(f.Text).Root, "Dispose").Count > 0)
            .Select(f => f.Tail)
            .ToList();

        Assert.True(
            disposers.Count == 1 && disposers[0].EndsWith(App, StringComparison.Ordinal),
            "expected only App to dispose " + ServiceType + "; found "
            + (disposers.Count == 0 ? "nobody" : string.Join(", ", disposers))
            + ". One service serves the process, so a dispose anywhere else takes the pipe "
            + "away from every window that is still open.");

        // And by text, for the call parked in a disabled `#if` region that no
        // parse in this file can read.
        var mentions = ServiceCallText("Dispose", corpus);
        Assert.True(
            mentions.Count == 1 && mentions[0].EndsWith(App, StringComparison.Ordinal),
            "expected one dispose of " + ServiceType + " in the whole corpus, found "
            + mentions.Count + ": " + string.Join(", ", mentions));
    }

    /// <summary>
    /// The pipe opens when a window registers, and not before.
    ///
    /// The pipe's existence is the readiness signal: `wintty +list-themes`
    /// probes for it with File.Exists and counts a successful write as
    /// delivery, never waiting for an answer. Opened at construction it
    /// exists within milliseconds of OnLaunched, which returns before the
    /// message loop can raise any window's Loaded -- so for the whole of
    /// startup the CLI connects, writes, exits 0, and the app logs that it
    /// had no window to draw on. The user gets no picker and no fallback
    /// either, because finding no pipe is exactly what sends the CLI to
    /// libghostty's own TUI picker.
    ///
    /// Pinned to the registration member by NAME, for the reason the
    /// construction rule pins OnLaunched: anchoring on a landmark proves only
    /// that the call sits beside that landmark, and both move together for
    /// free.
    /// </summary>
    [Fact]
    public void TheServerStartsWhenAWindowRegistersAndNotBefore()
    {
        var corpus = ShellSource.AllUnder(Corpus);
        Assert.True(
            corpus.Count > 100,
            $"expected the shell source corpus under '{Corpus}', got {corpus.Count} files");

        var starters = corpus
            .Where(f => ServiceCalls(ShellSource.ParseForCorpusScan(f.Text).Root, "Start").Count > 0)
            .Select(f => f.Tail)
            .ToList();
        Assert.True(
            starters.Count == 1 && starters[0].EndsWith(App, StringComparison.Ordinal),
            "expected exactly one file to start " + ServiceType + ", App; found "
            + (starters.Count == 0 ? "none" : string.Join(", ", starters)));

        var mentions = ServiceCallText("Start", corpus);
        Assert.True(
            mentions.Count == 1 && mentions[0].EndsWith(App, StringComparison.Ordinal),
            "expected one start of " + ServiceType + " in the whole corpus, found "
            + mentions.Count + ": " + string.Join(", ", mentions));

        var start = Assert.Single(ServiceCalls(AppSource().Root, "Start"));
        var member = start.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        Assert.True(
            member?.Identifier.ValueText == Registration,
            "the " + ServiceType + " start is in " + (member?.Identifier.ValueText ?? "no method")
            + ", not " + Registration + ". The pipe may not exist before some window can host a "
            + "picker, and registering is the moment one can.");
    }

    /// <summary>
    /// App is the only writer of the window registry.
    ///
    /// Registering a window is what opens the pipe, so a window that inserts
    /// itself directly registers without the side effect -- and the rule
    /// above would still find its one start call, in App, in the right
    /// member, never reached. Reads are left alone: several files resolve
    /// their owning window through this dictionary and always have.
    /// </summary>
    [Fact]
    public void OnlyAppWritesTheWindowRegistry()
    {
        var corpus = ShellSource.AllUnder(Corpus);
        Assert.True(
            corpus.Count > 100,
            $"expected the shell source corpus under '{Corpus}', got {corpus.Count} files");

        var writers = corpus
            .Where(f => RegistryWrites(ShellSource.ParseForCorpusScan(f.Text).Root) > 0)
            .Select(f => f.Tail)
            .ToList();
        Assert.True(
            writers.Count == 1 && writers[0].EndsWith(App, StringComparison.Ordinal),
            "expected only App to write " + Registry + "; found "
            + (writers.Count == 0 ? "nobody" : string.Join(", ", writers))
            + ". Registering a window is also what opens the +list-themes pipe, so an insert "
            + "that goes around App is a window the pipe never learns about.");

        // And by text, for the insert parked in a disabled `#if` region.
        var pattern = new Regex(
            @"\b" + Registry + @"\s*\[[^\]]*\]\s*=(?!=)"
            + @"|\b" + Registry + @"\s*\??\.\s*(?:Add|TryAdd|Remove|Clear)\s*\(",
            RegexOptions.Compiled);
        var mentions = corpus
            .Where(f => !f.Tail.EndsWith(App, StringComparison.Ordinal) && pattern.IsMatch(f.Text))
            .Select(f => f.Tail)
            .ToList();
        Assert.True(
            mentions.Count == 0,
            "these files write " + Registry + ": " + string.Join(", ", mentions));
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
