using System.Linq;
using System.Threading;
using Ghostty.Core.Profiles;
using Ghostty.Core.Profiles.Probes;
using Ghostty.Tests.Profiles.Fakes;
using Xunit;

namespace Ghostty.Tests.Profiles.Probes;

public sealed class Msys2ProbeTests
{
    [Fact]
    public async System.Threading.Tasks.Task Discover_Msys64WithWinpty_ReturnsWrappedProfile()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"C:\msys64\usr\bin\bash.exe");
        fs.AddFile(@"C:\msys64\usr\bin\winpty.exe");

        var p = Assert.Single(await new Msys2Probe(fs).DiscoverAsync(CancellationToken.None));
        Assert.Equal("msys2", p.Id);
        Assert.Equal("MSYS2", p.Name);
        Assert.Contains("winpty.exe", p.Command);
        Assert.Contains("bash.exe", p.Command);
        Assert.Contains("--login", p.Command);
        Assert.Equal("msys2", p.ProbeId);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_Msys64NoWinpty_ReturnsBareBash()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"C:\msys64\usr\bin\bash.exe");

        var p = Assert.Single(await new Msys2Probe(fs).DiscoverAsync(CancellationToken.None));
        Assert.DoesNotContain("winpty", p.Command);
        Assert.Contains("bash.exe", p.Command);
        Assert.Contains("--login", p.Command);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_Msys32Fallback_ReturnsProfile()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"C:\msys32\usr\bin\bash.exe");

        var p = Assert.Single(await new Msys2Probe(fs).DiscoverAsync(CancellationToken.None));
        Assert.Contains(@"msys32", p.Command);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_NotInstalled_ReturnsEmpty()
    {
        Assert.Empty(await new Msys2Probe(new FakeFileSystem()).DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_PrefersMsys64OverMsys32()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"C:\msys64\usr\bin\bash.exe");
        fs.AddFile(@"C:\msys32\usr\bin\bash.exe");

        var p = Assert.Single(await new Msys2Probe(fs).DiscoverAsync(CancellationToken.None));
        Assert.Contains(@"msys64", p.Command);
    }
}
