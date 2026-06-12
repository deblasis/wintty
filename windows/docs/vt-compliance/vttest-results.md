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

Representative sample against `windows`-HEAD. Remaining sections are pending.

| # | vttest section | Result | Notes |
|---|---|---|---|
| 1 | Cursor movements | inconclusive | border + centered E-frame render, but size-dependent -- redo size-matched |
| 2 | Screen features | inconclusive | autowrap layout artifact from the pty-size mismatch, not a VT bug -- redo size-matched |
| 3 | Character sets | pass | US-ASCII, British (`#`->`£`), DEC special graphics (line drawing), DEC alternate ROM, and SI/SO G0/G1 switching all correct. The configured font ligates `<=>` (cosmetic, not a VT issue) |
| 4 | Double-sized characters | partial | double-width (DECDWL) correct; double-height (DECDHL) renders as double-width single-height (the two halves appear as duplicate full lines), likely a shared Ghostty-core limitation -- confirm vs upstream |
| 5 | Keyboard | pending | needs real key input (the auto-feed driver cannot exercise it) |
| 6 | Terminal reports | pending | overlaps the esctest query findings |
| 7 | VT52 mode | pending | |
| 8 | VT102 Insert/Delete Char/Line | pending | |
| 9 | Known bugs | pending | |
| 10 | Reset and self-test | pending | |
| 11 | Non-VT100 (VT220 / xterm) | pending | |

Classify each failure as a ConPTY limitation (cross-check
[`conpty-reference.md`](conpty-reference.md)) or a Ghostty bug, consistent with
how the esctest baseline attributes its results. The one open item so far --
DECDHL double-height -- is a libghostty/core rendering question, not ConPTY.
