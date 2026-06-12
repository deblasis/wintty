#requires -Version 7
# Launch a real Wintty hosting latency-probe.py over WSL and report CPR/DSR/DA
# round-trip latency + loss. Uses the same launch mechanism as run-esctest.ps1
# (temp ghostty config under an isolated XDG_CONFIG_HOME, since the WinUI shell
# ignores --command; markers + JSON written distro-side onto the /mnt DrvFs mount).
[CmdletBinding()] param(
    [Parameter(Mandatory)][string]$WinttyExe,
    [string]$Distro = 'Ubuntu-24.04',
    [string]$OutDir = "$env:TEMP\latency-probe-run",
    [int]$Reps = 30,
    [double]$ReadTimeoutSec = 10,   # generous: a response under this is slow, not lost
    [int]$TimeoutSec = 900
)
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force $OutDir | Out-Null
$jsonWin = Join-Path $OutDir 'latency.json'
$doneWin = Join-Path $OutDir 'probe.done'
Remove-Item $doneWin, $jsonWin -ErrorAction SilentlyContinue

$drive    = $OutDir.Substring(0,1).ToLower()
$mnt      = "/mnt/$drive" + ($OutDir.Substring(2) -replace '\\','/')
$jsonMnt  = "$mnt/latency.json"
$doneMnt  = "$mnt/probe.done"

# The probe script lives in the repo; reach it from the distro via its /mnt path
# (the worktree path is space-free, so it splits cleanly through the config).
$probeWin = Join-Path $PSScriptRoot 'latency-probe.py'
$pdrive   = $probeWin.Substring(0,1).ToLower()
$probeMnt = "/mnt/$pdrive" + ($probeWin.Substring(2) -replace '\\','/')

# LF-only bash script (CRLF makes bash choke on \r); env vars feed the probe knobs.
$scriptWin = Join-Path $OutDir 'run.sh'
$scriptMnt = "$mnt/run.sh"
$bash = @(
    '#!/usr/bin/env bash'
    "export PROBE_REPS=$Reps"
    "export PROBE_TIMEOUT=$ReadTimeoutSec"
    "python3 '$probeMnt' '$jsonMnt'"
    "touch '$doneMnt'"
) -join "`n"
[System.IO.File]::WriteAllText($scriptWin, $bash + "`n", (New-Object System.Text.UTF8Encoding($false)))

$cfgDir = Join-Path $OutDir 'cfg'
$cfgGhostty = Join-Path $cfgDir 'ghostty'
New-Item -ItemType Directory -Force $cfgGhostty | Out-Null
"command = wsl.exe -d $Distro -- bash -l $scriptMnt" |
    Set-Content -LiteralPath (Join-Path $cfgGhostty 'config') -Encoding utf8

$prevXdg = $env:XDG_CONFIG_HOME
$proc = $null
try {
    $env:XDG_CONFIG_HOME = $cfgDir
    $proc = Start-Process -FilePath $WinttyExe -PassThru
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while (-not (Test-Path $doneWin) -and (Get-Date) -lt $deadline) {
        if ($proc.HasExited -and -not (Test-Path $jsonWin)) { break }
        Start-Sleep -Seconds 2
    }
}
finally {
    $env:XDG_CONFIG_HOME = $prevXdg
    if ($proc) { try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {} }
}

if (-not (Test-Path $jsonWin)) {
    throw "No probe JSON at $jsonWin -- the run never started. Check the Wintty surface spawned wsl.exe (temp config at $cfgDir) and $probeMnt is reachable."
}

$data = Get-Content -LiteralPath $jsonWin -Raw | ConvertFrom-Json
Write-Host "Latency probe ($Distro, $Reps reps/query, ${ReadTimeoutSec}s read-timeout):`n"
$rows = foreach ($p in $data.PSObject.Properties) {
    $v = $p.Value
    [pscustomobject]@{
        Query    = $p.Name
        Got      = $v.got
        Lost     = $v.lost
        'Min ms' = $v.min_ms
        'Med ms' = $v.median_ms
        'P95 ms' = $v.p95_ms
        'Max ms' = $v.max_ms
    }
}
$rows | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "Raw JSON: $jsonWin"
