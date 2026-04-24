using System;
using System.Threading;
using Ghostty.Core.Profiles;
using Ghostty.Core.Profiles.Probes;
using Ghostty.Tests.Profiles.Fakes;
using Xunit;

namespace Ghostty.Tests.Profiles.Probes;

public sealed class AzureCloudShellProbeTests
{
    [Fact]
    public async System.Threading.Tasks.Task Discover_AzPresent_ReturnsProfile()
    {
        var runner = new FakeProcessRunner();
        runner.EnqueueResult("cmd.exe", new[] { "/c", "az", "--version" },
            new ProcessResult(0, "azure-cli 2.55.0\n", "", TimeSpan.Zero));

        var probe = new AzureCloudShellProbe(runner);
        var result = await probe.DiscoverAsync(CancellationToken.None);

        var p = Assert.Single(result);
        Assert.Equal("azure-cloud-shell", p.Id);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_AzMissing_ReturnsEmpty()
    {
        var probe = new AzureCloudShellProbe(new FakeProcessRunner());
        var result = await probe.DiscoverAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_AzNonZeroExit_ReturnsEmpty()
    {
        var runner = new FakeProcessRunner();
        runner.EnqueueResult("cmd.exe", new[] { "/c", "az", "--version" },
            new ProcessResult(1, "", "some error", TimeSpan.Zero));
        var probe = new AzureCloudShellProbe(runner);
        var result = await probe.DiscoverAsync(CancellationToken.None);
        Assert.Empty(result);
    }
}
