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

    public PaneActionRouter(TabManager tabs)
    {
        _tabs = tabs;
    }

    public PaneActionRouter(
        TabManager tabs,
        Func<IReadOnlyList<ResolvedProfile>>? getProfiles,
        Action<string, ProfileLaunchTarget>? openProfile)
        : this(tabs)
    {
        _getProfiles = getProfiles;
        _openProfile = openProfile;
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

            // PR 5: profile slot chords. Resolve via the live registry; out-of-range
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
    /// Public dispatch entry for the sidebar collapse toggle. Reuses
    /// the existing ToggleVerticalTabsPinnedRequested event so
    /// MainWindow has one handler for every source (Ctrl+Shift+Space
    /// keyboard chord, chevron button, strip context menu).
    /// </summary>
    public void RequestToggleSidebarCollapse()
        => ToggleVerticalTabsPinnedRequested?.Invoke(this, EventArgs.Empty);

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
