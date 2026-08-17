using Ghostty.Core.JumpList;
using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.JumpList;

public class JumpListProfilesTests
{
    [Fact]
    public void From_Empty_ReturnsEmpty()
    {
        Assert.Empty(JumpListProfiles.From(System.Array.Empty<ResolvedProfile>()));
    }

    [Fact]
    public void From_MapsIdNameCommandAndWorkingDirectory()
    {
        var resolved = new[]
        {
            Profile("pwsh", "PowerShell", "pwsh.exe", @"C:\Users\me"),
            Profile("cmd", "Command Prompt", "cmd.exe", null),
        };

        var entries = JumpListProfiles.From(resolved);

        Assert.Equal(2, entries.Count);
        Assert.Equal("pwsh", entries[0].Id);
        Assert.Equal("PowerShell", entries[0].DisplayName);
        Assert.Equal("pwsh.exe", entries[0].ShellCommand);
        Assert.Equal(@"C:\Users\me", entries[0].WorkingDirectory);
        Assert.Equal("cmd", entries[1].Id);
        Assert.Equal("Command Prompt", entries[1].DisplayName);
    }

    [Fact]
    public void From_PathIcon_BecomesIconPath()
    {
        var resolved = new[]
        {
            Profile("ico", "Ico", "x.exe", null, new IconSpec.Path(@"C:\icons\pwsh.ico")),
        };

        var entries = JumpListProfiles.From(resolved);
        Assert.Equal(@"C:\icons\pwsh.ico", entries[0].IconPath);
    }

    [Fact]
    public void From_AutoForExeIcon_BecomesIconPath()
    {
        var resolved = new[]
        {
            Profile("exe", "Exe", "x.exe", null, new IconSpec.AutoForExe(@"C:\Windows\System32\cmd.exe")),
        };

        var entries = JumpListProfiles.From(resolved);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", entries[0].IconPath);
    }

    [Fact]
    public void From_BundledIcon_LeavesIconPathNull()
    {
        var resolved = new[]
        {
            Profile("bundled", "Bundled", "x.exe", null, new IconSpec.BundledKey("pwsh")),
        };

        var entries = JumpListProfiles.From(resolved);
        Assert.Null(entries[0].IconPath);
    }

    private static ResolvedProfile Profile(
        string id,
        string name,
        string command,
        string? cwd,
        IconSpec? icon = null)
        => new(
            Id: id,
            Name: name,
            Command: command,
            WorkingDirectory: cwd,
            Icon: icon ?? new IconSpec.BundledKey("default"),
            TabTitle: name,
            Visuals: EffectiveVisualOverrides.Empty,
            ProbeId: null,
            OrderIndex: 0,
            IsDefault: false);
}
