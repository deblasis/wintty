using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Notifications;

namespace Ghostty.Core.Taskbar;

/// <summary>
/// Owns the taskbar overlay slot. A badge is never shown without a notice
/// the user can read and dismiss; dismissing clears it; a producer's
/// <see cref="Clear"/> ends the overlay but leaves the notice for review.
/// A dismissal suppresses only a still-ongoing situation (the same
/// signature raised again with no intervening <see cref="Clear"/>); once
/// the producer calls <see cref="Clear"/> the acknowledgement is forgotten,
/// so a genuine later recurrence badges again instead of staying silenced
/// for the rest of the process.
/// Pure logic: no WinUI, no threads. Callers hold the UI thread.
/// </summary>
public sealed class TaskbarBadgeArbiter
{
    private readonly ITaskbarBadgeSink _sink;
    private readonly INotificationService _notices;
    private readonly Dictionary<string, (TaskbarBadge Badge, Notice Notice)> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _acknowledged = new(StringComparer.Ordinal);
    private readonly HashSet<string> _overlay = new(StringComparer.Ordinal);
    private TaskbarBadgeKind? _shown;
    private bool _superseding;

    public TaskbarBadgeArbiter(ITaskbarBadgeSink sink, INotificationService notices)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(notices);
        _sink = sink;
        _notices = notices;
    }

    /// <summary>A snapshot of the keys currently owning an overlay slot
    /// contender. Not a live view: safe to enumerate even if a reentrant
    /// Raise/Clear mutates the arbiter afterward.</summary>
    public IReadOnlyCollection<string> ActiveKeys => _overlay.ToArray();

    public void Raise(TaskbarBadge badge)
    {
        ArgumentNullException.ThrowIfNull(badge);
        if (_acknowledged.TryGetValue(badge.Key, out var seen) &&
            string.Equals(seen, badge.Signature, StringComparison.Ordinal))
        {
            return;
        }
        if (_active.TryGetValue(badge.Key, out var current))
        {
            if (string.Equals(current.Badge.Signature, badge.Signature, StringComparison.Ordinal))
            {
                _overlay.Add(badge.Key);
                Recompute();
                return;
            }
            // Superseded: retire the old notice without treating that as a
            // user dismissal of the new situation. Suppress the retiring
            // notice's own OnDismissed recompute here: it would otherwise
            // run mid-flight, with the new badge not yet in place, and
            // transiently clear or hand the slot to a different active key
            // before Raise puts the superseding badge back a few lines
            // down. One Recompute below is enough.
            _active.Remove(badge.Key);
            _superseding = true;
            try
            {
                _notices.Dismiss(current.Notice);
            }
            finally
            {
                _superseding = false;
            }
        }
        var notice = new Notice
        {
            Title = badge.Title,
            Message = badge.Message,
            Severity = badge.Severity,
            IsClosable = true,
            Actions = badge.Actions,
            DedupKey = "badge:" + badge.Key,
            OnDismiss = () => OnDismissed(badge.Key, badge.Signature),
        };
        _active[badge.Key] = (badge, notice);
        _overlay.Add(badge.Key);
        _notices.Show(notice);
        Recompute();
    }

    /// <summary>The trigger ended (focus regained, state moved on). The overlay
    /// drops this key; the notice stays until the user dismisses it. Also
    /// forgets any acknowledged signature for this key: an acknowledgement
    /// only suppresses a situation the producer still considers ongoing, so
    /// once the producer reports it over, a later recurrence (even with the
    /// exact same signature) is a new situation and must badge again.</summary>
    public void Clear(string key)
    {
        _acknowledged.Remove(key);
        if (_overlay.Remove(key)) Recompute();
    }

    private void OnDismissed(string key, string signature)
    {
        if (_active.TryGetValue(key, out var current) &&
            string.Equals(current.Badge.Signature, signature, StringComparison.Ordinal))
        {
            _active.Remove(key);
            _acknowledged[key] = signature;
        }
        if (_superseding) return;
        if (_overlay.Remove(key)) Recompute();
    }

    private void Recompute()
    {
        TaskbarBadgeKind? top = null;
        foreach (var key in _overlay)
        {
            if (!_active.TryGetValue(key, out var entry)) continue;
            if (top is null || entry.Badge.Kind < top) top = entry.Badge.Kind;
        }
        if (top == _shown) return;
        _shown = top;
        if (top is { } kind) _sink.Show(kind, _active.Values.First(e => e.Badge.Kind == kind).Badge.Title);
        else _sink.Clear();
    }
}
