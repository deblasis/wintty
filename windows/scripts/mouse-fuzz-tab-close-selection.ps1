#requires -Version 7
<#
    Randomized close against both tab strips, checking that the tab the user
    was on stays the tab the strip says it is on AND the tab the strip paints
    as the field.

    The defect this was written for: with vertical tabs, closing a row ABOVE
    the active one left the selected-row fill on the slot the closed tab
    vacated. The logical selection was right the whole time - UIA reported the
    correct tab selected - so an oracle that only asks UIA would have passed a
    build with the fill a row out of place. That is why there are two checks
    per close and why the second one reads pixels.

    Identity is the tab's title. Every tab here runs the same shell, so every
    tab would otherwise arrive with the same title and the strip would name
    them all alike; the seam's seed gives each one an override title of its
    own before anything is measured. A title belongs to the tab, follows it
    across a reflow, and is what the strip publishes as the accessible name.

    Not the UIA RuntimeId, which is what this used to match on. That is the id
    of the item CONTAINER, and nothing promises a container stays with a tab
    across a reflow: a strip is free to hand a container to whichever tab
    lands in its slot, and then the id of the tab that was active is found
    alive on the strip belonging to a different tab, and the oracle reports a
    move that never happened. The old setup control could not see that either
    way, because it removed the LAST row with the FIRST one active and nothing
    shifted. The control below removes a row that DOES shift the rest: it
    requires the titles to survive that, and it measures the RuntimeIds over
    the same close and reports what it found instead of asserting on it. Every
    round reports the same measurement as idDrift, so a build where the ids do
    start following slots says so in the log rather than in a verdict.

    Both checks read the strip only once it has stopped changing after the
    close. A removal takes several frames, and a selection sampled inside one
    describes a strip that has not finished deciding - which is not a defect,
    and reporting it as one costs a person an afternoon. Settling is on the
    strip holding still, never on it holding the expected answer, so a
    selection that lands wrong and stays wrong is still a finding. A strip
    that never holds still is exit 1, because a read taken off one is not a
    claim about the build either way.

    Both layouts get their own launch with their own config rather than a
    runtime toggle: the switch is animated and has its own harness (morph), and
    a second process is cheaper to reason about than a settled animation.

    Actuation: the in-process test seam, and nothing else. seed-tabs builds the
    corpus, select moves the active tab, close removes one - each of them the
    manager op the click and the chord funnel into. Zero OS input is
    synthesized: nothing is typed, no pointer is moved, no foreground is taken,
    so the machine stays usable while this runs and a keystroke meant for the
    strip can no longer land in whatever the owner was doing. The window is
    raised without activation, which is all a screen capture needs.

    One seed per process, and the tab count falls from there. Repeated
    seed-tabs churn in one process trips a filed access violation in coreclr
    around the seventh cumulative seed (see lib/seam-client.ps1), so a round
    does not rebuild the strip - it closes into the strip the previous round
    left, and the leg is sized to end with a selected row and at least one
    unselected one still standing.

    Elements are located by AutomationId ('NavView', 'TabViewControl') and by
    control type, never by being the first of their type on screen. The strip
    is READ over UIA and over pixels; it is never driven through either.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$Seed = 1337
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots') | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# The four facts a capture needs that lib/seam-client.ps1 does not carry: the
# client origin in screen pixels (the seam speaks window-root DIPs), who owns
# the pixels at a point, where the pointer is resting, and a raise. Reads and
# one window placement; no SetCursorPos, no SendInput, no input-queue attach.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class TcsWin {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);

    public static POINT ClientOrigin(long hwnd) {
        var p = new POINT();
        ClientToScreen(new IntPtr(hwnd), ref p);
        return p;
    }

    public static POINT Cursor() { POINT p; GetCursorPos(out p); return p; }

    // Place and raise WITHOUT activating: the capture needs this window's
    // pixels to be the topmost ones, it does not need the keyboard, and
    // taking the keyboard would yank the caret from whoever is at the box.
    public static bool PlaceOnTop(long hwnd, int x, int y, int w, int hh) {
        return SetWindowPos(new IntPtr(hwnd), new IntPtr(-1), x, y, w, hh, 0x0010 /* SWP_NOACTIVATE */);
    }

    // Which process owns the topmost window at a screen point. A capture reads
    // whatever is on screen, so an occluding window would otherwise be
    // measured as if it were the product.
    public static uint PidAt(int x, int y) {
        var h = WindowFromPoint(new POINT { X = x, Y = y });
        if (h == IntPtr.Zero) return 0;
        uint pid;
        GetWindowThreadProcessId(GetAncestor(h, 2 /* GA_ROOT */), out pid);
        return pid;
    }
}
'@ -ErrorAction SilentlyContinue

# Mixed-DPI discipline: UIA rects, the seam's converted rects and
# CopyFromScreen must all land in one coordinate space, or a probe point lands
# next to the row it named (-4 = PER_MONITOR_AWARE_V2).
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$UIA = [System.Windows.Automation.AutomationElement]
$TREE = [System.Windows.Automation.TreeScope]::Descendants
$CTRL = [System.Windows.Automation.ControlType]

<#
    The two boundaries the fill oracle decides on, in max-channel distance
    from the terminal's own ground.

    The claim is directional now, not relative: the ACTIVE tab is painted the
    terminal background so it reads as one surface with the pane it belongs
    to, and nothing else on the strip is. The strip's own ground is
    ShiftBrightness(bg, fg, 0.05) - five percent of the palette's contrast, so
    on Catppuccin Mocha (#1E1E2E over #CDD6F4) the two are nine points apart
    on the widest channel. That is the whole distance this instrument has to
    work in, which is why both numbers are small and why the old "differs by
    at least 20 from every other row" test cannot be rescued by lowering its
    threshold: at 20 it fails every scenario, and at anything under nine it
    stops being a claim about which row is the field.

    FieldMatch is deliberately tight. Both surfaces are opaque brushes over an
    opaque chrome - window-theme=wintty resolves the frame to a solid fill, so
    nothing composites into either sample - and two captures of one unchanged
    opaque surface agree exactly. Three points is capture noise plus rounding,
    not a colour difference.

    FieldApart is the other side, and it is a real margin rather than
    FieldMatch+1 because a row that is neither is a fact worth separating: at
    four or five points from the terminal ground a row is not the field and is
    not the strip either, and calling it either way would be inventing an
    answer. A measurement in that band is a HARVEST_MISS - the instrument
    could not decide - and NOT a finding, because the bug this exists to catch
    does not live there. A fill left behind on a vacated slot is the field's
    own brush on the wrong row: it reads zero from the terminal ground, deep
    inside "matches", and a build that painted EVERY row the terminal ground
    reads zero on all of them and fails on the unselected ones.
#>
$FieldMatch = 3
$FieldApart = 6

# How flat the terminal region has to be before its mean is called "the
# ground". Cells the shell has not written are one colour; a region that
# spreads wider than this has text, a cursor or somebody else's window in it,
# and its mean is not the background of anything.
$GroundFlat = 3

# How far the whole capture, coarsely sampled, has to spread. See
# Assert-CaptureAlive.
$CaptureAlive = 24

# ---- UIA: reading the strip -------------------------------------------------

function Get-UiaRoot([int64]$Hwnd64) {
    return $UIA::FromHandle([SeamWin]::P($Hwnd64))
}

function Find-ById($root, [string]$Id) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        $UIA::AutomationIdProperty, $Id)
    return $root.FindFirst($TREE, $cond)
}

function Get-RuntimeKey($el) {
    return (($el.GetRuntimeId()) -join '.')
}

# One row per tab, ordered the way the strip lays them out, each carrying the
# identity handle the oracle matches on.
function Get-TabRows($root, [bool]$Vertical) {
    $hostId = if ($Vertical) { 'NavView' } else { 'TabViewControl' }
    $stripEl = Find-ById $root $hostId
    if ($null -eq $stripEl) { throw "HARVEST_MISS: no strip with AutomationId $hostId" }

    $ct = if ($Vertical) { $CTRL::ListItem } else { $CTRL::TabItem }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        $UIA::ControlTypeProperty, $ct)
    $found = $stripEl.FindAll($TREE, $cond)

    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($el in $found) {
        $r = $el.Current.BoundingRectangle
        if ($r.Width -le 0 -or $r.Height -le 0) { continue }
        $selected = $null
        try {
            $pat = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $selected = [bool]$pat.Current.IsSelected
        } catch { $selected = $null }
        $rows.Add([pscustomobject]@{
            El       = $el
            Key      = Get-RuntimeKey $el
            Name     = $el.Current.Name
            Rect     = $r
            Selected = $selected
        })
    }
    if ($rows.Count -eq 0) { throw "HARVEST_MISS: no tab items under $hostId" }
    if ($rows | Where-Object { $null -eq $_.Selected }) {
        throw 'HARVEST_MISS: a tab item exposes no SelectionItem pattern, so selection cannot be read'
    }
    $sorted = if ($Vertical) { $rows | Sort-Object { $_.Rect.Y } } else { $rows | Sort-Object { $_.Rect.X } }
    return @($sorted)
}

# Identity is only identity while it is unique. A duplicate means the seeding
# did not take, which makes every verdict below meaningless, so it is exit 1
# (the corpus could not be established) and not a finding.
function Assert-DistinctTitles($rows, [string]$Where) {
    $dupes = @($rows | Group-Object Name | Where-Object { $_.Count -gt 1 })
    if ($dupes.Count -gt 0) {
        throw ("HARVEST_MISS: $Where has $($dupes[0].Count) tabs titled '$($dupes[0].Name)', " +
               'so a title cannot stand for tab identity')
    }
}

<#
    The bridge between what the strip DRAWS and what the seam ACTS on.

    Seam ops take a manager index; the rows above are whatever the strip laid
    out, in the order it laid them out. Matching the two on the title is what
    lets the harness say "close the row above the active one" and have the op
    reach that tab and no other.

    A row the manager does not know by that name, or a manager tab the strip
    did not draw, is exit 1 rather than a guess: both mean the two views have
    come apart, and an index picked out of a strip that no longer matches the
    manager would close a tab nobody chose.
#>
function Get-SeamIndexByTitle($State, $Rows, [string]$Where) {
    $byTitle = @{}
    foreach ($tab in $State.tabs) { $byTitle[[string]$tab.title] = [int]$tab.index }
    if ($Rows.Count -ne @($State.tabs).Count) {
        throw ("HARVEST_MISS: $Where the strip drew $($Rows.Count) rows for " +
               "$(@($State.tabs).Count) tabs, so a row cannot be matched to the tab it stands for")
    }
    foreach ($row in $Rows) {
        if (-not $byTitle.ContainsKey($row.Name)) {
            throw ("HARVEST_MISS: $Where the strip drew a row named '$($row.Name)' that the " +
                   "manager does not hold (it holds $(($State.tabs.title) -join ', '))")
        }
    }
    return $byTitle
}

<#
    Wait for the strip to stop moving after a close, and say how long it took.

    A close is not instant: the item leaves the collection, MUXC re-arranges
    the pane, and the app puts the manager's active tab back on the item that
    now holds it. The seam's close op already waits its own dispatcher turn
    out, which is where the model settles; this waits for the PAINTED strip,
    which is a different clock and the one both oracles read.

    Stop MOVING, not become correct. The loop exits on two consecutive reads
    that agree, whatever they say, so a strip that settles on the wrong tab
    still fails every check below; only the transit is skipped. Waiting for
    the expected answer instead would be an oracle that cannot fail.

    Row geometry is part of holding still, not just names and selection. The
    rects in the settling read are the probe points the paint check samples,
    and both strips drop the closed item from the collection before the
    survivors finish sliding up - so a read that agrees on names alone can
    hand the paint check the coordinates of a slot a row has already left, and
    a probe landing on a row boundary reads as a row painted neither way. That
    is the harness manufacturing the finding this harness exists to catch.

    A strip that never stops changing is exit 1 and not a verdict. Returning
    that read and asserting on it anyway lets a busy machine report a defect
    in the build, which is the failure this whole helper is here to remove.

    What the transit held is not thrown away. TransientOff says whether the
    tab that was active was ever not the selected row while the strip was
    still moving. It is recorded per round, never asserted on: a selection
    that is wrong mid-removal and right at rest is not something this harness
    can tell from a repaint, but a round carrying transientOff with no verdict
    is where to look if that ever stops being true.
#>
function Wait-StripSettled([int64]$Hwnd64, [bool]$Vertical, [string]$ExpectedName) {
    $prev = $null
    $transientOff = $false
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 5000) {
        $rows = Get-TabRows (Get-UiaRoot $Hwnd64) $Vertical
        $shape = (($rows | ForEach-Object {
            "$($_.Name)=$($_.Selected)@$([int]$_.Rect.X),$([int]$_.Rect.Y)"
        }) -join '|')
        if ($shape -eq $prev) {
            return [pscustomobject]@{
                Rows         = $rows
                SettleMs     = [int]$sw.ElapsedMilliseconds
                TransientOff = $transientOff
            }
        }
        if ($ExpectedName) {
            $sel = @($rows | Where-Object { $_.Selected })
            if ($sel.Count -ne 1 -or $sel[0].Name -ne $ExpectedName) { $transientOff = $true }
        }
        $prev = $shape
        Start-Sleep -Milliseconds 200
    }
    throw ('HARVEST_MISS: the strip was still changing 5s after a close, so nothing read ' +
           'off it is a claim about the build')
}

# ---- pixels -----------------------------------------------------------------

function Get-WindowShot([int64]$Hwnd64) {
    $rc = [SeamWin]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw 'HARVEST_MISS: degenerate window rect' }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $g.Dispose()
    return [pscustomobject]@{ Bmp = $bmp; L = $rc.L; T = $rc.T }
}

function Get-PixelAt($shot, [int]$X, [int]$Y) {
    $px = $X - $shot.L; $py = $Y - $shot.T
    if ($px -lt 0 -or $py -lt 0 -or $px -ge $shot.Bmp.Width -or $py -ge $shot.Bmp.Height) { return $null }
    $c = $shot.Bmp.GetPixel($px, $py)
    return [pscustomobject]@{ R = [int]$c.R; G = [int]$c.G; B = [int]$c.B }
}

function Get-ColorDelta($a, $b) {
    if ($null -eq $a -or $null -eq $b) { return -1 }
    $dr = [Math]::Abs($a.R - $b.R); $dg = [Math]::Abs($a.G - $b.G); $db = [Math]::Abs($a.B - $b.B)
    return [Math]::Max($dr, [Math]::Max($dg, $db))
}

function Format-Rgb($c) { return ('#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B) }

# A capture with nothing in it would pass the fill oracle's "matches" arm on
# every row at once and be reported as the worst form of the vacated-slot bug.
# A real window carries a title row, terminal text and tab labels, so anything
# flatter than $CaptureAlive is the grab failing - a DWM transition frame, a
# blanked surface - and that is a harness miss, not a verdict.
function Assert-CaptureAlive($shot, [string]$Where) {
    $lo = @(255, 255, 255); $hi = @(0, 0, 0)
    for ($y = 4; $y -lt $shot.Bmp.Height; $y += 37) {
        for ($x = 4; $x -lt $shot.Bmp.Width; $x += 37) {
            $c = $shot.Bmp.GetPixel($x, $y)
            foreach ($i in 0, 1, 2) {
                $v = @([int]$c.R, [int]$c.G, [int]$c.B)[$i]
                if ($v -lt $lo[$i]) { $lo[$i] = $v }
                if ($v -gt $hi[$i]) { $hi[$i] = $v }
            }
        }
    }
    $spread = [Math]::Max($hi[0] - $lo[0], [Math]::Max($hi[1] - $lo[1], $hi[2] - $lo[2]))
    if ($spread -lt $CaptureAlive) {
        throw ("HARVEST_MISS: $Where the capture spreads only $spread across the whole window " +
               "(< $CaptureAlive), so it carries no picture and nothing sampled out of it means anything")
    }
    return $spread
}

# One seam DIP rect, landed on the screen the capture came off.
function Convert-SeamRect($Rect, $Origin, [double]$Scale) {
    return [pscustomobject]@{
        L = [int][Math]::Round($Origin.X + $Rect.x * $Scale)
        T = [int][Math]::Round($Origin.Y + $Rect.y * $Scale)
        W = [int][Math]::Round($Rect.w * $Scale)
        H = [int][Math]::Round($Rect.h * $Scale)
    }
}

<#
    The terminal's own background, read out of the same capture the rows are
    read out of.

    Sampled rather than parsed out of the theme, and that is a deliberate
    choice over the two alternatives. A literal (#1E1E2E) would be a second
    copy of a number the product owns. Parsing the config's `theme` line would
    be worse than a literal: Start-SeamSession points XDG_CONFIG_HOME at an
    isolated directory, and once a config root exists the theme resolver
    searches only that root's own themes directories - so a bare
    `theme = Catppuccin Mocha` there may resolve to nothing at all and leave
    the built-in palette in force, and the harness would be asserting against
    a colour the app never used. The pixels under the terminal are the ground
    whatever resolved, which is exactly what the active tab has to match.

    Taken from the LOWER part of the pane, well below the shell's banner and
    its prompt, and required to be flat: cells nothing has written are one
    colour, and a region that is not one colour is not a background.
#>
function Measure-TerminalGround($shot, $LeafRect, [uint32]$ProcId, [string]$Where) {
    $inset = 14
    $top = [int]($LeafRect.T + $LeafRect.H * 0.55)
    $bottom = $LeafRect.T + $LeafRect.H - $inset
    $left = $LeafRect.L + $inset
    $right = $LeafRect.L + $LeafRect.W - $inset
    if ($right - $left -lt 80 -or $bottom - $top -lt 40) {
        throw "HARVEST_MISS: $Where the terminal pane is too small to read a background out of"
    }

    # Occlusion first, and before the flatness check below rather than after
    # it: another window over the pane is flat too, and a run that reported it
    # as "the terminal is carrying text" would send the reader to the shell.
    for ($y = $top; $y -lt $bottom; $y += [int](($bottom - $top) / 3)) {
        for ($x = $left; $x -lt $right; $x += [int](($right - $left) / 3)) {
            $owner = [TcsWin]::PidAt($x, $y)
            if ($owner -ne $ProcId) {
                throw ("HARVEST_MISS: $Where the point $x,$y inside the terminal belongs to " +
                       "pid $owner, not to the app under test (pid $ProcId)")
            }
        }
    }

    $sumR = 0; $sumG = 0; $sumB = 0; $n = 0
    $lo = @(255, 255, 255); $hi = @(0, 0, 0)
    for ($y = $top; $y -lt $bottom; $y += 11) {
        for ($x = $left; $x -lt $right; $x += 11) {
            $c = Get-PixelAt $shot $x $y
            if ($null -eq $c) { continue }
            $sumR += $c.R; $sumG += $c.G; $sumB += $c.B; $n++
            foreach ($i in 0, 1, 2) {
                $v = @($c.R, $c.G, $c.B)[$i]
                if ($v -lt $lo[$i]) { $lo[$i] = $v }
                if ($v -gt $hi[$i]) { $hi[$i] = $v }
            }
        }
    }
    if ($n -lt 60) {
        throw "HARVEST_MISS: $Where only $n terminal pixels landed inside the capture"
    }
    $spread = [Math]::Max($hi[0] - $lo[0], [Math]::Max($hi[1] - $lo[1], $hi[2] - $lo[2]))
    if ($spread -gt $GroundFlat) {
        throw ("HARVEST_MISS: $Where the terminal region spreads $spread across $n samples " +
               "(> $GroundFlat), so it is carrying text, a cursor or another window and its " +
               'mean is not the background')
    }

    return [pscustomobject]@{
        R = [int][Math]::Round($sumR / $n)
        G = [int][Math]::Round($sumG / $n)
        B = [int][Math]::Round($sumB / $n)
    }
}

# Where in a row's rect the fill is, and text and glyphs are not.
# Vertical: past the selection row's own 4-DIP left inset - scaled, because the
# inset is in DIPs and this rect is in screen pixels - and well left of the
# icon lane. Horizontal: horizontally centered, near the top of the handle,
# above the title baseline.
function Get-ProbePoint($row, [bool]$Vertical, [double]$Scale) {
    $r = $row.Rect
    if ($Vertical) {
        $inset = [int][Math]::Round(4 * $Scale) + 4
        return @([int]($r.X + $inset), [int]($r.Y + $r.Height / 2))
    }
    return @([int]($r.X + $r.Width / 2), [int]($r.Y + [Math]::Max(3.0, $r.Height * 0.2)))
}

<#
    The pointer is the one actuator this harness gave up and cannot replace.

    A row under the pointer wears the hover fill, and on the ACTIVE row that
    overlay sits on top of the field and moves it off the terminal ground -
    which this oracle would report as the field being painted on the wrong
    row. The old harness parked the pointer somewhere harmless with SendInput;
    moving the pointer moves the machine owner's pointer, so the replacement
    is to refuse. The band is the rows and a little around them, not the whole
    window, so a pointer resting anywhere else costs nothing.
#>
function Assert-PointerClear($rows, [string]$Where) {
    $p = [TcsWin]::Cursor()
    foreach ($row in $rows) {
        $r = $row.Rect
        if ($p.X -ge $r.X - 8 -and $p.X -le $r.X + $r.Width + 8 -and
            $p.Y -ge $r.Y - 8 -and $p.Y -le $r.Y + $r.Height + 8) {
            throw ("HARVEST_MISS: $Where the pointer is resting at $($p.X),$($p.Y), over row " +
                   "'$($row.Name)'; a hovered row wears the hover fill and would be read as a " +
                   'row painted the wrong way, and this harness may not move the pointer')
        }
    }
}

<#
    Read the fill under every row and decide whether the strip painted the
    field on the tab that holds it.

    Directional, and absolute in one term only. The active tab is painted the
    TERMINAL's ground so that it and the pane it belongs to read as one
    surface with no line between them; every other row is painted the strip's.
    So the claim is:

        the selected row's fill MATCHES the terminal background, and no
        unselected row's fill does.

    Both halves are load-bearing and they fail on different builds. Drop the
    first and a strip that paints nothing as the field passes. Drop the second
    and the defect this harness exists for - the field's own fill left behind
    on the slot a closed tab vacated - passes, because the row that kept it is
    only ever wrong relative to the terminal. The boundaries the two halves
    are decided on, and why they are two, are at $FieldMatch above.

    Anti-vacuity is at both ends. Zero rows never reaches here (Get-TabRows
    throws), a strip with no single selected row is a verdict rather than a
    skip, a row whose probe falls outside the capture is exit 1, and a
    measurement between the two boundaries is exit 1 with the number in it.
#>
function Test-SelectionFill($shot, $rows, [bool]$Vertical, [double]$Scale, $Ground, [uint32]$ProcId) {
    $selected = @($rows | Where-Object { $_.Selected })
    $others = @($rows | Where-Object { -not $_.Selected })
    if ($selected.Count -ne 1) {
        return "the strip paints $($selected.Count) rows as selected"
    }
    # One unselected row is enough for the claim - each row is measured
    # against the terminal, not against its peers - but none at all leaves
    # nothing that could carry a stranded fill, and calling that a pass would
    # be the harness reporting a check it did not make.
    if ($others.Count -lt 1) {
        throw 'HARVEST_MISS: the strip is down to one row, so a misplaced fill has nowhere to be'
    }

    $samples = @{}
    foreach ($row in $rows) {
        $pt = Get-ProbePoint $row $Vertical $Scale
        $owner = [TcsWin]::PidAt($pt[0], $pt[1])
        if ($owner -ne $ProcId) {
            throw ("HARVEST_MISS: the probe point $($pt[0]),$($pt[1]) on row '$($row.Name)' " +
                   "belongs to pid $owner, not to the app under test (pid $ProcId)")
        }
        $c = Get-PixelAt $shot $pt[0] $pt[1]
        if ($null -eq $c) { throw "HARVEST_MISS: probe point $($pt[0]),$($pt[1]) is outside the window" }
        $samples[$row.Key] = $c
    }

    # The band between the two boundaries is not a verdict either way, and
    # saying so beats guessing: a row four or five points off the terminal is
    # neither the field nor the strip, and the stranded fill this harness
    # hunts is the field's own brush and reads zero.
    foreach ($row in $rows) {
        $d = Get-ColorDelta $samples[$row.Key] $Ground
        if ($d -gt $FieldMatch -and $d -lt $FieldApart) {
            throw ("HARVEST_MISS: row '$($row.Name)' is painted $(Format-Rgb $samples[$row.Key]), " +
                   "$d from the terminal ground $(Format-Rgb $Ground) - neither the field " +
                   "(<= $FieldMatch) nor clearly off it (>= $FieldApart), so this instrument " +
                   'cannot say which the strip meant')
        }
    }

    $sel = $selected[0]
    $selDelta = Get-ColorDelta $samples[$sel.Key] $Ground
    if ($selDelta -gt $FieldMatch) {
        return ("the selected row '$($sel.Name)' is painted $(Format-Rgb $samples[$sel.Key]), " +
                "$selDelta off the terminal ground $(Format-Rgb $Ground) (> $FieldMatch): " +
                'the active tab is not carrying the field')
    }

    foreach ($o in $others) {
        $d = Get-ColorDelta $samples[$o.Key] $Ground
        if ($d -lt $FieldApart) {
            return ("unselected row '$($o.Name)' is painted $(Format-Rgb $samples[$o.Key]), only " +
                    "$d from the terminal ground $(Format-Rgb $Ground) (< $FieldApart): the field " +
                    "is on a row the strip does not report selected, while '$($sel.Name)' is")
        }
    }
    return $null
}

# ---- the run ----------------------------------------------------------------

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$rng = [System.Random]::new($Seed)
$findings = [System.Collections.Generic.List[object]]::new()
$rounds = [System.Collections.Generic.List[object]]::new()
$harnessErrors = [System.Collections.Generic.List[string]]::new()
# How many rows came back under another tab's container id in each layout's
# control close. Reported, not asserted on: it says what the strips do with
# their containers, which is why identity is matched on the title.
$script:IdDrift = [ordered]@{}

# Seeded once per process and then only ever closed into, because repeated
# seed-tabs churn in one process trips a filed access violation. Eight leaves
# the control close and five rounds room to land with a selected row and one
# unselected row still standing.
$TabTarget = 8
$RoundsPerLayout = 5

# Fixed geometry so a probe point that moves between runs is the product
# moving and not a window somebody dragged. Wide enough that eight tab handles
# stay laid out side by side rather than scrolling out of the strip, which
# Get-SeamIndexByTitle would refuse.
$WinX = 40; $WinY = 40; $WinW = 1400; $WinH = 900

# Titles are handed out once across the whole run and never reused. A tab
# seeded in the horizontal leg must not carry a name the vertical leg's
# oracle already made a claim about, or a stale UIA read would be taken for
# this leg's strip.
$script:TitleSeq = 0

function Get-HarnessConfig([bool]$Vertical) {
    $verticalLine = if ($Vertical) { 'vertical-tabs = true' } else { 'vertical-tabs = false' }
    # cmd is pinned because the terminal surface is now the harness's colour
    # reference: it has to be a shell that prints a couple of lines and then
    # stops, leaving the lower half of the pane as unwritten cells whose flat
    # colour is the background the active tab must match.
    return @"
command = cmd.exe
windows-single-instance = true
window-save-state = never
$verticalLine
vertical-tabs-hover-expand = false
window-theme = wintty
theme = Catppuccin Mocha
"@
}

function Invoke-Layout([bool]$Vertical) {
    $label = if ($Vertical) { 'vertical' } else { 'horizontal' }
    $session = $null
    try {
        $session = Start-SeamSession -ExePath $ExePath -ConfigText (Get-HarnessConfig $Vertical)
        $hwnd64 = [int64]$session.Hwnd64
        $pid32 = [uint32]$session.Proc.Id
        Write-Host "$label hwnd=$hwnd64 pid=$pid32"

        # Raised, never activated: the capture needs this window's pixels to
        # be the topmost ones, and taking the foreground would pull it off
        # whatever the owner of the machine is typing into. Nothing measured
        # below depends on activation - the shell theme paints the strip from
        # the palette, not from the window's active state.
        [void][TcsWin]::PlaceOnTop($hwnd64, $WinX, $WinY, $WinW, $WinH)
        Start-Sleep -Milliseconds 900

        $titles = @(1..$TabTarget | ForEach-Object {
            $script:TitleSeq++
            "fuzztab-$($script:TitleSeq)"
        })
        $state = (Invoke-SeamCommand $session @{
            op = 'seed-tabs'; count = $TabTarget; titles = $titles }).state

        if ($Vertical) {
            # A wide pane gives the fill probe room between the selection
            # row's left inset and the icon lane; the 48px compact rail does
            # not. The seam's toggle is the pane-pinned command the chevron
            # raises and its ack waits out the width tween, so the read below
            # is of the settled width.
            if ([double]$state.paneWidth -lt 120) {
                $state = (Invoke-SeamCommand $session @{ op = 'toggle-sidebar' }).state
            }
            if ([double]$state.paneWidth -lt 120) {
                throw ("HARVEST_MISS: the vertical pane stayed at $($state.paneWidth)px, so " +
                       'there is no room between the row inset and the icon lane to read a fill in')
            }
        }

        $scale = [double]$state.panes.scale
        $origin = [TcsWin]::ClientOrigin($hwnd64)

        # Establish the corpus the oracle measures against, using the case the
        # oracle actually asserts on: a removal that SHIFTS rows. The last row
        # is made active and the first one closed, so every survivor moves up
        # a slot.
        #
        # Titles are the manager's own override titles now rather than
        # something a shell reported, so what this control still asks is
        # whether the STRIP keeps publishing them as accessible names across a
        # reflow - which is the half identity is matched on. A build where it
        # does not gets exit 1 rather than a finding. Container RuntimeIds are
        # measured over the same close but only reported, because they are the
        # id of the slot's container and not of the tab that happens to be in
        # it.
        $before = Get-TabRows (Get-UiaRoot $hwnd64) $Vertical
        Assert-DistinctTitles $before 'the strip before the control close'
        $byTitle = Get-SeamIndexByTitle $state $before 'before the control close,'
        [void](Invoke-SeamCommand $session @{ op = 'select'; index = $byTitle[$before[-1].Name] })

        $before = Get-TabRows (Get-UiaRoot $hwnd64) $Vertical
        Assert-DistinctTitles $before 'the strip before the control close'
        $keyByTitle = @{}
        foreach ($r in $before) { $keyByTitle[$r.Name] = $r.Key }
        [void](Invoke-SeamCommand $session @{ op = 'close'; index = $byTitle[$before[0].Name] })

        # Through the same settle as the rounds. Read straight after the close
        # and a closing row still in the tree makes the title sets differ,
        # which lands as "titles did not survive a shifting removal" - a
        # HARVEST_MISS that blames the build for the harness reading early.
        $after = (Wait-StripSettled $hwnd64 $Vertical).Rows
        Assert-DistinctTitles $after 'the strip after the control close'
        $wantTitles = @($before | Select-Object -Skip 1 | ForEach-Object { $_.Name })
        $gotTitles = @($after | ForEach-Object { $_.Name })
        if (@(Compare-Object $wantTitles $gotTitles).Count -ne 0) {
            throw ('HARVEST_MISS: tab titles did not survive a shifting removal (wanted ' +
                   "$($wantTitles -join ', '); got $($gotTitles -join ', ')), " +
                   'so they cannot stand for tab identity')
        }

        $drifted = @($after | Where-Object { $keyByTitle[$_.Name] -ne $_.Key })
        $script:IdDrift[$label] = $drifted.Count
        if ($drifted.Count -eq 0) {
            Write-Host ("$label titles survive a shifting removal; container RuntimeIds " +
                        'happened to as well, but are still not what identity is matched on')
        }
        else {
            Write-Host ("$label titles survive a shifting removal; container RuntimeIds do NOT " +
                        "($($drifted.Count) of $($after.Count) rows came back under another " +
                        "tab's id), which is why identity is matched on the title")
        }

        for ($round = 0; $round -lt $RoundsPerLayout; $round++) {
            $rows = Get-TabRows (Get-UiaRoot $hwnd64) $Vertical
            # Three is the floor: after the close there has to be one selected
            # row and at least one unselected one, or the fill oracle has
            # nowhere for a stranded fill to be. The leg is sized to end
            # exactly here, so falling short means a close removed more than
            # it was asked to.
            if ($rows.Count -lt 3) {
                throw ("HARVEST_MISS: round $round found $($rows.Count) rows, and a close needs " +
                       'three to leave a selected row and an unselected one behind')
            }
            $state = (Invoke-SeamCommand $session @{ op = 'get-state' }).state
            Assert-DistinctTitles $rows "round $round before the close"
            $byTitle = Get-SeamIndexByTitle $state $rows "round $round before the close,"

            $keep = $rng.Next(0, $rows.Count)
            [void](Invoke-SeamCommand $session @{ op = 'select'; index = $byTitle[$rows[$keep].Name] })

            $rows = Get-TabRows (Get-UiaRoot $hwnd64) $Vertical
            # This read defines what "the tab that was active" means for the
            # whole round, so it has to be checked like the others: identity
            # is only identity while it is unique.
            Assert-DistinctTitles $rows "round $round after choosing the tab to keep"
            $expected = @($rows | Where-Object { $_.Selected })
            if ($expected.Count -ne 1) {
                throw "HARVEST_MISS: $($expected.Count) rows selected after asking for one"
            }
            $expectedName = $expected[0].Name

            $victims = @($rows | Where-Object { $_.Name -ne $expectedName })
            $victim = $victims[$rng.Next(0, $victims.Count)]
            $victimName = $victim.Name
            $victimAbove = ($rows.IndexOf($victim) -lt $rows.IndexOf($expected[0]))
            $keyByTitle = @{}
            foreach ($r in $rows) { $keyByTitle[$r.Name] = $r.Key }
            [void](Invoke-SeamCommand $session @{ op = 'close'; index = $byTitle[$victimName] })

            $settled = Wait-StripSettled $hwnd64 $Vertical $expectedName
            $rows = $settled.Rows
            Assert-DistinctTitles $rows "round $round after the close"
            # Recorded per round, not asserted on, so the log carries the
            # evidence for matching on the title: a round with drift above zero
            # is one where a container id would have named the wrong tab.
            $idDrift = @($rows | Where-Object { $keyByTitle[$_.Name] -ne $_.Key }).Count
            $stillThere = @($rows | Where-Object { $_.Name -eq $expectedName })
            $nowSelected = @($rows | Where-Object { $_.Selected })

            $verdicts = [System.Collections.Generic.List[string]]::new()
            if ($stillThere.Count -ne 1) {
                $verdicts.Add("the active tab '$expectedName' is gone after closing '$victimName'; " +
                              "the strip now holds $($rows.Name -join ', ')")
            }
            elseif ($nowSelected.Count -ne 1) {
                $verdicts.Add("$($nowSelected.Count) rows report themselves selected after closing " +
                              "'$victimName': $($nowSelected.Name -join ', ')")
            }
            elseif ($nowSelected[0].Name -ne $expectedName) {
                $verdicts.Add("selection moved off '$expectedName' onto '$($nowSelected[0].Name)' " +
                              "after closing '$victimName'")
            }

            Assert-PointerClear $rows "round ${round}:"
            $shot = Get-WindowShot $hwnd64
            $ground = $null
            try {
                [void](Assert-CaptureAlive $shot "round ${round}:")
                # The pane the active tab owns, straight off the drawing path,
                # so the terminal is sampled where it actually is rather than
                # where a fraction of the window guesses it might be.
                $paneState = (Invoke-SeamCommand $session @{ op = 'get-state' }).state
                $leaves = @($paneState.panes.leaves)
                $leafIndex = [int]$paneState.panes.activeLeaf
                if ($leafIndex -lt 0 -or $leafIndex -ge $leaves.Count) {
                    throw ("HARVEST_MISS: round ${round}: the active tab reports leaf $leafIndex " +
                           "of $($leaves.Count), so there is no pane to read the ground out of")
                }
                $leaf = Convert-SeamRect $leaves[$leafIndex] $origin $scale
                $ground = Measure-TerminalGround $shot $leaf $pid32 "round ${round}:"

                $paint = Test-SelectionFill $shot $rows $Vertical $scale $ground $pid32
                if ($paint) {
                    $verdicts.Add("the painted selection disagrees with the strip: $paint")
                    $shot.Bmp.Save((Join-Path $OutDir "shots\$label-round$round-paint.png"))
                }
            } finally { $shot.Bmp.Dispose() }

            $rounds.Add([pscustomobject]@{
                layout = $label; round = $round; kept = $expectedName
                closed = $victimName
                closedAbove = $victimAbove; remaining = $rows.Count
                idDrift = $idDrift; settleMs = $settled.SettleMs
                transientOff = $settled.TransientOff
                terminalGround = (Format-Rgb $ground)
                verdicts = @($verdicts)
            })
            foreach ($v in $verdicts) {
                $findings.Add("[$label round $round" + $(if ($victimAbove) { ', closed above' } else { ', closed below' }) + "] $v")
            }
            Write-Host ("$label round $round kept='$expectedName' closed='$victimName' " +
                        "closedAbove=$victimAbove remaining=$($rows.Count) " +
                        "ground=$(Format-Rgb $ground) " +
                        "idDrift=$idDrift settleMs=$($settled.SettleMs) " +
                        "transientOff=$($settled.TransientOff) " +
                        "verdicts=$($verdicts.Count)")
        }

        if ($session.Proc.HasExited) {
            throw "APP_EXIT: the app exited during the $label leg (code $($session.Proc.ExitCode))"
        }
    }
    finally {
        if ($null -ne $session) { Stop-SeamSession $session }
        Start-Sleep -Milliseconds 700
    }
}

if (-not (Test-Path $ExePath)) {
    Write-Host "HARVEST_MISS: missing exe: $ExePath"
    exit 1
}

Assert-NoWintty -Context 'The tab close selection harness'

# Each layout is its own process, so a leg that could not be run does not
# take the other's verdicts with it: a HARVEST_MISS is recorded and the next
# layout still reports what it measured.
foreach ($vertical in @($true, $false)) {
    try {
        Invoke-Layout $vertical
    }
    catch {
        $msg = "$($_.Exception.Message)"
        if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') {
            $findings.Add($msg)
        }
        else {
            $harnessErrors.Add($msg)
        }
        Write-Host "ERROR: $msg" -ForegroundColor Red
    }
}

$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
if ($crashGrew) { $findings.Add('crash.log grew during the run') }

$result = @{
    crashGrew        = $crashGrew
    seed             = $Seed
    rule             = ("the selected row's fill matches the terminal background within $FieldMatch, " +
                        "and every unselected row's is at least $FieldApart off it")
    fieldMatch       = $FieldMatch
    fieldApart       = $FieldApart
    containerIdDrift = $script:IdDrift
    rounds           = @($rounds)
    findings         = @($findings)
    harnessErrors    = @($harnessErrors)
}
$result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir 'result.json')
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)

if ($findings.Count -gt 0) {
    foreach ($f in $findings) { Write-Host "PRODUCT_FAIL $f" -ForegroundColor Red }
    exit 2
}
# A run that closed nothing is not a pass. Every leg bailing before it
# measured a round would otherwise print green with an empty rounds array.
if ($rounds.Count -eq 0) {
    Write-Host 'HARVEST_MISS: no close was measured, so nothing here rules anything out'
    exit 1
}
if ($harnessErrors.Count -gt 0) { exit 1 }
exit 0
