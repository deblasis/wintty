using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Composes registered IInstalledShellProbe implementations, runs them
/// in parallel, and merges results in probe-registration order. A probe
/// that throws is logged and skipped; other probes' results are still
/// returned.
///
/// When constructed with the full (caching) constructor, results are
/// persisted to <c>cacheFilePath</c> and reused on subsequent calls if:
///   1. The cache file exists and deserializes cleanly, AND
///   2. Its <c>WinttyVersion</c> matches the current build (version bump
///      invalidates so users see new probe behavior on upgrade), AND
///   3. Its <c>CreatedAt</c> is within the 24h TTL window.
/// Any cache miss falls back to running probes and rewriting the file.
/// </summary>
internal sealed partial class DiscoveryService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly IReadOnlyList<IInstalledShellProbe> _probes;
    private readonly IFileSystem? _fs;
    private readonly IClock? _clock;
    private readonly string _winttyVersion;
    private readonly string? _cacheFilePath;
    private readonly ILogger<DiscoveryService> _log;

    /// <summary>No-cache constructor used by Task 10 unit tests.</summary>
    public DiscoveryService(
        IEnumerable<IInstalledShellProbe> probes,
        ILogger<DiscoveryService>? log = null)
    {
        _probes = probes.ToList();
        _winttyVersion = "";
        _log = log ?? NullLogger<DiscoveryService>.Instance;
    }

    /// <summary>Full constructor with caching.</summary>
    public DiscoveryService(
        IEnumerable<IInstalledShellProbe> probes,
        IFileSystem fs,
        IClock clock,
        string winttyVersion,
        string cacheFilePath,
        ILogger<DiscoveryService>? log = null)
        : this(probes, log)
    {
        _fs = fs;
        _clock = clock;
        _winttyVersion = winttyVersion;
        _cacheFilePath = cacheFilePath;
    }

    public async Task<IReadOnlyList<DiscoveredProfile>> DiscoverAsync(CancellationToken ct)
    {
        var cached = await TryLoadFreshCacheAsync(ct).ConfigureAwait(false);
        if (cached is not null)
            return cached;

        var merged = await RunProbesAsync(ct).ConfigureAwait(false);
        await TryWriteCacheAsync(merged, ct).ConfigureAwait(false);
        return merged;
    }

    private async Task<IReadOnlyList<DiscoveredProfile>?> TryLoadFreshCacheAsync(CancellationToken ct)
    {
        if (_fs is null || _clock is null || _cacheFilePath is null) return null;
        if (!_fs.FileExists(_cacheFilePath)) return null;

        byte[] bytes;
        try
        {
            bytes = await _fs.ReadAllBytesAsync(_cacheFilePath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCacheReadFailed(ex);
            return null;
        }

        var file = DiscoveryCache.Deserialize(bytes);
        if (file is null) return null;
        if (!string.Equals(file.WinttyVersion, _winttyVersion, StringComparison.Ordinal)) return null;

        var age = _clock.UtcNow - file.CreatedAt;
        if (age < TimeSpan.Zero || age > CacheTtl) return null;

        return file.Profiles.Select(ToDiscoveredProfile).ToList();
    }

    private async Task<IReadOnlyList<DiscoveredProfile>> RunProbesAsync(CancellationToken ct)
    {
        var tasks = _probes
            .Select(async probe =>
            {
                try
                {
                    return (probe.ProbeId, await probe.DiscoverAsync(ct).ConfigureAwait(false));
                }
                // Cancellation is not a probe "failure"; surface it to the caller
                // instead of being swallowed by the generic handler below.
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogProbeFailed(ex, probe.ProbeId);
                    return (probe.ProbeId, (IReadOnlyList<DiscoveredProfile>)Array.Empty<DiscoveredProfile>());
                }
            })
            .ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var merged = new List<DiscoveredProfile>();
        foreach (var (_, items) in results)
            merged.AddRange(items);
        return merged;
    }

    private async Task TryWriteCacheAsync(IReadOnlyList<DiscoveredProfile> profiles, CancellationToken ct)
    {
        if (_fs is null || _clock is null || _cacheFilePath is null) return;
        try
        {
            var file = new DiscoveryCacheFile(
                SchemaVersion: DiscoveryCache.CurrentSchemaVersion,
                WinttyVersion: _winttyVersion,
                CreatedAt: _clock.UtcNow,
                Profiles: profiles.Select(ToCacheEntry).ToList());
            await _fs.WriteAllBytesAsync(_cacheFilePath, DiscoveryCache.Serialize(file), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCacheWriteFailed(ex);
        }
    }

    private static DiscoveryCacheEntry ToCacheEntry(DiscoveredProfile p) => new(
        p.Id, p.Name, p.Command, p.ProbeId, p.WorkingDirectory,
        IconToken: SerializeIcon(p.Icon), p.TabTitle);

    private static DiscoveredProfile ToDiscoveredProfile(DiscoveryCacheEntry e) => new(
        e.Id, e.Name, e.Command, e.ProbeId,
        WorkingDirectory: e.WorkingDirectory,
        Icon: DeserializeIcon(e.IconToken),
        TabTitle: e.TabTitle);

    // Icon token format is lossy-but-roundtrippable for discovery-side
    // variants: BundledKey, AutoForExe, AutoForWslDistro. Path and
    // Mdl2Token are never produced by probes, so absence is fine.
    private static string? SerializeIcon(IconSpec? icon) => icon switch
    {
        IconSpec.BundledKey b => "bundled:" + b.Key,
        IconSpec.AutoForExe a => "exe:" + a.ExePath,
        IconSpec.AutoForWslDistro w => "wsl-distro:" + w.DistroName,
        IconSpec.Path p => "path:" + p.FilePath,
        IconSpec.Mdl2Token m => "mdl2:" + m.CodePoint.ToString("x", System.Globalization.CultureInfo.InvariantCulture),
        null => null,
        _ => null,
    };

    private static IconSpec? DeserializeIcon(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var colon = token.IndexOf(':');
        if (colon <= 0) return null;
        var scheme = token[..colon];
        var value = token[(colon + 1)..];
        return scheme switch
        {
            "bundled" => new IconSpec.BundledKey(value),
            "exe" => new IconSpec.AutoForExe(value),
            "wsl-distro" => new IconSpec.AutoForWslDistro(value),
            "path" => new IconSpec.Path(value),
            "mdl2" => int.TryParse(value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var cp)
                ? new IconSpec.Mdl2Token(cp) : null,
            _ => null,
        };
    }

    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Profiles.ProbeFailed,
                   Level = LogLevel.Warning,
                   Message = "probe '{ProbeId}' failed")]
    private partial void LogProbeFailed(System.Exception ex, string probeId);

    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Profiles.CacheReadFailed,
                   Level = LogLevel.Debug,
                   Message = "cache read failed")]
    private partial void LogCacheReadFailed(System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Profiles.CacheWriteFailed,
                   Level = LogLevel.Warning,
                   Message = "cache write failed")]
    private partial void LogCacheWriteFailed(System.Exception ex);
}
