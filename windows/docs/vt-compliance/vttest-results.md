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

## Host validation

Bring-up confirmed end to end: Wintty spawns `wsl.exe -> vttest`, vttest 2.7
(20251205) runs, and its startup banner renders correctly in the pane:

```
VT100 test program, version 2.7 (20251205)
Line speed 0bd
Choose test type:
```

The banner text is placed by absolute cursor positioning (`CSI row;col H`) and
renders crisply at the right cells, so the WSL/ConPTY/libghostty/DX12 pipeline is
wired and the host is usable.

## Per-section assessment

Driving each vttest menu section and recording pass / fail / renders-incorrectly
is the next slice, and should be done against a clean build of the `windows`
branch (the bring-up above used a local dev build, so per-section results are not
recorded here yet).

| # | vttest section | Result | Notes |
|---|---|---|---|
| 1 | Cursor movements | pending | |
| 2 | Screen features | pending | |
| 3 | Character sets | pending | |
| 4 | Double-sized characters | pending | |
| 5 | Keyboard | pending | |
| 6 | Terminal reports | pending | overlaps the esctest query findings |
| 7 | VT52 mode | pending | |
| 8 | VT102 Insert/Delete Char/Line | pending | |
| 9 | Known bugs | pending | |
| 10 | Reset and self-test | pending | |
| 11 | Non-VT100 (VT220 / xterm) | pending | |

Classify each failure as a ConPTY limitation (cross-check
[`conpty-reference.md`](conpty-reference.md)) or a Ghostty bug, consistent with
how the esctest baseline attributes its results.
