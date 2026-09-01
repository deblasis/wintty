#requires -Version 7
<#
    The in-process test seam's acceptance run: one Wintty armed with a
    per-session seam token, driven over the named pipe with zero OS input,
    zero focus steals, and the driver asserting manager truth after every
    step.

    The scenario is the crash investigation's repro, made deterministic.
    Per iteration, all over the pipe:

      seed-tabs 5 -> pin 1 -> group 2,3 -> collapse 2 -> toggle-layout x2
      -> assert order, pin flag, group membership and collapse bit, and
      that the process is still alive.

    The toggle is run twice so every iteration contains one switch INTO the
    vertical layout with pins, groups and collapsed chips already in the
    strip -- the compound crash.log 2026-08-31T04:58:03Z died in
    (COMException 0x800F1000, NavigationViewItem style onto a ContentControl,
    VerticalTabStrip.ApplyPaneLayout -> set_PaneDisplayMode -> MeasureOverride).
    A final leg drives the drag engine itself: seed, unpin, then
    drag(1 -> 3) through the strip's real press/threshold/crossing/release
    sequence, asserting the landed order.

    Exits 0 on pass, 2 on a product finding (the app died, refused a
    command, or landed in a state the assertions reject), 1 when the harness
    could not run and nothing is known about the product (the exe is
    missing, a Wintty is already running, the seam pipe never appeared).
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    [ValidateRange(1, 100)][int]$Iterations = 5,
    # Milliseconds between commands. 0 is the tight train; 400 is the
    # pacing that let ordinary layout frames pass through freshly churned
    # strip items and died 6/6 before the realization guard.
    [ValidateRange(0, 5000)][int]$GapMs = 0
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

$titles = @('tab-1', 'tab-2', 'tab-3', 'tab-4', 'tab-5')

# ---- process and environment staging ------------------------------------

if (-not (Test-Path $ExePath)) {
    Write-Host "HARNESS: missing exe: $ExePath"
    exit 1
}
Assert-NoWintty -Context 'The seam acceptance run'

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) {
    (Get-Item $crashPath).LastWriteTimeUtc
} else {
    [datetime]::MinValue
}

$tempXdg = Join-Path $env:TEMP "wintty-seam-accept-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path (Join-Path $tempXdg 'wintty') | Out-Null
@'
windows-single-instance = true
window-save-state = never
vertical-tabs = true
'@ | Set-Content (Join-Path $tempXdg 'wintty\config.wintty') -Encoding utf8

$origXdgSet = Test-Path Env:XDG_CONFIG_HOME
$origXdg = if ($origXdgSet) { $env:XDG_CONFIG_HOME } else { $null }
$origSeamSet = Test-Path Env:WINTTY_TEST_SEAM
$origSeam = if ($origSeamSet) { $env:WINTTY_TEST_SEAM } else { $null }

$script:Proc = $null
$script:Reader = $null
$script:Writer = $null
$script:Pipe = $null
$stamp = Get-WinttyLaunchStamp

function Invoke-Seam {
    param([Parameter(Mandatory)][hashtable]$Command)
    if ($GapMs -gt 0) { Start-Sleep -Milliseconds $GapMs }
    if ($script:Proc.HasExited) {
        throw ("PRODUCT_EXIT: the app exited (code {0}) before '{1}'" -f
            $script:Proc.ExitCode, $Command['op'])
    }
    $script:Writer.WriteLine(($Command | ConvertTo-Json -Compress -Depth 6))
    $line = $script:Reader.ReadLine()
    if ($null -eq $line) {
        if ($script:Proc.HasExited) {
            throw ("PRODUCT_EXIT: the seam pipe closed and the app exited " +
                "(code {0}) during '{1}'" -f $script:Proc.ExitCode, $Command['op'])
        }
        throw ("HARNESS: the seam closed the connection without a " +
            "response to '{0}'" -f $Command['op'])
    }
    $response = $line | ConvertFrom-Json
    if ($null -eq $response) {
        throw ("HARNESS: the seam answered '{0}' with a non-JSON line" -f
            $Command['op'])
    }
    if (-not $response.ok) {
        throw ("PRODUCT_FAIL: {0} -> {1}" -f $Command['op'], $response.error)
    }
    Write-Host ("OK {0}" -f $Command['op'])
    return $response
}

function Assert-Order {
    param($State, [string[]]$Want, [string]$What)
    $got = @($State.state.tabs | ForEach-Object { $_.title })
    if (($got -join ',') -ne ($Want -join ',')) {
        throw ("PRODUCT_FAIL: {0}: order is [{1}], wanted [{2}]" -f
            $What, ($got -join ','), ($Want -join ','))
    }
}

function Assert-SeamGroup {
    param($State, [string]$Title, [string[]]$Members, [bool]$Collapsed)
    $groups = @($State.state.groups)
    if ($groups.Count -ne 1) {
        throw ("PRODUCT_FAIL: expected one group, saw {0}" -f $groups.Count)
    }
    $group = $groups[0]
    $got = @($group.members)
    if ($group.title -ne $Title -or ($got -join ',') -ne ($Members -join ',') `
        -or [bool]$group.collapsed -ne $Collapsed) {
        throw ("PRODUCT_FAIL: group is title={0} members=[{1}] collapsed={2}, " +
            "wanted title={3} members=[{4}] collapsed={5}" -f
            $group.title, ($got -join ','), $group.collapsed,
            $Title, ($Members -join ','), $Collapsed)
    }
}

# ---- run -----------------------------------------------------------------

try {
    $env:XDG_CONFIG_HOME = $tempXdg
    $token = New-SeamToken
    $env:WINTTY_TEST_SEAM = $token
    $proc = Start-Process -FilePath $ExePath -PassThru `
        -WorkingDirectory (Split-Path -Parent (Resolve-Path $ExePath))
    $script:Proc = $proc
    Write-Host "pid=$($proc.Id) pipe=$(Get-SeamPipeName $token) iterations=$Iterations"

    # The seam pipe appears once OnLaunched has built the window.
    [void](Wait-SeamPipe -Token $token -Proc $proc)
    $script:Pipe = Connect-SeamPipe -Token $token
    $script:Reader = [System.IO.StreamReader]::new($script:Pipe)
    $script:Writer = [System.IO.StreamWriter]::new(
        $script:Pipe, [System.Text.UTF8Encoding]::new($false))
    $script:Writer.AutoFlush = $true
    $script:Writer.NewLine = "`n"
    Write-Host 'seam connected'

    for ($i = 1; $i -le $Iterations; $i++) {
        $seeded = Invoke-Seam @{
            op = 'seed-tabs'; count = 5; titles = $titles }
        Assert-Order $seeded $titles "iteration $i seed"

        $pinned = Invoke-Seam @{ op = 'pin'; index = 1 }
        Assert-Order $pinned @('tab-2', 'tab-1', 'tab-3', 'tab-4', 'tab-5') `
            "iteration $i pin"
        if (-not $pinned.state.tabs[0].pinned) {
            throw "PRODUCT_FAIL: iteration ${i}: tab-2 did not land pinned"
        }

        $grouped = Invoke-Seam @{ op = 'group'; indices = @(2, 3) }
        Assert-Order $grouped @('tab-2', 'tab-1', 'tab-3', 'tab-4', 'tab-5') `
            "iteration $i group"
        Assert-SeamGroup $grouped 'group-1' @('tab-3', 'tab-4') $false

        $collapsed = Invoke-Seam @{
            op = 'collapse'; index = 2; collapsed = $true }
        if (-not $collapsed.state.tabs[2].collapsedGroup) {
            throw "PRODUCT_FAIL: iteration ${i}: group did not collapse"
        }

        # There and back: one of the two always switches INTO vertical with
        # pins, groups and collapsed chips in the strip.
        [void](Invoke-Seam @{ op = 'toggle-layout' })
        [void](Invoke-Seam @{ op = 'toggle-layout' })

        $state = Invoke-Seam @{ op = 'get-state' }
        if (-not $state.state.vertical) {
            throw "PRODUCT_FAIL: iteration ${i}: window did not come back vertical"
        }
        if ($state.state.switching) {
            throw "PRODUCT_FAIL: iteration ${i}: layout still switching at ack"
        }
        Assert-Order $state @('tab-2', 'tab-1', 'tab-3', 'tab-4', 'tab-5') `
            "iteration $i final"
        Assert-SeamGroup $state 'group-1' @('tab-3', 'tab-4') $true
        if ($proc.HasExited) {
            throw ("PRODUCT_EXIT: the app exited (code {0}) during iteration {1}" -f
                $proc.ExitCode, $i)
        }
        Write-Host "PASS iteration $i"
    }

    # The drag leg: the engine itself, through the seam. Fresh state, then
    # one unpinned body row walked two slots down.
    $fresh = Invoke-Seam @{ op = 'seed-tabs'; count = 5; titles = $titles }
    Assert-Order $fresh $titles 'drag leg seed'
    [void](Invoke-Seam @{ op = 'unpin'; index = 0 })
    $drag = Invoke-Seam @{ op = 'drag'; from = 1; to = 3 }
    Assert-Order $drag @('tab-1', 'tab-3', 'tab-4', 'tab-2', 'tab-5') 'drag leg'
    if ($drag.landed -ne 3) {
        throw ("PRODUCT_FAIL: drag landed at {0}, wanted 3" -f $drag.landed)
    }
    if ($proc.HasExited) {
        throw ("PRODUCT_EXIT: the app exited (code {0}) during the drag leg" -f
            $proc.ExitCode)
    }
    Write-Host 'PASS drag leg'

    if (Test-Path $crashPath) {
        $now = (Get-Item $crashPath).LastWriteTimeUtc
        if ($now -gt $crashStamp) {
            throw 'PRODUCT_FAIL: crash.log grew during the run'
        }
    }

    Write-Host ("SEAM-ACCEPTANCE PASS: {0} iterations + drag leg, " +
        "no flakes, no input injected" -f $Iterations)
    exit 0
}
catch {
    $message = $_.Exception.Message
    Write-Host $message
    if ($message -like 'HARNESS:*') { exit 1 }
    exit 2
}
finally {
    if ($script:Writer) { try { $script:Writer.Dispose() } catch { } }
    if ($script:Reader) { try { $script:Reader.Dispose() } catch { } }
    if ($script:Pipe) { try { $script:Pipe.Dispose() } catch { } }

    # Only the instance this run started; identified by stamp and exe path.
    try {
        Stop-WinttyStartedAfter -Since $stamp -ExePath (Resolve-Path $ExePath).Path
    } catch {
        Write-Host ("HARNESS: cleanup could not confirm every process it " +
            "started: {0}" -f $_.Exception.Message)
    }

    if ($origXdgSet) { $env:XDG_CONFIG_HOME = $origXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
    if ($origSeamSet) { $env:WINTTY_TEST_SEAM = $origSeam }
    else { Remove-Item Env:WINTTY_TEST_SEAM -ErrorAction SilentlyContinue }
    Remove-Item $tempXdg -Recurse -Force -ErrorAction SilentlyContinue
}
