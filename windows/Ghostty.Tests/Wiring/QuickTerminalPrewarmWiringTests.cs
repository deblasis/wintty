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
/// check cannot see.
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
        Assert.Single(quick.Statement.Calls("CloakUntilFirstShow"));

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

        // Same not-yet-visible block as the move and the show, in that order:
        // uncloaked before the move, the first visible frame is the stale
        // rect; after AppWindow.Show(), the summon itself shows nothing.
        var block = move.Ancestors().OfType<BlockSyntax>().First();
        Assert.True(block.Span.Contains(uncloak.Span), "the uncloak must sit in the same block as MoveToQuakePosition.");
        Assert.True(block.Span.Contains(appear.Span), "AppWindow.Show must sit in the same block as MoveToQuakePosition.");
        Assert.True(
            move.SpanStart < uncloak.SpanStart && uncloak.SpanStart < appear.SpanStart,
            "Order must be MoveToQuakePosition, then SetCloaked(false), then AppWindow.Show.");

        // One-shot: the flag comes down with the cloak.
        Assert.Contains(
            show.AssignsTo("_cloakedUntilFirstShow"),
            a => a.Right.IsKind(SyntaxKind.FalseLiteralExpression));
    }
}
