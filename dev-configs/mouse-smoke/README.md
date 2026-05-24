# Mouse-TUI smoke fixtures

Per-cell Ghostty `.conf` fixtures for the 2026-05-24 mouse-protocol smoke pass.
Each fixture pins a `command =` that launches a target TUI directly so Wintty
opens straight into the app under test (sidesteps the typing-into-terminal
restriction of automation tooling).

Each fixture also enables mouse / termio / input debug log scopes so byte-level
evidence is available when a click misbehaves.

Run a cell:

```pwsh
just build-dll build-win
pwsh -NoProfile -File scripts/mouse-smoke-run.ps1 -Cell 01-wsl2-mc
```

The runner copies the fixture into an isolated `XDG_CONFIG_HOME` and launches
Wintty.exe — this matches the existing `validate-transport-run.ps1` pattern
because the WinUI shell does not honor `--config-file` on the CLI.

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
