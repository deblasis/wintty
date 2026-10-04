using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// That a settings picker commits when the user commits to a suggestion, and
/// not while the user is still looking at one.
///
/// <c>AutoSuggestBox</c> raises <c>SuggestionChosen</c> for every HIGHLIGHTED
/// item, not only for one that was picked: an arrow keypress moves the
/// highlight and the control raises exactly the event a click does. So a
/// handler that treats it as a commit writes the config on every keypress of
/// a browse, and the last thing written is whatever the highlight happened to
/// be sitting on when the user pressed Escape -- which commits nothing to
/// leave behind, so Escape never reverted anything either.
///
/// <c>SearchableList</c> is where that behaviour lived, and it is shared: the
/// theme boxes on the Colors page and the font box on Appearance all commit
/// through it, and the Colors chain writes <c>theme</c> as a
/// <c>light:..,dark:..</c> pair built from BOTH boxes. Arrow-browsing one of
/// them therefore rewrote the other half of a setting the user never chose.
///
/// The split this pins: <c>SuggestionChosen</c> mirrors the text and nothing
/// else, and a consumer's callback is reached from <c>Commit</c> and from
/// nowhere else. Commit itself is reached from two places, and both are named
/// here -- Enter (the box's <c>QuerySubmitted</c>) and a click (the same event
/// arriving with the suggestion list already closed, because a click closes it
/// and browsing is what keeps it open). Escape closes the list without
/// raising the event at all, so it is the one ending that reaches no commit.
///
/// What this cannot prove: that WinUI raises these events the way the
/// comments here claim. The shell is WinUI and cannot be loaded into a test
/// host, so which event a click, an arrow key and Escape produce is only
/// observable with a window open.
/// </summary>
public class SettingsPickerCommitWiringTests
{
    /// <summary>The shared picker, as a tail of the shell source corpus.</summary>
    private const string ListFile = "Ghostty.Settings.SearchableList.cs";

    /// <summary>The corpus prefix every embedded shell source is under.</summary>
    private const string Corpus = "Ghostty.Tests.";

    /// <summary>The consumer callback the class was handed.</summary>
    private const string ChosenField = "_onChosen";

    /// <summary>The one exit a commit goes through.</summary>
    private const string CommitMethod = "Commit";

    /// <summary>The handler that mirrors a highlighted suggestion.</summary>
    private const string HighlightHandler = "OnSuggestionChosen";

    /// <summary>The handler Enter reaches the box through.</summary>
    private const string SubmitHandler = "OnQuerySubmitted";

    /// <summary>The property that distinguishes a click from a browse.</summary>
    private const string ListOpen = "IsSuggestionListOpen";

    /// <summary>The event the box raises for Enter.</summary>
    private const string QuerySubmitted = "QuerySubmitted";

    private static ShellSource List() => ShellSource.Load(ListFile);

    /// <summary>
    /// Anything that can stand between a statement and the code after it.
    /// </summary>
    private static bool IsBranch(SyntaxNode node) =>
        node is IfStatementSyntax
            or ConditionalExpressionSyntax
            or SwitchStatementSyntax
            or SwitchExpressionSyntax
            or WhileStatementSyntax
            or ForStatementSyntax
            or ForEachStatementSyntax
            or DoStatementSyntax
            or TryStatementSyntax;

    /// <summary>
    /// Whether <paramref name="node"/> is reached from <paramref name="scope"/>
    /// with nothing in front of it that can decide otherwise: not nested in a
    /// branch inside the scope, and with no branch standing earlier in any
    /// block between them.
    ///
    /// Both halves are load-bearing. A call wrapped in `if (false && ...)`
    /// is the escape everyone thinks of; the cheaper one is a call left
    /// exactly where the query looks for it, under an early return that
    /// always fires:
    ///
    ///     if (_loading) return;
    ///     Commit(chosen);
    ///
    /// There the commit is a sibling of the guard, not inside it, so an
    /// ancestor walk finds nothing wrong while the commit is disabled
    /// outright. Deliberately blunt -- a harmless `if` in front of the call
    /// fails this too -- because deciding which early exits are benign is
    /// exactly how the disabling one stays in.
    /// </summary>
    private static bool ReachedUnconditionallyFrom(SyntaxNode node, SyntaxNode scope)
    {
        if (node.Ancestors().TakeWhile(a => a != scope).Any(IsBranch)) return false;

        foreach (var step in node.AncestorsAndSelf().TakeWhile(a => a != scope))
        {
            if (step.Parent is not BlockSyntax block) continue;
            if (block.Statements.TakeWhile(s => s != step).Any(IsBranch)) return false;
        }

        return true;
    }

    /// <summary>
    /// The one invocation of the consumer callback, and the method it sits in.
    ///
    /// Counted over the whole file rather than per handler, so a commit added
    /// to a second place is a failure here instead of a second policy nobody
    /// reads.
    /// </summary>
    [Fact]
    public void TheConsumerCallbackIsInvokedFromTheCommitAndNowhereElse()
    {
        var source = List();

        // CalleeText rather than a shape match, because the null-conditional
        // spelling hoists its receiver: `_onChosen?.Invoke(v)` parses with the
        // receiver outside the invocation, so the only stable handle on "who
        // was called" is the text the source spells it with.
        var invocations = source.Root.Calls(ChosenField + "?.Invoke").ToList();
        Assert.True(
            invocations.Count == 1,
            $"expected exactly one {ChosenField} invocation in {ListFile}, found "
            + $"{invocations.Count}. Every commit path has to converge on one place: two call "
            + "sites is two policies, and the second one is the one that ships the defect back");

        var owner = invocations[0].FirstAncestorOrSelf<MethodDeclarationSyntax>();
        Assert.True(
            owner is not null && owner.Identifier.ValueText == CommitMethod,
            $"{ChosenField} must be invoked inside {CommitMethod}, found it in "
            + $"'{owner?.Identifier.ValueText ?? "<no method>"}'. The commit paths are named by "
            + "the tests below; a callback invoked from one of them directly is the rule those "
            + "tests cannot see");

        // An empty value is not a pick, and the pair in the Colors chain needs
        // both halves, so the exit that drops one is the exit that keeps a
        // half-written pair off disk.
        var body = owner!.Body;
        Assert.True(
            body is not null,
            $"{CommitMethod} must have a body; an expression-bodied commit still has to be able "
            + "to drop an empty value, which is what the guard below is for");

        var guards = body!.Statements
            .OfType<IfStatementSyntax>()
            .Where(s => s.Statement.DescendantNodesAndSelf()
                .OfType<ReturnStatementSyntax>().Any())
            .ToList();
        Assert.True(
            guards.Count >= 1,
            $"{CommitMethod} must return early on a value it cannot commit. Empty is what an "
            + "Enter on an empty box, and on a cleared pair box, produces, and committing it "
            + "writes `theme = ` over a theme the user set elsewhere");

        // And the return has to be in front of the callback, not merely
        // somewhere in the method.
        Assert.True(
            guards.Min(g => g.SpanStart) < invocations[0].SpanStart,
            $"the empty-value guard in {CommitMethod} must sit above the {ChosenField} call: a "
            + "return after it never drops anything");
    }

    /// <summary>
    /// The highlight handler mirrors the text and commits nothing else.
    ///
    /// This is the defect itself. The handler is the same event for an arrow
    /// key and for a click, and the old body treated it as a commit in both
    /// cases, so every keypress of a browse wrote the config. Restore the
    /// bare invocation and every other rule here still passes: Enter still
    /// commits, the callback still has one call site, and the click path
    /// still works.
    /// </summary>
    [Fact]
    public void HighlightingASuggestionOnlyMirrorsTheText()
    {
        var handler = List().Method(HighlightHandler);

        var mirrors = handler.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Text" })
            .ToList();
        Assert.True(
            mirrors.Count == 1,
            $"{HighlightHandler} must mirror the highlighted text into the box exactly once, "
            + $"found {mirrors.Count} assignments to .Text. That mirroring is the preview the "
            + "user browses over; without it the box keeps the half-typed query while the "
            + "highlight moves");

        // The commit, if there is one, is a guarded statement rather than one of
        // the block's own. Matched on the block rather than by walking
        // ancestors, because a branch between the call and the handler is
        // exactly what this rule wants here: the one commit in this handler
        // IS inside an `if`, and a walk cannot tell that if from one that only
        // reads that way.
        var body = Assert.IsType<BlockSyntax>(handler.Body);
        var unguarded = body.Statements
            .OfType<ExpressionStatementSyntax>()
            .SelectMany(s => s.DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Where(i => i.CalleeText() == CommitMethod)
            .ToList();
        Assert.True(
            unguarded.Count == 0,
            $"{HighlightHandler} must not commit from a top-level statement: the event fires for "
            + "every highlighted item, so an unconditional commit here rewrites the config on each "
            + "arrow keypress and leaves the last previewed theme applied");
    }

    /// <summary>
    /// And the one commit inside it is the click, not the browse.
    ///
    /// A click closes the suggestion list and a browse keeps it open -- the
    /// point of arrowing is to go on choosing -- and that is the whole of what
    /// separates them, because WinUI offers no event for either. Pinned as
    /// the closed list rather than as "some condition": replacing it with
    /// `if (true)`, or with a flag that nothing ever sets, leaves a commit in
    /// the handler and brings back a per-keypress write.
    /// </summary>
    [Fact]
    public void TheOnlyCommitInTheHighlightHandlerIsTheClick()
    {
        var handler = List().Method(HighlightHandler);
        var commits = handler.Calls(CommitMethod);

        Assert.True(
            commits.Count == 1,
            $"{HighlightHandler} must contain exactly one {CommitMethod} call -- the click -- and "
            + $"found {commits.Count}. Zero means a click commits nothing, which trades a "
            + "destructive bug for a silent one; more than one is the write-back this is for");

        var click = commits[0];
        var guard = click.Ancestors().OfType<IfStatementSyntax>()
            .FirstOrDefault(i => i.Statement.Span.Contains(click.Span));
        Assert.True(
            guard is not null,
            $"the {CommitMethod} in {HighlightHandler} must sit inside a branch, so browsing "
            + "cannot reach it. It is the click that commits");

        // The condition, and its polarity: closed, not open. Flipping the
        // negation is the mutation this whole file exists to catch.
        var condition = guard!.Condition.DescendantNodesAndSelf()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.Name.Identifier.ValueText == ListOpen)
            .ToList();
        Assert.True(
            condition.Count == 1,
            $"the click branch must test {ListOpen} exactly once, found {condition.Count}. It is "
            + "the only thing that tells a click from a browse, so a condition that tests "
            + "anything else commits on the wrong one of them");

        // The negation is the rule. A click closes the list and a browse leaves it
        // open, so testing it the other way round commits on every arrow
        // keypress -- which is the defect this file exists to keep out. Read
        // as a prefix operator over the property access rather than off the
        // access itself, because that is the shape `!x.IsSuggestionListOpen`
        // parses to.
        var negated = condition[0].Parent is PrefixUnaryExpressionSyntax prefix
            && prefix.IsKind(SyntaxKind.LogicalNotExpression)
            && prefix.Operand == condition[0];
        Assert.True(
            negated,
            $"the click branch must read `!{ListOpen}`: a click closes the list and browsing keeps "
            + "it open. Testing it the other way round commits on every arrow keypress, which is "
            + "the defect");
    }

    /// <summary>
    /// Enter commits, and nothing stands in front of it.
    ///
    /// The commit is only useful if the obvious way to type one still lands.
    /// A guard that reads a field nothing sets, or a commit behind a branch
    /// the test cannot see into, leaves Enter writing nothing while this file
    /// reads as wired.
    /// </summary>
    [Fact]
    public void EnterCommitsTheQuery()
    {
        var source = List();

        var subscriptions = source.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression))
            .Where(a => a.Left is MemberAccessExpressionSyntax member
                        && member.Name.Identifier.ValueText == QuerySubmitted)
            .ToList();
        Assert.True(
            subscriptions.Count == 1,
            $"expected exactly one `{QuerySubmitted} +=` subscription in {ListFile}, found "
            + $"{subscriptions.Count}. Without it there is no Enter path at all and a picker "
            + "can only be driven by a click");

        var handler = source.Method(SubmitHandler);
        var commits = handler.Calls(CommitMethod);
        Assert.True(
            commits.Count == 1,
            $"{SubmitHandler} must commit exactly once, found {commits.Count} calls to "
            + $"{CommitMethod}");
        Assert.True(
            ReachedUnconditionallyFrom(commits[0], handler),
            $"the {CommitMethod} in {SubmitHandler} must be reached whenever the handler runs. An "
            + "early return or a branch in front of it disables Enter while leaving the call "
            + "exactly where this query finds it");
    }

    /// <summary>
    /// Every picker in the tree is one of these boxes, so the census above
    /// covers the surface and not just the class.
    ///
    /// Load-bearing in the usual way: a prefix that stopped matching would
    /// pass this while reading nothing. And each site must hand the helper a
    /// box that some settings page names, so a consumer cannot move its
    /// wiring out of the markup the other half of these guards reads.
    /// </summary>
    [Fact]
    public void TheConsumerCensusFindsEveryPickerAndNamesItsBox()
    {
        var named = NamedControlsInPages();

        var sites = new List<(string File, string Box)>();
        foreach (var (tail, text) in ShellSource.AllUnder(Corpus))
        {
            if (tail.EndsWith(ListFile, StringComparison.Ordinal)) continue;

            foreach (var creation in ShellSource.ParseForCorpusScan(text).Root
                         .DescendantNodes()
                         .OfType<ObjectCreationExpressionSyntax>()
                         .Where(o => o.Type.ToString() == "SearchableList"))
            {
                var box = creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                Assert.True(
                    box is IdentifierNameSyntax,
                    $"{tail}: a SearchableList must be built with a named box as its first "
                    + $"argument, found '{box}'");
                sites.Add((tail, ((IdentifierNameSyntax)box!).Identifier.ValueText));
            }
        }

        Assert.True(
            sites.Count >= 4,
            $"expected the settings pages to build at least four SearchableLists (the three "
            + $"theme boxes and the font box), found {sites.Count}. A census that stopped "
            + "matching reads as \"nothing to fix\"");

        var strays = sites
            .Where(s => !named.Contains(s.Box))
            .Select(s => $"{s.File}: {s.Box}")
            .ToList();
        Assert.True(
            strays.Count == 0,
            "every SearchableList box must be an x:Name in a settings page, so the page's own "
            + "guards can find it. Not named in markup: " + string.Join(", ", strays));
    }

    /// <summary>Every x:Name declared by any settings page XAML.</summary>
    private static HashSet<string> NamedControlsInPages()
    {
        var prefix = "Ghostty.Tests.Settings.Pages.";
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
        {
            if (!resource.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!resource.EndsWith(".xaml", StringComparison.Ordinal)) continue;

            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(resource)!;
            var document = XDocument.Load(stream);
            foreach (var element in document.Descendants())
            {
                var name = element.Attribute(XamlNamespace + "Name")?.Value;
                if (!string.IsNullOrEmpty(name)) names.Add(name!);
            }
        }

        return names;
    }

    private static readonly XNamespace XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";
}