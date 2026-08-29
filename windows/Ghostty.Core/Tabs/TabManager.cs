using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Ghostty.Core.Panes;
using Ghostty.Core.Profiles;
using Ghostty.Core.Session;

namespace Ghostty.Core.Tabs;

/// <summary>
/// Owns the list of <see cref="TabModel"/>s for one window. All
/// operations on tabs (create, close, activate, move, navigate) go
/// through here.
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
    // Shared (app-level) store of recently-closed tabs for reopen-closed-tab.
    // Null in tests / contexts that do not wire reopen. CloseTab pushes a
    // snapshot here before the panes are disposed.
    private readonly ClosedStack<TabSession>? _closedTabs;
    private readonly ObservableCollection<TabModel> _tabs = new();
    private readonly MruList<TabModel> _mru = new();
    private readonly List<TabGroup> _groups = new();
    // The wrapper is created once and handed out on every read of Groups:
    // free per read, and a caller that casts it back to IList throws
    // instead of mutating the registry behind Normalize's back.
    private readonly ReadOnlyCollection<TabGroup> _groupsView;
    private TabModel _activeTab = null!;

    // Exposed as the concrete ObservableCollection so WinUI can bind
    // ItemsSource directly and pick up INotifyCollectionChanged for
    // free. Tests only depend on IReadOnlyList surface (Count, indexer),
    // which ObservableCollection satisfies.
    public ObservableCollection<TabModel> Tabs => _tabs;
    public TabModel ActiveTab => _activeTab;

    /// <summary>
    /// Tabs in most-recently-active order (index 0 = current active). Drives
    /// the Ctrl+Tab MRU cycle and orders the tab overview.
    /// </summary>
    public IReadOnlyList<TabModel> MruOrder => _mru.Order;

    /// <summary>
    /// Registered groups. Everything listed here has at least one member:
    /// <see cref="Normalize"/> dissolves emptied groups, so there is no
    /// empty-group state to render.
    /// </summary>
    public IReadOnlyList<TabGroup> Groups => _groupsView;

    /// <summary>
    /// Size of the pinned prefix of <see cref="Tabs"/>. Derived on every
    /// read, never stored: the pinned zone is a property of the order,
    /// not a second collection.
    /// </summary>
    public int PinCount
    {
        get
        {
            int count = 0;
            foreach (var t in _tabs)
                if (t.IsPinned) count++;
            return count;
        }
    }

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

    /// <summary>Raised when any owned tab's active leaf rings the bell.
    /// Window-level: the taskbar attention coordinator subscribes here and
    /// gates on the carried <c>bell-features.attention</c>.</summary>
    public event EventHandler<Ghostty.Core.Bell.BellFeatures>? BellRang;

    /// <summary>
    /// Raised AFTER the tab's manager subscriptions have been unwired
    /// but BEFORE the tab is removed from <see cref="Tabs"/>. Fired
    /// from <see cref="DetachTab"/> only; close paths do not fire it.
    /// </summary>
    public event EventHandler<TabModel>? TabDetaching;

    /// <summary>
    /// Non-null <paramref name="seed"/> is adopted as the initial tab
    /// (factory call is skipped); null falls through to the factory.
    /// <paramref name="initialSnapshot"/> is passed to the factory for
    /// that first tab (cold-start default profile / jump-list new
    /// window). Ignored when <paramref name="seed"/> is non-null.
    /// <paramref name="closedTabs"/> is the (app-shared) bounded store that
    /// <see cref="CloseTab"/> pushes a snapshot to before tearing a tab down;
    /// null disables capture (tests / non-reopen contexts).
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
    public TabManager(
        Func<ProfileSnapshot?, IPaneHost> paneHostFactory,
        TabModel? seed = null,
        ClosedStack<TabSession>? closedTabs = null,
        ProfileSnapshot? initialSnapshot = null)
    {
        _paneHostFactory = paneHostFactory;
        _closedTabs = closedTabs;
        _groupsView = new(_groups);
        if (seed is null)
        {
            var first = CreateTab(initialSnapshot);
            if (initialSnapshot is not null)
                first.AttachProfileSnapshot(initialSnapshot);
            _tabs.Add(first);
            _activeTab = first;
            _mru.Touch(first);
        }
        else
        {
            WireAdoptedTab(seed);
            _tabs.Add(seed);
            _activeTab = seed;
            _mru.Touch(seed);
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
        Normalize();
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

        // Capture a reopenable snapshot before the panes are torn down. Always
        // captures (including the window's last tab): closing a tab makes it
        // reopenable via reopen-closed-tab. Whole-window closes do not route
        // through CloseTab, so they are captured separately as window snapshots.
        _closedTabs?.Push(SessionCapture.CaptureTab(
            tab.PaneHost.RootNode,
            tab.PaneHost.ActiveLeaf,
            tab.PaneHost.ZoomedLeaf,
            tab.ProfileId,
            tab.UserOverrideTitle));

        tab.PaneHost.LeafFocused -= OnLeafFocused;
        tab.PropertyChanged -= OnTabPropertyChanged;
        UnsubscribeProgressForwarder(tab);
        tab.OnClose = null;
        tab.PaneHost.DisposeAllLeaves();

        _tabs.RemoveAt(index);
        _mru.Remove(tab);
        // The last member of a group may just have left; Normalize
        // dissolves it (collapse state included) before anyone sees the
        // removal.
        Normalize();
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
            _mru.Touch(next);
            ActiveTabChanged?.Invoke(this, next);
            WindowTitleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Activate(TabModel tab)
    {
        if (ReferenceEquals(tab, _activeTab)) return;
        if (!_tabs.Contains(tab)) return;
        _activeTab = tab;
        _mru.Touch(tab);
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

    /// <summary>
    /// Reorder one tab. The target is clamped against the pin boundary:
    /// a pinned tab cannot land outside the prefix and an unpinned tab
    /// cannot land inside it. Crossing the boundary on purpose is the
    /// caller's Move + <see cref="SetPinned"/> pair, one commit at the
    /// call site (the drag and keyboard layers), not something a plain
    /// reorder implies.
    /// </summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= _tabs.Count) return;
        if (to < 0 || to >= _tabs.Count) return;
        if (from == to) return;
        var tab = _tabs[from];
        if (tab.IsPinned)
            to = Math.Clamp(to, 0, PinCount - 1);
        else
            to = Math.Clamp(to, PinCount, _tabs.Count - 1);
        if (from == to) return;
        _tabs.RemoveAt(from);
        _tabs.Insert(to, tab);
        // The clamp keeps the op itself inside the invariants except for
        // group contiguity, which a pull-out mid-run breaks; Normalize
        // re-gathers the run. TabMoved still reports THIS op's indices:
        // repair relocations are state fixes the projector reconciles,
        // not moves to announce one by one.
        Normalize();
        TabMoved?.Invoke(this, (tab, from, to));
    }

    /// <summary>
    /// Pin or unpin, relocating the tab to the zone boundary: the end of
    /// the pinned prefix when pinning (so pinning keeps your neighbours),
    /// the first unpinned slot when unpinning. Pinning a grouped tab
    /// removes it from its group; activation and MRU are untouched.
    /// </summary>
    public void SetPinned(TabModel tab, bool pinned)
    {
        if (!_tabs.Contains(tab)) return;
        if (tab.IsPinned == pinned) return;
        if (pinned && tab.Group is not null)
            Ungroup(tab);
        tab.IsPinned = pinned;
        int from = _tabs.IndexOf(tab);
        _tabs.RemoveAt(from);
        // After the removal, the pinned count is the same slot for both
        // directions: end of the prefix, or first unpinned index.
        int to = PinCount;
        _tabs.Insert(to, tab);
        Normalize();
        if (from != to)
            TabMoved?.Invoke(this, (tab, from, to));
    }

    /// <summary>
    /// Slide the run containing <paramref name="from"/> so the grabbed
    /// member lands at <paramref name="to"/>, the other members rotating
    /// around it in their circular order. An intra-run operation: the
    /// target is clamped into the run's own span, because relocating the
    /// whole run is <see cref="MoveGroup"/> and pulling a member out of
    /// its run is <see cref="Ungroup"/> plus <see cref="Move"/>.
    /// </summary>
    public void MoveRun(int from, int to)
    {
        if (from < 0 || from >= _tabs.Count) return;
        var grabbed = _tabs[from];
        var run = RunOf(grabbed);
        if (run.Count <= 1)
        {
            Move(from, to);
            return;
        }

        int start = _tabs.IndexOf(run[0]);
        to = Math.Clamp(to, start, start + run.Count - 1);
        if (to == from) return;

        // The run keeps its circular order; only where the grabbed member
        // sits inside it changes. Members after it lead, members before it
        // trail, and the span re-forms around the grabbed slot.
        var rotated = new List<TabModel>(run.Count - 1);
        for (int i = (from - start) + 1; i < run.Count; i++) rotated.Add(run[i]);
        for (int i = 0; i < from - start; i++) rotated.Add(run[i]);

        var span = new List<TabModel>(run.Count);
        span.AddRange(rotated.GetRange(0, to - start));
        span.Add(grabbed);
        span.AddRange(rotated.GetRange(to - start, run.Count - 1 - (to - start)));

        var before = new List<TabModel>(_tabs);
        ReplaceSpan(start, span);
        Normalize();
        RaiseRunMoved(run, before);
    }

    /// <summary>
    /// Move a group's whole run as a unit so its first member lands at
    /// <paramref name="targetIndex"/>, internal member order preserved.
    /// The run is clamped to land whole and clear of the pinned prefix;
    /// groups cannot be pinned.
    ///
    /// This is the single commit behind a whole-run crossing, and both
    /// drag surfaces converge on it: the vertical header drag and the
    /// horizontal chip drag hand over a MODEL index measured from
    /// arranged positions at release (never an in-flight one), and the
    /// clamp here is why neither caller needs pin math of its own. A
    /// relocation raises one TabMoved per displaced member -- old index
    /// from before the splice, new from after -- which the projector
    /// reconciles; it is not a walkable sequence of single moves.
    /// </summary>
    public void MoveGroup(TabGroup group, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(group);
        var run = MembersOf(group);
        if (run.Count == 0) return;
        int start = _tabs.IndexOf(run[0]);
        // The target is the run head's slot in the final list: clamped to
        // land whole and clear of the pinned prefix (groups cannot be
        // pinned). State only reachable through the manager keeps that
        // clamp window non-empty; corrupt state (a tab pinned while still
        // grouped) can invert it, and Normalize below repairs whatever
        // the clamp cannot express.
        targetIndex = Math.Clamp(targetIndex, PinCount, Math.Max(PinCount, _tabs.Count - run.Count));
        if (targetIndex == start) return;

        var before = new List<TabModel>(_tabs);
        foreach (var tab in run) _tabs.Remove(tab);
        // Removing the block only shortens the list ahead of it; the
        // landing slot counts kept tabs, so the clamped target inserts
        // as-is.
        for (int k = 0; k < run.Count; k++)
            _tabs.Insert(targetIndex + k, run[k]);
        Normalize();
        RaiseRunMoved(run, before);
    }

    /// <summary>
    /// The contiguous run containing <paramref name="tab"/> in list order:
    /// its group's members, or the tab alone when ungrouped.
    /// </summary>
    public IReadOnlyList<TabModel> RunOf(TabModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.Group is null)
            return new List<TabModel> { tab };
        return MembersOf(tab.Group);
    }

    /// <summary>
    /// Put <paramref name="members"/> into <paramref name="group"/> and
    /// gather them into one contiguous run (in their current relative
    /// order) at the first member's position. Pinned members are skipped:
    /// the prefix outranks membership, the same rule <see cref="SetPinned"/>
    /// applies in the other direction.
    ///
    /// Membership only: the group's <see cref="TabGroup.IsCollapsed"/> bit
    /// is never touched here -- the property the session restore hangs its
    /// saved collapse state on (the auto-expanding join is
    /// <see cref="JoinGroup"/>).
    /// </summary>
    public void GroupTabs(IReadOnlyList<TabModel> members, TabGroup group)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(group);
        bool any = false;
        foreach (var tab in members)
        {
            if (!_tabs.Contains(tab) || tab.IsPinned) continue;
            tab.Group = group;
            any = true;
        }
        // An empty group must never be registered: Normalize dissolves
        // them, and a caller that grouped nothing gets nothing.
        if (!any) return;
        if (!_groups.Contains(group)) _groups.Add(group);
        Normalize();
    }

    /// <summary>
    /// New Group With Tab: a fresh group whose sole member is
    /// <paramref name="tab"/>. The tab keeps its position and the gather
    /// moves nothing (one member is already a run). Null when the tab is
    /// pinned (the prefix outranks membership, the same refusal
    /// <see cref="GroupTabs"/> applies) or not owned by this manager --
    /// either way nothing is registered. The caller renames and colors the
    /// returned group through its plain properties, which carry no
    /// invariants to protect.
    /// </summary>
    public TabGroup? CreateGroup(TabModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!_tabs.Contains(tab) || tab.IsPinned) return null;
        var group = new TabGroup();
        GroupTabs(new[] { tab }, group);
        return group;
    }

    /// <summary>
    /// Add one tab to a group: the single-commit join behind the
    /// drag-into-run drop, the Add to Group submenu, and a drop on a
    /// collapsed chip. Joining a COLLAPSED group auto-expands it (Edge's
    /// documented by-design rule), and the expand lives HERE -- manager
    /// state, so neither strip mode has to remember it and a drag can
    /// never carry it. The bit only moves when the join actually happened:
    /// a refused join (pinned, foreign tab) or a tab that is already a
    /// member leaves the user's collapse state exactly as they set it.
    /// Session restore does not come through here -- it must preserve the
    /// saved collapse bit, so it joins via <see cref="GroupTabs"/> instead.
    /// </summary>
    public void JoinGroup(TabModel tab, TabGroup group)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(group);
        bool wasMember = ReferenceEquals(tab.Group, group);
        GroupTabs(new[] { tab }, group);
        if (!wasMember && ReferenceEquals(tab.Group, group) && group.IsCollapsed)
            group.IsCollapsed = false;
    }

    /// <summary>
    /// Ungroup every member at once, in place: positions, activation, and
    /// MRU are untouched, and the group -- collapse state included -- is
    /// gone. The per-tab counterpart is <see cref="Ungroup"/>; this is the
    /// Remove from Group command's op.
    /// </summary>
    public void DissolveGroup(TabGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!_groups.Contains(group)) return;
        foreach (var t in _tabs)
            if (ReferenceEquals(t.Group, group))
                t.Group = null;
        Normalize();
    }

    /// <summary>
    /// Session restore's group op: recreate one saved group -- the saved
    /// id comes back as the live id, so group identity survives restart
    /// -- and gather <paramref name="members"/> into one run in saved
    /// tab order. The saved collapse bit is applied AS SAVED: unlike
    /// <see cref="JoinGroup"/>, restoring never auto-expands, which is
    /// why restore comes through here and
    /// <see cref="GroupTabs"/>-shaped membership, never the join.
    ///
    /// Repairs, never crashes, on corrupt saved state: a member that is
    /// somehow both pinned and grouped is skipped here (membership
    /// yields to the prefix), a member this manager does not own is
    /// skipped, and a group none of whose members made it back
    /// registers nothing -- Normalize then prunes it, so no orphan
    /// header or chip can reach a strip.
    /// </summary>
    public TabGroup RestoreGroup(Guid id, string title, TabColor color,
        bool collapsed, IReadOnlyList<TabModel> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var group = new TabGroup(id)
        {
            Title = title,
            Color = color,
            IsCollapsed = collapsed,
        };
        GroupTabs(members, group);
        return group;
    }

    /// <summary>
    /// Remove one tab from its group. The group dissolves if this was its
    /// last member, collapse state included. The tab keeps its position.
    /// </summary>
    public void Ungroup(TabModel tab)
    {
        if (tab.Group is null) return;
        tab.Group = null;
        Normalize();
    }

    /// <summary>
    /// Collapse or expand a group. Presentation only: no list mutation,
    /// and the active member of a collapsed group keeps its row once the
    /// projector lands. Collapse state lives on the group so both strip
    /// modes share one bit.
    ///
    /// No accordion: activating a member of a collapsed group swaps which
    /// member is visible and does NOT expand the group -- collapse state
    /// stays exactly as the user set it, and the strips below read the bit
    /// rather than any activation side effect. The strips also refuse to
    /// project a fully-collapsed group that holds the active tab
    /// (<see cref="HoldsActiveTab"/>), so selection is never hidden.
    /// </summary>
    public void CollapseGroup(TabGroup group, bool collapsed)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.IsCollapsed = collapsed;
    }

    /// <summary>
    /// Whether the group's run contains the active tab -- the model half
    /// of the Edge-135 rule. A collapsed group holding the active tab is
    /// never projected as fully collapsed: the vertical strip keeps the
    /// active member's row visible and the horizontal strip projects no
    /// chip for it, so selection is never hidden. Both strips ask here
    /// rather than re-deriving membership.
    /// </summary>
    public bool HoldsActiveTab(TabGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return ReferenceEquals(_activeTab.Group, group);
    }

    /// <summary>
    /// Whether closing the group would close every tab in the window:
    /// Close Group greys itself out then, the same rule as Move Tab to New
    /// Window. The close itself stays shell-side -- sequential through the
    /// per-tab confirmation path -- so the manager offers the guard, not
    /// the command.
    /// </summary>
    public bool GroupHoldsEveryTab(TabGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return _tabs.Count > 0 && MembersOf(group).Count == _tabs.Count;
    }

    /// <summary>
    /// Sort the pinned prefix A-Z by <see cref="TabModel.EffectiveTitle"/>,
    /// stable: equal titles keep their current order, so a second call is
    /// a no-op. Unpinned tabs are never touched.
    /// </summary>
    public void SortPinned()
    {
        // The one mutator that skips Normalize: only the prefix is
        // reordered, and a pinned tab can never be grouped (pinning a
        // grouped tab ungroups it; groups cannot be pinned), so a sort
        // cannot leave anything for Normalize to repair.
        int count = PinCount;
        if (count <= 1) return;
        var sorted = new List<TabModel>(count);
        for (int i = 0; i < count; i++) sorted.Add(_tabs[i]);
        // Insertion sort because List.Sort is unstable, and stability is
        // what makes equal titles idempotent here.
        for (int i = 1; i < sorted.Count; i++)
        {
            var key = sorted[i];
            int j = i - 1;
            while (j >= 0 &&
                   string.Compare(sorted[j].EffectiveTitle, key.EffectiveTitle,
                       StringComparison.OrdinalIgnoreCase) > 0)
            {
                sorted[j + 1] = sorted[j];
                j--;
            }
            sorted[j + 1] = key;
        }
        for (int i = 0; i < sorted.Count; i++)
        {
            var tab = sorted[i];
            int from = _tabs.IndexOf(tab);
            if (from == i) continue;
            _tabs.RemoveAt(from);
            _tabs.Insert(i, tab);
            // Each placement pulls from later in the prefix, so these are
            // ordinary single moves in the list state at fire time.
            TabMoved?.Invoke(this, (tab, from, i));
        }
    }

    /// <summary>
    /// Splice the contiguous span starting at <paramref name="start"/> for
    /// <paramref name="newSpan"/>. Members are removed high-to-low and
    /// reinserted at the same span start, which cannot shift the span
    /// (nothing before it moves) and keeps every intermediate mutation a
    /// plain Remove/Insert for CollectionChanged listeners.
    /// </summary>
    private void ReplaceSpan(int start, List<TabModel> newSpan)
    {
        for (int i = newSpan.Count - 1; i >= 0; i--) _tabs.RemoveAt(start + i);
        for (int i = 0; i < newSpan.Count; i++) _tabs.Insert(start + i, newSpan[i]);
    }

    private void RaiseRunMoved(IReadOnlyList<TabModel> run, List<TabModel> before)
    {
        // One event per relocated member, old index from before the splice
        // and new index from after. Unlike Move's, these pairs are not
        // individually applicable single moves - a run rotation cannot be
        // walked one tab at a time - so the consumer is the projector,
        // which re-derives rows from manager state rather than replaying
        // index math.
        foreach (var tab in run)
        {
            int from = before.IndexOf(tab);
            int to = _tabs.IndexOf(tab);
            if (from < 0 || from == to) continue;
            TabMoved?.Invoke(this, (tab, from, to));
        }
    }

    /// <summary>
    /// Repair the three list invariants after a mutation: no group without
    /// members, all pinned tabs in a leading prefix, every group's members
    /// in one contiguous run. Deterministic and silent - it raises no
    /// events and touches nothing but the order it repairs - so public
    /// mutators can run it before raising their own.
    /// </summary>
    private void Normalize()
    {
        // The prefix outranks membership (SetPinned's Chrome rule, applied
        // in the other direction): a tab that is somehow both pinned and
        // grouped loses the group, which also keeps every run inside the
        // unpinned zone so no repair below can straddle the boundary.
        // Membership is registry-backed: a group this window does not
        // list (an adoptee's source-window group, later a stale restore
        // id) is not a group at all here, so the member arrives
        // ungrouped.
        foreach (var t in _tabs)
        {
            if (t.Group is null) continue;
            if (t.IsPinned || !_groups.Contains(t.Group))
                t.Group = null;
        }

        // No empty groups: the last member leaving dissolves the group,
        // and its collapse state dies with the object.
        _groups.RemoveAll(g => MembersOf(g).Count == 0);

        // Pin prefix, repaired in place: the first pinned tab found after
        // unpinned tabs moves back to the prefix end. Both zones keep
        // their relative order, so a repair never shuffles tabs the
        // mutation did not touch.
        int prefix = 0;
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (!_tabs[i].IsPinned) continue;
            if (i != prefix)
            {
                var tab = _tabs[i];
                _tabs.RemoveAt(i);
                _tabs.Insert(prefix, tab);
            }
            prefix++;
        }

        // Group contiguity: a non-contiguous run re-forms at its first
        // member, in list order. This is what puts a member back after a
        // raw Move pulls it out mid-run; leaving the run on purpose is
        // Ungroup plus Move at the call site.
        foreach (var group in _groups)
        {
            var members = MembersOf(group);
            if (members.Count <= 1) continue;
            int start = _tabs.IndexOf(members[0]);
            bool contiguous = true;
            for (int k = 0; k < members.Count; k++)
            {
                if (ReferenceEquals(_tabs[start + k], members[k])) continue;
                contiguous = false;
                break;
            }
            if (contiguous) continue;
            foreach (var tab in members)
                _tabs.Remove(tab);
            for (int k = 0; k < members.Count; k++)
                _tabs.Insert(start + k, members[k]);
        }
    }

    private List<TabModel> MembersOf(TabGroup group)
    {
        var members = new List<TabModel>();
        foreach (var t in _tabs)
            if (ReferenceEquals(t.Group, group)) members.Add(t);
        return members;
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
        // Forward the active-leaf bell to the window level (taskbar badge)
        // and, when bell-features includes `title`, mark the tab indicator.
        // Captured as locals so CloseTab can unsubscribe alongside progress.
        EventHandler<Ghostty.Core.Bell.BellFeatures> bellRangHandler = (_, features) =>
        {
            BellRang?.Invoke(this, features);
            if (features.Title) tab.BellRinging = true;
        };
        // The set is gated on `title`; the clear is unconditional (clearing
        // an already-unset indicator is a no-op via the INPC equality guard).
        EventHandler bellAckHandler = (_, _) => tab.BellRinging = false;
        host.BellRang += bellRangHandler;
        host.BellAcknowledged += bellAckHandler;
        // Bridge "the last leaf in this tab closed" (e.g. the only shell
        // in this tab exited via `exit`, or libghostty's close-surface
        // callback fired for the sole pane) into a tab-level close. The
        // window-close → quit-after-last-window-closed chain relies on
        // CloseTab firing, which won't happen on its own when the close
        // originates from the surface rather than from a manager call.
        EventHandler lastLeafHandler = (_, _) => CloseTab(tab);
        host.LastLeafClosed += lastLeafHandler;
        tab.OnClose = () =>
        {
            host.ProgressChanged -= progressHandler;
            host.BellRang -= bellRangHandler;
            host.BellAcknowledged -= bellAckHandler;
            host.LastLeafClosed -= lastLeafHandler;
        };
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
        _mru.Remove(tab);
        // Same dissolve-on-last-member rule as the close path.
        Normalize();
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
            _mru.Touch(next);
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
    /// Raises <see cref="TabAdded"/> and activates the tab. Pin state
    /// travels with the tab: a pinned adoptee is folded into this
    /// window's prefix. Group membership does not: the tab's group, if
    /// any, belongs to the source window's registry, and Normalize drops
    /// unregistered membership, so the tab arrives ungrouped.
    /// </summary>
    public void AdoptTab(TabModel tab)
    {
        if (_tabs.Contains(tab))
            throw new InvalidOperationException("AdoptTab: tab already owned.");

        WireAdoptedTab(tab);
        _tabs.Add(tab);
        Normalize();
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
        EventHandler<Ghostty.Core.Bell.BellFeatures> bellRangHandler = (_, features) =>
        {
            BellRang?.Invoke(this, features);
            if (features.Title) tab.BellRinging = true;
        };
        // The set is gated on `title`; the clear is unconditional (clearing
        // an already-unset indicator is a no-op via the INPC equality guard).
        EventHandler bellAckHandler = (_, _) => tab.BellRinging = false;
        tab.PaneHost.BellRang += bellRangHandler;
        tab.PaneHost.BellAcknowledged += bellAckHandler;
        // Re-attach the last-leaf-closed bridge in the adopter's event
        // graph. See CreateTab for why the bridge exists; without it,
        // a tab detached to a new window would no longer close that
        // window when its shell exits.
        EventHandler lastLeafHandler = (_, _) => CloseTab(tab);
        tab.PaneHost.LastLeafClosed += lastLeafHandler;
        // OnClose stores the unsubscribe action so DetachTab /
        // CloseTab can walk back both wirings without needing to
        // re-capture the handler delegates.
        tab.OnClose = () =>
        {
            tab.PaneHost.ProgressChanged -= progressHandler;
            tab.PaneHost.BellRang -= bellRangHandler;
            tab.PaneHost.BellAcknowledged -= bellAckHandler;
            tab.PaneHost.LastLeafClosed -= lastLeafHandler;
        };
        tab.PropertyChanged += OnTabPropertyChanged;
    }

    /// <summary>
    /// Walk back the per-tab subscriptions installed by
    /// <see cref="CreateTab"/> or <see cref="WireAdoptedTab"/>: today
    /// the progress forwarder and the last-leaf-closed bridge. Shared
    /// between <see cref="CloseTab"/> and <see cref="DetachTab"/> so
    /// neither path has to spell out "invoke OnClose and null it"; in
    /// particular, <see cref="DetachTab"/> must NOT touch OnClose itself
    /// because the adopter overwrites it in <see cref="WireAdoptedTab"/>.
    /// </summary>
    private void UnsubscribeProgressForwarder(TabModel tab)
    {
        // Name is historical; OnClose now also unhooks LastLeafClosed.
        // OnClose is the aggregated unsubscribe action; running it
        // detaches every handler this manager attached to the pane
        // host. On DetachTab we go through this helper rather than
        // tab.OnClose?.Invoke() at the call site so the semantics
        // stay obvious: this is the per-tab teardown, not the full
        // close sequence.
        tab.OnClose?.Invoke();
    }
}
