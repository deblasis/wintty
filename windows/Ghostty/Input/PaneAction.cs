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
    SplitVertical = 0,
    SplitHorizontal = 1,
    ClosePane = 2, // legacy: pane-only close, no chord points to it after tabs landed
    FocusLeft = 3,
    FocusRight = 4,
    FocusUp = 5,
    FocusDown = 6,
    EqualizeSplits = 7,
    ToggleSplitZoom = 8,
    ToggleFullscreen = 9,

    // Tab operations (this PR)
    NewTab = 10,
    CloseActiveProgressive = 11, // pane -> tab -> window with confirmation
    NextTab = 12,
    PrevTab = 13,
    JumpTab1 = 14, JumpTab2 = 15, JumpTab3 = 16, JumpTab4 = 17,
    JumpTab5 = 18, JumpTab6 = 19, JumpTab7 = 20, JumpTab8 = 21,
    JumpTabLast = 22,
    MoveTabRight = 23,
    MoveTabLeft = 24,
    ToggleVerticalTabsPinned = 25,
    ToggleTabLayout = 26,
    ToggleCommandPalette = 27,
}
