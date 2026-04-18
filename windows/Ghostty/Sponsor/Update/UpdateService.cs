using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Config;
using Ghostty.Core.Sponsor.Update;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// App-scoped update orchestrator. Re-raises the underlying driver's
/// state transitions for five WinUI-aware consumers (pill, taskbar,
/// toast, jump list, exit interceptor). In D.1 the poll timer is a
/// stub; the simulator drives state directly via the driver API.
/// D.2 will wire the timer to <c>_driver.CheckAsync</c> and branch on
/// the <c>auto-update</c> config key.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private readonly IUpdateDriver _driver;
    private readonly IConfigService _config;
    private readonly CancellationTokenSource _cts = new();
    private PeriodicTimer? _timer;
    private Task? _pollLoop;

    public UpdateService(IUpdateDriver driver, IConfigService config)
    {
        _driver = driver;
        _config = config;
        _driver.StateChanged += OnDriverStateChanged;
        _config.ConfigChanged += OnConfigChanged;
    }

    /// <summary>Current state snapshot.</summary>
    public UpdateStateSnapshot Current => _driver.Current;

    /// <summary>Raised on every state transition, on the driver's thread.</summary>
    public event EventHandler<UpdateStateSnapshot>? StateChanged;

    /// <summary>
    /// Start the poll loop. D.1 uses a 4-hour interval but the loop
    /// body is a no-op until D.2 wires <see cref="IUpdateDriver.CheckAsync"/>.
    /// Safe to call multiple times; second+ calls no-op.
    /// </summary>
    public void Start()
    {
        if (_timer is not null) return;
        _timer = new PeriodicTimer(TimeSpan.FromHours(4));
        _pollLoop = PollLoopAsync(_cts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                // D.2: call _driver.CheckAsync(ct) here, branching on
                // the auto-update config key.
                Debug.WriteLine("[sponsor/update] poll tick (D.1 no-op)");
            }
        }
        catch (OperationCanceledException) { /* expected on dispose */ }
    }

    public Task CheckNowAsync() => _driver.CheckAsync(_cts.Token);
    public Task DownloadAsync() => _driver.DownloadAsync(_cts.Token);
    public Task ApplyAndRestartAsync() => _driver.ApplyAndRestartAsync();
    public Task DismissAsync() => _driver.DismissAsync(_cts.Token);

    private void OnDriverStateChanged(object? sender, UpdateStateSnapshot snap)
    {
        StateChanged?.Invoke(this, snap);
    }

    private void OnConfigChanged(IConfigService _)
    {
        // D.1: no-op. D.3 will re-evaluate `auto-update` here and toggle
        // the poll loop on/off.
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _driver.StateChanged -= OnDriverStateChanged;
        _config.ConfigChanged -= OnConfigChanged;
        // Cancel() makes WaitForNextTickAsync throw OperationCanceledException
        // synchronously, so PollLoopAsync observes cancellation in its existing
        // catch and exits; the Task completes shortly after on a thread-pool
        // thread. We intentionally do NOT block on _pollLoop here: Dispose runs
        // on the UI thread during shutdown, and D.2 will add awaits inside the
        // loop body that could deadlock a sync-over-async wait. The captured
        // CancellationToken remains valid after _cts.Dispose() per the
        // CancellationTokenSource contract.
        _cts.Dispose();
    }
}
