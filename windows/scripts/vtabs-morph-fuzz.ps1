#requires -Version 7
<#
    Randomized stress for the horizontal<->vertical tab layout switch,
    seam-actuated (#930).

    The layout switch has to hold up with a strip full of tabs, tabs
    whose titles and icons keep changing under it, per-tab colors, and
    switches that arrive before the previous one has finished. This
    drives all of that from a seeded RNG so a failure can be replayed
    with -Seed.

    Pass/fail is not "did it crash": the app emits a morph trace whenever
    the WINTTY_MORPH_TRACE env var names a log file (set per run below,
    inherited by the seam-launched child), and every SWITCH end line must
    report ghosts=0 and morph=null. A ghost left parked on the morph
    layer is the artifact this whole change exists to remove, so it is
    the thing worth asserting. Every SWITCH begin must also be answered
    by an end or a cancel - the cancel term is what keeps a graceful
    close honest, though no run of this harness reaches it: it keeps the
    strip above three tabs and tears the process down rather than
    closing the window. Every line MorphTrace writes starts with an
    elapsed-ms prefix ("529ms SWITCH end ..."), so every term below
    matches by substring - the old harness matched -like 'SWITCH begin*'
    against this emitter, which never matched anything, and its probe
    refused every run against a trace format the product had already
    moved on from.

    Actuation is the seam throughout: the layout toggle is the
    toggle-layout op (the router event the Ctrl+Shift+Comma chord raises,
    so the seam cannot drift from the real action), new tabs and shell
    spawns go through chord and send-text, closes and colors through the
    manager's own ops with counts from the manager's own state. The old
    harness's whole input stack - foreground ownership, thread
    attachment, the XAML-island arming click before every chord - is
    gone, because none of it can refuse a seam op.

    That is also why the anti-vacuity gate is rewritten rather than
    ported. The old floor divided SWITCH begins by chords the desktop
    happened to accept, with a miss cap for foreground steals; a seam
    op has no foreground to lose. What can still go wrong is a toggle
    that never began: RequestToggleTabLayout no-ops while a switch is
    mid-flight, so every COUNTED toggle first waits out the coordinator
    (state.switching false) and only then requests - and the floor is
    one begin per counted toggle. The interrupting second toggle (the
    30% leg) is deliberately sent mid-switch and counted nowhere: the
    coordinator is meant to drop it. A run whose counted toggles never
    began leaves with 1 - a corpus this harness could not establish,
    not a defect in the build - and a probe toggle up front proves both
    halves of the oracle are alive before minutes of fuzzing are spent:
    the router must begin a switch and the trace file must record it.

    Exits 0 on pass, 2 on a layout-switch defect, 1 when the run could
    not judge (no trace, chords that never dispatched, a floor of
    nothing). What this does NOT gate: switch animation quality - a
    switch that lands correctly but looks wrong still passes here.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [int]$Seed = 0,
    [int]$Iterations = 60,
    [switch]$StartHorizontal
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

if ($Seed -eq 0) { $Seed = Get-Random -Minimum 1 -Maximum 999999 }
$rng = [System.Random]::new($Seed)
Write-Host "seed=$Seed iterations=$Iterations"

$startVertical = -not $StartHorizontal
$Config = @"
vertical-tabs = $($startVertical.ToString().ToLower())
windows-single-instance = true
window-save-state = never
window-theme = wintty
theme = Catppuccin Mocha
profile.pwsh.name = PowerShell
profile.pwsh.command = pwsh.exe -NoProfile
default-profile = pwsh
"@

# The names TabColorPalette actually offers; anything else is an op error.
$Colors = @('None', 'Blue', 'Purple', 'Pink', 'Red', 'Orange', 'Yellow', 'Green', 'Teal', 'Graphite')
# Each spawns a different foreground process, so tab icons and titles differ
# and the morph ghost has to copy something other than a default pwsh tab.
$Shells = @('powershell -NoLogo', 'cmd', 'powershell -NoLogo -Command "$host.UI.RawUI.WindowTitle=''fuzz''; cmd"')

# Per-run path: a fixed name would interleave lines from a concurrently
# running instrumented instance in another worktree and corrupt the oracle.
$log = Join-Path $env:TEMP "wintty-morph-$([guid]::NewGuid()).log"

$script:Findings = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null
$actions = @{}
$toggleAttempts = 0
$begins = 0
$ends = 0
$cancels = 0

$origTrace = if (Test-Path Env:WINTTY_MORPH_TRACE) { $env:WINTTY_MORPH_TRACE } else { $null }

function Get-TraceLines {
    if (Test-Path $log) { return @(Get-Content $log -ErrorAction SilentlyContinue) }
    return @()
}

# A counted toggle needs the coordinator idle: RequestToggleTabLayout
# no-ops mid-switch, so a toggle fired into a switch still in the air
# would be counted as an attempt that can never begin.
function Wait-CoordinatorIdle($Session, [int]$Seconds = 5) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $r = Invoke-SeamCommand $Session @{ op = 'get-state' }
        if (-not $r.state.switching) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

# A chord through the frame's real routing. Actions re-home focus, so the
# frame takes focus first. The focus op REFUSES while focus sits on an
# overlay - the MRU switcher the cycle op opens auto-dismisses on a 1.2s
# timer, and a chord aimed while it still holds focus is not ours to send -
# so the focus is retried across that window rather than treated as a
# failure. A chord that never dispatched after a clean focus is a corpus
# this harness could not establish, not a product finding.
function Invoke-FrameChord($Session, [int]$Key, [switch]$Ctrl, [switch]$Shift) {
    $focused = $false
    foreach ($attempt in 1..4) {
        try {
            [void](Invoke-SeamCommand $Session @{ op = 'focus'; target = 'frame' })
            $focused = $true
            break
        }
        catch {
            if ("$($_.Exception.Message)" -notmatch 'overlay') { throw }
            Start-Sleep -Milliseconds 450
        }
    }
    if (-not $focused) {
        throw 'HARVEST_MISS: focus never came back from the overlay - the switcher popup never dismissed'
    }
    $r = Invoke-SeamCommand $Session @{ op = 'chord'; key = $Key; ctrl = $Ctrl.IsPresent; shift = $Shift.IsPresent }
    if (-not $r.dispatched) {
        throw ("HARVEST_MISS: chord 0x{0:X2} was not dispatched (focus was '{1}')" -f $Key, $r.focus)
    }
}

try {
    Assert-NoWintty -Context 'The layout-morph fuzz'
    $env:WINTTY_MORPH_TRACE = $log
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config -AllowInput
    $hwnd64 = [int64]$session.Hwnd64
    Write-Host "hwnd=$hwnd64 pid=$($session.Proc.Id)"
    [void][SeamWin]::MoveWindow([SeamWin]::P($hwnd64), 40, 40, 1400, 860, $true)
    Start-Sleep -Milliseconds 700

    # Seed the strip so the very first switches already have a crowd. Chords,
    # not the seed op: real tabs running real shells churn their own titles,
    # which is part of what the switch has to survive.
    foreach ($i in 1..7) {
        Invoke-FrameChord $session 0x54 -Ctrl
        Start-Sleep -Milliseconds 300
    }

    # One deterministic probe before minutes of fuzzing: the router must
    # begin a switch AND the trace file must record it. await:false answers
    # under way, so the response's own state says whether a switch started;
    # the trace half then proves the oracle the verdict will read.
    $probe = Invoke-SeamCommand $session @{ op = 'toggle-layout'; await = $false }
    $probeBegan = [bool]$probe.state.switching
    $traced = $false
    $probeDl = (Get-Date).AddSeconds(6)
    while ((Get-Date) -lt $probeDl) {
        Start-Sleep -Milliseconds 200
        if (@(Get-TraceLines | Where-Object { $_ -match 'SWITCH begin' }).Count -gt 0) { $traced = $true; break }
    }
    if (-not $probeBegan -and -not $traced) {
        throw 'HARVEST_MISS: the probe toggle began no switch - the router never saw it'
    }
    if (-not $traced) {
        throw "HARVEST_MISS: the probe switch ran but the trace stayed empty at $log - the app ignored WINTTY_MORPH_TRACE (a build older than the trace)"
    }
    Write-Host "probe: began=$probeBegan traced=$traced"

    $tabs = 8
    for ($i = 0; $i -lt $Iterations; $i++) {
        if ($session.Proc.HasExited) { throw "APP_EXIT: process exited at iteration $i (code $($session.Proc.ExitCode))" }

        $roll = $rng.Next(100)
        if ($roll -lt 45) {
            $act = 'toggle'
            if (-not (Wait-CoordinatorIdle $session)) {
                # Not a finding: a coordinator still busy after 5s means the
                # next toggle cannot begin, and the floor below would charge
                # the product for a corpus that was never established.
                $act = 'toggle-skipped-busy'
            } else {
                $toggleAttempts++
                [void](Invoke-SeamCommand $session @{ op = 'toggle-layout'; await = $false })
                # Sometimes toggle again before the switch has landed: the
                # coordinator is meant to drop a request that arrives
                # mid-flight, and counting it would charge it as a lost
                # toggle instead.
                if ($rng.Next(100) -lt 30) {
                    Start-Sleep -Milliseconds $rng.Next(40, 320)
                    [void](Invoke-SeamCommand $session @{ op = 'toggle-layout'; await = $false })
                    $act = 'toggle-interrupted'
                }
            }
        }
        elseif ($roll -lt 58) {
            $act = 'new-tab'
            Invoke-FrameChord $session 0x54 -Ctrl
            $tabs++
        }
        elseif ($roll -lt 66 -and $tabs -gt 3) {
            # Manager truth, not a shadow counter: a stale count eventually
            # closes the last tab, which closes the window.
            $state = Invoke-SeamCommand $session @{ op = 'get-state' }
            $tabs = @($state.state.tabs).Count
            if ($tabs -gt 3) {
                $act = 'close-tab'
                [void](Invoke-SeamCommand $session @{ op = 'close'; index = [int]$state.state.active })
                $tabs--
            } else {
                $act = 'close-skipped'
            }
        }
        elseif ($roll -lt 78) {
            $act = 'switch-tab'
            [void](Invoke-SeamCommand $session @{ op = 'cycle'; forward = ($rng.Next(2) -eq 0) })
        }
        elseif ($roll -lt 90) {
            $act = 'spawn-shell'
            [void](Invoke-SeamCommand $session @{ op = 'send-text'; text = $Shells[$rng.Next($Shells.Count)] + "`r" })
        }
        else {
            $act = 'tab-color'
            $state = Invoke-SeamCommand $session @{ op = 'get-state' }
            $n = @($state.state.tabs).Count
            if ($n -gt 0) {
                [void](Invoke-SeamCommand $session @{
                    op    = 'tab-color'
                    index = $rng.Next($n)
                    color = $Colors[$rng.Next($Colors.Count)]
                })
            } else { $act = 'tab-color-skipped' }
        }

        $actions[$act] = 1 + ($actions[$act] ?? 0)
        Start-Sleep -Milliseconds $rng.Next(160, 700)
    }

    # Let the last switch land before the finally kills the process: a
    # switch still in the air at kill time is a begin with no end, which
    # the oracle would report as a switch that never finished. Waited on
    # rather than slept through - the tail can be switches deep.
    $settleDl = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $settleDl) {
        Start-Sleep -Milliseconds 200
        $lines = Get-TraceLines
        $b = @($lines | Where-Object { $_ -match 'SWITCH begin' }).Count
        $e = @($lines | Where-Object { $_ -match 'SWITCH end' -or $_ -match 'SWITCH cancel' }).Count
        if ($b -eq $e) { break }
    }
}
catch {
    $msg = "$($_.Exception.Message)"
    if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { $script:Findings.Add($msg) }
    else { $harnessError = $msg }
    Write-Host "ERROR: $msg" -ForegroundColor Red
}
finally {
    if ($null -ne $session) { Stop-SeamSession $session }
    if ($null -ne $origTrace) { $env:WINTTY_MORPH_TRACE = $origTrace }
    else { Remove-Item Env:WINTTY_MORPH_TRACE -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host 'actions:'
$actions.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Host ("  {0,-24} {1}" -f $_.Key, $_.Value) }

$lines = Get-TraceLines
if ($lines.Count -eq 0) {
    # Everything below reads the trace; without it the run judged nothing.
    if ($harnessError) { Write-Host "ERROR: $harnessError" -ForegroundColor Red; exit 1 }
    if ($script:Findings.Count -gt 0) { $script:Findings | ForEach-Object { Write-Host "FAIL: $_" } ; exit 2 }
    Write-Host 'HARVEST_MISS: no morph trace - the app ignored WINTTY_MORPH_TRACE'
    exit 1
}

$begins = @($lines | Where-Object { $_ -match 'SWITCH begin' }).Count
$ends = @($lines | Where-Object { $_ -match 'SWITCH end' }).Count
$immediate = @($lines | Where-Object { $_ -match 'MORPH immediate' }).Count
$deferred = @($lines | Where-Object { $_ -match 'MORPH deferred' }).Count
$waiting = @($lines | Where-Object { $_ -match 'MORPH waiting' }).Count
$none = @($lines | Where-Object { $_ -match 'MORPH none' }).Count
$cancels = @($lines | Where-Object { $_ -match 'SWITCH cancel' }).Count
$leaked = @($lines | Where-Object { $_ -match 'ghosts=[1-9]|morph=LEAKED' })

Write-Host ''
Write-Host "layout toggles  : $toggleAttempts"
Write-Host "switches begun  : $begins"
Write-Host "switches ended  : $ends"
Write-Host "switches cancel : $cancels"
Write-Host "morph immediate : $immediate"
Write-Host "morph deferred  : $deferred  (waited: $waiting)"
Write-Host "morph none      : $none"

if ($leaked.Count -gt 0) {
    $script:Findings.Add("$($leaked.Count) switch(es) ended with a ghost still on the morph layer")
    $leaked | Select-Object -First 8 | ForEach-Object { Write-Host "  LEAK: $_" }
}
if ($begins -ne ($ends + $cancels)) {
    $script:Findings.Add("switch begin/end mismatch: $begins begun vs $ends ended + $cancels cancelled (a switch never finished)")
}
if ($immediate -eq 0 -and $begins -gt 0) {
    $script:Findings.Add('no switch ever staged a morph immediately')
}

# The rewritten vacuity gate: one begin per counted toggle. Counted toggles
# waited for an idle coordinator first, so the request landed on a
# coordinator free to begin; a shortfall here means the toggles went out but
# the router never saw them, and every term above was measured over nothing.
# Exit 1 - retryable corpus - not 2.
if ($toggleAttempts -gt 0 -and $begins -lt $toggleAttempts) {
    if ($harnessError) { Write-Host "ERROR: $harnessError" -ForegroundColor Red }
    if ($script:Findings.Count -gt 0) {
        # Defects outrank an empty corpus: report what was measured.
        $script:Findings | ForEach-Object { Write-Host "FAIL: $_" }
        Write-Host "reproduce with: -Seed $Seed"
        exit 2
    }
    Write-Host ("HARVEST_MISS: only {0} switch(es) began for {1} counted toggles - the toggles went out but the router never saw them" -f $begins, $toggleAttempts)
    Write-Host "reproduce with: -Seed $Seed"
    exit 1
}

if ($script:Findings.Count -gt 0) {
    $script:Findings | ForEach-Object { Write-Host "FAIL: $_" }
    Write-Host "reproduce with: -Seed $Seed"
    exit 2
}
if ($harnessError) { Write-Host "ERROR: $harnessError" -ForegroundColor Red; exit 1 }

Write-Host "PASS (seed $Seed)"
exit 0
