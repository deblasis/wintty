#requires -Version 7
<#
    The vertical strip's chrome geometry, seam-measured. The oracle is the
    strip's own arranged layout, read back over the seam's element-rects op
    (WINTTY_TEST_SEAM=1) -- not sampled pixels. That is deliberate: the
    strip wears Mica, so what a screen grab shows depends on the desktop
    behind the window, and the questions asked here are about where things
    were laid out, which layout answers exactly.

    One process, one seeded state (one pin, one group, three loose tabs),
    measured at both pane widths: compact (the 48px rail the strip starts
    in) and expanded (the pinned sidebar, reached with toggle-sidebar).

    Checks, each at both widths unless stated:

      boundary-centred      the pinned zone's boundary rule is centred on
                            the row band it separates. The rule stops short
                            of the pane edge on purpose; stopping short on
                            one side only is what makes it read crooked.
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

# The rule between the pinned zone and the body list marks a boundary
# across the row band, so its center must be the band's center. The pinned
# rows define that band: they are the strip's own rows rather than MUXC's,
# laid out from the same left inset the separators and the selection fill
# use.
function Test-BoundaryCentred($Rects, [string]$Leg) {
    $boundary = Assert-Rect $Rects.boundary "the boundary rule ($Leg)"
    if (@($Rects.pinned).Count -eq 0) { throw "HARVEST_MISS: no pinned row in the $Leg leg" }
    $row = Assert-Rect @($Rects.pinned)[0].row "the pinned row ($Leg)"
    $drift = (CenterX $boundary) - (CenterX $row)
    Add-Check "boundary-centred-$Leg" (
        "rule {0:F1}..{1:F1} center {2:F1}, row center {3:F1}, drift {4:F1}px" -f
        $boundary.x, (Right $boundary), (CenterX $boundary), (CenterX $row), $drift
    ) ([math]::Abs($drift) -le $Tolerance)
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

    # One pin so the zone exists, one group so a header renders, and loose
    # rows on both sides of the header so the body list is not degenerate.
    [void](Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = 5; titles = $names })
    [void](Invoke-SeamCommand $session @{ op = 'pin'; index = 0; via = 'router' })
    [void](Invoke-SeamCommand $session @{ op = 'group'; indices = @(3, 4) })
    # Off the group, so nothing folds and every row stays measurable.
    [void](Invoke-SeamCommand $session @{ op = 'select'; index = 1 })

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
    Test-BoundaryCentred $compact.rects 'compact'
    Test-NoCloseWhenCompact $compact.rects
    Test-HeaderFits $compact.rects 'compact'

    Write-Host ''
    Write-Host "=== expanded (pane $($expanded.state.paneWidth)px) ==="
    Test-BoundaryCentred $expanded.rects 'expanded'
    Test-CloseInsetExpanded $expanded.rects
    Test-HeaderFits $expanded.rects 'expanded'

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

$result = [ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=1); geometry read from arranged layout, no pixels'
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
