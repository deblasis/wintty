# PowerShell Shell Integration

Wintty (the Windows build of Ghostty) ships a PowerShell shell-integration
script alongside the per-shell scripts for bash, zsh, fish, etc.

## How it loads

When wintty spawns a PowerShell child process, it exports the path to the
script in the `GHOSTTY_SHELL_INTEGRATION_PS1` environment variable. The
script itself is NOT auto-injected; the user opts in by adding one line to
their `$PROFILE`:

```powershell
if ($env:GHOSTTY_SHELL_INTEGRATION_PS1) { . $env:GHOSTTY_SHELL_INTEGRATION_PS1 }
```

To find or create your profile:

```powershell
if (-not (Test-Path $PROFILE)) { New-Item -Type File -Path $PROFILE -Force }
notepad $PROFILE
```

## Manual sourcing

If `GHOSTTY_SHELL_INTEGRATION_PS1` is unset (e.g. shell integration is
disabled in the wintty config), you can source the script by absolute path:

```powershell
. "C:\path\to\ghostty\src\shell-integration\powershell\ghostty.ps1"
```

## What it does

- Emits OSC 133 prompt marks (A/B/C/D) so wintty can identify prompt
  boundaries, command input regions, and command exit codes.
- Reports the current working directory via OSC 7 (file:// URI) and
  OSC 9;9 (raw Windows path).
- Wraps the user's existing `prompt` function, so Oh-My-Posh, Starship,
  posh-git, and similar prompt customizations keep working.

## Compatibility

- Works on Windows PowerShell 5.1 and PowerShell 7+.
- No external dependencies.
- Uses PSReadLine if present (it ships with both 5.1 and 7+) to bracket
  the running command with OSC 133;C / 133;D. If PSReadLine is absent or
  fails to load, A/B prompt marks still work and the script does not
  raise an error.
- Idempotent: sourcing the script a second time is a no-op.
