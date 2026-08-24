# Nightly quality-control run for the windows branch.
#
# Runs the full test ladder and the fuzz suite against a fresh checkout of
# origin/windows in a dedicated worktree, and files a P1 issue on
# deblasis/ghostty for anything that breaks. Intended to run from a scheduled
# task at 23:00 (see register_nightly_fuzz.ps1) on a build machine that acts
# as its own CI: the task wakes the machine if needed, and when the run
# finishes the machine hibernates again (config-controlled), so the box works
# as a nightly appliance. Every path is derived from this script's location;
# nothing machine-specific is hardcoded.
#
# The run is idle-gated rather than clock-gated: a scheduled run waits for
# 3 minutes of continuous keyboard/mouse idle before starting (up to 3
# hours; if the user works all evening it gives up without hibernating).
# The fuzz suite drives the real GUI and needs an unlocked interactive
# desktop with nobody typing, so it waits for idle again after the headless
# legs finish, and a skip is recorded rather than counted as a pass. If no
# fuzz leg has succeeded for 7 days, that starvation is itself filed as an
# issue.
#
# Hibernate only happens for scheduled runs (or -HibernateAfter), only when
# the saved config allows it, and only after the same continuous-idle check,
# so the machine is never hibernated under someone using it.
#
# Config and status live next to the logs (.claude/nightly-logs/), managed
# by nightly_control.ps1: nightly-config.json {hibernateAfter, runFuzz},
# status.json {phase, sha, results, log}.
#
# Issue filing dedups by title: while an issue for a category is open, new
# failures in that category are logged but not re-filed.

param(
    [switch]$DryRun,
    [switch]$Scheduled,       # launched by the scheduled task: honor saved config, may hibernate
    [switch]$NoFuzz,          # skip the fuzz leg regardless of config
    [switch]$HibernateAfter,  # manual override: hibernate when the run finishes
    [switch]$SelfTest         # exercise config/status/idle helpers and exit
)

$ErrorActionPreference = 'Continue'
$repo = 'deblasis/ghostty'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot '..\..')).Path
$logDir = Join-Path $repoRoot '.claude\nightly-logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$configFile = Join-Path $logDir 'nightly-config.json'
$statusFile = Join-Path $logDir 'status.json'
$stamp = Get-Date -Format 'yyyy-MM-dd_HHmm'
$log = Join-Path $logDir "$stamp.log"

# Saved config (defaults apply when the file is missing or partial).
$config = @{ hibernateAfter = $true; runFuzz = $true }
if (Test-Path $configFile) {
    try {
        (Get-Content $configFile -Raw | ConvertFrom-Json).psobject.Properties |
            ForEach-Object { $config[$_.Name] = $_.Value }
    } catch { Write-Host "nightly: unreadable config, using defaults ($_)" }
}

if (-not ('NightlyIdleProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NightlyIdleProbe {
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    public static double MinutesIdle() {
        var lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>();
        if (!GetLastInputInfo(ref lii)) return 0;
        uint now = unchecked((uint)Environment.TickCount);
        return (now - lii.dwTime) / 60000.0;
    }
}
'@
}

# A user typing right now must not have input stolen by the fuzz leg, and
# must never have the machine hibernated under them. Idle gating means
# waiting for continuous idle, not skipping: returns true once the desktop
# has been idle for $IdleMinutes, false if that never happens within
# $TimeoutMinutes.
function Wait-ForIdle([double]$IdleMinutes = 3, [int]$TimeoutMinutes = 30) {
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ($true) {
        $idle = [NightlyIdleProbe]::MinutesIdle()
        if ($idle -ge $IdleMinutes) { return $true }
        if ((Get-Date) -ge $deadline) { return $false }
        Start-Sleep -Seconds 30
    }
}

$script:status = [ordered]@{
    phase = 'starting'; pid = $PID; started = (Get-Date -Format 'o')
    updated = $null; sha = $null; results = [ordered]@{}; log = $log
}
function Write-Status([string]$phase) {
    $script:status.phase = $phase
    $script:status.updated = Get-Date -Format 'o'
    try { $script:status | ConvertTo-Json -Depth 4 | Set-Content $statusFile } catch {}
}

if ($SelfTest) {
    $idle = [NightlyIdleProbe]::MinutesIdle()
    if ($idle -lt 0) { Write-Host "SELF-TEST FAILED: negative idle $idle"; exit 1 }
    Write-Status 'self-test'
    $back = Get-Content $statusFile -Raw | ConvertFrom-Json
    if ($back.phase -ne 'self-test' -or -not $back.log) { Write-Host 'SELF-TEST FAILED: status roundtrip'; exit 1 }
    @{ hibernateAfter = $false; runFuzz = $true } | ConvertTo-Json | Set-Content $configFile
    $cfg = Get-Content $configFile -Raw | ConvertFrom-Json
    if ($cfg.hibernateAfter) { Write-Host 'SELF-TEST FAILED: config roundtrip'; exit 1 }
    Remove-Item $configFile, $statusFile -ErrorAction SilentlyContinue
    if (-not (Wait-ForIdle -IdleMinutes 0 -TimeoutMinutes 0)) { Write-Host 'SELF-TEST FAILED: zero-threshold idle wait'; exit 1 }
    if (Wait-ForIdle -IdleMinutes 99999 -TimeoutMinutes 0) { Write-Host 'SELF-TEST FAILED: timeout path returned true'; exit 1 }
    Write-Host "SELF-TEST PASSED (idle=$([math]::Round($idle,1))m)"
    exit 0
}

Start-Transcript -Path $log | Out-Null
Write-Status 'preparing'

function Get-LogTail {
    if (Test-Path $log) { (Get-Content $log -Tail 60) -join "`n" } else { '' }
}

function File-Issue([string]$title, [string]$detail) {
    $open = gh issue list --repo $repo --state open --search "in:title `"$title`"" --json number --jq 'length' 2>$null
    if ($open -and [int]$open -gt 0) {
        Write-Host "nightly: issue already open for '$title', not re-filing"
        return
    }
    if ($DryRun) { Write-Host "nightly: DRYRUN would file '$title'"; return }
    gh label create P1 --repo $repo --color B60205 --description 'Break found by the nightly quality run' 2>$null
    $bodyFile = Join-Path $logDir "issue-body.tmp.md"
    @"
Found by the nightly quality run on $(Get-Date -Format 'yyyy-MM-dd HH:mm') at commit $script:sha.

$detail

Log tail:
``````
$(Get-LogTail)
``````
Full log: .claude/nightly-logs/$stamp.log on the build machine.
"@ | Set-Content $bodyFile
    gh issue create --repo $repo --title $title --label P1 --body-file $bodyFile
    Remove-Item $bodyFile -ErrorAction SilentlyContinue
}

# Scheduled runs start only once the user has stepped away: 3 minutes of
# continuous idle, waited for rather than assumed. Manual runs start
# immediately (the user just launched them) and only the fuzz leg re-checks.
if ($Scheduled) {
    Write-Status 'waiting-for-idle'
    Write-Host 'nightly: waiting for 3 minutes of continuous idle before starting (up to 3 hours)'
    if (-not (Wait-ForIdle -IdleMinutes 3 -TimeoutMinutes 180)) {
        Write-Host 'nightly: user stayed active for 3 hours; giving up for tonight (no hibernate)'
        Write-Status 'aborted-user-active'
        Stop-Transcript | Out-Null
        exit 0
    }
}

# Fresh checkout of origin/windows in a dedicated worktree, so nightly runs
# never touch a worktree a session is using.
git -C $repoRoot fetch origin windows
$wt = Join-Path $repoRoot '.claude\worktrees\nightly'
if (-not (Test-Path $wt)) {
    git -C $repoRoot worktree add --detach $wt origin/windows
}
git -C $wt checkout --detach origin/windows
git -C $wt reset --hard origin/windows
git -C $wt clean -fdx -e .zig-cache -e zig-out
$script:sha = (git -C $wt rev-parse --short HEAD).Trim()
$script:status.sha = $script:sha
Write-Host "nightly: running against origin/windows @ $script:sha"

# Leg 1: full test ladder (headless).
Write-Status 'zig-tests'
just --justfile (Join-Path $wt 'justfile') --working-directory $wt test
$testRc = $LASTEXITCODE
$script:status.results['zig-tests'] = $testRc
Write-Host "nightly: zig tests rc=$testRc"

Write-Status 'windows-tests'
just --justfile (Join-Path $wt 'justfile') --working-directory $wt test-win
$testWinRc = $LASTEXITCODE
$script:status.results['windows-tests'] = $testWinRc
Write-Host "nightly: windows tests rc=$testWinRc"

if ($testRc -ne 0) { File-Issue '[nightly] zig test suite failed on windows branch' "``just test`` exited $testRc." }
if ($testWinRc -ne 0) { File-Issue '[nightly] Windows test suite failed on windows branch' "``just test-win`` exited $testWinRc." }

# Leg 2: fuzz suite (needs an unlocked interactive desktop with nobody typing).
$locked = [bool](Get-Process LogonUI -ErrorAction SilentlyContinue)
$fuzzStateFile = Join-Path $logDir 'last-fuzz-success.txt'
$skipFuzz = $NoFuzz -or (-not $config.runFuzz)
if ($skipFuzz) {
    Write-Host 'nightly: fuzz leg disabled for this run (recorded as a skip, not a pass)'
    $script:status.results['fuzz'] = 'disabled'
} elseif ($locked) {
    Write-Host 'nightly: workstation is locked, skipping the GUI fuzz leg (recorded as a skip, not a pass)'
    $script:status.results['fuzz'] = 'skipped-locked'
} elseif (-not (Wait-ForIdle -IdleMinutes 3 -TimeoutMinutes 30)) {
    # The headless legs take a while; the user may have come back. The fuzz
    # leg steals real input, so it waits for idle again and skips if the
    # desktop stays busy for half an hour.
    Write-Host 'nightly: desktop stayed active for 30 minutes, skipping the GUI fuzz leg so it cannot steal input'
    $script:status.results['fuzz'] = 'skipped-user-active'
} else {
    Write-Status 'fuzz'
    just --justfile (Join-Path $wt 'justfile') --working-directory $wt fuzz
    $fuzzRc = $LASTEXITCODE
    $script:status.results['fuzz'] = $fuzzRc
    Write-Host "nightly: fuzz rc=$fuzzRc"
    switch ($fuzzRc) {
        0 { Get-Date -Format 'yyyy-MM-dd' | Set-Content $fuzzStateFile }
        1 { File-Issue '[nightly] fuzz suite found product failures' "``just fuzz`` exited 1 (product findings)." }
        default { File-Issue '[nightly] fuzz suite could not run' "``just fuzz`` exited $fuzzRc (harness failure, coverage is not running)." }
    }
}

# Starvation check: a skipped fuzz leg must not silently become the norm.
$lastSuccess = if (Test-Path $fuzzStateFile) { [datetime](Get-Content $fuzzStateFile -TotalCount 1) } else { $null }
if (-not $lastSuccess -or ((Get-Date) - $lastSuccess).TotalDays -gt 7) {
    File-Issue '[nightly] fuzz starvation: no successful fuzz run in 7 days' 'The GUI fuzz leg has been skipped or failing for over a week; fuzz coverage is effectively off.'
}

Write-Status 'done'
Stop-Transcript | Out-Null

# Power-down for the appliance flow: scheduled runs hibernate when the saved
# config says so; manual runs only with -HibernateAfter. Never under an
# active user, and a failed hibernate leaves the machine on rather than
# falling back to anything destructive.
$wantHibernate = $HibernateAfter -or ($Scheduled -and $config.hibernateAfter)
if ($wantHibernate -and -not $DryRun) {
    if (-not (Wait-ForIdle -IdleMinutes 3 -TimeoutMinutes 15)) {
        Write-Host 'nightly: desktop active, not hibernating'
    } else {
        Write-Status 'hibernating'
        shutdown /h
        if ($LASTEXITCODE -ne 0) {
            Write-Host "nightly: hibernate failed (rc=$LASTEXITCODE); is hibernation enabled? (powercfg /availablesleepstates)"
            Write-Status 'hibernate-failed'
        }
    }
}
