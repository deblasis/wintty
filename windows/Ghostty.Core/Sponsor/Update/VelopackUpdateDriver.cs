using System;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Sponsor.Auth;
using Ghostty.Core.Sponsor.Update.Mapping;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.Sponsor.Update;

/// <summary>
/// Production <see cref="IUpdateDriver"/> backed by Velopack. Wraps
/// <see cref="IVelopackManager"/> (a thin shim over Velopack's
/// UpdateManager) so the state machine is independently testable.
/// Public methods never throw: failures become Error snapshots via
/// <see cref="UpdateStateMapping.FromError"/>.
/// </summary>
internal sealed partial class VelopackUpdateDriver : IUpdateDriver, IDisposable
{
    private readonly IVelopackManager _manager;
    private readonly ISponsorTokenProvider _tokens;
    private readonly ILogger<VelopackUpdateDriver> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private UpdateStateSnapshot _current = UpdateStateSnapshot.Idle();
    private CancellationTokenSource? _downloadCts;

    // Snapshot of the last known update so cancel/restart transitions
    // can preserve Version + ReleaseNotesUrl across state hops.
    private VelopackUpdateInfo? _lastInfo;

    public VelopackUpdateDriver(
        IVelopackManager manager,
        ISponsorTokenProvider tokens,
        ILogger<VelopackUpdateDriver> logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(logger);

        _manager = manager;
        _tokens = tokens;
        _logger = logger;
        _tokens.TokenInvalidated += OnTokenInvalidated;
    }

    public UpdateStateSnapshot Current => _current;
    public event EventHandler<UpdateStateSnapshot>? StateChanged;

    public async Task CheckAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Short-circuit if the running exe isn't a Velopack install.
            // Without Update.exe on disk, Velopack's own CheckForUpdatesAsync
            // throws NotInstalledException before any network call, which
            // would otherwise get mapped to the misleading ServerError
            // fallback. Emit a targeted message instead.
            if (!_manager.IsInstalled)
            {
                _logger.LogInformation("[sponsor/update] CheckAsync aborted: not a Velopack install");
                Emit(UpdateStateMapping.FromError(
                    new UpdateCheckException(UpdateErrorKind.NotInstalled, "dev checkout or portable zip"),
                    targetVersion: _lastInfo?.Version));
                return;
            }

            var token = await _tokens.GetTokenAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("[sponsor/update] CheckAsync aborted: no JWT");
                Emit(UpdateStateMapping.FromError(
                    new UpdateCheckException(UpdateErrorKind.NoToken, "no JWT"),
                    targetVersion: _lastInfo?.Version));
                return;
            }

            VelopackUpdateInfo? info;
            try
            {
                info = await _manager.CheckForUpdatesAsync(ct).ConfigureAwait(false);
            }
            catch (UpdateCheckException uce)
            {
                _logger.LogWarning("[sponsor/update] CheckAsync known failure: {Kind}", uce.Kind);
                Emit(UpdateStateMapping.FromError(uce, targetVersion: _lastInfo?.Version));
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[sponsor/update] CheckAsync unexpected failure");
                Emit(UpdateStateMapping.FromError(
                    new UpdateCheckException(UpdateErrorKind.ServerError, ex.Message, ex),
                    targetVersion: _lastInfo?.Version));
                return;
            }

            _lastInfo = info;
            Emit(UpdateStateMapping.FromCheckResult(info));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DownloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_current.State != UpdateState.UpdateAvailable || _lastInfo is null)
            {
                _logger.LogDebug("[sponsor/update] DownloadAsync no-op: state={State}", _current.State);
                return;
            }

            var info = _lastInfo;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _downloadCts = cts;

            int lastEmitted = -1;
            var progress = new SyncProgress(p =>
            {
                var clamped = Math.Clamp(p, 0, 100);
                if (clamped > lastEmitted)
                {
                    lastEmitted = clamped;
                    _current = UpdateStateMapping.FromDownloadProgress(
                        clamped, info.Version, info.ReleaseNotesUrl);
                    StateChanged?.Invoke(this, _current);
                }
            });

            try
            {
                await _manager.DownloadUpdatesAsync(info, progress, cts.Token).ConfigureAwait(false);
                if (lastEmitted < 100)
                {
                    _current = UpdateStateMapping.FromDownloadProgress(
                        100, info.Version, info.ReleaseNotesUrl);
                    StateChanged?.Invoke(this, _current);
                }
                Emit(UpdateStateMapping.FromDownloadComplete(info.Version, info.ReleaseNotesUrl));
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[sponsor/update] DownloadAsync cancelled");
                // OnTokenInvalidated may have already written an Error snapshot
                // directly (without holding the gate). If so, keep it.
                if (_current.State != UpdateState.Error)
                    Emit(UpdateStateMapping.FromCancel(info.Version, info.ReleaseNotesUrl));
            }
            catch (UpdateCheckException uce)
            {
                _logger.LogWarning("[sponsor/update] DownloadAsync known failure: {Kind}", uce.Kind);
                Emit(UpdateStateMapping.FromError(uce, info.Version));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[sponsor/update] DownloadAsync unexpected failure");
                Emit(UpdateStateMapping.FromError(
                    new UpdateCheckException(UpdateErrorKind.ServerError, ex.Message, ex),
                    info.Version));
            }
            finally
            {
                _downloadCts = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyAndRestartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_lastInfo is null)
            {
                _logger.LogDebug("[sponsor/update] ApplyAndRestartAsync: no info on hand; no-op");
                return;
            }

            var info = _lastInfo;
            Emit(new UpdateStateSnapshot(
                UpdateState.Installing,
                info.Version,
                null, null,
                DateTimeOffset.UtcNow)
            {
                ReleaseNotesUrl = info.ReleaseNotesUrl,
            });

            try
            {
                _manager.ApplyUpdatesAndRestart(info);
            }
            catch (UpdateCheckException uce)
            {
                _logger.LogWarning("[sponsor/update] ApplyUpdatesAndRestart known failure: {Kind}", uce.Kind);
                Emit(UpdateStateMapping.FromError(uce, info.Version));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[sponsor/update] ApplyUpdatesAndRestart unexpected failure");
                Emit(UpdateStateMapping.FromError(
                    new UpdateCheckException(UpdateErrorKind.ApplyFailed, ex.Message, ex),
                    info.Version));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DismissAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Emit(UpdateStateSnapshot.Idle());
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task CancelDownloadAsync(CancellationToken ct = default)
    {
        _downloadCts?.Cancel();
        return Task.CompletedTask;
    }

    // Invokes the callback synchronously on the caller thread instead of
    // posting to a SynchronizationContext. Progress<T> would capture the
    // threadpool context in tests (no SC present), causing the handler to
    // run after DownloadUpdatesAsync returns and breaking progress-order
    // assertions. Synchronous dispatch keeps the fake's pump and the
    // driver's lastEmitted guard on the same thread.
    private sealed class SyncProgress : IProgress<int>
    {
        private readonly Action<int> _action;
        public SyncProgress(Action<int> action) { _action = action; }
        public void Report(int value) => _action(value);
    }

    private void OnTokenInvalidated(object? sender, EventArgs e)
    {
        _logger.LogWarning("[sponsor/update] TokenInvalidated event fired");

        // Write the Error snapshot before cancelling any in-flight download.
        // DownloadAsync's OperationCanceledException catch reads _current and
        // only emits FromCancel when the state isn't already Error; publishing
        // Error first ensures that check wins whether the catch inlines inside
        // Cancel() or resumes on the threadpool. Cancel() is a release barrier,
        // so the write is visible to the observer thread. Reversing this order
        // lets FromCancel race past Error under concurrent load.
        var snap = UpdateStateMapping.FromError(
            new UpdateCheckException(UpdateErrorKind.AuthExpired, "token invalidated"),
            targetVersion: _lastInfo?.Version);
        _current = snap;
        StateChanged?.Invoke(this, snap);

        _downloadCts?.Cancel();
    }

    private void Emit(UpdateStateSnapshot snap)
    {
        _current = snap;
        StateChanged?.Invoke(this, snap);
    }

    public void Dispose()
    {
        _tokens.TokenInvalidated -= OnTokenInvalidated;
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _gate.Dispose();
    }
}
