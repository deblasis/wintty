using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Tabs;

/// <summary>
/// Builds the per-tab right-click menu. Attached via
/// <see cref="TabViewItem.ContextFlyout"/> on each item, not on the
/// parent <see cref="TabView"/>: that gives an unambiguous target.
///
/// "Move to New Window" is intentionally absent. It needs reparent-
/// safe PaneHost work and a cross-window GhosttyHost handoff; that
/// is a follow-up PR.
/// TODO(tabs): detach-to-new-window
/// </summary>
internal static class TabContextMenuBuilder
{
    public static MenuFlyout Build(TabManager manager, TabModel tab, Func<TabModel, Task> requestClose)
    {
        var flyout = new MenuFlyout();

        // The Close item routes through requestClose so it shows the
        // multi-pane confirmation dialog when needed. Close Others
        // and Close Tabs to the Right are explicit user actions on
        // non-active tabs and skip the prompt — that matches how
        // VSCode and Windows Terminal behave.
        var close = new MenuFlyoutItem { Text = "Close" };
        close.Click += async (_, _) => await requestClose(tab);
        flyout.Items.Add(close);

        var closeOthers = new MenuFlyoutItem { Text = "Close Others" };
        closeOthers.Click += (_, _) => CloseOthers(manager, tab);
        flyout.Items.Add(closeOthers);

        var closeRight = new MenuFlyoutItem { Text = "Close Tabs to the Right" };
        closeRight.Click += (_, _) => CloseToRight(manager, tab);
        flyout.Items.Add(closeRight);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var rename = new MenuFlyoutItem { Text = "Rename Tab" };
        rename.Click += async (_, _) =>
        {
            var target = flyout.Target;
            if (target?.XamlRoot is null) return;
            var dlg = new RenameTabDialog(tab.UserOverrideTitle) { XamlRoot = target.XamlRoot };
            var res = await dlg.ShowAsync();
            if (res == ContentDialogResult.Primary)
                tab.UserOverrideTitle = string.IsNullOrWhiteSpace(dlg.Result) ? null : dlg.Result;
        };
        flyout.Items.Add(rename);

        var dup = new MenuFlyoutItem { Text = "Duplicate Tab" };
        dup.Click += (_, _) => manager.NewTab(); // TODO(config): respect ProfileId once profiles exist
        flyout.Items.Add(dup);

        return flyout;
    }

    private static void CloseOthers(TabManager manager, TabModel keep)
    {
        var snapshot = new List<TabModel>(manager.Tabs);
        foreach (var t in snapshot)
            if (!ReferenceEquals(t, keep)) manager.CloseTab(t);
    }

    private static void CloseToRight(TabManager manager, TabModel anchor)
    {
        var idx = manager.IndexOf(anchor);
        if (idx < 0) return;
        var snapshot = new List<TabModel>();
        for (int i = idx + 1; i < manager.Tabs.Count; i++) snapshot.Add(manager.Tabs[i]);
        foreach (var t in snapshot) manager.CloseTab(t);
    }
}
