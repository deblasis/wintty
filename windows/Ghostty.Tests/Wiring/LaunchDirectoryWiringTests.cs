using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// A launch hands its first pane the caller's directory only with a
/// <c>-e</c> command (<c>PaneCommandPolicy.LaunchDirectory</c>). The rule is
/// tested on its own; this pins that both places a launch opens a window go
/// through it: the cold start, and a launch forwarded to the running
/// instance or a jump-list task. A forwarded shortcut launch used to apply
/// the install folder to the default profile's pane.
/// </summary>
public class LaunchDirectoryWiringTests
{
    private const string Rule = "Ghostty.Core.Profiles.PaneCommandPolicy.LaunchDirectory";

    private static ShellSource App() => ShellSource.Load("App.xaml.cs");

    [Fact]
    public void AForwardedOrJumpListWindow_TakesTheCallersDirectoryThroughTheRule()
    {
        var method = App().Method("OpenJumpListWindow");
        var rule = method.Call(Rule);
        Assert.Equal("command", rule.Arg(0));
        Assert.Equal("workingDirectory", rule.Arg(1));

        // Any other read of the raw parameter would bypass the rule.
        var raw = method.Body!.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Count(id => id.Identifier.ValueText == "workingDirectory");
        Assert.Equal(1, raw);
    }

    [Fact]
    public void TheColdStart_TakesTheCallersDirectoryThroughTheRule()
    {
        var cold = App().Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText() == "LaunchFirstPaneSnapshot"
                && c.ArgumentList.Arguments.Any(a => a.NameColon?.Name.Identifier.ValueText == "initialCommand"));
        var directory = cold.ArgumentList.Arguments
            .Single(a => a.NameColon?.Name.Identifier.ValueText == "workingDirectory").Expression;
        Assert.True(
            directory is InvocationExpressionSyntax rule
                && rule.CalleeText() == Rule
                && rule.Arg(0) == "coldCommand"
                && rule.Arg(1) == "Program.LaunchWorkingDirectory",
            $"the cold start's first pane must take {Rule}(coldCommand, Program.LaunchWorkingDirectory), found {directory}");
    }
}