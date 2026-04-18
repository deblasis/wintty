using System.Threading.Tasks;
using Ghostty.Core.Sponsor.Update;
using Xunit;

namespace Ghostty.Tests.Sponsor.Update;

public class UpdateSimulatorTests
{
    [Fact]
    public async Task CancelDownloadAsync_IsNoOp_AndReturnsCompleted()
    {
        var sim = new UpdateSimulator();
        await sim.CancelDownloadAsync();
        // Simulator only transitions via Simulate(); CancelDownloadAsync is
        // a no-op that leaves the snapshot untouched.
        Assert.Equal(UpdateState.Idle, sim.Current.State);
    }

    [Fact]
    public async Task DismissAsync_EmitsIdleSnapshot()
    {
        var sim = new UpdateSimulator();
        sim.Simulate(UpdateState.UpdateAvailable, version: "9.9.9");
        Assert.Equal(UpdateState.UpdateAvailable, sim.Current.State);
        await sim.DismissAsync();
        Assert.Equal(UpdateState.Idle, sim.Current.State);
    }

    [Fact]
    public async Task CancelDownloadAsync_FromDownloading_LeavesStateUnchanged()
    {
        // The simulator is a deliberate no-op — the real VelopackUpdateDriver
        // (Phase 4) reverts to UpdateAvailable on cancel. This test pins the
        // simulator's deviation so future readers don't "fix" it.
        var sim = new UpdateSimulator();
        sim.Simulate(UpdateState.Downloading, version: "9.9.9", progress: 0.5);
        Assert.Equal(UpdateState.Downloading, sim.Current.State);
        await sim.CancelDownloadAsync();
        Assert.Equal(UpdateState.Downloading, sim.Current.State);
    }
}
