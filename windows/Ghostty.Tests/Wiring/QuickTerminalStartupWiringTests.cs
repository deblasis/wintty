using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The quick terminal is optional; the app is not.
///
/// OnLaunched builds a second, hidden MainWindow on every launch, and every
/// step of that build reaches Microsoft.UI.Windowing -- a surface that is
/// allowed to refuse. It was observed refusing: AppWindow.IsShownInSwitchers
/// threw NotImplementedException (E_NOTIMPL) on an ordinary machine, the
/// throw escaped OnLaunched, and the process died mid-launch with a real
/// window already on screen. Every Wintty build on that box stopped starting.
///
/// Two containments, and this pins both. The outer one keeps a refusal from
/// costing the process; the inner one keeps a refusal of the one redundant
/// call from costing the whole quick terminal.
///
/// Asserted as a tree, not as text: the point is that the calls sit INSIDE a
/// try whose catch logs, and a substring check cannot tell that from a call
/// sitting next to one.
/// </summary>
public class QuickTerminalStartupWiringTests
{
    [Fact]
    public void TheQuakeWindowBuild_CannotTakeTheProcessDown()
    {
        var app = ShellSource.Load("App.xaml.cs");
        var launched = app.Method("OnLaunched");

        var built = Assert.Single(
            launched.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.Left.ToString() == "_quakeWindow"
                            && a.Right is ObjectCreationExpressionSyntax)
                .ToList(),
            _ => true);

        var guard = built.Ancestors().OfType<TryStatementSyntax>().FirstOrDefault();
        Assert.True(
            guard is not null,
            "The quake window construction must sit inside a try. Unguarded, a "
                + "windowing refusal escapes OnLaunched and kills startup.");

        // Activation and the hide are part of the same exposure: both reach
        // the same windowing surface, so both must be inside the SAME try.
        // Pinned separately because moving either below the closing brace
        // would leave the assignment guarded and the app still killable.
        Assert.Single(guard!.Block.Calls("_quakeWindow.Activate"));
        Assert.Single(guard.Block.Calls("_quakeWindow.AppWindow.Hide"));

        var handler = Assert.Single(guard.Catches);
        Assert.Single(
            handler.Calls("Ghostty.Logging.StaticLoggers.App.LogQuakeWindowFailed"));

        // Null, not left half-built: every consumer of _quakeWindow is
        // null-safe, and a partially constructed one is not.
        Assert.Contains(
            handler.AssignsTo("_quakeWindow"),
            a => a.Right.IsKind(SyntaxKind.NullLiteralExpression));
    }

    [Fact]
    public void TheSwitcherCall_CannotCostTheQuickTerminal()
    {
        var win = ShellSource.Load("MainWindow.xaml.cs");
        var apply = win.Method("ApplyQuickTerminalBehaviour");

        var assigned = Assert.Single(
            apply.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.Left.ToString() == "AppWindow.IsShownInSwitchers")
                .ToList(),
            _ => true);

        var guard = assigned.Ancestors().OfType<TryStatementSyntax>().FirstOrDefault();
        Assert.True(
            guard is not null,
            "AppWindow.IsShownInSwitchers must sit inside a try. Its effect is "
                + "redundant with WS_EX_TOOLWINDOW, so a refusal must cost nothing.");

        var handler = Assert.Single(guard!.Catches);
        Assert.Single(handler.Calls("_logger.LogSwitcherRefused"));

        // The redundancy the swallow depends on: WS_EX_TOOLWINDOW is what
        // actually hides the window, and it is applied outside the try, so a
        // refusal above cannot skip it.
        Assert.Contains("WS_EX_TOOLWINDOW", apply.ToString());
        Assert.DoesNotContain(
            guard.Block.DescendantNodes().OfType<IdentifierNameSyntax>(),
            id => id.Identifier.ValueText == "WS_EX_TOOLWINDOW");
    }
}
