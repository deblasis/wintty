# Mouse-TUI smoke fixtures

Per-cell Ghostty `.conf` fixtures for the mouse-protocol smoke pass. Each
fixture pins a `command =` that launches a target TUI directly so Wintty opens
straight into the app under test, so the operator only needs to click, not
type.

Each fixture also enables mouse / termio / input debug log scopes so byte-level
evidence is available when a click misbehaves.

## Run a cell

```pwsh
just build-dll build-win
pwsh -NoProfile -File windows/scripts/mouse-smoke-run.ps1 -Cell 01-wsl2-mc
```

The runner copies the fixture into an isolated `XDG_CONFIG_HOME` and launches
`Wintty.exe`. It restores any pre-existing `XDG_CONFIG_HOME` on exit. Env-based
isolation is necessary because the WinUI shell does not honor
`--config-file` on the CLI.

Exit codes: `0` = Wintty exited normally, `2` = setup error (fixture or exe
not found) or watchdog timeout, otherwise Wintty's own exit code.

## Cells

| Cell | Env | TUI | Protocols |
|---|---|---|---|
| 01 | WSL2 | mc | 1000/1002 + 1006 |
| 02 | Native Win | lazygit | 1000+1002+1003+1006 (tcell) |
| 03 | WSL2 | lazygit | same as 02 |
| 04 | WSL2 | btop | 1002+1015+1006, +1003 hover |
| 05 | WSL2 | htop | X10 only (ncurses terminfo) |
| 06 | Native Win | btop4win | Win32 console path |
| 07 | Native Win | neovim | 1004 focus + mouse=a |
| 08 | WSL2 | neovim | same as 07 |
| 09 | MSYS2 | mc | Git Bash + MSYS2 pty path (gated) |
| 10 | MSYS2 | lazygit | gated |

## Prerequisites

- **Native cells** (02, 06, 07): `lazygit.exe`, `btop4win.exe`, and `nvim.exe`
  must be on PATH. Cell 06 uses winget's btop4win — install via
  `winget install aristocratos.btop4win` and add the WinGet Packages dir to
  PATH (or symlink the exe into an existing PATH directory).
- **WSL2 cells** (01, 03, 04, 05, 08): the matching binaries must be installed
  inside your default WSL2 distro. The cells wrap their command in
  `bash -lic` because `wsl.exe -- <bare-bin>` exits Wintty before surface init.
- **MSYS2 cells** (09, 10): require Git for Windows installed at the standard
  path. Cell 09 additionally requires Midnight Commander in MSYS2 (`pacman -S mc`
  from a full MSYS2 install — Git Bash's bundled MSYS2 ships only the message
  compiler `mc.exe`, not Midnight Commander).
