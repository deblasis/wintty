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
# legs finish and re-checks the lock state after that wait (locking is
# itself input, so the pre-wait state can be stale). A skip is recorded
# rather than counted as a pass, and if no fuzz leg has succeeded for 7
# days that starvation is itself filed as an issue. Note the appliance
# trade-off: a box that requires sign-in on wake resumes to the lock
# screen, so hibernate-after and the fuzz leg only coexist when sign-in on
# wake is off; the starvation issue carries that hint.
#
# Failure honesty: the required tools are preflighted, every leg treats a
# missing exit code as a failure, worktree preparation failures file an
# infra-titled issue instead of masquerading as product breaks, and the
# script exits nonzero when any leg failed.
#
# Hibernate only happens for scheduled runs (or -HibernateAfter), only when
# the saved config allows it, and only after the same continuous-idle check,
# so the machine is never hibernated under someone using it.
#
# Config and status live next to the logs (.agents/nightly-logs/), managed
# by nightly_control.ps1: nightly-config.json {hibernateAfter, runFuzz},
# status.json {phase, sha, results, log}.
#
# Issue filing dedups by exact open-issue title, compared client-side
# because the search API does not cover forks.

param(
    [switch]$DryRun,
    [switch]$Scheduled,       # launched by the scheduled task: honor saved config, may hibernate
    [switch]$NoFuzz,          # skip the fuzz leg regardless of config
    [switch]$HibernateAfter,  # manual override: hibernate when the run finishes
    [switch]$SelfTest         # exercise config/status/idle helpers (in a temp dir) and exit
)

$ErrorActionPreference = 'Continue'
$repo = 'deblasis/ghostty'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot '..\..')).Path
$logDir = Join-Path $repoRoot '.agents\nightly-logs'
if ($SelfTest) {
    # The self-test must never touch the real config, status, or logs.
    $logDir = Join-Path ([System.IO.Path]::GetTempPath()) "nightly-selftest-$PID"
}
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$configFile = Join-Path $logDir 'nightly-config.json'
$statusFile = Join-Path $logDir 'status.json'
$stamp = "{0}-{1}" -f (Get-Date -Format 'yyyy-MM-dd_HHmm'), $PID
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

function Test-Locked { [bool](Get-Process LogonUI -ErrorAction SilentlyContinue) }

$script:status = [ordered]@{
    phase = 'starting'; pid = $PID; started = (Get-Date -Format 'o')
    updated = $null; sha = $null; results = [ordered]@{}; log = $log
}
function Write-Status([string]$phase) {
    $script:status.phase = $phase
    $script:status.updated = Get-Date -Format 'o'
    # Atomic replace so the control panel's 2s reader never sees a torn file.
    try {
        $tmp = "$statusFile.$PID.tmp"
        $script:status | ConvertTo-Json -Depth 4 | Set-Content $tmp
        Move-Item -Force $tmp $statusFile
    } catch {}
}

# The fuzz suite's exit codes carry the suite's whole point (the table in
# windows/scripts/README.md, decided in fuzz-suite.ps1's Get-Verdict): 2 is a
# product finding, 1 is a harness that never exercised the product. Reading
# them the wrong way round files a product bug against a broken harness and
# buries a real finding under an infra title, and the inverted switch that did
# it here read plausibly for months. Kept as a function so the self-test can
# assert against THIS mapping rather than a copy of the rules, which is what
# lets a self-test pass while the shipped roll-up is inverted.
function Get-FuzzOutcome {
    param($Code)
    switch ([int]$Code) {
        0       { 'clean' }
        2       { 'findings' }
        1       { 'harness' }
        default { 'unknown' }
    }
}

if ($SelfTest) {
    $failed = $false
    $idle = [NightlyIdleProbe]::MinutesIdle()
    if ($idle -lt 0) { Write-Host "SELF-TEST FAILED: negative idle $idle"; $failed = $true }
    Write-Status 'self-test'
    $back = Get-Content $statusFile -Raw | ConvertFrom-Json
    if ($back.phase -ne 'self-test' -or -not $back.log) { Write-Host 'SELF-TEST FAILED: status roundtrip'; $failed = $true }
    @{ hibernateAfter = $false; runFuzz = $true } | ConvertTo-Json | Set-Content $configFile
    $cfg = Get-Content $configFile -Raw | ConvertFrom-Json
    if ($cfg.hibernateAfter) { Write-Host 'SELF-TEST FAILED: config roundtrip'; $failed = $true }
    if (-not (Wait-ForIdle -IdleMinutes 0 -TimeoutMinutes 0)) { Write-Host 'SELF-TEST FAILED: zero-threshold idle wait'; $failed = $true }
    if (Wait-ForIdle -IdleMinutes 99999 -TimeoutMinutes 0) { Write-Host 'SELF-TEST FAILED: timeout path returned true'; $failed = $true }
    # The fuzz exit-code mapping, asserted one code at a time rather than as a
    # table lookup that would restate Get-FuzzOutcome's switch and agree with
    # it however it is written. This shipped inverted - 1 filed as a product
    # finding, 2 falling through to "harness failure" - and no self-test
    # noticed, because nothing here read the mapping at all.
    foreach ($c in @(
        @{ code = 0; want = 'clean';    why = '0 is a clean run' }
        @{ code = 1; want = 'harness';  why = '1 means the harness could not run, so no product bug is filed' }
        @{ code = 2; want = 'findings'; why = '2 means real product findings' }
        @{ code = 7; want = 'unknown';  why = 'a code outside the contract is not silently a pass' }
    )) {
        $got = Get-FuzzOutcome $c.code
        if ($got -ne $c.want) {
            Write-Host "SELF-TEST FAILED: fuzz exit $($c.code) mapped to '$got', expected '$($c.want)' ($($c.why))"
            $failed = $true
        }
    }
    # An unset exit code is 'nothing was measured', never a finding: the run
    # site defaults it with `?? 1`, and this pins the value that default feeds.
    if ((Get-FuzzOutcome ($null ?? 1)) -ne 'harness') { Write-Host 'SELF-TEST FAILED: missing exit code must degrade to a harness failure, not a product finding'; $failed = $true }

    # The self-test dir must be disjoint from the real logs, or a run of the
    # self-test would wipe the user's saved options.
    if ($logDir -eq (Join-Path $repoRoot '.agents\nightly-logs')) { Write-Host 'SELF-TEST FAILED: not sandboxed'; $failed = $true }
    Remove-Item -Recurse -Force $logDir -ErrorAction SilentlyContinue
    if ($failed) { Write-Host 'SELF-TEST FAILED'; exit 1 }
    Write-Host "SELF-TEST PASSED (idle=$([math]::Round($idle,1))m)"
    exit 0
}

# Tool preflight: a missing tool must be a loud abort, not a green night.
# Command-not-found does not touch $LASTEXITCODE, so without this check a
# missing `just` would leave every leg reading a stale zero.
$missing = @('git', 'gh', 'just', 'pwsh') | Where-Object { -not (Get-Command $_ -ErrorAction SilentlyContinue) }
if ($missing) {
    Write-Status 'aborted-missing-tools'
    Add-Content $log "nightly: aborting, tools not on PATH in this session: $($missing -join ', ')"
    exit 1
}

# Single instance: a manual run and the scheduled run share one worktree, so
# a second starter defers to a live first one instead of stomping it.
if (Test-Path $statusFile) {
    try {
        $prev = Get-Content $statusFile -Raw | ConvertFrom-Json
        $active = @('starting', 'waiting-for-idle', 'preparing', 'zig-tests', 'windows-tests', 'fuzz', 'hibernating')
        if ($prev -and $prev.phase -in $active -and $prev.pid -ne $PID -and (Get-Process -Id $prev.pid -ErrorAction SilentlyContinue)) {
            Write-Host "nightly: run $($prev.pid) is already active (phase $($prev.phase)); exiting"
            exit 0
        }
    } catch {}
}

Start-Transcript -Path $log | Out-Null
Write-Status 'starting'

function Get-LogTail {
    if (Test-Path $log) {
        # Indented, not fenced: a transcript line containing a fence would
        # otherwise break the issue body's formatting.
        (Get-Content $log -Tail 60 | ForEach-Object { "    $_" }) -join "`n"
    } else { '' }
}

function File-Issue([string]$title, [string]$detail) {
    # Client-side dedup: the search API refuses to search forks, so an exact
    # title comparison against the plain open-issue list is the reliable way.
    $openTitles = @()
    try { $openTitles = @(gh issue list --repo $repo --state open --limit 100 --json title --jq '.[].title' 2>$null) } catch {}
    if ($openTitles -contains $title) {
        Write-Host "nightly: issue already open for '$title', not re-filing"
        return
    }
    if ($DryRun) { Write-Host "nightly: DRYRUN would file '$title'"; return }
    gh label create P1 --repo $repo --color B60205 --description 'Break found by the nightly quality run' 2>$null
    $bodyFile = Join-Path $logDir "issue-body.$PID.tmp.md"
    @"
Found by the nightly quality run on $(Get-Date -Format 'yyyy-MM-dd HH:mm') at commit $script:sha.

$detail

Log tail:

$(Get-LogTail)

Full log: .agents/nightly-logs/$stamp.log on the build machine.
"@ | Set-Content $bodyFile
    $global:LASTEXITCODE = $null
    gh issue create --repo $repo --title $title --label P1 --body-file $bodyFile
    if (($LASTEXITCODE ?? 1) -ne 0) {
        Write-Host "nightly: FAILED to file issue '$title' (gh rc=$LASTEXITCODE)"
        $script:status.results['issue-filing'] = 'failed'
    }
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
# never touch a worktree a session is using. Any failure here is an infra
# problem and must not be filed as a product break.
Write-Status 'preparing'
$global:LASTEXITCODE = $null
git -C $repoRoot fetch origin windows
if (($LASTEXITCODE ?? 1) -ne 0) {
    Write-Host 'nightly: fetch failed; testing the last-known origin/windows ref'
}
$wt = Join-Path $repoRoot '.agents\worktrees\nightly'
git -C $repoRoot worktree prune
if (-not (Test-Path $wt)) {
    git -C $repoRoot worktree add --detach $wt origin/windows
}
git -C $wt checkout --detach origin/windows
$checkoutRc = $LASTEXITCODE ?? 1
git -C $wt reset --hard origin/windows
$resetRc = $LASTEXITCODE ?? 1
git -C $wt clean -fdx -e .zig-cache -e zig-out | Out-Null
$script:sha = (git -C $wt rev-parse --short HEAD 2>$null | Out-String).Trim()
if ($checkoutRc -ne 0 -or $resetRc -ne 0 -or -not $script:sha) {
    Write-Status 'aborted-infra'
    File-Issue '[nightly] infra: could not prepare the nightly worktree' "checkout rc=$checkoutRc, reset rc=$resetRc, sha='$script:sha'. This is a runner problem, not a product break."
    Stop-Transcript | Out-Null
    exit 1
}
$script:status.sha = $script:sha
Write-Host "nightly: running against origin/windows @ $script:sha"

# Legs read their exit codes through `?? 1`: a null (command never ran)
# counts as a failure, never as a stale green.
Write-Status 'zig-tests'
$global:LASTEXITCODE = $null
just --justfile (Join-Path $wt 'justfile') --working-directory $wt test
$testRc = $LASTEXITCODE ?? 1
$script:status.results['zig-tests'] = $testRc
Write-Host "nightly: zig tests rc=$testRc"

Write-Status 'windows-tests'
$global:LASTEXITCODE = $null
just --justfile (Join-Path $wt 'justfile') --working-directory $wt test-win
$testWinRc = $LASTEXITCODE ?? 1
$script:status.results['windows-tests'] = $testWinRc
Write-Host "nightly: windows tests rc=$testWinRc"

if ($testRc -ne 0) { File-Issue '[nightly] zig test suite failed on windows branch' "``just test`` exited $testRc." }
if ($testWinRc -ne 0) { File-Issue '[nightly] Windows test suite failed on windows branch' "``just test-win`` exited $testWinRc." }

$wakeLockNote = 'If this machine wakes from hibernation to the lock screen every night (sign-in on wake), the fuzz leg can never run; the appliance flow needs sign-in on wake disabled to get fuzz coverage.'

# Leg 2: fuzz suite. Wait for idle first, then check the lock state, in
# that order: locking the machine is itself input, so a pre-wait lock check
# would be stale by the time the wait completes.
$fuzzStateFile = Join-Path $logDir 'last-fuzz-success.txt'
$fuzzRc = $null
if ($NoFuzz -or (-not $config.runFuzz)) {
    Write-Host 'nightly: fuzz leg disabled for this run (recorded as a skip, not a pass)'
    $script:status.results['fuzz'] = 'disabled'
} elseif (-not (Wait-ForIdle -IdleMinutes 3 -TimeoutMinutes 30)) {
    Write-Host 'nightly: desktop stayed active for 30 minutes, skipping the GUI fuzz leg so it cannot steal input'
    $script:status.results['fuzz'] = 'skipped-user-active'
} elseif (Test-Locked) {
    Write-Host "nightly: workstation is locked, skipping the GUI fuzz leg (recorded as a skip, not a pass). $wakeLockNote"
    $script:status.results['fuzz'] = 'skipped-locked'
} else {
    Write-Status 'fuzz'
    $global:LASTEXITCODE = $null
    just --justfile (Join-Path $wt 'justfile') --working-directory $wt fuzz
    # `?? 1`, matching every recipe in the justfile: a `just` that never ran
    # leaves $LASTEXITCODE unset, and nothing was measured. Defaulting that to
    # 2 would invent a product finding out of a recipe that never started.
    $fuzzRc = $LASTEXITCODE ?? 1
    $script:status.results['fuzz'] = $fuzzRc
    Write-Host "nightly: fuzz rc=$fuzzRc"
    switch (Get-FuzzOutcome $fuzzRc) {
        'clean'    { Get-Date -Format 'yyyy-MM-dd' | Set-Content $fuzzStateFile }
        'findings' { File-Issue '[nightly] fuzz suite found product failures' "``just fuzz`` exited 2 (product findings)." }
        'harness'  { File-Issue '[nightly] fuzz suite could not run' "``just fuzz`` exited 1 (harness failure, coverage is not running)." }
        'unknown'  { File-Issue '[nightly] fuzz suite could not run' "``just fuzz`` exited $fuzzRc, outside the suite's 0/1/2 contract, so the product was never judged." }
    }
}

# Starvation check: a skipped fuzz leg must not silently become the norm.
$lastSuccess = if (Test-Path $fuzzStateFile) { [datetime](Get-Content $fuzzStateFile -TotalCount 1) } else { $null }
if (-not $lastSuccess -or ((Get-Date) - $lastSuccess).TotalDays -gt 7) {
    File-Issue '[nightly] fuzz starvation: no successful fuzz run in 7 days' "The GUI fuzz leg has been skipped or failing for over a week; fuzz coverage is effectively off. $wakeLockNote"
}

$legFailed = ($testRc -ne 0) -or ($testWinRc -ne 0) -or ($fuzzRc -is [int] -and $fuzzRc -ne 0)

# Deferred signoffs are merges made on credit against exactly this run. A
# green pass over the whole branch is what they were borrowing, so it settles
# the ledger; a red one deliberately leaves the debt standing and visible.
if (-not $legFailed) {
    python (Join-Path $scriptRoot 'signoff.py') --settle "nightly full ladder green at $script:sha"
} else {
    $debt = python (Join-Path $scriptRoot 'signoff.py') --debt 2>$null
    if ($debt) {
        Write-Host "nightly: deferred signoff debt still outstanding after a red run:"
        $debt | ForEach-Object { Write-Host "  $_" }
        File-Issue '[nightly] deferred signoff debt outstanding while the branch is red' "Merges were made on deferred signoffs and the nightly ladder is failing, so nothing has verified them:`n`n$($debt -join "`n")"
    }
}

Write-Status 'done'
Stop-Transcript | Out-Null

# Power-down for the appliance flow: scheduled runs hibernate when the saved
# config says so; manual runs only with -HibernateAfter. Never under an
# active user, and a failed hibernate leaves the machine on rather than
# falling back to anything destructive. The transcript is closed, so
# post-transcript diagnostics go to the log file directly.
$wantHibernate = $HibernateAfter -or ($Scheduled -and $config.hibernateAfter)
if ($wantHibernate -and -not $DryRun) {
    if (-not (Wait-ForIdle -IdleMinutes 3 -TimeoutMinutes 15)) {
        Add-Content $log 'nightly: desktop active, not hibernating'
    } else {
        Write-Status 'hibernating'
        $global:LASTEXITCODE = $null
        shutdown /h
        if (($LASTEXITCODE ?? 1) -ne 0) {
            Add-Content $log "nightly: hibernate failed (rc=$LASTEXITCODE); is hibernation enabled? (powercfg /availablesleepstates)"
            Write-Status 'hibernate-failed'
        } else {
            # Execution resumes here after wake; leave an honest final state
            # instead of a status stuck at 'hibernating'.
            Add-Content $log 'nightly: resumed from hibernation'
            Write-Status 'done'
        }
    }
}

exit ($legFailed ? 1 : 0)
