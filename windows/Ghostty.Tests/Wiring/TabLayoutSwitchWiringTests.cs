using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The horizontal/vertical tab switch lands on a completion callback about
/// 340ms after it starts, and nothing cancels it when the window goes away in
/// between. Closing the last tab closes the window, and a storyboard that has
/// already begun still raises Completed, so the callback can run against a
/// window whose HWND is gone.
///
/// <c>Window.AppWindow</c> is null at that point. The chrome work the callback
/// does reaches <c>AppWindow.TitleBar</c>, so without a guard it throws
/// NullReferenceException on the UI thread, which is unhandled and takes the
/// process down. Four such crashes were recorded in crash.log with this exact
/// stack before the guard was added:
///
///   ApplyButtonColors -> ApplyCaptionButtonChrome -> RefreshTabHostChrome
///   -> AnimateTabLayoutTo's completion -> LayoutCoordinator.FinishSwitch
///
/// These are wiring guards. They prove the null checks are still on the path a
/// dead window takes; whether the window actually survives a switch is only
/// observable on a live UI.
/// </summary>
public class TabLayoutSwitchWiringTests
{
    private static ShellSource Window() => ShellSource.Load("Ghostty.MainWindow.xaml.cs");

    [Fact]
    public void SwitchCompletion_BailsOutWhenTheWindowIsGone()
    {
        var animate = Window().Method("AnimateTabLayoutTo");

        // The completion lambda handed to LayoutCoordinator.Animate.
        var completion = animate.DescendantNodes()
            .OfType<ParenthesizedLambdaExpressionSyntax>()
            .Single();

        var guard = completion.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .FirstOrDefault(s => s.Condition.ToString().Contains("AppWindow"));

        Assert.True(
            guard is not null,
            "the layout-switch completion must check AppWindow before touching window chrome");

        // A guard that does not return leaves the crash in place.
        Assert.Contains("return", guard!.Statement.ToString());

        // And it has to come first: the calls below it are what crash.
        var firstStatement = completion.Block!.Statements.First();
        Assert.Same(guard, firstStatement);
    }

    [Fact]
    public void ApplyButtonColors_ChecksAppWindowBeforeDereferencingIt()
    {
        var method = Window().Method("ApplyButtonColors");

        var guard = method.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .FirstOrDefault(s => s.Condition.ToString().Contains("AppWindow"));

        Assert.True(
            guard is not null,
            "ApplyButtonColors dereferences AppWindow.TitleBar and must check it first");
        Assert.Contains("return", guard!.Statement.ToString());
    }

    [Fact]
    public void ApplyButtonColors_DoesNotRecordColoursItNeverApplied()
    {
        var method = Window().Method("ApplyButtonColors");
        var statements = method.Body!.Statements;

        var guardIndex = statements
            .TakeWhile(s => s is not IfStatementSyntax i || !i.Condition.ToString().Contains("AppWindow"))
            .Count();

        // _lastButtonColors is the no-op cache: writing it on a path that
        // applies nothing makes the next real call skip those writes, so the
        // window would keep stale caption colours after the guard stops firing.
        var cacheIndex = statements
            .TakeWhile(s => !s.ToString().Contains("_lastButtonColors ="))
            .Count();

        Assert.True(
            guardIndex < cacheIndex,
            "the AppWindow guard must run before _lastButtonColors is updated");
    }
}
