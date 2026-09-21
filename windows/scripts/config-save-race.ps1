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

    Two things are counted:

      clobbers  A file that appeared at the config path while it was renamed
                away. That is libghostty's starter template landing on top of
                a save, and it takes the user's configuration with it. Only
                reachable in the swap shape.

      defaults  A reload that applied a config built from no config file. The
                staged config sets a distinctive font-size, so any other size
                in the app log is a reload that resolved to defaults and
                pushed it at the live surfaces.

    Exit code 1 if either count is non-zero, so this can gate a change.

.EXAMPLE
    pwsh -NoProfile -File windows/scripts/config-save-race.ps1 -Shape swap -Iterations 300
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

# font-size is the oracle. A reload that resolved to no configuration pushes
# libghostty's default size, and the app log records every size it is set to.
$FontSize = 21
$configText = @(
    'windows-single-instance = false'
    'auto-reload-config = true'
    'log-level = debug'
    "font-size = $FontSize"
) -join "`n"

$session = Enter-WinttyTestConfig -ConfigText $configText
try {
    $cfg = Join-Path $session.Dir 'wintty\config.wintty'
    $stateBase = Join-Path $session.Dir 'state'
    New-Item -ItemType Directory -Force -Path $stateBase | Out-Null
    $env:WINTTY_STATE_BASE = $stateBase

    Write-Host "root=$($session.Dir) shape=$Shape iterations=$Iterations"
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

    for ($i = 1; $i -le $Iterations; $i++) {
        # Arm the debounce with a real edit.
        [System.IO.File]::WriteAllText($cfg, $body + "# probe $i`n")

        # Spin to just before the debounce fires. Sleep resolution is far too
        # coarse for a 300ms target with a millisecond-wide window.
        $strike = $StrikeMinMs + $rng.Next(0, [Math]::Max(1, $StrikeMaxMs - $StrikeMinMs + 1))
        $sw.Restart()
        while ($sw.Elapsed.TotalMilliseconds -lt $strike) { }

        if ($Shape -eq 'swap') {
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

    # Count reloads that applied a configuration the user does not have.
    $defaults = 0
    $logDir = Join-Path $stateBase 'Wintty\logs'
    if (Test-Path -LiteralPath $logDir) {
        Get-ChildItem -LiteralPath $logDir -Filter *.log | ForEach-Object {
            Select-String -LiteralPath $_.FullName -Pattern 'set font size size=(\d+)' -AllMatches |
                ForEach-Object { $_.Matches } |
                ForEach-Object {
                    if ([int]$_.Groups[1].Value -ne $FontSize) { $defaults++ }
                }
        }
    }

    Write-Host ''
    Write-Host "template clobbers of the config path : $clobbers"
    Write-Host "reloads that applied defaults        : $defaults"
    if ($null -ne $exited) { Write-Host "the app EXITED, code=$exited" }
    Write-Host "root=$($session.Dir)"

    if ($clobbers -gt 0 -or $defaults -gt 0 -or $null -ne $exited) {
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
