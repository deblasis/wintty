using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ghostty.Core.Tabs;
using Ghostty.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

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
        Action toggleTabLayout,
        Action<TabModel, bool> requestPin,
        Action<TabModel> requestDuplicate,
        Action<TabModel> requestNewGroupWithTab,
        Action<TabModel, TabGroup> requestAddToGroup,
        Action<TabModel> requestRemoveFromGroup,
        bool isVertical = false,
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

        // One implementation for both hosts: the label is the only thing
        // that flips here. The pin itself routes through the caller's hook
        // (the router's RequestPin) so a menu pin announces exactly like a
        // palette one; the relocation it triggers is still the manager's
        // (SetPinned moves the tab to the zone boundary), so the strips
        // pick it up through their normal sync paths.
        var pin = new MenuFlyoutItem
        {
            Text = tab.IsPinned ? "Unpin Tab" : "Pin Tab",
        };
        pin.Click += (_, _) => requestPin(tab, !tab.IsPinned);
        flyout.Items.Add(pin);

        // The group block rides build-time state only, unlike the pin
        // label's Opening pass: the hosts build this flyout fresh per
        // right-click, membership cannot move inside one interaction, and
        // a click that DID move it closes the menu.
        if (!tab.IsPinned)
        {
            if (tab.Group is null)
            {
                var newGroup = new MenuFlyoutItem { Text = "New Group With Tab" };
                newGroup.Click += (_, _) => requestNewGroupWithTab(tab);
                flyout.Items.Add(newGroup);
            }
            else
            {
                var removeFromGroup = new MenuFlyoutItem { Text = "Remove from Group" };
                removeFromGroup.Click += (_, _) => requestRemoveFromGroup(tab);
                flyout.Items.Add(removeFromGroup);
            }

            // One run per registered group, the tab's own excluded: joining
            // it again is the no-op the router refuses, so offering it
            // would menu-speak a dead entry. No groups besides the tab's
            // own means nothing to offer and the submenu stays hidden.
            var others = new List<TabGroup>();
            foreach (var g in manager.Groups)
                if (!ReferenceEquals(g, tab.Group))
                    others.Add(g);
            if (others.Count > 0)
            {
                var addToGroup = new MenuFlyoutSubItem { Text = "Add to Group" };
                foreach (var g in others)
                {
                    var target = g;
                    var join = new MenuFlyoutItem { Text = g.Title };
                    join.Click += (_, _) => requestAddToGroup(tab, target);
                    addToGroup.Items.Add(join);
                }
                flyout.Items.Add(addToGroup);
            }
        }

        // Duplicate is a clone of THIS tab -- same shells, same split
        // arrangement, each pane respawned at its source pane's
        // last-reported directory -- so it routes through the caller's
        // hook the way pin does. It used to be manager.NewTab() here,
        // which copied nothing; the clone itself lives at the window
        // layer, which owns the capture/restore pair.
        var dup = new MenuFlyoutItem { Text = "Duplicate Tab" };
        dup.Click += (_, _) => requestDuplicate(tab);
        flyout.Items.Add(dup);

        // The directory the shell reported, in the two forms a person wants
        // it: on the clipboard, and open in File Explorer. Its own group,
        // because these are about the shell's directory rather than the
        // tab. Greyed until the model has one it will act on: reported,
        // plain text, and a directory the spawn policy accepts, since the
        // path is bytes off the pty.
        flyout.Items.Add(new MenuFlyoutSeparator());

        var copyCwd = new MenuFlyoutItem
        {
            Text = "Copy Working Directory",
            IsEnabled = tab.ActionableCwd is not null,
        };
        copyCwd.Click += (_, _) =>
        {
            if (tab.ActionableCwd is not { } path) return;
            var package = new DataPackage();
            package.SetText(path);
            // Another process can hold the clipboard open. The copy is
            // then lost, which beats the unhandled exception that would
            // take the window down.
            try { WinClipboard.SetContent(package); }
            catch (COMException) { }
        };
        flyout.Items.Add(copyCwd);

        var openCwd = new MenuFlyoutItem
        {
            Text = "Open in File Explorer",
            IsEnabled = tab.ActionableCwd is not null,
        };
        openCwd.Click += async (_, _) =>
        {
            if (tab.ActionableCwd is not { } path || !Path.IsPathFullyQualified(path)) return;
            // Off the UI thread: a UNC directory -- a stopped WSL distro,
            // a dead share -- can make the existence check block for
            // seconds.
            var isDirectory = await Task.Run(() => Directory.Exists(path));
            if (!isDirectory) return;
            // The folder-only launcher, and only after the directory check:
            // Explorer handed a file path runs that file's handler. Best
            // effort beyond that; a launch that fails leaves nothing to
            // recover, and an exception escaping an async void handler
            // would take the window down.
            try { await Launcher.LaunchFolderPathAsync(path); }
            catch (Exception) { }
        };
        flyout.Items.Add(openCwd);

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
        // The pin item's LABEL rides the same pass: a flyout can be
        // built while the tab is unpinned and opened after a drag
        // pinned it, and a stale "Pin Tab" that unpins would lie.
        flyout.Opening += (_, _) =>
        {
            detach.IsEnabled = manager.Tabs.Count > 1;
            if (moveToZone is not null)
                moveToZone.IsEnabled = manager.Tabs.Count > 1;
            pin.Text = tab.IsPinned ? "Unpin Tab" : "Pin Tab";
            // The directory items grey until the tab has one it will act
            // on, and a shell can report one any time after the flyout was
            // built.
            copyCwd.IsEnabled = tab.ActionableCwd is not null;
            openCwd.IsEnabled = tab.ActionableCwd is not null;
        };

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Same switch as StripContextMenuBuilder. Empty-strip right-click
        // vanishes once tabs fill the bar; the per-tab menu has to carry it.
        var switchLayout = new MenuFlyoutItem
        {
            Text = isVertical ? "Switch to horizontal tabs" : "Switch to vertical tabs",
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+,",
        };
        switchLayout.Click += (_, _) => toggleTabLayout();
        flyout.Items.Add(switchLayout);

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

    /// <summary>
    /// The header row's right-click menu (vertical only; horizontal grows
    /// a chip equivalent in PR 6). Rename and color are dialog ops like
    /// the per-tab menu's: the item hosts the dialog and hands the result
    /// to the router, so a commanded rename announces and a drag-performed
    /// title change stays silent. Collapse routes through the router so
    /// the command announces and the strip re-homes focus under the
    /// folding run; Close Group greys out exactly when the group's members
    /// are all the window's tabs, the same rule as Move Tab to New Window.
    /// Built fresh per right-click, so the collapse label reads the live
    /// bit -- but the label still re-evaluates on Opening, because the
    /// header can be toggled (chevron) between build and open.
    /// </summary>
    public static MenuFlyout BuildGroupMenu(
        TabManager manager,
        TabGroup group,
        DialogTracker dialogs,
        Action<TabGroup, bool> requestCollapseGroup,
        Action<TabGroup> requestDissolveGroup,
        Action<TabGroup> requestCloseGroup,
        Action<TabGroup, string?> requestRenameGroup,
        Action<TabGroup, TabColor> requestColorGroup)
    {
        var flyout = new MenuFlyout();

        var rename = new MenuFlyoutItem { Text = "Rename Group" };
        rename.Click += async (_, _) =>
        {
            var target = flyout.Target;
            if (target?.XamlRoot is null) return;
            var dlg = new RenameTabDialog(group.Title) { XamlRoot = target.XamlRoot };
            using (dialogs.Track(dlg))
            {
                var res = await dlg.ShowAsync();
                if (res == ContentDialogResult.Primary)
                    requestRenameGroup(group, dlg.Result);
            }
        };
        flyout.Items.Add(rename);

        var color = new MenuFlyoutItem { Text = "Group Color..." };
        color.Click += (_, _) =>
        {
            var target = flyout.Target as FrameworkElement;
            if (target is null) return;
            // allowNone: a group has no "no color" state, so None is not
            // offered here -- see TabGroup.Color.
            ShowColorPicker(target, group.Color, c => requestColorGroup(group, c),
                allowNone: false);
        };
        flyout.Items.Add(color);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var collapse = new MenuFlyoutItem
        {
            Text = group.IsCollapsed ? "Expand Group" : "Collapse Group",
        };
        collapse.Click += (_, _) => requestCollapseGroup(group, !group.IsCollapsed);
        flyout.Items.Add(collapse);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var dissolve = new MenuFlyoutItem { Text = "Dissolve Group" };
        dissolve.Click += (_, _) => requestDissolveGroup(group);
        flyout.Items.Add(dissolve);

        var close = new MenuFlyoutItem { Text = "Close Group" };
        close.Click += (_, _) => requestCloseGroup(group);
        flyout.Items.Add(close);

        flyout.Opening += (_, _) =>
        {
            collapse.Text = group.IsCollapsed ? "Expand Group" : "Collapse Group";
            close.IsEnabled = !manager.GroupHoldsEveryTab(group);
        };
        close.IsEnabled = !manager.GroupHoldsEveryTab(group);

        return flyout;
    }

    private static void ShowColorPicker(FrameworkElement anchor, TabModel tab)
        => ShowColorPicker(anchor, tab.Color, color => tab.Color = color, allowNone: true);

    private static void ShowColorPicker(
        FrameworkElement anchor, TabColor initial, Action<TabColor> apply, bool allowNone)
    {
        // Build the secondary flyout fresh each invocation. Cheap, and
        // avoids any stale selection-ring state from the previous
        // right-click.
        var picker = new TabColorPalettePicker(initial, allowNone);
        var subFlyout = new Flyout
        {
            Content = picker,
            Placement = FlyoutPlacementMode.Bottom,
            ShouldConstrainToRootBounds = true,
        };

        picker.ColorSelected += (_, color) =>
        {
            apply(color);
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
