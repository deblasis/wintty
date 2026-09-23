using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.SingleInstance;

/// <summary>
/// The shell side of #1136: a launch's first pane runs -e, else the
/// configured <c>command</c>, else the default profile's command, on a cold
/// start and on a forwarded launch alike. The WinUI assembly cannot load in
/// a test host, so what these pin is shape; the rules themselves are pinned
/// by <c>FirstPaneCommandTests</c>.
/// </summary>
public sealed class FirstPaneCommandWiringTests
{
    private static ShellSource App => ShellSource.Load("App.xaml.cs");

    [Fact]
    public void ColdStart_ReadsTheLaunchCommandFromItsOwnArgv()
    {
        var launched = App.Method("OnLaunched");
        var parse = launched.Call("Ghostty.Core.SingleInstance.LaunchCommand.FromArgs");
        Assert.Equal("Environment.GetCommandLineArgs()", parse.Arg(0));
    }

    [Fact]
    public void ColdStart_DashE_SkipsSessionRestore()
    {
        // `wt -- cmd` opens the command; it does not bring back the saved
        // layout. The restore read must sit behind the command check.
        var launched = App.Method("OnLaunched");
        var restore = launched.Call("_sessionManager.LoadForRestore");
        var conditional = restore.Ancestors().OfType<ConditionalExpressionSyntax>().First();
        Assert.Contains("coldCommand is not null", conditional.Condition.ToString());
        Assert.Contains("honorJumpList", conditional.Condition.ToString());
    }

    [Fact]
    public void ColdStart_FreshWindow_OpensOnTheFirstPaneSnapshot()
    {
        var launched = App.Method("OnLaunched");
        var fresh = launched.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(o => o.Type.ToString() == "MainWindow"
                && o.ArgumentList!.Arguments.Any(a => a.NameColon?.Name.Identifier.ValueText == "showLaunchIcon"
                    && a.Expression.ToString() == "true"))
            .ToList();
        Assert.Single(fresh);

        var seed = fresh[0].ArgumentList!.Arguments
            .Single(a => a.NameColon?.Name.Identifier.ValueText == "initialSnapshot");
        var call = seed.Expression.AssertCallTo("FirstPaneSnapshot");
        Assert.Contains("ResolveDefault", call.Arg(0));
        Assert.Equal("coldCommand", call.Arg(1));
    }

    [Fact]
    public void FirstPaneSnapshot_TakesTheConfiguredCommand_BehindTheLaunchCommand()
    {
        var helper = App.Method("FirstPaneSnapshot");
        var pick = helper.Call("Ghostty.Core.Profiles.FirstPaneCommand.Pick");
        Assert.Equal("launchCommand", pick.Arg(0));
        Assert.Equal("_configService?.ConfiguredCommand", pick.Arg(1));
        helper.Call("Ghostty.Core.Profiles.FirstPaneCommand.Apply");
    }

    [Fact]
    public void ForwardedBareLaunch_GoesThroughTheSameHelper()
    {
        var open = App.Method("OpenWindowFromLaunch");
        var call = open.Call("OpenJumpListWindow");
        Assert.Contains(call.ArgumentList.Arguments,
            a => a.NameColon?.Name.Identifier.ValueText == "implicitDefault"
                && a.Expression.ToString() == "true");

        var builder = App.Method("OpenJumpListWindow");
        var helper = builder.Call("FirstPaneSnapshot");
        var branch = helper.Ancestors().OfType<IfStatementSyntax>().First();
        Assert.Equal("implicitDefault && profileId is null", branch.Condition.ToString());
    }

    [Fact]
    public void ConfigService_ReadsTheCommandFromLibghostty_AndFreesIt()
    {
        var config = ShellSource.Load("Services.ConfigService.cs");
        var flags = config.Method("ReadFlagsCore");
        Assert.Contains(flags.AssignsTo("ConfiguredCommand"),
            a => a.Right.ToString() == "ReadConfiguredCommand()");

        var read = config.Method("ReadConfiguredCommand");
        read.Call("NativeMethods.ConfigCommand");
        var free = read.Call("NativeMethods.StringFree");
        Assert.NotEmpty(free.Ancestors().OfType<FinallyClauseSyntax>());
    }
}
