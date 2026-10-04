using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The quick terminal must never be summoned into an empty window.
///
/// App builds the quake window on every launch and hides it, so every user
/// has one. Its last tab going away used to take the window with it:
/// MainWindow closed on TabManager.LastTabClosed, and the quake window
/// intercepts AppWindow.Closing into a hide (that interception is what
/// keeps the hotkey able to re-summon the same shell), so the close
/// became a hide of a window with zero tabs.
///
/// Nothing recovers from there. The next Show() seeded no tab,
/// FocusActiveLeaf chased an ActiveTab that still pointed at the removed
/// one, and UpdateQuakeStripVisibility hides the strip at one tab or
/// fewer -- zero included -- so the new-tab button went with it.
/// Keybindings only reach the app through a focused TerminalControl, so
/// the window that came back had no keyboard path out at all until the
/// app was restarted.
///
/// Asserted as a tree: the fix is a shape (one handler, one branch, a
/// hide and a seed outside it), and a substring match cannot tell a
/// guarded call from an unconditional one.
/// </summary>
public class QuickTerminalLastTabWiringTests
{
    [Fact]
    public void TheLastTabClosing_HidesAndSeeds_TheQuickTerminal()
    {
        var win = ShellSource.Load("MainWindow.xaml.cs");

        // The subscription is the shape of the defect itself: an inline
        // Close() here is the close the interception turns into a hide.
        // The quick terminal needs a handler that can tell the two kinds
        // of window apart.
        var hooks = win.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                        && a.Left.ToString() == "_tabManager.LastTabClosed")
            .ToList();
        Assert.True(
            hooks.Count == 1,
            $"MainWindow must subscribe to LastTabClosed exactly once, so the "
                + $"close has one owner; found {hooks.Count}.");
        var lambda = Assert.IsType<ParenthesizedLambdaExpressionSyntax>(hooks[0].Right);
        var body = Assert.IsType<InvocationExpressionSyntax>(lambda.Body);
        Assert.True(
            body.Expression.ToString() != "Close",
            "the LastTabClosed subscription must route to a handler of its own, "
                + "not to an inline Close(): on the quick terminal that close is "
                + "intercepted into a hide, and the window then has no tabs, no "
                + "strip and no way back in.");
        var target = Assert.IsAssignableFrom<SimpleNameSyntax>(body.Expression);
        var handler = win.Method(target.Identifier.ValueText);

        // One branch decides the window kind, and it is the only conditional
        // that can stand between the quick terminal and its hide: any
        // other one in this handler is a path that skips both.
        var branches = handler.Body!.DescendantNodes().OfType<IfStatementSyntax>()
            .ToList();
        var kind = branches
            .Where(b => b.Condition.ToString().Contains("IsQuickTerminal"))
            .ToList();
        Assert.True(
            kind.Count == 1,
            "the LastTabClosed handler must decide the window kind exactly once, "
                + $"on IsQuickTerminal; found {kind.Count} such branches in "
                + $"{target.Identifier.ValueText}.");
        Assert.All(
            branches.Where(b => !kind.Contains(b)),
            b => Assert.True(
                b.Condition.ToString() == "_isClosed",
                "the only other conditional allowed in the LastTabClosed handler "
                    + "is the teardown guard: any further one is a path that can "
                    + "skip the hide or the seed, which is the defect itself."));
        var branch = kind[0];
        Assert.True(
            branch.Condition.ToString() == "!IsQuickTerminal",
            "the guard must name the quick terminal, and it must be the NEGATED "
                + "test: the close belongs to the regular window, whose Close() is "
                + $"not intercepted; found '{branch.Condition}'.");

        // The regular window still closes with its last tab, and only there.
        var close = Assert.Single(handler.Calls("Close"), _ => true);
        Assert.True(
            branch.Statement.Span.Contains(close.Span),
            "Close() belongs to the regular-window arm of the branch.");

        // The quick terminal hides and gets a tab to be summoned into, and
        // neither call sits under the branch: the branch returns, so
        // anything inside it would run for the wrong window kind.
        var hide = Assert.Single(handler.Calls("Hide"), _ => true);
        var seed = Assert.Single(handler.Calls("SeedQuickTerminalTab"), _ => true);
        Assert.False(
            branch.Span.Contains(hide.Span),
            "Hide() must run on the quick-terminal path, outside the branch that "
                + "closes a regular window.");
        Assert.False(
            branch.Span.Contains(seed.Span),
            "the quick terminal's replacement tab must be seeded outside the "
                + "branch that closes a regular window.");

        // Hide first: a window left visible while it is empty is the state
        // this whole guard exists to prevent.
        Assert.True(
            hide.SpanStart < seed.SpanStart,
            "the window must hide before the fresh tab is seeded; the other order "
                + "shows an empty quick terminal for a frame.");
    }

    [Fact]
    public void Show_SeedsAFreshTab_WhateverStateTheWindowWasSummonedFrom()
    {
        var win = ShellSource.Load("MainWindow.xaml.cs");
        var show = win.Method("Show");

        var seeds = show.Calls("SeedQuickTerminalTab");
        Assert.True(
            seeds.Count == 1,
            "Show() must ask for the empty-tab recovery; found "
                + $"{seeds.Count} calls.");
        var seed = seeds[0];

        // A top-level statement of Show, not one nested under the
        // `if (!AppWindow.IsVisible)` reveal: the hotkey re-summons a
        // window the tab-close path already hid, and a summon that only
        // seeded on the cold reveal would find nothing to seed.
        var statement = Assert.IsType<ExpressionStatementSyntax>(seed.Parent);
        Assert.Same(show.Body, statement.Parent);

        // And before the window appears, so no frame ever shows the empty
        // shell the seed exists to prevent.
        Assert.True(
            seed.SpanStart < show.Calls("AppWindow.Show").Single().SpanStart,
            "the tab must be seeded before AppWindow.Show, not after it.");

        // The helper is the one the close path calls, and it no-ops as soon
        // as a tab exists -- which is what makes calling it on every show
        // safe rather than a second tab per summon.
        var helper = win.Method("SeedQuickTerminalTab");
        Assert.True(
            helper.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .Any(m => m.ToString() == "_tabManager.Tabs.Count"),
            "the helper must ask the manager whether a tab is left; anything else "
                + "cannot tell an empty window from a full one.");

        // It seeds through the same funnel Ctrl+T uses, so a summoned quick
        // terminal runs the profile a new tab would.
        var open = Assert.Single(helper.Calls("OpenDefaultProfile"), _ => true);
        Assert.Equal("ProfileLaunchTarget.NewTab", open.Arg(0));

        // And never after the window is gone: a close during teardown would
        // otherwise build a pane host for a window on its way out.
        Assert.True(
            helper.DescendantNodes().OfType<IfStatementSyntax>()
                .Any(i => i.Condition.ToString() == "_isClosed"),
            "SeedQuickTerminalTab must refuse once the window is closed.");
    }
}