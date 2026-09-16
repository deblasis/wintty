using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The hidden quick terminal must never show up before it is summoned.
///
/// App builds the quick-terminal window ahead of the first summon, activates
/// it once so its content loads, and hides it straight away. That
/// activate-then-hide used to paint: the window was not at its quake position
/// yet (only Show() moves it there), and its constructor had applied the
/// regular window's saved placement, so for a few hundred milliseconds a
/// borderless, empty window covered the window the user had just watched
/// render.
///
/// Two halves, pinned separately because either one alone still flashes:
/// the constructor cloaks the quick terminal and takes no saved placement,
/// and Show() lifts the cloak only after MoveToQuakePosition and before the
/// window is shown.
///
/// Asserted as a tree, not as text, like the rest of the wiring guards: the
/// point is WHICH branch a call sits in and in what order, which a substring
/// check cannot see. Containment is not enough either: a call nested one
/// condition deeper is still inside the right span and runs only sometimes,
/// so each load-bearing statement is pinned to the exact block it must sit in.
/// </summary>
public class QuickTerminalPrewarmWiringTests
{
    [Fact]
    public void TheQuickTerminal_IsCloakedAtConstruction_AndTakesNoSavedPlacement()
    {
        var win = ShellSource.Load("MainWindow.xaml.cs");

        // The constructor that reads window-state.json is the one that
        // applies placement; the other constructors chain to it.
        var ctor = Assert.Single(
            win.Root.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
                .Where(c => c.Body is not null && c.Body.Calls("WindowState.Load").Count > 0)
                .ToList());

        var quick = Assert.Single(
            ctor.Body!.DescendantNodes().OfType<IfStatementSyntax>()
                .Where(i => i.Condition is IdentifierNameSyntax { Identifier.ValueText: "IsQuickTerminal" })
                .Where(i => i.Statement.Calls("CloakUntilFirstShow").Count > 0)
                .ToList());

        // The branch runs for every quick terminal: it sits directly in the
        // constructor body, not under some further condition.
        Assert.Same(ctor.Body, quick.Parent);

        // And the cloak runs every time the branch does: a bare statement of
        // the branch itself, not one wrapped in a condition of its own
        // (`if (false) CloakUntilFirstShow();` is still inside the branch).
        var cloakCall = Assert.Single(quick.Statement.Calls("CloakUntilFirstShow"));
        var cloakStatement = Assert.IsType<ExpressionStatementSyntax>(cloakCall.Parent);
        Assert.True(
            ReferenceEquals(cloakStatement, quick.Statement)
                || ReferenceEquals(cloakStatement.Parent, quick.Statement),
            "CloakUntilFirstShow() must be a statement of the quick-terminal branch itself, "
                + "not nested under another condition.");

        // Regular windows still restore their placement, and only from the
        // else side of the quick-terminal branch.
        var placements = ctor.Body.Calls("ApplyGeometry")
            .Concat(ctor.Body.Calls("RestoreWindowPlacement"))
            .ToList();
        Assert.NotEmpty(placements);
        Assert.NotNull(quick.Else);
        Assert.All(placements, call => Assert.True(
            quick.Else!.Span.Contains(call.Span),
            $"'{call}' is reachable for the quick terminal. window-state.json is the "
                + "regular window's placement, and applying it parks the hidden quick "
                + "terminal on top of that window."));

        // The cloak is DWMWA_CLOAK, switched on, and remembered for Show().
        var cloak = win.Method("CloakUntilFirstShow");
        var on = cloak.Call("SetCloaked");
        Assert.True(
            on.ArgExpression(0).IsKind(SyntaxKind.TrueLiteralExpression),
            "CloakUntilFirstShow must cloak (SetCloaked(true)).");
        Assert.Contains(
            cloak.AssignsTo("_cloakedUntilFirstShow"),
            a => a.Right is InvocationExpressionSyntax i && i.CalleeText() == "SetCloaked");

        var set = win.Method("SetCloaked");
        var dwm = set.Call("PInvoke.DwmSetWindowAttribute");
        Assert.EndsWith("DWMWINDOWATTRIBUTE.DWMWA_CLOAK", dwm.Arg(1));
        // The attribute value is the parameter, not a constant: a hard-coded
        // TRUE would turn the uncloak in Show() into a second cloak.
        Assert.Contains(
            set.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            v => v.Identifier.ValueText == "value"
                 && v.Initializer?.Value is IdentifierNameSyntax { Identifier.ValueText: "cloaked" });
        Assert.Equal("&value", dwm.Arg(2));
    }

    [Fact]
    public void Show_LiftsTheCloak_AfterMoveToQuakePosition_AndBeforeTheWindowAppears()
    {
        var show = ShellSource.Load("MainWindow.xaml.cs").Method("Show");

        var move = show.Call("MoveToQuakePosition");
        var appear = show.Call("AppWindow.Show");
        var uncloak = show.Call("SetCloaked");
        Assert.True(
            uncloak.ArgExpression(0).IsKind(SyntaxKind.FalseLiteralExpression),
            "Show() must uncloak (SetCloaked(false)).");

        // The block that runs the move, i.e. every show of a hidden window.
        var moveStatement = Assert.IsType<ExpressionStatementSyntax>(move.Parent);
        var block = Assert.IsType<BlockSyntax>(moveStatement.Parent);

        // The uncloak's guard is the one-shot flag, and that guard is a
        // statement of the move's block itself. Anywhere deeper runs only
        // sometimes: inside the animation's `if (duration > 0)` it would never
        // run with quick-terminal-animation-duration = 0, and the quick
        // terminal would stay cloaked, invisible on every summon.
        var guard = Assert.IsType<IfStatementSyntax>(
            uncloak.Ancestors().FirstOrDefault(n => n is IfStatementSyntax));
        Assert.True(
            guard.Condition is IdentifierNameSyntax { Identifier.ValueText: "_cloakedUntilFirstShow" },
            $"the uncloak must be guarded by _cloakedUntilFirstShow alone, found '{guard.Condition}'.");
        Assert.True(
            ReferenceEquals(guard.Parent, block),
            "the _cloakedUntilFirstShow guard must be a statement of the block that calls "
                + "MoveToQuakePosition, not nested under another condition.");
        var uncloakStatement = Assert.IsType<ExpressionStatementSyntax>(uncloak.Parent);
        Assert.True(
            ReferenceEquals(uncloakStatement, guard.Statement)
                || ReferenceEquals(uncloakStatement.Parent, guard.Statement),
            "SetCloaked(false) must be a statement of the guard itself.");

        // The window appears from the same block.
        var appearStatement = Assert.IsType<ExpressionStatementSyntax>(appear.Parent);
        Assert.True(
            ReferenceEquals(appearStatement.Parent, block),
            "AppWindow.Show must be a statement of the block that calls MoveToQuakePosition.");

        // Uncloaked before the move, the first visible frame is the stale
        // rect; after AppWindow.Show(), the summon itself shows nothing.
        Assert.True(
            move.SpanStart < uncloak.SpanStart && uncloak.SpanStart < appear.SpanStart,
            "Order must be MoveToQuakePosition, then SetCloaked(false), then AppWindow.Show.");

        // One-shot: the flag comes down with the cloak, under the same guard.
        Assert.Contains(
            show.AssignsTo("_cloakedUntilFirstShow"),
            a => a.Right.IsKind(SyntaxKind.FalseLiteralExpression)
                 && guard.Statement.Span.Contains(a.Span));
    }

    [Fact]
    public void TheFirstSummon_IsPlacedOnTheLastRegularWindowsMonitor()
    {
        // With no saved placement the hidden window sits wherever the OS
        // created it, so resolving quick-terminal-screen = main from its own
        // window would put the first summon on the primary monitor while the
        // user works on another one.
        var win = ShellSource.Load("MainWindow.xaml.cs");

        var resolve = win.Method("MoveToQuakePosition").Call("QuickTerminalMonitorResolver.Resolve");
        resolve.ArgExpression(0).AssertCallTo("QuakeMonitorAnchor");

        var anchor = win.Method("QuakeMonitorAnchor");
        var own = Assert.Single(anchor.ParameterList.Parameters).Identifier.ValueText;

        // Placed once, the window stays on its own monitor.
        var placed = Assert.IsType<IfStatementSyntax>(anchor.Body!.Statements[0]);
        Assert.True(
            placed.Condition is IdentifierNameSyntax { Identifier.ValueText: "_quakePlacedOnce" },
            $"QuakeMonitorAnchor must start by checking _quakePlacedOnce, found '{placed.Condition}'.");
        var early = Assert.IsType<ReturnStatementSyntax>(placed.Statement);
        Assert.True(
            early.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == own,
            "a placed quick terminal resolves its monitor from its own window.");

        // The flag goes up on the first placement, so only that one follows
        // the regular window.
        Assert.Contains(
            anchor.AssignsTo("_quakePlacedOnce"),
            a => a.Right.IsKind(SyntaxKind.TrueLiteralExpression));

        // And the first placement follows the last regular window: the
        // closing return picks that window's handle when there is one, and
        // falls back to the quick terminal's own.
        var last = Assert.IsType<ReturnStatementSyntax>(anchor.Body.Statements[^1]);
        var choice = Assert.IsType<ConditionalExpressionSyntax>(last.Expression);
        var test = Assert.IsType<IsPatternExpressionSyntax>(choice.Condition);
        Assert.Equal("App.LastRegularWindow", test.Expression.ToString());
        var pattern = Assert.IsType<RecursivePatternSyntax>(test.Pattern);
        var regular = Assert.IsType<SingleVariableDesignationSyntax>(pattern.Designation).Identifier.ValueText;
        var handle = choice.WhenTrue.AssertCallTo("WindowNative.GetWindowHandle");
        Assert.Equal(regular, handle.Arg(0));
        Assert.True(
            choice.WhenFalse is IdentifierNameSyntax fallback && fallback.Identifier.ValueText == own,
            "with no regular window to follow, the quick terminal falls back to its own window.");
    }
}
