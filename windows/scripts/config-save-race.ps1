<#
.SYNOPSIS
    Aim a config reload at the middle of a save, and count what it costs.

.DESCRIPTION
    Manual harness for issue #676. It launches the app against an isolated
    config root, then repeatedly arms the config watcher's debounce and takes
    the config file away just before the debounce fires, so the settle's
    file-existence check and libghostty's load straddle the gap an editor's
    save passes through.

    Two save shapes, because Windows editors use both and they reach different
    code:

      swap      Rename the config away, hold, rename it back. What an atomic
                save looks like (VS Code with atomic writes, File.Replace,
                delete-then-rename). The config file does not exist for the
                length of the hold.

      truncate  Open the config file, truncate it to zero, hold, write the
                content back. What a non-atomic in-place save looks like
                (PowerShell Set-Content and >, vim with backupcopy=yes). The
                file exists and is empty for the length of the hold.

    Three things are counted, and one of them is a positive control:

      clobbers  A file that appeared at the config path while it was renamed
                away. That is libghostty's starter template landing on top of
                a save, and it takes the user's configuration with it. Only
                reachable in the swap shape.

      wrong     A reload that applied a config the user does not have. The
                staged config sets a distinctive font-size and the app logs
                every size it is set to, so any other size is a reload that
                resolved to something else and pushed it at the live
                surfaces.

      applied   A reload that applied the staged font-size. This is the
                control, and it is what makes the two counts above mean
                anything: they are both zero in a build where no reload ever
                applies at all, which is exactly what an inverted reload
                guard produces, and this run used to call that a pass. A run
                has to show reloads landing as well as no bad ones.

    -Migrated stages a second config file, ghostty/config.ghostty, beside the
    one being saved. That is an ordinary setup for anyone who came from
    Ghostty, and it is the case a single staged file cannot reach: the older
    file still reads while the newer one is mid swap, so the load reports a
    perfectly good "a config file was read" with the saved file's settings
    missing from it. The older file carries its own distinctive size, so a
    reload that applied it lands in `wrong`.

    -DeleteMigratedAt N (needs -Migrated) runs N ordinary iterations, then
    deletes the staged ghostty/config.ghostty for good and finishes the run
    with plain writes and long settles. That is migration cleanup, the
    ordinary user action: the watcher watches the wintty directory only, so
    the deletion is never announced, and the session is left running on one
    more config file than exists. The mode is judged on its own window: a
    build that refuses the count shrink forever applies nothing after the
    deletion and fails, whatever the first N iterations landed; a build that
    recovers keeps applying within a few settles.

    Exit code 1 if either bad count is non-zero or too few reloads landed, so
    this can gate a change.

.EXAMPLE
    pwsh -NoProfile -File windows/scripts/config-save-race.ps1 -Shape swap -Iterations 300

.EXAMPLE
    pwsh -NoProfile -File windows/scripts/config-save-race.ps1 -Migrated -Iterations 200

.EXAMPLE
    pwsh -NoProfile -File windows/scripts/config-save-race.ps1 -Migrated -DeleteMigratedAt 30 -Iterations 90
#>
[CmdletBinding()]
param(
    [ValidateSet('swap', 'truncate')]
    [string]$Shape = 'swap',

    [int]$Iterations = 100,

    # How long the config file is left away or empty. Longer than the
    # watcher's 300ms debounce means a settle can land inside the gap.
    [int]$HoldMs = 120,

    # Where in the debounce period to take the file away. The default sits
    # just under 300ms so the delivery and the pull race each other.
    [int]$StrikeMinMs = 285,
    [int]$StrikeMaxMs = 320,

    [int]$SettleMs = 14000,

    # Stage ghostty/config.ghostty as well, the way a machine migrated from
    # Ghostty has it. The file being saved is still wintty/config.wintty.
    [switch]$Migrated,

    # With -Migrated: after this many iterations, delete the staged
    # ghostty/config.ghostty for good and finish the run with plain writes.
    # 0 (the default) never deletes it.
    [int]$DeleteMigratedAt = 0,

    # How many reloads have to land for the run to count. Every iteration
    # ends with the config file back in place and a wait past the debounce,
    # so one reload per iteration is the shape; half of that is the floor,
    # because a strike landing either side of the debounce can fold two
    # iterations into one settle. Zero would make this script pass a build
    # where no reload ever applies, which is what it is here to catch. The
    # floor is 2, not 1: startup applies the staged config once by itself,
    # and a one- or two-iteration run must not pass on that alone.
    [int]$MinApplied = -1,

    # Leave the staged config root and its app log behind, for working out
    # why a run failed.
    [switch]$KeepRoot,

    [string]$ExePath = 'windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not [System.IO.Path]::IsPathRooted($ExePath)) {
    $ExePath = Join-Path $repoRoot $ExePath
}
if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "no app at $ExePath. Build it with ``just build-dll build-win`` first."
}

# The repo's one spelling of "launch the app from a test": an isolated config
# root under the real temp directory, with the app's own guard armed so a
# launch that lost the root refuses instead of writing to the real config.
. (Join-Path $PSScriptRoot 'lib/test-config.ps1')

if ($DeleteMigratedAt -gt 0 -and -not $Migrated) {
    throw '-DeleteMigratedAt needs -Migrated: there is no legacy file to delete.'
}
if ($DeleteMigratedAt -lt 0 -or $DeleteMigratedAt -ge $Iterations) {
    throw '-DeleteMigratedAt has to sit inside the run: 1..(Iterations-1).'
}

if ($MinApplied -lt 0) { $MinApplied = [Math]::Max(2, [int]($Iterations / 2)) }

# font-size is the oracle. Every applied config change calls the core's
# setFontSize, which logs the size it was given, so the app log holds one
# line per reload that landed and says which configuration it came from.
#
#   $FontSize        the staged config, so a reload that read it
#   $MigratedSize    the older ghostty/config.ghostty, so a reload that read
#                    only that one: the newer file was mid swap
#   anything else    libghostty's own default, so a reload that read no
#                    config file at all
$FontSize = 21
$MigratedSize = 13
$configText = @(
    'windows-single-instance = false'
    'auto-reload-config = true'
    'log-level = debug'
    "font-size = $FontSize"
) -join "`n"

$session = Enter-WinttyTestConfig -ConfigText $configText
try {
    $cfg = Join-Path $session.Dir 'wintty\config.wintty'

    if ($Migrated) {
        # Older than the file being saved, so the newer one overrides it and
        # a healthy run never sees $MigratedSize. It is only reachable by a
        # reload that took the older file on its own, which is what the swap
        # gap of the newer one looks like.
        $ghosttyDir = Join-Path $session.Dir 'ghostty'
        New-Item -ItemType Directory -Force -Path $ghosttyDir | Out-Null
        [System.IO.File]::WriteAllText(
            (Join-Path $ghosttyDir 'config.ghostty'),
            "font-size = $MigratedSize`n")
    }

    $stateBase = Join-Path $session.Dir 'state'
    New-Item -ItemType Directory -Force -Path $stateBase | Out-Null
    $env:WINTTY_STATE_BASE = $stateBase

    Write-Host "root=$($session.Dir) shape=$Shape iterations=$Iterations migrated=$([bool]$Migrated) deleteMigratedAt=$DeleteMigratedAt"
    $proc = Start-Process $ExePath -PassThru
    Start-Sleep -Milliseconds $SettleMs
    $proc.Refresh()
    if ($proc.HasExited) {
        Write-Host "RESULT: the app exited during startup, code=$($proc.ExitCode)"
        exit 1
    }

    $away = "$cfg~"
    $body = "$configText`n"
    $sw = [System.Diagnostics.Stopwatch]::new()
    $rng = [System.Random]::new()
    $clobbers = 0
    $exited = $null
    $deletedAtUtc = [datetimeoffset]::MinValue

    for ($i = 1; $i -le $Iterations; $i++) {
        if ($DeleteMigratedAt -gt 0 -and $i -eq $DeleteMigratedAt + 1) {
            # Migration cleanup, mid-run: the watcher watches the wintty
            # directory only, so this raises no event anywhere and nothing
            # announces it. What the app does on the next edit is the whole
            # point of the mode.
            $deletedAtUtc = [datetimeoffset]::UtcNow
            Remove-Item -LiteralPath (Join-Path $session.Dir 'ghostty\config.ghostty') -Force
            Write-Host "  deleted the migrated ghostty config after iteration $DeleteMigratedAt"
        }

        if ($DeleteMigratedAt -gt 0 -and $i -gt $DeleteMigratedAt) {
            # Plain writes and long settles: no swap games past the deletion,
            # so no settle can catch the saved file away and rescue a wedged
            # build through the vanished path. A healthy run applies every
            # one of these; a wedged one applies none.
            [System.IO.File]::WriteAllText($cfg, $body + "# probe $i`n")
            Start-Sleep -Milliseconds 1200
            $proc.Refresh()
            if ($proc.HasExited) { $exited = $proc.ExitCode; break }
            continue
        }

        # Arm the debounce with a real edit.
        [System.IO.File]::WriteAllText($cfg, $body + "# probe $i`n")

        # Spin to just before the debounce fires. Sleep resolution is far too
        # coarse for a 300ms target with a millisecond-wide window.
        $strike = $StrikeMinMs + $rng.Next(0, [Math]::Max(1, $StrikeMaxMs - $StrikeMinMs + 1))
        $sw.Restart()
        while ($sw.Elapsed.TotalMilliseconds -lt $strike) { }

        if ($Shape -eq 'swap') {
            try {
                if (Test-Path -LiteralPath $away) { Remove-Item -LiteralPath $away -Force }
                [System.IO.File]::Move($cfg, $away)
                Start-Sleep -Milliseconds $HoldMs

                # Anything at the config path now is libghostty's doing: this
                # process moved the only file that was there.
                if (Test-Path -LiteralPath $cfg) {
                    $clobbers++
                    Remove-Item -LiteralPath $cfg -Force
                }
                [System.IO.File]::Move($away, $cfg)
            }
            catch [System.IO.IOException] {
                # The app opens the config for the length of a load, and a
                # move under that throws. Put the file back if it can be put
                # back and let the run carry on: one dropped strike is not a
                # measurement, a dead harness is no measurement at all.
                if ((Test-Path -LiteralPath $away) -and -not (Test-Path -LiteralPath $cfg)) {
                    try { [System.IO.File]::Move($away, $cfg) } catch { }
                }
            }
        }
        else {
            # Truncate in place and hold it empty, then write the content
            # back through the same handle.
            $stream = [System.IO.File]::Open(
                $cfg, [System.IO.FileMode]::Truncate,
                [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
            try {
                Start-Sleep -Milliseconds $HoldMs
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($body + "# probe $i`n")
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
        }

        Start-Sleep -Milliseconds 350
        $proc.Refresh()
        if ($proc.HasExited) { $exited = $proc.ExitCode; break }
        if ($i % 50 -eq 0) { Write-Host "  $i iterations, alive, clobbers=$clobbers" }
    }

    if (-not $proc.HasExited) {
        $proc.Kill()
        $proc.WaitForExit(5000) | Out-Null
    }
    Start-Sleep -Milliseconds 800

    # Every size the app was set to, split into the ones that came from the
    # staged config and the ones that did not. Both halves are needed: the
    # bad count alone is zero in a build where no reload ever applies.
    # The count is lines, not reloads: every applied reload logs one line
    # per surface, so it overcounts reloads, and the deletion mode judges
    # its own window rather than this total.
    $applied = 0
    $wrong = 0
    $migratedHits = 0
    $appliedAfterDeletion = 0
    $logDir = Join-Path $stateBase 'Wintty\logs'
    if (Test-Path -LiteralPath $logDir) {
        Get-ChildItem -LiteralPath $logDir -Filter *.log | ForEach-Object {
            Select-String -LiteralPath $_.FullName -Pattern 'set font size size=(\d+)' -AllMatches |
                ForEach-Object {
                    $stamp = [datetimeoffset]::Parse(
                        ($_.Line -split ' ')[0],
                        [System.Globalization.CultureInfo]::InvariantCulture)
                    foreach ($match in $_.Matches) {
                        $size = [int]$match.Groups[1].Value
                        if ($size -eq $FontSize) {
                            $applied++
                            if ($DeleteMigratedAt -gt 0 -and $stamp -ge $deletedAtUtc) {
                                $appliedAfterDeletion++
                            }
                        }
                        else {
                            $wrong++
                            if ($Migrated -and $size -eq $MigratedSize) { $migratedHits++ }
                        }
                    }
                }
        }
    }

    Write-Host ''
    Write-Host "template clobbers of the config path : $clobbers"
    Write-Host "reloads that applied the staged size : $applied (need at least $MinApplied)"
    Write-Host "reloads that applied something else  : $wrong"
    if ($Migrated) {
        Write-Host "  of those, the older config file    : $migratedHits"
    }
    if ($DeleteMigratedAt -gt 0) {
        $expectedAfter = $Iterations - $DeleteMigratedAt - 8
        Write-Host "applied after the deletion           : $appliedAfterDeletion (need at least $expectedAfter)"
    }
    if ($null -ne $exited) { Write-Host "the app EXITED, code=$exited" }
    Write-Host "root=$($session.Dir)"

    $failed = $false
    if ($clobbers -gt 0 -or $wrong -gt 0 -or $null -ne $exited) { $failed = $true }
    if ($DeleteMigratedAt -gt 0 -and
        $appliedAfterDeletion -lt ($Iterations - $DeleteMigratedAt - 8)) {
        # The whole point of the mode: reloads that keep landing after a
        # file the watcher does not watch is deleted. A session wedged on
        # the count shrink applies nothing from the deletion on.
        Write-Host "RELOADS STOPPED AFTER THE DELETION: the session never believed the file was gone"
        $failed = $true
    }
    if ($applied -lt $MinApplied) {
        # Not a pass with nothing wrong: it is a run that proved nothing.
        # An app whose reload guard refuses everything reaches here with
        # both bad counts at zero, and used to be reported as a pass.
        Write-Host "TOO FEW RELOADS LANDED: this run cannot tell a fixed build from one where no reload applies at all"
        $failed = $true
    }

    if ($failed) {
        Write-Host 'RESULT: FAIL'
        exit 1
    }
    Write-Host 'RESULT: PASS'
}
finally {
    if ($KeepRoot) {
        # Restore the environment without removing the root.
        if ($null -ne $session.OrigXdg) { $env:XDG_CONFIG_HOME = $session.OrigXdg }
        else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
        if ($null -ne $session.OrigTestConfig) { $env:WINTTY_TEST_CONFIG = $session.OrigTestConfig }
        else { Remove-Item Env:WINTTY_TEST_CONFIG -ErrorAction SilentlyContinue }
    }
    else {
        Exit-WinttyTestConfig -Session $session
    }
}
