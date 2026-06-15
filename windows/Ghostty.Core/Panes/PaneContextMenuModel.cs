using System.Collections.Generic;

namespace Ghostty.Core.Panes;

/// <summary>Whether a menu entry is an actionable item or a separator.</summary>
public enum PaneMenuItemKind
{
    Action,
    Separator,
}

/// <summary>
/// The commands the pane context menu can issue. Translated to a concrete
/// dispatch (PaneAction or libghostty binding action or title dialog) by the
/// WinUI <c>PaneContextMenuBuilder</c>. Kept here, in Core, so the menu's
/// shape and gating are testable without WinUI.
/// </summary>
public enum PaneMenuCommand
{
    /// <summary>Sentinel used only for separator rows; never dispatched as an action.</summary>
    None,
    Copy,
    Paste,
    SelectAll,
    SplitRight,
    SplitDown,
    ZoomPane,
    CommandPalette,
    ResetTerminal,
    ToggleInspector,
    ChangeTabTitle,
    ChangeTerminalTitle,
}

/// <summary>One row of the pane context menu.</summary>
public readonly record struct PaneMenuItem(
    PaneMenuItemKind Kind,
    PaneMenuCommand Command,
    string Label,
    bool IsEnabled,
    bool IsChecked,
    string? Icon);

/// <summary>
/// Builds the ordered pane context-menu model. Mirrors the macOS surface
/// context menu (Copy/Paste/Select All, splits, Reset/Inspector, titles) plus
/// Zoom Pane and Command Palette. The Windows pane model splits two ways only,
/// so there is no Split Left/Up; read-only mode is not implemented on Windows
/// so it is omitted.
/// </summary>
public static class PaneContextMenuModel
{
    private static PaneMenuItem Action(PaneMenuCommand command, string label, string icon,
        bool isEnabled = true, bool isChecked = false)
        => new(PaneMenuItemKind.Action, command, label, isEnabled, isChecked, icon);

    private static PaneMenuItem Separator()
        => new(PaneMenuItemKind.Separator, PaneMenuCommand.None, string.Empty, false, false, null);

    public static IReadOnlyList<PaneMenuItem> Build(bool hasSelection, bool isZoomed)
        => new[]
        {
            Action(PaneMenuCommand.Copy,                "Copy",                     "\uE8C8", isEnabled: hasSelection),
            Action(PaneMenuCommand.Paste,               "Paste",                    "\uE77F"),
            Action(PaneMenuCommand.SelectAll,           "Select All",               "\uE8B3"),
            Separator(),
            Action(PaneMenuCommand.SplitRight,          "Split Right",              "\uF57E"),
            Action(PaneMenuCommand.SplitDown,           "Split Down",               "\uF57E"),
            Action(PaneMenuCommand.ZoomPane,            "Zoom Pane",                "\uE740", isChecked: isZoomed),
            Separator(),
            Action(PaneMenuCommand.CommandPalette,      "Command Palette",          "\uE756"),
            Action(PaneMenuCommand.ResetTerminal,       "Reset Terminal",           "\uE777"),
            Action(PaneMenuCommand.ToggleInspector,     "Toggle Inspector",         "\uEBE8"),
            Separator(),
            Action(PaneMenuCommand.ChangeTabTitle,      "Change Tab Title...",      "\uE70F"),
            Action(PaneMenuCommand.ChangeTerminalTitle, "Change Terminal Title...", "\uE8AC"),
        };
}
