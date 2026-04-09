using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Ghostty.Core.Panes;

namespace Ghostty.Core.Tabs;

/// <summary>
/// Owns the list of <see cref="TabModel"/>s for one window. All
/// operations on tabs (create, close, activate, move, navigate) go
/// through here. Pure C#, no WinUI references — lives in
/// Ghostty.Core so the test project consumes it directly.
///
/// PaneHost construction is injected via a factory delegate so the
/// test project can supply <c>FakePaneHost</c>. The real call site
/// (<c>MainWindow</c>) wires this to <c>PaneHostFactory</c> in the
/// WinUI project.
///
/// Title routing: <see cref="TabManager"/> raises
/// <see cref="WindowTitleChanged"/> on:
///   - active tab change
///   - the active tab's <see cref="TabModel.ShellReportedTitle"/> or
///     <see cref="TabModel.UserOverrideTitle"/> changes
///   - the active tab's <see cref="IPaneHost.LeafFocused"/> fires
/// The actual leaf-title-changed hook lives in MainWindow because
/// the leaf's <c>Terminal</c> is WinUI-only.
/// </summary>
internal sealed class TabManager
{
    private readonly Func<IPaneHost> _paneHostFactory;
    private readonly ObservableCollection<TabModel> _tabs = new();
    private TabModel _activeTab = null!;

    // Exposed as the concrete ObservableCollection so WinUI can bind
    // ItemsSource directly and pick up INotifyCollectionChanged for
    // free. Tests only depend on IReadOnlyList surface (Count, indexer),
    // which ObservableCollection satisfies.
    public ObservableCollection<TabModel> Tabs => _tabs;
    public TabModel ActiveTab => _activeTab;

    /// <summary>
    /// Index of <paramref name="tab"/> in <see cref="Tabs"/>, or -1
    /// if not present. Provided here because <see cref="IReadOnlyList{T}"/>
    /// has no IndexOf and the underlying ObservableCollection's
    /// IndexOf is not exposed through the read-only surface.
    /// </summary>
    public int IndexOf(TabModel tab) => _tabs.IndexOf(tab);

    public event EventHandler<TabModel>? TabAdded;
    public event EventHandler<TabModel>? TabRemoved;
    public event EventHandler<(TabModel tab, int from, int to)>? TabMoved;
    public event EventHandler<TabModel>? ActiveTabChanged;
    public event EventHandler? LastTabClosed;
    public event EventHandler? WindowTitleChanged;

    public TabManager(Func<IPaneHost> paneHostFactory)
    {
        _paneHostFactory = paneHostFactory;
        var first = CreateTab();
        _tabs.Add(first);
        _activeTab = first;
    }

    public TabModel NewTab()
    {
        var tab = CreateTab();
        _tabs.Add(tab);
        TabAdded?.Invoke(this, tab);
        Activate(tab);
        return tab;
    }

    /// <summary>
    /// Progressive close on the active tab: closes a pane if there
    /// is more than one, otherwise closes the tab. The multi-pane
    /// confirmation prompt is the caller's responsibility (it needs
    /// a XamlRoot which this assembly cannot reach).
    /// </summary>
    public void RequestCloseActive()
    {
        var tab = _activeTab;
        if (tab.PaneHost.PaneCount > 1)
        {
            tab.PaneHost.CloseActive();
            return;
        }
        CloseTab(tab);
    }

    public void CloseTab(TabModel tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;

        tab.PaneHost.LeafFocused -= OnLeafFocused;
        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.OnClose?.Invoke();
        tab.OnClose = null;
        tab.PaneHost.DisposeAllLeaves();

        _tabs.RemoveAt(index);
        TabRemoved?.Invoke(this, tab);

        if (_tabs.Count == 0)
        {
            LastTabClosed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (ReferenceEquals(_activeTab, tab))
        {
            var next = _tabs[Math.Min(index, _tabs.Count - 1)];
            _activeTab = next;
            ActiveTabChanged?.Invoke(this, next);
            WindowTitleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Activate(TabModel tab)
    {
        if (ReferenceEquals(tab, _activeTab)) return;
        if (!_tabs.Contains(tab)) return;
        _activeTab = tab;
        ActiveTabChanged?.Invoke(this, tab);
        WindowTitleChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ActivateIndex(int i)
    {
        if (i < 0 || i >= _tabs.Count) return;
        Activate(_tabs[i]);
    }

    public void Next()
    {
        if (_tabs.Count <= 1) return;
        var i = _tabs.IndexOf(_activeTab);
        Activate(_tabs[(i + 1) % _tabs.Count]);
    }

    public void Prev()
    {
        if (_tabs.Count <= 1) return;
        var i = _tabs.IndexOf(_activeTab);
        Activate(_tabs[(i - 1 + _tabs.Count) % _tabs.Count]);
    }

    public void JumpTo(int i) => ActivateIndex(i);

    public void JumpToLast()
    {
        if (_tabs.Count <= 1) return;
        Activate(_tabs[^1]);
    }

    public void Move(int from, int to)
    {
        if (from < 0 || from >= _tabs.Count) return;
        if (to < 0 || to >= _tabs.Count) return;
        if (from == to) return;
        var tab = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(to, tab);
        TabMoved?.Invoke(this, (tab, from, to));
    }

    private TabModel CreateTab()
    {
        var host = _paneHostFactory();
        var tab = new TabModel(host);
        host.LeafFocused += OnLeafFocused;
        // Forward the active-leaf's progress onto the tab model. The
        // handler is captured as a local so CloseTab can unsubscribe
        // without needing a shared dictionary.
        EventHandler<TabProgressState> progressHandler = (_, state) => tab.Progress = state;
        host.ProgressChanged += progressHandler;
        tab.OnClose = () => host.ProgressChanged -= progressHandler;
        tab.PropertyChanged += OnTabPropertyChanged;
        return tab;
    }

    private void OnLeafFocused(object? sender, LeafPane leaf)
    {
        if (sender is IPaneHost host)
        {
            foreach (var t in _tabs)
            {
                if (ReferenceEquals(t.PaneHost, host) && ReferenceEquals(t, _activeTab))
                {
                    WindowTitleChanged?.Invoke(this, EventArgs.Empty);
                    break;
                }
            }
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TabModel t && ReferenceEquals(t, _activeTab) &&
            (e.PropertyName == nameof(TabModel.ShellReportedTitle) ||
             e.PropertyName == nameof(TabModel.UserOverrideTitle)))
        {
            WindowTitleChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
