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

    The launch goes through Start-SeamSession rather than being staged
    here. That is not tidiness: the shared launcher waits for the window to
    be READY -- a WinUI hwnd, then the splash gone -- where this script used
    to send its first command as soon as the pipe existed, which is earlier;
    and it strips NO_COLOR out of the child's environment, which a shell
    that sets it (Claude Code's PowerShell tool does) otherwise turns into a
    focus-stealing infobar across a third of the window. Both were #942.

    Exits 0 on pass, 2 on a product finding (the app died, refused a
    command, never showed a window or never dropped its splash, or landed in
    a state the assertions reject), 1 when the harness could not run and
    nothing is known about the product (the exe is missing, a Wintty is
    already running, the seam pipe never appeared).
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # This count does not currently decide whether the run passes. The app
    # dies with 0xC0000005 on the THIRD seed-tabs of a process, whatever
    # this is set to: at 5 that is iteration 3, and at 2 it is the drag
    # leg's own seed. Measured 3/3, 2026-09-02.
    #
    # Start-SeamSession's note says "around the seventh cumulative seed"
    # and cites a separately-filed issue. The threshold is three, and no
    # such issue exists in this repo -- both halves of that note are
    # repeated here only to say they were checked and are wrong.
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

$config = @'
windows-single-instance = true
window-save-state = never
vertical-tabs = true
'@

$script:Session = $null

function Invoke-Seam {
    param([Parameter(Mandatory)][hashtable]$Command)
    if ($GapMs -gt 0) { Start-Sleep -Milliseconds $GapMs }
    return Invoke-SeamCommand $script:Session $Command
}

function Assert-Order {
    param($State, [string[]]$Want, [string]$What)
    # A missing state block is a harness fault, not a product one. Without
    # this the property walk yields $null, @() turns it into an empty list,
    # and the comparison below reports "order is []" -- a PRODUCT_FAIL that
    # names the app for a response this script was reading wrong. Which is
    # exactly what the drag leg did; see Assert-DragOrder.
    if ($null -eq $State.state -or $null -eq $State.state.tabs) {
        throw ("HARNESS: {0}: the response carries no state.tabs to assert on" -f
            $What)
    }
    $got = @($State.state.tabs | ForEach-Object { $_.title })
    if (($got -join ',') -ne ($Want -join ',')) {
        throw ("PRODUCT_FAIL: {0}: order is [{1}], wanted [{2}]" -f
            $What, ($got -join ','), ($Want -join ','))
    }
}

# The drag ops answer with DragJson, which carries a flat `order` array and
# NO state block. Asserting them with Assert-Order read $State.state.tabs as
# $null on every drag, so the drag leg could only ever report "order is []"
# -- it has never been capable of passing. Nobody saw it, because the run
# died at its first op long before reaching this leg (#942).
function Assert-DragOrder {
    param($Response, [string[]]$Want, [string]$What)
    if ($null -eq $Response.order) {
        throw ("HARNESS: {0}: the drag response carries no order to assert on" -f
            $What)
    }
    $got = @($Response.order)
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
        throw (("PRODUCT_FAIL: group is title={0} members=[{1}] collapsed={2}, " +
            "wanted title={3} members=[{4}] collapsed={5}") -f
            $group.title, ($got -join ','), $group.collapsed,
            $Title, ($Members -join ','), $Collapsed)
    }
}

# ---- run -----------------------------------------------------------------

try {
    $script:Session = Start-SeamSession -ExePath $ExePath -ConfigText $config
    $proc = $script:Session.Proc
    Write-Host ("pid={0} pipe={1} iterations={2}" -f
        $proc.Id, (Get-SeamPipeName $script:Session.Token), $Iterations)
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
    Assert-DragOrder $drag @('tab-1', 'tab-3', 'tab-4', 'tab-2', 'tab-5') 'drag leg'
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

    Write-Host (("SEAM-ACCEPTANCE PASS: {0} iterations + drag leg, " +
        "no flakes, no input injected") -f $Iterations)
    exit 0
}
catch {
    $message = $_.Exception.Message
    Write-Host $message
    if ($message -like 'HARNESS:*') { exit 1 }
    exit 2
}
finally {
    if ($script:Session) { Stop-SeamSession $script:Session }
}
