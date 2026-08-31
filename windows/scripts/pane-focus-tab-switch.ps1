#requires -Version 7
<#
    The per-tab active pane, end to end and seam-actuated: split a tab,
    park the focus on its RIGHT leaf, leave for another tab, come back,
    and demand that the right leaf is where typing lands AND where the
    active-pane chrome is drawn. Issue #869.

    Two oracles, deliberately different in kind:

      - the seam's focus report, read out of FocusManager rather than out
        of PaneHost.ActiveLeaf. "The tab remembers its last pane" and "you
        can type into it" are separate claims: the memory has always been
        there, and asserting it would pass over the bug.
      - pixels, from a screenshot of the real window. The inactive-pane
        dim film must lie over the LEFT leaf (the chrome followed the
        memory), and the cursor block inside the right leaf must be the
        filled one a focused surface draws, not the hollow outline an
        unfocused one leaves behind.

    The dim-film assert is a regression guard on the drawing path and is
    green with or without the focus restore. The focus report and the
    cursor-block count are the two that go red without it.

    Zero OS input is synthesized: the seam drives the real handlers
    in-process, so the machine stays usable for the whole run. The one
    thing the harness does to the desktop is read pixels off it.

    Exits 0 on pass, 2 on a product finding, 1 when the harness could not
    run and nothing is known about the product.
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

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

// What a screen capture needs and the seam cannot answer: the client
// origin in screen pixels (the seam speaks window-root DIPs), a raise so
// the window is the one on screen, and a check that nothing is sitting on
// top of the pixels about to be read.
public static class PaneProbe {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public static POINT ClientOrigin(long hwnd) {
        var p = new POINT();
        ClientToScreen(new IntPtr(hwnd), ref p);
        return p;
    }

    // No move, no size, and above all NO activation: the capture needs
    // the window in front, never the keyboard focus this scenario exists
    // to measure. Plain HWND_TOP loses to anybody's always-on-top window,
    // so the raise is a topmost one the caller puts back afterwards.
    public static void Raise(long hwnd, bool topmost) {
        SetWindowPos(new IntPtr(hwnd), new IntPtr(topmost ? -1 : -2),
                     0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    // Which process owns the topmost window at a screen point. A capture
    // reads whatever is on screen, so an occluding window would otherwise
    // be measured as if it were the product.
    public static uint PidAt(int x, int y) {
        var h = WindowFromPoint(new POINT { X = x, Y = y });
        if (h == IntPtr.Zero) return 0;
        uint pid;
        GetWindowThreadProcessId(GetAncestor(h, 2 /* GA_ROOT */), out pid);
        return pid;
    }
}
'@ -ErrorAction SilentlyContinue

# Mixed-DPI discipline: every rect this harness reads must live in one
# coordinate space (-4 = PER_MONITOR_AWARE_V2).
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

# A bright ground and a saturated cursor, so 22% of black over a pane and
# a filled cursor block are both unmistakable in a screenshot. Everything
# else is left at stock: the point is to measure the product's appearance,
# not the owner's config.
$Config = @'
windows-single-instance = true
window-save-state = never
vertical-tabs = true
window-theme = wintty
background = f0f0f0
foreground = 202020
cursor-color = ff0000
cursor-style-blink = false
vertical-tabs-hover-expand = false
'@

$LeafSampleInset = 10   # px, clears the pane border and the divider
$DimMargin       = 8    # luma points the dimmed leaf must fall behind by
$CursorMinRed    = 90   # px; calibrated below, see the header of the run

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$script:MainHwnd64 = 0
$script:Scenarios = [System.Collections.Generic.List[object]]::new()

# ---- pixel plumbing --------------------------------------------------------

function Get-WindowShot([int64]$Hwnd64, [string]$SavePath) {
    $rc = [SeamWin]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw 'HARVEST_MISS: the window has no usable rect' }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $g.Dispose()
    if ($SavePath) { $bmp.Save($SavePath) }
    return [pscustomobject]@{ Bmp = $bmp; L = $rc.L; T = $rc.T }
}

# Every pixel about to be measured must belong to the app. A screen
# capture reads whatever is on top, and this desktop has other windows on
# it; a stray one over the pane is a harness miss, never a verdict about
# the product.
function Measure-Occlusion([uint32]$ProcId, $Rect) {
    $miss = 0
    $seen = 0
    for ($y = $Rect.T + $LeafSampleInset; $y -lt $Rect.T + $Rect.H - $LeafSampleInset; $y += 24) {
        for ($x = $Rect.L + $LeafSampleInset; $x -lt $Rect.L + $Rect.W - $LeafSampleInset; $x += 24) {
            $seen++
            if ([PaneProbe]::PidAt($x, $y) -ne $ProcId) { $miss++ }
        }
    }
    if ($seen -eq 0) { throw 'HARVEST_MISS: the probed rect has no area' }
    return [pscustomobject]@{ Miss = $miss; Seen = $seen }
}

# Raise and re-probe until the panes are clear. Other agents share this
# desktop, so one covered attempt is noise; a whole deadline of them is
# a harness miss.
function Wait-Unoccluded([uint32]$ProcId, $Rects, [int]$Seconds = 45) {
    $worst = $null
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        [PaneProbe]::Raise($script:MainHwnd64, $true)
        Start-Sleep -Milliseconds 700
        $worst = $null
        foreach ($rect in $Rects) {
            $got = Measure-Occlusion $ProcId $rect
            if ($null -eq $worst -or $got.Miss -gt $worst.Miss) { $worst = $got }
        }
        if ($worst.Miss -eq 0) { return }
    }
    throw ("HARVEST_MISS: the panes stayed occluded at {0} of {1} probe points for {2}s - another window is over the capture" -f
        $worst.Miss, $worst.Seen, $Seconds)
}

# One seam DIP rect, landed on the screen the screenshot came off.
function Convert-SeamRect($Rect, $Origin, [double]$Scale) {
    return [pscustomobject]@{
        L = [int][math]::Round($Origin.X + $Rect.x * $Scale)
        T = [int][math]::Round($Origin.Y + $Rect.y * $Scale)
        W = [int][math]::Round($Rect.w * $Scale)
        H = [int][math]::Round($Rect.h * $Scale)
    }
}

# Walk the interior of a screen rect, handing every sampled pixel to the
# caller. Sampling every 3rd pixel keeps a full-pane walk under a second
# while still catching a cursor cell.
function Invoke-OverPixels($Shot, $Rect, [int]$Inset, [int]$Step, [scriptblock]$OnPixel) {
    $n = 0
    for ($y = $Rect.T + $Inset; $y -lt $Rect.T + $Rect.H - $Inset; $y += $Step) {
        $py = $y - $Shot.T
        if ($py -lt 0 -or $py -ge $Shot.Bmp.Height) { continue }
        for ($x = $Rect.L + $Inset; $x -lt $Rect.L + $Rect.W - $Inset; $x += $Step) {
            $px = $x - $Shot.L
            if ($px -lt 0 -or $px -ge $Shot.Bmp.Width) { continue }
            & $OnPixel $Shot.Bmp.GetPixel($px, $py)
            $n++
        }
    }
    if ($n -eq 0) { throw 'HARVEST_MISS: the sampled rect landed outside the capture' }
    return $n
}

function Measure-MeanLuma($Shot, $Rect) {
    $script:lumaSum = 0.0
    $n = Invoke-OverPixels $Shot $Rect $LeafSampleInset 3 {
        param($c) $script:lumaSum += (0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B)
    }
    return [math]::Round($script:lumaSum / $n, 2)
}

# The cursor block, counted in pixels. cursor-color is pure red and the
# ground is near-white, so a saturated-red pixel inside the pane is the
# cursor and nothing else. A focused surface fills the cell; an unfocused
# one draws the hollow outline, which is a fraction of the area.
function Measure-CursorPixels($Shot, $Rect) {
    $script:redCount = 0
    [void](Invoke-OverPixels $Shot $Rect $LeafSampleInset 1 {
        param($c) if ($c.R -gt 150 -and $c.G -lt 110 -and $c.B -lt 110) { $script:redCount++ }
    })
    return $script:redCount
}

# ---- the scenario ----------------------------------------------------------

function Assert-Leaves($State, [int]$want) {
    $count = @($State.panes.leaves).Count
    if ($count -ne $want) {
        throw "PRODUCT_FAIL: the active tab has $count leaf/leaves, expected $want"
    }
}

function Invoke-Scenario([string]$Name, [scriptblock]$Body) {
    $crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }
    $s = $null
    $entry = [ordered]@{ name = $Name; ok = $false; class = ''; error = '' }
    Write-Host "=== scenario $Name ==="
    try {
        Assert-NoWintty -Context "The pane focus scenario '$Name'"
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $Config
        $script:MainHwnd64 = $s.Hwnd64
        & $Body $s
        if ($s.Proc.HasExited) {
            throw ("APP_EXIT: the app exited during '{0}' (code {1})" -f $Name, $s.Proc.ExitCode)
        }
        $entry.ok = $true
        Write-Host "PASS $Name" -ForegroundColor Green
    } catch {
        $msg = "$($_.Exception.Message)"
        $entry.error = $msg
        $entry.class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
        Write-Host "FAIL $Name [$($entry.class)]: $msg" -ForegroundColor Red
        if ($null -ne $s -and -not $s.Proc.HasExited) {
            try { [void](Get-WindowShot $script:MainHwnd64 (Join-Path $OutDir "shots\fail-$Name.png")) } catch { }
        }
    } finally {
        if ($null -ne $s) { Stop-SeamSession $s }
    }
    if ((Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)) {
        $entry.ok = $false
        $entry.class = 'product'
        $entry.error = ($entry.error + ' crash.log grew during the scenario').Trim()
        Write-Host "FAIL $Name [product]: crash.log grew" -ForegroundColor Red
    }
    $script:Scenarios.Add($entry)
}

if (-not (Test-Path $ExePath)) {
    Write-Host "HARVEST_MISS: missing exe: $ExePath"
    exit 1
}

Invoke-Scenario 'right-pane-survives-tab-switch' {
    param($s)

    [void](Invoke-SeamCommand $s @{ op = 'seed-tabs'; count = 2; titles = @('panefocus-1', 'panefocus-2') })
    [void](Invoke-SeamCommand $s @{ op = 'select'; index = 0 })

    $split = Invoke-SeamCommand $s @{ op = 'split'; orientation = 'vertical' }
    Assert-Leaves $split.state 2

    # Say which leaf explicitly rather than inheriting whatever the split
    # left active, so the scenario asserts a chosen pane and not a default.
    $armed = (Invoke-SeamCommand $s @{ op = 'focus-pane'; index = 1 }).state
    if ($armed.panes.activeLeaf -ne 1) {
        throw "PRODUCT_FAIL: focusing leaf 1 left the active leaf at $($armed.panes.activeLeaf)"
    }
    if ($armed.panes.focusedLeaf -ne 1) {
        throw "PRODUCT_FAIL: leaf 1 was focused but FocusManager reports leaf $($armed.panes.focusedLeaf) ('$($armed.panes.focusedElement)')"
    }

    # The startup glow is an animation over a fresh pane; let it finish
    # before anything reads pixels.
    Start-Sleep -Seconds 2

    # Away and back, through the manager's own activation -- the call the
    # strip's SelectionChanged and every jump chord funnel into.
    [void](Invoke-SeamCommand $s @{ op = 'select'; index = 1 })
    $back = (Invoke-SeamCommand $s @{ op = 'select'; index = 0 }).state

    # The memory half: already shipped, asserted so a regression there is
    # not mistaken for the focus bug.
    $tab0 = $back.tabs[0]
    if ($tab0.leaves -ne 2 -or $tab0.activeLeaf -ne 1) {
        throw "PRODUCT_FAIL: tab 1 came back with activeLeaf $($tab0.activeLeaf) of $($tab0.leaves) - the per-tab memory is gone"
    }

    # ---- the visual half ---------------------------------------------
    # Pixels are read and asserted BEFORE the seam's focus report, so a
    # run that goes red says what the window actually looked like rather
    # than stopping at the cheaper oracle.
    $shot = $null
    $origin = [PaneProbe]::ClientOrigin($script:MainHwnd64)
    $scale = $back.panes.scale
    $left = Convert-SeamRect $back.panes.leaves[0] $origin $scale
    $right = Convert-SeamRect $back.panes.leaves[1] $origin $scale
    $border = Convert-SeamRect $back.panes.border $origin $scale
    try {
        Wait-Unoccluded ([uint32]$s.Proc.Id) @($left, $right)
        $shot = Get-WindowShot $script:MainHwnd64 (Join-Path $OutDir 'shots\after-switch-back.png')
        if ($border.W -le 0 -or $border.H -le 0) {
            throw 'PRODUCT_FAIL: the active-pane border is not being drawn at all'
        }

        # Where the stroke is drawn, not which leaf a field names: the
        # border rect has to sit on the right leaf and nowhere near the
        # left one.
        $cx = $border.L + [int]($border.W / 2)
        if ($cx -lt $right.L -or $cx -gt $right.L + $right.W) {
            throw "PRODUCT_FAIL: the active-pane border is centred at x=$cx, outside the right leaf [$($right.L)..$($right.L + $right.W)]"
        }

        $lumaLeft = Measure-MeanLuma $shot $left
        $lumaRight = Measure-MeanLuma $shot $right
        $reds = Measure-CursorPixels $shot $right
        Write-Host ("MEASURED luma left={0} right={1}; cursor pixels in the right leaf={2}" -f $lumaLeft, $lumaRight, $reds)

        if ($lumaLeft -ge $lumaRight - $DimMargin) {
            throw ("PRODUCT_FAIL: the inactive-pane dim film is not over the left leaf (luma left={0}, right={1})" -f $lumaLeft, $lumaRight)
        }
        if ($reds -lt $CursorMinRed) {
            throw ("PRODUCT_FAIL: the right leaf shows {0} cursor pixel(s), below the {1} a filled (focused) cursor block draws - the pane is not focused" -f $reds, $CursorMinRed)
        }
    } finally {
        if ($null -ne $shot) { $shot.Bmp.Dispose() }
        [PaneProbe]::Raise($script:MainHwnd64, $false)
    }

    # The focus half, straight from FocusManager: the same defect said in
    # the product's own words, and the one that names the leaf.
    if ($back.panes.focusedLeaf -ne 1) {
        throw ("PRODUCT_FAIL: tab 1 remembers leaf {0} but keyboard focus came back on leaf {1} ('{2}') - the user has to click a pane before they can type" -f
            $back.panes.activeLeaf, $back.panes.focusedLeaf, $back.panes.focusedElement)
    }
}

# ---- verdict ---------------------------------------------------------------

$result = [ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=1); zero synthesized OS input'
    scenarios = $script:Scenarios
}
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

Write-Host ''
Write-Host 'scenario                      verdict'
Write-Host '----------------------------  -------'
foreach ($sc in $script:Scenarios) {
    $verdict = if ($sc.ok) { 'PASS' } else { "FAIL ($($sc.class))" }
    Write-Host ("{0,-29} {1}" -f $sc.name, $verdict)
}

$product = @($script:Scenarios | Where-Object { -not $_.ok -and $_.class -eq 'product' })
$harness = @($script:Scenarios | Where-Object { -not $_.ok -and $_.class -eq 'harness' })
if ($product.Count -gt 0) { exit 2 }
if ($harness.Count -gt 0) { exit 1 }
exit 0
