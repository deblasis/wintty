using System;
using System.Collections.Generic;
using Ghostty.Core.Tabs;

namespace Ghostty.Core.Panes;

/// <summary>
/// The pane host's title plumbing, kept apart from WinUI so it can be driven
/// with fakes. One pane host owns one forwarder.
///
/// Every terminal in the host is tracked for its whole life, not only the
/// focused one: subscribing on focus is what left background panes, and so
/// background tabs, with nobody listening (wintty#1129). A title from any
/// terminal other than the active one is dropped, so a background split, or
/// a pane soft-closed and kept alive for undo, cannot name the tab. A focus
/// change emits the newly active terminal's title, null included: a pane
/// that has not reported a title yet leaves the tab to its fallback, the way
/// Windows Terminal shows the profile name, and never keeps the title of a
/// pane that is no longer focused.
/// </summary>
/// <typeparam name="TTerminal">The terminal type (TerminalControl in the
/// shell). Compared by reference.</typeparam>
internal sealed class PaneTitleForwarder<TTerminal> where TTerminal : class
{
    private readonly Func<TTerminal> _activeTerminal;
    private readonly Func<TTerminal, string?> _currentTitle;
    private readonly Action<TTerminal, EventHandler<string>> _subscribe;
    private readonly Action<TTerminal, EventHandler<string>> _unsubscribe;
    private readonly Action<string?> _emit;
    private readonly EventHandler<string> _onTitleChanged;
    private readonly HashSet<TTerminal> _tracked = new(ReferenceEqualityComparer.Instance);
    private bool _stopped;

    /// <param name="activeTerminal">The host's active terminal, read at the
    /// moment a title arrives or focus moves.</param>
    /// <param name="currentTitle">A terminal's effective title (its per-pane
    /// override, else what its shell last set), or null for none yet.</param>
    /// <param name="subscribe">Attach a handler to a terminal's title event.</param>
    /// <param name="unsubscribe">Detach it.</param>
    /// <param name="emit">Where the host's title goes: the pane host raises
    /// its own <c>TitleChanged</c> from here.</param>
    public PaneTitleForwarder(
        Func<TTerminal> activeTerminal,
        Func<TTerminal, string?> currentTitle,
        Action<TTerminal, EventHandler<string>> subscribe,
        Action<TTerminal, EventHandler<string>> unsubscribe,
        Action<string?> emit)
    {
        _activeTerminal = activeTerminal;
        _currentTitle = currentTitle;
        _subscribe = subscribe;
        _unsubscribe = unsubscribe;
        _emit = emit;
        _onTitleChanged = OnTitleChanged;
    }

    /// <summary>How many terminals are subscribed right now.</summary>
    public int TrackedCount => _tracked.Count;

    /// <summary>Start listening to a terminal. Idempotent, so a host that
    /// re-wires a reused terminal does not double-subscribe.</summary>
    public void Track(TTerminal terminal)
    {
        if (_stopped || !_tracked.Add(terminal)) return;
        _subscribe(terminal, _onTitleChanged);
    }

    /// <summary>Stop listening to a terminal that is leaving the host.</summary>
    public void Untrack(TTerminal terminal)
    {
        if (!_tracked.Remove(terminal)) return;
        _unsubscribe(terminal, _onTitleChanged);
    }

    /// <summary>The active terminal changed: hand the tab its title, or null
    /// when it has none yet.</summary>
    public void ActiveChanged()
    {
        if (_stopped) return;
        _emit(_currentTitle(_activeTerminal()));
    }

    /// <summary>The host is being torn down: unsubscribe everything and emit
    /// nothing from now on.</summary>
    public void Stop()
    {
        _stopped = true;
        foreach (var terminal in _tracked) _unsubscribe(terminal, _onTitleChanged);
        _tracked.Clear();
    }

    private void OnTitleChanged(object? sender, string title)
    {
        if (_stopped) return;
        if (!LiveTitleGuard.Accepts(sender, _activeTerminal())) return;
        ActiveChanged();
    }
}
