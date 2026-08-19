#requires -Version 7
<#
    Shared process policy for the GUI harnesses in this directory.

    These scripts used to open with `Get-Process Wintty | Stop-Process -Force`
    to get a clean slate. That kills every Wintty on the machine, including
    builds from other worktrees and the window the developer is working in,
    which is not a harness's call to make.

    The replacement is two rules:

      1. Refuse to start while any Wintty is running. Say which pids, so the
         developer can close them. This is not about the single-instance
         mutex - that is keyed on a hash of the exe path, so another
         worktree's build would not collide. It is that state is shared:
         crash.log lives under %LOCALAPPDATA% per user rather than per exe
         path, and a harness that reads it cannot tell whose crash it saw.

      2. Clean up only what the run started, identified by start time and,
         where the caller knows it, image path. Anything that cannot be
         positively identified is left alone: an unreadable path or start
         time is a reason to skip a process, never a reason to kill it.

    Dot-source it:

        . (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
#>

# Throws if any Wintty is running. Call once, before the first launch.
function Assert-NoWintty {
    param([string]$Context = 'This harness')

    $running = @(Get-Process Wintty -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }

    $pids = ($running | ForEach-Object { $_.Id }) -join ', '
    throw ("close the running Wintty first (pid: $pids). " +
           "$Context shares crash.log and the state directory with it, so " +
           'its crashes would be read as belonging to this run, and this ' +
           'harness will not kill instances it did not start.')
}

# The timestamp to hand to Stop-WinttyStartedAfter. Take it immediately
# before the first Start-Process, not at script start: anything earlier
# widens the window in which an unrelated instance looks like ours.
function Get-WinttyLaunchStamp { return Get-Date }

# Kill the Wintty processes this run started. Fails closed on anything it
# cannot identify.
function Stop-WinttyStartedAfter {
    param(
        [Parameter(Mandatory)][datetime]$Since,
        # Optional, and worth passing whenever the caller knows which exe it
        # launched: start time alone will also match an instance the
        # developer opened while the run was in flight.
        [string]$ExePath,
        [int]$TimeoutMs = 3000
    )

    $full = $null
    if ($ExePath) {
        $full = (Resolve-Path -LiteralPath $ExePath -ErrorAction SilentlyContinue)?.Path
        # A path the caller supplied but that does not resolve means the
        # filter cannot be applied. Sweep nothing rather than quietly
        # widening to every process started since the stamp.
        if (-not $full) { return }
    }

    foreach ($p in @(Get-Process Wintty -ErrorAction SilentlyContinue)) {
        # Process.StartTime and .Path return null or throw for a process the
        # harness cannot open (elevated, another session, or one that exited
        # between enumeration and the read). Skip those.
        $started = try { $p.StartTime } catch { $null }
        if ($null -eq $started -or $started -lt $Since) { continue }

        if ($full) {
            $path = try { $p.Path } catch { $null }
            if ([string]::IsNullOrEmpty($path) -or $path -ne $full) { continue }
        }

        # Kill the tree: the shell runs as a child, and a wedged one would
        # otherwise outlive every run.
        try { $p.Kill($true); [void]$p.WaitForExit($TimeoutMs) } catch { }
    }
}
