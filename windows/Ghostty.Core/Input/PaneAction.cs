namespace Ghostty.Core.Input;

/// <summary>
/// The vocabulary of shell-related actions a key binding can invoke.
/// Routed through PaneActionRouter.Invoke in the WinUI assembly
/// (KeyBindings and PaneActionRouter stay in the Ghostty assembly
/// because they depend on Windows.System.VirtualKey and on TabManager
/// respectively).
///
/// Adding a new bindable action is two changes: add a value here,
/// add a case in PaneActionRouter.Invoke. Bindings in KeyBindings can
/// then reference it.
/// </summary>
public enum PaneAction
{
    // Pane operations (#163)
    SplitVertical = 0,
    SplitHorizontal = 1,
    ClosePane = 2,
    FocusLeft = 3,
    FocusRight = 4,
    FocusUp = 5,
    FocusDown = 6,
    EqualizeSplits = 7,
    ToggleSplitZoom = 8,
    ToggleFullscreen = 9,

    // Tab operations (PR 4)
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

    // Profiles (PR 5). One concrete member per slot mirrors JumpTab1..JumpTab8.
    // KeyBindings.Default maps Ctrl+Shift+Number1..Number9 -> these.
    OpenProfile1 = 28,
    OpenProfile2 = 29,
    OpenProfile3 = 30,
    OpenProfile4 = 31,
    OpenProfile5 = 32,
    OpenProfile6 = 33,
    OpenProfile7 = 34,
    OpenProfile8 = 35,
    OpenProfile9 = 36,
}
