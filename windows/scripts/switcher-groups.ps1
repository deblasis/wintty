#requires -Version 7
<#
    The Ctrl+Tab switcher must say two things it used to leave unsaid: which
    tiles belong to a group, and which tile the cycle is currently on.

    Both are read out of RENDERED PIXELS, for the reason lib/contrast.ps1
    exists: a brush value can be right while nothing paints with it. The
    `switcher-cells` seam op supplies the rects to point at -- the field's
    header band, the tile cards, the pane previews -- because none of those
    surfaces is reachable over UIA. They are bare panels and get no
    automation peer, which is the same wall switcher-preview-theme.ps1 hit.

    Five legs.

      structure   The seam's own reading of the card: a run of three tabs
                  reports one group on exactly three cells, with exactly one
                  head and exactly one tail, and the tabs outside the run
                  report no group at all. This is the plan the popup painted
                  from, not a second opinion formed by re-walking the tree.

      field       The head cell's header band must not paint the same colour
                  as an ungrouped cell's band. The band is the field's own
                  wash; if it composites to the card ground, the field is
                  invisible and the group grammar is decoration in the source
                  only.

      active      Exactly one cell reports active, it is the manager's active
                  tab, and its tile card measurably out-paints an idle one --
                  the dim is what makes the selection findable at a glance.

      moves       The bright cell has to MOVE with the cycle. One more step,
                  and the tile that was lit must be the dim one and the tile
                  that was dim must be lit. Without this leg an oracle that
                  always measured tile 0 would pass a build whose highlight
                  never moved at all.

      stays       A THIRD step, and the tile dimmed on the first must still
                  be sitting where the first step put it. The highlight is a
                  Storyboard, a stopped Storyboard reverts to its BASE, and
                  the base of the tile that was lit when the card was built
                  is LIT -- so the third press brought it back at full
                  opacity beside the real selection and left it there. Two
                  steps cannot see that: the reversion needs a stop, and the
                  first stop with anything to revert is the third press.

    What it does NOT see: the end bar, the field's rounding, the header's
    text, the highlight's easing or duration, reduce-motion (it runs one
    session with whatever the desktop's animation setting is), High Contrast,
    the light theme, a run that wraps across two rows of the grid, and
    anything about the vertical or horizontal STRIP -- this harness only ever
    opens the popup.

    Exits 0 on pass, 2 on a product finding, 1 when the harness could not run
    and nothing is known about the product.
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

# The one window fact a capture needs that lib/seam-client.ps1 does not
# carry: who owns the pixels at a point, so a verdict is never read out of
# somebody else's window. The raise is SWP_NOACTIVATE -- it never takes the
# keyboard away from whoever is using the machine.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SwitcherWin {
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

# Mixed-DPI discipline: the seam reports rects in screen pixels and RectOf
# reads the window in screen pixels, so both must land in the same coordinate
# space (-4 = PER_MONITOR_AWARE_V2).
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$WinX = 60; $WinY = 60; $WinW = 1500; $WinH = 950

# Two sampled regions "differ" only if some channel moves by more than this.
# Well over any capture noise and well under the separation a real wash
# produces (37 measured on the stock dark theme), so this is the
# paints-nothing trap rather than a fine distinction.
$MinDelta = 10

# ...and the dim's own floor, which is NOT the same number.
#
# The wash is a colour laid on a colour, so its separation is whatever the
# two colours are. The dim is a fraction: an idle tile is
# TabSwitcherShape.IdleTileOpacity of the lit one composited over what is
# behind it, so the measured delta is (1 - 0.7) = 30% of the distance
# between the tile's own fill and the field under it -- and on the stock
# dark theme that whole distance is small, which put the leg at 11 against a
# floor of 10. One count of headroom is not a threshold, it is a coin toss
# with the next theme: the harness would have reported a working dim as a
# product finding.
#
# Half of what the dim measurably produces. A dim that stops happening
# collapses this to 0 or 1 -- that is what the mutation run shows -- so
# halving costs nothing against the failure being guarded and buys the
# margin the arithmetic above says is needed.
$MinDimDelta = 5

$script:Findings = [System.Collections.Generic.List[string]]::new()
$script:Rows = [System.Collections.Generic.List[object]]::new()

function Format-Rgb($Rgb) { return ('#{0:X2}{1:X2}{2:X2}' -f $Rgb[0], $Rgb[1], $Rgb[2]) }

function Get-MaxChannelDelta($A, $B) {
    $d = 0
    for ($i = 0; $i -lt 3; $i++) { $d = [Math]::Max($d, [Math]::Abs($A[$i] - $B[$i])) }
    return $d
}

# Rec. 709 relative luminance, used only to compare two samples of the SAME
# surface: which way it moved, never how legible it is (that is
# lib/contrast.ps1's job and it does the sRGB linearisation properly).
function Get-Luminance($Rgb) {
    return 0.2126 * $Rgb[0] + 0.7152 * $Rgb[1] + 0.0722 * $Rgb[2]
}

# The highlight crosses on TabSwitcherShape.HighlightMs and the card enters
# on EnterMs. A capture taken before either settles reads the previous
# frame, which is a stale verdict that happens to look like a real one -- the
# first draft of this harness passed the moves leg on exactly that. Well
# clear of both, and well inside the popup's own 1.2s dismissal so the read
# still happens while it is up.
$SettleMs = 300

function Add-Row($Leg, $What, $Detail, $Delta, $Pass, $Floor = $MinDelta) {
    $script:Rows.Add([pscustomobject][ordered]@{
        leg = $Leg; what = $What; detail = $Detail
        delta = $Delta; threshold = $Floor; pass = $Pass
    })
}

function Add-Finding([string]$Text) {
    $script:Findings.Add($Text)
    Write-Host "  FAIL $Text"
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
            $owner = [SwitcherWin]::PidAt($px, $py)
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

# A seam rect, sampled out of the capture. The inset drops rounded corners
# and any 1px stroke at the edge, neither of which is the fill being asked
# about.
function Measure-Rect($Cap, $Rect, [string]$Label, [int]$Inset = 3) {
    if ($null -eq $Rect) { throw "HARNESS: the seam reported no rect for '$Label'" }
    $x = [int]$Rect.x - $Cap.L + $Inset
    $y = [int]$Rect.y - $Cap.T + $Inset
    $w = [int]$Rect.w - 2 * $Inset
    $h = [int]$Rect.h - 2 * $Inset
    if ($w -le 2 -or $h -le 2) { throw "HARNESS: the rect reported for '$Label' is too small to sample" }
    $s = [ContrastSampler]::Flat($Cap.Bmp, $x, $y, $w, $h)
    if (-not $s.Ok) { throw ("HARNESS: could not sample '{0}': {1}" -f $Label, $s.Why) }
    return @($s.BgR, $s.BgG, $s.BgB)
}

# Raise the popup and read the card back while it is still up: it dismisses
# itself on a 1.2s timer, so the read has to follow the cycle immediately.
function Get-SwitcherCells($Session) {
    [void](Invoke-SeamCommand $Session @{ op = 'cycle'; forward = $true })
    # Settle BEFORE the read, not just before the capture. The rects come
    # from TransformToVisual, which includes the card's own ScaleTransform,
    # so a rect read mid-highlight belongs to a card still carrying the 1.05
    # lift it is on its way out of -- a region a few pixels wider on every
    # side than what the later capture actually paints. The plurality
    # sampler absorbed the overhang, so this was flakiness rather than a
    # false pass, but reading and capturing on two different frames of the
    # same transition has no defence worth keeping.
    Start-Sleep -Milliseconds $SettleMs
    return Invoke-SeamCommand $Session @{ op = 'switcher-cells' }
}

if (-not (Test-Path $ExePath)) {
    Write-Host "HARNESS: missing exe: $ExePath"
    exit 1
}

$exit = 0
$session = $null
try {
    # Minimal on purpose: single-instance off so this runs beside somebody
    # else's Wintty, and no saved geometry from a previous run.
    $config = @'
windows-single-instance = false
window-save-state = never
'@
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $config
    [void][SwitcherWin]::PlaceOnTop([SeamWin]::P($session.Hwnd64), $WinX, $WinY, $WinW, $WinH)
    Start-Sleep -Milliseconds 1200

    [void](Invoke-SeamCommand $session @{
        op = 'seed-tabs'; count = 5
        titles = @('alpha', 'bravo', 'charlie', 'delta', 'echo') })
    # A run of three in the middle: tabs outside it on both sides, so a field
    # that leaked one cell either way is visible as a wrong cell count.
    [void](Invoke-SeamCommand $session @{ op = 'group'; indices = @(1, 2, 3) })

    # ---------------- structure ----------------
    Write-Host '-- structure: the seam''s reading of the card'
    $first = Get-SwitcherCells $session
    $cells = @($first.cells)
    $grouped = @($cells | Where-Object { $null -ne $_.group })
    $ungrouped = @($cells | Where-Object { $null -eq $_.group })
    $heads = @($grouped | Where-Object { $_.head })
    $tails = @($grouped | Where-Object { $_.tail })

    $structureOk = ($grouped.Count -eq 3) -and ($heads.Count -eq 1) -and ($tails.Count -eq 1) -and
                   ($ungrouped.Count -eq 2) -and
                   (@($grouped | Select-Object -ExpandProperty group -Unique).Count -eq 1)
    Add-Row 'structure' 'one field over the run' (
        '{0} grouped / {1} ungrouped / {2} head / {3} tail' -f
            $grouped.Count, $ungrouped.Count, $heads.Count, $tails.Count) $null $structureOk
    if (-not $structureOk) {
        Add-Finding ("the card does not read as one field over a three-tab run: {0} grouped cells, {1} ungrouped, {2} heads, {3} tails" -f
            $grouped.Count, $ungrouped.Count, $heads.Count, $tails.Count)
    }
    # Every grouped cell must carry a header band rect, or there is nothing
    # for the field leg below to sample and nothing on screen either.
    $bandless = @($cells | Where-Object { $null -eq $_.header })
    Add-Row 'structure' 'every slot reserves the header band' (
        '{0} of {1} slots without a band' -f $bandless.Count, $cells.Count) $null ($bandless.Count -eq 0)
    if ($bandless.Count -gt 0) {
        Add-Finding ("{0} of {1} slots reserve no header band, so a run's tiles cannot share a baseline" -f
            $bandless.Count, $cells.Count)
    }

    # ---------------- field ----------------
    Write-Host '-- field: the wash paints, and it is not the card ground'
    $cap = New-WindowCapture $session.Hwnd64 ([uint32]$session.Proc.Id) 'switcher-field'
    try {
        # Every tile's preview, before the step. The moves leg compares each
        # tile with ITSELF one step later, which is what keeps the field wash
        # and any preset tint out of the comparison entirely.
        $script:PreviewBefore = @{}
        foreach ($cell in $cells) {
            if ($cell.kind -ne 'tile') { continue }
            $script:PreviewBefore[$cell.title] = Measure-Rect $cap $cell.preview ("preview " + $cell.title)
        }

        if ($heads.Count -ge 1 -and $ungrouped.Count -ge 1) {
            $fieldBand = Measure-Rect $cap $heads[0].header 'field header band'
            $plainBand = Measure-Rect $cap $ungrouped[0].header 'ungrouped header band'
            $delta = Get-MaxChannelDelta $fieldBand $plainBand
            $ok = $delta -gt $MinDelta
            Write-Host ("  field band {0} vs ungrouped band {1}, delta {2} (> {3} required)" -f
                (Format-Rgb $fieldBand), (Format-Rgb $plainBand), $delta, $MinDelta)
            Add-Row 'field' 'the wash differs from the card ground' (
                '{0} vs {1}' -f (Format-Rgb $fieldBand), (Format-Rgb $plainBand)) $delta $ok
            if (-not $ok) {
                Add-Finding ("the group field paints the same colour as an ungrouped slot ({0} vs {1}, delta {2}): the field is invisible" -f
                    (Format-Rgb $fieldBand), (Format-Rgb $plainBand), $delta)
            }
        } else {
            throw 'HARNESS: the card has no head cell and no ungrouped cell to compare it against'
        }

        # ---------------- active ----------------
        Write-Host '-- active: exactly one cell is lit, and it is the manager''s'
        $active = @($cells | Where-Object { $_.active })
        $singleActive = $active.Count -eq 1
        Add-Row 'active' 'exactly one cell is the selection' ('{0} active' -f $active.Count) $null $singleActive
        if (-not $singleActive) {
            Add-Finding ("{0} cells report active; the popup's selection is not a single cell" -f $active.Count)
        }
        if ($singleActive) {
            # The manager's own answer, off the state block every seam
            # response carries: `active` is a manager INDEX into `tabs`.
            $managerActive = (@($first.state.tabs)[[int]$first.state.active]).title
            $titleOk = $active[0].title -eq $managerActive
            Add-Row 'active' 'the lit cell is the manager''s active tab' (
                'popup "{0}" vs manager "{1}"' -f $active[0].title, $managerActive) $null $titleOk
            if (-not $titleOk) {
                Add-Finding ("the popup lights '{0}' while the manager's active tab is '{1}'" -f
                    $active[0].title, $managerActive)
            }
        }

        # Like with like. A grouped tile's card sits on the field's wash and
        # an ungrouped one's does not, so comparing a lit ungrouped tile
        # against an idle GROUPED one measures the field as much as the dim
        # and would pass a build with no dim at all. The idle tile is picked
        # to share the active one's field status, which leaves the dim as
        # the only difference between them.
        $idle = @($cells | Where-Object {
            -not $_.active -and $_.kind -eq 'tile' -and $_.group -eq $active[0].group })
        if ($singleActive -and $active[0].kind -eq 'tile' -and $idle.Count -ge 1) {
            $litRgb = Measure-Rect $cap $active[0].preview 'active preview'
            $dimRgb = Measure-Rect $cap $idle[0].preview 'idle preview'
            $delta = Get-MaxChannelDelta $litRgb $dimRgb
            $ok = $delta -gt $MinDimDelta
            Write-Host ("  active '{0}' {1} vs idle '{2}' {3} (same field), delta {4} (> {5} required)" -f
                $active[0].title, (Format-Rgb $litRgb), $idle[0].title, (Format-Rgb $dimRgb),
                $delta, $MinDimDelta)
            Add-Row 'active' 'the lit tile out-paints an idle one in the same field' (
                '{0} {1} vs {2} {3}' -f $active[0].title, (Format-Rgb $litRgb),
                    $idle[0].title, (Format-Rgb $dimRgb)) $delta $ok $MinDimDelta
            if (-not $ok) {
                Add-Finding ("the active tile '{0}' and the idle tile '{1}' paint the same ({2} vs {3}, delta {4}): nothing on screen says which tab the cycle is on" -f
                    $active[0].title, $idle[0].title, (Format-Rgb $litRgb), (Format-Rgb $dimRgb), $delta)
            }
            $script:LitTitle = $active[0].title
            $script:LitBefore = $litRgb
            $script:DimTitle = $idle[0].title
        } else {
            throw 'HARNESS: the card has no lit tile and no idle tile sharing its field'
        }
    } finally { $cap.Bmp.Dispose() }

    # ---------------- moves ----------------
    # The leg that stops an always-tile-0 oracle, and the polarity with it.
    # Each of the two tiles is compared with ITSELF one step earlier, so no
    # field wash or preset tint enters the comparison: the tile that lost the
    # selection has to have changed, the tile that gained it has to have
    # changed, and the two changes have to go in OPPOSITE directions along
    # luminance. Which direction is which is a theme question -- a card is
    # lighter than the acrylic behind it on one theme and darker on the other
    # -- so the assertion is that they diverge, not that one goes up.
    Write-Host '-- moves: the highlight follows the cycle'
    $second = Get-SwitcherCells $session
    $secondCells = @($second.cells)
    $nowActive = @($secondCells | Where-Object { $_.active })
    $moved = ($nowActive.Count -eq 1) -and ($nowActive[0].title -ne $script:LitTitle)
    Add-Row 'moves' 'the selection is on a different tile' (
        'was "{0}", now "{1}"' -f $script:LitTitle, $(if ($nowActive.Count -eq 1) { $nowActive[0].title } else { '?' })
        ) $null $moved
    if (-not $moved) {
        Add-Finding ("one more cycle step left the selection on '{0}': the highlight does not follow the cycle" -f $script:LitTitle)
    }

    $cap2 = New-WindowCapture $session.Hwnd64 ([uint32]$session.Proc.Id) 'switcher-moved'
    try {
        # The popup must still be up, or the "after" sample is terminal
        # content and every number below is fiction.
        $stillUp = Invoke-SeamCommand $session @{ op = 'switcher-cells' }
        if (-not $stillUp.ok) { throw 'HARNESS: the popup dismissed before the second capture' }

        $wasLit = @($secondCells | Where-Object { $_.title -eq $script:LitTitle -and $_.kind -eq 'tile' })
        if ($moved -and $wasLit.Count -eq 1 -and $nowActive[0].kind -eq 'tile') {
            $loserAfter = Measure-Rect $cap2 $wasLit[0].preview 'newly dimmed preview'
            $loserDelta = Get-MaxChannelDelta $loserAfter $script:LitBefore
            $loserOk = $loserDelta -gt $MinDimDelta
            Write-Host ("  '{0}' was {1} lit, now {2}, delta {3} (> {4} required)" -f
                $script:LitTitle, (Format-Rgb $script:LitBefore), (Format-Rgb $loserAfter),
                $loserDelta, $MinDimDelta)
            Add-Row 'moves' 'the tile that lost the selection dimmed' (
                '{0}: {1} -> {2}' -f $script:LitTitle, (Format-Rgb $script:LitBefore),
                    (Format-Rgb $loserAfter)) $loserDelta $loserOk $MinDimDelta
            if (-not $loserOk) {
                Add-Finding ("'{0}' paints the same after losing the selection ({1} -> {2}, delta {3}): the highlight did not leave it" -f
                    $script:LitTitle, (Format-Rgb $script:LitBefore), (Format-Rgb $loserAfter), $loserDelta)
            }

            # The winner is measured against ITSELF in the first capture, so
            # the "before" has to be re-read from that capture's cell -- which
            # is why the first reading's rects are kept.
            $winnerBefore = $script:PreviewBefore[$nowActive[0].title]
            if ($null -eq $winnerBefore) {
                throw ("HARNESS: no first-capture sample for '{0}'" -f $nowActive[0].title)
            }
            $winnerAfter = Measure-Rect $cap2 $nowActive[0].preview 'newly lit preview'
            $winnerDelta = Get-MaxChannelDelta $winnerAfter $winnerBefore
            $winnerOk = $winnerDelta -gt $MinDimDelta
            Write-Host ("  '{0}' was {1} idle, now {2}, delta {3} (> {4} required)" -f
                $nowActive[0].title, (Format-Rgb $winnerBefore), (Format-Rgb $winnerAfter),
                $winnerDelta, $MinDimDelta)
            Add-Row 'moves' 'the tile that took the selection brightened' (
                '{0}: {1} -> {2}' -f $nowActive[0].title, (Format-Rgb $winnerBefore),
                    (Format-Rgb $winnerAfter)) $winnerDelta $winnerOk $MinDimDelta
            if (-not $winnerOk) {
                Add-Finding ("'{0}' paints the same after taking the selection ({1} -> {2}, delta {3}): the highlight did not arrive on it" -f
                    $nowActive[0].title, (Format-Rgb $winnerBefore), (Format-Rgb $winnerAfter), $winnerDelta)
            }

            # Opposite directions. Both tiles moving the same way is a
            # repaint of the whole card, not a selection moving between two
            # of its tiles -- which is exactly what a stale frame captured
            # mid-animation looks like.
            $loserWay = [Math]::Sign((Get-Luminance $loserAfter) - (Get-Luminance $script:LitBefore))
            $winnerWay = [Math]::Sign((Get-Luminance $winnerAfter) - (Get-Luminance $winnerBefore))
            $opposed = ($loserWay -ne 0) -and ($winnerWay -eq -$loserWay)
            Write-Host ("  directions: '{0}' {1}, '{2}' {3}" -f
                $script:LitTitle, $loserWay, $nowActive[0].title, $winnerWay)
            Add-Row 'moves' 'the two tiles moved in opposite directions' (
                '{0} {1} / {2} {3}' -f $script:LitTitle, $loserWay, $nowActive[0].title, $winnerWay
                ) $null $opposed
            if (-not $opposed) {
                Add-Finding ("'{0}' and '{1}' both changed the same way ({2} and {3}): the card repainted, but the selection did not move between them" -f
                    $script:LitTitle, $nowActive[0].title, $loserWay, $winnerWay)
            }
            $script:LoserAfter = $loserAfter
            $script:SecondLitTitle = $nowActive[0].title
        } elseif ($moved) {
            throw 'HARNESS: could not find the previously lit tile in the second reading'
        }
    } finally { $cap2.Bmp.Dispose() }

    # ---------------- stays ----------------
    # A THIRD step, and the leg exists because two were not enough.
    #
    # The highlight is a Storyboard, and a Storyboard that is stopped puts
    # every property it animated back to that property's BASE value. The
    # cards are born with the idle values as their base and the first
    # selection is written as a base too, so the first two steps look
    # perfect: step one animates A down and B up, step two stops that clock
    # -- reverting A to the base it was BORN with, which for the tile that
    # was lit at build time is LIT.
    #
    # So the tile the user started on comes back at full opacity and stays
    # there, lifted, beside the real selection, for every step after. Two
    # cycles cannot see it: the reversion needs a stop, and the first stop
    # with anything to revert is the third press. This leg is the third
    # press.
    Write-Host '-- stays: a tile that was dimmed two steps ago is still dim'
    if ($null -ne $script:LoserAfter) {
        $third = Get-SwitcherCells $session
        $thirdCells = @($third.cells)
        $cap3 = New-WindowCapture $session.Hwnd64 ([uint32]$session.Proc.Id) 'switcher-third'
        try {
            $stillUp3 = Invoke-SeamCommand $session @{ op = 'switcher-cells' }
            if (-not $stillUp3.ok) { throw 'HARNESS: the popup dismissed before the third capture' }

            $firstTile = @($thirdCells | Where-Object {
                $_.title -eq $script:LitTitle -and $_.kind -eq 'tile' })
            $nowActive3 = @($thirdCells | Where-Object { $_.active })
            if ($firstTile.Count -ne 1 -or $nowActive3.Count -ne 1) {
                throw ("HARNESS: the third reading has {0} copies of '{1}' and {2} active cells" -f
                    $firstTile.Count, $script:LitTitle, $nowActive3.Count)
            }
            # The selection must have left it -- otherwise "still dim" is a
            # claim about a tile that is legitimately lit.
            if ($nowActive3[0].title -eq $script:LitTitle) {
                throw ("HARNESS: three steps brought the selection back to '{0}'; the run is too short to ask this" -f
                    $script:LitTitle)
            }

            # Against its DIMMED reading, not against its lit one: the
            # question is whether it stayed where the second step put it.
            $firstAfter = Measure-Rect $cap3 $firstTile[0].preview 'first tile, two steps on'
            $drift = Get-MaxChannelDelta $firstAfter $script:LoserAfter
            $stayed = $drift -le $MinDimDelta
            Write-Host ("  '{0}' was dimmed to {1}, two steps on it is {2}, drift {3} (<= {4} required)" -f
                $script:LitTitle, (Format-Rgb $script:LoserAfter), (Format-Rgb $firstAfter),
                $drift, $MinDimDelta)
            Add-Row 'stays' 'a tile the cycle left stays left' (
                '{0}: {1} -> {2}' -f $script:LitTitle, (Format-Rgb $script:LoserAfter),
                    (Format-Rgb $firstAfter)) $drift $stayed $MinDimDelta
            if (-not $stayed) {
                Add-Finding ("'{0}' was dimmed two steps ago and has repainted since ({1} -> {2}, drift {3}): the card is showing more than one tile as the selection" -f
                    $script:LitTitle, (Format-Rgb $script:LoserAfter), (Format-Rgb $firstAfter), $drift)
            }
        } finally { $cap3.Bmp.Dispose() }
    } else {
        throw 'HARNESS: the moves leg did not run, so there is no dimmed reading to compare against'
    }

    if ($script:Findings.Count -gt 0) { $exit = 2 }
} catch {
    Write-Host ("HARNESS: {0}" -f $_.Exception.Message)
    Add-Row 'harness' 'could not run' $_.Exception.Message $null $null
    $exit = 1
} finally {
    if ($session) { Stop-SeamSession $session }
}

@{
    what = 'the Ctrl+Tab switcher must show which tiles belong to a group, and which tile the cycle is on'
    instrument = 'the switcher-cells seam op for the card''s plan, and rendered pixels for what it paints'
    rows = $script:Rows
    findings = $script:Findings
    exit = $exit
} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir 'switcher-groups.json') -Encoding utf8

Write-Host ''
if ($exit -eq 0) { Write-Host 'PASS: the field paints, one cell is lit, and the light moves with the cycle' }
elseif ($exit -eq 2) {
    Write-Host 'FINDINGS:'
    foreach ($f in $script:Findings) { Write-Host "  - $f" }
}
exit $exit
