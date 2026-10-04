using System;
using System.Collections.Generic;

namespace Ghostty.Core.Session;

/// <summary>
/// The window set the running session file holds, kept in memory so a
/// window can be REFRESHED instead of dropped.
///
/// Why this exists: at app shutdown no window is live any more, so a
/// clean-shutdown write that re-reads the file is writing whatever the
/// last debounced persist saw -- up to a debounce window stale, and
/// missing outright on a multi-window quit where the first window closed
/// before the debounce ever fired. Flipping
/// <see cref="SessionState.CleanShutdown"/> on that file is what lost a
/// rename made in the last second before the X. This holds the same
/// windows, captured, so the closing window's own state is what lands.
///
/// The two rules it keeps, both of which the file already wanted:
/// <list type="bullet">
/// <item>A window that closes stays in the set, refreshed rather than
/// removed. A single deliberate close is indistinguishable from the first
/// close of a multi-window quit, so shrinking the set here would lose
/// windows on a slow quit cascade.</item>
/// <item>A window that has not been seen yet is appended. That is the
/// cascade case: the first window of a quit closes before any persist has
/// named it, and dropping it would restore one window instead of
/// three.</item>
/// </list>
///
/// Order is first-capture order, so the set keeps the shape the file
/// already had rather than reshuffling on every refresh.
///
/// <typeparam name="TKey">
/// The window identity this ledger keys on. Not part of the persisted
/// shape: nothing here is serialized, it only decides which captured
/// window replaces which.
/// </typeparam>
internal sealed class SessionWindowLedger<TKey> where TKey : notnull
{
    private readonly List<(TKey Key, WindowSession Session)> _entries = new();

    /// <summary>How many windows the ledger holds.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// The captured windows in capture order. A fresh list: the caller
    /// hands it to a <see cref="SessionState"/> that outlives this call,
    /// and a view over the ledger would move under it.
    /// </summary>
    public List<WindowSession> Sessions
    {
        get
        {
            var windows = new List<WindowSession>(_entries.Count);
            foreach (var (key, session) in _entries)
                windows.Add(session);
            return windows;
        }
    }

    /// <summary>
    /// Capture <paramref name="window"/>'s state: replace what this
    /// ledger holds for that window in place, or append when the window
    /// is new to it.
    /// </summary>
    public void Capture(TKey window, WindowSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        for (int i = 0; i < _entries.Count; i++)
        {
            if (!EqualityComparer<TKey>.Default.Equals(_entries[i].Key, window)) continue;
            _entries[i] = (window, session);
            return;
        }
        _entries.Add((window, session));
    }

    /// <summary>
    /// Replace the whole ledger with the live set. The debounced persist
    /// calls this with the windows it just wrote: from here on, the file
    /// and the ledger are the same answer, and the next close refreshes an
    /// entry rather than resurrecting a window the set has moved past.
    /// </summary>
    public void ReplaceAll(IEnumerable<(TKey Window, WindowSession Session)> live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var replacement = new List<(TKey, WindowSession)>();
        foreach (var (window, session) in live)
        {
            ArgumentNullException.ThrowIfNull(session);
            replacement.Add((window, session));
        }
        _entries.Clear();
        _entries.AddRange(replacement);
    }
}