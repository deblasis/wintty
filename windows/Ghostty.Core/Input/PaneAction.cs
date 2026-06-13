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
    // KeyBindings.WindowsOnly maps Ctrl+Shift+Number1..Number9 -> these.
    OpenProfile1 = 28,
    OpenProfile2 = 29,
    OpenProfile3 = 30,
    OpenProfile4 = 31,
    OpenProfile5 = 32,
    OpenProfile6 = 33,
    OpenProfile7 = 34,
    OpenProfile8 = 35,
    OpenProfile9 = 36,

    // Scrollback navigation. Router dispatches via libghostty bindings;
    // see PaneActionRouter.Invoke for the wiring.
    ScrollToTop = 37,
    ScrollToBottom = 38,

    // Open the in-pane scrollback search bar. Routed via event to
    // MainWindow which forwards to the active leaf's TerminalControl.
    OpenSearch = 39,

    // OSC 133 prompt navigation. Router dispatches the
    // jump_to_prompt:N libghostty binding. Negative N is previous,
    // positive N is next.
    JumpToPreviousPrompt = 40,
    JumpToNextPrompt = 41,

    // Tree-order pane navigation. Complements the spatial focus
    // (FocusLeft/Right/Up/Down): these always advance even when no
    // leaf lies in any spatial direction, matching Ghostty's
    // goto_split:previous and goto_split:next bindings.
    GotoSplitPrevious = 42,
    GotoSplitNext = 43,

    // Keyboard-driven splitter resize. Each chord nudges the
    // nearest matching-orientation divider; direction is which way
    // the divider moves. Maps to Ghostty's resize_split:DIRECTION
    // binding.
    ResizeSplitUp = 44,
    ResizeSplitDown = 45,
    ResizeSplitLeft = 46,
    ResizeSplitRight = 47,

    // Toggle the singleton quake / drop-down window. Routed via an
    // App-level singleton rather than the active leaf because the
    // quake window may not even be open when the action fires.
    ToggleQuickTerminal = 48,

    // Open the read-only keyboard-shortcuts cheat sheet. Routed via event to
    // MainWindow, which shows the cheat sheet dialog (needs an XamlRoot/HWND).
    ShowKeybindCheatsheet = 49,

    // Pane undo/redo (#166). Apprt-only: libghostty has no Windows undo
    // implementation, so these are matched in KeyBindings.WindowsOnly and
    // dispatched to PaneHost.Undo/Redo. Time-bounded (~5s) snapshot stack.
    Undo = 50,
    Redo = 51,

    // Open the branded About window (app name, version/build/commit, links,
    // license). Apprt-only: routed via event to MainWindow, which opens one
    // About window per window (reused if already open). Mirrors
    // ShowKeybindCheatsheet (event-only, needs a window, no libghostty action).
    ShowAbout = 52,
}
