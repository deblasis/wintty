using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Sponsor.Update;

/// <summary>
/// Dev-only <see cref="IUpdateDriver"/> for exercising the pill and
/// adjunct surfaces without a real backend. Moved from the shell
/// assembly to <c>Ghostty.Core</c> in D.2 so unit tests can reach it
/// via the existing Ghostty.Tests -> Ghostty.Core ProjectReference.
///
/// Under <c>#if DEBUG</c> the shell's command palette has entries that
/// invoke <see cref="Simulate"/>.
/// </summary>
internal sealed class UpdateSimulator : IUpdateDriver
{
    private UpdateStateSnapshot _current = UpdateStateSnapshot.Idle();

    public UpdateStateSnapshot Current => _current;

    public event EventHandler<UpdateStateSnapshot>? StateChanged;

    /// <summary>
    /// Push a synthetic state snapshot. Used by command-palette triggers.
    /// </summary>
    public void Simulate(
        UpdateState state,
        string? version = null,
        double? progress = null,
        string? error = null,
        string? releaseNotesUrl = null)
    {
        var snap = new UpdateStateSnapshot(state, version, progress, error, DateTimeOffset.UtcNow)
        {
            ReleaseNotesUrl = releaseNotesUrl,
        };
        _current = snap;
        Debug.WriteLine($"[sponsor/update] sim -> {state}");
        StateChanged?.Invoke(this, snap);
    }

    public Task CheckAsync(CancellationToken cancellationToken = default)
    {
        Debug.WriteLine("[sponsor/update] simulator.CheckAsync (no-op)");
        return Task.CompletedTask;
    }

    public Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        Debug.WriteLine("[sponsor/update] simulator.DownloadAsync (no-op)");
        return Task.CompletedTask;
    }

    public Task ApplyAndRestartAsync()
    {
        Debug.WriteLine("[sponsor/update] simulator.ApplyAndRestartAsync (no-op)");
        return Task.CompletedTask;
    }

    public Task DismissAsync(CancellationToken cancellationToken = default)
    {
        Simulate(UpdateState.Idle);
        return Task.CompletedTask;
    }

    public Task CancelDownloadAsync(CancellationToken cancellationToken = default)
    {
        Debug.WriteLine("[sponsor/update] simulator.CancelDownloadAsync (no-op)");
        return Task.CompletedTask;
    }
}
