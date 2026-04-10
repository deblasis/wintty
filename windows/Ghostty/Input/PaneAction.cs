namespace Ghostty.Input;

/// <summary>
/// The vocabulary of shell-related actions a key binding can invoke.
/// Covers panes (PR #163) and tabs (this PR). Routed through
/// <see cref="PaneActionRouter.Invoke"/>.
///
/// Adding a new bindable action is two changes: add a value here,
/// add a case in <see cref="PaneActionRouter.Invoke"/>. Bindings in
/// <see cref="KeyBindings"/> can then reference it.
/// </summary>
internal enum PaneAction
{
    // Pane operations (#163)
    SplitVertical,
    SplitHorizontal,
    ClosePane, // legacy: pane-only close, no chord points to it after tabs landed
    FocusLeft,
    FocusRight,
    FocusUp,
    FocusDown,

    // Tab operations (this PR)
    NewTab,
    CloseActiveProgressive, // pane -> tab -> window with confirmation
    NextTab,
    PrevTab,
    JumpTab1, JumpTab2, JumpTab3, JumpTab4,
    JumpTab5, JumpTab6, JumpTab7, JumpTab8,
    JumpTabLast,
    MoveTabRight,
    MoveTabLeft,
    ToggleVerticalTabsPinned,
    ToggleTabLayout,
    ToggleCommandPalette,
}
