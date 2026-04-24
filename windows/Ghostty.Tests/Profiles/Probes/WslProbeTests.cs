using System;
using System.Linq;
using System.Threading;
using Ghostty.Core.Profiles;
using Ghostty.Core.Profiles.Probes;
using Ghostty.Tests.Profiles.Fakes;
using Xunit;

namespace Ghostty.Tests.Profiles.Probes;

public sealed class WslProbeTests
{
    [Fact]
    public async System.Threading.Tasks.Task Discover_ThreeDistros_ReturnsThreeProfiles()
    {
        var runner = new FakeProcessRunner();
        runner.EnqueueResult("wsl.exe", new[] { "--list", "--quiet" },
            new ProcessResult(0, "Ubuntu-22.04\nDebian\nkali-linux\n", "", TimeSpan.Zero));

        var probe = new WslProbe(runner);
        var result = await probe.DiscoverAsync(CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, p => p.Id == "wsl-ubuntu-2204");
        Assert.Contains(result, p => p.Id == "wsl-debian");
        Assert.Contains(result, p => p.Id == "wsl-kali-linux");
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_WslNotInstalled_ReturnsEmpty()
    {
        var runner = new FakeProcessRunner();
        // No canned result -> exit -1.
        var probe = new WslProbe(runner);
        var result = await probe.DiscoverAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_EmptyList_ReturnsEmpty()
    {
        var runner = new FakeProcessRunner();
        runner.EnqueueResult("wsl.exe", new[] { "--list", "--quiet" },
            new ProcessResult(0, "", "", TimeSpan.Zero));
        var probe = new WslProbe(runner);
        var result = await probe.DiscoverAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_GarbledOutput_SkipsMalformedLinesReturnsValidOnes()
    {
        var runner = new FakeProcessRunner();
        // Lines that are all-NUL or non-printable should be dropped.
        runner.EnqueueResult("wsl.exe", new[] { "--list", "--quiet" },
            new ProcessResult(0, "Ubuntu\n\0\0\0\n  \n", "", TimeSpan.Zero));
        var probe = new WslProbe(runner);
        var result = await probe.DiscoverAsync(CancellationToken.None);

        var p = Assert.Single(result);
        Assert.Equal("wsl-ubuntu", p.Id);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_CommandIsWslDashD()
    {
        var runner = new FakeProcessRunner();
        runner.EnqueueResult("wsl.exe", new[] { "--list", "--quiet" },
            new ProcessResult(0, "Ubuntu-22.04\n", "", TimeSpan.Zero));
        var probe = new WslProbe(runner);
        var result = await probe.DiscoverAsync(CancellationToken.None);
        var p = Assert.Single(result);
        Assert.Equal("wsl.exe -d Ubuntu-22.04", p.Command);
    }
}
