#requires -Version 7
<#
    The Ctrl+Tab switcher's pane preview must paint the TERMINAL's
    background, not a colour of its own. It painted #0B0B0E unconditionally
    (PanePreviewRenderer.BuildMiniLayout), so every tile was near-black on
    every theme: wrong on a light one, and wrong on any dark one that is not
    exactly that shade.

    Two legs, each a fresh app process on a real theme with a very different
    background, and each measured out of a screen capture rather than off a
    resolved brush -- the same reason lib/contrast.ps1 exists: a brush value
    can be right while nothing paints with it.

      light   Catppuccin Latte  background #eff1f5
      dark    Catppuccin Mocha  background #1e1e2e

    Both expected colours are read from the theme files under
    %APPDATA%\ghostty\themes at run time, so an edited theme cannot silently
    make the harness assert a stale number.

    Three asserts, and the third is the point: the two legs must also differ
    from EACH OTHER. `wintty-dark` and `wintty-light` are not theme names --
    they are internal strings applied only when `theme` is unset -- so a
    config naming them resolves to nothing, both legs come out identical,
    and a harness without this assert would call that a pass twice.

    The preview body is a bare Canvas and so has no automation peer; UIA
    cannot see it the way contrast-oracle.ps1 sees the tile's title. The
    `switcher-preview-rect` seam op reports its screen rect instead.

    Exits 0 on pass, 2 on a product finding (a fill that is not the theme's
    background, or two legs that agree when their themes do not), 1 when the
    harness could not run and nothing is known about the product.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
. (Join-Path $PSScriptRoot 'lib/contrast.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots') | Out-Null

Add-Type -AssemblyName System.Drawing

# The two window facts a capture needs that lib/seam-client.ps1 does not
# carry: who owns the pixels at a point (the occlusion refusal) and a raise
# that does not take the keyboard away from whoever is using the machine.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PreviewWin {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static uint PidAt(int x, int y) {
        uint pid;
        GetWindowThreadProcessId(WindowFromPoint(new POINT { X = x, Y = y }), out pid);
        return pid;
    }
    public static bool PlaceOnTop(IntPtr h, int x, int y, int w, int hh) {
        return SetWindowPos(h, HWND_TOPMOST, x, y, w, hh, 0x0010 /* SWP_NOACTIVATE */);
    }
}
'@ -ErrorAction SilentlyContinue

# Mixed-DPI discipline: the seam reports the preview rect in screen pixels
# and RectOf reads the window in screen pixels, so both must land in the
# same coordinate space (-4 = PER_MONITOR_AWARE_V2).
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$WinX = 60; $WinY = 60; $WinW = 1500; $WinH = 950

# Per channel. The fill is an opaque brush on an opaque Border, so nothing
# composites over it and the only spread is the sampler's own bucket mean.
$Tolerance = 6

# Two legs "differ" only if some channel moves by more than this. Well under
# the 208/48 the two themes' backgrounds actually differ by, and well over
# any capture noise: this is the identical-captures trap, not a fine
# distinction.
$MinLegDelta = 24

$script:Findings = [System.Collections.Generic.List[string]]::new()
$script:Rows = [System.Collections.Generic.List[object]]::new()

# Themes are named by ABSOLUTE path, and that is not a shortcut around the
# theme resolver -- it is the only way to reach a real theme from a seam
# session. Start-SeamSession points XDG_CONFIG_HOME at an isolated temp dir,
# and both theme.zig and ThemeSearchPath.UserDirectories search only the
# config ROOT's wintty/ghostty themes dirs, never %APPDATA%, once a config
# directory exists. A bare `theme = Catppuccin Latte` therefore resolves to
# nothing there and both legs fall back to the same built-in colours -- the
# same silent-fallback trap as naming `wintty-light`, one layer down. Both
# sides take the absolute path as-is (ThemeSearchPath.IsAbsolute /
# theme.zig's openAbsolute branch), so the terminal and the chrome read the
# same file.
function Get-ThemePath([string]$Name) {
    $path = Join-Path $env:APPDATA "ghostty\themes\$Name"
    if (-not (Test-Path $path)) {
        throw "HARNESS: no theme file '$Name' under %APPDATA%\ghostty\themes"
    }
    return $path
}

function Get-ThemeBackground([string]$Path) {
    foreach ($line in Get-Content $Path) {
        if ($line -match '^\s*background\s*=\s*#?([0-9a-fA-F]{6})\s*$') {
            $hex = $Matches[1]
            return @(
                [Convert]::ToInt32($hex.Substring(0, 2), 16),
                [Convert]::ToInt32($hex.Substring(2, 2), 16),
                [Convert]::ToInt32($hex.Substring(4, 2), 16))
        }
    }
    throw "HARNESS: theme file '$Path' declares no plain background line"
}

function Format-Rgb($Rgb) { return ('#{0:X2}{1:X2}{2:X2}' -f $Rgb[0], $Rgb[1], $Rgb[2]) }

function Get-MaxChannelDelta($A, $B) {
    $d = 0
    for ($i = 0; $i -lt 3; $i++) {
        $d = [Math]::Max($d, [Math]::Abs($A[$i] - $B[$i]))
    }
    return $d
}

# One capture of the app window, refusing outright if anything is on top of
# it: a verdict read out of somebody else's pixels is worse than no verdict.
function New-WindowCapture([long]$Hwnd64, [uint32]$ProcId, [string]$Label) {
    $rc = [SeamWin]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw "HARNESS: degenerate window rect for '$Label'" }
    for ($gx = 1; $gx -le 5; $gx++) {
        for ($gy = 1; $gy -le 5; $gy++) {
            $px = [int]($rc.L + $rc.W * $gx / 6.0)
            $py = [int]($rc.T + $rc.Hh * $gy / 6.0)
            $owner = [PreviewWin]::PidAt($px, $py)
            if ($owner -ne $ProcId) {
                throw ("HARNESS: at '{0}' the point {1},{2} inside the window belongs to pid {3}, not to the app under test (pid {4})" -f
                    $Label, $px, $py, $owner, $ProcId)
            }
        }
    }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir "shots\$Label.png"))
    return [pscustomobject]@{ Bmp = $bmp; L = $rc.L; T = $rc.T; W = $rc.W; H = $rc.Hh }
}

# The preview's fill, sampled out of the capture. The inset drops the pane's
# own 1px divider inset and the Border's rounded corners, neither of which is
# the fill being asked about.
function Measure-PreviewFill($Cap, $Rect, [int]$Inset = 5) {
    $x = [int]$Rect.x - $Cap.L + $Inset
    $y = [int]$Rect.y - $Cap.T + $Inset
    $w = [int]$Rect.w - 2 * $Inset
    $h = [int]$Rect.h - 2 * $Inset
    if ($w -le 2 -or $h -le 2) { throw 'HARNESS: the reported preview rect is too small to sample' }
    $s = [ContrastSampler]::Flat($Cap.Bmp, $x, $y, $w, $h)
    if (-not $s.Ok) { throw ("HARNESS: could not sample the preview fill: {0}" -f $s.Why) }
    return $s
}

function Invoke-Leg([string]$Name, [string]$Theme) {
    $themePath = Get-ThemePath $Theme
    $expected = Get-ThemeBackground $themePath
    Write-Host ("-- {0}: theme '{1}', background {2}" -f $Name, $Theme, (Format-Rgb $expected))

    # Minimal on purpose: single-instance off so this runs beside somebody
    # else's Wintty, no saved geometry from a previous run, and the theme --
    # which is the whole variable under test.
    $config = @"
windows-single-instance = false
window-save-state = never
theme = $themePath
"@
    $s = Start-SeamSession -ExePath $ExePath -ConfigText $config
    try {
        [void][PreviewWin]::PlaceOnTop([SeamWin]::P($s.Hwnd64), $WinX, $WinY, $WinW, $WinH)
        Start-Sleep -Milliseconds 1200

        [void](Invoke-SeamCommand $s @{
            op = 'seed-tabs'; count = 3; titles = @('alpha', 'bravo', 'charlie') })

        # Two independent frames have to agree before anything is believed.
        # A just-placed window can still be showing a DWM transition frame --
        # a blurred composite of the desktop that is nobody's real colour and
        # that the occlusion probe happily calls ours, because it IS ours.
        # Two captures of two separate raises reading the same fill is what
        # separates a painted surface from a frame in flight.
        $sample = $null; $measured = $null
        for ($attempt = 1; $attempt -le 4; $attempt++) {
            $reads = @()
            for ($shot = 0; $shot -lt 2; $shot++) {
                # Raise the popup, then ask where its first tile's preview is
                # while it is still up: it dismisses itself on a 1.2s timer.
                # The tile set is the strip's rows, so the first tile is
                # 'alpha' on every raise and the rect does not move.
                [void](Invoke-SeamCommand $s @{ op = 'cycle'; forward = $true })
                $rect = (Invoke-SeamCommand $s @{ op = 'switcher-preview-rect' }).rect
                $cap = New-WindowCapture $s.Hwnd64 ([uint32]$s.Proc.Id) "switcher-$Name-$attempt-$shot"
                try { $reads += , (Measure-PreviewFill $cap $rect) } finally { $cap.Bmp.Dispose() }
            }
            $a = @($reads[0].BgR, $reads[0].BgG, $reads[0].BgB)
            $b = @($reads[1].BgR, $reads[1].BgG, $reads[1].BgB)
            if ((Get-MaxChannelDelta $a $b) -le 2) {
                $sample = $reads[1]; $measured = $b
                break
            }
            Write-Host ("  .. attempt {0}: {1} then {2}; the window is still repainting" -f
                $attempt, (Format-Rgb $a), (Format-Rgb $b))
            Start-Sleep -Milliseconds 800
        }
        if ($null -eq $measured) {
            throw 'HARNESS: the preview fill never settled across two consecutive captures'
        }
        $delta = Get-MaxChannelDelta $measured $expected
        $ok = $delta -le $Tolerance

        $script:Rows.Add([pscustomobject][ordered]@{
            leg = $Name; theme = $Theme
            expected = (Format-Rgb $expected); measured = (Format-Rgb $measured)
            maxChannelDelta = $delta; tolerance = $Tolerance; pass = $ok
            share = [Math]::Round($sample.BgShare, 3)
            rect = ('{0},{1} {2}x{3}' -f $rect.x, $rect.y, $rect.w, $rect.h)
        })
        $mark = if ($ok) { 'ok  ' } else { 'FAIL' }
        Write-Host ("  {0} preview fill {1}  expected {2}  delta {3} (<= {4}), background is {5:P0} of the tile" -f
            $mark, (Format-Rgb $measured), (Format-Rgb $expected), $delta, $Tolerance, $sample.BgShare)
        if (-not $ok) {
            $script:Findings.Add(
                ("{0}: the preview tile paints {1}, but theme '{2}' resolves its terminal background to {3} (max channel delta {4} > {5})" -f
                    $Name, (Format-Rgb $measured), $Theme, (Format-Rgb $expected), $delta, $Tolerance))
        }
        return $measured
    } finally {
        Stop-SeamSession $s
    }
}

if (-not (Test-Path $ExePath)) {
    Write-Host "HARNESS: missing exe: $ExePath"
    exit 1
}

$exit = 0
try {
    $light = Invoke-Leg 'light' 'Catppuccin Latte'
    $dark = Invoke-Leg 'dark' 'Catppuccin Mocha'

    # The trap this assert exists for: two legs whose configs named nothing
    # the resolver recognises come out pixel-identical and prove nothing.
    $spread = Get-MaxChannelDelta $light $dark
    $together = $spread -le $MinLegDelta
    Write-Host ("-- spread: light {0} vs dark {1}, max channel delta {2} (> {3} required)" -f
        (Format-Rgb $light), (Format-Rgb $dark), $spread, $MinLegDelta)
    if ($together) {
        $script:Findings.Add(
            ("the two legs painted the same preview fill ({0} vs {1}, delta {2}): the tile is not following the theme at all" -f
                (Format-Rgb $light), (Format-Rgb $dark), $spread))
    }
    $script:Rows.Add([pscustomobject][ordered]@{
        leg = 'spread'; theme = ''
        expected = ''; measured = ''
        maxChannelDelta = $spread; tolerance = $MinLegDelta; pass = (-not $together)
        share = $null; rect = ''
    })

    if ($script:Findings.Count -gt 0) { $exit = 2 }
} catch {
    Write-Host ("HARNESS: {0}" -f $_.Exception.Message)
    $script:Rows.Add([pscustomobject][ordered]@{
        leg = 'harness'; theme = ''; expected = ''; measured = ''
        maxChannelDelta = $null; tolerance = $null; pass = $null
        share = $null; rect = $_.Exception.Message
    })
    $exit = 1
}

@{
    what = 'the Ctrl+Tab switcher pane preview must paint the terminal background of the theme in force'
    instrument = 'rendered pixels, sampled out of a screen capture of the app window'
    rows = $script:Rows
    findings = $script:Findings
    exit = $exit
} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir 'switcher-preview-theme.json') -Encoding utf8

Write-Host ''
if ($exit -eq 0) { Write-Host 'PASS: both legs paint their theme background, and they differ' }
elseif ($exit -eq 2) {
    Write-Host 'FINDINGS:'
    foreach ($f in $script:Findings) { Write-Host "  - $f" }
}
exit $exit
