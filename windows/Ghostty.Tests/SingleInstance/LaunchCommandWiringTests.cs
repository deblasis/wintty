using Xunit;

namespace Ghostty.Tests.SingleInstance;

/// <summary>
/// The shell-side wiring of the forwarded -e command (#1094 review M2):
/// the WinUI assembly cannot be loaded into a test host, so what these pin
/// is shape -- that <c>OpenWindowFromLaunch</c> consults the launch model
/// on the bare-launch arm and hands the command to the window builder,
/// which applies it to the first pane's snapshot (the same
/// <c>ResolvedCommand</c> the surface config reads). The model itself is
/// pinned by <c>LaunchCommandTests</c>.
/// </summary>
public sealed class LaunchCommandWiringTests
{
    [Fact]
    public void OpenWindowFromLaunch_ParsesTheCommand_OnTheBareLaunchArm()
    {
        var source = Wiring.ShellSource.Load("App.xaml.cs");
        var open = source.Method("OpenWindowFromLaunch").Body!.ToString();

        // The bare-launch arm (no jump-list action) is where a forwarded
        // -e lands, and only there: markers keep their existing priority.
        Assert.Contains("JumpListAction.None", open);
        Assert.Contains("LaunchCommand.FromArgs", open);

        // The parsed command travels into the window builder by name.
        Assert.Contains("OpenJumpListWindow", open);
        Assert.Contains("command:", open);
    }

    [Fact]
    public void OpenJumpListWindow_AppliesTheCommandToTheFirstPaneSnapshot()
    {
        var source = Wiring.ShellSource.Load("App.xaml.cs");
        var builder = source.Method("OpenJumpListWindow").Body!.ToString();

        // The command wins over the profile's resolved command, exactly the
        // precedence a cold start gives -e, and is applied on the snapshot
        // the first pane reads (ResolvedCommand feeds surfaceConfig.Command).
        Assert.Contains("command = null", source.Method("OpenJumpListWindow")
            .ParameterList.Parameters.ToString());
        Assert.Contains("ResolvedCommand = command", builder);
    }
}
