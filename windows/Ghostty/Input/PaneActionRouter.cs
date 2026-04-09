using System;
using Ghostty.Core.Panes;
using Ghostty.Core.Tabs;
using Ghostty.Panes;

namespace Ghostty.Input;

/// <summary>
/// Dispatches a <see cref="PaneAction"/> against a target
/// <see cref="TabManager"/>. Pane actions are routed to the active
/// tab's <see cref="IPaneHost"/>; tab actions are routed to the
/// manager directly. Single switch lives here so adding a new
/// action is one place to edit.
///
/// Escape hooks (full-tab close confirmation and vertical-tabs
/// pinned toggle) are passed in as delegates per invocation rather
/// than raised through static events. Static events would root
/// every MainWindow closure forever — a real leak once multi-window
/// lands. Per-call delegates keep the router stateless.
/// </summary>
internal static class PaneActionRouter
{
    public static void Invoke(
        PaneAction action,
        TabManager tabs,
        Action<TabManager>? onTabCloseRequested = null,
        Action<TabManager>? onToggleVerticalTabsPinned = null)
    {
        var pane = tabs.ActiveTab.PaneHost;
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

            // Tabs
            case PaneAction.NewTab: tabs.NewTab(); break;
            case PaneAction.CloseActiveProgressive: HandleProgressiveClose(tabs, onTabCloseRequested); break;
            case PaneAction.NextTab: tabs.Next(); break;
            case PaneAction.PrevTab: tabs.Prev(); break;
            case PaneAction.JumpTab1: tabs.JumpTo(0); break;
            case PaneAction.JumpTab2: tabs.JumpTo(1); break;
            case PaneAction.JumpTab3: tabs.JumpTo(2); break;
            case PaneAction.JumpTab4: tabs.JumpTo(3); break;
            case PaneAction.JumpTab5: tabs.JumpTo(4); break;
            case PaneAction.JumpTab6: tabs.JumpTo(5); break;
            case PaneAction.JumpTab7: tabs.JumpTo(6); break;
            case PaneAction.JumpTab8: tabs.JumpTo(7); break;
            case PaneAction.JumpTabLast: tabs.JumpToLast(); break;
            case PaneAction.MoveTabRight:
            {
                var i = tabs.IndexOf(tabs.ActiveTab);
                if (i >= 0 && i < tabs.Tabs.Count - 1) tabs.Move(i, i + 1);
                break;
            }
            case PaneAction.MoveTabLeft:
            {
                var i = tabs.IndexOf(tabs.ActiveTab);
                if (i > 0) tabs.Move(i, i - 1);
                break;
            }
            case PaneAction.ToggleVerticalTabsPinned:
                onToggleVerticalTabsPinned?.Invoke(tabs);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private static void HandleProgressiveClose(TabManager tabs, Action<TabManager>? onTabCloseRequested)
    {
        // If the active tab has more than one pane, close one and stop.
        // Otherwise the entire tab is being closed; let the caller show
        // the confirmation dialog (TabManager has no XamlRoot).
        if (tabs.ActiveTab.PaneHost.PaneCount > 1)
        {
            tabs.ActiveTab.PaneHost.CloseActive();
            return;
        }
        onTabCloseRequested?.Invoke(tabs);
    }
}
