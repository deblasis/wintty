using System;
using Ghostty.Core.Input;
using Ghostty.Core.Panes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Panes;

/// <summary>
/// Translates the pure <see cref="PaneContextMenuModel"/> into a WinUI
/// <see cref="MenuFlyout"/> and wires each command to its dispatch. Mirrors
/// <see cref="Ghostty.Tabs.TabContextMenuBuilder"/>: a thin, logic-free
/// translation so the tested Core model carries the behavior.
/// </summary>
internal static class PaneContextMenuBuilder
{
    public static MenuFlyout Build(
        Action<PaneAction> invokePaneAction,
        Action<string> invokeBindingAction,
        Func<bool> hasSelection,
        Func<bool> isZoomed,
        Action promptTabTitle,
        Action promptTerminalTitle)
    {
        var flyout = new MenuFlyout();
        Populate(flyout, invokePaneAction, invokeBindingAction,
            hasSelection(), isZoomed(),
            promptTabTitle, promptTerminalTitle);

        // Rebuild on each open so Copy's enabled state and the Zoom icon
        // reflect current selection/zoom. Matches TabContextMenuBuilder's
        // Opening re-check.
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            Populate(flyout, invokePaneAction, invokeBindingAction,
                hasSelection(), isZoomed(),
                promptTabTitle, promptTerminalTitle);
        };

        return flyout;
    }

    private static void Populate(
        MenuFlyout flyout,
        Action<PaneAction> invokePaneAction,
        Action<string> invokeBindingAction,
        bool hasSelection,
        bool isZoomed,
        Action promptTabTitle,
        Action promptTerminalTitle)
    {
        foreach (var item in PaneContextMenuModel.Build(hasSelection, isZoomed))
        {
            if (item.Kind == PaneMenuItemKind.Separator)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                continue;
            }

            var menuItem = new MenuFlyoutItem
            {
                Text = item.Label,
                IsEnabled = item.IsEnabled,
            };
            menuItem.Click += (_, _) => Dispatch(item.Command,
                invokePaneAction, invokeBindingAction,
                promptTabTitle, promptTerminalTitle);
            ApplyIcon(menuItem, item.Icon);
            flyout.Items.Add(menuItem);
        }
    }

    private static void ApplyIcon(MenuFlyoutItem item, string? glyph)
    {
        if (string.IsNullOrEmpty(glyph)) return;
        var fontIcon = new FontIcon { Glyph = glyph };
        // Pin to the symbol font the rest of the app uses; FontIcon's default
        // family is not guaranteed to be the symbol set on every machine.
        if (Application.Current.Resources.TryGetValue("SymbolThemeFontFamily", out var ff)
            && ff is FontFamily family)
            fontIcon.FontFamily = family;
        item.Icon = fontIcon;
    }

    private static void Dispatch(
        PaneMenuCommand command,
        Action<PaneAction> invokePaneAction,
        Action<string> invokeBindingAction,
        Action promptTabTitle,
        Action promptTerminalTitle)
    {
        switch (command)
        {
            case PaneMenuCommand.Copy: invokeBindingAction("copy_to_clipboard"); break;
            case PaneMenuCommand.Paste: invokeBindingAction("paste_from_clipboard"); break;
            case PaneMenuCommand.SelectAll: invokeBindingAction("select_all"); break;
            case PaneMenuCommand.ResetTerminal: invokeBindingAction("reset"); break;

            case PaneMenuCommand.SplitRight: invokePaneAction(PaneAction.SplitVertical); break;
            case PaneMenuCommand.SplitDown: invokePaneAction(PaneAction.SplitHorizontal); break;
            case PaneMenuCommand.ZoomPane: invokePaneAction(PaneAction.ToggleSplitZoom); break;
            case PaneMenuCommand.CommandPalette: invokePaneAction(PaneAction.ToggleCommandPalette); break;
            case PaneMenuCommand.ToggleInspector: invokePaneAction(PaneAction.ToggleInspector); break;

            case PaneMenuCommand.ChangeTabTitle: promptTabTitle(); break;
            case PaneMenuCommand.ChangeTerminalTitle: promptTerminalTitle(); break;

            default: throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }
    }
}
