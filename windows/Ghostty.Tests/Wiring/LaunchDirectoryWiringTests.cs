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

        // The rule's answer is what both arms hand the first pane. The one
        // thing that may sit in front of it is a directory the launch asked
        // for on its own command line (#1433): not the inherited cwd, and it
        // wins over the rule outright.
        var directory = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "directory");
        var coalesce = Assert.IsType<BinaryExpressionSyntax>(directory.Initializer!.Value);
        Assert.Equal("??", coalesce.OperatorToken.ValueText);
        Assert.Equal("requestedDirectory", coalesce.Left.ToString());
        Assert.Same(rule, coalesce.Right);
        Assert.Empty(method.AssignsTo("directory"));
        Assert.Equal("directory", method.Call("LaunchFirstPaneSnapshot").Arg(1));
        Assert.Equal("directory", method.Call("Ghostty.Core.Profiles.PaneCommandPolicy.ApplyLaunchCommand").Arg(2));

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
