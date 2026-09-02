#requires -Version 7
<#
    The vertical strip's chrome geometry, seam-measured. The oracle is the
    strip's own arranged layout, read back over the seam's element-rects op
    (WINTTY_TEST_SEAM=<session token>) -- not sampled pixels. That is deliberate: the
    strip wears Mica, so what a screen grab shows depends on the desktop
    behind the window, and the questions asked here are about where things
    were laid out, which layout answers exactly.

    Two processes. The geometry legs run in one, over a single seeded
    state (two pins, one group, two loose tabs) measured at both pane
    widths: compact (the 48px rail the strip starts in) and expanded (the
    pinned sidebar, reached with toggle-sidebar). The band drag runs in a
    second, because it needs four pins and an expanded pane -- a column
    boundary inside the staged state rather than beyond it.

    Checks, each at both widths unless stated:

      band-squares          every pinned tab is arranged as the SAME
                            square, and the band's box contains them all.
                            The zone's division is structural now, so the
                            square is the division: a pin that arranged as
                            a row again would read as an ordinary row with
                            nothing marking the zone.
      band-wraps            two pins share a band row in the expanded pane
                            and stack in the 48px rail (expanded/compact
                            respectively). This is the shape's whole
                            claim -- pins cost band rows, not list rows.
      no-boundary-rule      the retired boundary stroke is not arranged.
                            A rule redrawn beside a structural division
                            states the division twice, which is what the
                            shape was chosen to stop.
      close-inset           the close glyph's right edge sits one named
                            inset in from the pane edge (expanded), and the
                            compact rail carries no close glyph at all --
                            MUXC's item template lays the row's content out
                            past the 48px rail, so a close button that
                            still existed there would be arranged outside
                            the pane.
      header-fits           a group header's painted span -- swatch through
                            chevron -- stays inside the pane at both
                            widths.

    And in the second process, expanded only, with four pins staged:

      band-reorder          dragging a pin left over its neighbour swaps
                            them. Impossible on the crossing engine
                            outright: squares sharing a band row share the
                            Y it compares, so no crossing between them can
                            ever be produced.
      band-drop-slot        a body tab dragged into the band lands at the
                            slot it was aimed at, and comes back PINNED.
                            Aimed at the last slot rather than the first,
                            because slot 0 is what the old engine produced
                            for every arrival and could not tell a working
                            drop from a broken one.
      band-drop-rect        ...and the square is arranged where the manager
                            says it is: on the last occupied band row,
                            right of every pin sharing it. The column count
                            is derived from the arranged rects, so the
                            check does not assume a four-column band.

    Findings are collected rather than thrown one at a time: a geometry run
    that reports the first bad number and stops hides the rest of the
    picture, and every check here is independent.

    Exits 0 when every check holds, 2 on a product finding (a number
    outside tolerance, the app dying, crash.log growth), 1 when the harness
    could not run and nothing is known about the product.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots') | Out-Null
Add-Type -AssemblyName System.Drawing
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

# Half a pixel: the strip lays out on whole pixels at 100% scaling, so
# anything looser would accept a one-pixel drift as centred.
$Tolerance = 0.5

# The gap the close glyph keeps from the pane's right edge. The selected
# row's fill runs all the way to that edge, so this reads as padding inside
# the fill rather than as a second inset.
$CloseInsetRight = 8

# The pinned square's edge, from Ghostty.Core.Tabs.TabPinBand.ChipSize.
# Repeated here rather than read out of the seam on purpose: a harness that
# asks the product what the product should be measures nothing.
$ChipSize = 40

# A stock strip, not the developer's: the seam session stages this as the
# whole of XDG_CONFIG_HOME, so nothing from the machine's own config
# reaches the window under test.
$Config = @'
windows-single-instance = true
window-save-state = never
vertical-tabs = true
vertical-tabs-pinned = false
vertical-tabs-hover-expand = false
window-theme = wintty
theme = Catppuccin Mocha
'@

$names = @('geom-1', 'geom-2', 'geom-3', 'geom-4', 'geom-5')
$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$script:Findings = [System.Collections.Generic.List[object]]::new()
$script:Checks = [System.Collections.Generic.List[object]]::new()

function Add-Check([string]$Name, [string]$Detail, [bool]$Ok) {
    $script:Checks.Add([ordered]@{ name = $Name; detail = $Detail; ok = $Ok })
    if ($Ok) {
        Write-Host ("  PASS {0,-28} {1}" -f $Name, $Detail) -ForegroundColor Green
    } else {
        Write-Host ("  FAIL {0,-28} {1}" -f $Name, $Detail) -ForegroundColor Red
        $script:Findings.Add("$Name : $Detail")
    }
}

function Assert-Rect($Rect, [string]$What) {
    if (-not $Rect.visible) { throw "HARVEST_MISS: $What has no arranged box" }
    return $Rect
}

function Right($Rect) { return $Rect.x + $Rect.w }
function CenterX($Rect) { return $Rect.x + $Rect.w / 2 }

function Save-StripShot([int64]$Hwnd64, [string]$Name) {
    $rc = [SeamWin]::RectOf($Hwnd64)
    if ($null -eq $rc) { return }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir "shots\$Name.png"))
    $g.Dispose(); $bmp.Dispose()
}

# ---- the checks ------------------------------------------------------------

# The band's own drag, which the linear crossing engine cannot do.
#
# Five pins in the expanded pane, so the band is two rows of four and one:
# a column boundary is inside the staged state rather than beyond it, and a
# reorder that only works while every pin shares one row would show here.
#
# Three claims, each read back off the MANAGER (the seam's state block)
# rather than off the gesture's own report:
#
#   band-reorder     dragging a pin left over its neighbour swaps them. On
#                    the crossing engine this was impossible outright:
#                    squares sharing a band row share the Y it compares, so
#                    no crossing between them can ever be produced.
#   band-drop-slot   dragging a body tab into the band lands it at the slot
#                    it was walked to, NOT at slot 0. The drain-the-crossings
#                    loop committed 2->1, re-evaluated against the identical
#                    centre, committed 1->0, and put every arriving tab at
#                    the front of the band whatever the pointer said.
#   band-drop-rect   ...and the square is ARRANGED there, asked of
#                    element-rects rather than trusted from the model. A
#                    model that moved while the panel drew the old order is
#                    the failure this file exists to catch.
function Test-BandDrag($Session) {
    # Its OWN session, staged from launch rather than re-seeded into the
    # one the geometry checks used. Re-seeding a live session -- tearing
    # five tabs down and rebuilding them with pins already in an expanded
    # vertical pane -- took the app down twice with exit 2173 and no
    # crash.log entry, which is the layout-switch crash family
    # seam-acceptance.ps1 exists to stage. Not this harness's question, and
    # not a shape it should be provoking on the way to asking its own.
    foreach ($i in 0, 1, 2, 3) {
        [void](Invoke-SeamCommand $Session @{ op = 'pin'; index = $i; via = 'router' })
    }
    # Four pinned, one loose. Selection off the band so the selection
    # chrome is not sitting on a square being dragged.
    [void](Invoke-SeamCommand $Session @{ op = 'select'; index = 4 })
    [void](Invoke-SeamCommand $Session @{ op = 'toggle-sidebar' })

    $before = Invoke-SeamCommand $Session @{ op = 'get-state' }
    $order = @($before.state.tabs | ForEach-Object { $_.title })
    if ($order.Count -lt 5) { throw "HARVEST_MISS: the drag leg seeded $($order.Count) tabs" }
    if ($before.state.paneWidth -le 96) {
        throw "HARVEST_MISS: the drag leg's pane is $($before.state.paneWidth)px, wanted the expanded sidebar"
    }

    # Reorder INSIDE a band row: the third pin onto the second's slot.
    # The drag op answers with `order` and `landed`, not a state block.
    $moved = Invoke-SeamCommand $Session @{ op = 'drag'; from = 2; to = 1 }
    $after = @($moved.order)
    $want = @($order[0], $order[2], $order[1], $order[3], $order[4])
    Add-Check 'band-reorder' (
        'dragged pin 2 onto slot 1: [{0}], wanted [{1}]' -f
            ($after -join ','), ($want -join ',')) (($after -join ',') -eq ($want -join ','))

    # A body tab into the band, aimed at the LAST slot rather than the
    # first. Slot 0 is what the old engine produced for every arrival, so
    # a target of 0 here could not tell a working drop from a broken one.
    $arrived = Invoke-SeamCommand $Session @{ op = 'drag'; from = 4; to = 3 }
    $landed = @($arrived.order)
    $ok = $landed.Count -ge 4 -and $landed[3] -eq $order[4] `
        -and [int]$arrived.landed -eq 3 -and [bool]$arrived.pinned
    Add-Check 'band-drop-slot' (
        "'{0}' landed at {1} pinned={2}; order [{3}]" -f
            $order[4], $arrived.landed, [bool]$arrived.pinned, ($landed -join ',')) $ok

    # ...and the square is arranged where the manager says it is.
    $rects = Invoke-SeamCommand $Session @{ op = 'element-rects' }
    $pins = @($rects.rects.pinned)
    $target = @($pins | Where-Object { $_.title -eq $order[4] })
    if ($target.Count -ne 1) {
        Add-Check 'band-drop-rect' (
            "the band arranges {0} squares titled '{1}'" -f $target.Count, $order[4]) $false
    } elseif (-not $target[0].row.visible) {
        # A FINDING, not a harness miss. Assert-Rect throws HARVEST_MISS
        # here, which the catch below turns into exit 1 -- "nothing is known
        # about the product" -- but a newly pinned square with no arranged
        # box is precisely the defect this check exists to name: a model
        # that moved while the panel drew the old order. Exit 1 would let a
        # gate keyed on findings read the headline bug as infrastructure
        # noise.
        Add-Check 'band-drop-rect' (
            "the newly pinned square '{0}' is not arranged at all" -f $order[4]) $false
    } else {
        # READING ORDER is the claim, and it needs no column count at all.
        # The band lays its slots out left to right, wrapping, so sorting
        # the arranged squares by row then column must reproduce the
        # manager's pinned prefix exactly -- and the dropped tab landed at
        # manager index 3, so it must be the fourth square read that way.
        #
        # Deriving a column count and predicting a row/column was the
        # obvious alternative and it is a trap twice over: assuming four
        # columns is a false finding as soon as the pane fits five, and
        # "the last occupied row" is simply wrong here, because slot 3 of
        # five in a four-column band is row 0, not the row holding the
        # fifth pin. Reading order sidesteps the arithmetic and asserts the
        # thing the check is actually named for.
        $r = $target[0].row
        $reading = @($pins | Sort-Object `
            @{ Expression = { [Math]::Round($_.row.y) } }, `
            @{ Expression = { $_.row.x } })
        $at = [Array]::IndexOf(@($reading | ForEach-Object { $_.title }), $order[4])
        $ok = $at -eq 3
        Add-Check 'band-drop-rect' (
            "'{0}' is square {1} in reading order (want 3); at ({2:F1},{3:F1}); band reads [{4}]" -f
                $order[4], $at, $r.x, $r.y,
                (($reading | ForEach-Object { $_.title }) -join ',')) $ok
    }
}

# A pinned tab is an icon square, and that change of shape is what marks
# the zone now that nothing is drawn between the zones. So the square is
# the thing to measure: every pin the same size, and all of them inside the
# band's own box, which is the element whose bottom edge IS the zone's end.
function Test-BandSquares($Rects, [string]$Leg) {
    $band = Assert-Rect $Rects.band "the pinned band ($Leg)"
    $pins = @($Rects.pinned)
    if ($pins.Count -eq 0) { throw "HARVEST_MISS: no pinned row in the $Leg leg" }

    $worst = 0.0
    $detail = ''
    foreach ($pin in $pins) {
        $r = Assert-Rect $pin.row "the pinned square '$($pin.title)' ($Leg)"
        # Off-square in either dimension, and outside the band in either
        # direction, all fold into one worst-case number: the check is
        # "this is a square in the band", and any of them failing is it.
        $offs = @(
            [math]::Abs($r.w - $ChipSize)
            [math]::Abs($r.h - $ChipSize)
            [math]::Max(0, $band.x - $r.x)
            [math]::Max(0, $band.y - $r.y)
            [math]::Max(0, (Right $r) - (Right $band))
            [math]::Max(0, ($r.y + $r.h) - ($band.y + $band.h))
        )
        $off = ($offs | Measure-Object -Maximum).Maximum
        if ($off -ge $worst) {
            $worst = $off
            $detail = "'{0}' {1:F1}x{2:F1} at ({3:F1},{4:F1}); band {5:F1}x{6:F1} at ({7:F1},{8:F1}); worst off {9:F1}px (want {10}px squares inside the band)" -f
                $pin.title, $r.w, $r.h, $r.x, $r.y, $band.w, $band.h, $band.x, $band.y, $off, $ChipSize
        }
    }
    Add-Check "band-squares-$Leg" $detail ($worst -le $Tolerance)
}

# The shape's whole claim: pins cost BAND rows. Two pins share a row in the
# expanded pane (220px fits four columns) and stack in the 48px rail (one
# column). Same-row is a shared y; stacked is a y one square-pitch apart --
# read as "different y", because the pitch itself is TabPinBand's business
# and Test-BandSquares already pins the size.
function Test-BandWraps($Rects, [string]$Leg, [bool]$SameRow) {
    $pins = @($Rects.pinned)
    if ($pins.Count -lt 2) { throw "HARVEST_MISS: the $Leg leg needs two pins to show wrapping" }
    $a = Assert-Rect $pins[0].row "the first pinned square ($Leg)"
    $b = Assert-Rect $pins[1].row "the second pinned square ($Leg)"
    $shares = [math]::Abs($a.y - $b.y) -le $Tolerance
    $beside = $b.x -gt $a.x + $Tolerance
    $ok = if ($SameRow) { $shares -and $beside } else { -not $shares }
    Add-Check "band-wraps-$Leg" (
        "pins at ({0:F1},{1:F1}) and ({2:F1},{3:F1}); want {4}" -f
        $a.x, $a.y, $b.x, $b.y, $(if ($SameRow) { 'one band row' } else { 'stacked' })
    ) $ok
}

# The retired stroke stays retired. The seam reports an element it cannot
# measure as visible:$false, and it no longer writes a 'boundary' key at
# all -- so both "the key came back" and "the key came back arranged" are
# a finding, and only a genuinely absent rule passes.
function Test-NoBoundaryRule($Rects, [string]$Leg) {
    $drawn = ($null -ne $Rects.boundary) -and $Rects.boundary.visible
    $detail = if ($null -eq $Rects.boundary) {
        'no boundary rect is reported'
    } else {
        "a boundary rect is reported, visible=$($Rects.boundary.visible)"
    }
    Add-Check "no-boundary-rule-$Leg" $detail (-not $drawn)
}

# Expanded: the close glyph's right edge is one named inset in from the
# pane edge, and every body row agrees -- a grouped row is indented on the
# left and must not pay for that on the right.
function Test-CloseInsetExpanded($Rects) {
    $pane = Assert-Rect $Rects.pane 'the pane (expanded)'
    $worst = $null
    $detail = ''
    foreach ($row in $Rects.rows) {
        if (-not $row.close.visible) {
            Add-Check 'close-inset-expanded' "row '$($row.title)' has no close glyph" $false
            return
        }
        $gap = (Right $pane) - (Right $row.close)
        if ($null -eq $worst -or [math]::Abs($gap - $CloseInsetRight) -gt [math]::Abs($worst - $CloseInsetRight)) {
            $worst = $gap
            $detail = "row '$($row.title)' close ends at {0:F1}, pane at {1:F1}, gap {2:F1}px (want {3})" -f
                (Right $row.close), (Right $pane), $gap, $CloseInsetRight
        }
    }
    if ($null -eq $worst) { throw 'HARVEST_MISS: no body rows in the expanded leg' }
    Add-Check 'close-inset-expanded' $detail ([math]::Abs($worst - $CloseInsetRight) -le $Tolerance)
}

# Compact: the 48px rail is icon-only, and MUXC's item template puts the
# row's content past the rail's right edge there, so a close glyph that
# still existed would be arranged outside the pane it belongs to.
function Test-NoCloseWhenCompact($Rects) {
    $pane = Assert-Rect $Rects.pane 'the pane (compact)'
    foreach ($row in $Rects.rows) {
        if (-not $row.close.visible) { continue }
        Add-Check 'close-hidden-compact' (
            "row '{0}' close is laid out at {1:F1}..{2:F1}, past the pane edge {3:F1}" -f
            $row.title, $row.close.x, (Right $row.close), (Right $pane)
        ) ((Right $row.close) -le (Right $pane) + $Tolerance)
        return
    }
    Add-Check 'close-hidden-compact' 'no body row carries a close glyph' $true
}

# A group header paints from its color swatch to its chevron; both ends
# must sit inside the pane, or the header is clipped by the rail it lives
# in.
function Test-HeaderFits($Rects, [string]$Leg) {
    $pane = Assert-Rect $Rects.pane "the pane ($Leg)"
    if (@($Rects.headers).Count -eq 0) { throw "HARVEST_MISS: no group header in the $Leg leg" }
    $header = @($Rects.headers)[0]
    $swatch = Assert-Rect $header.swatch "the header swatch ($Leg)"
    $chevron = Assert-Rect $header.chevron "the header chevron ($Leg)"
    $overflow = (Right $chevron) - (Right $pane)
    $underflow = $pane.x - $swatch.x
    Add-Check "header-fits-$Leg" (
        "swatch starts {0:F1}, chevron ends {1:F1}, pane {2:F1}..{3:F1} (overflow {4:F1}px)" -f
        $swatch.x, (Right $chevron), $pane.x, (Right $pane), $overflow
    ) ($overflow -le $Tolerance -and $underflow -le $Tolerance)
}

# ---- the run ---------------------------------------------------------------

if (-not (Test-Path $ExePath)) {
    Write-Host "HARVEST_MISS: missing exe: $ExePath"
    exit 1
}

$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }
$session = $null
$harnessError = ''
try {
    Assert-NoWintty -Context 'The vertical strip geometry harness'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config

    # TWO pins, because one cannot show a band: whether pins share a row
    # or stack is the shape's claim, and a single square satisfies either
    # reading. One group so a header renders, and a loose row left over so
    # the body list is not degenerate.
    [void](Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = 5; titles = $names })
    [void](Invoke-SeamCommand $session @{ op = 'pin'; index = 0; via = 'router' })
    [void](Invoke-SeamCommand $session @{ op = 'pin'; index = 1; via = 'router' })
    [void](Invoke-SeamCommand $session @{ op = 'group'; indices = @(3, 4) })
    # Off the group AND off the band, so nothing folds, every row stays
    # measurable, and the selection chrome is not sitting on a square.
    [void](Invoke-SeamCommand $session @{ op = 'select'; index = 2 })

    $compact = Invoke-SeamCommand $session @{ op = 'element-rects' }
    if ($compact.state.paneWidth -ge 96) {
        throw "HARVEST_MISS: the strip started at $($compact.state.paneWidth)px, expected the compact rail"
    }
    Save-StripShot $session.Hwnd64 'compact'

    [void](Invoke-SeamCommand $session @{ op = 'toggle-sidebar' })
    $expanded = Invoke-SeamCommand $session @{ op = 'element-rects' }
    if ($expanded.state.paneWidth -le $compact.state.paneWidth) {
        throw "HARVEST_MISS: toggle-sidebar left the pane at $($expanded.state.paneWidth)px"
    }
    Save-StripShot $session.Hwnd64 'expanded'

    @{ compact = $compact; expanded = $expanded } |
        ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutDir 'rects.json') -Encoding utf8

    Write-Host ''
    Write-Host "=== compact (pane $($compact.state.paneWidth)px) ==="
    Test-BandSquares $compact.rects 'compact'
    Test-BandWraps $compact.rects 'compact' $false
    Test-NoBoundaryRule $compact.rects 'compact'
    Test-NoCloseWhenCompact $compact.rects
    Test-HeaderFits $compact.rects 'compact'

    Write-Host ''
    Write-Host "=== expanded (pane $($expanded.state.paneWidth)px) ==="
    Test-BandSquares $expanded.rects 'expanded'
    Test-BandWraps $expanded.rects 'expanded' $true
    Test-NoBoundaryRule $expanded.rects 'expanded'
    Test-CloseInsetExpanded $expanded.rects
    Test-HeaderFits $expanded.rects 'expanded'

    if ($session.Proc.HasExited) {
        throw "APP_EXIT: the app exited during the run (code $($session.Proc.ExitCode))"
    }

    # The band's own drag, in its own process. See Test-BandDrag.
    Stop-SeamSession $session
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config
    [void](Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = 5; titles = $names })
    Write-Host ''
    Write-Host '=== band drag (four pins, expanded) ==='
    Test-BandDrag $session
    Save-StripShot $session.Hwnd64 'band-drag'

    if ($session.Proc.HasExited) {
        throw "APP_EXIT: the app exited during the drag leg (code $($session.Proc.ExitCode))"
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

$result = [ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=<session token>); geometry read from arranged layout, no pixels'
    tolerance = $Tolerance
    checks    = $script:Checks
    findings  = $script:Findings
    harness   = $harnessError
}
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

Write-Host ''
Write-Host 'check                          verdict'
Write-Host '-----------------------------  -------'
foreach ($check in $script:Checks) {
    Write-Host ("{0,-30} {1}" -f $check.name, $(if ($check.ok) { 'PASS' } else { 'FAIL' }))
}

if ($script:Findings.Count -gt 0) {
    Write-Host ''
    Write-Host "$($script:Findings.Count) finding(s):" -ForegroundColor Red
    foreach ($f in $script:Findings) { Write-Host "  $f" -ForegroundColor Red }
    exit 2
}
if ($harnessError) { exit 1 }
Write-Host ''
Write-Host 'all geometry checks hold at both pane widths' -ForegroundColor Green
exit 0
