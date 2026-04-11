using Ghostty.Core.Tabs;
using Ghostty.Input;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Tabs;

/// <summary>
/// Builds the right-click menu for empty areas of the tab strip (both
/// horizontal TabView background and vertical sidebar background).
/// Mirrors the static-class pattern of TabContextMenuBuilder.
///
/// Items intentionally kept short: strip-level actions only. Per-tab
/// actions (close, rename, duplicate) live in TabContextMenuBuilder
/// and are reached by right-clicking a tab item itself.
/// </summary>
internal static class StripContextMenuBuilder
{
    public static MenuFlyout Build(
        TabManager manager,
        PaneActionRouter router,
        bool isVertical,
        bool isSidebarCollapsed = false)
    {
        var flyout = new MenuFlyout();

        var newTab = new MenuFlyoutItem
        {
            Text = "New Tab",
            KeyboardAcceleratorTextOverride = "Ctrl+T",
        };
        newTab.Click += (_, _) => manager.NewTab();
        flyout.Items.Add(newTab);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Label flips to match the destination state so users see
        // where they will end up; matches TabContextMenuBuilder's
        // pattern.
        var switchLayout = new MenuFlyoutItem
        {
            Text = isVertical ? "Switch to horizontal tabs" : "Switch to vertical tabs",
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+,",
        };
        switchLayout.Click += (_, _) => router.RequestToggleTabLayout();
        flyout.Items.Add(switchLayout);

        if (isVertical)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var collapse = new MenuFlyoutItem
            {
                Text = isSidebarCollapsed ? "Expand sidebar" : "Collapse sidebar",
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+Space",
            };
            collapse.Click += (_, _) => router.RequestToggleSidebarCollapse();
            flyout.Items.Add(collapse);
        }

        return flyout;
    }
}
