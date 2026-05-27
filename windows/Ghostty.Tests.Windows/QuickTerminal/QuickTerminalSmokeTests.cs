using Xunit;

namespace Ghostty.Tests.Windows.QuickTerminal;

public class QuickTerminalSmokeTests
{
    // Manual smoke spec for the quake / drop-down terminal.
    //
    // Automated coverage lives elsewhere:
    //  - GhosttyActionsLayoutTests pins the libghostty action
    //    ordinal (ToggleQuickTerminal = 10).
    //  - PaneAction / KeyBindings / BuiltInCommandSource are
    //    table-extension changes already covered by build-time
    //    enum exhaustiveness in the router.
    //
    // The pieces this spec exercises -- global hotkey arrival on
    // WM_HOTKEY, AppWindow.Show/Hide, MoveAndResize positioning,
    // and the toolwindow Ex-style suppressing the taskbar /
    // Alt+Tab presence -- all require a real HWND on a real
    // dispatcher and the user pressing the chord from a
    // foreground app that is not wintty. Same blocker that keeps
    // ProfileChordSmokeTests / SearchBarSmokeTests un-automated.
    //
    // To validate by hand:
    //  1. Launch wintty (`just run-win`).
    //  2. Switch focus to any other app (Explorer, browser).
    //  3. Press Ctrl+` (backtick). The quake window slides into
    //     the top half of the primary monitor and takes focus.
    //  4. Press Ctrl+` again. The window hides.
    //  5. Alt+Tab does NOT show the quake window in the switcher.
    //  6. The quake window has no taskbar icon.
    //  7. Splits (Ctrl+Shift+D / E), tabs (Ctrl+Shift+T), command
    //     palette (Ctrl+Shift+P), and profile chords all work
    //     inside the quake window the same as in regular windows.
    //  8. Clicking the title bar X hides the window instead of
    //     closing wintty.
    //  9. Exiting wintty (regular window close-all-windows path)
    //     also unregisters the hotkey: re-pressing Ctrl+` after
    //     exit does nothing.
    [Fact(Skip = "Manual smoke; WindowsGlobalHotKey requires a real HWND + foreground-app focus switch.")]
    public void CtrlBacktick_SummonsAndDismissesQuakeWindow()
    {
    }
}
