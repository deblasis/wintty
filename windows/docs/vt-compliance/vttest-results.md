# vttest on Windows (ConPTY) -- host and results

[vttest](https://invisible-island.net/vttest/) is the interactive, *visual* VT
compliance tester (cursor movement, screen features, character sets, double-size
glyphs, VT52/VT102/VT220 behavior). Unlike esctest it writes no machine-readable
log -- you read the rendered screen. This page covers how the vttest host is set
up for the Windows port and records the per-section assessment.

For the non-visual, query-response half of VT compliance see
[`esctest-baseline.md`](esctest-baseline.md); for how ConPTY handles VT sequences
see [`conpty-reference.md`](conpty-reference.md).

## Host

vttest runs inside WSL and is hosted in Wintty over the same
`wsl.exe -> ConPTY -> libghostty` path the esctest harness uses. Native
MSYS2/MinGW vttest is optional and not required for this.

`apt-get install vttest` needs a sudo password (non-interactive), so vttest is
built from the upstream tarball to a per-user prefix instead -- gcc/make/termios
are already present in a default Ubuntu WSL image and the build needs no network.

```bash
# Inside WSL. With no argument it downloads the tarball (needs WSL network).
windows/scripts/vttest/build-vttest.sh
```

WSL often has no outbound network even when the Windows host is online (DNS
fails). In that case download the tarball on the Windows side and pass its
`/mnt/c/...` path:

```powershell
Invoke-WebRequest https://invisible-island.net/archives/vttest/vttest.tar.gz -OutFile C:\temp\vttest.tar.gz
wsl.exe -d Ubuntu-24.04 -- bash windows/scripts/vttest/build-vttest.sh /mnt/c/temp/vttest.tar.gz
```

The build produces a `~/vttest` symlink the runner targets.

```powershell
# Launch Wintty hosting vttest; prints the PID to stop when done.
windows/scripts/vttest/run-vttest.ps1 -WinttyExe <path-to-Wintty.exe>
```

vttest puts the tty in raw mode (termios), so its menus must be driven with real
keyboard input to the Wintty window -- stdin cannot be piped (a pipe is not a
tty). Screenshot each section and assess the render.

## Host validation (clean `windows`-HEAD build)

Confirmed end to end against a fresh build of the `windows` branch: Wintty spawns
`wsl.exe -> vttest`, vttest 2.7 (20251205) runs, and the main menu (test
selector) renders fully and correctly -- the banner, all 12 entries, and the
prompt:

```
VT100 test program, version 2.7 (20251205)
Line speed 38400bd
Choose test type:
  0. Exit
  1. Test of cursor movements
  ...
  12. Modify test-parameters
Enter choice number (0 - 12):
```

The menu exercises absolute cursor positioning (`CSI row;col H`), erase-below
(`CSI 0 J`), and plain text -- all correct. (An earlier observation that the menu
list did not render came from a stale dev build / a capture taken before the draw
finished; it does **not** reproduce on a HEAD build.)

## Driving sections without GUI keyboard

Synthetic keyboard input does not reach the Wintty window (WinUI lifted-input
focus cannot be forced from a detached process), so sections are driven with
`run-vttest-section.ps1` + `vttest-section.sh`: vttest runs under an inner
`script` pty inside the pane and its menu choice is auto-fed from a pipe.

This is reliable for **size-independent** content (character sets, glyph shapes).
**Size-dependent** tests (full-screen borders, autowrap) are not reliably
assessed this way yet: the inner `script` pty size and a post-launch window
resize do not match Wintty's grid, which produces layout artifacts that are *not*
Wintty bugs. A size-matched launch (fixed window on the primary monitor, no
post-draw resize) is the refinement for those.

## Per-section assessment

Against `windows`-HEAD. The rendering-relevant sections (1-4) are assessed; the
remaining sections are behavioral and covered better elsewhere (see the summary).

| # | vttest section | Result | Notes |
|---|---|---|---|
| 1 | Cursor movements | pass | unbroken `*`/`+` border around the full edge with a centered E-frame; cursor positioning correct, and the box correctly redraws on resize |
| 2 | Screen features | deferred (harness) | autowrap (DECAWM) content is generated for the terminal width at test time, which the auto-feed harness cannot present at a fixed final size (an early window resize blanks the surface; a late one cannot regenerate the pre-computed pattern). Not a Wintty defect -- autowrap is covered by esctest and the `Terminal.zig` unit tests |
| 3 | Character sets | pass | US-ASCII, British (`#`->`£`), DEC special graphics (line drawing), DEC alternate ROM, and SI/SO G0/G1 switching all correct. The configured font ligates `<=>` (cosmetic, not a VT issue) |
| 4 | Double-sized characters | not implemented | The DEC line-size attributes are not implemented in libghostty, so double-width and double-height lines render as normal single-size text. `src/terminal/stream.zig` dispatches only `ESC #8` (DECALN); `ESC #6` (DECDWL), `ESC #3`/`#4` (DECDHL top/bottom) and `ESC #5` (DECSWL) fall through unhandled. This is in the shared terminal core (no Windows override), so it is a base-Ghostty limitation, not ConPTY/Windows-specific |
| 5 | Keyboard | pending | needs real key input (the auto-feed driver cannot exercise it) |
| 6 | Terminal reports | pending | overlaps the esctest query findings |
| 7 | VT52 mode | pending | |
| 8 | VT102 Insert/Delete Char/Line | pending | |
| 9 | Known bugs | pending | |
| 10 | Reset and self-test | pending | |
| 11 | Non-VT100 (VT220 / xterm) | pending | |

## Summary

**No ConPTY or Windows-specific VT defect was found.** Across the rendering
sections, Wintty renders vttest correctly: the test selector, cursor-movement
border (section 1), and character sets (section 3) all pass, and section 1 also
redraws correctly on resize. The single non-pass -- DEC double-width/height lines
(section 4) -- is a base-Ghostty terminal-core limitation (the attributes are not
implemented at all; `src/terminal/stream.zig` handles only `ESC #8`), not anything
the Windows port introduces. This agrees with the esctest baseline, which already
showed the transport is clean and the only gaps are query types libghostty
deliberately does not answer.

**What is left, and why it is low value here:**

- Section 2 (autowrap) is deferred for the harness reason above; DECAWM is covered
  by esctest and `Terminal.zig`.
- Section 5 (keyboard) needs real key input, which the auto-feed driver cannot
  produce (synthetic keys do not reach the WinUI window); it is an interactive
  check, not a rendering one.
- Section 6 (terminal reports) is exactly what the esctest baseline already
  measured (DA/DSR/CPR/DECRQM etc.).
- Sections 7-11 (VT52, VT102 insert/delete, known bugs, reset, VT220/xterm) are
  behavioral and covered by libghostty's own `Terminal.zig`/`Parser.zig` unit
  tests, which are platform-independent.

The valuable, Windows-specific output of this pass is the result above plus the
re-runnable host and section-driver scripts. Remaining per-section captures would
add little signal over the existing esctest baseline and unit tests.
