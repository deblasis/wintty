<#
.SYNOPSIS
    Does a colour-tagged tab's PROFILE ICON actually get the tag's ink?

.DESCRIPTION
    The horizontal strip's ink pass paints a colour-tagged tab's title and
    then hands the same brush to the rest of the header row -- profile
    icon, bell. The row lookup went through the header panel's first
    child, and when the group rail took that slot (#833) the whole row
    assignment became unreachable: titles kept colouring, glyphs silently
    stopped (#883, and the dead glyph ink behind #882).

    The canary used to be the pushpin. Pinned tabs are icon squares now
    and carry no pushpin, so the profile icon is the glyph that remains --
    and it is the same claim, because it is the same loop over the row's
    children that stopped running. The two measured tabs stay pinned: a
    pinned tab shows its icon and nothing else, so the sample cannot land
    on a title by accident.

    Nothing in the C# said so. The brush was still computed, the call was
    still written, and a test over resolved brushes would have measured
    the correct value of a colour nothing painted with. So this reads
    PIXELS: it drives real state through the seam, asks the seam where the
    icon landed, and samples the ink out of a screen capture.

    The claim is deliberately RELATIVE. The strip renders light in every
    leg on this machine (Mica shows a light desktop through it even under
    a dark theme), so "the pin is light because the theme is dark" would
    be measuring the wrong thing. What the fix guarantees, and what this
    asserts, is that the tagged tab's icon ink IS the tag foreground and
    is NOT the untagged tab's icon ink. Both tabs are pinned and neither
    is active, so the only difference between them is the tag.

    Coexistence. Like contrast-oracle.ps1 this does NOT call
    Assert-NoWintty: other agents and the developer run their own Wintty
    while it works. It launches its own instance against an isolated
    XDG_CONFIG_HOME with single-instance off, moves only its own window,
    and stops only what it started.

    Exit codes: 0 the ink matched, 1 the harness could not measure
    (no launch, no capture, a rect it could not sample), 2 the ink was
    measured and it was wrong.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\artifacts\tab-tag-ink'),
    # The preset to tag with. Any palette name; the harness asserts against
    # whatever foreground the product derives for it rather than a colour
    # hardcoded here.
    [string]$Color = 'Red'
)

. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
. (Join-Path $PSScriptRoot 'lib/contrast.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Add-Type -AssemblyName System.Drawing

# Mixed-DPI discipline: the seam reports physical screen pixels, and
# CopyFromScreen must be reading the same space or every sample lands next
# to the glyph it named.
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class TagInkWin {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    const uint SWP_NOACTIVATE = 0x0010;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    // Place and raise WITHOUT activating: the capture needs this window's
    // pixels to be the topmost ones, it does not need the keyboard, and
    // taking the keyboard would yank the caret from whoever is at the box.
    public static bool PlaceOnTop(IntPtr h, int x, int y, int w, int hh) {
        return SetWindowPos(h, HWND_TOPMOST, x, y, w, hh, SWP_NOACTIVATE);
    }
    public static bool TryActivate(IntPtr h) { return SetForegroundWindow(h); }
    public static uint PidAt(int x, int y) {
        var h = WindowFromPoint(new POINT { X = x, Y = y });
        uint pid; GetWindowThreadProcessId(h, out pid); return pid;
    }
    public static string ClassAt(int x, int y) {
        var h = WindowFromPoint(new POINT { X = x, Y = y });
        var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString();
    }
}
'@ -ErrorAction SilentlyContinue

# Fixed geometry so a rect that moves between runs is a product change and
# not a window somebody dragged.
$WinX = 60; $WinY = 60; $WinW = 1500; $WinH = 950

# Minimal and explicit: the stock appearance, not the developer's config.
# Horizontal is the layout under test; the vertical strip builds its pin
# marks somewhere else entirely and is not measured here.
$ConfigText = @'
windows-single-instance = false
window-save-state = never
vertical-tabs = false
window-theme = dark
'@

# How far apart two inks have to be before the difference is paint rather
# than Mica noise plus a ClearType fringe. The sampler already gathers both
# into one cluster per colour; this is the second guard, in the space the
# assertion is actually made in.
$InkDistinct = 24

function Get-ChebyshevDistance($A, $B) {
    return [Math]::Max([Math]::Abs($A[0] - $B[0]),
           [Math]::Max([Math]::Abs($A[1] - $B[1]), [Math]::Abs($A[2] - $B[2])))
}

function ConvertFrom-Hex([string]$Hex) {
    $h = $Hex.TrimStart('#')
    return @([Convert]::ToInt32($h.Substring(0, 2), 16),
             [Convert]::ToInt32($h.Substring(2, 2), 16),
             [Convert]::ToInt32($h.Substring(4, 2), 16))
}

function Format-Rgb($C) { return ('#{0:X2}{1:X2}{2:X2}' -f $C[0], $C[1], $C[2]) }

# One capture, sampled twice. A fresh screenshot per glyph would let the
# window repaint between the two samples, which is how a harness invents an
# ink difference that never happened.
function New-WindowCapture([int64]$Hwnd64, [uint32]$ProcId, [string]$Label) {
    $rc = [SeamWin]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw "HARVEST_MISS: degenerate window rect" }
    for ($gx = 1; $gx -le 5; $gx++) {
        for ($gy = 1; $gy -le 5; $gy++) {
            $px = [int]($rc.L + $rc.W * $gx / 6.0)
            $py = [int]($rc.T + $rc.Hh * $gy / 6.0)
            $owner = [TagInkWin]::PidAt($px, $py)
            if ($owner -ne $ProcId) {
                throw ("OCCLUDED: {0},{1} inside the window belongs to pid {2} (class '{3}'), not to the app under test (pid {4}); nothing was measured" -f
                    $px, $py, $owner, [TagInkWin]::ClassAt($px, $py), $ProcId)
            }
        }
    }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir "$Label.png"))
    return [pscustomobject]@{ Bmp = $bmp; L = $rc.L; T = $rc.T; W = $rc.W; H = $rc.Hh }
}

# A seam rect (physical screen pixels) sampled out of the capture. A rect
# that does not sit wholly inside the capture is not clamped into one that
# does: it is a rect nobody can vouch for, and it stops the run.
function Get-InkAt($Cap, $Rect, [string]$What) {
    $x = [int]$Rect.x - $Cap.L
    $y = [int]$Rect.y - $Cap.T
    $w = [int]$Rect.w
    $h = [int]$Rect.h
    if ($x -lt 0 -or $y -lt 0 -or $w -le 2 -or $h -le 2 -or
        ($x + $w) -gt $Cap.W -or ($y + $h) -gt $Cap.H) {
        throw ("HARVEST_MISS: the {0} rect {1},{2} {3}x{4} is not inside the window capture" -f $What, $x, $y, $w, $h)
    }
    $s = [ContrastSampler]::Region($Cap.Bmp, $x, $y, $w, $h)
    if (-not $s.Ok) { throw ("HARVEST_MISS: could not sample the {0}: {1}" -f $What, $s.Why) }
    if ($s.FgCount -eq 0) {
        throw ("HARVEST_MISS: the {0} rect is flat -- no glyph was painted in it at all" -f $What)
    }
    return $s
}

if (-not (Test-Path $ExePath)) {
    Write-Host "HARVEST_MISS: missing exe: $ExePath"
    exit 1
}

$session = $null
$verdict = 0
try {
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $ConfigText
    $hwnd = [SeamWin]::P($session.Hwnd64)
    [void][TagInkWin]::PlaceOnTop($hwnd, $WinX, $WinY, $WinW, $WinH)
    [void][TagInkWin]::TryActivate($hwnd)
    Start-Sleep -Milliseconds 700

    [void](Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = 3; titles = @('tagged', 'plain', 'other') })
    # Both measured tabs pinned, so both show the glyph; the third is
    # selected so neither of them is the active tab. The tag is then the
    # only thing that differs between them.
    [void](Invoke-SeamCommand $session @{ op = 'pin'; index = 0 })
    [void](Invoke-SeamCommand $session @{ op = 'pin'; index = 1 })
    [void](Invoke-SeamCommand $session @{ op = 'select'; index = 2 })
    $state = Invoke-SeamCommand $session @{ op = 'tab-color'; index = 0; color = $Color }

    $tagged = $state.state.tabs[0]
    $plain = $state.state.tabs[1]
    if (-not $tagged.pinned -or -not $plain.pinned) {
        throw "HARVEST_MISS: the two measured tabs are not both pinned"
    }
    if ($tagged.color -ne $Color) {
        throw ("HARVEST_MISS: the tag did not land: tab 0 reports '{0}'" -f $tagged.color)
    }
    if ($state.state.active -eq 0 -or $state.state.active -eq 1) {
        throw ("HARVEST_MISS: a measured tab is active (index {0})" -f $state.state.active)
    }

    $taggedRect = Invoke-SeamCommand $session @{ op = 'header-rect'; index = 0; part = 'icon' }
    $plainRect = Invoke-SeamCommand $session @{ op = 'header-rect'; index = 1; part = 'icon' }
    if (-not $taggedRect.fg) {
        throw "HARVEST_MISS: the seam reported no tag foreground for the tagged tab"
    }
    $expect = ConvertFrom-Hex $taggedRect.fg

    Start-Sleep -Milliseconds 400
    $cap = New-WindowCapture $session.Hwnd64 ([uint32]$session.Proc.Id) 'strip'
    try {
        $taggedInk = Get-InkAt $cap $taggedRect 'tagged profile icon'
        $plainInk = Get-InkAt $cap $plainRect 'untagged profile icon'
    } finally { $cap.Bmp.Dispose() }

    $tagged3 = @($taggedInk.FgR, $taggedInk.FgG, $taggedInk.FgB)
    $plain3 = @($plainInk.FgR, $plainInk.FgG, $plainInk.FgB)
    $distinct = Get-ChebyshevDistance $tagged3 $plain3
    $toExpect = Get-ChebyshevDistance $tagged3 $expect
    $plainToExpect = Get-ChebyshevDistance $plain3 $expect

    Write-Host ''
    Write-Host ("tag foreground the product chose : {0}" -f (Format-Rgb $expect))
    Write-Host ("tagged icon ink   (measured)     : {0} on {1}" -f $taggedInk.FgHex, $taggedInk.BgHex)
    Write-Host ("untagged icon ink (measured)     : {0} on {1}" -f $plainInk.FgHex, $plainInk.BgHex)
    Write-Host ("tagged vs untagged               : {0} (need >= {1})" -f $distinct, $InkDistinct)
    Write-Host ("tagged vs tag foreground         : {0}" -f $toExpect)
    Write-Host ("untagged vs tag foreground       : {0} (tagged must be nearer)" -f $plainToExpect)
    Write-Host ''

    # Three relative claims and no absolute colour target. A 12pt glyph
    # anti-aliased over a translucent tint cannot land ON its brush -- the
    # cluster mean is a blend by construction -- so any "within N of the
    # brush" bar would be a number somebody liked. What the fix actually
    # guarantees is direction: the ink MOVED, it moved TOWARD the tag
    # foreground, and it ended up nearer that foreground than the ink it
    # would have carried untagged.
    $fail = @()
    if ($distinct -lt $InkDistinct) {
        $fail += ("the tagged tab's profile icon is the same ink as the untagged tab's ({0} vs {1}, distance {2}): the tag never reached the glyph" -f
            $taggedInk.FgHex, $plainInk.FgHex, $distinct)
    }
    if ($toExpect -ge $plainToExpect) {
        $fail += ("the tagged profile icon is no closer to the tag foreground {0} than the untagged one is ({1} vs {2})" -f
            (Format-Rgb $expect), $toExpect, $plainToExpect)
    }
    if ($toExpect -ge $distinct) {
        $fail += ("the tagged profile icon sits nearer the untagged ink than the tag foreground {0} ({1} vs {2}): it drifted, it did not land on the tag" -f
            (Format-Rgb $expect), $distinct, $toExpect)
    }

    if ($fail.Count -gt 0) {
        Write-Host 'FAIL: the colour tag did not reach the profile icon' -ForegroundColor Red
        foreach ($f in $fail) { Write-Host ("  - {0}" -f $f) -ForegroundColor Red }
        $verdict = 2
    } else {
        Write-Host 'PASS: the tagged tab paints its profile icon in the tag foreground' -ForegroundColor Green
    }
}
catch {
    Write-Host ("HARNESS: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
    $verdict = 1
}
finally {
    if ($session) { Stop-SeamSession $session }
}
exit $verdict
