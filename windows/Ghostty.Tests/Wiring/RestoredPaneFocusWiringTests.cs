using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// #815: a window whose panes were rebuilt by a session restore came up
/// with no keyboard focus in any of them, so every chord -- palette, new
/// tab -- was dead until the user clicked a pane. The restore path makes
/// no focus call of its own; the only ask is the load-time
/// <c>TerminalControl.OnLoaded</c> focus, which a window still behind the
/// splash cannot take, and which lands on whichever restored leaf loads
/// last rather than the active one.
///
/// The fix is a one-shot focus of the active leaf on the window's first
/// activation, through the same <see cref="MainWindow"/> seam the palette
/// and notification focus-returns use. These guards pin that wiring; they
/// cannot prove keyboard focus actually lands, which is only observable
/// on a live UIA tree.
/// </summary>
public class RestoredPaneFocusWiringTests
{
    private static ShellSource MainWindow() => ShellSource.Load("MainWindow.xaml.cs");

    /// <summary>
    /// The one-shot handler, as a node. A local function in the window's
    /// constructor, twin of the session-restored announcement arm.
    /// </summary>
    private static LocalFunctionStatementSyntax Handler()
    {
        var found = MainWindow().Root.DescendantNodes()
            .OfType<LocalFunctionStatementSyntax>()
            .Where(f => f.Identifier.ValueText == "OnFirstActivationFocusActiveLeaf")
            .ToList();
        Assert.True(found.Count == 1,
            $"expected one local function OnFirstActivationFocusActiveLeaf, found {found.Count}");
        return found[0];
    }

    /// <summary>
    /// Every `Activated += OnFirstActivationFocusActiveLeaf` registration.
    /// Matched structurally so a comment or a string can never satisfy it.
    /// </summary>
    private static List<AssignmentExpressionSyntax> Registrations() =>
        MainWindow().Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax { Identifier.ValueText: "Activated" }
                        && a.IsKind(SyntaxKind.AddAssignmentExpression)
                        && a.Right is IdentifierNameSyntax
                        {
                            Identifier.ValueText: "OnFirstActivationFocusActiveLeaf"
                        })
            .ToList();

    [Fact]
    public void FirstActivation_ArmsTheFocusOnEveryRegularWindow()
    {
        // Exactly one registration, and it lives inside the regular-window
        // gate: the quake window's Show() owns its own focus choreography,
        // and a one-shot that fired on the hidden quake window's startup
        // activation would be spent before the user ever summoned it.
        var registrations = Registrations();
        Assert.Single(registrations);
        var gate = registrations[0].Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        Assert.NotNull(gate);
        Assert.Contains("!IsQuickTerminal", gate.Condition.ToString());
    }

    [Fact]
    public void TheHandler_IsOneShot_AndWaitsForARealActivation()
    {
        var handler = Handler();

        // One-shot: it takes itself back off, so the arm can never fight a
        // later deliberate focus (search box, palette) on reactivation.
        Assert.Contains(handler.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left is IdentifierNameSyntax { Identifier.ValueText: "Activated" }
                 && a.IsKind(SyntaxKind.SubtractAssignmentExpression));

        // The startup dance (the hidden quake window's activate-and-hide)
        // deactivates this window before it settles, so a Deactivated edge
        // must not spend the one shot.
        var guard = Assert.Single(handler.DescendantNodes().OfType<IfStatementSyntax>());
        Assert.Contains("Deactivated", guard.Condition.ToString());
    }

    [Fact]
    public void TheHandler_FocusesTheActivePaneThroughTheSharedSeam()
    {
        // FocusActiveLeaf resolves the active tab's active leaf at run
        // time, which is what makes this heal a restore whose leaves
        // loaded in the wrong focus order. Pinned as the single effect so
        // the arm cannot grow into something that grabs focus on its own.
        Assert.Single(Handler().Calls("FocusActiveLeaf"));
    }

    [Fact]
    public void FocusActiveLeaf_DeferencesTheFocusPastTheActivationChurn()
    {
        // The arm fires inside the Activated event, where WinUI is still
        // moving focus itself; the focus must land on a later dispatcher
        // turn, never synchronously and never off a sleep. The whole
        // enqueued body is pinned, folded across its line breaks: it
        // resolves the pane at fire time from the manager (so a restore
        // that replaced leaves still finds the active one) and focuses it
        // programmatically.
        var enqueue = MainWindow().Method("FocusActiveLeaf")
            .Call("DispatcherQueue.TryEnqueue");
        var body = Assert.IsType<ParenthesizedLambdaExpressionSyntax>(enqueue.ArgExpression(0))
            .Body;
        Assert.Equal(
            "_tabManager.ActiveTab?.PaneHost?.ActiveLeaf?.Terminal() .Focus(FocusState.Programmatic)",
            string.Join(" ", body.ToString().Split((char[])null,
                StringSplitOptions.RemoveEmptyEntries)));
    }
}
