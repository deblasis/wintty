<#
.SYNOPSIS
    The active tab is the field: measure the join it makes with the terminal.

.DESCRIPTION
    The active tab alone is painted the terminal's ground and runs into the
    pane below (horizontal) or beside it (vertical) with no line between. The
    line is removed by a seam cover: a rectangle of the tab's own fill drawn
    over the strip of pane border the tab meets.

    The failure this exists to catch is the cover being a pixel out. It is not
    a colour bug and a screenshot cannot answer it: the cover and the tab are
    the same colour by construction, so a capture shows one continuous surface
    whether the spans line up or not, and what a misalignment leaves behind is
    a single pixel of the pane's stroke standing at the tab's corner -- under
    Mica, indistinguishable from noise. So this reads ARRANGED GEOMETRY back
    over the seam instead, converts to device pixels with the window's own
    scale, and compares the two spans that must be one.

    Swept over several window widths because the reported risk is specifically
    a misalignment that appears on resize: an equal-width strip divides the
    window by the tab count, so most widths put every tab edge on a fraction
    of a DIP, and whether the cover and the tab round the same way is the
    whole question.

    Zero synthesized OS input: every state change goes through the test seam,
    and the only window the harness touches is the one it launched.

.NOTES
    Exit 0 clean, 2 product findings, 1 the harness could not run.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)

. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

trap {
    if ("$_" -like 'PRODUCT_FAIL*') { Write-Host "$_"; exit 2 }
    break
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

# The gutter every leaf keeps clear for its chrome, which is the band the
# stroke is drawn in and therefore exactly what the cover has to fill.
# Ghostty.Core.Panes.PaneChrome.SurfaceInset, in DIPs.
$SurfaceInset = 2.0

# MainWindow.VerticalSeamOverlap: how far the vertical cover starts back
# inside the row, so nothing of the strip shows between the row's edge and
# the pane border.
$VerticalOverlap = 4.0

# VerticalTabStrip.RowInsetVertical: the selection row is a Border on its own
# canvas, inset from the NavigationViewItem whose slot it marks. layout-frame
# reports the ITEM, so this is the offset between the two.
$RowInsetVertical = 2.0

# The selection row's own top and bottom strokes, which the cover stays inside
# so they still close onto the pane border the way a tab's corners do.
$RowEdgeStroke = 1.0

# So the cover's top is this far below the item's, and its bottom this far
# above the item's. Two constants rather than one because they come from two
# different decisions and a change to either is a different bug.
$RowToCover = $RowInsetVertical + $RowEdgeStroke

$Widths = @(760, 900, 1024, 1103, 1280, 1441)
# A subset of the above: the vertical leg costs three window states per width
# (compact, wide, and back), so it takes the three that already differ most in
# how they land the strip's right edge rather than repeating all six.
$VerticalWidths = @(900, 1103, 1280)

$script:Findings = [System.Collections.Generic.List[string]]::new()
$script:Rows = [System.Collections.Generic.List[object]]::new()

$Config = @'
windows-single-instance = true
window-save-state = never
window-theme = wintty
theme = Catppuccin Mocha
'@

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'

# One comparison: two DIP edges that must land on the SAME device pixel.
#
# Not "within one pixel". The defect this exists to catch IS one pixel -- a
# single column of the pane's stroke left standing at the tab's corner -- so a
# budget of one admits it. An earlier version had exactly that budget, and a
# deliberate one-DIP shift of the cover moved the reported gap from 0 to 1 and
# still passed it.
#
# The raw sub-pixel distance is recorded alongside rather than judged on,
# because at a fractional scale two edges can round together while drifting,
# and a number creeping toward half a pixel is worth seeing before it crosses.
function Add-Measure {
    param(
        [string]$Leg, [string]$Check,
        [double]$Left, [double]$Right, [double]$Scale, [string]$What)

    $leftPx = $Left * $Scale
    $rightPx = $Right * $Scale
    $delta = [Math]::Round([Math]::Abs($leftPx - $rightPx), 3)
    $aPixel = [int][Math]::Round($leftPx, [MidpointRounding]::AwayFromZero)
    $bPixel = [int][Math]::Round($rightPx, [MidpointRounding]::AwayFromZero)
    $pass = $aPixel -eq $bPixel

    $script:Rows.Add([ordered]@{
        leg = $Leg; check = $Check; what = $What
        aDip = [Math]::Round($Left, 2); bDip = [Math]::Round($Right, 2)
        aPixel = $aPixel; bPixel = $bPixel; subPixel = $delta; pass = $pass
    })

    if (-not $pass) {
        $script:Findings.Add(
            ("$Leg/$Check`: $What land on different device pixels, " +
             "$aPixel and $bPixel ($delta px apart); " +
             "$([Math]::Round($Left,2)) vs $([Math]::Round($Right,2)) DIP at scale $Scale"))
    }
}

function Get-Frame {
    param($Session)
    # layout-frame is the one op that skips the settle pass, so ask for a
    # settling op first and refuse any frame taken mid-switch: a strip that
    # is still flying has no seam to be right or wrong about.
    $state = Invoke-SeamCommand $Session @{ op = 'get-state' }
    if ($state.state.switching) {
        throw 'HARVEST_MISS: the window was still mid-layout-switch when the frame was asked for'
    }
    return Invoke-SeamCommand $Session @{ op = 'layout-frame' }
}

function Measure-Horizontal {
    param($Frame, [string]$Leg)

    $scale = [double]$Frame.state.panes.scale
    $host_ = $Frame.render.horizontal
    $seam = $Frame.render.seamHorizontal
    $active = @($host_.rows | Where-Object { $_.active -and $_.kind -ne 'chip' })

    if ($active.Count -ne 1) {
        $script:Findings.Add(
            "$Leg/active-row: the horizontal strip reports $($active.Count) active non-chip rows, so there is no one span the cover can be checked against")
        return
    }
    $row = $active[0]

    if (-not $seam.shown) {
        $script:Findings.Add(
            "$Leg/shown: the horizontal seam cover is not shown while tab '$($row.label)' is active, so the pane's top border runs straight through the active tab")
        return
    }

    Add-Measure $Leg 'left'  $row.x ($seam.x) $scale 'the active tab''s left edge and the cover''s left edge'
    Add-Measure $Leg 'right' ($row.x + $row.w) ($seam.x + $seam.w) $scale 'the active tab''s right edge and the cover''s right edge'

    # No band left uncovered between the strip's bottom and the top of the
    # cover: the cover starts at the pane row's own top, and the strip ends
    # where that row begins.
    Add-Measure $Leg 'meet' ($host_.hy + $host_.hh) $seam.y $scale 'the strip''s bottom edge and the cover''s top edge'

    # Deep enough to bury the stroke, and no deeper: past the gutter are live
    # terminal cells, and this fill is the tab's.
    if ([Math]::Abs($seam.h - $SurfaceInset) -gt 0.51) {
        $script:Findings.Add(
            "$Leg/depth: the horizontal cover is $($seam.h) DIP deep; the pane gutter it must fill exactly is $SurfaceInset")
    }
}

function Measure-Vertical {
    param($Frame, [string]$Leg)

    $scale = [double]$Frame.state.panes.scale
    $seam = $Frame.render.seamVertical
    $active = @($Frame.render.vertical.rows | Where-Object { $_.active -and $_.kind -ne 'header' })

    if ($active.Count -ne 1) {
        $script:Findings.Add(
            "$Leg/active-row: the vertical strip reports $($active.Count) active rows, so there is no one span the cover can be checked against")
        return
    }
    $row = $active[0]

    if (-not $seam.shown) {
        $script:Findings.Add(
            "$Leg/shown: the vertical seam cover is not shown while tab '$($row.label)' is active, so the pane's left border runs straight through the active row")
        return
    }

    # The cover stays inside the row's own top and bottom strokes, so those
    # still close onto the pane border the way a horizontal tab's corners do.
    Add-Measure $Leg 'top'    ($row.y + $RowToCover) $seam.y $scale 'the selection row''s inner top edge and the cover''s top edge'
    Add-Measure $Leg 'bottom' ($row.y + $row.h - $RowToCover) ($seam.y + $seam.h) $scale 'the selection row''s inner bottom edge and the cover''s bottom edge'

    # Starts back inside the row -- which is already the terminal colour, so
    # the overlap costs nothing -- and stops exactly at the far side of the
    # gutter. Erring narrow leaves a line; erring wide paints the row's fill
    # over the first column of cells.
    Add-Measure $Leg 'start' ($row.x + $row.w - $VerticalOverlap) $seam.x $scale 'the row''s right edge less the overlap and the cover''s left edge'
    Add-Measure $Leg 'reach' ($row.x + $row.w + $SurfaceInset) ($seam.x + $seam.w) $scale 'the far side of the pane gutter and the cover''s right edge'
}

# Every width this harness actually achieved, as read back off the window.
# The whole point of the sweep is that six widths put the tab edges on six
# different fractions of a DIP; if the window did not really resize, all 126
# comparisons are of ONE geometry measured six times, and they agree for the
# most boring possible reason while the summary claims a sweep.
$script:AchievedWidths = [System.Collections.Generic.List[int]]::new()

# MoveWindow reports failure only through its return, and leaves the window
# where it was. Refused or clamped -- a maximized or snapped window, a width
# under the window's own minimum -- is indistinguishable from success unless
# the geometry is read back, so it is.
function Set-WindowWidth([IntPtr]$Hwnd, [int64]$Hwnd64, [int]$Width) {
    if (-not [SeamWin]::MoveWindow($Hwnd, 60, 60, $Width, 820, $true)) {
        throw "HARNESS: MoveWindow refused a resize to ${Width}x820"
    }
    # MoveWindow does not go through the seam, so nothing acked the new size;
    # the next command's own settle pass is what does.
    Start-Sleep -Milliseconds 350

    $rc = [SeamWin]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw "HARNESS: the window's rect could not be read back after a resize to $Width" }
    # A couple of pixels of slack for the frame the shell owns; a clamp is
    # tens of pixels or more, never this.
    if ([Math]::Abs($rc.W - $Width) -gt 4) {
        throw "HARNESS: asked for width $Width and the window came back $($rc.W); a clamped window measures one geometry repeatedly"
    }
    $script:AchievedWidths.Add($rc.W)
}

if (-not (Test-Path $ExePath)) {
    Write-Host "HARVEST_MISS: missing exe: $ExePath"
    exit 1
}

$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }
$session = $null
$harnessError = ''
try {
    Assert-NoWintty -Context 'The tab field seam harness'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config

    $names = @('alpha', 'bravo', 'charlie', 'delta', 'echo')
    [void](Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = 5; titles = $names })

    $hwnd = [SeamWin]::P([int64]$session.Hwnd64)

    foreach ($w in $Widths) {
        Set-WindowWidth $hwnd $session.Hwnd64 $w

        # First, middle and last, because an equal-width strip puts every tab
        # edge on its own fraction of a DIP and the ends are where a cover
        # clipped to the scrolling viewport would show it.
        foreach ($i in @(0, 2, 4)) {
            [void](Invoke-SeamCommand $session @{ op = 'select'; index = $i })
            # Past the field settle (167ms), so the measurement is of the
            # geometry at rest rather than of a frame mid-transition.
            Start-Sleep -Milliseconds 260
            Measure-Horizontal (Get-Frame $session) "h-$w-tab$i"
        }
    }

    [void](Invoke-SeamCommand $session @{ op = 'toggle-layout' })
    Start-Sleep -Milliseconds 500

    foreach ($w in $VerticalWidths) {
        Set-WindowWidth $hwnd $session.Hwnd64 $w
        foreach ($pane in @('compact', 'wide')) {
            foreach ($i in @(0, 2, 4)) {
                [void](Invoke-SeamCommand $session @{ op = 'select'; index = $i })
                Start-Sleep -Milliseconds 260
                Measure-Vertical (Get-Frame $session) "v-$w-$pane-tab$i"
            }
            if ($pane -eq 'compact') {
                [void](Invoke-SeamCommand $session @{ op = 'toggle-sidebar' })
                Start-Sleep -Milliseconds 300
            }
        }
        # Back to compact for the next width, so each width starts the same.
        [void](Invoke-SeamCommand $session @{ op = 'toggle-sidebar' })
        Start-Sleep -Milliseconds 300
    }

    if ($session.Proc.HasExited) {
        throw "APP_EXIT: the app exited during the run (code $($session.Proc.ExitCode))"
    }
}
catch {
    $msg = "$($_.Exception.Message)"
    if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') {
        $script:Findings.Add($msg)
    } else {
        $harnessError = $msg
    }
    Write-Host "ERROR: $msg" -ForegroundColor Red
}
finally {
    if ($null -ne $session) { Stop-SeamSession $session }
}

if ((Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)) {
    $script:Findings.Add('crash.log grew during the run')
}

$distinctWidths = @($script:AchievedWidths | Sort-Object -Unique)

@{
    measures = $script:Rows
    findings = $script:Findings
    rule = 'both edges must round to the same device pixel'
    widths = $Widths
    achievedWidths = $distinctWidths
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutDir 'result.json')

$worst = 0.0
foreach ($r in $script:Rows) { if ($r.subPixel -gt $worst) { $worst = $r.subPixel } }
Write-Host ("$($script:Rows.Count) span comparisons over $($distinctWidths.Count) distinct window widths; " +
            "every pair must land on one device pixel, worst sub-pixel drift $worst")

# Nothing measured is not a pass. A run whose every leg bailed before it
# compared anything would otherwise print green.
if ($script:Rows.Count -eq 0 -and $script:Findings.Count -eq 0) {
    Write-Host 'HARVEST_MISS: no span was compared, so nothing here rules anything out'
    exit 1
}

# Neither is measuring one geometry many times. The claim this harness makes is
# about tab edges landing on DIFFERENT fractions of a device pixel, and that
# claim is only worth the widths it actually got: a window that ignored every
# resize would produce a full set of agreeing comparisons and a drift of zero.
$wantWidths = @($Widths + $VerticalWidths | Sort-Object -Unique).Count
if ($distinctWidths.Count -lt $wantWidths) {
    Write-Host ("HARVEST_MISS: asked for $wantWidths distinct widths and the window held " +
                "$($distinctWidths.Count) ($($distinctWidths -join ', ')); the sweep did not sweep")
    exit 1
}

if ($script:Findings.Count -gt 0) {
    Write-Host ''
    Write-Host "$($script:Findings.Count) finding(s):" -ForegroundColor Red
    foreach ($f in $script:Findings) { Write-Host "  $f" -ForegroundColor Red }
    exit 2
}
if ($harnessError) { exit 1 }

Write-Host ''
Write-Host 'the cover lands on the active tab''s own span in both layouts, at every width measured' -ForegroundColor Green
exit 0
