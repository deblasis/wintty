using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The horizontal/vertical tab switch lands on a completion callback about
/// 340ms after it starts, and nothing cancels it when the window goes away in
/// between. Closing the last tab closes the window, and a storyboard that has
/// already begun still raises Completed, so the callback can run against a
/// window that is tearing down. crash.log recorded four NullReferenceExceptions
/// with this stack before the gate was added:
///
///   ApplyButtonColors -> ApplyCaptionButtonChrome -> RefreshTabHostChrome
///   -> AnimateTabLayoutTo's completion -> LayoutCoordinator.FinishSwitch
///
/// The gate is _isClosed, not a null AppWindow. AppWindow survives into
/// OnClosedAsync, so it goes null strictly later than teardown starts, and in
/// that gap the theme manager is disposed and panes are being freed. These
/// tests pin the earlier signal deliberately, because a later one leaves the
/// gap open.
///
/// These are wiring guards. They prove the gate is on the path a closing
/// window takes; whether the window survives a switch is only observable live.
/// </summary>
public class TabLayoutSwitchWiringTests
{
    private static ShellSource Window() => ShellSource.Load("Ghostty.MainWindow.xaml.cs");

    /// <summary>
    /// The lambda passed as <c>onCompleted:</c>, found by argument name rather
    /// than by being the only lambda present, so adding another callback to
    /// the method does not turn this into an unreadable sequence error.
    /// </summary>
    private static ParenthesizedLambdaExpressionSyntax SwitchCompletion()
    {
        var animate = Window().Method("AnimateTabLayoutTo");
        var arg = animate.DescendantNodes()
            .OfType<ArgumentSyntax>()
            .Single(a => a.NameColon?.Name.Identifier.Text == "onCompleted");
        return Assert.IsType<ParenthesizedLambdaExpressionSyntax>(arg.Expression);
    }

    private static bool ReturnsEarly(IfStatementSyntax guard) =>
        guard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any();

    [Fact]
    public void SwitchCompletion_BailsOutOnceTeardownHasStarted()
    {
        var completion = SwitchCompletion();
        var statements = completion.Block!.Statements;

        // Identity, not a substring: `if (_isClosed) return;` and nothing that
        // merely mentions the field. An inverted or dead condition is a
        // different syntax shape and does not match.
        var guard = statements
            .OfType<IfStatementSyntax>()
            .FirstOrDefault(s => s.Condition is IdentifierNameSyntax { Identifier.Text: "_isClosed" });

        Assert.True(
            guard is not null,
            "the layout-switch completion must return early once _isClosed is set; "
            + "a null AppWindow is a strictly later signal and leaves the teardown gap open");
        Assert.True(ReturnsEarly(guard!), "the teardown gate must return, not just log");

        // Order matters rather than position: everything below the gate is
        // what touches disposed state.
        var guardIndex = statements.IndexOf(guard!);
        var refreshIndex = statements
            .TakeWhile(s => !s.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Any(i => i.Expression.ToString() == "RefreshTabHostChrome"))
            .Count();

        Assert.True(refreshIndex < statements.Count, "expected a RefreshTabHostChrome call to guard");
        Assert.True(guardIndex < refreshIndex, "the teardown gate must precede the chrome work");
    }

    [Fact]
    public void ApplyButtonColors_ChecksBothHalvesBeforeDereferencing()
    {
        var method = Window().Method("ApplyButtonColors");

        // The crash line reads AppWindow.TitleBar, which faults whether
        // AppWindow or TitleBar is null, so the check has to cover both. A
        // plain `AppWindow is null` test leaves the second half live.
        var guard = method.Body!.Statements
            .OfType<IfStatementSyntax>()
            .FirstOrDefault(s =>
                s.Condition is IsPatternExpressionSyntax pattern &&
                pattern.Expression.ToString().Contains("AppWindow?.TitleBar"));

        Assert.True(
            guard is not null,
            "ApplyButtonColors must check AppWindow?.TitleBar, covering a null window and a null title bar");
        Assert.True(ReturnsEarly(guard!), "the guard must return");
    }

    [Fact]
    public void ApplyButtonColors_GuardsBeforeMutatingState()
    {
        var method = Window().Method("ApplyButtonColors");
        var statements = method.Body!.Statements;

        var guardIndex = statements
            .TakeWhile(s => s is not IfStatementSyntax i ||
                            !i.Condition.ToString().Contains("AppWindow"))
            .Count();

        var cacheIndex = statements
            .TakeWhile(s => !s.DescendantNodesAndSelf()
                .OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left.ToString() == "_lastButtonColors"))
            .Count();

        // Both have to exist, or TakeWhile silently returns the full count and
        // the comparison passes while pinning nothing.
        Assert.True(guardIndex < statements.Count, "expected an AppWindow guard");
        Assert.True(cacheIndex < statements.Count, "expected a _lastButtonColors assignment");
        Assert.True(guardIndex < cacheIndex, "the guard must run before any state is mutated");
    }
}
