# Manual smoke tests

## Quick terminal

Automated coverage lives elsewhere: GhosttyActionsLayoutTests pins the
libghostty action ordinal (ToggleQuickTerminal = 10); PaneAction /
KeyBindings / BuiltInCommandSource are table-extension changes already
covered by build-time enum exhaustiveness in the router. The pieces below
-- global hotkey arrival on WM_HOTKEY, AppWindow.Show/Hide,
MoveAndResize positioning, and the toolwindow Ex-style suppressing the
taskbar / Alt+Tab presence -- all require a real HWND on a real dispatcher
and the user pressing the chord from a foreground app that is not wintty.

To validate by hand:

1. Launch wintty (`just run-win`).
2. Switch focus to any other app (Explorer, browser).
3. Press Ctrl+` (backtick). The quake window slides into the top half of
   the primary monitor and takes focus.
4. Press Ctrl+` again. The window hides.
5. Alt+Tab does NOT show the quake window in the switcher.
6. The quake window has no taskbar icon.
7. Splits (Alt+Shift+= / Alt+Shift+-, the curated split chords), tabs
   (Ctrl+T and Ctrl+Shift+T), command palette (Ctrl+Shift+P), and profile
   chords all work inside the quake window the same as in regular windows.
8. Clicking the title bar X hides the window instead of closing wintty.
9. Exiting wintty (regular window close-all-windows path) also unregisters
   the hotkey: re-pressing Ctrl+` after exit does nothing.

### Animation, autohide, key override

Three config-driven behaviours on top of the summon/dismiss foundation
above. Each needs a real HWND + compositor + the user watching. Validate
by editing the wintty config and reloading (Ctrl+Shift+R) between each.

Animation (quick-terminal-animation-duration, default 0.2):

- default: Ctrl+` slides the window down from the top over ~0.2s, and
  slides back up on the second press.
- quick-terminal-position = bottom + duration = 0.4 -> slides up from the
  bottom over ~0.4s.
- quick-terminal-position = center + duration = 0.3 -> fades in centered
  (no slide).
- duration = 0 -> appears/disappears instantly.
- Rapidly mashing Ctrl+` must never leave the window stuck half-on-screen,
  and must not hide-after-show / show-after-hide (the animator's monotonic
  token guards superseded completions).

Autohide (quick-terminal-autohide, default true):

- default: summon the window, then click another app -> it hides.
- quick-terminal-autohide = false -> it stays open when clicked away from.

Key override (quick-terminal-key, default ctrl+backquote):

- quick-terminal-key = alt+space -> after reload, Ctrl+` no longer toggles;
  Alt+Space does.
- quick-terminal-key = ctrl+nonsense (invalid) -> after reload, falls back
  to Ctrl+` (a parse-fallback the log notes).

## Search bar

SearchState behaviour is covered exhaustively by SearchStateTests in
Ghostty.Tests (pure-logic xUnit, no host). The pieces exercised below --
keyboard accelerator routing into OpenSearch(), Visibility-toggle, debounce
timer ticks against the real DispatcherQueue, and the libghostty round-trip
for needle/total/selected updates -- all require a real MainWindow on a
dispatcher with a live libghostty surface.

To validate by hand once the binary lands:

1. Open wintty, run `seq 1 5000` to fill the scrollback.
2. Press Ctrl+Shift+F. The search bar appears at the top-right, the needle
   textbox has focus, and any prior text is pre-selected.
3. Type "500". After ~80ms the counter shows "1 of 1" and the match in the
   scrollback highlights.
4. Backspace then type "1". The counter updates to e.g. "1 of 271" (one
   per line containing "1" in the range).
5. Press Enter. The "selected" index advances by one and the counter shows
   "2 of 271".
6. Press Shift+Enter. The index moves back to 1.
7. Press Esc. The search bar collapses; focus returns to the terminal
   surface so typing reaches the shell.
8. Press Ctrl+Shift+F again. The previous needle is still in the box, and
   the search runs again for it: the counter repopulates with the live
   count and the matches highlight, without waiting for the needle to be
   retyped. Enter steps matches immediately.
9. Split the pane, then repeat steps 1-3 in the surviving pane. Typing
   must still start a search: WinUI reparents the control on a split, and
   a debounce handler that does not come back leaves the needle box inert.

### Counter updates from libghostty

libghostty fires GHOSTTY_ACTION_SEARCH_TOTAL and
GHOSTTY_ACTION_SEARCH_SELECTED as the user navigates; the counter updates
depend on GhosttyHost.OnAction routing those payloads to
TerminalControl.OnSearchTotalChanged / OnSearchSelectedChanged on the UI
thread. The ordinals (61 / 62) and struct layouts are pinned by
GhosttyActionsLayoutTests; the live wiring needs a real surface to verify
end-to-end, which is what the steps above cover.

### Keystroke isolation while the bar is open

The search bar's controls are children of TerminalControl's visual tree,
so their KeyDown / CharacterReceived routed events bubble to the
TerminalControl handlers that forward input to libghostty. Without a
guard, every character typed into the needle also reaches the shell.
TerminalControl.OnKeyDown / OnKeyUp / OnCharacterReceived gate on
SearchBarControl.ContainsFocus (live focus anywhere in the bar, not the
bar's open state) so the leak is closed without killing terminal typing
when the bar is left visible and the user clicks back into the surface.

To validate by hand once the binary lands:

1. Open wintty on a pwsh pane.
2. Press Ctrl+Shift+F, then type "hello". The text appears ONLY in the
   search needle; nothing is echoed at the shell prompt.
3. Press Esc. Focus returns to the terminal; type "echo hi" and confirm it
   reaches the shell normally.
4. Press Ctrl+Shift+F again, then click back into the terminal surface
   while the bar stays visible. Typing now reaches the shell (the needle
   no longer holds focus), confirming the gate keys off focus rather than
   the bar's open state.

## Resize overlay

ResizeOverlayState behaviour (dedup, mode gating, SizeText) is covered
exhaustively by ResizeOverlayStateTests in Ghostty.Tests (pure-logic
xUnit, no host). The piece guarded below lives in ResizeOverlayControl and
cannot be automated: it needs a real DispatcherQueue (for the one-shot
hide timer to tick) and a live visual tree that reparents on split.

REGRESSION (fixed): ResizeOverlayControl used to drop its hide timer's
Tick handler on Unloaded. WinUI 3 raises Unloaded on every reparent (each
split / rebuild), not only on teardown, so after the first split every
pre-existing pane's pill lost its auto-hide: the restarted timer fired
into a detached delegate and the pill stayed stuck on screen. Only the
newest, never-reparented pane still hid. The fix keeps the timer + Tick
handler wired for the control's whole lifetime (the cycle is
self-contained and GC-collected together).

To validate by hand once the binary lands:

1. Open wintty (single pane). Drag the window border. A "cols x rows" pill
   appears and auto-hides ~750ms after the drag settles.
2. Split into three or more panes (e.g. Ctrl+Shift+E then Ctrl+Shift+O) so
   the pre-existing panes reparent at least once.
3. Drag the window border so ALL panes resize at once. Every pane's pill
   appears.
4. Stop dragging and wait ~1 second WITHOUT touching the window. EVERY
   pane's pill must disappear -- not just one. Before the fix, one pill
   (the most recently created pane) hid and the rest stayed stuck visible.

## Profile chord

Slot-to-id resolution is already covered exhaustively by
ProfileSlotResolverTests in Ghostty.Tests. The piece this procedure
exercises -- the WinUI keyboard accelerator into PaneAction routing --
requires a real MainWindow on a dispatcher.

To validate by hand: with 3 profiles ("a","b","c") in the config, press
Ctrl+Shift+2 and confirm a new tab opens for profile "b".

## Profiles settings page

Visible/hidden ordering and per-row hide-marker parsing are already
covered by ProfileOrderResolverTests, ProfileRegistryTests,
ProfileHiddenKeyTests, and ProfileSourceParserHiddenIdsTests in
Ghostty.Tests. What this procedure adds is the SettingsCard rendering and
the ToggleSwitch into IConfigFileEditor write path inside a real
SettingsWindow on a dispatcher.

To validate by hand:

1. Open Settings -> Profiles with 2 visible + 1 hidden profile.
2. Confirm 3 rows render in registry order with toggle states off/off/on.
3. Toggle the first row on; confirm the config file gains
   'profile.<id>.hidden = true'.
4. Toggle the third row off; confirm the line is removed via RemoveValue
   (not rewritten as 'hidden = false').

## Tab strip

### New-tab split button

Not automated: hosting a real MainWindow inside Ghostty.Tests.Windows
would require Microsoft.WindowsAppSDK + a XAML dispatcher host + a
ProjectReference to Ghostty.csproj, and MainWindow's ctor calls into
libghostty via GhosttyHost. Ghostty.Tests.Windows intentionally references
only Ghostty.Core, so neither dependency is acceptable there.

To validate by hand:

1. Run the app.
2. Confirm the new-tab control is hosted in the tab footer and the legacy
   add-button is hidden.

### Title-bar passthrough

Same architectural blocker as the split button: verifying the SplitButton
bounds sit outside the title-bar drag region requires a live MainWindow on
a dispatcher, which is outside the contract of Ghostty.Tests.Windows
(Core-only references; no WinUI host).

To validate by hand:

1. Run the app.
2. Drag the window from the area just left of the new-tab split button.
   The drag should succeed (the spacer cell is the title-bar element).
3. Click the same split button. It should activate the new-tab dropdown
   without dragging the window.
