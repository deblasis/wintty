#requires -Version 7
<#
    The command palette's theme mode, seam-actuated end to end (issue #1081).

    One app on a temp config root and a temp state base, four staged themes
    with distinctive colours (three dark, one light), and no theme configured
    (the case where the user never set one). Each step reads the theme back
    three ways:

      - the terminal's config, as the live native config states it (the
        preview's while one is up): what every surface renders from;
      - the chrome's resolved colours, all sixteen palette entries included;
      - pixels: a patch of the active terminal's background, captured from
        the screen with the window placed on top without activation.

    And the list itself, as drawn: every theme row's swatch (background,
    foreground, cursor, eight ANSI colours) against its theme file, in the
    model and in pixels; its accessible name ("current theme"), light/dark
    hint and badge (Current / Previewing); one row size throughout; the
    footer hint and the search box's name in theme mode.

    Scenario:

      1. open the palette, run "Change Theme", arrow down twice and back up:
         every move re-themes the live terminal, the config file is untouched;
      2. a held key (30 presses back to back) settles on the last row, and
         Page Up / Page Down move a screenful;
      3. a fast arrow run across dark and light themes, sampled on screen
         while the keys land: the terminal only ever shows one of the themes
         (never a default or an in-between frame) and the palette never flips
         between its light and dark surface;
      4. Escape: every readout equals the pre-palette baseline exactly, and the
         config file is byte for byte what it was;
      5. reopen, pick a theme, Enter: the file gains exactly one line,
         `theme = <name>`, and the live views stay on it;
      6. relaunch on that exact file: the theme is still there at startup, the
         highlight opens on it (marked Current), and a browse-then-Escape
         returns to it exactly;
      7. two short launches with window-theme = dark and = light, each
         previewing a dark and then a light theme.

    Screenshots of the palette theme mode (window captures of this run's own
    instance, PNG) land in -ShotsDir: both app themes with both a dark and a
    light previewed theme, the auto case, and the Current/Previewing pair.

    Zero OS input is synthesized. The single-instance election is off in the
    staged config and the state base is private, so a Wintty the user is
    running (another exe path, another election name, another state tree) is
    neither reached nor read, and is never stopped: cleanup kills only
    processes started from -ExePath after this run began.

    Exits 0 on pass, 2 on a product finding, 1 when the harness could not
    run and nothing is known about the product.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # Skip the pixel oracle and the screenshots (a desktop where the window
    # cannot be captured).
    [switch]$NoPixels,
    # Where the screenshots go. Defaults to <OutDir>\shots.
    [string]$ShotsDir = ''
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (-not $ShotsDir) { $ShotsDir = Join-Path $OutDir 'shots' }
New-Item -ItemType Directory -Force -Path $ShotsDir | Out-Null
Add-Type -AssemblyName System.Drawing
# One coordinate space for every rect this harness reads (-4 =
# PER_MONITOR_AWARE_V2), matching the seam's screen-pixel rects.
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

# The window's visible bounds, without the invisible resize border that
# GetWindowRect includes on Windows 11, so a screenshot is the window only.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ShotWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    public static int[] Bounds(long hwnd) {
        RECT r;
        if (DwmGetWindowAttribute(new IntPtr(hwnd), 9, out r, Marshal.SizeOf(typeof(RECT))) != 0) return null;
        return new[] { r.L, r.T, r.R - r.L, r.B - r.T };
    }
}
'@ -ErrorAction SilentlyContinue

$exeFull = (Resolve-Path $ExePath).Path

# Refuse only an instance of THIS build. Assert-NoWintty refuses any Wintty
# at all because it reads the shared crash.log; this run reads nothing
# shared (private state base below), so a Wintty from another path is none
# of its business and is left alone.
$mine = @(Get-Process Wintty -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -eq $exeFull } catch { $false } })
if ($mine.Count -gt 0) {
    Write-Host "HARNESS: close the Wintty running from $exeFull first (pid $($mine.Id -join ', '))"
    exit 1
}

$stateBase = Join-Path $env:TEMP "wintty-theme-state-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $stateBase | Out-Null
$origStateBase = if (Test-Path Env:WINTTY_STATE_BASE) { $env:WINTTY_STATE_BASE } else { $null }
$env:WINTTY_STATE_BASE = $stateBase

# Names that sort Alpha < Beta < Gamma < Omega and cannot collide with a
# real theme. Alpha to Gamma are dark, Omega is light, so a browse crosses
# the light/dark boundary the window chrome resolves its variant on.
$Themes = [ordered]@{
    'Wintty Probe Alpha' = @{ bg = '#102030'; fg = '#E0E0E0'; cursor = '#F5A524'
        p = @('#202020', '#E0555A', '#5BC67A', '#E8C55A', '#4F8FE0', '#B77EE0', '#46C1C9', '#C8CDD3') }
    'Wintty Probe Beta'  = @{ bg = '#304050'; fg = '#F0E0D0'; cursor = '#FF7A59'
        p = @('#404040', '#FF6B6B', '#7BD88F', '#FFD166', '#6AA9FF', '#C792EA', '#5CE1E6', '#E6E6E6') }
    'Wintty Probe Gamma' = @{ bg = '#506070'; fg = '#D0F0E0'; cursor = '#9BE564'
        p = @('#606060', '#F07178', '#A8E6A3', '#F9E27D', '#82AAFF', '#D4A5F5', '#89DDFF', '#F2F2F2') }
    'Wintty Probe Omega' = @{ bg = '#FAF4E8'; fg = '#2B2B2B'; cursor = '#D9480F'
        p = @('#3B3B3B', '#C0392B', '#2E8B57', '#B7791F', '#2B6CB0', '#8E44AD', '#1B8A8F', '#8A8175') }
}
$Alpha, $Beta, $Gamma, $Omega = @($Themes.Keys)
$Last = $Omega

$extra = @{}
foreach ($name in $Themes.Keys) {
    $t = $Themes[$name]
    $lines = @("background = $($t.bg)", "foreground = $($t.fg)", "cursor-color = $($t.cursor)")
    for ($i = 0; $i -lt $t.p.Count; $i++) { $lines += "palette = $i=$($t.p[$i])" }
    $extra["wintty/themes/$name"] = ($lines -join "`n") + "`n"
}

$config = @"
windows-single-instance = false
window-save-state = never
background-opacity = 1
"@

# The footer hints, spelled with code points so this file stays ASCII.
$arrows = "$([char]0x2191)$([char]0x2193)"
$enterKey = [char]0x21B5
$ThemeHint = "$arrows preview   $enterKey apply   Esc cancel"
$SearchHint = "$arrows navigate   $enterKey run   Esc close"

$script:Checks = [System.Collections.Generic.List[object]]::new()
$script:Shots = [System.Collections.Generic.List[string]]::new()
$script:Failed = $false
$script:HarnessFault = $false
$script:RowSize = $null

function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $script:Checks.Add([pscustomobject]@{ name = $Name; ok = $Ok; detail = $Detail })
    if ($Ok) { Write-Host "PASS $Name $Detail" -ForegroundColor Green }
    else { Write-Host "FAIL $Name $Detail" -ForegroundColor Red; $script:Failed = $true }
}

function Seam($s, [hashtable]$Command) {
    Send-SeamCommand $s $Command
    return Receive-SeamResponse $s $Command['op']
}

# Every readout the restore has to reproduce exactly.
function Snapshot($r) {
    [ordered]@{
        configTheme = $r.configTheme
        previewing = $r.previewing
        nativeBackground = $r.nativeBackground
        nativeForeground = $r.nativeForeground
        background = $r.background
        foreground = $r.foreground
        cursor = $r.cursor
        cursorText = $r.cursorText
        palette = ($r.palette -join ',')
    }
}

function Same($a, $b) {
    $diff = foreach ($k in $a.Keys) { if ("$($a[$k])" -ne "$($b[$k])") { "$k '$($a[$k])' vs '$($b[$k])'" } }
    return @($diff)
}

function Capture([int]$X, [int]$Y, [int]$W, [int]$H) {
    $bmp = New-Object System.Drawing.Bitmap ([Math]::Max(1, $W)), ([Math]::Max(1, $H))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen($X, $Y, 0, 0, $bmp.Size) } finally { $g.Dispose() }
    return $bmp
}

function Hex($c) { '#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B }

function Dominant($bmp) {
    $counts = @{}
    for ($i = 0; $i -lt $bmp.Width; $i++) {
        for ($j = 0; $j -lt $bmp.Height; $j++) {
            $key = Hex ($bmp.GetPixel($i, $j))
            $counts[$key] = 1 + ($counts[$key] ?? 0)
        }
    }
    return ($counts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1).Key
}

# The dominant colour of a patch near the bottom-right of the active
# terminal: shell output starts top-left, so this is background.
function Sample-TerminalBackground($s) {
    [SeamWin]::PlaceOnTop($s.Hwnd64)
    $r = Seam $s @{ op = 'get-theme' }
    $rect = $r.terminalRect
    if ($null -eq $rect) { throw 'HARNESS: the seam reported no terminal rect' }
    $bmp = Capture ([int]($rect.x + $rect.w - 40)) ([int]($rect.y + $rect.h - 40)) 24 24
    try { return Dominant $bmp } finally { $bmp.Dispose() }
}

function Same-Bytes([string]$Path, [byte[]]$Expected) {
    $actual = [System.IO.File]::ReadAllBytes($Path)
    return [Convert]::ToBase64String($actual) -eq [Convert]::ToBase64String($Expected)
}

function Near([string]$a, [string]$b, [int]$Tolerance = 8) {
    if (-not $a -or -not $b) { return $false }
    $pa = [Convert]::FromHexString($a.TrimStart('#')); $pb = [Convert]::FromHexString($b.TrimStart('#'))
    for ($i = 0; $i -lt 3; $i++) { if ([Math]::Abs([int]$pa[$i] - [int]$pb[$i]) -gt $Tolerance) { return $false } }
    return $true
}

# The same BT.709 test the chrome and the row hint use.
function Is-DarkHex([string]$hex) {
    $b = [Convert]::FromHexString($hex.TrimStart('#'))
    return ((0.2126 * $b[0] + 0.7152 * $b[1] + 0.0722 * $b[2]) / 255.0) -lt 0.5
}

# The renderer repaints on its own frame after the config lands, so the
# pixel oracle polls briefly rather than sampling once.
function Check-Pixels($s, [string]$Name, [string]$Want) {
    if ($NoPixels) { return }
    $seen = ''
    $deadline = (Get-Date).AddSeconds(3)
    do {
        Start-Sleep -Milliseconds 200
        $seen = Sample-TerminalBackground $s
        if (Near $seen $Want) { break }
    } while ((Get-Date) -lt $deadline)
    Check "$Name/pixels" (Near $seen $Want) "screen $seen, want $Want"
}

function Open-ThemeList($s) {
    [void](Seam $s @{ op = 'palette-open' })
    $typed = Seam $s @{ op = 'palette-type'; text = 'Change Theme' }
    if ($typed.paletteUi.selected -ne 'Change Theme') {
        throw "PRODUCT_FAIL: typing 'Change Theme' highlighted '$($typed.paletteUi.selected)'"
    }
    return Seam $s @{ op = 'palette-key'; key = 'enter' }
}

# The list realizes its rows and fills swatches in a later phase; wait until
# every realized row is painted before judging how it looks.
function Wait-Rows($s, [int]$Want) {
    $r = $null
    $deadline = (Get-Date).AddSeconds(4)
    do {
        $r = Seam $s @{ op = 'get-theme' }
        $rows = @($r.paletteUi.rows)
        if ($rows.Count -ge $Want -and @($rows | Where-Object { -not $_.painted }).Count -eq 0) { return $r }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    return $r
}

function PixelAt($bmp, $origin, [double]$X, [double]$Y) {
    $px = [int][Math]::Floor($X) - $origin.x
    $py = [int][Math]::Floor($Y) - $origin.y
    if ($px -lt 0 -or $py -lt 0 -or $px -ge $bmp.Width -or $py -ge $bmp.Height) { return '' }
    return Hex ($bmp.GetPixel($px, $py))
}

# Every staged theme's row: named for the theme (and "current theme" on the
# configured one), hinted light or dark, its swatch read from its file and
# drawn in exactly those colours, and every row the same size as every other
# row seen in this run (so nothing moved as swatches filled in).
function Check-Rows($s, $r, [string]$Name, [string]$Current = '') {
    Check "$Name/look-readable" (-not $r.paletteUi.lookError) "$($r.paletteUi.lookError)"
    $rows = @($r.paletteUi.rows)
    $staged = @($rows | Where-Object { $Themes.Contains($_.theme) })
    Check "$Name/rows-realized" ($staged.Count -eq $Themes.Count) "rows [$(@($rows | ForEach-Object theme) -join ', ')]"
    Check "$Name/rows-painted" (@($rows | Where-Object { -not $_.painted }).Count -eq 0)
    $sizes = @($rows | ForEach-Object { "row $($_.row.w)x$($_.row.h) swatch $($_.swatch.w)x$($_.swatch.h)" } | Select-Object -Unique)
    if ($null -eq $script:RowSize -and $sizes.Count -gt 0) { $script:RowSize = $sizes[0] }
    Check "$Name/rows-one-size" ($sizes.Count -eq 1 -and $sizes[0] -eq $script:RowSize) "[$($sizes -join '; ')], first seen $($script:RowSize)"

    foreach ($row in $staged) {
        $t = $Themes[$row.theme]
        $isCurrent = $row.theme -eq $Current
        $wantName = if ($isCurrent) { "$($row.theme), current theme" } else { $row.theme }
        $dark = Is-DarkHex $t.bg
        Check "$Name/$($row.theme)/name" ($row.automationName -eq $wantName) "'$($row.automationName)'"
        Check "$Name/$($row.theme)/hint" ($row.hint -eq ($dark ? 'Dark' : 'Light') -and $row.helpText -eq ($dark ? 'Dark theme' : 'Light theme')) "hint '$($row.hint)', help '$($row.helpText)'"
        $c = $row.colors
        $paletteOff = if ($c) { @(0..7 | Where-Object { $c.palette[$_] -ne $t.p[$_] }) } else { @(0) }
        $model = $null -ne $c -and $c.background -eq $t.bg -and $c.foreground -eq $t.fg -and $c.cursor -eq $t.cursor -and $paletteOff.Count -eq 0
        Check "$Name/$($row.theme)/swatch-reads-the-file" $model "bg $($c.background) fg $($c.foreground) cursor $($c.cursor) palette-off [$($paletteOff -join ',')]"
    }

    if ($NoPixels) { return }
    [SeamWin]::PlaceOnTop($s.Hwnd64)
    Start-Sleep -Milliseconds 150
    $card = $r.paletteUi.card
    $bmp = Capture $card.x $card.y $card.w $card.h
    try {
        foreach ($row in $staged) {
            $t = $Themes[$row.theme]
            $sw = $row.swatch
            $bgSeen = PixelAt $bmp $card ($sw.x + $sw.w * 0.8) ($sw.y + $sw.h * 0.3)
            $cu = $row.cursor
            $cursorSeen = PixelAt $bmp $card ($cu.x + $cu.w / 2) ($cu.y + $cu.h / 2)
            $stripOff = @()
            for ($i = 0; $i -lt 8; $i++) {
                $cell = $row.strip[$i]
                $seen = PixelAt $bmp $card ($cell.x + $cell.w / 2) ($cell.y + $cell.h / 2)
                if (-not (Near $seen $t.p[$i] 10)) { $stripOff += "${i}:$seen" }
            }
            # Some pixel of "Aa" is the theme's foreground.
            $sa = $row.sample
            $fgHit = $false
            for ($x = $sa.x; $x -lt $sa.x + $sa.w -and -not $fgHit; $x++) {
                for ($y = $sa.y; $y -lt $sa.y + $sa.h; $y++) {
                    if (Near (PixelAt $bmp $card $x $y) $t.fg 40) { $fgHit = $true; break }
                }
            }
            $ok = (Near $bgSeen $t.bg) -and (Near $cursorSeen $t.cursor) -and $stripOff.Count -eq 0 -and $fgHit
            Check "$Name/$($row.theme)/swatch-pixels" $ok "bg $bgSeen (want $($t.bg)), cursor $cursorSeen (want $($t.cursor)), strip-off [$($stripOff -join ' ')], fg-in-sample $fgHit"
        }
    } finally { $bmp.Dispose() }
}

# Exactly one row carries each badge the moment calls for, and no other row
# carries any.
function Check-Badges($r, [string]$Name, [string]$Current = '', [string]$Previewing = '') {
    $bad = foreach ($row in @($r.paletteUi.rows | Where-Object { $Themes.Contains($_.theme) })) {
        $want = if ($row.theme -eq $Current) { 'Current' } elseif ($row.theme -eq $Previewing) { 'Previewing' } else { '' }
        if ($row.badge -ne $want) { "$($row.theme) '$($row.badge)' want '$want'" }
    }
    Check "$Name/badges" (@($bad).Count -eq 0) (@($bad) -join '; ')
}

function Check-ThemeChrome($r, [string]$Name) {
    $u = $r.paletteUi
    Check "$Name/footer-hint" ($u.hint -eq $ThemeHint) "'$($u.hint)'"
    Check "$Name/search-box-filters-themes" ($u.searchName -eq 'Filter themes' -and $u.placeholder -eq 'Filter themes...' -and $u.listName -eq 'Themes') "name '$($u.searchName)', placeholder '$($u.placeholder)', list '$($u.listName)'"
}

function Save-Shot($s, [string]$File) {
    if ($NoPixels) { return }
    [SeamWin]::PlaceOnTop($s.Hwnd64)
    Start-Sleep -Milliseconds 350
    $b = [ShotWin]::Bounds($s.Hwnd64)
    if ($null -eq $b) {
        $rc = [SeamWin]::RectOf($s.Hwnd64)
        if ($null -eq $rc) { Write-Host "HARNESS: no window rect for $File"; return }
        $b = @($rc.L, $rc.T, $rc.W, $rc.Hh)
    }
    $bmp = Capture $b[0] $b[1] $b[2] $b[3]
    try {
        $path = Join-Path $ShotsDir $File
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $script:Shots.Add($path)
        Write-Host "SHOT $path"
    } finally { $bmp.Dispose() }
}

# Fast samples for the arrow run: a small patch, so a sample costs a few
# milliseconds and many land while the keys are still arriving.
function Quick-Terminal($rect) {
    $bmp = Capture ([int]($rect.x + $rect.w - 40)) ([int]($rect.y + $rect.h - 40)) 6 6
    try { return Dominant $bmp } finally { $bmp.Dispose() }
}

# Mean luminance of a patch of the palette's own surface, in the empty middle
# of its header: light acrylic reads far above 128, dark far below it.
function Quick-Luma($card, [double]$Scale) {
    $bmp = Capture ([int]($card.x + $card.w / 2) - 3) ([int]($card.y + 20 * $Scale) - 3) 6 6
    try {
        $sum = 0.0
        for ($i = 0; $i -lt 6; $i++) { for ($j = 0; $j -lt 6; $j++) {
            $c = $bmp.GetPixel($i, $j); $sum += 0.2126 * $c.R + 0.7152 * $c.G + 0.0722 * $c.B } }
        return $sum / 36
    } finally { $bmp.Dispose() }
}

# Send one palette-key and sample the screen until its answer arrives, then a
# few frames more: what a human watching the arrow run would have seen.
function Run-Sampled($s, [hashtable]$Command, $TermRect, $Card, [double]$Scale) {
    [SeamWin]::PlaceOnTop($s.Hwnd64)
    Send-SeamCommand $s $Command
    $task = $s.Reader.ReadLineAsync()
    $samples = [System.Collections.Generic.List[object]]::new()
    $deadline = (Get-Date).AddSeconds(10)
    while (-not $task.IsCompleted -and (Get-Date) -lt $deadline) {
        $samples.Add([pscustomobject]@{ terminal = (Quick-Terminal $TermRect); luma = (Quick-Luma $Card $Scale) })
    }
    if (-not $task.Wait(10000)) { throw 'HARNESS: no answer to a sampled palette-key' }
    $response = $task.Result | ConvertFrom-Json
    if (-not $response.ok) { throw "PRODUCT_FAIL: palette-key -> $($response.error)" }
    for ($i = 0; $i -lt 4; $i++) {
        Start-Sleep -Milliseconds 25
        $samples.Add([pscustomobject]@{ terminal = (Quick-Terminal $TermRect); luma = (Quick-Luma $Card $Scale) })
    }
    return @{ Response = $response; Samples = $samples }
}

# Poll until the palette's variant is the one wanted: the window resolves its
# variant on a later dispatcher turn than the config change that moved it.
function Wait-Variant($s, [string]$Want) {
    $r = $null
    $deadline = (Get-Date).AddSeconds(3)
    do {
        $r = Seam $s @{ op = 'get-theme' }
        if ($r.paletteUi.elementTheme -eq $Want) { break }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    return $r
}

$crashBefore = @(Get-ChildItem $stateBase -Recurse -Filter crash.log -ErrorAction SilentlyContinue)
$s = $null
try {
    # ---- run 1: browse, revert, confirm --------------------------------
    $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -ExtraFiles $extra
    $cfgPath = Join-Path $s.TempXdg 'wintty\config.wintty'
    $bytes0 = [System.IO.File]::ReadAllBytes($cfgPath)

    $base = Seam $s @{ op = 'get-theme' }
    $baseSnap = Snapshot $base
    Check 'baseline/not-previewing' (-not $base.previewing)
    Check 'baseline/no-theme-configured' ($base.configTheme -eq '') "configTheme '$($base.configTheme)'"
    $basePixel = if ($NoPixels) { '' } else { Sample-TerminalBackground $s }
    Check 'baseline/pixels-are-the-terminal' ($NoPixels -or (Near $basePixel $base.nativeBackground)) "screen $basePixel, native $($base.nativeBackground)"

    $list = Open-ThemeList $s
    Check 'enter/theme-mode' ($list.paletteUi.mode -eq 'Theme') "mode $($list.paletteUi.mode)"
    Check 'enter/lists-the-staged-themes' ($list.paletteUi.count -ge $Themes.Count) "count $($list.paletteUi.count)"
    Check 'enter/highlight-starts-at-top' ($list.paletteUi.selectedTheme -eq $Alpha) "selected '$($list.paletteUi.selectedTheme)'"
    Check 'enter/opening-the-list-previews-nothing' (-not $list.previewing)
    $openVariant = $list.paletteUi.elementTheme
    $opened = Wait-Rows $s $Themes.Count
    Check-ThemeChrome $opened 'enter'
    Check-Rows $s $opened 'enter'
    Check-Badges $opened 'enter/no-row-claims-a-preview'

    foreach ($step in @(
            @{ key = 'down'; want = $Beta },
            @{ key = 'down'; want = $Gamma },
            @{ key = 'up'; want = $Beta })) {
        $r = Seam $s @{ op = 'palette-key'; key = $step.key }
        $t = $Themes[$step.want]
        Check "browse/$($step.key)->$($step.want)/highlight" ($r.paletteUi.selectedTheme -eq $step.want) "'$($r.paletteUi.selectedTheme)'"
        Check "browse/$($step.want)/previewing" ($r.previewing -and $r.previewTheme -eq $step.want) "previewTheme '$($r.previewTheme)'"
        Check "browse/$($step.want)/terminal" ($r.nativeBackground -eq $t.bg -and $r.nativeForeground -eq $t.fg) "native $($r.nativeBackground)/$($r.nativeForeground)"
        Check "browse/$($step.want)/chrome" ($r.background -eq $t.bg -and $r.palette[0] -eq $t.p[0]) "chrome $($r.background) palette0 $($r.palette[0])"
        Check "browse/$($step.want)/file-untouched" (Same-Bytes $cfgPath $bytes0)
        Check-Badges $r "browse/$($step.want)" -Previewing $step.want
        Check "browse/$($step.want)/palette-holds-its-variant" ($r.paletteUi.elementTheme -eq $openVariant) "'$($r.paletteUi.elementTheme)', opened '$openVariant'"
        Check-Pixels $s "browse/$($step.want)" $t.bg
    }
    Save-Shot $s 'auto-app_previewing-dark.png'

    # A held key: thirty presses back to back settle on the last row.
    $held = Seam $s @{ op = 'palette-key'; key = 'down'; repeat = 30 }
    Check 'held-key/settles-on-the-last-row' ($held.paletteUi.selectedTheme -eq $Last -and $held.previewTheme -eq $Last -and $held.nativeBackground -eq $Themes[$Last].bg) "selected '$($held.paletteUi.selectedTheme)' previewed '$($held.previewTheme)'"
    Check 'held-key/palette-holds-its-variant' ($held.paletteUi.elementTheme -eq $openVariant) "'$($held.paletteUi.elementTheme)', opened '$openVariant'"
    $lightRows = Wait-Rows $s $Themes.Count
    Check-Rows $s $lightRows 'light-preview'
    Save-Shot $s 'auto-app_previewing-light.png'

    # Page Up / Page Down: a screenful (here the whole four-row list).
    $pageUp = Seam $s @{ op = 'palette-key'; key = 'pageup' }
    Check 'page-up/reaches-the-top' ($pageUp.paletteUi.selectedTheme -eq $Alpha -and $pageUp.previewTheme -eq $Alpha) "'$($pageUp.paletteUi.selectedTheme)'"
    $pageDown = Seam $s @{ op = 'palette-key'; key = 'pagedown' }
    Check 'page-down/reaches-the-bottom' ($pageDown.paletteUi.selectedTheme -eq $Last -and $pageDown.previewTheme -eq $Last) "'$($pageDown.paletteUi.selectedTheme)'"

    # A fast arrow run across dark and light themes, watched on screen.
    if (-not $NoPixels) {
        $look = Wait-Rows $s $Themes.Count
        $scale = if (@($look.paletteUi.rows).Count -gt 0) { $look.paletteUi.rows[0].row.h / 44.0 } else { 1.0 }
        $all = [System.Collections.Generic.List[object]]::new()
        $final = $null
        foreach ($run in @('up', 'down', 'up', 'down')) {
            $out = Run-Sampled $s @{ op = 'palette-key'; key = $run; repeat = 3; intervalMs = 40 } $look.terminalRect $look.paletteUi.card $scale
            $all.AddRange($out.Samples)
            $final = $out.Response
        }
        $known = @($basePixel) + @($Themes.Values | ForEach-Object { $_.bg })
        $foreign = @($all | Where-Object { $sample = $_.terminal; -not ($known | Where-Object { Near $sample $_ }) })
        $sawDark = @($all | Where-Object { Near $_.terminal $Themes[$Alpha].bg }).Count
        $sawLight = @($all | Where-Object { Near $_.terminal $Themes[$Omega].bg }).Count
        Check 'fast-run/samples-taken' ($all.Count -ge 16) "$($all.Count) samples"
        Check 'fast-run/terminal-only-ever-shows-a-theme' ($foreign.Count -eq 0) "foreign [$(@($foreign | ForEach-Object terminal | Select-Object -Unique) -join ', ')]"
        Check 'fast-run/terminal-repainted-mid-run' ($sawDark -gt 0 -and $sawLight -gt 0) "Alpha seen $sawDark, Omega seen $sawLight"
        $lumas = @($all | ForEach-Object luma)
        $wantLight = $openVariant -eq 'Light'
        $flips = @($lumas | Where-Object { ($_ -ge 128) -ne $wantLight })
        $range = if ($lumas.Count) { '{0:N0}..{1:N0}' -f ($lumas | Measure-Object -Minimum).Minimum, ($lumas | Measure-Object -Maximum).Maximum } else { 'none' }
        Check 'fast-run/palette-surface-never-flips' ($flips.Count -eq 0) "opened $openVariant, header luminance $range, $($flips.Count) off"
        Check 'fast-run/settles-on-the-last-row' ($final.paletteUi.selectedTheme -eq $Last -and $final.previewTheme -eq $Last -and $final.nativeBackground -eq $Themes[$Last].bg) "selected '$($final.paletteUi.selectedTheme)' previewed '$($final.previewTheme)'"
        Check 'fast-run/palette-holds-its-variant' ($final.paletteUi.elementTheme -eq $openVariant) "'$($final.paletteUi.elementTheme)'"
    }

    $esc = Seam $s @{ op = 'palette-key'; key = 'escape' }
    Check 'escape/closes' (-not $esc.paletteUi.open)
    $diff = Same $baseSnap (Snapshot $esc)
    Check 'escape/restores-every-readout-exactly' ($diff.Count -eq 0) ($diff -join '; ')
    Check 'escape/file-byte-for-byte' (Same-Bytes $cfgPath $bytes0)
    if (-not $NoPixels) { Check-Pixels $s 'escape' $basePixel }
    $after = Wait-Variant $s $openVariant
    Check 'escape/palette-variant-is-the-baseline' ($after.paletteUi.elementTheme -eq $openVariant) "'$($after.paletteUi.elementTheme)'"
    Check 'escape/palette-chrome-is-back-to-commands' ($after.paletteUi.hint -eq $SearchHint -and $after.paletteUi.searchName -eq 'Search commands' -and $after.paletteUi.listName -eq 'Command results') "hint '$($after.paletteUi.hint)', name '$($after.paletteUi.searchName)'"

    # Reopen: nothing left over from the browse that was cancelled.
    $again = Open-ThemeList $s
    Check 'reopen/clean' (-not $again.previewing -and $again.paletteUi.selectedTheme -eq $Alpha) "previewing $($again.previewing), selected '$($again.paletteUi.selectedTheme)'"
    # Closing unloaded the palette; a reopened one follows the window again.
    Check 'reopen/palette-tracks-the-window-theme' ($again.paletteUi.tracksWindowTheme -eq $true) "tracksWindowTheme $($again.paletteUi.tracksWindowTheme)"
    [void](Seam $s @{ op = 'palette-key'; key = 'down' })
    $kept = Seam $s @{ op = 'palette-key'; key = 'enter' }
    Check 'confirm/closes' (-not $kept.paletteUi.open)
    Check 'confirm/no-preview-left' (-not $kept.previewing)
    Check 'confirm/config-names-the-theme' ($kept.configTheme -eq $Beta) "configTheme '$($kept.configTheme)'"
    Check 'confirm/terminal-stays' ($kept.nativeBackground -eq $Themes[$Beta].bg) "native $($kept.nativeBackground)"
    Check 'confirm/chrome-stays' ($kept.background -eq $Themes[$Beta].bg) "chrome $($kept.background)"
    Check-Pixels $s 'confirm' $Themes[$Beta].bg
    # The palette caught up with the dark theme it was held off during the
    # browse (vacuous when it opened dark already; the detail says which).
    $caught = Wait-Variant $s 'Dark'
    Check 'confirm/palette-catches-up-after-the-browse' ($caught.paletteUi.elementTheme -eq 'Dark') "'$($caught.paletteUi.elementTheme)', opened '$openVariant'"
    # And what the user sees next: the palette opened again over the dark
    # window it now floats over is drawn dark, and still tracks the window.
    [void](Seam $s @{ op = 'palette-open' })
    $reopened = Wait-Variant $s 'Dark'
    Check 'confirm/reopened-palette-matches-the-window' ($reopened.paletteUi.open -and $reopened.paletteUi.elementTheme -eq 'Dark' -and $reopened.paletteUi.tracksWindowTheme -eq $true) "open $($reopened.paletteUi.open), '$($reopened.paletteUi.elementTheme)', tracksWindowTheme $($reopened.paletteUi.tracksWindowTheme)"
    $closedAgain = Seam $s @{ op = 'palette-key'; key = 'escape' }
    Check 'confirm/reopened-palette-closes' (-not $closedAgain.paletteUi.open -and $closedAgain.configTheme -eq $Beta) "open $($closedAgain.paletteUi.open), configTheme '$($closedAgain.configTheme)'"

    # Compared as lines: the shared config editor writes the whole file back
    # with LF endings on every write (the Settings pages' behaviour too),
    # while the harness staged it with CRLF, so bytes differ on every line.
    $before = @([System.Text.Encoding]::UTF8.GetString($bytes0) -split "\r?\n" | Where-Object { $_ -ne '' })
    $persisted = [System.IO.File]::ReadAllText($cfgPath)
    $afterLines = @($persisted -split "\r?\n" | Where-Object { $_ -ne '' })
    $added = @($afterLines | Where-Object { $before -notcontains $_ })
    $lost = @($before | Where-Object { $afterLines -notcontains $_ })
    Check 'confirm/exactly-one-line-written' ($added.Count -eq 1 -and $added[0] -eq "theme = $Beta" -and $lost.Count -eq 0) "added [$($added -join ' | ')] lost [$($lost -join ' | ')]"

    Stop-SeamSession $s
    $s = $null

    # ---- run 2: a new process on that exact file ------------------------
    $relaunch = @{} + $extra
    $relaunch['wintty/config.wintty'] = $persisted
    $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -ExtraFiles $relaunch
    $boot = Seam $s @{ op = 'get-theme' }
    Check 'restart/config-still-names-the-theme' ($boot.configTheme -eq $Beta) "configTheme '$($boot.configTheme)'"
    Check 'restart/terminal-starts-themed' ($boot.nativeBackground -eq $Themes[$Beta].bg) "native $($boot.nativeBackground)"
    Check 'restart/chrome-starts-themed' ($boot.background -eq $Themes[$Beta].bg) "chrome $($boot.background)"
    Check-Pixels $s 'restart' $Themes[$Beta].bg

    # With a theme configured, the list opens on it, marked Current, and
    # Escape returns to it.
    $bootSnap = Snapshot $boot
    $list2 = Open-ThemeList $s
    Check 'restart/highlight-opens-on-the-configured-theme' ($list2.paletteUi.selectedTheme -eq $Beta) "'$($list2.paletteUi.selectedTheme)'"
    $opened2 = Wait-Rows $s $Themes.Count
    Check-Rows $s $opened2 'restart' -Current $Beta
    Check-Badges $opened2 'restart/opens' -Current $Beta
    $moved = Seam $s @{ op = 'palette-key'; key = 'down' }
    Check 'restart/browse-previews' ($moved.nativeBackground -eq $Themes[$Gamma].bg) "native $($moved.nativeBackground)"
    Check-Badges $moved 'restart/browsing' -Current $Beta -Previewing $Gamma
    Save-Shot $s 'current-and-previewing.png'
    $esc2 = Seam $s @{ op = 'palette-key'; key = 'escape' }
    $diff2 = Same $bootSnap (Snapshot $esc2)
    Check 'restart/escape-restores-the-configured-theme-exactly' ($diff2.Count -eq 0) ($diff2 -join '; ')
    Check-Pixels $s 'restart-escape' $Themes[$Beta].bg
    if ($s.Proc.HasExited) { Check 'app-alive' $false "exit code $($s.Proc.ExitCode)" }
    Stop-SeamSession $s
    $s = $null

    # ---- runs 3 and 4: explicit light and dark app themes ---------------
    foreach ($appTheme in @('dark', 'light')) {
        $want = if ($appTheme -eq 'dark') { 'Dark' } else { 'Light' }
        $s = Start-SeamSession -ExePath $ExePath -ConfigText ($config + "`nwindow-theme = $appTheme`n") -ExtraFiles $extra
        [void](Open-ThemeList $s)
        $look = Wait-Rows $s $Themes.Count
        Check "$appTheme-app/palette-variant" ($look.paletteUi.elementTheme -eq $want) "'$($look.paletteUi.elementTheme)'"
        Check-ThemeChrome $look "$appTheme-app"
        Check-Rows $s $look "$appTheme-app"
        $d = Seam $s @{ op = 'palette-key'; key = 'down' }
        Check "$appTheme-app/previews-a-dark-theme" ($d.previewTheme -eq $Beta -and $d.nativeBackground -eq $Themes[$Beta].bg) "previewed '$($d.previewTheme)'"
        Check-Pixels $s "$appTheme-app/dark-preview" $Themes[$Beta].bg
        Save-Shot $s "$appTheme-app_previewing-dark.png"
        $l = Seam $s @{ op = 'palette-key'; key = 'pagedown' }
        Check "$appTheme-app/previews-a-light-theme" ($l.previewTheme -eq $Omega -and $l.nativeBackground -eq $Themes[$Omega].bg -and $l.paletteUi.elementTheme -eq $want) "previewed '$($l.previewTheme)', palette '$($l.paletteUi.elementTheme)'"
        Check-Pixels $s "$appTheme-app/light-preview" $Themes[$Omega].bg
        Check-Badges $l "$appTheme-app/light-preview" -Previewing $Omega
        Save-Shot $s "$appTheme-app_previewing-light.png"
        $e = Seam $s @{ op = 'palette-key'; key = 'escape' }
        Check "$appTheme-app/escape-restores" (-not $e.previewing -and $e.configTheme -eq '' -and $e.nativeBackground -eq $base.nativeBackground) "native $($e.nativeBackground)"
        if ($s.Proc.HasExited) { Check "$appTheme-app/alive" $false "exit code $($s.Proc.ExitCode)" }
        Stop-SeamSession $s
        $s = $null
    }
} catch {
    $msg = "$($_.Exception.Message)"
    if ($msg -like 'PRODUCT_*') { Check 'scenario' $false $msg }
    else { Write-Host "HARNESS: $msg" -ForegroundColor Red; $script:HarnessFault = $true }
    if ($null -ne $s -and -not $s.Proc.HasExited) {
        try {
            $rc = [SeamWin]::RectOf($s.Hwnd64)
            if ($null -ne $rc) {
                $bmp = Capture $rc.L $rc.T $rc.W $rc.Hh
                $bmp.Save((Join-Path $OutDir 'fail.png'))
                $bmp.Dispose()
            }
        } catch { }
    }
} finally {
    if ($null -ne $s) { Stop-SeamSession $s }
    $crashes = @(Get-ChildItem $stateBase -Recurse -Filter crash.log -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -gt 0 })
    if ($crashes.Count -gt $crashBefore.Count) { Check 'no-crash-log' $false ($crashes.FullName -join ', ') }
    if ($null -ne $origStateBase) { $env:WINTTY_STATE_BASE = $origStateBase }
    else { Remove-Item Env:WINTTY_STATE_BASE -ErrorAction SilentlyContinue }
    Remove-Item $stateBase -Recurse -Force -ErrorAction SilentlyContinue
}

[ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=<session token>); zero synthesized OS input'
    checks = $script:Checks
    shots = $script:Shots
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

$passed = @($script:Checks | Where-Object { $_.ok }).Count
Write-Host ''
Write-Host ("{0} of {1} checks passed" -f $passed, $script:Checks.Count)
if ($script:Failed) { exit 2 }
if ($script:HarnessFault) { exit 1 }
exit 0
