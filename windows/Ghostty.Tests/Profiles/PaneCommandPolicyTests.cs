using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

/// <summary>
/// What a new pane runs (#1136), one test per rule of the precedence
/// Config.zig documents for <c>command</c>. The shell wiring that feeds the
/// rules is pinned by <c>FirstPaneCommandWiringTests</c>.
/// </summary>
public sealed class PaneCommandPolicyTests
{
    private static ProfileSnapshot DefaultProfile() => new(
        ProfileId: "pwsh",
        Version: 7,
        ResolvedCommand: "pwsh.exe -NoLogo",
        WorkingDirectory: @"C:\Users\me",
        DisplayName: "PowerShell",
        Icon: new IconSpec.BundledKey("pwsh"),
        Visuals: EffectiveVisualOverrides.Empty);

    private static readonly ConfiguredCommand Nu = new("nu.exe --login", IsArgv: false);

    // ---- default-profile set: it beats `command` everywhere ---------------

    [Fact]
    public void DefaultProfileSet_BeatsTheConfiguredCommand()
    {
        var snapshot = DefaultProfile();
        Assert.Same(snapshot, PaneCommandPolicy.ImplicitDefault(snapshot, Nu, defaultProfileSet: true));
    }

    [Fact]
    public void DefaultProfileSet_BeatsTheConfiguredCommand_OnALaunchsFirstPane()
    {
        var snapshot = DefaultProfile();
        Assert.Same(snapshot, PaneCommandPolicy.LaunchFirstPane(
            snapshot, launchArgv: null, Nu, defaultProfileSet: true, workingDirectory: null));
    }

    // ---- default-profile unset: `command` runs in every new pane ---------

    [Fact]
    public void CommandSet_DefaultProfileUnset_RunsTheCommand()
    {
        var pane = PaneCommandPolicy.ImplicitDefault(DefaultProfile(), Nu, defaultProfileSet: false);

        Assert.NotNull(pane);
        Assert.Equal("nu.exe --login", pane!.ResolvedCommand);
        Assert.Equal(PaneCommandOrigin.ConfiguredCommand, pane.CommandOrigin);
        Assert.False(pane.CommandIsArgv);
        // It replaced the default, so it does not wear the default's name.
        Assert.Equal("nu", pane.DisplayName);
        Assert.Equal("", pane.ProfileId);
    }

    [Fact]
    public void CommandSet_DefaultProfileUnset_RunsTheCommand_WithNoProfilesAtAll()
    {
        var pane = PaneCommandPolicy.ImplicitDefault(null, Nu, defaultProfileSet: false);
        Assert.Equal("nu.exe --login", pane!.ResolvedCommand);
    }

    [Fact]
    public void CommandSet_DefaultProfileUnset_RunsOnALaunchsFirstPaneToo()
    {
        var pane = PaneCommandPolicy.LaunchFirstPane(
            DefaultProfile(), launchArgv: null, Nu, defaultProfileSet: false, workingDirectory: null);
        Assert.Equal("nu.exe --login", pane!.ResolvedCommand);
    }

    [Fact]
    public void CommandPane_SplitsIntoWhatANewPaneRunsNow()
    {
        // Asked afresh, so a reload that changed `command` (or set a
        // default-profile) reaches the split the same way it reaches Ctrl+T.
        var pane = PaneCommandPolicy.ImplicitDefault(null, Nu, defaultProfileSet: false);
        var now = PaneCommandPolicy.ImplicitDefault(
            null, new ConfiguredCommand("fish", false), defaultProfileSet: false);
        Assert.Same(now, PaneCommandPolicy.Inherit(pane, () => now));
    }

    [Theory]
    [InlineData("nu.exe --login", false, "nu.exe --login")]
    [InlineData("nu.exe --login", true, null)]
    [InlineData("  ", false, null)]
    [InlineData(null, false, null)]
    public void CommandInEffect_OnlyWithoutADefaultProfile(string? text, bool defaultProfileSet, string? want)
    {
        ConfiguredCommand? configured = text is null ? null : new ConfiguredCommand(text, false);
        Assert.Equal(want, PaneCommandPolicy.CommandInEffect(configured, defaultProfileSet));
    }

    [Fact]
    public void DirectCommand_StaysAnArgv()
    {
        var pane = PaneCommandPolicy.ImplicitDefault(
            null, new ConfiguredCommand("tool.exe a&b", IsArgv: true), defaultProfileSet: false);

        Assert.True(pane!.CommandIsArgv);
        Assert.Equal("direct:tool.exe a&b", PaneCommandPolicy.SurfaceCommand(pane));
    }

    // ---- neither set: the registry's default profile ----------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoCommand_LeavesTheDefaultProfile(string? text)
    {
        var snapshot = DefaultProfile();
        ConfiguredCommand? configured = text is null ? null : new ConfiguredCommand(text, false);
        Assert.Same(snapshot, PaneCommandPolicy.ImplicitDefault(snapshot, configured, defaultProfileSet: false));
    }

    // ---- -e: that launch's first pane only --------------------------------

    [Fact]
    public void LaunchCommand_WinsOnTheFirstPane_OverCommandAndDefaultProfile()
    {
        foreach (var set in new[] { false, true })
        {
            var pane = PaneCommandPolicy.LaunchFirstPane(
                DefaultProfile(), "pwsh -File \"C:\\a b\\x.ps1\"", Nu, set, workingDirectory: @"D:\proj");

            Assert.Equal("pwsh -File \"C:\\a b\\x.ps1\"", pane!.ResolvedCommand);
            Assert.True(pane.CommandIsArgv);
            Assert.Equal(PaneCommandOrigin.LaunchCommand, pane.CommandOrigin);
            // wt -- cmd keeps the default profile's identity.
            Assert.Equal("PowerShell", pane.DisplayName);
            // It runs in the caller's directory.
            Assert.Equal(@"D:\proj", pane.WorkingDirectory);
        }
    }

    [Fact]
    public void LaunchCommand_IsNotInheritedBySplits()
    {
        var first = PaneCommandPolicy.LaunchFirstPane(
            DefaultProfile(), "htop", Nu, defaultProfileSet: false, workingDirectory: null);
        var fallback = PaneCommandPolicy.ImplicitDefault(DefaultProfile(), Nu, defaultProfileSet: false);

        Assert.Same(fallback, PaneCommandPolicy.Inherit(first, () => fallback));
    }

    [Fact]
    public void LaunchCommand_WithNoProfile_StillCarriesTheCommand()
    {
        var pane = PaneCommandPolicy.LaunchFirstPane(
            null, "\"C:\\Tools\\my tool.exe\" --flag", configured: null,
            defaultProfileSet: false, workingDirectory: @"D:\work");

        Assert.Equal("\"C:\\Tools\\my tool.exe\" --flag", pane!.ResolvedCommand);
        Assert.True(pane.CommandIsArgv);
        Assert.Equal(@"D:\work", pane.WorkingDirectory);
        Assert.Equal("my tool", pane.DisplayName);
    }

    [Fact]
    public void LaunchCommand_IsAlwaysAnArgvAtTheSurface()
    {
        // Harmless payloads that would be syntax to cmd.exe: the surface is
        // told to split the argv and run it directly.
        var pane = PaneCommandPolicy.ApplyLaunchCommand(
            DefaultProfile(), "findstr r.txt&echo.INJECTED %USERNAME%", workingDirectory: null);
        Assert.Equal("direct:findstr r.txt&echo.INJECTED %USERNAME%", PaneCommandPolicy.SurfaceCommand(pane!));
    }

    // ---- a picked profile runs that profile -------------------------------

    [Fact]
    public void PickedProfile_SplitsIntoThatProfile()
    {
        var picked = DefaultProfile() with { ProfileId = "ubuntu", ResolvedCommand = "wsl.exe -d Ubuntu" };
        Assert.Same(picked, PaneCommandPolicy.Inherit(picked, () => DefaultProfile()));
    }

    [Fact]
    public void ProfileCommand_GoesToTheSurfaceAsWritten()
        => Assert.Equal("pwsh.exe -NoLogo", PaneCommandPolicy.SurfaceCommand(DefaultProfile()));

    [Fact]
    public void ApplyLaunchCommand_NoCommand_KeepsTheSnapshot()
    {
        var snapshot = DefaultProfile();
        Assert.Same(snapshot, PaneCommandPolicy.ApplyLaunchCommand(snapshot, null, null));
        Assert.Null(PaneCommandPolicy.ApplyLaunchCommand(null, " ", null));
    }
}
