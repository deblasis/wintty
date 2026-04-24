using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Composition service: merges <see cref="IProfileConfigSource"/>
/// user-defined profiles with a discovery probe run into the ordered
/// snapshot consumed by the settings-UI and command-palette consumers.
/// Dispatches <see cref="IProfileRegistry.ProfilesChanged"/> on the UI
/// thread via an injected <c>Action&lt;Action&gt;</c> (wraps
/// <c>DispatcherQueue.TryEnqueue</c> in the production wiring).
/// </summary>
internal sealed partial class ProfileRegistry : IProfileRegistry
{
    // All three fields (Profiles, ById, DefaultProfileId) are published
    // together via a single volatile reference so readers always see a
    // consistent set -- no torn snapshot between the list and the dict.
    private sealed record Snapshot(
        IReadOnlyList<ResolvedProfile> Profiles,
        FrozenDictionary<string, ResolvedProfile> ById,
        string? DefaultProfileId);

    private static readonly Snapshot EmptySnapshot = new(
        Array.Empty<ResolvedProfile>(),
        FrozenDictionary<string, ResolvedProfile>.Empty,
        DefaultProfileId: null);

    private readonly IProfileConfigSource _source;
    private readonly Func<bool, CancellationToken, Task<IReadOnlyList<DiscoveredProfile>>> _discover;
    private readonly Action<Action> _dispatcher;
    private readonly ILogger<ProfileRegistry> _log;
    private readonly Lock _sync = new();

    private readonly CancellationTokenSource _discoveryCts = new();
    private int _disposed;

    private volatile Snapshot _snapshot = EmptySnapshot;
    private long _version;

    private IReadOnlyList<DiscoveredProfile> _discovered = Array.Empty<DiscoveredProfile>();

    public event Action<IProfileRegistry>? ProfilesChanged;

    public IReadOnlyList<ResolvedProfile> Profiles => _snapshot.Profiles;
    public string? DefaultProfileId => _snapshot.DefaultProfileId;
    public long Version => Interlocked.Read(ref _version);

    public ProfileRegistry(
        IProfileConfigSource source,
        Func<bool, CancellationToken, Task<IReadOnlyList<DiscoveredProfile>>> discover,
        Action<Action> dispatcher,
        ILogger<ProfileRegistry>? log = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(discover);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _source = source;
        _discover = discover;
        _dispatcher = dispatcher;
        _log = log ?? NullLogger<ProfileRegistry>.Instance;

        RecomposeAndFire();
        _source.ProfileConfigChanged += OnSourceChanged;
        _ = RunInitialDiscoveryAsync();
    }

    private async Task RunInitialDiscoveryAsync()
    {
        try
        {
            var discovered = await _discover(false, _discoveryCts.Token).ConfigureAwait(false);
            lock (_sync)
            {
                _discovered = discovered;
            }
            RecomposeAndFire();
        }
        catch (OperationCanceledException)
        {
            // Disposal-initiated cancellation is expected; do not log.
        }
        catch (Exception ex)
        {
            LogDiscoveryRefreshFailed(ex);
        }
    }

    private void OnSourceChanged() => RecomposeAndFire();

    private void RecomposeAndFire()
    {
        // Disposed-guard: a probe that ignores its cancellation token
        // can still return normally after Dispose cancels _discoveryCts.
        // If that happens, the continuation reaches here -- skip the
        // snapshot publish and event dispatch so subscribers don't
        // see updates against a torn-down registry.
        if (Volatile.Read(ref _disposed) != 0) return;

        IReadOnlyList<ResolvedProfile> next;
        FrozenDictionary<string, ResolvedProfile> nextById;
        string? nextDefault;

        lock (_sync)
        {
            var resolved = ProfileOrderResolver.Resolve(
                user: [.. _source.ParsedProfiles.Values],
                discovered: _discovered,
                profileOrder: _source.ProfileOrder,
                defaultProfileId: _source.DefaultProfileId,
                hidden: _source.HiddenProfileIds);

            next = resolved;
            var dict = new Dictionary<string, ResolvedProfile>(resolved.Count, StringComparer.OrdinalIgnoreCase);
            nextDefault = null;
            foreach (var p in resolved)
            {
                dict[p.Id] = p;
                if (p.IsDefault) nextDefault = p.Id;
            }
            nextById = dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }

        _snapshot = new Snapshot(next, nextById, nextDefault);
        var newVersion = Interlocked.Increment(ref _version);
        LogRecomposed(newVersion, next.Count);

        _dispatcher(() => ProfilesChanged?.Invoke(this));
    }

    public ResolvedProfile? Resolve(string profileId)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        return _snapshot.ById.TryGetValue(profileId, out var p) ? p : null;
    }

    public async Task RefreshDiscoveryAsync(CancellationToken ct)
    {
        // Dispose guard: CreateLinkedTokenSource below would throw
        // ObjectDisposedException on _discoveryCts after Dispose, so
        // surface the disposal explicitly instead of as a noisy fault.
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(ProfileRegistry));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _discoveryCts.Token);
        try
        {
            var discovered = await _discover(true, linked.Token).ConfigureAwait(false);
            lock (_sync)
            {
                _discovered = discovered;
            }
            RecomposeAndFire();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // User-initiated refresh: log and rethrow so the caller
            // (settings-UI button, command palette) can show a failure
            // toast. The bootstrap path in RunInitialDiscoveryAsync
            // swallows by design because no caller is waiting on it.
            LogDiscoveryRefreshFailed(ex);
            throw;
        }
    }

    public void Dispose()
    {
        // Idempotent: second call is a no-op. App.xaml.cs's shutdown
        // path can run twice on error recovery, and CTS.Cancel throws
        // ObjectDisposedException after the first Dispose.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _source.ProfileConfigChanged -= OnSourceChanged;
        _discoveryCts.Cancel();
        _discoveryCts.Dispose();
    }

    [LoggerMessage(EventId = LogEvents.Profiles.RegistryRecomposed,
                   Level = LogLevel.Debug,
                   Message = "registry recomposed: version={Version} count={Count}")]
    private partial void LogRecomposed(long version, int count);

    [LoggerMessage(EventId = LogEvents.Profiles.DiscoveryRefreshFailed,
                   Level = LogLevel.Warning,
                   Message = "discovery refresh failed")]
    private partial void LogDiscoveryRefreshFailed(Exception ex);
}
