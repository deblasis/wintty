#requires -Version 7
<#
    The heavy job lanes' configuration, applied from the repo and verified
    against the machine.

    incoda keeps its queue configuration (slots, descriptions, whether a
    --reason is required, which keys are closed) in its own state dir, so
    until this script existed nothing in the repo recorded it: AGENTS.md
    described the lanes, and whether the box agreed was anyone's word. That
    is how a claim that the old `wintty` key was closed passed review while
    the key still took jobs. The table below is now the record; `just lanes`
    applies it and `just doctor` checks the live state against it.

    Apply mode only touches a lane that drifts, so on a machine that already
    matches it changes nothing and writes no config event into any lane's
    log. -Check reads `incoda status --all --json` and changes nothing.

    Exit codes: 0 the lanes match (or, in apply mode, match after applying);
    1 drift, one line per finding; 2 the check itself could not run (incoda
    not found, status unreadable). The lookup is the justfile's: PATH, then
    the installer's location under %LOCALAPPDATA%.
#>

[CmdletBinding()]
param(
    [switch]$Check   # report drift and exit; apply nothing
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# incoda's exit code is read explicitly below; a nonzero one must not become
# a terminating error before it can be reported.
$PSNativeCommandUseErrorActionPreference = $false

# What AGENTS.md ("Heavy job lanes") promises. wintty-desktop and
# wintty-publish carry no --slots: incoda's default is one, and one is the
# point of those lanes.
$lanes = @(
    [ordered]@{
        key = 'wintty-build'; slots = 3; requireReason = $true
        description = 'CPU and RAM: zig and dotnet builds and tests, signoff ladders, materialize'
    }
    [ordered]@{
        key = 'wintty-desktop'; slots = 1; requireReason = $true
        description = 'the interactive desktop, alone: GUI harnesses, pixel capture, env-guard theme flips'
    }
    [ordered]@{
        key = 'wintty-publish'; slots = 1; requireReason = $true
        description = 'release channels, signing, the installed app: cuts and uploads'
    }
    [ordered]@{
        key = 'wintty'
        closed = 'retired: use wintty-build for builds and tests, wintty-desktop for harnesses, wintty-publish for release cuts (AGENTS.md, heavy job lanes)'
    }
)

$inc = (Get-Command incoda -ErrorAction SilentlyContinue)?.Source ?? (Join-Path $env:LOCALAPPDATA 'Programs\incoda\incoda.exe')
if (-not (Test-Path $inc)) {
    Write-Host 'incoda not found on PATH or in Programs\incoda: the heavy job lanes need it (AGENTS.md; https://github.com/deblasis/incoda)'
    exit 2
}

# A property read that returns $null for a key the JSON did not carry:
# incoda's config object only has the keys that were set, and under strict
# mode a missing property is an error rather than a null.
function Get-Prop($obj, [string]$name) {
    if ($null -ne $obj -and $obj.PSObject.Properties[$name]) { $obj.$name } else { $null }
}

function Read-Queues {
    $global:LASTEXITCODE = $null
    $raw = & $inc status --all --json 2>&1 | Out-String
    if (($LASTEXITCODE ?? 1) -ne 0) {
        Write-Host "incoda status --all --json exited $LASTEXITCODE`: $($raw.Trim())"
        exit 2
    }
    try { $status = $raw | ConvertFrom-Json } catch {
        Write-Host "incoda status --all --json did not return JSON: $($raw.Trim())"
        exit 2
    }
    $byKey = @{}
    foreach ($q in @(Get-Prop $status 'queues')) { $byKey[(Get-Prop $q 'key')] = $q }
    $byKey
}

# The drift lines for one lane against its live queue, empty when it
# matches. The exact text is what doctor prints, so it names the fix.
function Get-Drift($lane, $queue) {
    $key = $lane.key
    if ($null -eq $queue -or -not (Get-Prop $queue 'exists')) {
        if ($lane.Contains('closed')) { return @("$key`: not closed (the key does not exist, so a run would create it open)") }
        return @("$key`: missing")
    }
    $cfg = Get-Prop $queue 'config'
    $closed = Get-Prop $cfg 'closed'
    if ($lane.Contains('closed')) {
        if ([string]::IsNullOrEmpty($closed)) { return @("$key`: not closed") }
        if ($closed -ne $lane.closed) { return @("$key`: closed with a different message: '$closed'") }
        return @()
    }
    $drift = @()
    if (-not [string]::IsNullOrEmpty($closed)) { $drift += "$key`: closed ('$closed'), want open" }
    # 0 is incoda's "use the default", and the default is 1.
    $slots = Get-Prop $cfg 'slots'
    if ($null -eq $slots -or $slots -le 0) { $slots = 1 }
    if ($slots -ne $lane.slots) { $drift += "$key`: slots $slots, want $($lane.slots)" }
    if ($lane.requireReason -and -not (Get-Prop $cfg 'require_reason')) { $drift += "$key`: a run without --reason is accepted, want refused" }
    $desc = Get-Prop $cfg 'description'
    if ($desc -ne $lane.description) { $drift += "$key`: description '$desc', want '$($lane.description)'" }
    $drift
}

# Every call site wraps the result in @(): a function returning an empty
# array hands the caller $null, and strict mode has no .Count on that.
function Get-AllDrift($queues) {
    $all = @()
    foreach ($lane in $lanes) { $all += @(Get-Drift $lane $queues[$lane.key]) }
    $all
}

$summary = "lanes match: wintty-build (3 slots), wintty-desktop and wintty-publish require a --reason; wintty is closed"

if ($Check) {
    $drift = @(Get-AllDrift (Read-Queues))
    foreach ($d in $drift) { Write-Host "drift $d" }
    if ($drift.Count -gt 0) { Write-Host "run 'just lanes' to apply the configuration from .agents/scripts/lanes.ps1"; exit 1 }
    Write-Host "ok   $summary"
    exit 0
}

$queues = Read-Queues
$applied = 0
foreach ($lane in $lanes) {
    $drift = @(Get-Drift $lane $queues[$lane.key])
    if ($drift.Count -eq 0) { Write-Host "ok      $($lane.key)"; continue }
    foreach ($d in $drift) { Write-Host "drift   $d" }
    # --open on a lane that is already open is a no-op, and a lane that
    # somehow got closed is drift this has to heal rather than report twice.
    $cargs = if ($lane.Contains('closed')) {
        @('config', $lane.key, '--close', $lane.closed)
    } elseif ($lane.slots -ne 1) {
        @('config', $lane.key, '--slots', $lane.slots, '--description', $lane.description, '--require-reason', '--open')
    } else {
        @('config', $lane.key, '--description', $lane.description, '--require-reason', '--open')
    }
    $global:LASTEXITCODE = $null
    & $inc @cargs
    if (($LASTEXITCODE ?? 1) -ne 0) { Write-Host "incoda config $($lane.key) exited $LASTEXITCODE"; exit 2 }
    Write-Host "applied $($lane.key)"
    $applied++
}

# Read back rather than trust the exit code: the check is the contract, and
# a config that took but did not land is exactly the drift this exists for.
$after = @(Get-AllDrift (Read-Queues))
foreach ($d in $after) { Write-Host "drift $d (still, after applying)" }
if ($after.Count -gt 0) { exit 1 }
if ($applied -eq 0) { Write-Host "ok   $summary (nothing applied)" } else { Write-Host "ok   $summary ($applied applied)" }
exit 0
