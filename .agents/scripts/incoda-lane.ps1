#requires -Version 7
<#
    The single decision point for whether a recipe can take an incoda lane.

    Test-LaneReady requires an installed incoda, an open queue for every named
    key, and open pools behind each linked queue. Write-LaneFallback prints one
    warning when a recipe runs bare instead, so a clone that never saw the
    development machine's queue setup still builds and launches.

    incoda refuses a closed or unlinked pool with exit 120 before the child
    starts, so a queue that only looks configured would turn the fallback into
    a crash. The caller records its decision in WINTTY_LANE_DECISION; the guard
    reads that instead of asking again, so a lane that changes state between
    the two cannot make them disagree.

    Justfile recipes dot-source this file. `pwsh -File ... -Ready` is the same
    query for outside callers, and -SelfTest exercises the parsing and
    decision logic against hand-written payloads. The self-test never resolves
    a real incoda or reads its state dir, and runs in `just gates-selftest`.
#>

[CmdletBinding()]
param(
    [string[]]$Queue = @('wintty-build'),
    [switch]$Ready,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# Set by Test-LaneReady so the warning can name the condition it found.
$script:LaneIssue = ''

function Get-Prop($obj, [string]$name) {
    if ($null -ne $obj -and $obj.PSObject.Properties[$name]) { $obj.$name } else { $null }
}

# PATH first, then incoda's installer location. The caller supplies both
# candidates so -SelfTest can drive this without touching the machine.
function Resolve-Incoda([string]$found, [string]$installed, [bool]$installedExists) {
    if (-not [string]::IsNullOrEmpty($found)) { return $found }
    if (-not [string]::IsNullOrEmpty($installed) -and $installedExists) { return $installed }
    $null
}

function Find-Incoda {
    $installed = if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Programs\incoda\incoda.exe' } else { $null }
    Resolve-Incoda (Get-Command incoda -ErrorAction SilentlyContinue)?.Source `
                   $installed `
                   (Test-Path $installed -ErrorAction SilentlyContinue)
}

# $null means unreadable: an absent, non-JSON or nonzero status cannot vouch
# for a lane, so callers treat it the same as a missing incoda.
function ConvertFrom-IncodaStatus([string]$raw, [int]$rc) {
    if ($rc -ne 0) { return $null }
    try { return ($raw | ConvertFrom-Json) } catch { return $null }
}

function Read-IncodaStatus([string]$inc) {
    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        $global:LASTEXITCODE = $null
        $raw = & $inc status --all --json --no-color 2>$errFile | Out-String
        $rc = $LASTEXITCODE ?? 1
    } finally {
        Remove-Item $errFile -Force -ErrorAction SilentlyContinue
    }
    ConvertFrom-IncodaStatus $raw $rc
}

# Pools are queues too, and a closed one refuses every project lane linked to
# it. Walk the links with a visited list so a cycle cannot hang the check.
function Test-QueueUsable($status, [string]$key, [string[]]$seen = @()) {
    if ($seen -ccontains $key) { return $false }
    $seen += $key
    $queues = @(Get-Prop $status 'queues')
    $poolMachine = @($queues | Where-Object { Get-Prop $_ 'is_pool' }).Count -gt 0
    foreach ($q in $queues) {
        if ((Get-Prop $q 'key') -cne $key) { continue }
        if (-not (Get-Prop $q 'exists')) { return $false }
        $cfg = Get-Prop $q 'config'
        if (-not [string]::IsNullOrEmpty((Get-Prop $cfg 'closed'))) { return $false }
        if ($poolMachine -and -not (Get-Prop $q 'is_pool')) {
            $links = @(Get-Prop $cfg 'pools' | Where-Object { -not [string]::IsNullOrEmpty($_) })
            if ($links.Count -eq 0) { return $false }
            foreach ($pool in $links) {
                if (-not (Test-QueueUsable $status $pool $seen)) { return $false }
            }
        }
        return $true
    }
    $false
}

function Test-AllUsable($status, [string[]]$keys) {
    foreach ($k in $keys) {
        if (-not (Test-QueueUsable $status $k)) { return $false }
    }
    $true
}

# The warning should say which condition stopped the lane, not just that
# something was unusable.
function Get-LaneProblem($status, [string]$key) {
    $queues = @(Get-Prop $status 'queues')
    $poolMachine = @($queues | Where-Object { Get-Prop $_ 'is_pool' }).Count -gt 0
    $matches = @($queues | Where-Object { (Get-Prop $_ 'key') -ceq $key })
    $q = if ($matches.Count -gt 0) { $matches[0] } else { $null }
    if ($null -eq $q) { return "$key queue is missing" }
    if (-not (Get-Prop $q 'exists')) { return "$key queue does not exist" }
    $cfg = Get-Prop $q 'config'
    $closed = Get-Prop $cfg 'closed'
    if (-not [string]::IsNullOrEmpty($closed)) { return "$key queue is closed ('$closed')" }
    if ($poolMachine -and -not (Get-Prop $q 'is_pool')) {
        $links = @(Get-Prop $cfg 'pools' | Where-Object { -not [string]::IsNullOrEmpty($_) })
        if ($links.Count -eq 0) { return "$key queue is not linked to a pool" }
        foreach ($pool in $links) {
            $poolMatches = @($queues | Where-Object { (Get-Prop $_ 'key') -ceq $pool })
            $poolQueue = if ($poolMatches.Count -gt 0) { $poolMatches[0] } else { $null }
            if ($null -eq $poolQueue -or -not (Get-Prop $poolQueue 'exists')) {
                return "$key links to missing pool $pool"
            }
            $poolClosed = Get-Prop (Get-Prop $poolQueue 'config') 'closed'
            if (-not [string]::IsNullOrEmpty($poolClosed)) {
                return "$key links to closed pool $pool ('$poolClosed')"
            }
        }
    }
    ''
}

# The resolvers are injectable so -SelfTest can prove that Keys is honoured,
# not just that a canned status parses.
function Test-LaneReady {
    param(
        [string[]]$Keys = @('wintty-build'),
        [scriptblock]$IncodaResolver = { Find-Incoda },
        [scriptblock]$StatusReader = { param($inc) Read-IncodaStatus $inc }
    )
    $script:LaneIssue = ''
    $inc = & $IncodaResolver
    if (-not $inc) {
        $script:LaneIssue = 'incoda is not installed (PATH or %LOCALAPPDATA%\Programs\incoda)'
        return $false
    }
    $status = & $StatusReader $inc
    if (-not $status) {
        $script:LaneIssue = 'incoda status could not be read'
        return $false
    }
    if (-not (Test-AllUsable $status $Keys)) {
        $problems = @($Keys | ForEach-Object { Get-LaneProblem $status $_ } | Where-Object { $_ })
        $script:LaneIssue = if ($problems) { "incoda lane problem: $($problems -join '; ')" } else { "incoda lane $($Keys -join ', ') is not usable here" }
        return $false
    }
    $true
}

# Sibling recipe shells inherit neither each other's environment nor a parent
# just process's variables. A marker named for their shared parent process
# keeps a two-phase recipe from printing the same warning twice.
function Get-ParentMarkerKey {
    try {
        $parent = (Get-Process -Id $PID -ErrorAction Stop).Parent
        if ($parent) { return "$($parent.Id)-$($parent.StartTime.Ticks)" }
    } catch {
        # A process without an accessible parent still gets its own marker.
    }
    "pid-$PID"
}

function Write-LaneFallback {
    param(
        [string[]]$Keys = @('wintty-build'),
        [string]$Label = 'this command',
        [string]$MarkerDir = ''
    )
    if ($env:WINTTY_INCODA_WARNED -eq '1') { return }
    if (-not $MarkerDir) {
        $MarkerDir = $env:WINTTY_WARNING_DIR
        if (-not $MarkerDir) {
            $MarkerDir = Join-Path ([System.IO.Path]::GetTempPath()) 'wintty-incoda-warnings'
        }
    }
    $marker = Join-Path $MarkerDir (Get-ParentMarkerKey)
    if (Test-Path $marker) {
        $env:WINTTY_INCODA_WARNED = '1'
        return
    }
    New-Item -ItemType Directory -Force -Path $MarkerDir | Out-Null
    New-Item -ItemType File -Force -Path $marker | Out-Null
    $why = if ($script:LaneIssue) { $script:LaneIssue } else { "incoda lane $($Keys -join ', ') is not usable here" }
    Write-Host "warning: $why; running '$Label' without a heavy job lane (AGENTS.md, heavy job lanes)" -ForegroundColor Yellow
    $env:WINTTY_INCODA_WARNED = '1'
}

# Dot-sourcing installs the functions without running anything, which is how
# the justfile calls them.
if ($MyInvocation.InvocationName -eq '.' -or -not $MyInvocation.MyCommand) { return }

if ($SelfTest) {
    $script:failed = $false
    function Assert-Ok([bool]$ok, [string]$label) {
        if (-not $ok) { Write-Host "SELF-TEST FAILED: $label" -ForegroundColor Red; $script:failed = $true }
    }
    function New-Queue($config, [string]$key = 'wintty-build', [bool]$pool = $false, [bool]$exists = $true) {
        [pscustomobject]@{ key = $key; exists = $exists; is_pool = $pool; config = $config }
    }
    function New-Status { param([object[]]$queues) [pscustomobject]@{ queues = @($queues) } }
    $open = [pscustomobject]@{ slots = 3; require_reason = $true }
    $linked = [pscustomobject]@{ slots = 3; require_reason = $true; pools = @('builds') }
    $nullStatus = $null

    Assert-Ok ((Resolve-Incoda '/bin/incoda' 'C:\Programs\incoda.exe' $true) -eq '/bin/incoda') 'PATH wins over the installer location'
    Assert-Ok ((Resolve-Incoda '' 'C:\Programs\incoda.exe' $true) -eq 'C:\Programs\incoda.exe') 'the installer location is the fallback'
    Assert-Ok ($null -eq (Resolve-Incoda '' '' $false)) 'no incoda resolves to nothing'

    Assert-Ok ($null -eq (ConvertFrom-IncodaStatus '' 1)) 'a nonzero status is unreadable'
    Assert-Ok ($null -eq (ConvertFrom-IncodaStatus 'not json' 0)) 'a non-JSON status is unreadable'
    Assert-Ok ($null -ne (ConvertFrom-IncodaStatus '{"queues":[]}' 0)) 'an empty status is readable'

    Assert-Ok (-not (Test-QueueUsable $nullStatus 'wintty-build')) 'a null status has no usable lane'
    Assert-Ok (-not (Test-QueueUsable ([pscustomobject]@{ schema = 1 }) 'wintty-build')) 'a status with no queues has no usable lane'
    $missing = New-Status @((New-Queue $open 'wintty-build' $false $false))
    Assert-Ok (-not (Test-QueueUsable $missing 'wintty-build')) 'a queue that does not exist is not usable'
    $closed = New-Status @((New-Queue ([pscustomobject]@{ closed = 'retired' })))
    Assert-Ok (-not (Test-QueueUsable $closed 'wintty-build')) 'a closed queue is not usable'
    $openStatus = New-Status @((New-Queue $open))
    Assert-Ok (Test-QueueUsable $openStatus 'wintty-build') 'an open queue on a pool-free machine is usable'
    $poolQueue = New-Queue $open 'builds' $true
    $unlinked = New-Status @($poolQueue, (New-Queue $open))
    Assert-Ok (-not (Test-QueueUsable $unlinked 'wintty-build')) 'an unlinked queue on a pool machine is not usable'
    $linkedStatus = New-Status @($poolQueue, (New-Queue $linked))
    Assert-Ok (Test-QueueUsable $linkedStatus 'wintty-build') 'a linked queue on a pool machine is usable'
    $closedPool = New-Status @((New-Queue ([pscustomobject]@{ closed = 'maintenance' }) 'builds' $true), (New-Queue $linked))
    Assert-Ok (-not (Test-QueueUsable $closedPool 'wintty-build')) 'a link to a closed pool is not usable'
    Assert-Ok ((Get-LaneProblem $unlinked 'wintty-build') -match 'not linked to a pool') 'the unlinked warning names the link'
    Assert-Ok ((Get-LaneProblem $closedPool 'wintty-build') -match 'links to closed pool builds') 'the closed-pool warning names the pool'
    Assert-Ok ((Get-LaneProblem $missing 'wintty-build') -match 'does not exist') 'the absent-queue warning names the queue'
    $missingPool = New-Status @((New-Queue $open 'tests' $true), (New-Queue $linked))
    Assert-Ok (-not (Test-QueueUsable $missingPool 'wintty-build')) 'a link to an absent pool is not usable'
    $cycleCfg = [pscustomobject]@{ pools = @('other') }
    $cycle = New-Status @((New-Queue $open 'tests' $true), (New-Queue $cycleCfg 'wintty-build'), (New-Queue $cycleCfg 'other'))
    Assert-Ok (-not (Test-QueueUsable $cycle 'wintty-build')) 'a pool cycle is not usable'
    Assert-Ok (-not (Test-AllUsable $linkedStatus @('wintty-build', 'wintty-desktop'))) 'one named lane is not enough for two names'
    $bothStatus = New-Status @($poolQueue, (New-Queue $linked), (New-Queue $linked 'wintty-desktop'))
    Assert-Ok (Test-AllUsable $bothStatus @('wintty-build', 'wintty-desktop')) 'both named lanes must be usable'

    # Readiness follows the named keys, not a hardcoded one. A resolver that
    # returns nothing, and an unreadable status, both refuse the lane.
    $reader = { param($inc) $bothStatus }
    Assert-Ok (Test-LaneReady -Keys @('wintty-build', 'wintty-desktop') -IncodaResolver { '/fake/incoda' } -StatusReader $reader) 'two ready keys make the lane ready'
    Assert-Ok (-not (Test-LaneReady -Keys @('wintty-build', 'missing-lane') -IncodaResolver { '/fake/incoda' } -StatusReader $reader)) 'an unready named key makes the lane unusable'
    Assert-Ok (-not (Test-LaneReady -Keys @('wintty-desktop') -IncodaResolver { $null } -StatusReader $reader)) 'no incoda makes the lane unusable'
    Assert-Ok (-not (Test-LaneReady -Keys @('wintty-desktop') -IncodaResolver { '/fake/incoda' } -StatusReader { param($inc) $null })) 'an unreadable status makes the lane unusable'
    Assert-Ok ($script:LaneIssue -eq 'incoda status could not be read') 'the issue names the condition found'

    # One warning per process tree: the marker stands in for a sibling shell
    # whose environment does not carry the first shell's variables.
    $markerDir = Join-Path ([System.IO.Path]::GetTempPath()) ('selftest-warnings-' + [guid]::NewGuid())
    try {
        $env:WINTTY_INCODA_WARNED = '0'
        $script:LaneIssue = 'incoda is not installed (PATH or %LOCALAPPDATA%\Programs\incoda)'
        $warnings = @(& {
            Write-LaneFallback -Keys @('wintty-build') -Label 'just build-dll' -MarkerDir $markerDir
            Write-LaneFallback -Keys @('wintty-build') -Label 'just build-win' -MarkerDir $markerDir
        } 6>&1)
        Assert-Ok ($warnings.Count -eq 1 -and $warnings[0] -match 'incoda is not installed') 'one warning covers the whole fallback tree'
        $env:WINTTY_INCODA_WARNED = '0'
        $again = @(& { Write-LaneFallback -Keys @('wintty-build') -Label 'phase two' -MarkerDir $markerDir } 6>&1)
        Assert-Ok ($again.Count -eq 0) 'a sibling shell sees the same marker'
    } finally {
        Remove-Item $markerDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($script:failed) { exit 1 }
    Write-Host 'SELF-TEST PASSED'
    exit 0
}

if ($Ready) { if (Test-LaneReady -Keys $Queue) { exit 0 } else { exit 1 } }
