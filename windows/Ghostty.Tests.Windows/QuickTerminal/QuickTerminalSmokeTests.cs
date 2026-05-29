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

    // PR C adds three config-driven behaviours on top of the foundation
    // above. Each needs a real HWND + compositor + the user watching, so
    // they stay manual. Validate by editing the wintty config and
    // reloading (Ctrl+Shift+R) between each:
    //
    //  Animation (quick-terminal-animation-duration, default 0.2):
    //   - default: Ctrl+` slides the window down from the top over ~0.2s,
    //     and slides back up on the second press.
    //   - quick-terminal-position = bottom + duration = 0.4 -> slides up
    //     from the bottom over ~0.4s.
    //   - quick-terminal-position = center + duration = 0.3 -> fades in
    //     centered (no slide).
    //   - duration = 0 -> appears/disappears instantly (PR B behaviour).
    //   - Rapidly mashing Ctrl+` must never leave the window stuck
    //     half-on-screen, and must not hide-after-show / show-after-hide
    //     (the animator's monotonic token guards superseded completions).
    //
    //  Autohide (quick-terminal-autohide, default true):
    //   - default: summon the window, then click another app -> it hides.
    //   - quick-terminal-autohide = false -> it stays open when clicked
    //     away from.
    //
    //  Key override (quick-terminal-key, default ctrl+backquote):
    //   - quick-terminal-key = alt+space -> after reload, Ctrl+` no longer
    //     toggles; Alt+Space does.
    //   - quick-terminal-key = ctrl+nonsense (invalid) -> after reload,
    //     falls back to Ctrl+` (a parse-fallback the log notes).
    [Fact(Skip = "Manual smoke; compositor slide/fade over quick-terminal-animation-duration, plus 0=instant and rapid-toggle stability.")]
    public void Animation_SlidesOrFades_FromConfiguredEdge()
    {
    }

    [Fact(Skip = "Manual smoke; quick-terminal-autohide=true hides on focus loss, =false keeps the window open.")]
    public void Autohide_HidesOnFocusLoss_WhenEnabled()
    {
    }

    [Fact(Skip = "Manual smoke; quick-terminal-key rebinds the global hotkey on reload; invalid values fall back to Ctrl+`.")]
    public void KeyOverride_RebindsGlobalHotkey_OnReload()
    {
    }
}
