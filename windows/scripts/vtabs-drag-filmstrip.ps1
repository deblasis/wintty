#requires -Version 7
<#
    A per-frame film of a live vertical drag, judged in pixels.

    The spec's oracle for the drag motion is visual: when a crossing
    commits, the gap must open within 2 frames, and the displaced rows'
    offsets must converge within 500ms. A UIA read cannot see either --
    the accessibility tree reports the settled layout, never the glide --
    so this harness films the strip while a drag crosses one neighbour,
    and measures the frames.

    Actuation is the in-process test seam's PACED drag (drag-paced): the
    strip's own drag handlers walked in fine steps on a wall clock, so the
    capture has frames between the moves and zero OS input is synthesized.
    The seam's response timestamps the commit and the release on the
    gesture's own clock; the harness stamps the moment it sent the command
    on the same stopwatch the frames ride, and the crossing time is the
    send stamp plus the reported commit offset. The send stamp can only be
    EARLY relative to the gesture's true start (pipe and marshal latency
    land after it), so a measured gap can only read worse than reality,
    never better -- the same conservative polarity the SendInput schedule
    had.

    The tracked pixel is the SELECTED row's fill, and the selected row is
    the DISPLACED one, not the dragged one: the dragged row's chrome dims
    while the gesture holds it, and unselected rows sit nearly transparent
    on the strip background and cannot be told apart in pixels. Row 3 is
    selected through the seam, row 2 is dragged down past it, and row 3's
    band slides up one slot when the crossing commits; the band's Y over
    time IS the offset animation the oracle is about:

      - gap open: the first frame after the commit whose band top has
        risen at least 5px, minus the commit time, must be within 2
        frames.
      - convergence: the band must be within 2px of its final position
        for 6 consecutive frames within 500ms of the commit.
      - travel: the band must end at least 60% of a row height above
        where it started, so a no-op drag cannot pass.
      - and the layout must really have swapped: the final UIA order is
        read back and asserted.

    Calibration comes from frame 0, not from hard-coded colours: the band
    reference is sampled inside row 3's own rect, and if frame 0 does not
    show the band where row 3 says it is, the harness refuses rather than
    track a guess.

    This harness needs a machine whose client-area animations are ON: with
    them off the product cuts every glide, the offsets converge in one
    frame, and the timings above measure nothing. That is exit 1, not a
    product finding. The window must also stay visible on screen while the
    film runs -- the capture is CopyFromScreen -- which is the one way this
    harness still borrows the desktop.

    Exits 0 on pass, 2 on a product finding, 1 when the harness could not
    run -- a calibration that found no band, animations disabled on this
    machine.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$IntervalMs = 60,
    [int]$MaxFrames = 52,
    # Wall-clock pacing per 4px walker tick; 70ms spreads one row pitch
    # over ~10 frames at the default interval.
    [int]$TickMs = 70
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'frames'), (Join-Path $OutDir 'shots') | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Mixed-DPI discipline: rects and CopyFromScreen must share one space
# (-4 = PER_MONITOR_AWARE_V2).
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$UIA = [System.Windows.Automation.AutomationElement]
$TREE = [System.Windows.Automation.TreeScope]::Descendants
$CTRL = [System.Windows.Automation.ControlType]

function Get-UiaRoot([int64]$Hwnd64) { return $UIA::FromHandle([SeamWin]::P($Hwnd64)) }

function Find-ById($root, [string]$Id) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $Id)
    return $root.FindFirst($TREE, $cond)
}

# The vertical strip's rows in paint order, area-gated like the drag
# harness's reader.
function Get-StripRows {
    $root = Get-UiaRoot $script:MainHwnd64
    $stripEl = Find-ById $root 'NavView'
    if ($null -eq $stripEl) { throw 'HARVEST_MISS: no strip with AutomationId NavView' }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $CTRL::ListItem)
    $found = $stripEl.FindAll($TREE, $cond)
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($el in $found) {
        $r = $el.Current.BoundingRectangle
        if ($r.Width -le 0 -or $r.Height -le 0) { continue }
        $rows.Add([pscustomobject]@{ Name = $el.Current.Name; Rect = $r })
    }
    if ($rows.Count -eq 0) { throw 'HARVEST_MISS: no rows under NavView' }
    return @($rows | Sort-Object { $_.Rect.Y })
}

# ---- the pixel oracle -------------------------------------------------------

function Get-Pixels([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    return @{ bytes = $bytes; stride = $data.Stride; w = $w; h = $h }
}

function Get-Pixel([hashtable]$Px, [int]$X, [int]$Y) {
    $o = $Y * $Px.stride + $X * 4
    return @($Px.bytes[$o + 2], $Px.bytes[$o + 1], $Px.bytes[$o])
}

# Topmost y in the crop whose pixel is within $Tol of $Ref on every
# channel, over the column band [x - HalfW, x + HalfW] when $X is given,
# the full width otherwise, and only between $From and $To when $To is
# given. -1 when there is none. Column-scoped is what the band tracking
# wants: the calibrated colour is only known discriminating at the column
# it was sampled at, and a full-width scan happily matches ink or chrome
# on other rows first. The $To ceiling is the other half: the tracked
# band physically cannot leave the measured span, so rows outside it
# (another row's title ink) must not be readable at all.
function Find-BandTop([hashtable]$Px, [array]$Ref, [int]$Tol, [int]$From = 0, [int]$X = -1, [int]$HalfW = -1, [int]$To = -1) {
    $bytes = $Px.bytes
    $stride = $Px.stride
    $x0 = if ($X -ge 0 -and $HalfW -ge 0) { [Math]::Max(0, $X - $HalfW) } else { 0 }
    $x1 = if ($X -ge 0 -and $HalfW -ge 0) { [Math]::Min($Px.w - 1, $X + $HalfW) } else { $Px.w - 1 }
    $yMax = if ($To -ge 0) { [Math]::Min($Px.h - 1, $To) } else { $Px.h - 1 }
    for ($y = $From; $y -le $yMax; $y++) {
        $rowOff = $y * $stride
        for ($x = $x0; $x -le $x1; $x++) {
            $o = $rowOff + $x * 4
            if ([math]::Abs($bytes[$o + 2] - $Ref[0]) -le $Tol -and
                [math]::Abs($bytes[$o + 1] - $Ref[1]) -le $Tol -and
                [math]::Abs($bytes[$o] - $Ref[2]) -le $Tol) {
                return $y
            }
        }
    }
    return -1
}

# ---- run -------------------------------------------------------------------

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$Config = @'
windows-single-instance = true
window-save-state = never
windows-settings-ui = true
vertical-tabs = true
window-theme = wintty
theme = Catppuccin Mocha
vertical-tabs-hover-expand = false
'@

$script:MainHwnd64 = 0
$session = $null
$script:FatalWasProduct = $null

# Above the try, so the refusal survives a finally that would otherwise bind
# a null stamp to a mandatory parameter.
Assert-NoWintty -Context 'The drag filmstrip'

# The machine's animation gate, read directly: this oracle measures the
# glide, and a machine running with animations off would make every timing
# verdict here describe a cut. Exit 1 - an environment the oracle is not
# for - not a product finding.
$SPI_GETCLIENTAREAANIMATION = [uint32]0x1042
if (-not ('WinttyDragFilm.Spi' -as [type])) {
    Add-Type -Namespace WinttyDragFilm -Name Spi -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)]
public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);
'@
}
[uint32]$anim = 0
[void][WinttyDragFilm.Spi]::SystemParametersInfo($SPI_GETCLIENTAREAANIMATION, 0, [ref]$anim, 0)
if ($anim -eq 0) {
    Write-Host 'HARVEST_MISS: client-area animations are off on this machine; the glide oracle measures nothing. Turn "animate controls and elements" back on to run this harness.'
    exit 1
}

$frames = [System.Collections.Generic.List[object]]::new()

try {
    if (-not (Test-Path $ExePath)) { throw "missing exe: $ExePath" }
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config
    $script:MainHwnd64 = $session.Hwnd64
    [void][SeamWin]::MoveWindow([SeamWin]::P($script:MainHwnd64), 60, 60, 1280, 820, $true)
    Start-Sleep -Milliseconds 600
    Write-Host "hwnd=$($script:MainHwnd64) pid=$($session.Proc.Id) interval=${IntervalMs}ms frames=$MaxFrames tick=${TickMs}ms"

    $names = @('fuzzfilm-1', 'fuzzfilm-2', 'fuzzfilm-3', 'fuzzfilm-4')
    $seeded = Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = 4; titles = $names }
    $gotTitles = @($seeded.state.tabs | ForEach-Object { $_.title })
    if (($gotTitles -join ',') -ne ($names -join ',')) {
        throw "PRODUCT_FAIL: seeded order is [$($gotTitles -join ',')], expected [$($names -join ',')]"
    }

    # The pane opens through the seam's own sidebar toggle: a compact
    # 48px pane films icon slots whose selection chrome cannot be told
    # from the background at the sample column.
    if ($seeded.state.paneWidth -lt 200) {
        $widened = Invoke-SeamCommand $session @{ op = 'toggle-sidebar' }
        if ($widened.state.paneWidth -lt 200) {
            throw "HARVEST_MISS: the sidebar did not open (paneWidth $($widened.state.paneWidth))"
        }
    }
    $rows = Get-StripRows
    $gotNames = @($rows | ForEach-Object { $_.Name })
    if (($gotNames -join ',') -ne ($names -join ',')) {
        throw "PRODUCT_FAIL: UIA order is [$($gotNames -join ',')], expected [$($names -join ',')]"
    }

    # Row 3 is selected and row 2 does the dragging: the tracked band is
    # the DISPLACED selected row's fill, sliding up one slot when the
    # crossing commits -- the offset animation the oracle is about. The
    # dragged row itself is useless to track (its chrome dims while the
    # gesture holds it), and unselected rows cannot be told apart in
    # pixels. A seam press selects nothing, so dragging the unselected
    # neighbour no longer cancels the gesture the way a SendInput click
    # did. Selection goes through the seam -- the manager's own
    # activation, so the strip's selection sync paints the fill the
    # calibration samples.
    [void](Invoke-SeamCommand $session @{ op = 'select'; index = 2 })
    Start-Sleep -Milliseconds 500

    $rows = Get-StripRows
    $row2 = $rows | Where-Object { $_.Name -eq 'fuzzfilm-2' }
    $row3 = $rows | Where-Object { $_.Name -eq 'fuzzfilm-3' }
    $row4 = $rows | Where-Object { $_.Name -eq 'fuzzfilm-4' }
    if ($null -eq $row2 -or $null -eq $row3 -or $null -eq $row4) { throw 'HARVEST_MISS: expected rows not found before the drag' }

    $rowH = [int]((@($row2.Rect.Height, $row3.Rect.Height, $row4.Rect.Height) | Sort-Object)[1])
    $cropX = [int]$rows[0].Rect.X - 8
    $cropY = [int]$rows[0].Rect.Y - 12
    $cropW = [int]($rows[0].Rect.Width + 16)
    $cropH = [int]($row4.Rect.Y + $row4.Rect.Height - $cropY + 16)
    if ($rowH -lt 16 -or $cropW -lt 40) { throw 'HARVEST_MISS: strip geometry too small to film' }
    Write-Host "crop=${cropX},${cropY} ${cropW}x${cropH} rowH=$rowH"

    # Sampled right of centre: the title text ends well before that, and a
    # sample on the text ink would calibrate to the wrong colour.
    #
    # The selected row's chrome in the current shell is a FILL (a bright
    # band across the whole row), so its colour is sampled from the row's
    # own interior and required to differ per-channel from the unselected
    # row directly above -- the discriminating condition the per-frame
    # scan rides. The tracker's frame-0 read then has to land on the
    # fill's own top edge, or the colour is not discriminating at this
    # column (text ink or a separator matches it first) and the run would
    # measure the wrong feature. The programmatic selection can silently
    # not land, so the retry lives inside the calibration: each attempt
    # re-selects through the seam, re-captures frame 0, and re-samples.
    # Three strikes and the run refuses with the evidence frame.
    $colX = [int]($cropW * 0.72)
    $calibrated = $false
    $bandRef = $null; $trackerTop0 = -1
    $full = $null; $px0 = $null
    $releaseTop = -1
    $r3Top = [int]($row3.Rect.Y - $cropY)
    $r2Top = $r3Top - $rowH
    for ($calAttempt = 1; $calAttempt -le 3 -and -not $calibrated; $calAttempt++) {
        if ($calAttempt -gt 1) {
            [void](Invoke-SeamCommand $session @{ op = 'select'; index = 2 })
            Start-Sleep -Milliseconds 500
        }
        $full = [System.Drawing.Bitmap]::new($cropW, $cropH)
        $g = [System.Drawing.Graphics]::FromImage($full)
        $g.CopyFromScreen($cropX, $cropY, 0, 0, $full.Size)
        $g.Dispose()
        $px0 = Get-Pixels $full
        $bandRef = Get-Pixel $px0 $colX ($r3Top + [int]($rowH * 0.5))
        $bgRef = Get-Pixel $px0 $colX ($r2Top + [int]($rowH * 0.5))
        if (([math]::Abs($bandRef[0] - $bgRef[0]) -le 12) -and
            ([math]::Abs($bandRef[1] - $bgRef[1]) -le 12) -and
            ([math]::Abs($bandRef[2] - $bgRef[2]) -le 12)) {
            Write-Host ("calibration attempt {0}: selected fill rgb({1}) matches the unselected background rgb({2}) at x={3} - the selection did not paint" -f $calAttempt, ($bandRef -join ','), ($bgRef -join ','), $colX)
            continue
        }
        # The tracker's own frame-0 read must land on the fill's top edge:
        # scanning downward from just above the row, the first colour
        # match IS the band top the per-frame loop will keep re-finding.
        $releaseTop = $r3Top - $rowH
        $trackerTop0 = Find-BandTop $px0 $bandRef 24 ([Math]::Max(1, $releaseTop - [int]($rowH * 0.4))) $colX 8 ([Math]::Min($px0.h - 1, $r3Top + [int]($rowH * 0.25)))
        if ($trackerTop0 -lt 0 -or [math]::Abs($trackerTop0 - $r3Top) -gt 6) {
            Write-Host ("calibration attempt {0}: tracker found band at y={1} but row 3's top is y={2} (band rgb({3}) at x={4})" -f $calAttempt, $trackerTop0, $r3Top, ($bandRef -join ','), $colX)
            continue
        }
        $calibrated = $true
    }
    if (-not $calibrated) {
        $diag = Join-Path $OutDir 'calibration-frame0.png'
        if ($null -ne $full) { $full.Save($diag, [System.Drawing.Imaging.ImageFormat]::Png) }
        Write-Host "calibration: frame 0 saved to $diag"
        throw 'HARVEST_MISS: could not calibrate the tracked band on the selected row (3 attempts) - the selection never painted where the sampler could see it'
    }
    $bandTop0 = $trackerTop0
    Write-Host "calibrated: selection fill rgb($($bandRef -join ',')) at y=$bandTop0 (bg rgb($($bgRef -join ',')))"

    # The gesture and the capture share one stopwatch: the send stamp is
    # taken on it just before the command goes down the pipe, the frames
    # are stamped on it as they are grabbed, and the seam's own commit and
    # release offsets are added to the send stamp afterwards. The response
    # is deliberately collected AFTER the film: the paced walk is in
    # flight while the frames accumulate.
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $sendAt = $sw.ElapsedMilliseconds
    Send-SeamCommand $session @{ op = 'drag-paced'; from = 1; to = 2; tickMs = $TickMs }
    for ($i = 0; $i -lt $MaxFrames; $i++) {
        $full = [System.Drawing.Bitmap]::new($cropW, $cropH)
        $g = [System.Drawing.Graphics]::FromImage($full)
        $g.CopyFromScreen($cropX, $cropY, 0, 0, $full.Size)
        $g.Dispose()
        $frames.Add([pscustomobject]@{ t = $sw.ElapsedMilliseconds; bmp = $full })
        $remain = (($i + 1) * $IntervalMs) - $sw.ElapsedMilliseconds
        if ($remain -gt 0) { Start-Sleep -Milliseconds $remain }
    }
    $drag = Receive-SeamResponse $session 'drag-paced'
    if ($null -eq $drag.commitMs) { throw 'PRODUCT_FAIL: the paced drag reported no commit - the crossing never fired' }
    $crossAt = $sendAt + [int]$drag.commitMs
    $releaseAt = if ($null -ne $drag.releaseMs) { $sendAt + [int]$drag.releaseMs } else { -1 }
    Write-Host ("captured {0} frames over {1}ms; commit@{2} release@{3} landed={4}" -f
        $frames.Count, $sw.ElapsedMilliseconds, $crossAt, $releaseAt, $drag.landed)

    # Analysis: band top per frame, saved as PNGs for the transcript. The
    # scan is SPAN-SCOPED: the tracked band physically cannot leave the
    # span between the release row's band top (minus the settle spring's
    # overshoot headroom) and a little below the grab row's band top, so
    # rows outside that span -- notably another row's title ink -- are not
    # readable at all.
    $scanFrom = [Math]::Max(1, $releaseTop - [int]($rowH * 0.4))
    $scanTo = [Math]::Min($frames[0].bmp.Height - 1, $bandTop0 + [int]($rowH * 0.25))
    $tops = New-Object int[] $frames.Count
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $px = Get-Pixels $frames[$i].bmp
        $tops[$i] = Find-BandTop $px $bandRef 24 $scanFrom $colX 8 $scanTo
        $frames[$i].bmp.Save((Join-Path $OutDir ("frames\frame-{0:d3}-{1:d4}ms.png" -f $i, $frames[$i].t)))
    }
    for ($i = 0; $i -lt $frames.Count; $i++) {
        Write-Host ("frame {0:d3} t={1:d4}ms bandTop={2}" -f $i, $frames[$i].t, $tops[$i])
    }

    # A lost band inside the measured window is the tracker saying
    # "unreadable" -- the fast glide motion-blurs the stroke below the
    # colour match. That is tolerable as long as the window stays mostly
    # readable and both measurements land on real, uncarried readings.
    $measureEndMs = $crossAt + 500
    $windowFrames = @(0..($frames.Count - 1) | Where-Object { $frames[$_].t -le $measureEndMs })
    $bad = @($windowFrames | Where-Object { $tops[$_] -lt 0 })
    if ($bad.Count -gt [int]([Math]::Ceiling($windowFrames.Count / 2.0))) {
        throw "PRODUCT_FAIL: the band was unreadable in $($bad.Count) of $($windowFrames.Count) measured-window frames - the tracker lost the drag"
    }

    # Gap open: the first REAL reading (never a carried or blurred frame)
    # whose band top has risen 5px or more off its pre-crossing position,
    # measured from the commit the seam reported.
    $commitFrame = -1
    for ($i = 0; $i -lt $frames.Count; $i++) { if ($frames[$i].t -ge $crossAt) { $commitFrame = $i; break } }
    if ($commitFrame -lt 0) { throw 'HARVEST_MISS: no frame at or after the commit' }
    $gapFrame = -1
    for ($i = $commitFrame; $i -lt $frames.Count; $i++) {
        if ($tops[$i] -ge 0 -and $tops[$i] -le ($bandTop0 - 5)) { $gapFrame = $i; break }
    }
    $gapMs = if ($gapFrame -ge 0) { $frames[$gapFrame].t - $crossAt } else { -1 }
    Write-Host "gap: commitFrame=$commitFrame gapFrame=$gapFrame gapMs=$gapMs"

    # Convergence: the band STOPS -- six consecutive real readings within
    # 2px of each other, at a position within one row of the release
    # row's band top, within 500ms of the commit.
    $settledMs = -1
    $settledTop = -1
    for ($i = $commitFrame; $i -lt $frames.Count; $i++) {
        if ($frames[$i].t -gt $measureEndMs) { break }
        if ($tops[$i] -lt 0) { continue }
        if ([math]::Abs($tops[$i] - $releaseTop) -gt $rowH) { continue }
        $run = 1
        for ($j = $i + 1; $j -lt $frames.Count -and $run -lt 6; $j++) {
            if ($tops[$j] -ge 0 -and [math]::Abs($tops[$j] - $tops[$i]) -le 2) { $run++ } else { break }
        }
        if ($run -ge 6) {
            $settledMs = $frames[$i].t - $crossAt
            $settledTop = $tops[$i]
            break
        }
    }
    Write-Host "converge: releaseTop=$releaseTop settledTop=$settledTop settledMs=$settledMs"

    # The travel reads the last REAL reading: a trailing blur frame says
    # nothing about where the band went.
    $lastReal = $bandTop0
    for ($i = $frames.Count - 1; $i -ge 0; $i--) { if ($tops[$i] -ge 0) { $lastReal = $tops[$i]; break } }
    $travel = $bandTop0 - $lastReal

    # The layout really swapped.
    Start-Sleep -Milliseconds 600
    $after = @(Get-StripRows | ForEach-Object { $_.Name })
    $wantOrder = (@($names[0], $names[2], $names[1], $names[3]) -join ',')
    $orderOk = ($after -join ',') -eq $wantOrder
    Write-Host "order after: $($after -join ',')"

    $result = [ordered]@{
        actuation = 'seam drag-paced; zero synthesized OS input'
        intervalMs = $IntervalMs
        tickMs = $TickMs
        frames = $frames.Count
        bandRef = $bandRef
        bandTop0 = $bandTop0
        crossAt = $crossAt
        releaseAt = $releaseAt
        tops = $tops
        gapOpenMs = $gapMs
        settledMs = $settledMs
        travelPx = $travel
        rowHeight = $rowH
        orderAfter = ($after -join ',')
        orderOk = $orderOk
        animations = $anim
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

    if (-not $orderOk) { throw "PRODUCT_FAIL: order after the drag is [$($after -join ',')], expected [$wantOrder]" }
    if ($travel -lt [int]($rowH * 0.6)) {
        throw "PRODUCT_FAIL: the band travelled only ${travel}px over a $rowH px row - the drag never displaced row 3, so the timings measure nothing"
    }
    if ($gapFrame -lt 0) {
        throw "PRODUCT_FAIL: the gap never opened - row 3's band never left its slot after the commit"
    }
    if ($gapMs -gt (2 * $IntervalMs + 40)) {
        throw "PRODUCT_FAIL: the gap opened ${gapMs}ms after the commit; the oracle allows 2 frames ($((2 * $IntervalMs + 40))ms)"
    }
    if ($settledMs -lt 0) {
        throw 'PRODUCT_FAIL: the band never settled within the film - offsets did not converge'
    }
    if ($settledMs -gt 500) {
        throw "PRODUCT_FAIL: offsets converged ${settledMs}ms after the commit; the oracle allows 500ms"
    }
    Write-Host "PASS gap=${gapMs}ms settled=${settledMs}ms travel=${travel}px order=$($after -join ',')"
}
catch {
    if ($null -ne $session -and $null -ne $session.Proc -and -not $session.Proc.HasExited) {
        try {
            $rc = [SeamWin]::RectOf($script:MainHwnd64)
            if ($null -ne $rc) {
                $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
                $bmp.Save((Join-Path $OutDir 'shots\fail-state.png'))
                $g.Dispose(); $bmp.Dispose()
            }
        } catch { }
    }
    $script:FatalWasProduct = ("$_" -like 'PRODUCT_FAIL*' -or "$_" -like 'PRODUCT_EXIT*')
    Write-Host "$_" -ForegroundColor Red
}
finally {
    if ($null -ne $session) { Stop-SeamSession $session }
}

$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
if ($crashGrew) {
    Write-Host 'PRODUCT_FAIL: crash.log grew during the run' -ForegroundColor Red
    exit 2
}
if ($script:FatalWasProduct) { exit 2 }
if ($script:FatalWasProduct -eq $false) { exit 1 }
exit 0
