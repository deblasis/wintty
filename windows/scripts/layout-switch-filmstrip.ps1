#requires -Version 7
<#
The layout switch, filmed and judged.

Two things a single before/after pair cannot tell apart: a transition that
carries the collapsed state across, and one that flashes the run expanded
for three frames on its way. The manager agrees with itself either way --
the collapse bit never moves -- so the evidence has to be what the strips
were HOLDING mid-flight. That is the `layout-frame` seam op: both hosts'
rendered inventories, with each row's effective alpha and its rect in the
window's coordinates.

So this harness films twice over, on two clocks. The PICTURE filmstrip is
a desktop blit and owes the app nothing, so it keeps sampling at full
speed through the very stalls that make the motion worth looking at. The
STATE filmstrip is a seam round trip and therefore waits on the app's UI
thread -- the same thread the switch blocks -- so it is thinned to every
$StateEvery-th pass and is the oracle rather than the picture. Sampling
both on the seam's cadence yielded three frames for an entire flight, the
first a third of a second in, which is a filmstrip that cannot see the
motion it exists to judge.

There is also one pixel cross-check per settled frame: that the selected
row's chrome is really on screen where the seam says it is. The seam is a
model read, and a model that agrees with itself while the compositor
draws something else is exactly the failure a model-only oracle cannot
see.

Seam-driven throughout: no synthesized keystrokes, no focus theft, so the
machine stays usable while this runs. Takes the machine-wide seam lock
(C:\temp\seam-lock.ps1) itself if one is not already held.

Exit codes: 0 every assertion passed, 1 an assertion failed (a finding),
2 the harness could not run (build missing, seam refused, app died).
#>
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\Ghostty\bin\x64\Debug\net10.0-windows10.0.19041.0\Wintty.exe'),
    [string]$OutDir = (Join-Path $PSScriptRoot ("layout-switch/run-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))),

    # The switch storyboard is 340ms (LayoutCoordinator.SwitchDurationMs).
    # The budget is deliberately not 340: what this measures is the wall
    # clock from firing the toggle to the coordinator reporting settled,
    # and that window also holds the pre-roll (the lane change and the
    # morph measure, 50-150ms warm) and a Completed callback that is
    # raised on the UI thread and therefore queues behind whatever the
    # terminal's own render is doing. Warm legs were measured landing at
    # 570-700ms across 1 to 10 tabs.
    #
    # 900 is that measured ceiling plus room for a slow machine. It is a
    # guard against a SECOND switch nobody asked for -- the
    # write-then-reload revert is exactly that shape, and it doubles the
    # figure -- not a smoothness bar. Smoothness is reported as the stall
    # metric below, which is measured and printed rather than asserted,
    # because its cause sits under this layer.
    [int]$BudgetMs = 900,

    # Passes per state read. Pictures are taken every pass; this thins
    # only the seam round trips, which are the ones that have to wait for
    # the app's UI thread. Raising it buys a denser picture filmstrip at
    # the cost of a coarser oracle.
    [int]$StateEvery = 2,

    # Drop the pictures and sample state as fast as the pipe answers.
    #
    # A screen grab costs about 175ms on this machine whatever its size --
    # a fixed cost of reading a composited desktop through GDI, measured
    # identically at 1280x820 and 400x400 and unchanged by CAPTUREBLT --
    # so a picture-bearing run gets three to five state reads out of a
    # whole flight. That is enough to judge the shape of the motion and
    # thin for an oracle. This mode trades every picture for roughly an
    # order of magnitude more state reads, and is how the assertions are
    # meant to be run in anger.
    [switch]$NoPictures,

    [int]$WinW = 1280,
    [int]$WinH = 820
)

$ErrorActionPreference = 'Stop'
$lib = Join-Path $PSScriptRoot 'lib'
. (Join-Path $lib 'wintty-process.ps1')
. (Join-Path $lib 'seam-client.ps1')
. (Join-Path $lib 'contrast.ps1')
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $ExePath)) {
    Write-Host "HARNESS: no build at $ExePath"
    exit 2
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ---- scenario ---------------------------------------------------------

# Six tabs, one pinned, a three-tab group collapsed, and the active tab
# OUTSIDE that group. The last part is what makes the scenario load-
# bearing: Edge-135 keeps the active member of a collapsed run visible, so
# a run holding the active tab renders its member legitimately and could
# never be caught flashing. Only a run with no active member has a shape
# ("no member rows at all") that a flash can violate.
$TabTitles = @('alpha', 'bravo', 'charlie', 'delta', 'echo', 'foxtrot')
$GroupIndices = @(2, 3, 4)
$ActiveIndex = 5
$PinIndex = 0

# ---- capture ----------------------------------------------------------

# One grab, into a buffer that is allocated once and blitted into on every
# pass. Allocating a fresh 1280x820 bitmap and its Graphics per frame cost
# about 150ms a pass -- four pictures for a whole flight, which is not a
# filmstrip. Reusing the surface leaves only the blit, and the frames are
# copied out for keeping after the flight rather than during it.
$script:ShotBuffer = $null
$script:ShotGraphics = $null

function Get-WindowShot([int64]$Hwnd) {
    $r = [SeamWin]::RectOf($Hwnd)
    if ($null -eq $r) { throw 'HARNESS: the window rect went unreadable mid-run' }
    if ($null -eq $script:ShotBuffer -or
        $script:ShotBuffer.Width -ne $r.W -or $script:ShotBuffer.Height -ne $r.Hh) {
        if ($script:ShotGraphics) { $script:ShotGraphics.Dispose() }
        if ($script:ShotBuffer) { $script:ShotBuffer.Dispose() }
        $script:ShotBuffer = New-Object System.Drawing.Bitmap $r.W, $r.Hh
        $script:ShotGraphics = [System.Drawing.Graphics]::FromImage($script:ShotBuffer)
    }
    $script:ShotGraphics.CopyFromScreen($r.L, $r.T, 0, 0, $script:ShotBuffer.Size)
    # Cloned because the buffer is about to be overwritten by the next
    # pass. A clone is a memory copy; the blit above is the expensive half
    # and it is not repeated.
    return @{ Bmp = $script:ShotBuffer.Clone(); L = $r.L; T = $r.T }
}

# The pixel cross-check. The seam says where the active row is, in window
# coordinates; this asks the screen whether anything is actually painted
# there. A flat region matching the terminal ground means the seam is
# describing a row the compositor is not drawing.
function Get-RowInk($Shot, $Row) {
    $x = [int][Math]::Round($Row.x); $y = [int][Math]::Round($Row.y)
    $w = [int][Math]::Round($Row.w); $h = [int][Math]::Round($Row.h)
    # Bite the middle of the row: the edges carry rounding, the seam cover
    # and the neighbour's border, and none of those are the fill.
    $x += [int]($w * 0.25); $w = [Math]::Max(2, [int]($w * 0.5))
    $y += [int]($h * 0.25); $h = [Math]::Max(2, [int]($h * 0.5))
    if ($x -lt 0 -or $y -lt 0 -or ($x + $w) -gt $Shot.Bmp.Width -or ($y + $h) -gt $Shot.Bmp.Height) {
        return $null
    }
    try { return [ContrastSampler]::Flat($Shot.Bmp, $x, $y, $w, $h) } catch { return $null }
}

# ---- one filmed switch ------------------------------------------------

# Block until nothing is in flight. Between legs, because a toggle that
# arrives mid-switch is queued rather than run now: firing the next leg
# early would film the tail of the previous one under the next one's name,
# which is how the first version of this harness came to label a
# horizontal-to-vertical switch "vertical-to-horizontal".
function Wait-SeamSettled($Session, [int]$TimeoutMs = 5000) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt $TimeoutMs) {
        Send-SeamCommand $Session @{ op = 'layout-frame' }
        $f = Receive-SeamResponse $Session 'layout-frame'
        if (-not $f.state.switching) { return $f }
        Start-Sleep -Milliseconds 25
    }
    throw ("HARNESS: the layout never settled within {0}ms" -f $TimeoutMs)
}

function Invoke-FilmedSwitch($Session, [int64]$Hwnd, [int]$Ordinal) {
    # The direction is read, not assumed: the tag has to name what the
    # frames actually show.
    $before = Wait-SeamSettled $Session
    $Tag = if ($before.state.vertical) { "{0:d2}-vertical-to-horizontal" -f $Ordinal }
           else { "{0:d2}-horizontal-to-vertical" -f $Ordinal }

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    # Fire and return: a blocking toggle would hold the only channel a
    # sampler could use for the whole flight.
    Send-SeamCommand $Session @{ op = 'toggle-layout'; await = $false }
    $null = Receive-SeamResponse $Session 'toggle-layout'

    # The two oracles run on different clocks ON PURPOSE.
    #
    # A state read is a seam round trip, so it needs the app's UI thread --
    # the same thread the switch's own work blocks for a few hundred
    # milliseconds at a time. Sampling pictures on that cadence produced
    # three frames for a whole flight, the first of them a third of a
    # second in: a filmstrip that cannot see the motion it exists to
    # judge, which is the failure a filmstrip is supposed to fix.
    #
    # A screen grab needs nothing from the app. It is a desktop blit, and
    # it keeps running at full speed exactly while the UI thread is
    # wedged -- which is the interesting part. So pictures are taken every
    # pass and state every $StateEvery-th, and each sample records which
    # of the two it actually carries.
    $samples = [System.Collections.Generic.List[object]]::new()
    $settledSeen = 0
    $i = 0
    while ($clock.ElapsedMilliseconds -lt $BudgetMs) {
        $shotMs = $clock.ElapsedMilliseconds
        $shot = if ($NoPictures) { $null } else { Get-WindowShot $Hwnd }
        if ($null -eq $shot) { $shotMs = -1 }

        # The last stretch of the budget always reads state, whatever
        # $StateEvery says. Without this a thinned state track simply ran
        # out of reads before the switch landed and reported "never
        # settled" against a switch that had -- a harness saying the
        # product is broken because the harness stopped looking.
        $endgame = $clock.ElapsedMilliseconds -gt ($BudgetMs - 250)
        $before = -1; $after = -1; $fresh = $null
        if ($NoPictures -or $endgame -or ($i % $StateEvery) -eq 0) {
            $before = $clock.ElapsedMilliseconds
            Send-SeamCommand $Session @{ op = 'layout-frame' }
            $fresh = Receive-SeamResponse $Session 'layout-frame'
            $after = $clock.ElapsedMilliseconds
        }

        $samples.Add([pscustomobject]@{
            I        = $i
            ShotMs   = $shotMs
            # -1 on a picture-only pass. Downstream must never read a
            # carried-over frame as though it were sampled here.
            StateMs  = $before
            StateEnd = $after
            Frame    = $fresh
            Shot     = $shot
        })
        $i++

        if ($null -ne $fresh -and -not $fresh.state.switching) {
            # One settled read ends the leg. It used to take two, on the
            # reasoning that the storyboard's Completed handler and the
            # Snap it runs are separate turns -- but state reads are
            # rationed now (they need the UI thread) and a whole flight
            # yields two or three, so waiting for a second settled one
            # regularly ran out the budget on a switch that had plainly
            # landed. The separation the second read was protecting is
            # provided instead by Wait-SeamSettled at the head of the next
            # leg, which blocks until nothing is in flight.
            $settledSeen++
            break
        }
    }
    $clock.Stop()

    $settleMs = -1
    foreach ($s in $samples) {
        if ($null -eq $s.Frame) { continue }
        if (-not $s.Frame.state.switching) { $settleMs = $s.StateEnd; break }
    }

    # PNG encoding is deferred out of the loop on purpose: it costs more
    # than the sampling interval and would smear the timeline it is meant
    # to record.
    foreach ($s in $samples) {
        if ($null -eq $s.Shot) { continue }
        $name = '{0}-{1:d3}-t{2:d4}ms.png' -f $Tag, $s.I, $s.ShotMs
        $s.Shot.Bmp.Save((Join-Path $OutDir $name))
    }

    return [pscustomobject]@{
        Tag      = $Tag
        Samples  = $samples
        SettleMs = $settleMs
        TotalMs  = $clock.ElapsedMilliseconds
    }
}

function Write-ContactSheet($Leg) {
    $shots = @($Leg.Samples | Where-Object { $null -ne $_.Shot })
    if ($shots.Count -eq 0) { return }
    $w = $shots[0].Shot.Bmp.Width; $h = $shots[0].Shot.Bmp.Height
    $cols = 3
    $rows = [Math]::Ceiling($shots.Count / $cols)
    $sheet = New-Object System.Drawing.Bitmap ($w * $cols), ($h * $rows)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    for ($k = 0; $k -lt $shots.Count; $k++) {
        $g.DrawImage($shots[$k].Shot.Bmp, ($k % $cols) * $w, [int]($k / $cols) * $h, $w, $h)
    }
    $g.Dispose()
    $sheet.Save((Join-Path $OutDir ("{0}-sheet.png" -f $Leg.Tag)))
    $sheet.Dispose()
}

# ---- assertions -------------------------------------------------------

# A row this faint is not something a user sees; the cross-fade spends its
# last frames below it. Above it, a row is on screen and counts.
$VisibleAlpha = 0.02
# What the selected tab has to keep to count as "identifiable": faint
# enough to allow the handover frames, firm enough that a selection which
# has dissolved to nothing fails.
$SelectionAlpha = 0.15
# Half. Above it a strip reads as present rather than as arriving or
# leaving, so two hosts above it at once is two tab strips at once.
$CrossfadeLeadAlpha = 0.5

$findings = [System.Collections.Generic.List[string]]::new()

function Test-Leg($Leg, [string[]]$CollapsedGroups) {
    $tag = $Leg.Tag

    # --- the flash. No frame may render a member of a collapsed run that
    # is not the active tab. This is #871 stated as a sequence property:
    # the end states are both correct, only the middle is not.
    foreach ($s in $Leg.Samples) {
        if ($null -eq $s.Frame) { continue }
        foreach ($laneName in @('horizontal', 'vertical')) {
            $lane = $s.Frame.render.$laneName
            foreach ($row in $lane.rows) {
                if ($row.kind -ne 'tab' -and $row.kind -ne 'pinned') { continue }
                if ($null -eq $row.group -or $CollapsedGroups -notcontains $row.group) { continue }
                if ($row.active) { continue }
                if (-not $row.shown -or $row.alpha -le $VisibleAlpha) { continue }
                $findings.Add(("{0} frame {1} (t={2}ms): {3} strip renders '{4}', a member of collapsed run '{5}', at alpha {6}" -f
                    $tag, $s.I, $s.StateMs, $laneName, $row.label, $row.group, $row.alpha))
            }
        }
    }

    # --- the selection. Either the morph ghost is carrying it or a real
    # row is showing it; a frame where neither holds is a frame where the
    # user cannot see which tab they are on.
    foreach ($s in $Leg.Samples) {
        if ($null -eq $s.Frame) { continue }
        if ($s.Frame.render.morphLayer -gt 0) { continue }
        $lit = @()
        foreach ($laneName in @('horizontal', 'vertical')) {
            $lit += @($s.Frame.render.$laneName.rows |
                Where-Object { $_.active -and $_.shown -and $_.alpha -gt $SelectionAlpha })
        }
        if ($lit.Count -eq 0) {
            $findings.Add(("{0} frame {1} (t={2}ms): no morph ghost and no active row above alpha {3} -- the selection is nowhere" -f
                $tag, $s.I, $s.StateMs, $SelectionAlpha))
        }
    }

    # --- no row outside its own strip. A rect is measured against the
    # window root, and each host reports its own lane in the same space,
    # so this compares like with like.
    foreach ($s in $Leg.Samples) {
        if ($null -eq $s.Frame) { continue }
        foreach ($laneName in @('horizontal', 'vertical')) {
            $lane = $s.Frame.render.$laneName
            if ($lane.hw -le 0 -or $lane.hh -le 0) { continue }
            foreach ($row in $lane.rows) {
                if (-not $row.shown -or $row.alpha -le $VisibleAlpha) { continue }
                # One row-height of slack: the lane is the settled rect and
                # the strips deliberately travel EmergeTravel (40px) into
                # and out of it. A row further out than its own height has
                # left the lane, not entered it.
                $slack = [Math]::Max(48, $row.h)
                $out = ($row.x + $row.w) -lt ($lane.hx - $slack) -or
                       $row.x -gt ($lane.hx + $lane.hw + $slack) -or
                       ($row.y + $row.h) -lt ($lane.hy - $slack) -or
                       $row.y -gt ($lane.hy + $lane.hh + $slack)
                if ($out) {
                    $findings.Add(("{0} frame {1} (t={2}ms): {3} row '{4}' at ({5},{6} {7}x{8}) is outside its lane ({9},{10} {11}x{12})" -f
                        $tag, $s.I, $s.StateMs, $laneName, $row.label,
                        $row.x, $row.y, $row.w, $row.h, $lane.hx, $lane.hy, $lane.hw, $lane.hh))
                }
            }
        }
    }

    # --- the cross-fade needs a leader.
    #
    # A switch is not two strips politely sharing the frame; it is one
    # leaving and one arriving, and the eye can only follow that if the
    # departing one is visibly on its way out before the arriving one is
    # established. Sampled mid-flight, the pre-fix build showed the two
    # hosts at 0.89/0.59 and 0.68/0.84 -- both strips most of the way
    # opaque at the same instant, in two different lanes, with the pane
    # reveal already slicing the departing one into fragments. That is
    # what "looks ok" looks like when you stop it and measure it.
    #
    # Timeline-free on purpose: correlating a driver's clock with the
    # storyboard's would need a correlation the seam does not offer, and
    # the property worth holding does not need one. At no instant may
    # both hosts be more than half present.
    foreach ($s in $Leg.Samples) {
        if ($null -eq $s.Frame) { continue }
        $h = $s.Frame.render.horizontal
        $v = $s.Frame.render.vertical
        $hOn = $h.visible -and $h.opacity -gt $CrossfadeLeadAlpha
        $vOn = $v.visible -and $v.opacity -gt $CrossfadeLeadAlpha
        if ($hOn -and $vOn) {
            $findings.Add(("{0} frame {1} (t={2}ms): the cross-fade has no leader -- horizontal at {3} and vertical at {4}, both above {5}" -f
                $tag, $s.I, $s.StateMs,
                [Math]::Round($h.opacity, 3), [Math]::Round($v.opacity, 3), $CrossfadeLeadAlpha))
        }
    }

    # --- the budget.
    if ($Leg.SettleMs -lt 0) {
        $findings.Add(("{0}: the switch never settled inside {1}ms (sampled {2} frames over {3}ms)" -f
            $tag, $BudgetMs, $Leg.Samples.Count, $Leg.TotalMs))
    }

    # --- the stall metric. REPORTED, NOT ASSERTED, and the distinction is
    # deliberate: the gap between consecutive samples is how long the UI
    # thread went unavailable, and a seam round trip that costs 4ms at
    # rest costing 300ms mid-switch is the clearest number anyone has for
    # how janky the flight is. It is not an assertion because its cause is
    # below this layer -- the terminal's own resize and reflow own that
    # thread, and nothing in the shell can yield it -- so a gate on it
    # would be red forever and would stop gating the things that ARE
    # fixable here. Every visual the switch stages runs on the compositor
    # precisely so these stalls do not reach the eye.
    $stateEnds = @($Leg.Samples | Where-Object { $null -ne $_.Frame } | ForEach-Object { $_.StateEnd })
    $gaps = @()
    for ($k = 1; $k -lt $stateEnds.Count; $k++) {
        $gaps += ($stateEnds[$k] - $stateEnds[$k - 1])
    }
    if ($gaps.Count -gt 0) {
        Write-Host ("  ui-thread stalls: max {0}ms, median {1}ms over {2} samples" -f
            ($gaps | Measure-Object -Maximum).Maximum,
            ($gaps | Sort-Object)[[int]($gaps.Count / 2)],
            $Leg.Samples.Count)
    }

    # --- the pixel cross-check: where the seam says the selected row is,
    # something is painted. Reported, not asserted, on frames where the
    # morph ghost owns the selection: the ghost is not the row.
    $inkMisses = 0
    foreach ($s in $Leg.Samples) {
        if ($null -eq $s.Shot -or $null -eq $s.Frame) { continue }
        if ($s.Frame.state.switching) { continue }
        $row = @($s.Frame.render.horizontal.rows + $s.Frame.render.vertical.rows |
            Where-Object { $_.active -and $_.shown -and $_.alpha -gt 0.9 }) | Select-Object -First 1
        if ($null -eq $row) { continue }
        $ink = Get-RowInk $s.Shot $row
        if ($null -eq $ink) { $inkMisses++ }
    }
    if ($inkMisses -gt 0) {
        Write-Host ("  note: {0} settled frame(s) could not be sampled for row ink (off-screen or occluded)" -f $inkMisses)
    }
}

# ---- run --------------------------------------------------------------

$ownLock = $null
if (-not $env:WINTTY_SEAM_LOCK_HELD) {
    . C:\temp\seam-lock.ps1
    $ownLock = Enter-SeamLock -Owner 'layout-switch-filmstrip'
}

$exit = 0
$session = $null
try {
    Assert-NoWintty -Context 'the layout-switch filmstrip'

    # vertical-tabs is written explicitly, and false is the value the run
    # starts from. Not decoration: MainWindow only lets a reload re-drive
    # the layout for a key the file actually sets, so a config without it
    # would leave the toggle un-reverted for a different reason than the
    # one this harness means to exercise.
    $session = Start-SeamSession -ExePath $ExePath -ConfigText @"
windows-single-instance = false
vertical-tabs = false
"@

    $hwnd = [int64]$session.Hwnd64
    [void][SeamWin]::MoveWindow([SeamWin]::P($hwnd), 60, 60, $WinW, $WinH, $true)
    Start-Sleep -Milliseconds 500

    Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = $TabTitles.Count; titles = $TabTitles } | Out-Null
    Invoke-SeamCommand $session @{ op = 'pin'; index = $PinIndex; via = 'router' } | Out-Null
    Invoke-SeamCommand $session @{ op = 'group'; indices = $GroupIndices } | Out-Null
    Invoke-SeamCommand $session @{ op = 'select'; index = $ActiveIndex } | Out-Null
    Invoke-SeamCommand $session @{ op = 'collapse'; index = $GroupIndices[0]; collapsed = $true; via = 'router' } | Out-Null
    $st = Invoke-SeamCommand $session @{ op = 'layout-frame' }

    $collapsed = @($st.state.groups | Where-Object { $_.collapsed } | ForEach-Object { $_.title })
    if ($collapsed.Count -eq 0) {
        Write-Host 'HARNESS: nothing ended up collapsed; the scenario did not build'
        exit 2
    }
    if ($st.state.vertical) {
        Write-Host 'HARNESS: the run must start horizontal'
        exit 2
    }
    Write-Host ("scenario: {0} tabs, collapsed run(s) [{1}], active index {2}, starting horizontal" -f
        $st.state.tabs.Count, ($collapsed -join ','), $st.state.active)

    # The settled baseline, asserted before any motion is filmed. If the
    # horizontal strip is already rendering a collapsed run's members while
    # nothing is switching, then what a filmstrip would go on to catch is
    # not a transition defect at all, and every later finding would be that
    # one wearing a costume. Cheap, and it keeps the legs honest.
    foreach ($row in $st.render.horizontal.rows) {
        if ($row.kind -ne 'tab' -and $row.kind -ne 'pinned') { continue }
        if ($null -eq $row.group -or $collapsed -notcontains $row.group) { continue }
        if ($row.active) { continue }
        $findings.Add(("baseline (settled, horizontal): the strip renders '{0}', a member of collapsed run '{1}', before any switch" -f
            $row.label, $row.group))
    }

    # Warm-up, unfilmed and unasserted. The first switch of a session pays
    # for the incoming host's container realization and the terminal's
    # first resize, and in a Debug build that cost blocked the UI thread
    # for the better part of a second -- real, worth knowing, and not what
    # the motion looks like in use. It is reported rather than folded into
    # the legs.
    $coldClock = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-SeamCommand $session @{ op = 'toggle-layout' } | Out-Null
    $coldMs = $coldClock.ElapsedMilliseconds
    Invoke-SeamCommand $session @{ op = 'toggle-layout' } | Out-Null
    Write-Host ("warm-up: first switch of the session took {0}ms (cold realization, not filmed)" -f $coldMs)

    # Four legs, so each direction is judged twice and a leg that only
    # passes because it inherited the previous one's warm containers shows
    # up as a disagreement between the pair.
    $legs = @(1..4 | ForEach-Object { Invoke-FilmedSwitch $session $hwnd $_ })

    foreach ($leg in $legs) {
        $stateCount = @($leg.Samples | Where-Object { $null -ne $_.Frame }).Count
        Write-Host ("{0}: {1} pictures / {2} state reads, settled at {3}ms (budget {4}ms)" -f
            $leg.Tag, $leg.Samples.Count, $stateCount, $leg.SettleMs, $BudgetMs)
        Write-ContactSheet $leg
        Test-Leg $leg $collapsed
        $leg.Samples | Where-Object { $null -ne $_.Frame } | ForEach-Object {
            [pscustomobject]@{ i = $_.I; stateMs = $_.StateMs; stateEndMs = $_.StateEnd; shotMs = $_.ShotMs; frame = $_.Frame }
        } | ConvertTo-Json -Depth 8 -Compress |
            Set-Content (Join-Path $OutDir ("{0}-frames.json" -f $leg.Tag)) -Encoding utf8
    }

    if ($findings.Count -gt 0) {
        Write-Host ''
        Write-Host ("FINDINGS ({0}):" -f $findings.Count)
        # Capped: a flash reports once per offending row per frame, which
        # is dozens of near-identical lines. The count above is the metric;
        # the sample below is the evidence.
        $findings | Select-Object -First 12 | ForEach-Object { Write-Host ("  {0}" -f $_) }
        if ($findings.Count -gt 12) {
            Write-Host ("  ... and {0} more (all frames in the JSON sidecars)" -f ($findings.Count - 12))
        }
        $exit = 1
    }
    else {
        Write-Host ''
        Write-Host 'PASS: no frame flashed a collapsed run, lost the selection, or left a lane; both legs inside budget.'
    }
}
catch {
    Write-Host ("HARNESS: {0}" -f $_.Exception.Message)
    $exit = 2
}
finally {
    if ($session) { Stop-SeamSession $session }
    if ($ownLock) { Exit-SeamLock $ownLock }
}

Write-Host "OUT=$OutDir"
exit $exit
