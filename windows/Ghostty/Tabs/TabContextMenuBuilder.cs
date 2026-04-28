using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ghostty.Core.Tabs;
using Ghostty.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Ghostty.Tabs;

/// <summary>
/// Information the picker needs when "Move Tab to Zone" is clicked.
/// Supplied by the TabHost caller so TabContextMenuBuilder does not
/// have to reach into WinUI windowing APIs itself.
/// </summary>
internal readonly record struct SnapZoneSource(
    int WorkAreaWidth,
    int WorkAreaHeight);

/// <summary>
/// Builds the per-tab right-click menu. Attached via
/// <see cref="TabViewItem.ContextFlyout"/> on each item, not on the
/// parent <see cref="TabView"/>: that gives an unambiguous target.
/// </summary>
internal static class TabContextMenuBuilder
{
    public static MenuFlyout Build(
        TabManager manager,
        TabModel tab,
        Func<TabModel, Task> requestClose,
        Action<TabModel> requestDetachToNewWindow,
        DialogTracker dialogs,
        Func<SnapZoneSource>? getSnapSource = null,
        Action<TabModel, SnapZone>? detachWithZone = null)
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
            using (dialogs.Track(dlg))
            {
                var res = await dlg.ShowAsync();
                if (res == ContentDialogResult.Primary)
                    tab.UserOverrideTitle = string.IsNullOrWhiteSpace(dlg.Result) ? null : dlg.Result;
            }
        };
        flyout.Items.Add(rename);

        var dup = new MenuFlyoutItem { Text = "Duplicate Tab" };
        dup.Click += (_, _) => manager.NewTab();
        flyout.Items.Add(dup);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var detach = new MenuFlyoutItem { Text = "Move Tab to New Window" };
        detach.IsEnabled = manager.Tabs.Count > 1;
        detach.Click += (_, _) => requestDetachToNewWindow(tab);
        flyout.Items.Add(detach);

        // "Move Tab to Zone" opens a visual picker for Snap Layouts
        // zones. Only shown when both snap callbacks are wired and
        // there is more than one tab (detaching the last tab is a no-op).
        MenuFlyoutItem? moveToZone = null;
        if (getSnapSource is not null && detachWithZone is not null)
        {
            moveToZone = new MenuFlyoutItem { Text = "Move Tab to Zone" };
            moveToZone.IsEnabled = manager.Tabs.Count > 1;
            moveToZone.Click += (_, _) =>
            {
                var target = flyout.Target;
                if (target?.XamlRoot is null) return;

                var source = getSnapSource();
                var picker = new SnapZonePicker();
                picker.Render(source.WorkAreaWidth, source.WorkAreaHeight);

                var pickerFlyout = new Flyout
                {
                    Content = picker,
                    Placement = FlyoutPlacementMode.Bottom,
                };

                picker.ZoneSelected += (_, zone) =>
                {
                    pickerFlyout.Hide();
                    detachWithZone(tab, zone);
                };

                pickerFlyout.ShowAt(target);
            };
            flyout.Items.Add(moveToZone);
        }

        // Re-evaluate IsEnabled on each open so tabs closed after
        // the flyout was built (but before the user right-clicked)
        // are reflected in the grey state. Matches Windows Terminal.
        flyout.Opening += (_, _) =>
        {
            detach.IsEnabled = manager.Tabs.Count > 1;
            if (moveToZone is not null)
                moveToZone.IsEnabled = manager.Tabs.Count > 1;
        };

        flyout.Items.Add(new MenuFlyoutSeparator());

        // "Tab Color..." opens a secondary Flyout anchored to the
        // right-clicked TabViewItem. We use a plain MenuFlyoutItem
        // (not MenuFlyoutSubItem) because the swatch grid needs a
        // real Flyout host to avoid MenuFlyoutItem hit-testing
        // quirks on WinAppSDK 1.6.
        var colorPick = new MenuFlyoutItem { Text = "Tab Color..." };
        colorPick.Click += (_, _) =>
        {
            var target = flyout.Target as FrameworkElement;
            if (target is null) return;
            ShowColorPicker(target, tab);
        };
        flyout.Items.Add(colorPick);

        return flyout;
    }

    private static void ShowColorPicker(FrameworkElement anchor, TabModel tab)
    {
        // Build the secondary flyout fresh each invocation. Cheap, and
        // avoids any stale selection-ring state from the previous
        // right-click.
        var picker = new TabColorPalettePicker(tab.Color);
        var subFlyout = new Flyout
        {
            Content = picker,
            Placement = FlyoutPlacementMode.Bottom,
            ShouldConstrainToRootBounds = true,
        };

        picker.ColorSelected += (_, color) =>
        {
            tab.Color = color;
            subFlyout.Hide();
        };

        subFlyout.ShowAt(anchor);
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
