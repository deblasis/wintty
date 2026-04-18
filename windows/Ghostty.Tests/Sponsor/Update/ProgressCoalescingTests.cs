using System.Threading.Tasks;
using Ghostty.Core.Sponsor.Auth;
using Ghostty.Core.Sponsor.Update;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ghostty.Tests.Sponsor.Update;

public class ProgressCoalescingTests
{
    [Fact]
    public async Task SubIntegerProgressValues_CoalesceToOneEmitPerBoundary()
    {
        var info = new VelopackUpdateInfo("1.0.0", null, new object());
        var mgr = new ChatteryManager { NextCheckResult = info };
        var driver = new VelopackUpdateDriver(mgr,
            new VelopackUpdateDriverTests.StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);
        await driver.CheckAsync();

        var emits = new System.Collections.Generic.List<int>();
        driver.StateChanged += (_, s) =>
        {
            if (s.State == UpdateState.Downloading && s.Progress.HasValue)
                emits.Add((int)System.Math.Round(s.Progress.Value * 100));
        };

        await driver.DownloadAsync();

        // ChatteryManager fires many sub-integer values; after coalescing,
        // we should see strictly increasing integer emits.
        for (int i = 1; i < emits.Count; i++)
            Assert.True(emits[i] > emits[i - 1], $"emit[{i}]={emits[i]} not > emit[{i-1}]={emits[i-1]}");

        Assert.Contains(100, emits);
    }

    /// <summary>
    /// Emits in 0.3% logical steps (rounded to int on report) to exercise the coalescer.
    /// </summary>
    private sealed class ChatteryManager : IVelopackManager
    {
        public VelopackUpdateInfo? NextCheckResult { get; set; }
        public bool IsInstalled => true;

        public Task<VelopackUpdateInfo?> CheckForUpdatesAsync(System.Threading.CancellationToken ct)
            => Task.FromResult(NextCheckResult);

        public Task DownloadUpdatesAsync(
            VelopackUpdateInfo info, System.IProgress<int> progress, System.Threading.CancellationToken ct)
        {
            for (double p = 0; p <= 100; p += 0.3)
            {
                progress.Report((int)p);
            }
            progress.Report(100);
            return Task.CompletedTask;
        }

        public void ApplyUpdatesAndRestart(VelopackUpdateInfo info) { }
    }
}
