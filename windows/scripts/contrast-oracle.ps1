#requires -Version 7
<#
    The permanent contrast guard for Wintty's CHROME, measured in RENDERED
    PIXELS.

    Why this exists. "Make sure that contrast is always good" is not a fix,
    it is a property, and the chrome has lost it twice. Once the chrome
    glyphs measured 1.72-1.87:1 against a WCAG non-text floor of 3:1. Once
    the vertical strip's selected-row title measured 1.11:1 on the light
    half -- and that second one is the reason this harness reads pixels
    rather than brushes: the brush VALUES were right, and the code path
    that hands the tab hosts the terminal's colours simply never ran in a
    session with no config reload (MainWindow.xaml.cs:877-890 carries the
    postmortem). A checker over resolved brushes would have measured the
    correct value of a colour nothing was painting with, and passed.

    So the instrument is a screenshot. Composition, the Mica backdrop,
    opacity blending and every fallback path exist only in the rendered
    result, and that is what a user's eyes get.

    Shape. State is driven through the in-process test seam
    (WINTTY_TEST_SEAM=<session token>, lib/seam-client.ps1): seed-tabs, select, pin,
    group, collapse, cycle, toggle-sidebar, toggle-layout. No OS input is
    synthesized, nothing is focused, the machine stays usable. Surfaces are
    LOCATED read-only over UIA and MEASURED out of one capture per
    measurement point.

    Coexistence. This harness deliberately does NOT call Assert-NoWintty.
    Other agents and the developer run their own Wintty while it works, so
    it launches its own instance against an isolated XDG_CONFIG_HOME with
    windows-single-instance off, moves only its own window, and cleans up
    only the processes it started. The one thing it cannot share is the
    seam pipe: the name belongs to whichever opted-in process took it
    first, so a second seam-enabled Wintty on the box makes the launch fail
    with exit 1 rather than measure the wrong window.

    Matrix. One fresh process per config; inside each, both layouts and
    both sidebar states, so the toggles are measured on the same window
    they change:

      nocfg        --no-config, the stock built-in pair in whatever
                   polarity the desktop is in (CliAliases.cs:220 rewrites
                   the flag to --config-default-files=false, which is also
                   why windows-single-instance lands off by default)
      stock-light  the built-in light half, read straight out of
                   src/config/wintty_theme.zig at run time so the harness
                   cannot drift from the palette it is guarding
      stock-dark   the built-in dark half, same source
      themed       an explicitly-themed config (Catppuccin Mocha)

    Thresholds. Named and sourced in lib/contrast.ps1:
      text   >= 4.5:1  WCAG 2.1 SC 1.4.3, and the same 4.5 the built-in
                       palette is held to in src/config/wintty_theme_test.zig
      glyph  >= 3.0:1  WCAG 2.1 SC 1.4.11 non-text contrast
      fill    > 1.2:1  the palette test's distinguishability rule for fill
                       slots, strictly greater the way it is written there

    Anti-vacuity. -Mutate terminal launches with a deliberately illegible
    pair (background #808080, foreground #858585) and the run must go RED
    with the ratio it measured. An oracle that has never failed is not
    evidence, so the mutation is part of the harness rather than a thing
    somebody did once by hand.

    Known blind spot, stated rather than papered over: the floating group
    run label (TabRunLabel.cs:20-22) carries no AutomationProperties on
    purpose, so it cannot be located over UIA and is NOT measured here.
    Its ink is the same ink the group chip and the vertical group header
    carry, and both of those ARE measured.

    Exits 0 when every measured surface clears its floor, 1 when the
    harness could not run (no launch, no capture, a surface it could not
    locate or could not sample), 2 when a surface FAILED its threshold.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # The anti-vacuity lever. 'terminal' launches every leg with an
    # illegible foreground/background pair, which must turn the run red.
    [ValidateSet('none', 'terminal')][string]$Mutate = 'none',
    # Run a subset of the config legs by name, for a quick re-measure.
    [string[]]$Only = @()
)

. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
. (Join-Path $PSScriptRoot 'lib/contrast.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots') | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Mixed-DPI discipline: UIA rects and CopyFromScreen coordinates must live
# in one space, or every sample lands next to the element it named.
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

# Read-only occlusion plumbing. A screenshot of a covered window measures
# whatever is covering it, which would be a contrast verdict about the
# wrong pixels; this is how the harness refuses instead.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ContrastWin {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    const uint SWP_NOACTIVATE = 0x0010;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    // Place and raise in one call, WITHOUT activating. A screen capture
    // needs the window's pixels to be the topmost ones at those
    // coordinates; it does not need the keyboard, and taking the keyboard
    // would yank the caret out from under whoever is using the machine.
    public static bool PlaceOnTop(IntPtr h, int x, int y, int w, int hh) {
        return SetWindowPos(h, HWND_TOPMOST, x, y, w, hh, SWP_NOACTIVATE);
    }

    // A best-effort activation, tried once. The chrome paints an inactive
    // window differently, so a measurement of an activated window is the
    // state a reader is actually in. It is allowed to fail (the foreground
    // lock is not ours to break) and the report says which state each
    // capture was taken in.
    public static bool TryActivate(IntPtr h) { return SetForegroundWindow(h); }
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    public static uint PidAt(int x, int y) {
        var h = WindowFromPoint(new POINT { X = x, Y = y });
        uint pid; GetWindowThreadProcessId(h, out pid); return pid;
    }
    public static string ClassAt(int x, int y) {
        var h = WindowFromPoint(new POINT { X = x, Y = y });
        var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString();
    }
    public static uint ForegroundPid() {
        uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid;
    }
}
'@ -ErrorAction SilentlyContinue

$UIA = [System.Windows.Automation.AutomationElement]
$TREE = [System.Windows.Automation.TreeScope]::Descendants
$CTRL = [System.Windows.Automation.ControlType]

# The window geometry every leg is measured at. Fixed so a rect that moves
# between runs is a product change rather than a window the developer
# happened to resize.
$WinX = 60; $WinY = 60; $WinW = 1500; $WinH = 950

$script:Findings = [System.Collections.Generic.List[object]]::new()
$script:Rows = [System.Collections.Generic.List[object]]::new()
$script:HarnessErrors = [System.Collections.Generic.List[string]]::new()
$script:CaptureStates = [System.Collections.Generic.List[object]]::new()
$script:MainHwnd64 = 0
$script:ProcId = 0

# ---- UIA locating (read-only) ---------------------------------------------

function Get-UiaRoot { return $UIA::FromHandle([SeamWin]::P($script:MainHwnd64)) }

function Find-ById($root, [string]$Id) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $Id)
    return $root.FindFirst($TREE, $cond)
}

function Find-ByIdRetry([string]$Id, [int]$ms = 3000) {
    $dl = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $dl) {
        $el = Find-ById (Get-UiaRoot) $Id
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 120
    }
    return $null
}

function Get-Kids($el, $ControlType) {
    if ($null -eq $el) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $ControlType)
    $found = $el.FindAll($TREE, $cond)
    $out = [System.Collections.Generic.List[object]]::new()
    foreach ($i in $found) {
        $r = $i.Current.BoundingRectangle
        if ([double]::IsNaN($r.X) -or $r.Width -le 0 -or $r.Height -le 0) { continue }
        $out.Add($i)
    }
    return @($out)
}

# Text runs inside one row, ordered the way they are painted rather than
# the way the tree happens to hold them: a group header is swatch, title,
# count, chevron left to right, and the oracle names them by that order.
function Get-TextRuns($el) {
    return @(Get-Kids $el $CTRL::Text | Sort-Object { $_.Current.BoundingRectangle.X })
}

function Test-HasPattern($el, $pattern) {
    try { $null = $el.GetCurrentPattern($pattern); return $true } catch { return $false }
}

# Rows in the vertical strip, split the three ways the oracle needs them.
# A group header is the one that answers ExpandCollapse; a pinned row is
# the one whose ItemStatus carries Pinned (it lives in the pane's custom
# content, not in MenuItems, but both are under NavView).
function Get-VerticalRows {
    $nav = Find-ByIdRetry 'NavView'
    if ($null -eq $nav) { throw 'HARVEST_MISS: no NavView (is the vertical strip up?)' }
    $rows = @(Get-Kids $nav $CTRL::ListItem)
    $out = [System.Collections.Generic.List[object]]::new()
    foreach ($el in $rows) {
        $status = $el.Current.ItemStatus
        $kind = 'tab'
        if (Test-HasPattern $el ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)) { $kind = 'header' }
        elseif ($status -match 'Pinned') { $kind = 'pinned' }
        $selected = $false
        if ($kind -eq 'tab') {
            try {
                $p = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                $selected = $p.Current.IsSelected
            } catch { }
        }
        $out.Add([pscustomobject]@{
            El = $el; Kind = $kind; Selected = $selected
            Name = $el.Current.Name; Status = $status
            Rect = $el.Current.BoundingRectangle
        })
    }
    return @($out | Sort-Object { $_.Rect.Y })
}

function Get-HorizontalItems {
    $tv = Find-ByIdRetry 'TabViewControl'
    if ($null -eq $tv) { throw 'HARVEST_MISS: no TabViewControl (is the horizontal strip up?)' }
    $items = @(Get-Kids $tv $CTRL::TabItem)
    $out = [System.Collections.Generic.List[object]]::new()
    foreach ($el in $items) {
        # A group chip and a tab are both TabItems. The chip is the one
        # carrying a group status and no close Button of its own.
        $closes = @(Get-Kids $el $CTRL::Button)
        $kind = if ($closes.Count -eq 0) { 'chip' } else { 'tab' }
        $selected = $false
        try {
            $p = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $selected = $p.Current.IsSelected
        } catch { }
        $out.Add([pscustomobject]@{
            El = $el; Kind = $kind; Selected = $selected
            Name = $el.Current.Name; Status = $el.Current.ItemStatus
            Close = if ($closes.Count -gt 0) { $closes[0] } else { $null }
            Rect = $el.Current.BoundingRectangle
        })
    }
    return @($out | Sort-Object { $_.Rect.X })
}

# ---- capture ---------------------------------------------------------------

# One capture per measurement point, sampled many times. Taking a fresh
# screenshot per surface would let the window repaint between two samples
# of the same frame, which is how a harness invents a contrast change that
# never happened.
function New-Capture([string]$Label) {
    $rc = [SeamWin]::RectOf($script:MainHwnd64)
    if ($null -eq $rc) { throw "HARVEST_MISS: degenerate window rect for '$Label'" }

    # The occlusion refusal. A grid of probes across the window must all
    # answer with this process; anything else means something is on top of
    # the pixels about to be measured, and a verdict from those pixels
    # would be about the wrong window.
    for ($gx = 1; $gx -le 6; $gx++) {
        for ($gy = 1; $gy -le 6; $gy++) {
            $px = [int]($rc.L + $rc.W * $gx / 7.0)
            $py = [int]($rc.T + $rc.Hh * $gy / 7.0)
            $owner = [ContrastWin]::PidAt($px, $py)
            if ($owner -ne $script:ProcId) {
                throw ("OCCLUDED: at '{0}' the point {1},{2} inside the window belongs to pid {3} (class '{4}'), not to the app under test (pid {5}); nothing was measured" -f
                    $Label, $px, $py, $owner, [ContrastWin]::ClassAt($px, $py), $script:ProcId)
            }
        }
    }

    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir "shots\$Label.png"))
    # Recorded, not enforced: an unoccluded window's pixels are its own
    # whether or not it holds the keyboard, but the chrome paints an
    # inactive window differently, so the report has to say which state
    # each number was read in.
    $fg = ([ContrastWin]::ForegroundPid() -eq $script:ProcId)
    $script:CaptureStates.Add([pscustomobject]@{ capture = $Label; activated = $fg })
    return [pscustomobject]@{
        Bmp = $bmp; L = $rc.L; T = $rc.T; W = $rc.W; H = $rc.Hh
        Label = $Label
        Foreground = $fg
    }
}

# A UIA screen rect, inset and clamped into capture-local coordinates. The
# inset drops the element's own border and its outermost anti-aliased row,
# neither of which is the ink or the ground the oracle is asking about.
function ConvertTo-Local($Cap, $Rect, [int]$Inset = 1) {
    if ($null -eq $Rect -or [double]::IsNaN($Rect.X)) { return $null }
    $x = [int][Math]::Round($Rect.X) - $Cap.L + $Inset
    $y = [int][Math]::Round($Rect.Y) - $Cap.T + $Inset
    $w = [int][Math]::Round($Rect.Width) - 2 * $Inset
    $h = [int][Math]::Round($Rect.Height) - 2 * $Inset
    if ($w -le 0 -or $h -le 0) { return $null }
    if ($x -lt 0) { $w += $x; $x = 0 }
    if ($y -lt 0) { $h += $y; $y = 0 }
    if ($x + $w -gt $Cap.W) { $w = $Cap.W - $x }
    if ($y + $h -gt $Cap.H) { $h = $Cap.H - $y }
    if ($w -le 2 -or $h -le 2) { return $null }
    return @{ X = $x; Y = $y; W = $w; H = $h }
}

# ---- the verdict table -----------------------------------------------------

function Add-Row([string]$Leg, [string]$Surface, [string]$Class,
                 [double]$Ratio, [string]$Fg, [string]$Bg, [string]$Note) {
    $rule = Get-ContrastRule $Class
    $pass = Test-ContrastPasses $Ratio $Class
    $row = [ordered]@{
        leg = $Leg; surface = $Surface; class = $Class
        ratio = [Math]::Round($Ratio, 2); min = $rule.Min; rule = $rule.Source
        fg = $Fg; bg = $Bg; pass = $pass; note = $Note
    }
    $script:Rows.Add([pscustomobject]$row)
    if (-not $pass) { $script:Findings.Add([pscustomobject]$row) }
    $mark = if ($pass) { 'ok  ' } else { 'FAIL' }
    # 'field' is judged from above, so printing it with the same '>=' every
    # other class carries would report the rule backwards.
    $sense = if ($Class -eq 'field') { '<=' } else { '>=' }
    Write-Host ("  {0} {1,-24} {2,6:N2} : 1  ({3} {4}) {5} on {6}" -f
        $mark, $Surface, $Ratio, $sense, $rule.Min, $Fg, $Bg)
}

# A surface that could not be located or could not be sampled is NOT a
# pass. It is recorded as an unmeasured surface and it makes the run exit
# 1: nothing is known about it, and reporting nothing as green is how an
# oracle rots.
function Add-Unmeasured([string]$Leg, [string]$Surface, [string]$Why) {
    $script:HarnessErrors.Add("$Leg/$Surface : $Why")
    $script:Rows.Add([pscustomobject][ordered]@{
        leg = $Leg; surface = $Surface; class = ''; ratio = $null
        min = $null; rule = ''; fg = ''; bg = ''; pass = $null; note = $Why
    })
    Write-Host ("  ??   {0,-24} not measured: {1}" -f $Surface, $Why) -ForegroundColor Yellow
}

# Different from unmeasured, and the difference is the whole honesty of the
# report: a surface the product deliberately does not paint in this
# configuration is not a gap in the oracle. A compact pane has no titles by
# design (VerticalTabPinnedRow.cs:40). Recorded with its reason, and it does
# not make the run exit 1 -- but it is never called for a surface that
# should have been there.
function Add-NotApplicable([string]$Leg, [string]$Surface, [string]$Why) {
    $script:Rows.Add([pscustomobject][ordered]@{
        leg = $Leg; surface = $Surface; class = ''; ratio = $null
        min = $null; rule = ''; fg = ''; bg = ''; pass = $null; note = "n/a: $Why"
    })
    Write-Host ("  --   {0,-24} n/a: {1}" -f $Surface, $Why)
}

# An element that UIA reports offscreen, or whose rect is not wholly inside
# the strip that owns it, is CLIPPED: it has a rectangle but the pixels in
# it belong to something else. Sampling one is how a compact pane's hidden
# title got read as a 1.02:1 contrast failure.
function Test-RectInside($Inner, $Outer, [double]$Slack = 1.0) {
    if ($null -eq $Inner -or $null -eq $Outer) { return $false }
    if ([double]::IsNaN($Inner.X) -or [double]::IsNaN($Outer.X)) { return $false }
    return ($Inner.X -ge $Outer.X - $Slack) -and ($Inner.Y -ge $Outer.Y - $Slack) -and
           ($Inner.X + $Inner.Width -le $Outer.X + $Outer.Width + $Slack) -and
           ($Inner.Y + $Inner.Height -le $Outer.Y + $Outer.Height + $Slack)
}

function Test-Visible($El) {
    if ($null -eq $El) { return $false }
    try { if ($El.Current.IsOffscreen) { return $false } } catch { }
    $r = $El.Current.BoundingRectangle
    return (-not [double]::IsNaN($r.X)) -and $r.Width -gt 2 -and $r.Height -gt 2
}

# Ink against the ground it actually sits on, both read out of one region.
# The background is that region's plurality colour -- a text element is
# mostly background -- and the ink is the cluster furthest from it in WCAG
# terms, which is the glyph core rather than its anti-aliased edge.
function Measure-Surface($Cap, [string]$Leg, [string]$Surface, [string]$Class,
                         $Rect, [int]$Inset = 1, [string]$Note = '',
                         [bool]$Guessed = $false) {
    $local = ConvertTo-Local $Cap $Rect $Inset
    if ($null -eq $local) {
        Add-Unmeasured $Leg $Surface 'the element has no usable rect in the capture'
        return
    }
    $s = [ContrastSampler]::Region($Cap.Bmp, $local.X, $local.Y, $local.W, $local.H)
    if (-not $s.Ok) { Add-Unmeasured $Leg $Surface $s.Why; return }
    # A rect UIA handed over is the element; a rect the harness guessed
    # geometrically may simply have missed it. So a flat sample means two
    # different things: on a located element it is the finding (nothing was
    # painted where the ink should be), on a guess it is the guess failing,
    # and calling the second one a contrast failure would be a lie.
    if ($Guessed -and $s.FgCount -eq 0) {
        Add-Unmeasured $Leg $Surface 'the geometric fallback rect holds no ink; the guess missed the glyph'
        return
    }
    Add-Row $Leg $Surface $Class $s.Ratio $s.FgHex $s.BgHex $Note
}

# Two flat regions against each other, for a fill that carries no ink of
# its own: the selected-row fill against the strip it sits in.
function Measure-Pair($Cap, [string]$Leg, [string]$Surface, [string]$Class,
                      $FgRect, $BgRect, [int]$Inset = 1, [string]$Note = '') {
    $a = ConvertTo-Local $Cap $FgRect $Inset
    $b = ConvertTo-Local $Cap $BgRect $Inset
    if ($null -eq $a -or $null -eq $b) {
        Add-Unmeasured $Leg $Surface 'one of the two regions has no usable rect in the capture'
        return
    }
    $sa = [ContrastSampler]::Flat($Cap.Bmp, $a.X, $a.Y, $a.W, $a.H)
    $sb = [ContrastSampler]::Flat($Cap.Bmp, $b.X, $b.Y, $b.W, $b.H)
    if (-not $sa.Ok) { Add-Unmeasured $Leg $Surface ("fill region: " + $sa.Why); return }
    if (-not $sb.Ok) { Add-Unmeasured $Leg $Surface ("ground region: " + $sb.Why); return }
    $ratio = [ContrastMath]::Ratio($sa.BgR, $sa.BgG, $sa.BgB, $sb.BgR, $sb.BgG, $sb.BgB)
    Add-Row $Leg $Surface $Class $ratio $sa.BgHex $sb.BgHex $Note
}

function New-Rect([double]$X, [double]$Y, [double]$W, [double]$H) {
    return New-Object System.Windows.Rect($X, $Y, $W, $H)
}

# ---- the legs --------------------------------------------------------------

# Every ink sample is taken from an element that is on screen AND wholly
# inside the strip that owns it. Both halves matter: UIA hands out a rect
# for a text run the pane has clipped away, and the pixels at that rect
# belong to whatever is painted there instead.
function Test-Samplable($El, $Container) {
    if (-not (Test-Visible $El)) { return $false }
    if ($null -eq $Container) { return $true }
    return Test-RectInside $El.Current.BoundingRectangle $Container.Current.BoundingRectangle
}

function Get-SamplableRuns($El, $Container) {
    return @(Get-TextRuns $El | Where-Object { Test-Samplable $_ $Container })
}

# Pick a run by what it SAYS, not by where it sits. Ordering by X put a
# pinned tab's pushpin FontIcon first and got it measured as the tab title:
# the pin really was a near-invisible 1.86:1 glyph, but that is a different
# surface with a different floor, and naming it wrong would have buried
# both. A UIA Text peer's Name is its text content, so the title is the run
# that says the title.
function Select-RunSaying($Runs, [string]$Text) {
    return @($Runs | Where-Object { $_.Current.Name -eq $Text }) | Select-Object -First 1
}

# The member count is the run that is a bare number.
function Select-CountRun($Runs) {
    return @($Runs | Where-Object { $_.Current.Name -match '^\s*\d+\s*$' }) | Select-Object -First 1
}

# Whatever is left after the title and the count: the chevron, when its
# FontIcon happens to surface a Text peer at all.
function Select-OtherRun($Runs, [string]$Title) {
    return @($Runs | Where-Object {
        $_.Current.Name -ne $Title -and $_.Current.Name -notmatch '^\s*\d+\s*$'
    }) | Sort-Object { -$_.Current.BoundingRectangle.X } | Select-Object -First 1
}

function Measure-VerticalLeg($Cap, [string]$Leg, [bool]$Compact, [string]$GroupTitle) {
    $nav = Find-ByIdRetry 'NavView'
    $rows = Get-VerticalRows
    $tabs = @($rows | Where-Object { $_.Kind -eq 'tab' })
    $pins = @($rows | Where-Object { $_.Kind -eq 'pinned' })
    $heads = @($rows | Where-Object { $_.Kind -eq 'header' })

    $active = @($tabs | Where-Object { $_.Selected }) | Select-Object -First 1
    $idle = @($tabs | Where-Object { -not $_.Selected }) | Select-Object -First 1

    # A compact pane collapses the title columns by design, so a missing
    # title there is the product working, not the oracle failing.
    $compactWhy = 'the compact pane collapses the title column'

    if ($null -eq $active) {
        Add-Unmeasured $Leg 'vtab-title-active' 'no row reports itself selected'
        Add-Unmeasured $Leg 'vtab-close-glyph' 'no row reports itself selected'
    } else {
        # The container is the ROW, not the strip: a title the pane has
        # clipped keeps a rect that runs past its own row's edge, and the
        # pixels out there belong to whatever is painted beyond it.
        $t = Select-RunSaying (Get-SamplableRuns $active.El $active.El) $active.Name
        if ($null -eq $t) {
            if ($Compact) { Add-NotApplicable $Leg 'vtab-title-active' $compactWhy }
            else { Add-Unmeasured $Leg 'vtab-title-active' 'the selected row exposes no visible, unclipped Text run' }
        }
        else { Measure-Surface $Cap $Leg 'vtab-title-active' 'text' $t.Current.BoundingRectangle 1 "row '$($active.Name)'" }

        $close = @(Get-Kids $active.El $CTRL::Button | Where-Object { Test-Samplable $_ $active.El }) | Select-Object -First 1
        if ($null -eq $close) {
            if ($Compact) { Add-NotApplicable $Leg 'vtab-close-glyph' 'the compact pane leaves no room for the close button' }
            else { Add-Unmeasured $Leg 'vtab-close-glyph' 'the selected row exposes no visible close Button' }
        }
        else { Measure-Surface $Cap $Leg 'vtab-close-glyph' 'glyph' $close.Current.BoundingRectangle 2 'the close X' }
    }

    if ($null -eq $idle) {
        Add-Unmeasured $Leg 'vtab-title-inactive' 'no unselected tab row'
    } else {
        $t = Select-RunSaying (Get-SamplableRuns $idle.El $idle.El) $idle.Name
        if ($null -eq $t) {
            if ($Compact) { Add-NotApplicable $Leg 'vtab-title-inactive' $compactWhy }
            else { Add-Unmeasured $Leg 'vtab-title-inactive' 'the unselected row exposes no visible, unclipped Text run' }
        }
        else { Measure-Surface $Cap $Leg 'vtab-title-inactive' 'text' $t.Current.BoundingRectangle 1 "row '$($idle.Name)'" }

        # The close X on an UNselected row is its own surface: the selected
        # row's fill is a different ground, and the inactive ink is chosen
        # by a different path.
        $idleClose = @(Get-Kids $idle.El $CTRL::Button | Where-Object { Test-Samplable $_ $idle.El }) | Select-Object -First 1
        if ($null -eq $idleClose) {
            if ($Compact) { Add-NotApplicable $Leg 'vtab-close-glyph-inactive' 'the compact pane leaves no room for the close button' }
            else { Add-Unmeasured $Leg 'vtab-close-glyph-inactive' 'the unselected row exposes no visible close Button' }
        }
        else { Measure-Surface $Cap $Leg 'vtab-close-glyph-inactive' 'glyph' $idleClose.Current.BoundingRectangle 2 "the close X on '$($idle.Name)'" }
    }

    # The pinned shelf. Below a 96px pane the title column is deliberately
    # collapsed (VerticalTabPinnedRow.cs:40), so the collapsed-sidebar leg
    # has an icon and no title, and saying so is the honest reading.
    if ($pins.Count -eq 0) {
        Add-Unmeasured $Leg 'vtab-pinned-title' 'no pinned row in the strip'
        Add-Unmeasured $Leg 'vtab-pinned-icon' 'no pinned row in the strip'
    } else {
        $pin = $pins[0]
        $t = Select-RunSaying (Get-SamplableRuns $pin.El $pin.El) $pin.Name
        if ($null -eq $t) {
            if ($Compact) { Add-NotApplicable $Leg 'vtab-pinned-title' $compactWhy }
            else { Add-Unmeasured $Leg 'vtab-pinned-title' 'the pinned row exposes no visible, unclipped Text run' }
        } else {
            Measure-Surface $Cap $Leg 'vtab-pinned-title' 'text' $t.Current.BoundingRectangle 1 "pinned '$($pin.Name)'"
        }
        # The icon has no automation identity of its own: it is the square
        # at the row's leading edge, which is where the row's own rect puts
        # it whatever the pane width is.
        $r = $pin.Rect
        $side = [Math]::Min($r.Height, $r.Width)
        Measure-Surface $Cap $Leg 'vtab-pinned-icon' 'glyph' (New-Rect $r.X $r.Y $side $r.Height) 3 'the leading square of the pinned row' $true
    }

    # The pin boundary stroke: a 2px accent rule under the shelf
    # (VerticalTabStrip.xaml.cs:595, 1353). It carries no automation
    # identity, so it is read as the band between the last pinned row and
    # the first body row -- and it is judged by the palette test's fill
    # rule, because a rule has to be VISIBLE, not readable.
    if ($pins.Count -gt 0 -and $tabs.Count -gt 0) {
        $lastPin = $pins[-1]
        $firstBody = @($tabs | Where-Object { $_.Rect.Y -gt $lastPin.Rect.Y }) | Select-Object -First 1
        if ($null -eq $firstBody) {
            Add-Unmeasured $Leg 'vtab-boundary-stroke' 'no body row below the pinned shelf'
        } else {
            $top = $lastPin.Rect.Y + $lastPin.Rect.Height
            $bot = $firstBody.Rect.Y
            if ($bot - $top -lt 4) {
                Add-Unmeasured $Leg 'vtab-boundary-stroke' ("the gap under the shelf is {0:N0}px, too thin to sample" -f ($bot - $top))
            } else {
                Measure-Surface $Cap $Leg 'vtab-boundary-stroke' 'fill' `
                    (New-Rect $lastPin.Rect.X $top $lastPin.Rect.Width ($bot - $top)) 0 `
                    'the 2px rule between the pinned shelf and the body'
            }
        }
    } else {
        Add-Unmeasured $Leg 'vtab-boundary-stroke' 'the boundary only exists with both a pinned shelf and a body'
    }

    # The group header row: title, count and chevron, left to right.
    if ($heads.Count -eq 0) {
        foreach ($n in @('vtab-group-title', 'vtab-group-count', 'vtab-group-chevron')) {
            Add-Unmeasured $Leg $n 'no group header in the strip'
        }
    } else {
        $head = $heads[0]
        $runs = @(Get-SamplableRuns $head.El $head.El)
        $titleRun = Select-RunSaying $runs $head.Name
        $countRun = Select-CountRun $runs
        $chevronRun = Select-OtherRun $runs $head.Name

        if ($null -ne $titleRun) { Measure-Surface $Cap $Leg 'vtab-group-title' 'text' $titleRun.Current.BoundingRectangle 1 "group '$($head.Name)'" }
        elseif ($Compact) { Add-NotApplicable $Leg 'vtab-group-title' $compactWhy }
        else { Add-Unmeasured $Leg 'vtab-group-title' 'the header exposes no visible, unclipped run saying the group title' }

        if ($null -ne $countRun) { Measure-Surface $Cap $Leg 'vtab-group-count' 'text' $countRun.Current.BoundingRectangle 1 'the member count (painted at 0.7 opacity)' }
        elseif ($Compact) { Add-NotApplicable $Leg 'vtab-group-count' $compactWhy }
        else { Add-Unmeasured $Leg 'vtab-group-count' 'the header exposes no visible run that is a bare member count' }

        # The chevron is a FontIcon, and a FontIcon does not always surface
        # as a Text peer. When it does not, it is still the glyph at the
        # trailing edge of the header row, which is where the row's own
        # rect puts it.
        if ($null -ne $chevronRun) {
            Measure-Surface $Cap $Leg 'vtab-group-chevron' 'glyph' $chevronRun.Current.BoundingRectangle 1 'the collapse chevron'
        } elseif ($Compact) {
            Add-NotApplicable $Leg 'vtab-group-chevron' $compactWhy
        } elseif ($null -ne $countRun) {
            $qr = $countRun.Current.BoundingRectangle
            Measure-Surface $Cap $Leg 'vtab-group-chevron' 'glyph' `
                (New-Rect ($qr.X + $qr.Width + 1) ($qr.Y - 2) 22.0 ($qr.Height + 4)) 0 `
                'the collapse chevron, read just past the member count (the FontIcon exposes no Text peer)' $true
        } else {
            Add-Unmeasured $Leg 'vtab-group-chevron' 'no chevron run and no count run to anchor a fallback on'
        }
    }

    # The selection fill against the TERMINAL, and the pass is that they
    # match.
    #
    # This used to score the fill against the empty strip below the last
    # row, under the >1.2 fill rule -- the selected row had to be visible
    # as a differently coloured surface. That is no longer the design and
    # scoring it that way asserts a decision that was reversed: the active
    # tab is the FIELD, painted the terminal's own ground, running into the
    # pane beside it with no line between. Its separation from the chrome
    # is carried by the accent stroke on its three closed sides, not by its
    # fill, and a fill that DID separate from the terminal would be the
    # defect. So the same two regions are still read, one of them is now
    # the terminal instead of the strip, and the comparison is inverted.
    #
    # Which is a stricter bar than the one it replaces, not a relaxed one:
    # >1.2 admitted any of a hundred colours, and this admits one.
    #
    # SelectionRow is a Border behind the NavView; a Border does not always
    # get an automation peer, so the selected row's own rect stands in. Both
    # name the same band -- UpdateSelectionRow places the fill on the
    # selected row.
    $sel = Find-ById (Get-UiaRoot) 'SelectionRow'
    $selRect = if ($null -ne $sel) { $sel.Current.BoundingRectangle } elseif ($null -ne $active) { $active.Rect } else { $null }
    if ($null -eq $selRect -or $null -eq $nav) {
        Add-Unmeasured $Leg 'vtab-selection-field' 'neither SelectionRow nor a selected row could be located'
    } else {
        $sr = $selRect
        # Past the strip's trailing edge, the same anchor the terminal band
        # itself is derived from below, and level with the selected row so
        # both samples sit in one horizontal band of the window.
        $tRight = ($rows | ForEach-Object { $_.Rect.X + $_.Rect.Width } | Measure-Object -Maximum).Maximum
        if ([double]::IsNaN($sr.X) -or $sr.Width -le 6) {
            Add-Unmeasured $Leg 'vtab-selection-field' 'no usable fill slice on the selected row'
        } else {
            # A slice inside the fill, clear of the title ink.
            $sliceW = [Math]::Max(6.0, $sr.Width * 0.12)
            $fillRect = New-Rect ($sr.X + $sr.Width - $sliceW - 2) ($sr.Y + 3) $sliceW ([Math]::Max(6.0, $sr.Height - 6))
            # Well right of the prompt, so the sample is terminal ground and
            # not the shell's own first line.
            $groundRect = New-Rect ($tRight + 40) ($sr.Y + 3) 24 ([Math]::Max(6.0, $sr.Height - 6))
            Measure-Pair $Cap $Leg 'vtab-selection-field' 'field' $fillRect $groundRect 0 'selected-row fill against the terminal it must match'
        }
    }

    # The terminal starts past the strip's trailing edge. Derived from the
    # rows rather than from NavView, because a NavigationView's rect is the
    # whole window -- its content IS the terminal -- so anchoring on it put
    # every sample off the right-hand edge of the capture and reported the
    # terminal as unmeasurable in every vertical leg.
    $stripRight = ($rows | ForEach-Object { $_.Rect.X + $_.Rect.Width } | Measure-Object -Maximum).Maximum
    $stripTop = ($rows | ForEach-Object { $_.Rect.Y } | Measure-Object -Minimum).Minimum
    # The first row is not the top of the terminal: the strip's own header
    # sits above it, and the shell's first line is level with that. Take
    # whichever is higher, so the prompt is inside the first band.
    Measure-Terminal $Cap $Leg ($stripRight + 12) ([Math]::Min($stripTop, $Cap.T + 44.0))
}

function Measure-HorizontalLeg($Cap, [string]$Leg, [string]$GroupTitle) {
    $tv = Find-ByIdRetry 'TabViewControl'
    $items = Get-HorizontalItems
    # A chip and a tab are both TabItems. The chip is the one named after
    # the group the seam just created, which the driver knows for certain;
    # inferring it from the absence of a close Button proved unreliable.
    $chips = @($items | Where-Object { $_.Name -eq $GroupTitle })
    $tabs = @($items | Where-Object { $_.Name -ne $GroupTitle })
    $active = @($tabs | Where-Object { $_.Selected }) | Select-Object -First 1
    $idle = @($tabs | Where-Object { -not $_.Selected }) | Select-Object -First 1

    if ($null -eq $active) {
        Add-Unmeasured $Leg 'htab-title-active' 'no tab reports itself selected'
        Add-Unmeasured $Leg 'htab-close-glyph' 'no tab reports itself selected'
    } else {
        $t = Select-RunSaying (Get-SamplableRuns $active.El $active.El) $active.Name
        if ($null -eq $t) { Add-Unmeasured $Leg 'htab-title-active' 'the selected tab exposes no visible run saying its title' }
        else { Measure-Surface $Cap $Leg 'htab-title-active' 'text' $t.Current.BoundingRectangle 1 "tab '$($active.Name)'" }
        if ($null -eq $active.Close -or -not (Test-Samplable $active.Close $active.El)) {
            Add-Unmeasured $Leg 'htab-close-glyph' 'the selected tab exposes no visible close Button'
        }
        else { Measure-Surface $Cap $Leg 'htab-close-glyph' 'glyph' $active.Close.Current.BoundingRectangle 2 'the close X' }
    }

    if ($null -eq $idle) {
        Add-Unmeasured $Leg 'htab-title-inactive' 'no unselected tab'
    } else {
        $t = Select-RunSaying (Get-SamplableRuns $idle.El $idle.El) $idle.Name
        if ($null -eq $t) { Add-Unmeasured $Leg 'htab-title-inactive' 'the unselected tab exposes no visible run saying its title' }
        else { Measure-Surface $Cap $Leg 'htab-title-inactive' 'text' $t.Current.BoundingRectangle 1 "tab '$($idle.Name)'" }
        if ($null -eq $idle.Close -or -not (Test-Samplable $idle.Close $idle.El)) {
            Add-Unmeasured $Leg 'htab-close-glyph-inactive' 'the unselected tab exposes no visible close Button'
        }
        else { Measure-Surface $Cap $Leg 'htab-close-glyph-inactive' 'glyph' $idle.Close.Current.BoundingRectangle 2 "the close X on '$($idle.Name)'" }
    }

    # The pinned tab's pushpin. Its own surface with its own floor: it is a
    # glyph, not a word, and it was the run an X-ordered lookup mistook for
    # the title.
    $pinned = @($tabs | Where-Object { $_.Status -match 'Pinned' }) | Select-Object -First 1
    if ($null -eq $pinned) {
        Add-Unmeasured $Leg 'htab-pin-glyph' 'no tab reports itself pinned'
    } else {
        $glyph = Select-OtherRun (Get-SamplableRuns $pinned.El $pinned.El) $pinned.Name
        if ($null -ne $glyph) {
            Measure-Surface $Cap $Leg 'htab-pin-glyph' 'glyph' $glyph.Current.BoundingRectangle 1 "the pushpin on '$($pinned.Name)'"
        } else {
            # The pushpin and the tab icon are FontIcons with no Text peer
            # of their own, so the leading strip of the tab -- which is
            # where both are drawn -- stands in for them.
            $pr = $pinned.Rect
            Measure-Surface $Cap $Leg 'htab-pin-glyph' 'glyph' `
                (New-Rect ($pr.X + 2) ($pr.Y + 3) ([Math]::Min(56.0, $pr.Width * 0.35)) ([Math]::Max(8.0, $pr.Height - 6))) 1 `
                "the leading glyph strip of the pinned tab '$($pinned.Name)' (the pushpin exposes no Text peer)" $true
        }
    }

    if ($chips.Count -eq 0) {
        foreach ($n in @('htab-chip-title', 'htab-chip-count', 'htab-chip-chevron')) {
            Add-Unmeasured $Leg $n "no strip item named '$GroupTitle'; the group chip was not found"
        }
    } else {
        $chip = $chips[0]
        $runs = @(Get-SamplableRuns $chip.El $chip.El)
        $chipTitle = Select-RunSaying $runs $chip.Name
        $chipCount = Select-CountRun $runs
        $chipChevron = Select-OtherRun $runs $chip.Name
        if ($null -ne $chipTitle) { Measure-Surface $Cap $Leg 'htab-chip-title' 'text' $chipTitle.Current.BoundingRectangle 1 "chip '$($chip.Name)'" }
        else { Add-Unmeasured $Leg 'htab-chip-title' 'the chip exposes no visible run saying the group title' }
        if ($null -ne $chipCount) { Measure-Surface $Cap $Leg 'htab-chip-count' 'text' $chipCount.Current.BoundingRectangle 1 'the member count (painted at 0.7 opacity)' }
        else { Add-Unmeasured $Leg 'htab-chip-count' 'the chip exposes no visible run that is a bare member count' }
        if ($null -ne $chipChevron) {
            Measure-Surface $Cap $Leg 'htab-chip-chevron' 'glyph' $chipChevron.Current.BoundingRectangle 1 'the collapse chevron'
        } elseif ($null -ne $chipCount) {
            # The chevron follows the count. Anchoring on the count rather
            # than on the chip's trailing edge matters: a TabViewItem is
            # padded well past its content, and a trailing-edge guess
            # sampled empty chrome and came back flat.
            $qr = $chipCount.Current.BoundingRectangle
            Measure-Surface $Cap $Leg 'htab-chip-chevron' 'glyph' `
                (New-Rect ($qr.X + $qr.Width + 1) ($qr.Y - 2) 22.0 ($qr.Height + 4)) 0 `
                'the collapse chevron, read just past the member count (the FontIcon exposes no Text peer)' $true
        } else {
            Add-Unmeasured $Leg 'htab-chip-chevron' 'no chevron run and no count run to anchor a fallback on'
        }
    }

    # The terminal starts under the strip. Anchored on the tab items for
    # the same reason the vertical leg anchors on its rows.
    $stripBottom = ($items | ForEach-Object { $_.Rect.Y + $_.Rect.Height } | Measure-Object -Maximum).Maximum
    $stripLeft = ($items | ForEach-Object { $_.Rect.X } | Measure-Object -Minimum).Minimum
    Measure-Terminal $Cap $Leg ($stripLeft + 4) ($stripBottom + 12)
}

# The terminal's own foreground against its own background, read where the
# shell prompt is. This is the surface the palette test guards in theory;
# measuring it here proves the theme that passed the palette test is the
# theme that reached the glass.
function Measure-Terminal($Cap, [string]$Leg, [double]$Left, [double]$Top) {
    $left = $Left; $top = $Top
    $w = [Math]::Min(520.0, ($Cap.L + $Cap.W - 12) - $left)
    if ($w -lt 60) { Add-Unmeasured $Leg 'terminal-fg-on-bg' 'no room to sample the terminal surface'; return }

    # The prompt is somewhere in the first few rows, and where exactly
    # depends on the shell. Walk a band down the top of the surface and
    # take the first one that has ink in it; a flat band is a terminal with
    # nothing printed on it yet, which is not a contrast finding.
    for ($step = 0; $step -lt 6; $step++) {
        $rect = New-Rect $left ($top + $step * 40.0) $w 40.0
        $local = ConvertTo-Local $Cap $rect 0
        if ($null -eq $local) { continue }
        $s = [ContrastSampler]::Region($Cap.Bmp, $local.X, $local.Y, $local.W, $local.H)
        if (-not $s.Ok -or $s.FgCount -eq 0) { continue }
        Add-Row $Leg 'terminal-fg-on-bg' 'text' $s.Ratio $s.FgHex $s.BgHex 'the shell prompt against the terminal ground'
        return
    }
    Add-Unmeasured $Leg 'terminal-fg-on-bg' 'every sampled band on the terminal surface is flat: the shell had printed nothing when the capture was taken'
}

# The tile container is a VariableSizedWrapGrid, and a bare panel does not
# always get an automation peer, so the ScrollViewer and then the popup
# control itself stand in behind it.
function Find-SwitcherHost {
    $root = Get-UiaRoot
    foreach ($id in @('CandidateRow', 'CandidateScroll', 'TabSwitcherPopupUI')) {
        $el = Find-ById $root $id
        if ($null -ne $el) { return $el }
    }
    return $null
}

# The tile rect is located BEFORE the capture, not after: the popup
# dismisses itself on a 1.2s timer, so a lookup that ran after the
# screenshot would be asking about a window that had already gone.
function Get-SwitcherTileRect {
    $el = Find-SwitcherHost
    if ($null -eq $el) { return $null }
    $runs = @(Get-TextRuns $el)
    if ($runs.Count -eq 0) { return $null }
    return $runs[0].Current.BoundingRectangle
}

function Measure-Switcher($Cap, [string]$Leg, $TileRect) {
    if ($null -eq $TileRect) {
        Add-Unmeasured $Leg 'switcher-tile-text' 'the switcher popup exposed no tile text when the cycle command acked'
        return
    }
    Measure-Surface $Cap $Leg 'switcher-tile-text' 'text' $TileRect 1 'the first tile title'
}

# ---- configs ---------------------------------------------------------------

# The built-in halves are read out of the zig source at run time rather
# than copied here. A palette edit then reaches this harness by itself; a
# copy would have to be remembered, and would not be.
function Get-BuiltinTheme([string]$Half) {
    $path = Join-Path $PSScriptRoot '..\..\src\config\wintty_theme.zig'
    $path = (Resolve-Path $path -ErrorAction SilentlyContinue)?.Path
    if (-not $path) { throw "HARVEST_MISS: cannot find src/config/wintty_theme.zig from $PSScriptRoot" }
    $lines = Get-Content $path
    $body = [System.Collections.Generic.List[string]]::new()
    $inside = $false
    foreach ($line in $lines) {
        if ($line -match "^pub const $Half\s*:") { $inside = $true; continue }
        if (-not $inside) { continue }
        if ($line.Trim() -eq ';') { break }
        $t = $line.Trim()
        if ($t.StartsWith('\\')) {
            $v = $t.Substring(2).Trim()
            if ($v) { [void]$body.Add($v) }
        }
    }
    if ($body.Count -lt 20) {
        throw "HARVEST_MISS: parsed only $($body.Count) lines out of the built-in '$Half' half; the theme source shape changed"
    }
    return ($body -join "`n")
}

# Every leg gets these. windows-single-instance off is what lets this run
# beside somebody else's Wintty; window-save-state never keeps a previous
# run's geometry from deciding where this one measures.
$CommonConfig = @'
windows-single-instance = false
window-save-state = never
windows-settings-ui = true
vertical-tabs = true
vertical-tabs-hover-expand = false
'@

# The anti-vacuity mutation. Not a change to the oracle's arithmetic and
# not a fake reading: a real config the app really renders, whose
# foreground is a shade off its own background. If the run stays green
# with this in force, the instrument is measuring nothing.
$MutantTheme = @'
background = #808080
foreground = #858585
'@

function New-ConfigLegs {
    $legs = [System.Collections.Generic.List[object]]::new()
    $legs.Add([ordered]@{
        name = 'nocfg'
        args = @('--no-config')
        # A comment, not an empty string: the isolated XDG dir still gets a
        # config file so nothing on the developer's disk can leak into the
        # leg, and --no-config then makes the app ignore it. (An empty
        # string is also not a value the shared launcher accepts.)
        config = '# deliberately ignored: this leg runs with --no-config'
        what = 'the unconfigured build: the stock built-in pair in the desktop polarity'
    })
    $legs.Add([ordered]@{
        name = 'stock-light'
        args = @()
        config = $CommonConfig + "`nwindow-theme = light`n" + (Get-BuiltinTheme 'light')
        what = 'the built-in light half, read from src/config/wintty_theme.zig'
    })
    $legs.Add([ordered]@{
        name = 'stock-dark'
        args = @()
        config = $CommonConfig + "`nwindow-theme = dark`n" + (Get-BuiltinTheme 'dark')
        what = 'the built-in dark half, read from src/config/wintty_theme.zig'
    })
    $legs.Add([ordered]@{
        name = 'themed'
        args = @()
        config = $CommonConfig + "`nwindow-theme = dark`ntheme = Catppuccin Mocha`n"
        what = 'an explicitly-themed config'
    })
    if ($Mutate -eq 'terminal') {
        foreach ($leg in $legs) {
            # --no-config would throw the mutation away, so the mutated
            # nocfg leg carries the flag no further: it becomes the same
            # configured launch as the rest, named so the report says so.
            $leg.args = @()
            $leg.config = $CommonConfig + "`nwindow-theme = dark`n" + $MutantTheme
            $leg.what = 'MUTATED: ' + $leg.what
        }
    }
    return @($legs)
}

# ---- run -------------------------------------------------------------------

if (-not (Test-Path $ExePath)) {
    Write-Host "HARVEST_MISS: missing exe: $ExePath"
    exit 1
}

$titles = @('alpha', 'bravo', 'charlie', 'delta', 'echo')

# The surface names each layout owns, so a leg that never reached that
# layout can still say exactly what went unmeasured instead of leaving a
# silent hole in the table.
$VerticalSurfaces = @(
    'vtab-title-active', 'vtab-close-glyph', 'vtab-title-inactive',
    'vtab-close-glyph-inactive', 'vtab-pinned-title', 'vtab-pinned-icon',
    'vtab-boundary-stroke', 'vtab-group-title', 'vtab-group-count',
    'vtab-group-chevron', 'vtab-selection-field', 'terminal-fg-on-bg'
)
$HorizontalSurfaces = @(
    'htab-title-active', 'htab-close-glyph', 'htab-title-inactive',
    'htab-close-glyph-inactive', 'htab-pin-glyph', 'htab-chip-title',
    'htab-chip-count', 'htab-chip-chevron', 'terminal-fg-on-bg'
)
$script:LegVerdicts = [System.Collections.Generic.List[object]]::new()

foreach ($leg in (New-ConfigLegs)) {
    if ($Only.Count -gt 0 -and $Only -notcontains $leg.name) { continue }
    Write-Host ""
    Write-Host ("=== {0} : {1} ===" -f $leg.name, $leg.what) -ForegroundColor Cyan
    $s = $null
    $legErr = ''
    try {
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $leg.config -Arguments $leg.args
        $script:MainHwnd64 = $s.Hwnd64
        $script:ProcId = [uint32]$s.Proc.Id
        $hwnd = [SeamWin]::P($script:MainHwnd64)
        [void][ContrastWin]::PlaceOnTop($hwnd, $WinX, $WinY, $WinW, $WinH)
        [void][ContrastWin]::TryActivate($hwnd)
        Start-Sleep -Milliseconds 700

        # One compound state, built once: five tabs, a pin so the shelf and
        # its boundary exist, a collapsed group so the header and its
        # chevron exist, and an active row that is neither.
        [void](Invoke-SeamCommand $s @{ op = 'seed-tabs'; count = 5; titles = $titles })
        [void](Invoke-SeamCommand $s @{ op = 'pin'; index = 0; via = 'router' })
        [void](Invoke-SeamCommand $s @{ op = 'group'; indices = @(3, 4) })
        [void](Invoke-SeamCommand $s @{ op = 'select'; index = 1 })
        $state = Invoke-SeamCommand $s @{ op = 'get-state' }

        # Every leg is measured in both layouts, whatever it started in --
        # when the build lets it be. Under --no-config the toggle acks and
        # the window stays horizontal, so the ack is verified rather than
        # trusted: an unverified toggle would have this leg photograph a
        # horizontal strip and report it as the vertical one.
        # ToggleTabLayout no-ops while the layout coordinator is still
        # mid-switch, which it can be right after a launch, so the toggle is
        # retried and then verified. An unverified toggle would have this
        # leg photograph a horizontal strip and file it as the vertical one.
        $vertical = [bool]$state.state.vertical
        for ($try = 0; $try -lt 3 -and -not $vertical; $try++) {
            $vertical = [bool](Invoke-SeamCommand $s @{ op = 'toggle-layout' }).state.vertical
            if (-not $vertical) { Start-Sleep -Milliseconds 900 }
        }
        # Give the shells a moment to print a prompt; a flat terminal band
        # is an unmeasured surface, not a finding, and waiting here is
        # cheaper than losing the surface.
        Start-Sleep -Seconds 3

        # Which sidebar state each pass measures is read from the pane
        # width the app reports, not from which toggle the driver sent. The
        # app decides where it starts, and a leg labelled by the driver's
        # intention rather than the app's answer names the wrong thing.
        $groups = @($state.state.groups)
        $groupTitle = if ($groups.Count -gt 0) { $groups[0].title } else { 'group-1' }
        if (-not $vertical) {
            # One row, not twelve: the whole vertical set went unmeasured
            # for one reason, and repeating it per surface buries the rest
            # of the report.
            Add-Unmeasured "$($leg.name)/vertical" ("{0} surfaces" -f $VerticalSurfaces.Count) `
                ('the layout toggle acked three times and the window stayed horizontal, so the vertical strip was never on screen (' +
                 ($VerticalSurfaces -join ', ') + ')')
        } else {
            foreach ($pass in 1, 2) {
                $now = Invoke-SeamCommand $s @{ op = 'get-state' }
                $paneWidth = [double]$now.state.paneWidth
                # VerticalTabPinnedRow.TitlePaneWidthThreshold: below 96px
                # the title columns are collapsed by design.
                $compact = $paneWidth -lt 96
                $tag = if ($compact) { 'vert-compact' } else { 'vert-wide' }
                Write-Host ("-- vertical, pane {0:N0}px ({1})" -f $paneWidth, $tag)
                Measure-VerticalLeg (New-Capture "$($leg.name)-$tag") "$($leg.name)/$tag" $compact $groupTitle
                if ($pass -eq 1) {
                    [void](Invoke-SeamCommand $s @{ op = 'toggle-sidebar' })
                    Start-Sleep -Milliseconds 600
                }
            }
            # Leave the strip wide, whichever state it started in, so the
            # switcher and the layout toggle are measured from the same
            # place on every run.
            if ([double](Invoke-SeamCommand $s @{ op = 'get-state' }).state.paneWidth -lt 96) {
                [void](Invoke-SeamCommand $s @{ op = 'toggle-sidebar' })
                Start-Sleep -Milliseconds 600
            }
        }

        Write-Host "-- switcher"
        # The popup dismisses itself on a 1.2s timer, so the tile is located
        # first and the capture follows immediately, with no settle sleep in
        # between either of them.
        [void](Invoke-SeamCommand $s @{ op = 'cycle'; forward = $true })
        $tileRect = Get-SwitcherTileRect
        Measure-Switcher (New-Capture "$($leg.name)-switcher") "$($leg.name)/switcher" $tileRect
        Start-Sleep -Milliseconds 1400

        Write-Host "-- horizontal"
        # Folded first: the horizontal strip paints a group as a coloured
        # rail over its member tabs while the group is open, and only draws
        # the chip once it is collapsed. Measuring the chip means folding
        # it. The activity is parked off the members first, because the
        # active-visible rule keeps an active member out of the fold.
        [void](Invoke-SeamCommand $s @{ op = 'select'; index = 1 })
        [void](Invoke-SeamCommand $s @{ op = 'collapse'; index = 3; collapsed = $true; via = 'router' })
        $back = Invoke-SeamCommand $s @{ op = 'get-state' }
        for ($try = 0; $try -lt 3 -and $back.state.vertical; $try++) {
            $back = Invoke-SeamCommand $s @{ op = 'toggle-layout' }
            if ($back.state.vertical) { Start-Sleep -Milliseconds 900 }
        }
        if ($back.state.vertical) {
            Add-Unmeasured "$($leg.name)/horizontal" ("{0} surfaces" -f $HorizontalSurfaces.Count) `
                ('the layout toggle acked three times and the window stayed vertical, so the horizontal strip was never on screen (' +
                 ($HorizontalSurfaces -join ', ') + ')')
        } else {
            # The fold has to reach the strip before the shutter: the chip
            # is built on the collapse, and a capture taken on the ack found
            # a strip that had not drawn it yet.
            Start-Sleep -Milliseconds 1400
            Measure-HorizontalLeg (New-Capture "$($leg.name)-horizontal") "$($leg.name)/horizontal" $groupTitle
        }
    }
    catch {
        $legErr = "$($_.Exception.Message)"
        Write-Host ("LEG FAILED {0}: {1}" -f $leg.name, $legErr) -ForegroundColor Red
        $script:HarnessErrors.Add("$($leg.name) : $legErr")
    }
    finally {
        if ($null -ne $s) { Stop-SeamSession $s }
    }
    $script:LegVerdicts.Add([pscustomobject]@{ name = $leg.name; error = $legErr })
}

# ---- report ----------------------------------------------------------------

$result = [ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=<session token>); zero synthesized OS input'
    instrument = 'rendered pixels, sampled out of a screen capture of the app window'
    mutate = $Mutate
    thresholds = [ordered]@{
        text = @{ min = $script:CONTRAST_TEXT_AA; source = 'WCAG 2.1 SC 1.4.3 (and src/config/wintty_theme_test.zig)' }
        glyph = @{ min = $script:CONTRAST_NONTEXT; source = 'WCAG 2.1 SC 1.4.11 non-text contrast' }
        fill = @{ min = $script:CONTRAST_FILL_VISIBLE; source = 'src/config/wintty_theme_test.zig fill rule, strictly greater' }
    }
    blindSpots = @(
        'the floating group run label (TabRunLabel.cs:20-22) carries no AutomationProperties on purpose and cannot be located over UIA; the same ink is measured on the group chip and the vertical group header'
    )
    captures = $script:CaptureStates
    rows = $script:Rows
    findings = $script:Findings
    unmeasured = $script:HarnessErrors
    legs = $script:LegVerdicts
}
$result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

Write-Host ''
Write-Host 'leg                        surface                  class   ratio   floor  fg        bg        verdict'
Write-Host '-------------------------  -----------------------  ------  ------  -----  --------  --------  -------'
foreach ($r in $script:Rows) {
    if ($null -eq $r.ratio) {
        # 'n/a' and 'NOT MEASURED' are different answers and the table has
        # to say which: one is the product deliberately not painting the
        # surface here, the other is the oracle not knowing.
        $label = if ($r.note -like 'n/a:*') { 'n/a' } else { 'NOT MEASURED' }
        Write-Host ("{0,-25}  {1,-23}  {2,-6}  {3,6}  {4,5}  {5,-8}  {6,-8}  {7}" -f
            $r.leg, $r.surface, '-', '-', '-', '-', '-', $label)
        continue
    }
    $verdict = if ($r.pass) { 'pass' } else { 'FAIL' }
    Write-Host ("{0,-25}  {1,-23}  {2,-6}  {3,6:N2}  {4,5:N1}  {5,-8}  {6,-8}  {7}" -f
        $r.leg, $r.surface, $r.class, $r.ratio, $r.min, $r.fg, $r.bg, $verdict)
}

if ($script:Findings.Count -gt 0) {
    Write-Host ''
    Write-Host 'CONTRAST FAILURES' -ForegroundColor Red
    foreach ($f in $script:Findings) {
        Write-Host ("  {0} / {1}: {2:N2}:1 against a {3} floor of {4} ({5}); {6} on {7}" -f
            $f.leg, $f.surface, $f.ratio, $f.class, $f.min, $f.rule, $f.fg, $f.bg) -ForegroundColor Red
    }
}
if ($script:HarnessErrors.Count -gt 0) {
    Write-Host ''
    Write-Host 'NOT MEASURED (nothing is known about these)' -ForegroundColor Yellow
    foreach ($e in $script:HarnessErrors) { Write-Host "  $e" -ForegroundColor Yellow }
}

# A contrast failure is the verdict this harness exists to deliver, so it
# outranks an unmeasured surface: a run that found real failures reports
# them as 2 even if some other surface could not be located.
if ($script:Findings.Count -gt 0) { exit 2 }
if ($script:HarnessErrors.Count -gt 0) { exit 1 }
if ($script:Rows.Count -eq 0) {
    Write-Host 'HARVEST_MISS: nothing was measured at all' -ForegroundColor Red
    exit 1
}
exit 0
