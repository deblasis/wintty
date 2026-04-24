using System;
using System.Linq;
using System.Threading;
using Ghostty.Core.Profiles;
using Ghostty.Core.Profiles.Probes;
using Ghostty.Tests.Profiles.Fakes;
using Xunit;

namespace Ghostty.Tests.Profiles.Probes;

public sealed class PowerShellProbeTests
{
    [Fact]
    public async System.Threading.Tasks.Task Discover_Pwsh7Installed_ReturnsPwshProfileWithVersion()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.ProgramFiles, @"C:\Program Files");
        fs.SetKnownFolder(KnownFolderId.System, @"C:\Windows\System32");
        fs.AddFile(@"C:\Program Files\PowerShell\7\pwsh.exe");

        var runner = new FakeProcessRunner();
        runner.EnqueueResult(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            new[] { "-v" },
            new ProcessResult(0, "PowerShell 7.4.1\n", "", TimeSpan.Zero));

        var probe = new PowerShellProbe(fs, runner);
        var result = await probe.DiscoverAsync(CancellationToken.None);

        var p = Assert.Single(result);
        Assert.Equal("pwsh-7", p.Id);
        Assert.Equal("PowerShell 7.4.1", p.Name);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_WindowsPowerShellOnly_ReturnsPowershellProfile()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.System, @"C:\Windows\System32");
        fs.AddFile(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");

        var probe = new PowerShellProbe(fs, new FakeProcessRunner());
        var result = await probe.DiscoverAsync(CancellationToken.None);

        var p = Assert.Single(result);
        Assert.Equal("pwsh-windows", p.Id);
        Assert.Equal("Windows PowerShell", p.Name);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_BothInstalled_ReturnsTwoProfiles()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.ProgramFiles, @"C:\Program Files");
        fs.SetKnownFolder(KnownFolderId.System, @"C:\Windows\System32");
        fs.AddFile(@"C:\Program Files\PowerShell\7\pwsh.exe");
        fs.AddFile(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");

        var runner = new FakeProcessRunner();
        runner.EnqueueResult(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            new[] { "-v" },
            new ProcessResult(0, "PowerShell 7.4.1\n", "", TimeSpan.Zero));

        var probe = new PowerShellProbe(fs, runner);
        var result = await probe.DiscoverAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.Id == "pwsh-7");
        Assert.Contains(result, p => p.Id == "pwsh-windows");
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_PwshVersionProbeFails_FallsBackToGenericName()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.ProgramFiles, @"C:\Program Files");
        fs.AddFile(@"C:\Program Files\PowerShell\7\pwsh.exe");
        // No canned result -> FakeProcessRunner returns exit code -1.

        var probe = new PowerShellProbe(fs, new FakeProcessRunner());
        var result = await probe.DiscoverAsync(CancellationToken.None);

        var p = Assert.Single(result);
        Assert.Equal("pwsh-7", p.Id);
        Assert.Equal("PowerShell", p.Name);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_NoPwshAnywhere_ReturnsEmpty()
    {
        var fs = new FakeFileSystem();
        var probe = new PowerShellProbe(fs, new FakeProcessRunner());
        var result = await probe.DiscoverAsync(CancellationToken.None);
        Assert.Empty(result);
    }
}
