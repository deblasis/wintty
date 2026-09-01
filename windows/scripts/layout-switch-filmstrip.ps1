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

So this harness films twice over, on two clocks that do not share a
thread.

The PICTURE track is lib/window-capture.ps1: a separate process taking
frames from the compositor. It owes the app nothing and keeps running at
the window's full present rate straight through the stalls that make the
motion worth looking at. It replaced a Graphics.CopyFromScreen loop that
cost ~175ms per grab whatever the region size, and which got three
pictures out of an entire flight, the first a third of a second in -- a
filmstrip that could not see the motion it exists to judge.

The STATE track is a seam round trip and therefore does wait on the app's
UI thread. That is fine, because it is the oracle rather than the picture:
it reports what each host is RENDERING, which no pixel comparison can
tell you, and a few dozen reads per flight is plenty for the sequence
properties asserted below.

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

    # Skip the camera. The state track is the oracle and does not need
    # pictures; a run that only has to answer "did it regress" is faster
    # without them.
    [switch]$NoPictures,

    [int]$WinW = 1280,
    [int]$WinH = 820
)

$ErrorActionPreference = 'Stop'
$lib = Join-Path $PSScriptRoot 'lib'
. (Join-Path $lib 'wintty-process.ps1')
. (Join-Path $lib 'seam-client.ps1')
. (Join-Path $lib 'contrast.ps1')
. (Join-Path $lib 'window-capture.ps1')
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

# The pixel cross-check, run once per leg against a settled frame the
# camera caught. The seam says where the active row is, in window
# coordinates; this asks the picture whether anything is actually painted
# there. A model that agrees with itself while the compositor draws
# something else is the one failure a model-only oracle cannot see.
function Get-RowInk([string]$PngPath, $Row) {
    if (-not (Test-Path $PngPath)) { return $null }
    $bmp = [System.Drawing.Image]::FromFile($PngPath)
    try {
        $x = [int][Math]::Round($Row.x); $y = [int][Math]::Round($Row.y)
        $w = [int][Math]::Round($Row.w); $h = [int][Math]::Round($Row.h)
        # Bite the middle of the row: the edges carry rounding, the seam
        # cover and the neighbour's border, and none of those are the fill.
        $x += [int]($w * 0.25); $w = [Math]::Max(2, [int]($w * 0.5))
        $y += [int]($h * 0.25); $h = [Math]::Max(2, [int]($h * 0.5))
        if ($x -lt 0 -or $y -lt 0 -or ($x + $w) -gt $bmp.Width -or ($y + $h) -gt $bmp.Height) {
            return $null
        }
        return [ContrastSampler]::Flat($bmp, $x, $y, $w, $h)
    }
    catch { return $null }
    finally { $bmp.Dispose() }
}

# The film half of the leader evidence: whether the incoming strip's lane
# CHANGES progressively through the fade window, which is what a rendered
# fade does and a dead channel (strip popping in at the landing) does not.
# The active tab's own band is excluded, because the ghost lands there and
# would register change with the fades stone dead. Returns the number of
# consecutive frame pairs inside the window whose lane slice differed, and
# how many frames the window held -- a camera that only delivered one
# frame there measured nothing, and says so.
function Measure-FadeProgress($Leg, [string]$IncomingLane) {
    if ($null -eq $Leg.Film -or $Leg.Film.Frames.Count -lt 2) { return $null }
    $settled = @($Leg.Samples | Where-Object {
        $null -ne $_.Frame -and -not $_.Frame.state.switching }) | Select-Object -Last 1
    if ($null -eq $settled) { return $null }
    $lane = $settled.Frame.render.$IncomingLane
    if ($lane.hw -le 0 -or $lane.hh -le 0) { return $null }
    $active = @($lane.rows | Where-Object { $_.active }) | Select-Object -First 1

    $frames = @($Leg.Film.Frames |
        Where-Object { $_.sinceStartMs -ge 80 -and $_.sinceStartMs -le 340 })
    if ($frames.Count -lt 2) {
        return [pscustomobject]@{ Pairs = 0; Changed = 0; Frames = $frames.Count }
    }

    function LaneSlice([string]$path) {
        $bmp = [System.Drawing.Bitmap]::new($path)
        try {
            $x0 = [int][Math]::Max(0, $lane.hx); $y0 = [int][Math]::Max(0, $lane.hy)
            $x1 = [int][Math]::Min($bmp.Width, $lane.hx + $lane.hw)
            $y1 = [int][Math]::Min($bmp.Height, $lane.hy + $lane.hh)
            $vals = [System.Collections.Generic.List[int]]::new()
            for ($y = $y0; $y -lt $y1; $y += 4) {
                for ($x = $x0; $x -lt $x1; $x += 4) {
                    if ($null -ne $active -and
                        $x -ge $active.x -and $x -lt ($active.x + $active.w) -and
                        $y -ge $active.y -and $y -lt ($active.y + $active.h)) { continue }
                    $c = $bmp.GetPixel($x, $y)
                    $vals.Add([int]$c.R + [int]$c.G + [int]$c.B)
                }
            }
            return ,$vals
        } finally { $bmp.Dispose() }
    }

    $changed = 0; $pairs = 0
    $prev = $null
    foreach ($f in $frames) {
        $slice = LaneSlice (Join-Path $Leg.Film.OutDir $f.file)
        if ($null -ne $prev -and $slice.Count -gt 0 -and $slice.Count -eq $prev.Count) {
            $pairs++
            [long]$sum = 0
            for ($i = 0; $i -lt $slice.Count; $i++) {
                $sum += [Math]::Abs($slice[$i] - $prev[$i])
            }
            if (($sum / $slice.Count) -gt 2.0) { $changed++ }
        }
        $prev = $slice
    }
    return [pscustomobject]@{ Pairs = $pairs; Changed = $changed; Frames = $frames.Count }
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

    # Roll the camera BEFORE firing the toggle. Start-WindowCapture returns
    # only once the capture session has actually started, so the opening
    # frames -- the ones a switch is judged on -- are in the film rather
    # than missed while the tool warms up.
    $capture = $null
    if (-not $NoPictures) {
        $capture = Start-WindowCapture -Hwnd $Hwnd -OutDir $OutDir -Tag $Tag `
            -DurationMs $BudgetMs -MaxFrames 200
    }

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    # Fire and return: a blocking toggle would hold the only channel a
    # sampler could use for the whole flight.
    Send-SeamCommand $Session @{ op = 'toggle-layout'; await = $false }
    $null = Receive-SeamResponse $Session 'toggle-layout'

    # State only. The camera is already rolling in its own process on its
    # own clock, so this loop no longer has to interleave pictures with
    # seam round trips -- which is what used to make both tracks as slow
    # as the slower one.
    #
    # It still stalls, and the stalls are the app's: a layout-frame round
    # trip costs about 4ms at rest and a few hundred mid-switch, because
    # the terminal's own resize owns the UI thread this has to marshal on.
    # Those gaps are reported as a metric below rather than smoothed over.
    $samples = [System.Collections.Generic.List[object]]::new()
    $settledSeen = 0
    $i = 0
    while ($clock.ElapsedMilliseconds -lt $BudgetMs) {
        $before = $clock.ElapsedMilliseconds
        Send-SeamCommand $Session @{ op = 'layout-frame' }
        $fresh = Receive-SeamResponse $Session 'layout-frame'
        $after = $clock.ElapsedMilliseconds

        $samples.Add([pscustomobject]@{
            I        = $i
            StateMs  = $before
            StateEnd = $after
            Frame    = $fresh
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

    # The camera runs its own clock and stops on its own. Collected after
    # the state loop so the film covers the landing too, not just the part
    # the oracle stayed awake for.
    $film = if ($capture) { Stop-WindowCapture $capture } else { $null }

    $settleMs = -1
    foreach ($s in $samples) {
        if ($null -eq $s.Frame) { continue }
        if (-not $s.Frame.state.switching) { $settleMs = $s.StateEnd; break }
    }

    return [pscustomobject]@{
        Tag      = $Tag
        Samples  = $samples
        SettleMs = $settleMs
        TotalMs  = $clock.ElapsedMilliseconds
        Film     = $film
    }
}

# A strip of the frames that actually cover the motion, side by side and
# scaled down, so one look answers "what shape is this". Deliberately only
# the flight: the settled tail is a dozen identical pictures and it is the
# middle that is ever in question.
function Write-ContactSheet($Leg, [int]$UntilMs) {
    if ($null -eq $Leg.Film) { return }
    # sinceStartMs, not atMs: the camera's clock starts before the toggle
    # is fired and the offset is around a hundred milliseconds, which on a
    # 340ms animation picks the wrong frames while still looking plausible.
    $frames = @($Leg.Film.Frames |
        Where-Object { $_.sinceStartMs -ge 0 -and $_.sinceStartMs -le $UntilMs })
    if ($frames.Count -eq 0) { return }
    # At most twelve, evenly spaced: a sheet wider than that stops being
    # readable at the size anyone will look at it.
    if ($frames.Count -gt 12) {
        $step = $frames.Count / 12.0
        $frames = @(0..11 | ForEach-Object { $frames[[int]($_ * $step)] })
    }
    $scale = 0.5
    $w = [int]($frames[0].w * $scale); $h = [int]($frames[0].h * $scale)
    $cols = 4
    $rows = [Math]::Ceiling($frames.Count / $cols)
    $sheet = New-Object System.Drawing.Bitmap ($w * $cols), ($h * $rows)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    for ($k = 0; $k -lt $frames.Count; $k++) {
        $img = [System.Drawing.Image]::FromFile((Join-Path $Leg.Film.OutDir $frames[$k].file))
        $g.DrawImage($img, ($k % $cols) * $w, [int]($k / $cols) * $h, $w, $h)
        $img.Dispose()
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
# There is no $CrossfadeLeadAlpha any more, and that is deliberate: the
# live leader assertion it parameterized went blind when the fades moved
# onto compositor expressions (stale reads; see the leader comment in
# Test-Leg for where the property is asserted now).

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
                # left the lane, not entered it. (The slides ride the
                # compositor's Translation now, which TransformToVisual
                # never sees, so these rects are resting rects and the
                # slack is generous rather than load-bearing -- kept, so a
                # future XAML-side motion cannot silently reintroduce the
                # failure this catches.)
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

    # --- the cross-fade needs a leader, and this oracle can no longer
    # watch it live.
    #
    # The fades ride compositor ExpressionAnimations now, and animated
    # composition values read from the UI thread are STALE -- measured on
    # this SDK by the T-model spike: the driving scalar read 0.000 for an
    # entire flight while the screen animated, and a visual's Opacity
    # reflected neither the running expression nor the writes that
    # followed it. The element opacities this track reads sit at their
    # end-state constants for the whole flight, so the old per-sample
    # assertion here would pass vacuously forever -- which is not
    # coverage, it is a test that cannot go red wearing one's clothes.
    #
    # The decision, made deliberately: the leader margin is asserted over
    # the AUTHORED curves from elapsed time -- the weaker option, and
    # named as such on purpose -- in
    # Ghostty.Tests TabLayoutSwitchWiringTests.LeaderMargin_HoldsOverTheAuthoredCurves,
    # against the same constants the expressions are built from. What the
    # curves cannot prove is that the fades RENDER at all; the film is the
    # witness for that half, reported below as the fade-progress metric
    # (reported, not asserted, because the camera's delivery rate varies
    # 7-28fps run to run and a gate that flakes with the camera stops
    # gating the product).
    $incomingLane = if ($tag -match 'to-vertical$') { 'vertical' } else { 'horizontal' }
    $fade = Measure-FadeProgress $Leg $incomingLane
    if ($null -ne $fade) {
        Write-Host ("  fade-progress ({0} lane): {1}/{2} changed pairs over {3} frames in the 80-340ms window" -f
            $incomingLane, $fade.Changed, $fade.Pairs, $fade.Frames)
        if ($fade.Pairs -ge 2 -and $fade.Changed -eq 0) {
            Write-Host '  note: the incoming lane never changed during the fade window -- if this repeats across legs, the fades are not rendering'
        }
    }

    # There is no caption-seam assertion here, and that is deliberate
    # rather than an omission (#892).
    #
    # The defect was real and was caught: a geometric check comparing the
    # seam cover against the caption fill it continued reported 136
    # findings across four legs -- up to 10 DIP out of position, opacity
    # 1.0 over a row at 0.74, and absent for entire flights. The fix was to
    # DELETE the cover, after measuring that it masks nothing (identical to
    # the pixel with and without it, on two window themes), so that check
    # now has nothing to compare and would pass by vacuity.
    #
    # A pixel replacement was attempted and abandoned rather than shipped.
    # On the default dark palette the largest step across the band in any
    # frame of a switch was 2.7 of 255 -- the misplacement was real and
    # invisible -- so the check passed against the pre-fix build, which is
    # the definition of a test that does not work. On a light palette the
    # stroke locator it depends on latched onto strip edges instead of the
    # stroke. A check that cannot be shown red is not coverage, and one
    # that looks like coverage is worse than none.
    #
    # What guards this now is that the two rectangles are one rectangle.

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
    if ($null -ne $Leg.Film -and $Leg.Film.Frames.Count -gt 0) {
        $settled = @($Leg.Samples | Where-Object {
            $null -ne $_.Frame -and -not $_.Frame.state.switching }) | Select-Object -First 1
        $last = $Leg.Film.Frames[-1]
        if ($null -ne $settled -and $null -ne $last) {
            $row = @($settled.Frame.render.horizontal.rows + $settled.Frame.render.vertical.rows |
                Where-Object { $_.active -and $_.shown -and $_.alpha -gt 0.9 }) | Select-Object -First 1
            if ($null -ne $row) {
                $ink = Get-RowInk (Join-Path $Leg.Film.OutDir $last.file) $row
                if ($null -eq $ink) {
                    Write-Host '  note: the selected row could not be sampled for ink in the last frame'
                }
            }
        }
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
        $film = if ($leg.Film) {
            "{0} frames at {1:F1} fps, {2} dropped, camera lead {3:F0}ms" -f
                $leg.Film.Frames.Count, $leg.Film.Fps, $leg.Film.Dropped, $leg.Film.ReadyMs
        } else { 'no camera' }
        Write-Host ("{0}: {1} state reads, settled at {2}ms (budget {3}ms); film: {4}" -f
            $leg.Tag, $leg.Samples.Count, $leg.SettleMs, $BudgetMs, $film)
        Write-ContactSheet $leg ([Math]::Max($leg.SettleMs, 400))
        Test-Leg $leg $collapsed
        if ($leg.Film) {
            $leg.Film.Frames | ConvertTo-Json -Depth 4 -Compress |
                Set-Content (Join-Path $OutDir ("{0}-film.json" -f $leg.Tag)) -Encoding utf8
        }
        $leg.Samples | Where-Object { $null -ne $_.Frame } | ForEach-Object {
            [pscustomobject]@{ i = $_.I; stateMs = $_.StateMs; stateEndMs = $_.StateEnd; frame = $_.Frame }
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
