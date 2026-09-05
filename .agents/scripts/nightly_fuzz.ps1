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
# The test legs run under the wintty-build lane (AGENTS.md, heavy job
# lanes): `just test` and `just test-win` are single-class recipes that stay
# lane-free inside so they run on any host, so their caller wraps them, and
# the nightly is a caller like any other. `just fuzz` takes its own two
# lanes per phase and is called bare. incoda is therefore preflighted with
# the rest: an unlaned nightly would drop a full zig build and a dotnet test
# run next to whatever a session is already building, which is the collision
# the lanes exist to prevent.
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
    # $null explicitly, not left to [int] coercion, which turns "no exit code
    # was ever collected" into 0, into a clean night - one that stamps
    # last-fuzz-success and so resets the very starvation check that exists to
    # notice the leg never ran. The run site's `?? 1` makes that unreachable
    # today, and a guard two hundred lines away is not a guard.
    if ($null -eq $Code) { return 'harness' }
    switch ([int]$Code) {
        0       { 'clean' }
        2       { 'findings' }
        1       { 'harness' }
        default { 'unknown' }
    }
}

# incoda's lookup, the justfile's: PATH, then the installer's location. The
# agent shell this was written under had the latter and not the former.
function Get-IncodaPath {
    $found = (Get-Command incoda -ErrorAction SilentlyContinue)?.Source
    if ($found) { return $found }
    $installed = Join-Path $env:LOCALAPPDATA 'Programs\incoda\incoda.exe'
    if (Test-Path $installed) { return $installed }
    return $null
}

# The argv for one laned test leg. Built here rather than inline so the
# self-test can assert the wrap is actually there: a leg that quietly lost
# its `incoda run` prefix would still pass every other check in this file
# and only show up as a machine falling over at 23:00.
function Get-LegArgs([string]$Recipe, [string]$Reason, [string]$Justfile, [string]$WorkTree) {
    # --wait 2h, spelled out rather than left to incoda's 30-minute default.
    # The build lane runs three at a time and a busy evening queues deep:
    # measured with 3 holders and 8 waiters, tickets that had waited 34, 27
    # and 26 minutes were still acquiring normally. At the default those
    # legs would have timed out and filed an infra issue against a machine
    # that was working exactly as designed. Not a negative wait (incoda's
    # "wait forever"): a nightly that hangs on the queue into the working
    # day never reaches the code that files the issue saying it could not
    # run, so it fails silently and steals the desktop besides. A finite
    # wait plus the could-not-run classification below is the honest pair.
    @('run', '--queue', 'wintty-build', '--wait', '2h', '--reason', $Reason, '--',
      'just', '--justfile', $Justfile, '--working-directory', $WorkTree, $Recipe)
}

# Running one leg, separated from deciding what to run so the self-test can
# exercise each half. This half is where a leg's own output has to go
# somewhere other than the return value: a native command inside a function
# sends its stdout to the caller, so without Out-Host the "exit code" would
# come back as an array of every line the build printed with the status
# tacked on the end, and every comparison against it would be nonsense.
# Out-Host is also what Start-Transcript records, so the log tail an issue
# quotes is unchanged.
$script:LegRunner = {
    param($exe, $argv)
    $global:LASTEXITCODE = $null
    & $exe @argv | Out-Host
    $LASTEXITCODE ?? 1
}

# One leg, wrapped. Both call sites go through here so the self-test can run
# a leg with an injected runner and see the argv that would have been
# executed: asserting Get-LegArgs alone proved only that a builder nobody
# had to call built the right list, and a call site reverted to a bare
# `just` would have kept every assertion green.
function Invoke-Leg {
    param(
        [string]$Recipe,
        [string]$Reason,
        [string]$Justfile,
        [string]$WorkTree,
        [scriptblock]$Runner
    )
    $legArgs = Get-LegArgs $Recipe $Reason $Justfile $WorkTree
    [int](& ($Runner ?? $script:LegRunner) $script:incoda $legArgs)
}

# incoda's own exit codes, from its `--help` table and AGENTS.md ("heavy
# job lanes"). `run` passes the child's status through unchanged, so any
# code NOT in this list is `just`'s own and means the recipe ran and
# reached a verdict. Every code in it means the opposite: the recipe never
# started, or was stopped from outside, so nothing about the product was
# measured. Filing one of these as a red test suite puts a product title on
# a scheduling problem - the same mistake, the other way round, that
# Get-FuzzOutcome exists to prevent, and until this list existed only 121
# was spared: a lane kill (124) or a run refused by a closed queue (120)
# was filed as "the test suite failed".
#   120 usage error: a closed queue, a missing --reason, a bad flag
#   121 --wait elapsed while still queued
#   122 the state dir or its file locking is unusable
#   123 the lane was acquired but the command could not be started
#   124 the run was killed through the lane (incoda kill)
#   125 a kill went unacknowledged
#   130 incoda was interrupted while queueing
# The overlap is real and deliberate: a `just` recipe could in principle
# exit 124 itself. Reading the ambiguous codes as could-not-run files an
# infra issue for something that might have been a product break, which is
# the safe direction - it never invents a product bug, and the issue it
# does file names the exit code.
$script:LaneExitCodes = @(120, 121, 122, 123, 124, 125, 130)

# What a laned test leg's exit code means. A function so the self-test
# asserts against THIS mapping, not a copy of it.
function Get-LegOutcome {
    param($Code)
    # $null explicitly: an exit code that was never collected means the leg
    # was not observed, not that it passed.
    if ($null -eq $Code) { return 'could-not-run' }
    $c = [int]$Code
    if ($c -eq 0) { return 'pass' }
    if ($script:LaneExitCodes -contains $c) { return 'could-not-run' }
    'fail'
}

# The sentence a could-not-run leg's issue carries. Per code, because "the
# lane was never granted within the wait" is simply untrue of a job someone
# killed, and an issue that misdescribes what happened sends whoever reads
# it looking at the queue depth instead of at the kill reason on stderr.
function Get-LegNote {
    param($Code)
    if ($null -eq $Code) { return 'No exit code was collected, so the leg was never observed and nothing was judged.' }
    $tail = 'This is a runner problem, not a product break: nothing was judged.'
    switch ([int]$Code) {
        121 { "The wintty-build lane was not granted within incoda's wait, so the recipe never started. $tail" }
        124 { "The run was killed through the lane (incoda kill); the reason is on its stderr. $tail" }
        120 { "incoda refused the run (a closed queue, a missing --reason, or a bad flag), so the recipe never started. $tail" }
        123 { "The lane was acquired but the command could not be started. $tail" }
        default { "incoda could not carry the run (see its exit-code table), so the recipe never reached a verdict. $tail" }
    }
}

# Whether the branch went unverified. A leg that could not run is no more a
# green than a red one, so both keep the deferred ledger unsettled. The
# fuzz leg is the odd one: a skip records a string ('skipped-locked'), not
# an int, and a skip is not a failure - hence the [int] test rather than a
# bare -ne 0. A function so the self-test can state that directly.
function Test-LegFailed {
    param($TestRc, $TestWinRc, $FuzzRc)
    ($TestRc -ne 0) -or ($TestWinRc -ne 0) -or ($FuzzRc -is [int] -and $FuzzRc -ne 0)
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
    # $null is the input that matters, not `$null ?? 1`: `??` is evaluated
    # before the call, so asserting on it would only re-test code 1 above while
    # reading like coverage of the missing-exit-code path. An exit code that was
    # never collected must not coerce to 0 and pass as a clean night.
    if ((Get-FuzzOutcome $null) -ne 'harness') { Write-Host 'SELF-TEST FAILED: an uncollected exit code must not read as a clean run'; $failed = $true }

    # The test legs' lane wrap and its exit codes. The wrap is asserted
    # twice over: once as the argv a leg actually executes, and once
    # against this file's own source at both call sites, because argv from
    # a builder nobody is obliged to call is not evidence that the shipped
    # legs are wrapped.
    $legArgs = @(Get-LegArgs 'test' 'nightly zig tests' 'C:\wt\justfile' 'C:\wt')
    $sep = [array]::IndexOf($legArgs, '--')
    $waitIdx = [array]::IndexOf($legArgs, '--wait')
    $reasonIdx = [array]::IndexOf($legArgs, '--reason')
    if ($legArgs[0] -ne 'run' -or $legArgs[1] -ne '--queue' -or $legArgs[2] -ne 'wintty-build') {
        Write-Host "SELF-TEST FAILED: a test leg must run under the wintty-build lane, got '$($legArgs -join ' ')'"; $failed = $true
    }
    if ($reasonIdx -lt 0 -or -not $legArgs[$reasonIdx + 1]) {
        Write-Host 'SELF-TEST FAILED: the lane refuses a run without --reason, so the leg must carry one'; $failed = $true
    }
    if ($sep -lt 0 -or $legArgs[$sep + 1] -ne 'just' -or $legArgs[-1] -ne 'test') {
        Write-Host "SELF-TEST FAILED: the wrapped command must be the just recipe, got '$($legArgs -join ' ')'"; $failed = $true
    }
    # The wait is stated, longer than incoda's 30-minute default, and
    # finite. A negative wait is incoda's "wait forever": the nightly would
    # hang on the queue into the working day and never reach the code that
    # files the issue saying it could not run.
    if ($waitIdx -lt 0 -or $waitIdx -gt $sep) {
        Write-Host 'SELF-TEST FAILED: a test leg must state its own --wait, not inherit incoda''s 30-minute default'; $failed = $true
    } elseif ($legArgs[$waitIdx + 1] -notmatch '^\d+(\.\d+)?[hm]?$' -or [double]($legArgs[$waitIdx + 1] -replace '[hm]$','') -le 0) {
        Write-Host "SELF-TEST FAILED: --wait must be a positive finite duration, got '$($legArgs[$waitIdx + 1])' (negative means wait forever)"; $failed = $true
    }

    # Both legs, as they are actually invoked. The runner stands in for
    # incoda and reports what it was handed, so a call site that lost its
    # wrap fails here rather than at 23:00 on a machine nobody is watching.
    foreach ($leg in @(
        @{ recipe = 'test';     reason = 'nightly zig tests' }
        @{ recipe = 'test-win'; reason = 'nightly windows tests' }
    )) {
        $seen = $null
        $rc = Invoke-Leg -Recipe $leg.recipe -Reason $leg.reason -Justfile 'C:\wt\justfile' -WorkTree 'C:\wt' -Runner {
            param($exe, $argv) $script:seen = $argv; 121
        }
        $seen = @($seen)
        if ($rc -ne 121) { Write-Host "SELF-TEST FAILED: a leg must report the status of the run it made, got $rc"; $failed = $true }
        if ($seen[0] -ne 'run' -or $seen[2] -ne 'wintty-build' -or $seen[-1] -ne $leg.recipe) {
            Write-Host "SELF-TEST FAILED: the $($leg.recipe) leg must execute a wintty-build run of that recipe, got '$($seen -join ' ')'"; $failed = $true
        }
    }
    # The runner the legs actually use, against a real child that prints
    # and then fails. A leg's status has to come back as one integer: a
    # native command inside a function writes its stdout to the caller, so
    # a runner that let it would return the whole build log with the code
    # at the end, and `$testRc -ne 0` on an array is not the question
    # anyone meant to ask.
    $realRc = & $script:LegRunner 'pwsh' @('-NoProfile', '-Command', "'self-test: a leg''s output belongs in the transcript, not in its exit code'; exit 7")
    if (@($realRc).Count -ne 1 -or $realRc -ne 7) {
        Write-Host "SELF-TEST FAILED: a leg must report one exit code and let its output go to the transcript, got '$($realRc -join ', ')'"; $failed = $true
    }

    # And that this file's two shipped call sites really are those legs. A
    # leg reverted to a bare `just --justfile ... test` would satisfy every
    # assertion above, which is precisely the failure the wrap exists for.
    $src = Get-Content $PSCommandPath -Raw
    foreach ($recipe in @('test', 'test-win')) {
        if ($src -notmatch [regex]::Escape("Invoke-Leg -Recipe '$recipe'")) {
            Write-Host "SELF-TEST FAILED: the $recipe leg must be invoked through Invoke-Leg, so it cannot lose its lane"; $failed = $true
        }
    }
    if ($src -match '(?m)^\s*(just|& \$incoda)\s.*--working-directory\s+\$wt\s+test(-win)?\s*$') {
        Write-Host 'SELF-TEST FAILED: a test leg is running outside Invoke-Leg, so its lane wrap is not guarded'; $failed = $true
    }
    # The issue-filing arms, likewise: an `if ($testRc -ne 0)` in place of
    # the switch would file every lane-level code as a red test suite.
    # Assembled rather than written out, for the same reason as $arm below:
    # spelled literally, the assertion would find itself and pass over a
    # call site that no longer consults Get-LegOutcome at all.
    foreach ($v in @('testRc', 'testWinRc')) {
        $needle = 'switch (Get-LegOutcome $' + $v + ')'
        if ($src -notmatch [regex]::Escape($needle)) {
            Write-Host "SELF-TEST FAILED: missing '$needle'; both legs must decide what to file through Get-LegOutcome"; $failed = $true
        }
    }
    # Built from two pieces so this line is not itself a third match: the
    # source being counted is this same file.
    $arm = "'could-not-run'" + ' { File-Issue'
    if (([regex]::Matches($src, [regex]::Escape($arm))).Count -ne 2) {
        Write-Host 'SELF-TEST FAILED: both legs must file an infra issue for a run that never happened, not a red test suite'; $failed = $true
    }

    # incoda's exit codes, one at a time. Every one of them means the
    # recipe never reached a verdict; before this, only 121 was spared and
    # a lane kill was filed as "the test suite failed".
    foreach ($c in @(
        @{ code = 0;   want = 'pass';          why = '0 is a green leg' }
        @{ code = 1;   want = 'fail';          why = 'the child ran and failed' }
        @{ code = 2;   want = 'fail';          why = 'a child status incoda passed through is the recipe''s verdict' }
        @{ code = 120; want = 'could-not-run'; why = 'incoda 120 is a usage error: a closed queue or a missing --reason, so the recipe never started' }
        @{ code = 121; want = 'could-not-run'; why = 'incoda 121 means the lane was never granted, so the recipe never started' }
        @{ code = 122; want = 'could-not-run'; why = 'incoda 122 means the state dir is unusable, so nothing was queued' }
        @{ code = 123; want = 'could-not-run'; why = 'incoda 123 means the lane was taken but the command never started' }
        @{ code = 124; want = 'could-not-run'; why = 'incoda 124 means someone killed the run through the lane' }
        @{ code = 125; want = 'could-not-run'; why = 'incoda 125 is an unacknowledged kill, not a product break' }
        @{ code = 130; want = 'could-not-run'; why = 'incoda 130 means it was interrupted while queueing' }
    )) {
        $got = Get-LegOutcome $c.code
        if ($got -ne $c.want) {
            Write-Host "SELF-TEST FAILED: leg exit $($c.code) mapped to '$got', expected '$($c.want)' ($($c.why))"
            $failed = $true
        }
    }
    if ((Get-LegOutcome $null) -ne 'could-not-run') { Write-Host 'SELF-TEST FAILED: an uncollected leg exit code must not read as a pass'; $failed = $true }
    # A killed run and a queue that never came free are different events,
    # and an issue that describes one as the other sends its reader to the
    # wrong place.
    if ((Get-LegNote 124) -notmatch 'killed') { Write-Host 'SELF-TEST FAILED: a killed leg must say so, not report a queue timeout'; $failed = $true }
    if ((Get-LegNote 121) -notmatch 'never granted|not granted') { Write-Host 'SELF-TEST FAILED: a timed-out leg must say the lane was never granted'; $failed = $true }
    if ((Get-LegNote 124) -eq (Get-LegNote 121)) { Write-Host 'SELF-TEST FAILED: a kill and a queue timeout must not carry the same note'; $failed = $true }
    foreach ($c in @(120, 121, 122, 123, 124, 125, 130, $null)) {
        if ((Get-LegNote $c) -notmatch 'not a product break|nothing was judged') {
            Write-Host "SELF-TEST FAILED: the note for exit $c must say nothing about the product was judged"; $failed = $true
        }
    }

    # The roll-up into the exit status and the deferred ledger. A leg that
    # could not run leaves the branch just as unverified as a red one, and
    # a fuzz leg that was skipped records a string, not a failure.
    foreach ($c in @(
        @{ t = 0;   w = 0; f = 0;     want = $false; why = 'three greens settle the ledger' }
        @{ t = 1;   w = 0; f = 0;     want = $true;  why = 'a red zig leg is a failure' }
        @{ t = 0;   w = 1; f = 0;     want = $true;  why = 'a red windows leg is a failure' }
        @{ t = 0;   w = 0; f = 2;     want = $true;  why = 'fuzz findings are a failure' }
        @{ t = 121; w = 0; f = 0;     want = $true;  why = 'a leg that never ran verified nothing, so the ledger stays unsettled' }
        @{ t = 124; w = 0; f = 0;     want = $true;  why = 'a killed leg verified nothing either' }
        @{ t = 0;   w = 0; f = $null; want = $false; why = 'a fuzz leg that never ran is a skip, and the tests still passed' }
        @{ t = 0;   w = 0; f = 'skipped-locked'; want = $false; why = 'a recorded skip is a string, not a nonzero status' }
    )) {
        $got = Test-LegFailed $c.t $c.w $c.f
        if ($got -ne $c.want) {
            Write-Host "SELF-TEST FAILED: legs ($($c.t), $($c.w), $($c.f ?? 'null')) read as failed=$got, expected $($c.want) ($($c.why))"
            $failed = $true
        }
    }

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

# incoda is required for the same reason: the test legs must hold the
# wintty-build lane, and a leg that cannot take it must refuse rather than
# run unlaned next to a session's build. Not in $missing above because the
# lookup is not just PATH.
# $script: explicitly, because Invoke-Leg reads it by that name: an
# unqualified assignment here would work by accident of top-level scope and
# break silently the moment this moved inside anything.
$script:incoda = Get-IncodaPath
if (-not $script:incoda) {
    Write-Status 'aborted-missing-tools'
    Add-Content $log 'nightly: aborting, incoda is not on PATH or in Programs\incoda; the test legs run under the wintty-build lane and must not run unlaned (AGENTS.md, heavy job lanes; https://github.com/deblasis/incoda)'
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
# The greens this run produces are recorded for reuse below; the digest
# they are recorded against must describe the toolchain the legs actually
# ran under, so the environment is snapshotted BEFORE the legs and the
# record refuses when it has moved since. The worktree's own copy of the
# script is used so everything is computed against $wt, not this checkout.
# It runs before the leg's exit-code reset below, so its own exit code can
# never stand in for a `just` that failed to launch.
$legCache = Join-Path $wt '.agents\scripts\leg_cache.py'
$envSnapshot = Join-Path $logDir "$stamp.env.json"
if (Test-Path $legCache) { python $legCache snapshot $envSnapshot }

Write-Status 'zig-tests'
$global:LASTEXITCODE = $null
$legClock = [System.Diagnostics.Stopwatch]::StartNew()
# The justfile pins the test seed so signoff runs can hit zig's run cache;
# the nightly is the run that wants fresh randomness, so it draws one. It
# rides in the failure issue too, or a seed-dependent red could not be
# reproduced from the report.
$env:WINTTY_TEST_SEED = '0x{0:x}' -f (Get-Random -Minimum 1 -Maximum 2147483647)
Write-Host "nightly: WINTTY_TEST_SEED=$env:WINTTY_TEST_SEED"
$testRc = Invoke-Leg -Recipe 'test' -Reason 'nightly zig tests' -Justfile (Join-Path $wt 'justfile') -WorkTree $wt
$testSeconds = [int]$legClock.Elapsed.TotalSeconds
$script:status.results['zig-tests'] = $testRc
Write-Host "nightly: zig tests rc=$testRc"

Write-Status 'windows-tests'
$global:LASTEXITCODE = $null
$legClock.Restart()
$testWinRc = Invoke-Leg -Recipe 'test-win' -Reason 'nightly windows tests' -Justfile (Join-Path $wt 'justfile') -WorkTree $wt
$testWinSeconds = [int]$legClock.Elapsed.TotalSeconds
$script:status.results['windows-tests'] = $testWinRc
Write-Host "nightly: windows tests rc=$testWinRc"

# A green leg here is a green for its content digest, and the store is
# shared by every worktree of this repo: recording it lets every branch
# whose inputs for that leg equal origin/windows carry it in its own
# signoff. Refusals (a dirty worktree, a toolchain that moved since the
# snapshot) print their reason and cost nothing; the nightly's verdict
# does not depend on this.
if ((Test-Path $legCache) -and $script:sha -and (Test-Path $envSnapshot)) {
    if ($testRc -eq 0) {
        python $legCache record zig-tests --from-sha $script:sha --origin observed --seconds $testSeconds --env-snapshot $envSnapshot
    }
    if ($testWinRc -eq 0) {
        python $legCache record windows-tests --from-sha $script:sha --origin observed --seconds $testWinSeconds --env-snapshot $envSnapshot
    }
}

switch (Get-LegOutcome $testRc) {
    'fail'          { File-Issue '[nightly] zig test suite failed on windows branch' "``just test`` exited $testRc (WINTTY_TEST_SEED=$env:WINTTY_TEST_SEED)." }
    'could-not-run' { File-Issue '[nightly] infra: the zig test leg never ran' "incoda exited $testRc. $(Get-LegNote $testRc)" }
}
switch (Get-LegOutcome $testWinRc) {
    'fail'          { File-Issue '[nightly] Windows test suite failed on windows branch' "``just test-win`` exited $testWinRc." }
    'could-not-run' { File-Issue '[nightly] infra: the Windows test leg never ran' "incoda exited $testWinRc. $(Get-LegNote $testWinRc)" }
}

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
        'findings' { File-Issue '[nightly] fuzz suite found product failures' "``just fuzz`` exited $fuzzRc (product findings)." }
        'harness'  { File-Issue '[nightly] fuzz suite could not run' "``just fuzz`` exited $fuzzRc (harness failure, coverage is not running)." }
        'unknown'  { File-Issue '[nightly] fuzz suite could not run' "``just fuzz`` exited $fuzzRc, outside the suite's 0/1/2 contract, so the product was never judged." }
    }
}

# Starvation check: a skipped fuzz leg must not silently become the norm.
$lastSuccess = if (Test-Path $fuzzStateFile) { [datetime](Get-Content $fuzzStateFile -TotalCount 1) } else { $null }
if (-not $lastSuccess -or ((Get-Date) - $lastSuccess).TotalDays -gt 7) {
    File-Issue '[nightly] fuzz starvation: no successful fuzz run in 7 days' "The GUI fuzz leg has been skipped or failing for over a week; fuzz coverage is effectively off. $wakeLockNote"
}

$legFailed = Test-LegFailed $testRc $testWinRc $fuzzRc

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
