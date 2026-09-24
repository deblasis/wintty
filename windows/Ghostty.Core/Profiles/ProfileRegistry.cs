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
    // All four fields (Profiles, HiddenProfiles, ById, DefaultProfileId)
    // are published together via a single volatile reference so readers
    // always see a consistent set -- no torn snapshot between the visible
    // list, hidden list, and the lookup dict.
    private sealed record Snapshot(
        IReadOnlyList<ResolvedProfile> Profiles,
        IReadOnlyList<ResolvedProfile> HiddenProfiles,
        FrozenDictionary<string, ResolvedProfile> ById,
        string? DefaultProfileId,
        IReadOnlyList<string> Warnings);

    private static readonly Snapshot EmptySnapshot = new(
        Array.Empty<ResolvedProfile>(),
        Array.Empty<ResolvedProfile>(),
        FrozenDictionary<string, ResolvedProfile>.Empty,
        DefaultProfileId: null,
        Warnings: Array.Empty<string>());

    private readonly IProfileConfigSource _source;
    private readonly Func<bool, CancellationToken, Task<IReadOnlyList<DiscoveredProfile>>> _discover;
    private readonly Action<Action> _dispatcher;
    private readonly Func<string?>? _readKnownHosts;
    private readonly ILogger<ProfileRegistry> _log;
    private readonly Lock _sync = new();

    private readonly CancellationTokenSource _discoveryCts = new();
    private int _disposed;

    private volatile Snapshot _snapshot = EmptySnapshot;
    private long _version;

    private IReadOnlyList<DiscoveredProfile> _discovered = Array.Empty<DiscoveredProfile>();

    public event Action<IProfileRegistry>? ProfilesChanged;

    public IReadOnlyList<ResolvedProfile> Profiles => _snapshot.Profiles;
    public IReadOnlyList<ResolvedProfile> HiddenProfiles => _snapshot.HiddenProfiles;
    public string? DefaultProfileId => _snapshot.DefaultProfileId;
    public IReadOnlyList<string> ProfileWarnings => _snapshot.Warnings;
    public long Version => Interlocked.Read(ref _version);

    public ProfileRegistry(
        IProfileConfigSource source,
        Func<bool, CancellationToken, Task<IReadOnlyList<DiscoveredProfile>>> discover,
        Action<Action> dispatcher,
        ILogger<ProfileRegistry>? log = null,
        Func<string?>? readKnownHosts = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(discover);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _source = source;
        _discover = discover;
        _dispatcher = dispatcher;
        _readKnownHosts = readKnownHosts;
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

    private IReadOnlyList<DiscoveredProfile> ReadSshHosts()
    {
        if (_readKnownHosts is null || !_source.SshHostsDiscovery)
            return Array.Empty<DiscoveredProfile>();
        try
        {
            return SshKnownHosts.Parse(_readKnownHosts(), _source.SshHostsUser);
        }
        catch (Exception ex)
        {
            // An unreadable known_hosts costs the ssh entries, never the
            // rest of the profile list.
            LogDiscoveryRefreshFailed(ex);
            return Array.Empty<DiscoveredProfile>();
        }
    }

    private void RecomposeAndFire()
    {
        // Disposed-guard: a probe that ignores its cancellation token
        // can still return normally after Dispose cancels _discoveryCts.
        // If that happens, the continuation reaches here -- skip the
        // snapshot publish and event dispatch so subscribers don't
        // see updates against a torn-down registry.
        if (Volatile.Read(ref _disposed) != 0) return;

        IReadOnlyList<ResolvedProfile> next;
        IReadOnlyList<ResolvedProfile> nextHidden;
        FrozenDictionary<string, ResolvedProfile> nextById;
        string? nextDefault;
        IReadOnlyList<string> nextWarnings;

        // ssh hosts are rebuilt on every recompose (config reload) rather
        // than going through the 24h discovery cache, because they depend
        // on config: the toggle and ssh-hosts-user. Read outside the lock.
        var sshHosts = ReadSshHosts();

        lock (_sync)
        {
            var resolvedSet = ProfileOrderResolver.Resolve(
                user: [.. _source.ParsedProfiles.Values],
                overrides: _source.ProfileOverrides,
                discovered: sshHosts.Count == 0 ? _discovered : [.. _discovered, .. sshHosts],
                profileOrder: _source.ProfileOrder,
                defaultProfileId: _source.DefaultProfileId,
                hiddenIds: _source.HiddenProfileIds);

            next = resolvedSet.Visible;
            nextHidden = resolvedSet.Hidden;
            var dict = new Dictionary<string, ResolvedProfile>(resolvedSet.Visible.Count, StringComparer.OrdinalIgnoreCase);
            nextDefault = null;
            foreach (var p in resolvedSet.Visible)
            {
                dict[p.Id] = p;
                if (p.IsDefault) nextDefault = p.Id;
            }
            nextById = dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

            // The parser warns about every partial block it cannot make a
            // definition out of, and it cannot see discovery. This is the
            // pass that can: an id a partial block merged onto a discovered
            // profile for is an intentional override, and telling the user
            // their profile "was dropped" about one of their own edits is
            // the warning being wrong. The config service's hidden-only
            // pass has already filtered its own markers by the time we get
            // here.
            nextWarnings = Config.ConfigServiceProfileParser.SuppressExplainedWarnings(
                _source.ProfileWarnings, _source.ParsedProfiles, resolvedSet.MergedOverrideIds);
        }

        _snapshot = new Snapshot(next, nextHidden, nextById, nextDefault, nextWarnings);
        var newVersion = Interlocked.Increment(ref _version);
        LogRecomposed(newVersion, next.Count);

        // Contained per subscriber: this runs as a dispatcher callback, and
        // an exception escaping one of those cannot be marshalled back to
        // the enqueuer, so WinRT stows it and fail-fasts the process.
        _dispatcher(() => Config.ConfigChangeFanOut.InvokeAll(
            ProfilesChanged, this, LogChangedHandlerFailed));
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

    [LoggerMessage(EventId = LogEvents.Profiles.ChangedHandlerFailed,
                   Level = LogLevel.Error,
                   Message = "a profiles-changed subscriber threw; its view of the profiles is now stale")]
    private partial void LogChangedHandlerFailed(Exception ex);
}
