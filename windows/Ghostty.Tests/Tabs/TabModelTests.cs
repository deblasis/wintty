using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class TabModelTests
{
    [Fact]
    public void OnActiveProcessChanged_UnknownExe_RevertsToProfile()
    {
        var tab = MakeTab(profileIcon: new IconSpec.BrandKey("pwsh", null), profileTooltip: "PowerShell");
        tab.OnActiveProcessChanged("never-shipped.exe", commandLine: null);

        Assert.Equal("pwsh", ((IconSpec.BrandKey)tab.TabIcon.Icon).Key);
    }

    [Fact]
    public void OnActiveProcessChanged_KnownExe_AppliesOverride()
    {
        var tab = MakeTab(profileIcon: new IconSpec.BrandKey("pwsh", null), profileTooltip: "PowerShell");
        tab.OnActiveProcessChanged("vim.exe", commandLine: null);

        var brand = Assert.IsType<IconSpec.BrandKey>(tab.TabIcon.Icon);
        Assert.Equal("vim", brand.Key);
        Assert.Equal("vim", tab.TabIcon.TooltipText);
    }

    [Fact]
    public void OnActiveProcessChanged_NullExe_RevertsToProfile()
    {
        var tab = MakeTab(profileIcon: new IconSpec.BrandKey("pwsh", null), profileTooltip: "PowerShell");
        tab.OnActiveProcessChanged("vim.exe", commandLine: null);
        tab.OnActiveProcessChanged(null, commandLine: null);

        Assert.Equal("pwsh", ((IconSpec.BrandKey)tab.TabIcon.Icon).Key);
        Assert.Equal("PowerShell", tab.TabIcon.TooltipText);
    }

    [Fact]
    public void OnActiveProcessChanged_ToProfileExe_SuppressesOverride()
    {
        // When the active process IS the profile's launch shell, no override.
        var tab = MakeTab(profileIcon: new IconSpec.BrandKey("pwsh", null), profileTooltip: "PowerShell");
        tab.OnActiveProcessChanged("pwsh.exe", commandLine: null);

        var brand = Assert.IsType<IconSpec.BrandKey>(tab.TabIcon.Icon);
        Assert.Equal("pwsh", brand.Key);
        Assert.Equal("PowerShell", tab.TabIcon.TooltipText);
    }

    [Fact]
    public void OnActiveProcessChanged_TracksForegroundFalse_NeverApplyOverride()
    {
        var tab = MakeTab(
            profileIcon: new IconSpec.BrandKey("pwsh", null),
            profileTooltip: "PowerShell",
            tracksForeground: false);
        tab.OnActiveProcessChanged("vim.exe", commandLine: null);

        // Despite a known mapped exe, the override is suppressed.
        Assert.Equal("pwsh", ((IconSpec.BrandKey)tab.TabIcon.Icon).Key);
        Assert.Equal("PowerShell", tab.TabIcon.TooltipText);
    }

    private static TabModel MakeTab(IconSpec profileIcon, string profileTooltip, bool tracksForeground = true)
    {
        var tab = new TabModel(new FakePaneHost());
        var snapshot = ProfileSnapshotStore.From(
            new ResolvedProfile(
                Id: "test", Name: profileTooltip, Command: "cmd.exe",
                WorkingDirectory: null, Icon: profileIcon,
                TabTitle: profileTooltip, Visuals: EffectiveVisualOverrides.Empty,
                ProbeId: null, OrderIndex: 0, IsDefault: true,
                TabIconTracksForeground: tracksForeground),
            version: 1);
        tab.AttachProfileSnapshot(snapshot);
        return tab;
    }
}
