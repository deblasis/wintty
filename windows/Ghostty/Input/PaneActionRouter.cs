using System;
using System.Collections.Generic;
using Ghostty.Core.Input;
using Ghostty.Core.Panes;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Ghostty.Panes;

namespace Ghostty.Input;

/// <summary>
/// Dispatches a <see cref="PaneAction"/> against a target
/// <see cref="TabManager"/>. Pane actions are routed to the active
/// tab's <see cref="IPaneHost"/>; tab actions are routed to the
/// manager directly. Single switch lives here so that adding a new
/// action is one place to edit.
///
/// CloseActiveProgressive is special: when a pane-only close suffices
/// it goes directly to <see cref="IPaneHost.CloseActive"/>; when a
/// full-tab close is needed, the router raises
/// <see cref="TabCloseRequestedFromKeyboard"/> so MainWindow can show
/// the multi-pane confirmation dialog from a context with an XamlRoot.
///
/// Instance-scoped: one <see cref="PaneActionRouter"/> per
/// <see cref="TabManager"/>, owned by <c>MainWindow</c>. The earlier
/// version exposed static events which kept MainWindow rooted past
/// close and would leak once the shell supported multiple windows.
/// </summary>
internal sealed class PaneActionRouter
{
    private readonly TabManager _tabs;
    private readonly Func<IReadOnlyList<ResolvedProfile>>? _getProfiles = null;
    private readonly Action<string, ProfileLaunchTarget>? _openProfile = null;
    private readonly Action<string>? _bindingAction = null;

    public PaneActionRouter(TabManager tabs)
    {
        _tabs = tabs;
    }

    public PaneActionRouter(
        TabManager tabs,
        Func<IReadOnlyList<ResolvedProfile>>? getProfiles,
        Action<string, ProfileLaunchTarget>? openProfile,
        Action<string>? bindingAction = null)
        : this(tabs)
    {
        _getProfiles = getProfiles;
        _openProfile = openProfile;
        _bindingAction = bindingAction;
    }

    public TabManager Tabs => _tabs;

    /// <summary>
    /// Raised when the Ctrl+Shift+Space chord fires. MainWindow
    /// listens and calls <c>VerticalTabHost.TogglePinnedFromKeyboard</c>
    /// if the current <see cref="Tabs.ITabHost"/> is a VerticalTabHost.
    /// </summary>
    public event EventHandler? ToggleVerticalTabsPinnedRequested;

    /// <summary>
    /// Raised when the Ctrl+Shift+, chord or the strip context menu
    /// item fires. MainWindow listens and runs its animated layout
    /// switch.
    /// </summary>
    public event EventHandler? ToggleTabLayoutRequested;

    /// <summary>
    /// Raised when the Ctrl+Shift+P chord fires. MainWindow listens
    /// and shows or hides the command palette overlay.
    /// </summary>
    public event EventHandler? CommandPaletteToggleRequested;

    /// <summary>
    /// Raised when the keyboard close chord targets a full-tab close.
    /// MainWindow listens and shows the confirmation dialog (if needed)
    /// before calling <see cref="TabManager.CloseTab"/>.
    /// </summary>
    public event EventHandler? TabCloseRequestedFromKeyboard;

    /// <summary>
    /// Raised when <see cref="PaneAction.ToggleFullscreen"/> fires.
    /// MainWindow listens and toggles
    /// <c>AppWindow.SetPresenter(FullScreen/Default)</c>.
    /// </summary>
    public event EventHandler? ToggleFullscreenRequested;

    /// <summary>
    /// Raised when the Ctrl+Shift+F chord fires. MainWindow listens
    /// and calls OpenSearch() on the active leaf's TerminalControl.
    /// </summary>
    public event EventHandler? OpenSearchRequested;

    /// <summary>
    /// Raised when the quake / drop-down chord fires. MainWindow listens
    /// and forwards to App.ToggleQuickTerminal(), which owns the
    /// singleton quake window.
    /// </summary>
    public event EventHandler? QuickTerminalToggleRequested;

    /// <summary>
    /// Raised when the cheat-sheet chord or palette entry fires. MainWindow
    /// listens and shows the keyboard-shortcuts cheat sheet dialog.
    /// </summary>
    public event EventHandler? ShowKeybindCheatsheetRequested;

    /// <summary>
    /// Raised when the About chord or palette entry fires. MainWindow
    /// listens and opens the singleton About window.
    /// </summary>
    public event EventHandler? ShowAboutRequested;

    /// <summary>
    /// Raised when the Ctrl+Shift+I inspector chord fires. MainWindow listens
    /// and toggles the inspector window (same handler the command palette and
    /// the libghostty inspector apprt action use).
    /// </summary>
    public event EventHandler? InspectorToggleRequested;

    /// <summary>
    /// Raised when the reopen-closed-tab chord or palette entry fires.
    /// MainWindow listens and rebuilds a tab from the most recent closed-tab
    /// snapshot into this window (reconstruction needs the WinUI factory).
    /// </summary>
    public event EventHandler? ReopenClosedTabRequested;

    /// <summary>
    /// Raised when the reopen-closed-window chord or palette entry fires.
    /// App listens and creates a window from the most recent closed-window
    /// snapshot (TabManager cannot create windows).
    /// </summary>
    public event EventHandler? ReopenClosedWindowRequested;

    /// <summary>
    /// Raised for goto_window. The argument is the direction:
    /// -1 = previous, +1 = next. MainWindow forwards to
    /// App.ActivateRelativeWindow.
    /// </summary>
    public event EventHandler<int>? GotoWindowRequested;

    /// <summary>Raised for reset_window_size. MainWindow resizes the
    /// AppWindow back to the captured initial (config-default) size.</summary>
    public event EventHandler? ResetWindowSizeRequested;

    /// <summary>Raised for toggle_background_opacity. MainWindow flips the
    /// configured opacity between 1.0 and the remembered baseline.</summary>
    public event EventHandler? ToggleBackgroundOpacityRequested;

    /// <summary>
    /// Raised for float_window. The argument matches ghostty_action_float_window_e:
    /// 0 = on, 1 = off, 2 = toggle. MainWindow applies always-on-top.
    /// </summary>
    public event EventHandler<int>? FloatWindowRequested;

    /// <summary>
    /// Raised when a Ctrl+Tab / Ctrl+Shift+Tab chord fires. The bool is true for
    /// Ctrl+Tab (next), false for Ctrl+Shift+Tab (prev). MainWindow listens and
    /// switches to the next/previous tab immediately, flashing the preview popup.
    /// </summary>
    public event EventHandler<bool>? MruCycleRequested;

    /// <summary>
    /// Raised when the tab-overview chord or the palette "Show all tabs" entry
    /// fires. MainWindow listens and shows the tab overview grid.
    /// </summary>
    public event EventHandler? ShowTabOverviewRequested;

    /// <summary>
    /// Raised after a commanded pin/unpin has landed on the manager --
    /// palette entry, chord, or the per-tab context menu, all of which
    /// dispatch through <see cref="RequestPin"/>. The bool is the state
    /// the tab was left in. MainWindow listens to raise the UIA
    /// announcement from a window-owned element: pointer drags cross the
    /// same boundary through <see cref="TabManager.SetPinned"/> and raise
    /// nothing, so the source has to be the dispatch path, not the state
    /// change.
    /// </summary>
    public event EventHandler<(TabModel Tab, bool Pinned)>? TabPinChangedFromCommand;

    public void Invoke(PaneAction action)
    {
        // Event-only actions that don't need pane/tab state — handle
        // before accessing ActiveTab.PaneHost to avoid null/cast issues.
        switch (action)
        {
            case PaneAction.ToggleVerticalTabsPinned:
                ToggleVerticalTabsPinnedRequested?.Invoke(this, EventArgs.Empty);
                return;
            case PaneAction.ToggleTabLayout:
                ToggleTabLayoutRequested?.Invoke(this, EventArgs.Empty);
                return;
            case PaneAction.ToggleCommandPalette:
                CommandPaletteToggleRequested?.Invoke(this, EventArgs.Empty);
                return;
            case PaneAction.ToggleFullscreen:
                ToggleFullscreenRequested?.Invoke(this, EventArgs.Empty);
                return;
            case PaneAction.OpenSearch:
                OpenSearchRequested?.Invoke(this, EventArgs.Empty);
                return;
            case PaneAction.ToggleQuickTerminal:
                QuickTerminalToggleRequested?.Invoke(this, EventArgs.Empty);
                return;
            case PaneAction.ShowKeybindCheatsheet:
                ShowKeybindCheatsheetRequested?.Invoke(this, EventArgs.Empty);
                return;
            case PaneAction.ShowAbout:
                ShowAboutRequested?.Invoke(this, EventArgs.Empty);
                return;
            case PaneAction.ToggleInspector:
                InspectorToggleRequested?.Invoke(this, EventArgs.Empty);
                return;
            case PaneAction.ReopenClosedTab:
                ReopenClosedTabRequested?.Invoke(this, EventArgs.Empty);
                return;
            case PaneAction.ReopenClosedWindow:
                ReopenClosedWindowRequested?.Invoke(this, EventArgs.Empty);
                return;
            case PaneAction.GotoWindowPrevious:
                GotoWindowRequested?.Invoke(this, -1);
                return;
            case PaneAction.GotoWindowNext:
                GotoWindowRequested?.Invoke(this, +1);
                return;
            case PaneAction.ResetWindowSize:
                ResetWindowSizeRequested?.Invoke(this, EventArgs.Empty);
                return;
            case PaneAction.ToggleBackgroundOpacity:
                ToggleBackgroundOpacityRequested?.Invoke(this, EventArgs.Empty);
                return;
            case PaneAction.FloatWindowOn:
                FloatWindowRequested?.Invoke(this, 0);
                return;
            case PaneAction.FloatWindowOff:
                FloatWindowRequested?.Invoke(this, 1);
                return;
            case PaneAction.FloatWindowToggle:
                FloatWindowRequested?.Invoke(this, 2);
                return;
            case PaneAction.MruCycleNext:
                MruCycleRequested?.Invoke(this, true);
                return;
            case PaneAction.MruCyclePrev:
                MruCycleRequested?.Invoke(this, false);
                return;
            case PaneAction.ShowTabOverview:
                ShowTabOverviewRequested?.Invoke(this, EventArgs.Empty);
                return;

            // Scrollback jumps are dispatched as libghostty binding
            // actions against the active surface. The injected delegate
            // resolves the active leaf so we don't need to reach through
            // the pane tree here. Silent no-op if no delegate wired.
            case PaneAction.ScrollToTop:
                _bindingAction?.Invoke("scroll_to_top");
                return;
            case PaneAction.ScrollToBottom:
                _bindingAction?.Invoke("scroll_to_bottom");
                return;
            case PaneAction.JumpToPreviousPrompt:
                _bindingAction?.Invoke("jump_to_prompt:-1");
                return;
            case PaneAction.JumpToNextPrompt:
                _bindingAction?.Invoke("jump_to_prompt:1");
                return;

            // Profile slot chords resolve via the live registry; out-of-range
            // and missing-delegate are silent no-ops.
            case PaneAction.OpenProfile1: OpenProfileSlot(1); return;
            case PaneAction.OpenProfile2: OpenProfileSlot(2); return;
            case PaneAction.OpenProfile3: OpenProfileSlot(3); return;
            case PaneAction.OpenProfile4: OpenProfileSlot(4); return;
            case PaneAction.OpenProfile5: OpenProfileSlot(5); return;
            case PaneAction.OpenProfile6: OpenProfileSlot(6); return;
            case PaneAction.OpenProfile7: OpenProfileSlot(7); return;
            case PaneAction.OpenProfile8: OpenProfileSlot(8); return;
            case PaneAction.OpenProfile9: OpenProfileSlot(9); return;
        }

        var pane = _tabs.ActiveTab.PaneHost;
        var concrete = (PaneHost)pane;
        switch (action)
        {
            // Panes
            case PaneAction.SplitVertical:   concrete.Split(PaneOrientation.Vertical); break;
            case PaneAction.SplitHorizontal: concrete.Split(PaneOrientation.Horizontal); break;
            case PaneAction.ClosePane:       pane.CloseActive(); break;
            case PaneAction.FocusLeft:       concrete.FocusDirection(FocusDirection.Left); break;
            case PaneAction.FocusRight:      concrete.FocusDirection(FocusDirection.Right); break;
            case PaneAction.FocusUp:         concrete.FocusDirection(FocusDirection.Up); break;
            case PaneAction.FocusDown:       concrete.FocusDirection(FocusDirection.Down); break;
            case PaneAction.EqualizeSplits:  concrete.EqualizeSplits(); break;
            case PaneAction.ToggleSplitZoom: concrete.ToggleSplitZoom(); break;
            case PaneAction.GotoSplitPrevious: concrete.GotoPreviousSplit(); break;
            case PaneAction.GotoSplitNext:     concrete.GotoNextSplit(); break;
            case PaneAction.ResizeSplitUp:     concrete.ResizeSplit(ResizeDirection.Up); break;
            case PaneAction.ResizeSplitDown:   concrete.ResizeSplit(ResizeDirection.Down); break;
            case PaneAction.ResizeSplitLeft:   concrete.ResizeSplit(ResizeDirection.Left); break;
            case PaneAction.ResizeSplitRight:  concrete.ResizeSplit(ResizeDirection.Right); break;
            case PaneAction.Undo:            concrete.Undo(); break;
            case PaneAction.Redo:            concrete.Redo(); break;

            // Tabs
            case PaneAction.NewTab: _tabs.NewTab(); break;
            case PaneAction.CloseActiveProgressive: HandleProgressiveClose(); break;
            case PaneAction.NextTab: _tabs.Next(); break;
            case PaneAction.PrevTab: _tabs.Prev(); break;
            case PaneAction.JumpTab1: _tabs.JumpTo(0); break;
            case PaneAction.JumpTab2: _tabs.JumpTo(1); break;
            case PaneAction.JumpTab3: _tabs.JumpTo(2); break;
            case PaneAction.JumpTab4: _tabs.JumpTo(3); break;
            case PaneAction.JumpTab5: _tabs.JumpTo(4); break;
            case PaneAction.JumpTab6: _tabs.JumpTo(5); break;
            case PaneAction.JumpTab7: _tabs.JumpTo(6); break;
            case PaneAction.JumpTab8: _tabs.JumpTo(7); break;
            case PaneAction.JumpTabLast: _tabs.JumpToLast(); break;
            case PaneAction.MoveTabRight:
            {
                var i = _tabs.IndexOf(_tabs.ActiveTab);
                if (i >= 0 && i < _tabs.Tabs.Count - 1) _tabs.Move(i, i + 1);
                break;
            }
            case PaneAction.MoveTabLeft:
            {
                var i = _tabs.IndexOf(_tabs.ActiveTab);
                if (i > 0) _tabs.Move(i, i - 1);
                break;
            }
            // Group-as-unit moves for the ACTIVE tab's group, one
            // neighbouring group per step. An ungrouped active tab falls
            // out silently -- and so does a pinned one, because a pinned
            // tab can never be in a group. Where the run can land is
            // MoveGroup's own clamp (clear of the pinned prefix), the
            // same commit the drag surfaces hand over, so this adds no
            // boundary policy of its own.
            case PaneAction.MoveGroupRight:
            case PaneAction.MoveGroupLeft:
            {
                var group = _tabs.ActiveTab?.Group;
                if (group is null) break;
                var run = _tabs.MembersOf(group);
                var start = _tabs.IndexOf(run[0]);
                if (action == PaneAction.MoveGroupLeft)
                {
                    // The pinned prefix is not a neighbour to swap with:
                    // nothing unpinned to the left is a no-op, matching
                    // MoveTabLeft's `i > 0` guard.
                    if (start <= _tabs.PinCount) break;
                    _tabs.MoveGroup(group, start - _tabs.RunOf(_tabs.Tabs[start - 1]).Count);
                }
                else
                {
                    if (start + run.Count >= _tabs.Tabs.Count) break;
                    _tabs.MoveGroup(group, start + _tabs.RunOf(_tabs.Tabs[start + run.Count]).Count);
                }
                break;
            }
            case PaneAction.PinTab:
            case PaneAction.UnpinTab:
            {
                var tab = _tabs.ActiveTab;
                if (tab is not null)
                    RequestPin(tab, action == PaneAction.PinTab);
                break;
            }
            // Group ops act on the active tab's group; an ungrouped active
            // tab falls out silently, and each Request's own refusal guards
            // (pinned refusal, same-state silence, landed-bit gate) decide
            // the rest -- the palette adds no policy of its own.
            case PaneAction.NewGroupWithTab:
            {
                var tab = _tabs.ActiveTab;
                if (tab is not null) RequestNewGroupWithTab(tab);
                break;
            }
            case PaneAction.RemoveFromGroup:
            {
                var tab = _tabs.ActiveTab;
                if (tab is not null) RequestRemoveFromGroup(tab);
                break;
            }
            case PaneAction.CollapseGroup:
            case PaneAction.ExpandGroup:
            {
                var group = _tabs.ActiveTab?.Group;
                if (group is not null)
                    RequestCollapseGroup(group, action == PaneAction.CollapseGroup);
                break;
            }
            case PaneAction.DissolveGroup:
            case PaneAction.CloseGroup:
            {
                var group = _tabs.ActiveTab?.Group;
                if (group is null) break;
                if (action == PaneAction.DissolveGroup) RequestDissolveGroup(group);
                else RequestCloseGroup(group);
                break;
            }
            // Rename and color are dialog ops: the op itself is a plain
            // INPC set, but opening the dialog and the picker needs a
            // XamlRoot, so the window hosts them and hands the result
            // back through the Request below.
            case PaneAction.RenameGroup:
            case PaneAction.ColorGroup:
            {
                var group = _tabs.ActiveTab?.Group;
                if (group is null) break;
                if (action == PaneAction.RenameGroup) GroupRenameRequested?.Invoke(this, group);
                else GroupColorRequested?.Invoke(this, group);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    /// <summary>
    /// Public dispatch entry used by non-keyboard triggers (context
    /// menu, title-bar button). Reuses the same event path so
    /// MainWindow has a single handler for every toggle source.
    /// </summary>
    public void RequestToggleTabLayout()
        => ToggleTabLayoutRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// One pin, one implementation: the manager op plus the event that
    /// tells the window to announce it. Invoke's PinTab/UnpinTab land
    /// here, and so does the per-tab context menu, whose pin targets the
    /// right-clicked tab rather than the active one. Pointer drags cross
    /// the same boundary through <see cref="TabManager.SetPinned"/>
    /// directly and announce nothing (5.6), so the source is the dispatch
    /// path, not the state change.
    /// </summary>
    public void RequestPin(TabModel tab, bool pin)
    {
        if (_tabs.IndexOf(tab) < 0) return;
        // A command naming the state the tab already has did nothing;
        // announcing it would narrate a change that never happened.
        if (tab.IsPinned == pin) return;
        _tabs.SetPinned(tab, pin);
        TabPinChangedFromCommand?.Invoke(this, (tab, pin));
    }

    /// <summary>
    /// Raised when the per-tab context menu asks for a duplicate of one
    /// specific tab. MainWindow performs the clone: it owns the
    /// capture/restore pair and the pane-host factory, none of which the
    /// router (or the menu builder) should reach into.
    /// </summary>
    public event EventHandler<TabModel>? DuplicateTabRequested;

    /// <summary>
    /// Public dispatch entry for the menu's Duplicate Tab item, the same
    /// one-hop shape as <see cref="RequestToggleTabLayout"/>: no chord
    /// stands behind it (no defaults in v1), so there is no PaneAction.
    /// </summary>
    public void RequestDuplicateTab(TabModel tab)
        => DuplicateTabRequested?.Invoke(this, tab);

    /// <summary>
    /// Public dispatch entry for the sidebar collapse toggle. Reuses
    /// the existing ToggleVerticalTabsPinnedRequested event so
    /// MainWindow has one handler for every source (Ctrl+Shift+Space
    /// keyboard chord, chevron button, strip context menu).
    /// </summary>
    public void RequestToggleSidebarCollapse()
        => ToggleVerticalTabsPinnedRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Public dispatch entry for the MRU cycle, the same one-hop shape as
    /// <see cref="RequestToggleTabLayout"/>. The Ctrl+Tab chord reaches
    /// <see cref="MruCycleRequested"/> through PaneAction; this lets a
    /// non-keyboard caller raise the identical event, so the switcher
    /// popup a driver sees is the popup the chord raises rather than a
    /// second code path that could drift from it.
    /// </summary>
    public void RequestMruCycle(bool forward)
        => MruCycleRequested?.Invoke(this, forward);

    private void HandleProgressiveClose()
    {
        // If the active tab has more than one pane, close one and stop.
        // Otherwise the entire tab is being closed; emit the request
        // event so MainWindow can show the confirmation dialog
        // (TabManager has no XamlRoot).
        if (_tabs.ActiveTab.PaneHost.PaneCount > 1)
        {
            _tabs.ActiveTab.PaneHost.CloseActive();
            return;
        }
        TabCloseRequestedFromKeyboard?.Invoke(this, EventArgs.Empty);
    }

    // -----------------------------------------------------------------
    // Group commands (5b-2a). One dispatch discipline with pins: every
    // source (context menu, the keyboard chevron, later the palette)
    // routes through a Request, each Request guards + mutates + raises,
    // and the WINDOW announces. The manager op alone stays silent, so a
    // drag-join or a session restore narrates nothing.
    // -----------------------------------------------------------------

    /// <summary>What a <see cref="GroupChangedFromCommand"/> payload did.</summary>
    internal enum GroupCommandKind
    {
        Created,
        Joined,
        Removed,
        Dissolved,
        Collapsed,
        Renamed,
        Colored,
    }

    /// <summary>
    /// One command's announce data. <paramref name="Tab"/> is the affected
    /// tab when the command is tab-shaped (null for dissolve/collapse, and
    /// the announcement source falls back to the focused element).
    /// <paramref name="MemberCount"/> is PRE-op on purpose: it feeds
    /// Dissolved, and after the op the group owns no members to count.
    /// <paramref name="OldTitle"/> is pre-op too: Renamed lands the new
    /// title on the group, so the text could otherwise only say the new
    /// name twice.
    /// </summary>
    internal readonly record struct GroupCommandData(
        GroupCommandKind Kind,
        TabGroup Group,
        TabModel? Tab,
        int MemberCount,
        string? OldTitle = null);

    /// <summary>
    /// Raised after a commanded group change has landed, for the window to
    /// announce. Pointer drags and session restores perform the same ops
    /// through the manager and never pass through here, which is exactly
    /// the dispatch-path discipline.
    /// </summary>
    public event EventHandler<GroupCommandData>? GroupChangedFromCommand;

    /// <summary>
    /// Raised when a command asks a group to collapse or expand. Collapse
    /// re-homes keyboard focus under the folding group, and only the strip
    /// knows where focus sits, so the window forwards this to the vertical
    /// host and the op runs through the strip's command entry -- the same
    /// toggle the chevron uses, stand-down and fence included.
    /// </summary>
    public event EventHandler<(TabGroup Group, bool Collapsed)>? GroupCollapseRequested;

    /// <summary>
    /// Raised when a command asks a group to close. The close runs
    /// shell-side -- each member goes through the per-tab confirmation
    /// path, which needs a XamlRoot the manager and this router lack.
    /// </summary>
    public event EventHandler<TabGroup>? GroupCloseRequested;

    /// <summary>
    /// Raised when a command asks to rename a group. Rename is a dialog
    /// op: the router has no XamlRoot to host RenameTabDialog, so the
    /// window shows it and hands the result back through
    /// <see cref="RequestRenameGroup"/>, which performs the set and the
    /// announce. The menu path opens the same dialog directly.
    /// </summary>
    public event EventHandler<TabGroup>? GroupRenameRequested;

    /// <summary>
    /// Raised when a command asks to recolor a group. The palette picker
    /// is a Flyout, so like rename this forwards to the window, which owns
    /// both the XamlRoot and an element to anchor it on.
    /// </summary>
    public event EventHandler<TabGroup>? GroupColorRequested;

    /// <summary>
    /// New Group With Tab: a fresh group whose sole member is the given
    /// tab. Refused silently for a pinned tab (the prefix outranks
    /// membership, and the manager returns null), so nothing announces.
    /// </summary>
    public void RequestNewGroupWithTab(TabModel tab)
    {
        if (_tabs.IndexOf(tab) < 0) return;
        if (_tabs.CreateGroup(tab) is not { } group) return;
        GroupChangedFromCommand?.Invoke(this, new(GroupCommandKind.Created, group, tab, 1));
    }

    /// <summary>
    /// Add one tab to a group. A tab that is already a member is a no-op
    /// that announces nothing -- narrating a membership that did not
    /// change is a lie, and re-joining would also auto-expand a collapsed
    /// group the user folded on purpose.
    /// </summary>
    public void RequestAddToGroup(TabModel tab, TabGroup group)
    {
        if (_tabs.IndexOf(tab) < 0) return;
        if (!_tabs.Groups.Contains(group)) return;
        // JoinGroup silently skips pinned members: a refusal must not narrate.
        if (tab.IsPinned) return;
        if (ReferenceEquals(tab.Group, group)) return;
        _tabs.JoinGroup(tab, group);
        GroupChangedFromCommand?.Invoke(this, new(GroupCommandKind.Joined, group, tab, 0));
    }

    /// <summary>
    /// Remove one tab from its group. The announcement names the PRE-op
    /// group: after the op the tab answers "no group" and the text would
    /// name nothing.
    /// </summary>
    public void RequestRemoveFromGroup(TabModel tab)
    {
        if (tab.Group is not { } group) return;
        if (_tabs.IndexOf(tab) < 0) return;
        _tabs.Ungroup(tab);
        GroupChangedFromCommand?.Invoke(this, new(GroupCommandKind.Removed, group, tab, 0));
    }

    /// <summary>
    /// Dissolve a group: every member ungroups in place. A group the
    /// manager no longer registers is a no-op, so nothing announces --
    /// dissolving twice would narrate the second one too.
    /// </summary>
    public void RequestDissolveGroup(TabGroup group)
    {
        if (!_tabs.Groups.Contains(group)) return;
        var count = _tabs.MembersOf(group).Count;
        _tabs.DissolveGroup(group);
        GroupChangedFromCommand?.Invoke(this, new(GroupCommandKind.Dissolved, group, null, count));
    }

    /// <summary>
    /// Collapse or expand a group. A command naming the state the group
    /// already has is a no-op that announces nothing, exactly like
    /// <see cref="RequestPin"/>. Execution forwards to the strip (focus
    /// re-homing lives there); the announce rides behind it, so the state
    /// read at announce time is the landed one -- and a stood-down
    /// forward (drag in flight) leaves that bit untouched, so it refuses
    /// to announce an op that did not happen.
    /// </summary>
    public void RequestCollapseGroup(TabGroup group, bool collapsed)
    {
        if (!_tabs.Groups.Contains(group)) return;
        if (group.IsCollapsed == collapsed) return;
        GroupCollapseRequested?.Invoke(this, (group, collapsed));
        // The forward can be a no-op: the strip stands down under a drag.
        // The chain is synchronous on the UI thread, so the landed bit is
        // post-forward truth.
        if (group.IsCollapsed != collapsed) return;
        GroupChangedFromCommand?.Invoke(this, new(GroupCommandKind.Collapsed, group, _tabs.ActiveTab, 0));
    }

    /// <summary>
    /// Close every member of a group. The guard is registration, not
    /// emptiness: a group with no members cannot be right-clicked, and the
    /// shell-side loop stops on its own once the members run out.
    /// </summary>
    public void RequestCloseGroup(TabGroup group)
    {
        if (!_tabs.Groups.Contains(group)) return;
        GroupCloseRequested?.Invoke(this, group);
    }

    /// <summary>
    /// Rename a group: a plain INPC set, no manager op -- the title lives
    /// on the group alone and the strips re-read it through their group
    /// bindings. A blank title, a group the manager no longer registers
    /// (a dialog can outlive a dissolve), or the title the group already
    /// has is a no-op that announces nothing.
    /// </summary>
    public void RequestRenameGroup(TabGroup group, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        if (!_tabs.Groups.Contains(group)) return;
        if (group.Title == title) return;
        var old = group.Title;
        group.Title = title;
        GroupChangedFromCommand?.Invoke(this, new(GroupCommandKind.Renamed, group, null, 0, old));
    }

    /// <summary>
    /// Recolor a group: the same plain-set shape as rename. Picking the
    /// color the group already wears is a no-op that announces nothing.
    /// </summary>
    public void RequestColorGroup(TabGroup group, TabColor color)
    {
        if (!_tabs.Groups.Contains(group)) return;
        // Compared after resolution, because the setter resolves: a group
        // already wearing the default that is handed a colour with no preset
        // is asked for no change at all. Comparing the OFFERED value let that
        // through, and the announcement below then told a screen reader the
        // colour had changed while nothing repainted, because the model
        // correctly raised no INPC.
        if (group.Color == TabColorPalette.EnsureGroupColor(color)) return;
        group.Color = color;
        GroupChangedFromCommand?.Invoke(this, new(GroupCommandKind.Colored, group, null, 0));
    }

    /// <summary>
    /// Open the profile at 1-based <paramref name="slot"/> in the live
    /// registry snapshot. Silent no-op (no exception) when the slot is out
    /// of range, the registry isn't wired, or no open-profile delegate was
    /// injected. Real slot logic lives in
    /// <see cref="Ghostty.Core.Input.ProfileSlotResolver"/> so it stays
    /// testable from Ghostty.Tests.
    /// </summary>
    private void OpenProfileSlot(int slot)
    {
        if (_getProfiles is null || _openProfile is null) return;
        var id = ProfileSlotResolver.Resolve(_getProfiles(), slot);
        if (id is null) return;
        _openProfile(id, ProfileLaunchTarget.NewTab);
    }
}
