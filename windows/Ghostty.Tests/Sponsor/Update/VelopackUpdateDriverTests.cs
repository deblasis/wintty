using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Sponsor.Auth;
using Ghostty.Core.Sponsor.Update;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ghostty.Tests.Sponsor.Update;

public partial class VelopackUpdateDriverTests
{
    internal sealed class FakeVelopackManager : IVelopackManager
    {
        public bool IsInstalled { get; set; } = true;
        public VelopackUpdateInfo? NextCheckResult { get; set; }
        public System.Collections.Generic.List<int> ProgressEmits { get; } = new();
        public bool ApplyCalled { get; private set; }
        public System.Exception? CheckThrows { get; set; }
        public System.Exception? DownloadThrows { get; set; }
        public System.TimeSpan DownloadDelay { get; set; } = System.TimeSpan.Zero;

        public Task<VelopackUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct)
        {
            if (CheckThrows is not null) throw CheckThrows;
            return Task.FromResult(NextCheckResult);
        }

        public async Task DownloadUpdatesAsync(
            VelopackUpdateInfo info, System.IProgress<int> progress, CancellationToken ct)
        {
            if (DownloadThrows is not null) throw DownloadThrows;
            for (int p = 0; p <= 100; p += 25)
            {
                ct.ThrowIfCancellationRequested();
                if (DownloadDelay > System.TimeSpan.Zero)
                    await Task.Delay(DownloadDelay, ct);
                progress.Report(p);
                ProgressEmits.Add(p);
            }
        }

        public void ApplyUpdatesAndRestart(VelopackUpdateInfo info)
        {
            ApplyCalled = true;
        }
    }

    internal sealed class StubTokens : ISponsorTokenProvider
    {
        public string? Token { get; set; } = "valid";
        public int InvalidateCount { get; private set; }
        public Task<string?> GetTokenAsync(CancellationToken ct = default) => Task.FromResult(Token);
        public void Invalidate() { InvalidateCount++; Token = null; }
        public event System.EventHandler? TokenInvalidated;
        public void RaiseInvalidated() => TokenInvalidated?.Invoke(this, System.EventArgs.Empty);
    }

    [Fact]
    public void Current_DefaultsToIdle()
    {
        var driver = new VelopackUpdateDriver(new FakeVelopackManager(), new StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);
        Assert.Equal(UpdateState.Idle, driver.Current.State);
    }

    [Fact]
    public async Task CheckAsync_UpdateAvailable_EmitsSnapshotWithVersion()
    {
        var mgr = new FakeVelopackManager
        {
            NextCheckResult = new VelopackUpdateInfo("1.4.2", "https://notes.example", new object()),
        };
        var driver = new VelopackUpdateDriver(mgr, new StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);

        var seen = new System.Collections.Generic.List<UpdateStateSnapshot>();
        driver.StateChanged += (_, s) => seen.Add(s);

        await driver.CheckAsync();

        Assert.Single(seen);
        Assert.Equal(UpdateState.UpdateAvailable, seen[0].State);
        Assert.Equal("1.4.2", seen[0].TargetVersion);
        Assert.Equal("https://notes.example", seen[0].ReleaseNotesUrl);
    }

    [Fact]
    public async Task CheckAsync_NoUpdate_EmitsNoUpdatesFound()
    {
        var mgr = new FakeVelopackManager { NextCheckResult = null };
        var driver = new VelopackUpdateDriver(mgr, new StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);

        await driver.CheckAsync();
        Assert.Equal(UpdateState.NoUpdatesFound, driver.Current.State);
    }

    [Fact]
    public async Task CheckAsync_NoToken_EmitsErrorWithoutCallingManager()
    {
        var mgr = new FakeVelopackManager();
        var driver = new VelopackUpdateDriver(mgr, new StubTokens { Token = null },
            NullLogger<VelopackUpdateDriver>.Instance);

        await driver.CheckAsync();
        Assert.Equal(UpdateState.Error, driver.Current.State);
        Assert.Equal("Sign in to check for updates.", driver.Current.ErrorMessage);
        Assert.False(mgr.ApplyCalled);
    }

    [Fact]
    public async Task CheckAsync_ManagerThrowsUpdateCheckException_MapsToError()
    {
        var mgr = new FakeVelopackManager
        {
            CheckThrows = new UpdateCheckException(UpdateErrorKind.ServerError, "503"),
        };
        var driver = new VelopackUpdateDriver(mgr, new StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);

        await driver.CheckAsync();
        Assert.Equal(UpdateState.Error, driver.Current.State);
        Assert.Equal("Update server is having a hiccup. Try again later.", driver.Current.ErrorMessage);
    }

    [Fact]
    public async Task CheckAsync_UnknownException_DoesNotPropagate()
    {
        var mgr = new FakeVelopackManager
        {
            CheckThrows = new System.InvalidOperationException("unexpected"),
        };
        var driver = new VelopackUpdateDriver(mgr, new StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);

        await driver.CheckAsync();
        Assert.Equal(UpdateState.Error, driver.Current.State);
    }

    [Fact]
    public async Task CheckAsync_NotInstalled_EmitsTargetedErrorBeforeNetwork()
    {
        var mgr = new FakeVelopackManager
        {
            IsInstalled = false,
            // Neither CheckThrows nor NextCheckResult should be touched.
            CheckThrows = new System.InvalidOperationException("must not reach manager"),
        };
        var driver = new VelopackUpdateDriver(mgr, new StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);

        await driver.CheckAsync();

        Assert.Equal(UpdateState.Error, driver.Current.State);
        Assert.Contains("signed installs", driver.Current.ErrorMessage ?? "");
    }

    [Fact]
    public async Task DownloadAsync_HappyPath_EmitsProgressThenRestartPending()
    {
        var info = new VelopackUpdateInfo("1.4.2", null, new object());
        var mgr = new FakeVelopackManager { NextCheckResult = info };
        var driver = new VelopackUpdateDriver(mgr, new StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);

        await driver.CheckAsync();
        Assert.Equal(UpdateState.UpdateAvailable, driver.Current.State);

        var seen = new System.Collections.Generic.List<UpdateStateSnapshot>();
        driver.StateChanged += (_, s) => seen.Add(s);

        await driver.DownloadAsync();

        // FakeVelopackManager reports 0, 25, 50, 75, 100; expect 5 Downloading + 1 RestartPending.
        var dl = seen.FindAll(s => s.State == UpdateState.Downloading).Count;
        Assert.InRange(dl, 5, 6);
        Assert.Equal(UpdateState.RestartPending, seen[^1].State);
        Assert.Equal("1.4.2", seen[^1].TargetVersion);
    }

    [Fact]
    public async Task DownloadAsync_FromIdleState_NoOps()
    {
        var mgr = new FakeVelopackManager();
        var driver = new VelopackUpdateDriver(mgr, new StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);

        await driver.DownloadAsync();
        Assert.Equal(UpdateState.Idle, driver.Current.State);
        Assert.Empty(mgr.ProgressEmits);
    }

    [Fact]
    public async Task DownloadAsync_ManagerThrows_EmitsError()
    {
        var info = new VelopackUpdateInfo("1.4.2", null, new object());
        var mgr = new FakeVelopackManager
        {
            NextCheckResult = info,
            DownloadThrows = new UpdateCheckException(UpdateErrorKind.HashMismatch, "bad-sha"),
        };
        var driver = new VelopackUpdateDriver(mgr, new StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);

        await driver.CheckAsync();
        await driver.DownloadAsync();

        Assert.Equal(UpdateState.Error, driver.Current.State);
        Assert.Equal("Downloaded update didn't verify. Try again.", driver.Current.ErrorMessage);
    }

    [Fact]
    public async Task DownloadAsync_ProgressCoalescing_NoDuplicateIntegerEmits()
    {
        var info = new VelopackUpdateInfo("1.4.2", null, new object());
        var mgr = new FakeVelopackManager { NextCheckResult = info };
        var driver = new VelopackUpdateDriver(mgr, new StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);
        await driver.CheckAsync();

        var emits = new System.Collections.Generic.List<int>();
        driver.StateChanged += (_, s) =>
        {
            if (s.State == UpdateState.Downloading && s.Progress.HasValue)
                emits.Add((int)System.Math.Round(s.Progress.Value * 100));
        };

        await driver.DownloadAsync();
        Assert.Equal(new[] { 0, 25, 50, 75, 100 }, emits);
    }

    [Fact]
    public async Task CancelDownloadAsync_DuringDownload_RevertsToUpdateAvailable()
    {
        var info = new VelopackUpdateInfo("1.4.2", "https://notes", new object());
        var mgr = new FakeVelopackManager
        {
            NextCheckResult = info,
            DownloadDelay = System.TimeSpan.FromMilliseconds(50),
        };
        var driver = new VelopackUpdateDriver(mgr, new StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);
        await driver.CheckAsync();

        var downloadTask = driver.DownloadAsync();
        await Task.Delay(20);
        await driver.CancelDownloadAsync();
        await downloadTask;

        Assert.Equal(UpdateState.UpdateAvailable, driver.Current.State);
        Assert.Equal("1.4.2", driver.Current.TargetVersion);
        Assert.Equal("https://notes", driver.Current.ReleaseNotesUrl);
    }

    [Fact]
    public async Task CancelDownloadAsync_WhenNoDownload_IsNoOp()
    {
        var driver = new VelopackUpdateDriver(new FakeVelopackManager(), new StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);
        await driver.CancelDownloadAsync();
        Assert.Equal(UpdateState.Idle, driver.Current.State);
    }

    [Fact]
    public async Task ApplyAndRestartAsync_HappyPath_EmitsInstallingThenCallsManager()
    {
        var info = new VelopackUpdateInfo("1.4.2", null, new object());
        var mgr = new FakeVelopackManager { NextCheckResult = info };
        var driver = new VelopackUpdateDriver(mgr, new StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);
        await driver.CheckAsync();
        await driver.DownloadAsync();
        Assert.Equal(UpdateState.RestartPending, driver.Current.State);

        var seen = new System.Collections.Generic.List<UpdateStateSnapshot>();
        driver.StateChanged += (_, s) => seen.Add(s);

        await driver.ApplyAndRestartAsync();
        Assert.Contains(seen, s => s.State == UpdateState.Installing);
        Assert.True(mgr.ApplyCalled);
    }

    [Fact]
    public async Task ApplyAndRestartAsync_ManagerThrows_EmitsError()
    {
        var info = new VelopackUpdateInfo("1.4.2", null, new object());
        var mgr = new FakeVelopackManager { NextCheckResult = info };
        var throwing = new ThrowingApplyManager(mgr);
        var driver = new VelopackUpdateDriver(throwing, new StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);
        await driver.CheckAsync();
        await driver.DownloadAsync();

        await driver.ApplyAndRestartAsync();
        Assert.Equal(UpdateState.Error, driver.Current.State);
        Assert.Equal("Couldn't apply the update. Try again or reinstall.", driver.Current.ErrorMessage);
    }

    private sealed class ThrowingApplyManager : IVelopackManager
    {
        private readonly IVelopackManager _inner;
        public ThrowingApplyManager(IVelopackManager inner) { _inner = inner; }
        public bool IsInstalled => _inner.IsInstalled;
        public Task<VelopackUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct) => _inner.CheckForUpdatesAsync(ct);
        public Task DownloadUpdatesAsync(VelopackUpdateInfo info, System.IProgress<int> progress, CancellationToken ct)
            => _inner.DownloadUpdatesAsync(info, progress, ct);
        public void ApplyUpdatesAndRestart(VelopackUpdateInfo info) =>
            throw new UpdateCheckException(UpdateErrorKind.ApplyFailed, "not installed");
    }

    [Fact]
    public async Task DismissAsync_EmitsIdle()
    {
        var info = new VelopackUpdateInfo("1.4.2", null, new object());
        var mgr = new FakeVelopackManager { NextCheckResult = info };
        var driver = new VelopackUpdateDriver(mgr, new StubTokens(),
            NullLogger<VelopackUpdateDriver>.Instance);
        await driver.CheckAsync();
        Assert.Equal(UpdateState.UpdateAvailable, driver.Current.State);
        await driver.DismissAsync();
        Assert.Equal(UpdateState.Idle, driver.Current.State);
    }

    [Fact]
    public void TokenInvalidated_WhileIdle_EmitsErrorAuthExpired()
    {
        var tokens = new StubTokens();
        var driver = new VelopackUpdateDriver(new FakeVelopackManager(), tokens,
            NullLogger<VelopackUpdateDriver>.Instance);

        tokens.RaiseInvalidated();
        Assert.Equal(UpdateState.Error, driver.Current.State);
        Assert.Equal("Sponsor session expired. Sign in again.", driver.Current.ErrorMessage);
    }

    [Fact]
    public async Task TokenInvalidated_DuringDownload_CancelsAndEmitsError()
    {
        var info = new VelopackUpdateInfo("1.4.2", null, new object());
        var mgr = new FakeVelopackManager
        {
            NextCheckResult = info,
            DownloadDelay = TimeSpan.FromMilliseconds(50),
        };
        var tokens = new StubTokens();
        var driver = new VelopackUpdateDriver(mgr, tokens, NullLogger<VelopackUpdateDriver>.Instance);
        await driver.CheckAsync();

        var downloadTask = driver.DownloadAsync();
        await Task.Delay(20);
        tokens.RaiseInvalidated();
        await downloadTask;

        Assert.Equal(UpdateState.Error, driver.Current.State);
    }
}
