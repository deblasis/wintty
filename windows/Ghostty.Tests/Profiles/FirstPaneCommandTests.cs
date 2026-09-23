using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

/// <summary>
/// The first-pane command rules (#1136): -e, then the configured
/// <c>command</c>, then the default profile's own command, applied on the
/// snapshot the pane spawns from. The shell wiring that feeds them is pinned
/// by <c>FirstPaneCommandWiringTests</c>.
/// </summary>
public sealed class FirstPaneCommandTests
{
    private static ProfileSnapshot DefaultProfile() => new(
        ProfileId: "pwsh",
        Version: 7,
        ResolvedCommand: "pwsh.exe -NoLogo",
        WorkingDirectory: @"C:\Users\me",
        DisplayName: "PowerShell",
        Icon: new IconSpec.BundledKey("pwsh"),
        Visuals: EffectiveVisualOverrides.Empty);

    [Fact]
    public void Pick_LaunchCommandWinsOverTheConfiguredCommand()
        => Assert.Equal("htop", FirstPaneCommand.Pick("htop", "cmd.exe"));

    [Fact]
    public void Pick_ConfiguredCommandWhenThereIsNoLaunchCommand()
        => Assert.Equal("cmd.exe /k ver", FirstPaneCommand.Pick(null, "  cmd.exe /k ver "));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "\t")]
    public void Pick_NeitherSet_LeavesTheProfileCommand(string? launch, string? configured)
        => Assert.Null(FirstPaneCommand.Pick(launch, configured));

    [Fact]
    public void Apply_ReplacesOnlyTheCommand_AndKeepsTheProfile()
    {
        var applied = FirstPaneCommand.Apply(DefaultProfile(), "pwsh -File C:\\x.ps1");

        Assert.NotNull(applied);
        Assert.Equal("pwsh -File C:\\x.ps1", applied!.ResolvedCommand);
        Assert.Equal(DefaultProfile() with { ResolvedCommand = "pwsh -File C:\\x.ps1" }, applied);
    }

    [Fact]
    public void Apply_NoCommand_ReturnsTheSnapshotItself()
    {
        var snapshot = DefaultProfile();
        Assert.Same(snapshot, FirstPaneCommand.Apply(snapshot, null));
        Assert.Null(FirstPaneCommand.Apply(null, " "));
    }

    [Fact]
    public void Apply_NoProfile_StillCarriesTheCommand()
    {
        // A null snapshot hands the choice to libghostty, which runs -e only
        // on the process's first surface. A forwarded launch is never that,
        // so the command has to travel on a snapshot of its own.
        var applied = FirstPaneCommand.Apply(null, "\"C:\\Tools\\my tool.exe\" --flag", @"D:\work");

        Assert.NotNull(applied);
        Assert.Equal("\"C:\\Tools\\my tool.exe\" --flag", applied!.ResolvedCommand);
        Assert.Equal(@"D:\work", applied.WorkingDirectory);
        Assert.Equal("my tool", applied.DisplayName);
    }

    [Fact]
    public void Apply_NoProfile_EmptyWorkingDirectoryIsUnset()
        => Assert.Null(FirstPaneCommand.Apply(null, "cmd.exe", "")!.WorkingDirectory);
}
