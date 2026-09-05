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

    These three keys are shared with wintty-release, which builds the same
    thing on the same box under the same names. Description equality is a
    drift trigger, so if that repo ever grows its own applier it must carry
    these strings character for character: two appliers with different prose
    would each see the other's description as drift, heal it, and write a
    config event into the lane's log on every run, forever.

    Exit codes: 0 the lanes match (or, in apply mode, match after applying);
    1 drift, one line per finding; 2 the check itself could not run (incoda
    not found, status unreadable). The lookup is the justfile's: PATH, then
    the installer's location under %LOCALAPPDATA%.
#>

[CmdletBinding()]
param(
    [switch]$Check,    # report drift and exit; apply nothing
    [switch]$SelfTest  # exercise the drift and argv logic; touch no machine state
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# incoda's exit code is read explicitly below; a nonzero one must not become
# a terminating error before it can be reported.
$PSNativeCommandUseErrorActionPreference = $false

# What AGENTS.md ("Heavy job lanes") promises.
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

# A property read that returns $null for a key the JSON did not carry:
# incoda's config object only has the keys that were set, and under strict
# mode a missing property is an error rather than a null.
function Get-Prop($obj, [string]$name) {
    if ($null -ne $obj -and $obj.PSObject.Properties[$name]) { $obj.$name } else { $null }
}

# The key->queue map for one status payload. Split out from Read-Queues so
# the self-test can hand it a payload directly, and because the empty cases
# are the ones that bite: a `queues` that is absent or JSON null reaches
# this as @($null), a ONE-element array whose element indexes the hashtable
# to a null key, which throws. That throw would leave the script exiting 1
# with its message on stderr, and doctor reads exit 1 as drift, so a machine
# that was never read at all would be reported as a lane that drifted.
function ConvertTo-QueueMap($status) {
    $byKey = @{}
    foreach ($q in @(Get-Prop $status 'queues')) {
        $key = Get-Prop $q 'key'
        if ([string]::IsNullOrEmpty($key)) { continue }
        $byKey[$key] = $q
    }
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

# The argv that heals one lane. `incoda config` MERGES into the stored
# configuration rather than replacing it - measured: set --slots 5, then
# apply without --slots, and it reads back 5 - so every field this script
# has an opinion about has to be on the command line every time. Leaving
# --slots off the one-slot lanes meant a wintty-desktop that had drifted to
# 5 was reported by -Check, "healed" by an apply that never mentioned
# slots, and reported again on the next run, forever.
function Get-ApplyArgs($lane) {
    # --open on a lane that is already open is a no-op, and a lane that
    # somehow got closed is drift this has to heal rather than report twice.
    if ($lane.Contains('closed')) { return @('config', $lane.key, '--close', $lane.closed) }
    @('config', $lane.key, '--slots', $lane.slots, '--description', $lane.description, '--require-reason', '--open')
}

$summary = "lanes match: wintty-build has 3 slots, wintty-desktop and wintty-publish 1, all three refuse a run without --reason; wintty is closed"

if ($SelfTest) {
    # No incoda, no state dir, no machine: everything below is the pure
    # logic, exercised against hand-written payloads. It runs before the
    # incoda lookup so `just gates-selftest` passes on a clone that has
    # never heard of the lanes.
    $script:failed = $false
    function Assert-Ok([bool]$ok, [string]$label) {
        if (-not $ok) { Write-Host "SELF-TEST FAILED: $label"; $script:failed = $true }
    }
    $build = $lanes[0]
    $desktop = $lanes[1]
    $retired = $lanes[3]
    function New-Queue($config) { [pscustomobject]@{ key = 'q'; exists = $true; config = $config } }

    # The empty payloads. `queues: []` is what a fresh state dir reports and
    # is fine; absent and null are the shapes that used to throw, and a
    # throw here is exit 1 with the reason on stderr, which doctor reads as
    # drift. An entry with no key is dropped rather than indexing to null.
    Assert-Ok ((ConvertTo-QueueMap ([pscustomobject]@{ queues = @() })).Count -eq 0) 'an empty queue list is an empty map'
    Assert-Ok ((ConvertTo-QueueMap ([pscustomobject]@{ queues = $null })).Count -eq 0) 'a null queue list must not index the map to a null key'
    Assert-Ok ((ConvertTo-QueueMap ([pscustomobject]@{ schema = 1 })).Count -eq 0) 'a payload with no queues list must not throw'
    Assert-Ok ((ConvertTo-QueueMap $null).Count -eq 0) 'a null payload must not throw'
    Assert-Ok ((ConvertTo-QueueMap ([pscustomobject]@{ queues = @([pscustomobject]@{ exists = $true }) })).Count -eq 0) 'a queue with no key is dropped'
    $map = ConvertTo-QueueMap ([pscustomobject]@{ queues = @([pscustomobject]@{ key = 'wintty-build'; exists = $true }) })
    Assert-Ok ($map.Count -eq 1 -and $null -ne $map['wintty-build']) 'a real queue lands under its key'

    # Get-Drift, one condition at a time. The text is doctor's output, so
    # the assertions are on the finding, not on the exact sentence.
    $matching = New-Queue ([pscustomobject]@{ slots = 3; require_reason = $true; description = $build.description })
    Assert-Ok (@(Get-Drift $build $matching).Count -eq 0) 'a lane that matches has no drift'
    Assert-Ok (@(Get-Drift $build $null) -join '' -match 'missing') 'a missing key is drift'
    Assert-Ok (@(Get-Drift $build ([pscustomobject]@{ key = 'x'; exists = $false })) -join '' -match 'missing') 'a key that does not exist is drift'
    # A queue with no config object at all: every field falls back, so all
    # three of this lane's opinions are drift, and none of them may throw.
    $noConfig = [pscustomobject]@{ key = 'wintty-build'; exists = $true }
    $bare = @(Get-Drift $build $noConfig)
    Assert-Ok ($bare.Count -eq 3 -and ($bare -join '') -match 'slots 1, want 3' -and ($bare -join '') -match 'without --reason') 'a queue with no config object drifts on every field'
    $wrongSlots = @(Get-Drift $build (New-Queue ([pscustomobject]@{ slots = 5; require_reason = $true; description = $build.description })))
    Assert-Ok ($wrongSlots.Count -eq 1 -and $wrongSlots[0] -match 'slots 5, want 3') 'wrong slots is drift'
    $zeroSlots = @(Get-Drift $desktop (New-Queue ([pscustomobject]@{ slots = 0; require_reason = $true; description = $desktop.description })))
    Assert-Ok ($zeroSlots.Count -eq 0) "incoda's 0 means the default, and the default is one slot"
    $noReason = @(Get-Drift $build (New-Queue ([pscustomobject]@{ slots = 3; require_reason = $false; description = $build.description })))
    Assert-Ok ($noReason.Count -eq 1 -and $noReason[0] -match 'without --reason') 'a lane that accepts a reasonless run is drift'
    $wrongDesc = @(Get-Drift $build (New-Queue ([pscustomobject]@{ slots = 3; require_reason = $true; description = 'something else' })))
    Assert-Ok ($wrongDesc.Count -eq 1 -and $wrongDesc[0] -match 'description') 'a changed description is drift'
    $closedOpenLane = @(Get-Drift $build (New-Queue ([pscustomobject]@{ slots = 3; require_reason = $true; description = $build.description; closed = 'nope' })))
    Assert-Ok ($closedOpenLane.Count -eq 1 -and $closedOpenLane[0] -match 'want open') 'an open lane that got closed is drift'

    # The retired key, whose whole point is that it stays closed with a
    # message naming the replacements.
    Assert-Ok (@(Get-Drift $retired (New-Queue ([pscustomobject]@{ closed = $retired.closed }))).Count -eq 0) 'the retired key closed with its message matches'
    Assert-Ok (@(Get-Drift $retired (New-Queue ([pscustomobject]@{ slots = 1 }))) -join '' -match 'not closed') 'the retired key left open is drift'
    Assert-Ok (@(Get-Drift $retired $null) -join '' -match 'not closed') 'the retired key missing is drift, because a run would create it open'
    $otherMsg = @(Get-Drift $retired (New-Queue ([pscustomobject]@{ closed = 'closed for another reason' })))
    Assert-Ok ($otherMsg.Count -eq 1 -and $otherMsg[0] -match 'different message') 'the retired key closed with other prose is drift'

    # The apply argv, for every shape of lane. --slots is the one that went
    # missing: incoda merges, so a lane left out of the argv keeps whatever
    # it drifted to and -Check never goes green again.
    foreach ($l in @($build, $desktop)) {
        $a = @(Get-ApplyArgs $l)
        $slotIdx = [array]::IndexOf($a, '--slots')
        Assert-Ok ($a[0] -eq 'config' -and $a[1] -eq $l.key) "$($l.key): the apply argv configures its own key"
        Assert-Ok ($slotIdx -ge 0 -and [int]$a[$slotIdx + 1] -eq $l.slots) "$($l.key): --slots is always passed, because incoda config merges rather than replaces"
        Assert-Ok ($a -contains '--require-reason' -and $a -contains '--open') "$($l.key): the apply argv requires a reason and opens the lane"
        $descIdx = [array]::IndexOf($a, '--description')
        Assert-Ok ($descIdx -ge 0 -and $a[$descIdx + 1] -eq $l.description) "$($l.key): the description applied is the one drift is measured against"
    }
    $closedArgs = @(Get-ApplyArgs $retired)
    $closeIdx = [array]::IndexOf($closedArgs, '--close')
    Assert-Ok ($closedArgs[0] -eq 'config' -and $closedArgs[1] -eq 'wintty') 'the retired key is configured by name'
    Assert-Ok ($closeIdx -ge 0 -and $closedArgs[$closeIdx + 1] -eq $retired.closed) 'the retired key is closed with the message drift is measured against'
    Assert-Ok (-not ($closedArgs -contains '--open')) 'the retired key is never reopened'

    # Applying what the table says must leave nothing to report, or apply
    # mode would loop: heal, read back, still drift, exit 1.
    $healed = ConvertTo-QueueMap ([pscustomobject]@{ queues = @(
        [pscustomobject]@{ key = 'wintty-build'; exists = $true; config = [pscustomobject]@{ slots = 3; require_reason = $true; description = $lanes[0].description } }
        [pscustomobject]@{ key = 'wintty-desktop'; exists = $true; config = [pscustomobject]@{ slots = 1; require_reason = $true; description = $lanes[1].description } }
        [pscustomobject]@{ key = 'wintty-publish'; exists = $true; config = [pscustomobject]@{ slots = 1; require_reason = $true; description = $lanes[2].description } }
        [pscustomobject]@{ key = 'wintty'; exists = $true; config = [pscustomobject]@{ closed = $lanes[3].closed } }
    ) })
    Assert-Ok (@(Get-AllDrift $healed).Count -eq 0) 'a machine configured from this table reports no drift'
    $drifted = ConvertTo-QueueMap ([pscustomobject]@{ queues = @(
        [pscustomobject]@{ key = 'wintty-build'; exists = $true; config = [pscustomobject]@{ slots = 1; require_reason = $true; description = $lanes[0].description } }
    ) })
    Assert-Ok (@(Get-AllDrift $drifted).Count -eq 4) 'one drifted lane and three missing ones are four findings'

    if ($script:failed) { Write-Host 'SELF-TEST FAILED'; exit 1 }
    Write-Host 'SELF-TEST PASSED'
    exit 0
}

# %LOCALAPPDATA% is unset on any host that is not Windows, and Join-Path
# refuses a null path with an error about parameter binding rather than
# about incoda, which is not a useful thing to read.
$installed = if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Programs\incoda\incoda.exe' } else { $null }
$inc = (Get-Command incoda -ErrorAction SilentlyContinue)?.Source ?? $installed
if (-not $inc -or -not (Test-Path $inc)) {
    Write-Host 'incoda not found on PATH or in Programs\incoda: the heavy job lanes need it (AGENTS.md; https://github.com/deblasis/incoda)'
    exit 2
}

function Read-Queues {
    # stderr goes to its own file rather than into the pipeline: folded in
    # with 2>&1 it lands inside $raw, and one line of lane chatter or a
    # deprecation warning would turn a perfectly healthy machine into "did
    # not return JSON" and exit 2. --no-color for the same reason, since a
    # colored status is not JSON either.
    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        $global:LASTEXITCODE = $null
        $raw = & $inc status --all --json --no-color 2>$errFile | Out-String
        $rc = $LASTEXITCODE ?? 1
        $err = (Get-Content $errFile -Raw -ErrorAction SilentlyContinue) ?? ''
    } finally { Remove-Item $errFile -Force -ErrorAction SilentlyContinue }
    if ($rc -ne 0) {
        Write-Host "incoda status --all --json exited $rc`: $(($err + ' ' + $raw).Trim())"
        exit 2
    }
    try { $status = $raw | ConvertFrom-Json } catch {
        Write-Host "incoda status --all --json did not return JSON: $($raw.Trim()) $($err.Trim())"
        exit 2
    }
    ConvertTo-QueueMap $status
}

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
    $cargs = Get-ApplyArgs $lane
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
