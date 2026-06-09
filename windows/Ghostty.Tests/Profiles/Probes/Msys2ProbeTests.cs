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
        Assert.Contains("msys32", p.Command);
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
        Assert.Contains("msys64", p.Command);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_Ucrt64Present_EmitsVariantAfterBase()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"C:\msys64\usr\bin\bash.exe");
        fs.AddFile(@"C:\msys64\usr\bin\env.exe");
        fs.AddFile(@"C:\msys64\ucrt64.exe");

        var result = (await new Msys2Probe(fs).DiscoverAsync(CancellationToken.None)).ToList();
        Assert.Equal(2, result.Count);
        Assert.Equal("msys2", result[0].Id);

        var v = result[1];
        Assert.Equal("msys2-ucrt64", v.Id);
        Assert.Equal("MSYS2 UCRT64", v.Name);
        Assert.Equal("msys2", v.ProbeId);
        Assert.Contains("env.exe", v.Command);
        Assert.Contains("MSYSTEM=UCRT64", v.Command);
        Assert.Contains("bash.exe", v.Command);
        Assert.Contains("--login", v.Command);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_AllSubsystems_EmitsAllOrdered()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"C:\msys64\usr\bin\bash.exe");
        fs.AddFile(@"C:\msys64\usr\bin\env.exe");
        fs.AddFile(@"C:\msys64\ucrt64.exe");
        fs.AddFile(@"C:\msys64\mingw64.exe");
        fs.AddFile(@"C:\msys64\clang64.exe");

        var ids = (await new Msys2Probe(fs).DiscoverAsync(CancellationToken.None))
            .Select(p => p.Id).ToList();
        Assert.Equal(new[] { "msys2", "msys2-ucrt64", "msys2-mingw64", "msys2-clang64" }, ids);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_32BitSubsystems_AreIgnored()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"C:\msys64\usr\bin\bash.exe");
        fs.AddFile(@"C:\msys64\usr\bin\env.exe");
        fs.AddFile(@"C:\msys64\mingw32.exe");
        fs.AddFile(@"C:\msys64\clang32.exe");

        var p = Assert.Single(await new Msys2Probe(fs).DiscoverAsync(CancellationToken.None));
        Assert.Equal("msys2", p.Id);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_VariantWithWinpty_PrependsWinptyBeforeEnv()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"C:\msys64\usr\bin\bash.exe");
        fs.AddFile(@"C:\msys64\usr\bin\winpty.exe");
        fs.AddFile(@"C:\msys64\usr\bin\env.exe");
        fs.AddFile(@"C:\msys64\ucrt64.exe");

        var v = (await new Msys2Probe(fs).DiscoverAsync(CancellationToken.None)).First(p => p.Id == "msys2-ucrt64");
        Assert.Contains("winpty.exe", v.Command);
        Assert.True(v.Command.IndexOf("winpty.exe") < v.Command.IndexOf("env.exe"),
            "winpty must precede env in the command");
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_VariantNoWinpty_ExactCommand()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"C:\msys64\usr\bin\bash.exe");
        fs.AddFile(@"C:\msys64\usr\bin\env.exe");
        fs.AddFile(@"C:\msys64\ucrt64.exe");

        var v = (await new Msys2Probe(fs).DiscoverAsync(CancellationToken.None)).First(p => p.Id == "msys2-ucrt64");
        // Pin the exact token sequence + quoting: env precedes the unquoted
        // MSYSTEM assignment precedes bash; no winpty when absent.
        Assert.Equal(
            @"C:\msys64\usr\bin\env.exe MSYSTEM=UCRT64 C:\msys64\usr\bin\bash.exe --login -i",
            v.Command);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_Msys32Root_EmitsVariant()
    {
        // Subsystem detection is root-relative; confirm it works under the
        // legacy 32-bit fallback root too.
        var fs = new FakeFileSystem();
        fs.AddFile(@"C:\msys32\usr\bin\bash.exe");
        fs.AddFile(@"C:\msys32\usr\bin\env.exe");
        fs.AddFile(@"C:\msys32\ucrt64.exe");

        var v = (await new Msys2Probe(fs).DiscoverAsync(CancellationToken.None)).First(p => p.Id == "msys2-ucrt64");
        Assert.Contains(@"C:\msys32\usr\bin\env.exe", v.Command);
        Assert.Contains("MSYSTEM=UCRT64", v.Command);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_EnvMissing_EmitsBaseOnly()
    {
        // env.exe absent => cannot set MSYSTEM, so no variants even though
        // a subsystem launcher is present.
        var fs = new FakeFileSystem();
        fs.AddFile(@"C:\msys64\usr\bin\bash.exe");
        fs.AddFile(@"C:\msys64\ucrt64.exe");

        var p = Assert.Single(await new Msys2Probe(fs).DiscoverAsync(CancellationToken.None));
        Assert.Equal("msys2", p.Id);
    }
}
