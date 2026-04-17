using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.Logging;

/// <summary>
/// Holds the current <see cref="LoggerFilterOptions"/> behind a
/// <c>volatile</c> field so <see cref="LoggingBootstrap.ApplyFilters"/>
/// can atomically swap in a rebuilt rules set on config reload without
/// tearing down the factory or providers.
///
/// Also owns a per-category resolved-threshold cache. MEL invokes the
/// filter delegate on every log call per (provider, category) pair, so
/// repeating the StartsWith scan over the rules list at that frequency
/// is wasteful once the answer is stable. <see cref="Replace"/> swaps
/// in a fresh cache instance so the old decisions cannot leak into the
/// post-reload rule set.
/// </summary>
internal sealed class FilterState
{
    // Reference writes are atomic in .NET. `volatile` additionally
    // prevents the filter delegate from observing a stale reference.
    private volatile LoggerFilterOptions _options;
    private volatile ConcurrentDictionary<string, LogLevel> _cache = new();

    public FilterState(LoggerFilterOptions initial) => _options = initial;

    public LoggerFilterOptions Options => _options;

    /// <summary>
    /// Per-category resolved threshold cache. Read by the filter
    /// delegate; invalidated by <see cref="Replace"/>.
    /// </summary>
    public ConcurrentDictionary<string, LogLevel> Cache => _cache;

    public void Replace(LoggerFilterOptions next)
    {
        _options = next;
        // Swap to a fresh cache instance. A racing log-call on the old
        // reference at worst populates a dictionary that is already
        // unreferenced; the next lookup will hit the new (empty) cache
        // and recompute against the updated rules.
        _cache = new ConcurrentDictionary<string, LogLevel>();
    }
}
