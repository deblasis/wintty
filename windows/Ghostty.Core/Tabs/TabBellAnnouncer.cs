using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Ghostty.Core.Tabs;

/// <summary>
/// Turns a tab that starts ringing into one announcement, so a listener
/// hears which tab wants attention without having to go looking for it.
///
/// Window-level rather than per-strip. Both strips are alive at once (the
/// layout switch cross-fades between them) and both watch the same
/// TabModel, so an announcement raised from a strip would be raised
/// twice, once by the strip on screen and once by the one waiting behind
/// it. One announcer per window is one announcement per ring.
///
/// Rising edge only. BellRinging guards its setter on equality, so a
/// change notification for it already IS the edge: a second bell while
/// the tab is still ringing raises no notification and says nothing, and
/// the acknowledge that clears the flag is the falling edge and is
/// likewise silent.
///
/// Pure logic. The sink is a delegate so the UIA raise stays on the WinUI
/// side and this stays testable, in the shape
/// <see cref="Ghostty.Core.Taskbar.TaskbarAttentionCoordinator"/> uses.
/// It gets the tab as well as the words: the raise has to come from an
/// element that has an automation peer, and the tab's own item is one.
/// </summary>
internal sealed class TabBellAnnouncer : IDisposable
{
    private readonly TabManager _manager;
    private readonly Action<TabModel, string> _announce;
    private readonly Dictionary<TabModel, PropertyChangedEventHandler> _hooks = new();

    public TabBellAnnouncer(TabManager manager, Action<TabModel, string> announce)
    {
        _manager = manager;
        _announce = announce;
        foreach (var tab in manager.Tabs) Watch(tab);
        manager.TabAdded += OnTabAdded;
        manager.TabRemoved += OnTabRemoved;
    }

    private void OnTabAdded(object? sender, TabModel tab) => Watch(tab);

    private void OnTabRemoved(object? sender, TabModel tab) => Unwatch(tab);

    private void Watch(TabModel tab)
    {
        if (_hooks.ContainsKey(tab)) return;
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName != nameof(TabModel.BellRinging)) return;
            if (!tab.BellRinging) return;
            _announce(tab, TabAccessibleText.BellAnnouncement(tab));
        };
        tab.PropertyChanged += handler;
        _hooks[tab] = handler;
    }

    private void Unwatch(TabModel tab)
    {
        if (!_hooks.Remove(tab, out var handler)) return;
        tab.PropertyChanged -= handler;
    }

    public void Dispose()
    {
        _manager.TabAdded -= OnTabAdded;
        _manager.TabRemoved -= OnTabRemoved;
        foreach (var (tab, handler) in _hooks) tab.PropertyChanged -= handler;
        _hooks.Clear();
    }
}
