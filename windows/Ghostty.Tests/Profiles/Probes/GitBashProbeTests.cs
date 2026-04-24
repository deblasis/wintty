using System.Linq;
using System.Threading;
using Ghostty.Core.Profiles;
using Ghostty.Core.Profiles.Probes;
using Ghostty.Tests.Profiles.Fakes;
using Xunit;

namespace Ghostty.Tests.Profiles.Probes;

public sealed class GitBashProbeTests
{
    [Fact]
    public async System.Threading.Tasks.Task Discover_RegistryAndBashPresent_ReturnsProfile()
    {
        var reg = new FakeRegistryReader();
        reg.SetValue(RegistryHive.LocalMachine, @"SOFTWARE\GitForWindows",
            "InstallPath", @"C:\Program Files\Git");

        var fs = new FakeFileSystem();
        fs.AddFile(@"C:\Program Files\Git\bin\bash.exe");

        var probe = new GitBashProbe(reg, fs);
        var result = await probe.DiscoverAsync(CancellationToken.None);

        var p = Assert.Single(result);
        Assert.Equal("git-bash", p.Id);
        Assert.Equal("Git Bash", p.Name);
        Assert.Contains("--login", p.Command);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_RegistryKeyMissing_ReturnsEmpty()
    {
        var probe = new GitBashProbe(new FakeRegistryReader(), new FakeFileSystem());
        var result = await probe.DiscoverAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_RegistryPresentButBashMissing_ReturnsEmpty()
    {
        var reg = new FakeRegistryReader();
        reg.SetValue(RegistryHive.LocalMachine, @"SOFTWARE\GitForWindows",
            "InstallPath", @"C:\Program Files\Git");
        // bash.exe not in the fake filesystem.
        var probe = new GitBashProbe(reg, new FakeFileSystem());
        var result = await probe.DiscoverAsync(CancellationToken.None);
        Assert.Empty(result);
    }
}
