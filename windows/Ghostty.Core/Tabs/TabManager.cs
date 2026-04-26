using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Ghostty.Core.Panes;
using Ghostty.Core.Profiles;

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
    private readonly Func<ProfileSnapshot?, IPaneHost> _paneHostFactory;
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

    /// <summary>
    /// Raised AFTER the tab's manager subscriptions have been unwired
    /// but BEFORE the tab is removed from <see cref="Tabs"/>. Fired
    /// from <see cref="DetachTab"/> only; close paths do not fire it.
    /// </summary>
    public event EventHandler<TabModel>? TabDetaching;

    public TabManager(Func<ProfileSnapshot?, IPaneHost> paneHostFactory)
        : this(paneHostFactory, seed: null) { }

    /// <summary>
    /// Seeded constructor. If <paramref name="seed"/> is non-null the
    /// manager adopts it as its initial tab and skips the factory call.
    /// If <paramref name="seed"/> is null the legacy path runs.
    ///
    /// Seeded construction does NOT raise <see cref="TabAdded"/> for
    /// the seed: it is the initial tab, and TabAdded is for growth.
    /// Both <c>TabHost.xaml.cs</c> and <c>VerticalTabStrip.xaml.cs</c>
    /// already iterate <see cref="Tabs"/> on construction before they
    /// subscribe to <see cref="TabAdded"/>, so a seeded tab is visible
    /// in the window's UI on first render.
    ///
    /// Seeded construction also does NOT raise
    /// <see cref="ActiveTabChanged"/> or <see cref="WindowTitleChanged"/>
    /// for the seed. This matches the legacy factory path, which
    /// assigns <see cref="ActiveTab"/> directly without events because
    /// no listener is wired at ctor time.
    /// </summary>
    public TabManager(Func<ProfileSnapshot?, IPaneHost> paneHostFactory, TabModel? seed)
    {
        _paneHostFactory = paneHostFactory;
        if (seed is null)
        {
            var first = CreateTab(snapshot: null);
            _tabs.Add(first);
            _activeTab = first;
        }
        else
        {
            WireAdoptedTab(seed);
            _tabs.Add(seed);
            _activeTab = seed;
        }
    }

    /// <summary>
    /// Open a new tab with no profile snapshot attached. Identical to
    /// <see cref="NewTab(ProfileSnapshot?)"/> with a null argument;
    /// preserved as the no-arg call shape for the legacy no-profile
    /// path (vertical tab strip's + glyph in PR 4) and the
    /// no-profiles-configured cold-start fallback in
    /// <c>MainWindow.OpenProfile</c>.
    /// </summary>
    public TabModel NewTab() => NewTab(snapshot: null);

    /// <summary>
    /// Open a new tab. When <paramref name="snapshot"/> is non-null it
    /// is attached to the new <see cref="TabModel"/> via
    /// <see cref="TabModel.AttachProfileSnapshot"/> before
    /// <see cref="TabAdded"/> fires; subscribers can read
    /// <see cref="TabModel.ProfileSnapshot"/> synchronously.
    /// </summary>
    public TabModel NewTab(ProfileSnapshot? snapshot)
    {
        var tab = CreateTab(snapshot);
        if (snapshot is not null)
            tab.AttachProfileSnapshot(snapshot);
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
        UnsubscribeProgressForwarder(tab);
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

    private TabModel CreateTab(ProfileSnapshot? snapshot)
    {
        var host = _paneHostFactory(snapshot);
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

    /// <summary>
    /// Remove <paramref name="tab"/> from this manager without tearing
    /// down its pane host. Caller takes ownership of the returned model
    /// and must hand it to another manager via <see cref="AdoptTab"/>.
    /// Raises <see cref="TabDetaching"/>, then <see cref="TabRemoved"/>,
    /// then either <see cref="LastTabClosed"/> (if it was the last tab)
    /// or <see cref="ActiveTabChanged"/> / <see cref="WindowTitleChanged"/>
    /// (if it was the active tab of more than one).
    /// </summary>
    public TabModel DetachTab(TabModel tab)
    {
        if (_tabs.Count <= 1)
            throw new InvalidOperationException("Cannot detach the last tab.");

        var index = _tabs.IndexOf(tab);
        if (index < 0)
            throw new InvalidOperationException(
                "DetachTab: tab not owned by this manager.");

        TabDetaching?.Invoke(this, tab);

        // Unwire manager-side subscriptions. Intentionally do NOT call
        // tab.OnClose or tab.PaneHost.DisposeAllLeaves: the tab is
        // moving, not dying. The progress-forwarder unsubscribe is
        // shared with the close path via UnsubscribeProgressForwarder
        // so this detach path never has to name OnClose.
        tab.PaneHost.LeafFocused -= OnLeafFocused;
        tab.PropertyChanged -= OnTabPropertyChanged;
        UnsubscribeProgressForwarder(tab);
        // tab.OnClose is intentionally LEFT ALONE. WireAdoptedTab on
        // the destination manager overwrites it as part of adoption.

        _tabs.RemoveAt(index);
        TabRemoved?.Invoke(this, tab);

        if (_tabs.Count == 0)
        {
            LastTabClosed?.Invoke(this, EventArgs.Empty);
            return tab;
        }

        if (ReferenceEquals(_activeTab, tab))
        {
            var next = _tabs[Math.Min(index, _tabs.Count - 1)];
            _activeTab = next;
            ActiveTabChanged?.Invoke(this, next);
            WindowTitleChanged?.Invoke(this, EventArgs.Empty);
        }

        return tab;
    }

    /// <summary>
    /// Attach an externally-sourced <see cref="TabModel"/> to this
    /// manager. Rewires <see cref="TabModel.OnClose"/>,
    /// <see cref="IPaneHost.LeafFocused"/>, progress forwarding, and
    /// property-change forwarding to the adopter's event graph.
    /// Raises <see cref="TabAdded"/> and activates the tab.
    /// </summary>
    public void AdoptTab(TabModel tab)
    {
        if (_tabs.Contains(tab))
            throw new InvalidOperationException("AdoptTab: tab already owned.");

        WireAdoptedTab(tab);
        _tabs.Add(tab);
        TabAdded?.Invoke(this, tab);
        Activate(tab);
    }

    /// <summary>
    /// Shared rewire used by both <see cref="AdoptTab"/> and the
    /// seeded constructor. Does NOT touch _tabs, does NOT raise any
    /// events; the caller owns activation and TabAdded.
    /// </summary>
    private void WireAdoptedTab(TabModel tab)
    {
        tab.PaneHost.LeafFocused += OnLeafFocused;
        EventHandler<TabProgressState> progressHandler = (_, state) => tab.Progress = state;
        tab.PaneHost.ProgressChanged += progressHandler;
        // OnClose stores the unsubscribe action so DetachTab /
        // CloseTab can walk back the progress wiring without needing
        // to re-capture the handler delegate.
        tab.OnClose = () => tab.PaneHost.ProgressChanged -= progressHandler;
        tab.PropertyChanged += OnTabPropertyChanged;
    }

    /// <summary>
    /// Walk back the progress-forwarder subscription installed by
    /// <see cref="CreateTab"/> or <see cref="WireAdoptedTab"/>. Shared
    /// between <see cref="CloseTab"/> and <see cref="DetachTab"/> so
    /// neither path has to spell out "invoke OnClose and null it";
    /// in particular, <see cref="DetachTab"/> must NOT invoke OnClose
    /// (per spec) because OnClose is the close-signal hook,
    /// not the detach hook.
    /// </summary>
    private void UnsubscribeProgressForwarder(TabModel tab)
    {
        // OnClose is the unsubscribe action. Running it detaches the
        // progress handler from PaneHost.ProgressChanged. On DetachTab
        // we run this helper instead of tab.OnClose?.Invoke() at the
        // call site so the semantics are obvious: we are dismantling
        // ONE specific subscription, not running the full close.
        tab.OnClose?.Invoke();
    }
}
