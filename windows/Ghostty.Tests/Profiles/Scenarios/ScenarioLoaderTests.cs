using System.Linq;
using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles.Scenarios;

public sealed class ScenarioLoaderTests
{
    [Fact]
    public void Load_EmptyMachine_PopulatesCmdOnly()
    {
        var s = ScenarioLoader.Load("EmptyMachine");

        Assert.True(s.FileSystem.FileExists(@"C:\Windows\System32\cmd.exe"));
        Assert.False(s.FileSystem.FileExists(@"C:\Program Files\PowerShell\7\pwsh.exe"));
        Assert.Equal(@"C:\Windows\System32", s.FileSystem.GetKnownFolder(KnownFolderId.System));
    }

    [Fact]
    public void Load_HeavyDevMachine_PopulatesAllProbes()
    {
        var s = ScenarioLoader.Load("HeavyDevMachine");

        Assert.True(s.FileSystem.FileExists(@"C:\Program Files\PowerShell\7\pwsh.exe"));
        Assert.True(s.Registry.KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\GitForWindows"));
        Assert.Equal(
            @"C:\Program Files\Git",
            s.Registry.ReadString(RegistryHive.LocalMachine, @"SOFTWARE\GitForWindows", "InstallPath"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Load_HeavyDevMachine_ProcessesReturnCannedOutput()
    {
        var s = ScenarioLoader.Load("HeavyDevMachine");

        var result = await s.ProcessRunner.RunAsync(
            "wsl.exe",
            new[] { "--list", "--quiet" },
            System.TimeSpan.FromSeconds(1),
            System.Threading.CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Ubuntu-22.04", result.Stdout);
    }
}
