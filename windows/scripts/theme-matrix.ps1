#requires -Version 7
<#
    The theme matrix (#937): every selected theme against desktop polarity,
    app material, frame material, tab layout and a backdrop scene, measured
    in RENDERED PIXELS and judged by the floors in lib/contrast.ps1.

    Why. contrast-oracle.ps1 guards four configs on whatever polarity the
    desktop is in; frame-style-fuzz.ps1 sweeps materials against the
    developer's own wallpaper. Neither answers the question a user has: with
    MY theme, in MY desktop mode, with the material I picked, in front of MY
    wallpaper and windows, is the chrome readable? That is a matrix, and this
    is it. It is exploratory by design: a red run is the expected outcome and
    the matrix.md it leaves is the deliverable, posted to #937.

    Axes. Every one takes one value, a comma list, or `all`:
      -Theme     names, `curated` (the built-in pair + 14 catalogue names),
                 or `all` (the whole staged catalogue)
      -Polarity  light | dark        the DESKTOP setting, flipped by this
                                     harness under a snapshot (see below)
      -App       solid | frosted | crystal        background-style
      -Frame     inherit | solid | frosted | crystal   frame-style; inherit
                                     writes no line, which is the product's
                                     "Match backdrop"
      -Layout    horizontal | vertical           vertical-tabs, toggled in
                                     process through the seam
      -Scene     names from lib/backdrop-stage.ps1's catalogue

    frosted and crystal cells carry background-opacity (-Opacity, 0.85):
    the product flattens crystal to solid at 1.0, so without it a crystal
    cell would measure Mica and call it crystal. solid cells set none.

    One process per (polarity, theme, app, frame). Inside it the scene is
    changed on the stage AND the wallpaper (Mica reads the wallpaper, acrylic
    and crystal read the stage), the change is verified on the stage's own
    margin before anything is photographed, and each layout is captured once
    per scene.

    The desktop is machine state, and this is the first harness here that
    SETS it. It runs the polarity the desktop is already in first, flips to
    the other, and puts everything back: polarity, wallpaper, and the env
    guard's snapshot with its read-back. -NoFlip keeps the read-only policy
    every other harness has, and reports the skipped polarity as unmeasured.
    High Contrast pins every material solid, so the run refuses under it
    rather than measure the pin.

    Themes are given to the config as ABSOLUTE PATHS under a staging copy of
    the catalogue: a name resolves against the XDG root the seam session
    isolates, and a name that resolves to nothing falls back silently to the
    compile-time default (#877, #878). Each process then proves the theme
    reached the glass by comparing the terminal ground it photographs with
    the theme file's own `background`; a process whose ground is not the
    theme's is reported unmeasured, never scored.

    Anti-vacuity: -Mutate terminal launches every cell with an illegible
    pair and the run must go red.

    Exit 0 clean, 2 findings, 1 the harness could not run or a surface went
    unmeasured. Findings outrank unmeasured, as in the oracle.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    [string[]]$Theme = @('curated'),
    [string[]]$Polarity = @('all'),
    [string[]]$App = @('all'),
    [string[]]$Frame = @('all'),
    [string[]]$Layout = @('all'),
    [string[]]$Scene = @('all'),
    # background-opacity for the frosted and crystal cells. The product
    # flattens crystal to solid at opacity 1.0 (MainWindow.ApplyBackdropStyle:
    # a fully opaque window has nothing to reveal), so a crystal cell without
    # this would measure Mica and call it crystal. solid cells never set it.
    [double]$Opacity = 0.85,
    [switch]$NoFlip,
    [switch]$DryRun,
    [ValidateSet('none', 'terminal')][string]$Mutate = 'none',
    # Where the shipped catalogue is read from. Default: the first that exists
    # of zig-out/share/ghostty/themes (a -Demit-themes build), the user's
    # wintty themes, the user's ghostty themes.
    [string]$ThemesDir = '',
    [int]$Seed = 1337,
    # Stop after this many processes, for a smoke. 0 is no cap.
    [int]$MaxCells = 0
)

. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
. (Join-Path $PSScriptRoot 'lib/contrast.ps1')
. (Join-Path $PSScriptRoot 'lib/backdrop-stage.ps1')
. (Join-Path $PSScriptRoot 'lib/env-guard.ps1')
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$UIA = [System.Windows.Automation.AutomationElement]
$TREE = [System.Windows.Automation.TreeScope]::Descendants
$CTRL = [System.Windows.Automation.ControlType]

if (-not ('MatrixWin' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MatrixWin {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    // Placed and raised without activating; a separate best-effort activate
    // because the chrome paints an inactive window differently.
    public static bool PlaceOnTop(IntPtr h, int x, int y, int w, int hh) { return SetWindowPos(h, HWND_TOPMOST, x, y, w, hh, 0x0010); }
    public static bool TryActivate(IntPtr h) { return SetForegroundWindow(h); }
    public static uint PidAt(int x, int y) { uint pid; GetWindowThreadProcessId(WindowFromPoint(new POINT { X = x, Y = y }), out pid); return pid; }
    public static uint ForegroundPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid; }
}
'@
}

# ---- axes ------------------------------------------------------------------

$AllApps = @('solid', 'frosted', 'crystal')
$AllFrames = @('inherit', 'solid', 'frosted', 'crystal')
$AllLayouts = @('horizontal', 'vertical')
$AllPolarities = @('light', 'dark')
$CuratedThemes = @(
    'wintty-light', 'wintty-dark',
    'Catppuccin Latte', 'Catppuccin Mocha', 'Gruvbox Light', 'Gruvbox Dark',
    'Nord Light', 'Nord', 'One Half Light', 'One Half Dark',
    'Rose Pine Dawn', 'Rose Pine', 'GitHub Light Default', 'GitHub Dark',
    'Zenburn', 'Dracula')

# One value, a comma list, or all: `just` hands the whole -X argument over as
# one string, so "a,b" has to mean two values here.
function Resolve-Axis([string[]]$Given, [string[]]$All, [string]$Name) {
    $values = @($Given | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($values -contains 'all') { return @($All) }
    foreach ($v in $values) {
        if ($All -notcontains $v) { throw "HARNESS: -$Name '$v' is not one of: $($All -join ', '), all" }
    }
    return @($values | Select-Object -Unique)
}

$apps = Resolve-Axis $App $AllApps 'App'
$frames = Resolve-Axis $Frame $AllFrames 'Frame'
$layouts = Resolve-Axis $Layout $AllLayouts 'Layout'
$polarities = Resolve-Axis $Polarity $AllPolarities 'Polarity'
$sceneCatalogue = Get-BackdropScenes
$scenes = @(Resolve-Axis $Scene @($sceneCatalogue.Keys) 'Scene' | ForEach-Object { $sceneCatalogue[$_] })

# ---- theme staging -----------------------------------------------------------

# The built-in halves, read out of the zig source at run time so a palette
# edit reaches this harness by itself (contrast-oracle.ps1 does the same).
function Get-BuiltinTheme([string]$Half) {
    $path = (Resolve-Path (Join-Path $PSScriptRoot '..\..\src\config\wintty_theme.zig') -ErrorAction SilentlyContinue)?.Path
    if (-not $path) { throw "HARNESS: cannot find src/config/wintty_theme.zig from $PSScriptRoot" }
    $body = [System.Collections.Generic.List[string]]::new(); $inside = $false
    foreach ($line in (Get-Content $path)) {
        if ($line -match "^pub const $Half\s*:") { $inside = $true; continue }
        if (-not $inside) { continue }
        if ($line.Trim() -eq ';') { break }
        $t = $line.Trim()
        if ($t.StartsWith('\\')) { $v = $t.Substring(2).Trim(); if ($v) { [void]$body.Add($v) } }
    }
    if ($body.Count -lt 20) { throw "HARNESS: parsed only $($body.Count) lines of the built-in '$Half' half; the source shape changed" }
    return ($body -join "`n")
}

if (-not $ThemesDir) {
    foreach ($candidate in @(
        (Join-Path $PSScriptRoot '..\..\zig-out\share\ghostty\themes'),
        (Join-Path $env:APPDATA 'wintty\themes'),
        (Join-Path $env:APPDATA 'Ghostty\themes'))) {
        if ((Test-Path -LiteralPath $candidate) -and @(Get-ChildItem -LiteralPath $candidate -File).Count -gt 0) { $ThemesDir = (Resolve-Path $candidate).Path; break }
    }
}
$runStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stageRoot = Join-Path $env:TEMP "wintty-theme-matrix-$runStamp"
$stagedThemes = Join-Path $stageRoot 'themes'
New-Item -ItemType Directory -Force -Path $stagedThemes | Out-Null
if ($ThemesDir) { Get-ChildItem -LiteralPath $ThemesDir -File | Copy-Item -Destination $stagedThemes }
[IO.File]::WriteAllText((Join-Path $stagedThemes 'wintty-light'), (Get-BuiltinTheme 'light') + "`n")
[IO.File]::WriteAllText((Join-Path $stagedThemes 'wintty-dark'), (Get-BuiltinTheme 'dark') + "`n")
$catalogue = @(Get-ChildItem -LiteralPath $stagedThemes -File | Select-Object -ExpandProperty Name | Sort-Object)

$themeValues = @($Theme | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$themes = [System.Collections.Generic.List[string]]::new()
$skippedThemes = [System.Collections.Generic.List[string]]::new()
foreach ($t in $themeValues) {
    $want = switch ($t) { 'all' { $catalogue } 'curated' { $CuratedThemes } default { @($t) } }
    foreach ($name in $want) {
        if ($catalogue -contains $name) { if ($themes -notcontains $name) { $themes.Add($name) } }
        else { $skippedThemes.Add($name) }
    }
}
if ($themes.Count -eq 0) { throw "HARNESS: no selected theme exists in the staged catalogue ($($catalogue.Count) names from '$ThemesDir')" }

# The colour the theme says its ground is, so a process can prove the theme
# it was given is the theme on the glass.
function Get-ThemeBackground([string]$Name) {
    foreach ($line in (Get-Content (Join-Path $stagedThemes $Name))) {
        if ($line -match '^\s*background\s*=\s*#?([0-9A-Fa-f]{6})\s*$') { return '#' + $Matches[1].ToUpperInvariant() }
    }
    return $null
}

# ---- the plan ----------------------------------------------------------------

$current = if ([int](Get-ItemProperty -LiteralPath $script:PersonalizeKey -ErrorAction SilentlyContinue).AppsUseLightTheme -eq 0) { 'dark' } else { 'light' }
# Current polarity first, so a -NoFlip run and the first half of a flipping
# run measure the same thing.
$polarityOrder = @(@($polarities | Where-Object { $_ -eq $current }) + @($polarities | Where-Object { $_ -ne $current }))

$cells = [System.Collections.Generic.List[object]]::new()
foreach ($p in $polarityOrder) { foreach ($t in $themes) { foreach ($a in $apps) { foreach ($f in $frames) {
    $cells.Add([pscustomobject]@{ Polarity = $p; Theme = $t; App = $a; Frame = $f
        Id = ('{0}/{1}/{2}-{3}' -f $p, $t, $a, $f) })
} } } }
if ($MaxCells -gt 0 -and $cells.Count -gt $MaxCells) { $cells = [System.Collections.Generic.List[object]]@($cells | Select-Object -First $MaxCells) }
$captures = $cells.Count * $scenes.Count * $layouts.Count
$estimateMin = [Math]::Round(($cells.Count * 9 + $captures * 2.4 + ($polarityOrder.Count - 1) * 10) / 60.0, 1)

Write-Host ("theme-matrix: {0} processes, {1} captures, ~{2} min; polarity now {3}, order {4}{5}" -f
    $cells.Count, $captures, $estimateMin, $current, ($polarityOrder -join ' then '), $(if ($NoFlip) { ' (no flip)' } else { '' }))
Write-Host ("  themes {0}: {1}{2}" -f $themes.Count, (($themes | Select-Object -First 16) -join ', '), $(if ($themes.Count -gt 16) { ', ...' } else { '' }))
if ($skippedThemes.Count -gt 0) { Write-Host ("  not in catalogue, skipped: {0}" -f ($skippedThemes -join ', ')) -ForegroundColor Yellow }
Write-Host ("  app {0}; frame {1}; layout {2}; scene {3}" -f ($apps -join ','), ($frames -join ','), ($layouts -join ','), (($scenes | ForEach-Object Name) -join ','))
if ($DryRun) {
    foreach ($c in $cells) { Write-Host ("  {0}" -f $c.Id) }
    Remove-Item $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    exit 0
}

# ---- refusals before anything moves ----------------------------------------

if (-not (Test-Path -LiteralPath $ExePath)) { Write-Host "HARVEST_MISS: missing exe: $ExePath"; exit 1 }
if (((Get-HighContrastFlags) -band 1) -ne 0) {
    Write-Host 'HARNESS: High Contrast is on, which pins every material solid; nothing here would measure a material. Refusing.' -ForegroundColor Red
    exit 1
}
New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots'), (Join-Path $OutDir 'scenes') | Out-Null
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

# ---- measurement -------------------------------------------------------------

$WinX = 160; $WinY = 120; $WinW = 1500; $WinH = 950
$Margin = 120
$script:Rows = [System.Collections.Generic.List[object]]::new()
$script:Findings = [System.Collections.Generic.List[object]]::new()
$script:Unmeasured = [System.Collections.Generic.List[object]]::new()
$script:Deltas = [System.Collections.Generic.List[object]]::new()
$script:Cell = $null; $script:LayoutName = ''; $script:SceneName = ''; $script:Shot = ''
$script:MainHwnd64 = 0; $script:ProcId = 0

function Add-Row([string]$Surface, [string]$Class, [double]$Ratio, [string]$Fg, [string]$Bg, [string]$Note) {
    $rule = Get-ContrastRule $Class
    $pass = Test-ContrastPasses $Ratio $Class
    $row = [pscustomobject][ordered]@{
        polarity = $script:Cell.Polarity; theme = $script:Cell.Theme; app = $script:Cell.App; frame = $script:Cell.Frame
        layout = $script:LayoutName; scene = $script:SceneName; surface = $Surface; class = $Class
        ratio = [Math]::Round($Ratio, 2); min = $rule.Min; rule = $rule.Source; fg = $Fg; bg = $Bg; pass = $pass; note = $Note; shot = $script:Shot
    }
    $script:Rows.Add($row)
    if (-not $pass) { $script:Findings.Add($row) }
    Write-Host ("    {0} {1,-22} {2,6:N2}:1  {3} on {4}" -f $(if ($pass) { 'ok  ' } else { 'FAIL' }), $Surface, $Ratio, $Fg, $Bg)
}

# Not a pass: nothing is known, and the run leaves with 1 for it.
function Add-Unmeasured([string]$Surface, [string]$Why) {
    $script:Unmeasured.Add([pscustomobject][ordered]@{
        polarity = $script:Cell.Polarity; theme = $script:Cell.Theme; app = $script:Cell.App; frame = $script:Cell.Frame
        layout = $script:LayoutName; scene = $script:SceneName; surface = $Surface; why = $Why; shot = $script:Shot })
    Write-Host ("    ??   {0,-22} not measured: {1}" -f $Surface, $Why) -ForegroundColor Yellow
}

# Recorded, never judged: the distances #897 and #878 keep asking for.
function Add-Delta([string]$Name, $A, $B) {
    $ratio = [ContrastMath]::Ratio($A.BgR, $A.BgG, $A.BgB, $B.BgR, $B.BgG, $B.BgB)
    $script:Deltas.Add([pscustomobject][ordered]@{
        polarity = $script:Cell.Polarity; theme = $script:Cell.Theme; app = $script:Cell.App; frame = $script:Cell.Frame
        layout = $script:LayoutName; scene = $script:SceneName; delta = $Name; ratio = [Math]::Round($ratio, 2); a = $A.BgHex; b = $B.BgHex })
    Write-Host ("    ..   {0,-22} {1,6:N2}:1  {2} vs {3}" -f $Name, $ratio, $A.BgHex, $B.BgHex)
}

function Get-UiaRoot { return $UIA::FromHandle([SeamWin]::P($script:MainHwnd64)) }
function Find-ById([string]$Id, [int]$ms = 3000) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $Id)
    $dl = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $dl) {
        $el = (Get-UiaRoot).FindFirst($TREE, $cond)
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 120
    }
    return $null
}
function Get-Kids($el, $ControlType) {
    if ($null -eq $el) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $ControlType)
    return @($el.FindAll($TREE, $cond) | Where-Object {
        $r = $_.Current.BoundingRectangle
        $off = try { $_.Current.IsOffscreen } catch { $true }
        -not [double]::IsNaN($r.X) -and $r.Width -gt 2 -and $r.Height -gt 2 -and -not $off })
}
function Test-Selected($el) {
    try { return $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } catch { return $false }
}
# The title is the run that SAYS the title, not the leftmost run: a pin
# glyph or an icon sits before it (contrast-oracle.ps1 learned this).
function Get-TitleRun($el) {
    return @(Get-Kids $el $CTRL::Text | Where-Object { $_.Current.Name -eq $el.Current.Name }) | Select-Object -First 1
}

# The strip's tabs, either layout: El, Selected, Rect, Close (which may be
# null: a row can hide its close button until hovered, and a tab without
# one is still a tab). The seeded state has no groups and no pins, so every
# item that says a title is a tab.
function Get-StripTabs([string]$LayoutName) {
    if ($LayoutName -eq 'vertical') {
        $nav = Find-ById 'NavView'
        if ($null -eq $nav) { throw 'HARVEST_MISS: no NavView' }
        $items = @(Get-Kids $nav $CTRL::ListItem | Where-Object { $_.Current.ItemStatus -notmatch 'Pinned' })
    } else {
        $tv = Find-ById 'TabViewControl'
        if ($null -eq $tv) { throw 'HARVEST_MISS: no TabViewControl' }
        $items = @(Get-Kids $tv $CTRL::TabItem)
    }
    return @($items | Where-Object { $_.Current.Name } | ForEach-Object {
        $close = @(Get-Kids $_ $CTRL::Button) | Select-Object -First 1
        [pscustomobject]@{ El = $_; Selected = (Test-Selected $_); Rect = $_.Current.BoundingRectangle; Close = $close; Name = $_.Current.Name }
    } | Sort-Object { $_.Rect.Y * 10000 + $_.Rect.X })
}

function New-Capture {
    $rc = [SeamWin]::RectOf($script:MainHwnd64)
    if ($null -eq $rc) { throw 'HARVEST_MISS: degenerate window rect' }
    for ($gx = 1; $gx -le 6; $gx++) { for ($gy = 1; $gy -le 6; $gy++) {
        $px = [int]($rc.L + $rc.W * $gx / 7.0); $py = [int]($rc.T + $rc.Hh * $gy / 7.0)
        $owner = [MatrixWin]::PidAt($px, $py)
        if ($owner -ne $script:ProcId) { throw "OCCLUDED: $px,$py inside the window belongs to pid $owner, not $($script:ProcId); nothing measured" }
    } }
    $bmp = [System.Drawing.Bitmap]::new($rc.W, $rc.Hh)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size); $g.Dispose()
    $script:Shot = ('{0}-{1}-{2}-{3}-{4}-{5}.png' -f $script:Cell.Polarity, ($script:Cell.Theme -replace '[^A-Za-z0-9]', '_'), $script:Cell.App, $script:Cell.Frame, $script:SceneName, $script:LayoutName)
    $bmp.Save((Join-Path $OutDir "shots\$($script:Shot)"))
    return [pscustomobject]@{ Bmp = $bmp; L = $rc.L; T = $rc.T; W = $rc.W; H = $rc.Hh; Activated = ([MatrixWin]::ForegroundPid() -eq $script:ProcId) }
}

function ConvertTo-Local($Cap, $Rect, [int]$Inset = 1) {
    if ($null -eq $Rect -or [double]::IsNaN($Rect.X)) { return $null }
    $x = [int][Math]::Round($Rect.X) - $Cap.L + $Inset; $y = [int][Math]::Round($Rect.Y) - $Cap.T + $Inset
    $w = [int][Math]::Round($Rect.Width) - 2 * $Inset; $h = [int][Math]::Round($Rect.Height) - 2 * $Inset
    if ($x -lt 0) { $w += $x; $x = 0 }; if ($y -lt 0) { $h += $y; $y = 0 }
    if ($x + $w -gt $Cap.W) { $w = $Cap.W - $x }; if ($y + $h -gt $Cap.H) { $h = $Cap.H - $y }
    if ($w -le 2 -or $h -le 2) { return $null }
    return @{ X = $x; Y = $y; W = $w; H = $h }
}
function Sample($Cap, $Rect, [int]$Inset = 1) {
    $l = ConvertTo-Local $Cap $Rect $Inset
    if ($null -eq $l) { return $null }
    return [ContrastSampler]::Region($Cap.Bmp, $l.X, $l.Y, $l.W, $l.H)
}
function Measure-Ink([string]$Surface, [string]$Class, $Cap, $Rect, [int]$Inset = 1, [string]$Note = '') {
    $s = Sample $Cap $Rect $Inset
    if ($null -eq $s) { Add-Unmeasured $Surface 'no usable rect in the capture'; return $null }
    if (-not $s.Ok) { Add-Unmeasured $Surface $s.Why; return $null }
    Add-Row $Surface $Class $s.Ratio $s.FgHex $s.BgHex $Note
    return $s
}
function New-Rect([double]$X, [double]$Y, [double]$W, [double]$H) { return New-Object System.Windows.Rect($X, $Y, $W, $H) }

# One photograph, five judged surfaces and three deltas.
function Measure-Capture($Cap, [string]$LayoutName, [string]$ThemeBg, $SceneGround, [bool]$GroundIsPicture = $false) {
    $tabs = Get-StripTabs $LayoutName
    $active = @($tabs | Where-Object Selected) | Select-Object -First 1
    $idle = @($tabs | Where-Object { -not $_.Selected }) | Select-Object -First 1

    # The terminal's ground, read from a band low and to the right where no
    # prompt reaches, clear of the DWM border band. Judged first, because it
    # decides whether the theme is on the glass at all.
    $ground = Sample $Cap (New-Rect ($Cap.L + $Cap.W - 220) ($Cap.T + $Cap.H - 160) 48 48) 0
    if ($null -eq $ground -or -not $ground.Ok) { Add-Unmeasured 'terminal-ground' 'the terminal band could not be sampled'; return }
    # $ThemeBg is what the ground should read: the theme's own value on an
    # opaque terminal, the theme blended with the scene on a translucent one
    # over a flat scene, and null over an image scene at opacity below 1,
    # where the ground is the picture and nothing can be proven from it.
    if ($ThemeBg) {
        $expect = ConvertTo-DrawingColor $ThemeBg
        $off = [Math]::Abs($ground.BgR - $expect.R) + [Math]::Abs($ground.BgG - $expect.G) + [Math]::Abs($ground.BgB - $expect.B)
        # Wide, because the compositor's blend and the sampler's bucketing
        # both move the number; the compile-time default (#282C34) a silent
        # fallback lands on is dozens of counts per channel away from any
        # theme this is likely to be run with.
        if ($off -gt 60) {
            Add-Unmeasured 'theme-on-glass' ("the terminal ground is {0}, it should read {1}: the theme did not reach the glass, so nothing here is scored" -f $ground.BgHex, $ThemeBg)
            return
        }
    }

    if ($null -eq $active) { Add-Unmeasured 'tab-title-active' 'no tab reports itself selected'; Add-Unmeasured 'tab-close-active' 'no tab reports itself selected'; Add-Unmeasured 'tab-field' 'no tab reports itself selected' }
    else {
        $t = Get-TitleRun $active.El
        if ($null -eq $t) { Add-Unmeasured 'tab-title-active' 'the selected tab exposes no Text run saying its title' }
        else { [void](Measure-Ink 'tab-title-active' 'text' $Cap $t.Current.BoundingRectangle 1 "tab '$($active.Name)'") }
        if ($null -eq $active.Close) { Add-Unmeasured 'tab-close-active' 'the selected tab exposes no close Button' }
        else { [void](Measure-Ink 'tab-close-active' 'glyph' $Cap $active.Close.Current.BoundingRectangle 2 'the close X') }
        # The active tab is the field: its fill must BE the terminal ground.
        # A slice clear of the ink: the top band of a horizontal tab, the
        # trailing end of a vertical row (where contrast-oracle reads it).
        $r = $active.Rect
        $slice = if ($LayoutName -eq 'vertical') { New-Rect ($r.X + $r.Width - [Math]::Max(6.0, $r.Width * 0.12) - 2) ($r.Y + 3) ([Math]::Max(6.0, $r.Width * 0.12)) ([Math]::Max(6.0, $r.Height - 6)) }
                 else { New-Rect ($r.X + 14) ($r.Y + 3) ([Math]::Max(6.0, $r.Width - 28)) 4.0 }
        $fill = Sample $Cap $slice 0
        if ($null -eq $fill -or -not $fill.Ok) { Add-Unmeasured 'tab-field' 'the fill slice could not be sampled' }
        else { Add-Row 'tab-field' 'field' ([ContrastMath]::Ratio($fill.BgR, $fill.BgG, $fill.BgB, $ground.BgR, $ground.BgG, $ground.BgB)) $fill.BgHex $ground.BgHex 'the active tab fill against the terminal it must match' }
    }

    $strip = $null
    if ($null -eq $idle) { Add-Unmeasured 'tab-title-inactive' 'no unselected tab' }
    else {
        $t = Get-TitleRun $idle.El
        if ($null -eq $t) { Add-Unmeasured 'tab-title-inactive' 'the unselected tab exposes no Text run saying its title' }
        else { $strip = Measure-Ink 'tab-title-inactive' 'text' $Cap $t.Current.BoundingRectangle 1 "tab '$($idle.Name)'" }
    }

    # The shell prompt against its ground: of the bands walked down the top
    # of the surface, the one with the MOST contrast in it. The first band
    # with ink would do on an opaque terminal, but a translucent one over a
    # busy scene has "ink" in every band, and the prompt is the one where the
    # real glyphs beat the scene's own variance.
    $left = if ($LayoutName -eq 'vertical') { ($tabs | ForEach-Object { $_.Rect.X + $_.Rect.Width } | Measure-Object -Maximum).Maximum + 12 } else { $Cap.L + 12 }
    $top = if ($LayoutName -eq 'vertical') { $Cap.T + 44 } else { ($tabs | ForEach-Object { $_.Rect.Y + $_.Rect.Height } | Measure-Object -Maximum).Maximum + 12 }
    #
    # Over a picture, through a translucent terminal, there is no flat
    # ground for the prompt to be read against: the band with the prompt is
    # the one the sampler refuses (no dominant cluster), and the band it
    # accepts is one with no prompt in it, which scored a legible prompt as
    # 1.3:1. Judging text on a busy ground needs a local metric this harness
    # does not have (noted in #937), so it is reported as not measured.
    $best = $null
    if ($GroundIsPicture) {
        Add-Unmeasured 'terminal-fg-on-bg' 'the terminal is translucent over a picture; text on a busy ground needs a local metric this harness does not have'
        $best = 'skipped'
    }
    for ($step = 0; $step -lt 6 -and $best -isnot [string]; $step++) {
        $s = Sample $Cap (New-Rect $left ($top + $step * 40.0) ([Math]::Min(520.0, ($Cap.L + $Cap.W - 12) - $left)) 40.0) 0
        if ($null -ne $s -and $s.Ok -and $s.FgCount -gt 0 -and ($null -eq $best -or $s.Ratio -gt $best.Ratio)) { $best = $s }
    }
    if ($best -is [string]) { }
    elseif ($null -ne $best) { Add-Row 'terminal-fg-on-bg' 'text' $best.Ratio $best.FgHex $best.BgHex 'the shell prompt against the terminal ground' }
    else { Add-Unmeasured 'terminal-fg-on-bg' 'every sampled band on the terminal surface is flat' }

    # The deltas. The strip ground is the inactive tab's plurality colour,
    # which is the chrome as it rendered under this material.
    if ($null -ne $strip) {
        Add-Delta 'strip-vs-terminal' $strip $ground
        if ($null -ne $SceneGround) { Add-Delta 'strip-vs-scene' $strip $SceneGround }
    }
}

# ---- desktop polarity --------------------------------------------------------

function Get-DesktopPolarity {
    $v = (Get-ItemProperty -LiteralPath $script:PersonalizeKey -ErrorAction SilentlyContinue).AppsUseLightTheme
    return $(if ($null -ne $v -and [int]$v -eq 0) { 'dark' } else { 'light' })
}
# Both Personalize values and the broadcast Settings sends, then a read-back.
# Explorer, DWM and UISettings all pick the change up from the broadcast, and
# the app launched afterwards reads the system value fresh.
function Set-DesktopPolarity([string]$P) {
    $v = $(if ($P -eq 'light') { 1 } else { 0 })
    Set-ItemProperty -LiteralPath $script:PersonalizeKey -Name 'AppsUseLightTheme' -Value $v -Type DWord
    Set-ItemProperty -LiteralPath $script:PersonalizeKey -Name 'SystemUsesLightTheme' -Value $v -Type DWord
    Send-SettingChange 'ImmersiveColorSet'
    Start-Sleep -Seconds 4
    if ((Get-DesktopPolarity) -ne $P) { throw "HARNESS: the desktop polarity did not read back as $P" }
}

# ---- run ---------------------------------------------------------------------

$startedUtc = (Get-Date).ToUniversalTime().ToString('o')
$snapshotPath = Save-EnvSnapshot
$wallpaperBefore = Get-DesktopWallpaper
$polarityBefore = Get-DesktopPolarity
$stage = $null
$cellVerdicts = [System.Collections.Generic.List[object]]::new()
$fatal = ''
$titles = @('alpha', 'bravo', 'charlie')
$sceneDir = Join-Path $OutDir 'scenes'
$marginPoint = @{ X = $WinX - [int]($Margin / 2); Y = $WinY + [int]($WinH / 2) }

try {
    $stage = Start-BackdropStage -X ($WinX - $Margin) -Y ($WinY - $Margin) -W ($WinW + 2 * $Margin) -H ($WinH + 2 * $Margin)
    $activePolarity = $polarityBefore
    foreach ($cell in $cells) {
        if ($cell.Polarity -ne $activePolarity) {
            if ($NoFlip) {
                $script:Cell = $cell; $script:LayoutName = '*'; $script:SceneName = '*'; $script:Shot = ''
                Add-Unmeasured 'cell' "-NoFlip: the desktop is $activePolarity and this cell needs $($cell.Polarity)"
                continue
            }
            Write-Host ("=== flipping the desktop to {0} ===" -f $cell.Polarity) -ForegroundColor Cyan
            Set-DesktopPolarity $cell.Polarity
            $activePolarity = $cell.Polarity
            # The flip recreates the stage's window underneath WinForms; the
            # placement is re-asserted so it is topmost again before the
            # next window under test is placed above it.
            [void](Invoke-BackdropStage $stage @{ op = 'place'; x = ($WinX - $Margin); y = ($WinY - $Margin); w = ($WinW + 2 * $Margin); h = ($WinH + 2 * $Margin) })
        }
        $script:Cell = $cell
        Write-Host ""
        Write-Host ("=== {0} ===" -f $cell.Id) -ForegroundColor Cyan
        $firstLayout = $layouts[0]
        $themePath = Join-Path $stagedThemes $cell.Theme
        $config = @(
            'windows-single-instance = false', 'window-save-state = never', 'windows-settings-ui = true',
            ('vertical-tabs = ' + $(if ($firstLayout -eq 'vertical') { 'true' } else { 'false' })),
            'vertical-tabs-hover-expand = false', 'vertical-tabs-pinned = true',
            "theme = $themePath",
            "background-style = $($cell.App)")
        if ($cell.Frame -ne 'inherit') { $config += "frame-style = $($cell.Frame)" }
        if ($cell.App -ne 'solid') { $config += ('background-opacity = {0}' -f $Opacity.ToString('F2', [System.Globalization.CultureInfo]::InvariantCulture)) }
        $themeBg = Get-ThemeBackground $cell.Theme
        if ($Mutate -eq 'terminal') { $config += 'background = #808080', 'foreground = #858585'; $themeBg = '#808080' }
        $translucent = ($cell.App -ne 'solid') -or ($cell.Frame -in @('frosted', 'crystal'))

        $s = $null; $cellErr = ''
        try {
            $s = Start-SeamSession -ExePath $ExePath -ConfigText ($config -join "`n")
            $script:MainHwnd64 = $s.Hwnd64; $script:ProcId = [uint32]$s.Proc.Id
            $hwnd = [SeamWin]::P($script:MainHwnd64)
            [void][MatrixWin]::PlaceOnTop($hwnd, $WinX, $WinY, $WinW, $WinH)
            [void][MatrixWin]::TryActivate($hwnd)
            [void](Invoke-SeamCommand $s @{ op = 'seed-tabs'; count = 3; titles = $titles })
            [void](Invoke-SeamCommand $s @{ op = 'select'; index = 1 })
            Start-Sleep -Seconds 3

            # $sc, not $scene: the loop variable would be the typed [string[]]
            # -Scene parameter, and assigning the scene object to it coerces
            # the object to strings and loses its Name.
            foreach ($sc in $scenes) {
                $script:SceneName = $sc.Name
                [void](Set-BackdropScene -Stage $stage -Scene $sc -SceneDir $sceneDir -Wallpaper -Seed $Seed)
                Start-Sleep -Milliseconds $(if ($translucent) { 1800 } else { 500 })
                # The scene is proven on the stage's own margin before the
                # shutter: the pixel there must belong to the stage, and for a
                # flat scene must be its colour.
                # The stage can lose its topmost standing: a polarity flip
                # makes WinForms recreate its window, and the developer's
                # own terminal was found sitting over the margin right after
                # one. Re-asserting the placement puts it back on top; three
                # beats and it is a real occlusion, refused rather than
                # measured through.
                $owner = 0
                for ($attempt = 1; $attempt -le 3; $attempt++) {
                    $owner = [MatrixWin]::PidAt($marginPoint.X, $marginPoint.Y)
                    if ($owner -eq [uint32]$stage.Proc.Id) { break }
                    [void](Invoke-BackdropStage $stage @{ op = 'place'; x = ($WinX - $Margin); y = ($WinY - $Margin); w = ($WinW + 2 * $Margin); h = ($WinH + 2 * $Margin) })
                    [void][MatrixWin]::PlaceOnTop($hwnd, $WinX, $WinY, $WinW, $WinH)
                    Start-Sleep -Milliseconds 800
                }
                $marginPx = Get-ScreenPixel -X $marginPoint.X -Y $marginPoint.Y
                if ($owner -ne [uint32]$stage.Proc.Id) { throw "OCCLUDED: the stage margin at $($marginPoint.X),$($marginPoint.Y) belongs to pid $owner, not the stage" }
                if ($sc.Kind -eq 'solid' -and -not (Test-PixelNear $marginPx $sc.Color 3)) { throw "HARNESS: the stage margin reads $($marginPx.Hex), not the $($sc.Name) scene's $($sc.Color)" }
                $sceneGround = $(if ($sc.Kind -eq 'solid') { [pscustomobject]@{ BgR = $marginPx.R; BgG = $marginPx.G; BgB = $marginPx.B; BgHex = $marginPx.Hex } } else { $null })
                # What the terminal ground should read under this cell's
                # opacity: the theme itself when opaque, the theme blended
                # with a flat scene when not, nothing provable over a picture.
                $expectBg = $themeBg
                if ($cell.App -ne 'solid' -and $themeBg) {
                    if ($sc.Kind -eq 'solid') {
                        $tc = ConvertTo-DrawingColor $themeBg
                        $expectBg = '#{0:X2}{1:X2}{2:X2}' -f [int][Math]::Round($tc.R * $Opacity + $marginPx.R * (1 - $Opacity)),
                            [int][Math]::Round($tc.G * $Opacity + $marginPx.G * (1 - $Opacity)),
                            [int][Math]::Round($tc.B * $Opacity + $marginPx.B * (1 - $Opacity))
                    } else { $expectBg = $null }
                }

                foreach ($layoutName in $layouts) {
                    $script:LayoutName = $layoutName
                    $wantVertical = ($layoutName -eq 'vertical')
                    $state = Invoke-SeamCommand $s @{ op = 'get-state' }
                    for ($try = 0; $try -lt 3 -and ([bool]$state.state.vertical -ne $wantVertical); $try++) {
                        $state = Invoke-SeamCommand $s @{ op = 'toggle-layout' }
                        if ([bool]$state.state.vertical -ne $wantVertical) { Start-Sleep -Milliseconds 900 }
                    }
                    if ([bool]$state.state.vertical -ne $wantVertical) {
                        $script:Shot = ''
                        Add-Unmeasured 'layout' "the layout toggle acked three times and the window stayed $(if ($state.state.vertical) { 'vertical' } else { 'horizontal' })"
                        continue
                    }
                    Start-Sleep -Milliseconds 700
                    Write-Host ("-- {0} / {1}" -f $sc.Name, $layoutName)
                    $cap = New-Capture
                    try { Measure-Capture $cap $layoutName $expectBg $sceneGround ($cell.App -ne 'solid' -and $sc.Kind -ne 'solid') }
                    finally { $cap.Bmp.Dispose() }
                }
            }
        }
        catch {
            $cellErr = "$($_.Exception.Message)"
            Write-Host ("CELL FAILED {0}: {1}" -f $cell.Id, $cellErr) -ForegroundColor Red
            # The frame, because a binding error names a parameter and not
            # the call that bound it.
            Write-Host ("  at " + (($_.ScriptStackTrace -split "`n" | Select-Object -First 3) -join "`n  at ")) -ForegroundColor DarkGray
            $script:Shot = ''
            Add-Unmeasured 'cell' $cellErr
        }
        finally {
            if ($null -ne $s) { Stop-SeamSession $s }
        }
        $cellVerdicts.Add([pscustomobject]@{ id = $cell.Id; error = $cellErr })
    }
}
catch {
    $fatal = "$($_.Exception.Message)"
    Write-Host "RUN FAILED: $fatal" -ForegroundColor Red
}
finally {
    # Everything back, in the order that makes the read-back true: the
    # wallpaper through the API that actually applies one, the polarity
    # through the broadcast, then the env guard's own restore and read-back.
    if ($null -ne $stage) { try { Stop-BackdropStage $stage } catch { } }
    try { Set-DesktopWallpaper -Path $wallpaperBefore.Path -Style $wallpaperBefore.Style -Tile $wallpaperBefore.Tile } catch { Write-Host "RESTORE: wallpaper: $_" -ForegroundColor Red }
    try { if ((Get-DesktopPolarity) -ne $polarityBefore) { Set-DesktopPolarity $polarityBefore } } catch { Write-Host "RESTORE: polarity: $_" -ForegroundColor Red }
    try { Restore-EnvSnapshot -Path $snapshotPath } catch { Write-Host "RESTORE: env guard: $_ (run 'just env-restore')" -ForegroundColor Red }
    Remove-Item $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- report ------------------------------------------------------------------

$buildSha = try { (git -C $PSScriptRoot rev-parse --short HEAD 2>$null) } catch { '' }
$result = [ordered]@{
    harness = 'theme-matrix.ps1'
    issue = 937
    exe = (Resolve-Path $ExePath).Path
    buildSha = "$buildSha"
    startedUtc = $startedUtc
    finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
    machine = [ordered]@{ polarityBefore = $polarityBefore; wallpaperBefore = $wallpaperBefore.Path; noFlip = [bool]$NoFlip; screen = (Get-VirtualScreenRect) }
    filters = [ordered]@{ theme = $Theme; polarity = $Polarity; app = $App; frame = $Frame; layout = $Layout; scene = $Scene; opacity = $Opacity; maxCells = $MaxCells; mutate = $Mutate }
    axes = [ordered]@{ themes = @($themes); skippedThemes = @($skippedThemes); polarities = @($polarityOrder); apps = @($apps); frames = @($frames); layouts = @($layouts); scenes = @($scenes | ForEach-Object Name); themesDir = $ThemesDir }
    thresholds = [ordered]@{
        text = @{ min = $script:CONTRAST_TEXT_AA; sense = '>=' }; glyph = @{ min = $script:CONTRAST_NONTEXT; sense = '>=' }
        field = @{ min = $script:CONTRAST_FIELD_SAME; sense = '<=' } }
    cells = $cellVerdicts
    rows = $script:Rows
    deltas = $script:Deltas
    findings = $script:Findings
    unmeasured = $script:Unmeasured
    fatal = $fatal
}
$result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'theme-matrix-report.ps1') -RunDir $OutDir | Out-Null

Write-Host ""
Write-Host ("theme-matrix: {0} rows, {1} findings, {2} unmeasured, {3} deltas -> {4}" -f
    $script:Rows.Count, $script:Findings.Count, $script:Unmeasured.Count, $script:Deltas.Count, (Join-Path $OutDir 'matrix.md'))
if ($script:Findings.Count -gt 0) { exit 2 }
if ($fatal -or $script:Unmeasured.Count -gt 0 -or $script:Rows.Count -eq 0) { exit 1 }
exit 0
