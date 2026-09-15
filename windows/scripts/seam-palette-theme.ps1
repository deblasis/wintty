#requires -Version 7
<#
    The command palette's theme mode, seam-actuated end to end (issue #1081).

    One app on a temp config root and a temp state base, the themes the build
    ships (share\ghostty\themes beside the exe) plus two user themes staged in
    the temp config root: a user copy of one bundled theme, in colours of its
    own, and one theme only the user has. No theme is configured (the case
    where the user never set one). The browse itself runs over a small group
    of real bundled themes that one filter word narrows the list to, picked
    from the shipped set at run time (the second dark, the last light, the
    ones the pixel oracle compares clearly apart), with the user's copy
    third. Each step
    reads the theme back three ways:

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

      0. open the palette, run "Change Theme": the list holds every bundled
         and user theme on disk, once each (the user's copy wins), in catalog
         order, virtualized; type the filter word a key at a time: nothing
         narrows or previews while the keys land, then the list narrows once
         to the expected names, the highlight is on the best match and it is
         previewed exactly once, and the screen never shows anything but the
         baseline or that theme;
      1. arrow down twice and back up:
         every move re-themes the live terminal, the config file is untouched;
      2. a held key (30 presses back to back) settles on the last row, and
         Page Up / Page Down move a screenful;
      3. a fast arrow run across dark and light themes, sampled on screen
         while the keys land: the terminal only ever shows one of the themes
         (never a default or an in-between frame) and the palette never flips
         between its light and dark surface;
      4. a filter that matches nothing: an empty list that says so, the
         preview left where it was, Enter keeps nothing; Backspace widens
         back to the group and then to every theme;
      5. Escape: every readout equals the pre-palette baseline exactly, and the
         config file is byte for byte what it was;
      6. reopen, type a theme's name and press Enter before typing pauses: the
         file gains exactly one line, `theme = <name>`, and the live views
         stay on it;
      7. relaunch on that exact file: the theme is still there at startup, the
         highlight opens on it (marked Current), and a browse-then-Escape
         returns to it exactly;
      8. two short launches with window-theme = dark and = light, each
         previewing a dark and then a light theme;
      9. an external config change landing mid-browse: the reload puts the
         committed config on every view and derives the chrome from it, and
         Escape leaves those colours alone (the pre-browse chrome snapshot
         is not painted back over them); no theme-preview.conf is left in
         the state tree afterwards;
     10. a light/dark theme pair configured: the list opens on the half the
         OS scheme has live (marked Current), the other half previews, and
         Escape returns to the live half exactly with the pair still in
         the file.

    Screenshots of the palette theme mode (window captures of this run's own
    instance, PNG) land in -ShotsDir: the full list, the filtered list, the
    no-match state, both app themes with both a dark and a light previewed
    theme, the auto case, and the Current/Previewing pair.

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

# The themes this run browses are picked from the shipped set further down,
# once the helpers they need are defined (see "The theme set").

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

# Every session's window at the same known place, before anything samples
# the screen: the desktop's window cascade walks each new window a step
# along from the last, and once a session's window lands far enough from
# the primary origin the seam's DIP-to-screen rects and a screen capture
# disagree about where it is, so the pixel oracle reads a foreign window
# (seen live: a shared desktop with another terminal running beside the
# run). Pinning the window keeps every run's geometry the first run's.
function Place-SessionWindow($s, [int]$X = 40, [int]$Y = 40) {
    if ($NoPixels) { return }
    $r = [SeamWin]::RectOf($s.Hwnd64)
    if ($r) {
        [void][SeamWin]::MoveWindow([SeamWin]::P($s.Hwnd64), $X, $Y, $r.W, $r.Hh, $true)
    }
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

# The first readout of a freshly launched instance. The seam answers as
# soon as its pipe is up, which can be before the window has drawn its
# first frame; a readout that lands then can fail inside WinRT (seen once
# as a COMException on a still-black relaunched window). Retried until the
# window answers with its terminal on screen, so what the checks after it
# read is the started app, not its first frames. A failure that persists
# past the deadline is still reported as the product failure it is.
function Boot-Theme($s) {
    $deadline = (Get-Date).AddSeconds(15)
    while ($true) {
        try {
            $r = Seam $s @{ op = 'get-theme' }
            if ($null -ne $r.terminalRect -or (Get-Date) -ge $deadline) { return $r }
        } catch {
            if ("$($_.Exception.Message)" -notlike 'PRODUCT_*' -or (Get-Date) -ge $deadline) { throw }
            Write-Host "boot readout retried: $($_.Exception.Message)"
        }
        Start-Sleep -Milliseconds 250
    }
}

# ---- The theme set ----------------------------------------------------
#
# What a theme row's swatch is read from, parsed the way ThemeSwatch.Parse
# reads it: later lines win, what is unset falls back to libghostty's
# defaults, and an unset cursor colour is the foreground.
$DefaultPalette = @('#1D1F21', '#CC6666', '#B5BD68', '#F0C674', '#81A2BE', '#B294BB', '#8ABEB7', '#C5C8C6')
function Norm-Hex([string]$v) {
    $v = $v.Trim().Trim('"')
    if ($v -match '^#?([0-9a-fA-F]{6})$') { return '#' + $Matches[1].ToUpperInvariant() }
    return $null
}
function Read-Theme([string]$Path) {
    $t = @{ bg = '#282C34'; fg = '#FFFFFF'; cursor = $null; p = [string[]]$DefaultPalette.Clone() }
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        $l = $line.Trim()
        if (-not $l -or $l[0] -eq '#') { continue }
        $eq = $l.IndexOf('=')
        if ($eq -le 0) { continue }
        $k = $l.Substring(0, $eq).Trim()
        $v = $l.Substring($eq + 1).Trim()
        $h = $null
        switch -CaseSensitive ($k) {
            'background' { if ($h = Norm-Hex $v) { $t.bg = $h } }
            'foreground' { if ($h = Norm-Hex $v) { $t.fg = $h } }
            'cursor-color' { if ($h = Norm-Hex $v) { $t.cursor = $h } }
            'palette' {
                if ($v -match '^(\d+)\s*=\s*(.+)$') {
                    $i = [int]$Matches[1]
                    if ($i -lt 8 -and ($h = Norm-Hex $Matches[2])) { $t.p[$i] = $h }
                }
            }
        }
    }
    if (-not $t.cursor) { $t.cursor = $t.fg }
    return $t
}
function Theme-Text($t) {
    $lines = @("background = $($t.bg)", "foreground = $($t.fg)", "cursor-color = $($t.cursor)")
    for ($i = 0; $i -lt $t.p.Count; $i++) { $lines += "palette = $i=$($t.p[$i])" }
    return ($lines -join "`n") + "`n"
}

# ThemeCatalog.IsPersistableName: what the app lists.
function Is-Persistable([string]$n) {
    if (-not $n -or $n -ne $n.Trim() -or $n -eq '.DS_Store') { return $false }
    if ($n[0] -in '\', '/' -or [System.IO.Path]::GetFileName($n) -ne $n) { return $false }
    if ($n -match '[,="]' -or $n -match '[\x00-\x1F\x7F-\x9F]') { return $false }
    return $true
}

# ThemeCatalog's order: case-insensitive, ties broken ordinally.
function Sort-Catalog([string[]]$Names) {
    $list = [System.Collections.Generic.List[string]]::new([string[]]$Names)
    $list.Sort([System.Comparison[string]] {
            param($a, $b)
            $c = [StringComparer]::OrdinalIgnoreCase.Compare($a, $b)
            if ($c -ne 0) { $c } else { [StringComparer]::Ordinal.Compare($a, $b) }
        })
    return , $list.ToArray()
}

# ThemeCatalog.MatchRank and Filter: best match first, catalog order within.
function Match-Rank([string]$Name, [string]$Q) {
    if ($Name.Equals($Q, [StringComparison]::OrdinalIgnoreCase)) { return 0 }
    $best = [int]::MaxValue
    $at = $Name.IndexOf($Q, [StringComparison]::OrdinalIgnoreCase)
    while ($at -ge 0) {
        $r = if ($at -eq 0) { 1 } elseif (-not [char]::IsLetterOrDigit($Name[$at - 1])) { 2 } else { 3 }
        if ($r -lt $best) { $best = $r }
        if ($best -eq 1 -or $at + 1 -ge $Name.Length) { break }
        $at = $Name.IndexOf($Q, $at + 1, [StringComparison]::OrdinalIgnoreCase)
    }
    return $best
}
function Filter-Themes([string[]]$Catalog, [string]$Q) {
    $Q = $Q.Trim()
    if (-not $Q) { return , $Catalog }
    $hits = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $Catalog.Count; $i++) {
        $r = Match-Rank $Catalog[$i] $Q
        if ($r -ne [int]::MaxValue) { $hits.Add([pscustomobject]@{ n = $Catalog[$i]; i = $i; r = $r }) }
    }
    return , [string[]]@($hits | Sort-Object r, i | ForEach-Object n)
}

$BundledDir = Join-Path (Split-Path $exeFull) 'share\ghostty\themes'
if (-not (Test-Path -LiteralPath $BundledDir)) {
    Write-Host "FAIL source/bundled-themes-shipped: no $BundledDir beside the exe" -ForegroundColor Red
    [ordered]@{ checks = @([pscustomobject]@{ name = 'source/bundled-themes-shipped'; ok = $false; detail = "no $BundledDir" }) } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8
    exit 2
}
$bundled = @{}   # persistable name -> path; keys compare case-insensitively
# -Force: the app lists hidden files too (Directory.EnumerateFiles).
foreach ($f in Get-ChildItem -LiteralPath $BundledDir -File -Force) {
    if (Is-Persistable $f.Name) { $bundled[$f.Name] = $f.FullName }
}
$BundledCatalog = Sort-Catalog @($bundled.Keys)
$parsed = @{}
function Theme-Of([string]$Name) {
    if (-not $parsed.ContainsKey($Name)) { $parsed[$Name] = Read-Theme $bundled[$Name] }
    return $parsed[$Name]
}

# The user's two themes. The copy's colours are its own, so every readout of
# that name proves which file loaded; the other name exists only for the user.
$UserOnly = 'Wintty Harness Fixture'
$UserOnlyTheme = @{ bg = '#20302A'; fg = '#E8F5EE'; cursor = '#F0B429'
    p = @('#1B2420', '#E5534B', '#57AB5A', '#C69026', '#539BF5', '#B083F0', '#39C5CF', '#D1D7E0') }
$CopyCandidates = @(
    @{ bg = '#3A1F4D'; fg = '#F2E9FF'; cursor = '#FFB000'
        p = @('#241430', '#FF5C8A', '#7CE38B', '#FFD866', '#78A9FF', '#D291FF', '#5FE0D6', '#EDE4F7') },
    @{ bg = '#1F4D3A'; fg = '#E9FFF2'; cursor = '#FF7A00'
        p = @('#14301F', '#FF6B6B', '#9BE564', '#FFE066', '#6CB6FF', '#C792EA', '#63E6BE', '#E4F7EC') })

# A filter word that narrows the shipped set to a group this scenario can
# browse: four to seven names (all on screen at once), the second dark and
# the last light. The first, the second and the last are the ones the pixel
# oracle tells apart, so their backgrounds must be clear of each other and
# of the untouched baseline by more than twice the oracle's tolerance; the
# third is replaced by the user's copy, in colours clear of the whole group.
# Picked from the set on disk, so a refresh of the bundled themes changes
# the group, not the harness.
$Apart = 16
$avoid = @('#F4F6FB', $UserOnlyTheme.bg)
$tokens = @($BundledCatalog | ForEach-Object { $_ -split '[^A-Za-z0-9]+' } |
    Where-Object { $_.Length -ge 4 } | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object -Unique)
$Group = $null; $Token = $null; $Copy = $null
foreach ($tok in $tokens) {
    if ((Match-Rank $UserOnly $tok) -ne [int]::MaxValue) { continue }
    $count = 0
    foreach ($n in $BundledCatalog) { if ($n.IndexOf($tok, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $count++ } }
    if ($count -lt 4 -or $count -gt 7) { continue }
    $m = Filter-Themes $BundledCatalog $tok
    $bgs = @($m | ForEach-Object { (Theme-Of $_).bg })
    if (-not (Is-DarkHex $bgs[1]) -or (Is-DarkHex $bgs[-1])) { continue }
    $told = @($bgs[0], $bgs[1], $bgs[-1])
    $ok = $true
    for ($i = 0; $i -lt $told.Count -and $ok; $i++) {
        foreach ($a in $avoid) { if (Near $told[$i] $a $Apart) { $ok = $false } }
        for ($j = $i + 1; $j -lt $told.Count; $j++) { if (Near $told[$i] $told[$j] $Apart) { $ok = $false } }
    }
    if (-not $ok) { continue }
    # The user's copy replaces the third name's colours; they must stay clear
    # of every other name in the group.
    $others = @($bgs[0], $bgs[1]) + @($bgs | Select-Object -Skip 3)
    $Copy = $CopyCandidates | Where-Object { $c = $_; -not ($others | Where-Object { Near $_ $c.bg $Apart }) } | Select-Object -First 1
    if ($null -eq $Copy) { continue }
    $Group = $m; $Token = $tok
    break
}
if ($null -eq $Group) {
    Write-Host "HARNESS: no filter word narrows the $($BundledCatalog.Count) shipped themes to a usable group"
    exit 1
}

$Alpha = $Group[0]; $Beta = $Group[1]; $Gamma = $Group[2]; $Omega = $Group[-1]; $Last = $Omega
$Themes = [ordered]@{}
foreach ($n in $Group) { $Themes[$n] = if ($n -eq $Gamma) { $Copy } else { Theme-Of $n } }

$extra = @{
    "wintty/themes/$Gamma" = Theme-Text $Copy
    "wintty/themes/$UserOnly" = Theme-Text $UserOnlyTheme
}

# What the list should hold: every persistable name on disk, once, the
# user's spelling first (user directories are searched first).
$merged = @{}
foreach ($n in @($Gamma, $UserOnly) + $BundledCatalog) { if (-not $merged.ContainsKey($n)) { $merged[$n] = $true } }
$Catalog = Sort-Catalog @($merged.Keys)
$ExpectedTotal = $Catalog.Count
$UserCount = 2
$Shadowed = 1
Write-Host "theme set: $($BundledCatalog.Count) bundled + $UserCount user ($Shadowed shadowing a bundled one) = $ExpectedTotal; filter '$Token' -> [$($Group -join ', ')]"

# The palette's own scale, from a realized row (44 DIPs tall).
function Row-Scale($r) {
    $rows = @($r.paletteUi.rows)
    if ($rows.Count -gt 0 -and $rows[0].row) { return $rows[0].row.h / 44.0 }
    return 1.0
}

# The screen samples of a typing run: the terminal may show only the
# themes this browse can land on, and the palette's surface never flips.
function Check-Samples([string]$Name, $Samples, [string[]]$Allowed, [string]$OpenVariant) {
    if ($NoPixels) { return }
    $foreign = @($Samples | Where-Object { $sample = $_.terminal; -not ($Allowed | Where-Object { Near $sample $_ }) })
    Check "$Name/samples-taken" (@($Samples).Count -ge 8) "$(@($Samples).Count) samples"
    Check "$Name/terminal-only-ever-shows-a-candidate" ($foreign.Count -eq 0) "foreign [$(@($foreign | ForEach-Object terminal | Select-Object -Unique) -join ', ')], allowed [$($Allowed -join ', ')]"
    $wantLight = $OpenVariant -eq 'Light'
    $flips = @($Samples | Where-Object { ($_.luma -ge 128) -ne $wantLight })
    Check "$Name/palette-surface-never-flips" ($flips.Count -eq 0) "opened $OpenVariant, $($flips.Count) off"
}

$crashBefore = @(Get-ChildItem $stateBase -Recurse -Filter crash.log -ErrorAction SilentlyContinue)
$s = $null
try {
    # ---- run 1: browse, revert, confirm --------------------------------
    $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -ExtraFiles $extra
    Place-SessionWindow $s
    $cfgPath = Join-Path $s.TempXdg 'wintty\config.wintty'
    $bytes0 = [System.IO.File]::ReadAllBytes($cfgPath)

    $base = Boot-Theme $s
    $baseSnap = Snapshot $base
    Check 'baseline/not-previewing' (-not $base.previewing)
    Check 'baseline/no-theme-configured' ($base.configTheme -eq '') "configTheme '$($base.configTheme)'"
    $basePixel = if ($NoPixels) { '' } else { Sample-TerminalBackground $s }
    Check 'baseline/pixels-are-the-terminal' ($NoPixels -or (Near $basePixel $base.nativeBackground)) "screen $basePixel, native $($base.nativeBackground)"

    $list = Open-ThemeList $s
    Check 'enter/theme-mode' ($list.paletteUi.mode -eq 'Theme') "mode $($list.paletteUi.mode)"
    $names = @($list.paletteUi.names)
    Check 'source/lists-every-bundled-and-user-theme' ($list.paletteUi.count -eq $ExpectedTotal -and $list.paletteUi.total -eq $ExpectedTotal) "count $($list.paletteUi.count), total $($list.paletteUi.total), on disk $($BundledCatalog.Count) bundled + $UserCount user - $Shadowed shadowed = $ExpectedTotal"
    Check 'source/in-catalog-order' (($names -join "`n") -ceq ($Catalog -join "`n")) "first [$(@($names | Select-Object -First 3) -join ', ')], want [$(@($Catalog | Select-Object -First 3) -join ', ')]"
    Check 'source/user-copy-listed-once' (@($names | Where-Object { $_ -eq $Gamma }).Count -eq 1) "'$Gamma'"
    Check 'source/user-only-theme-listed' ($names -ccontains $UserOnly)
    Check 'enter/highlight-starts-at-top' ($list.paletteUi.selectedTheme -ceq $Catalog[0]) "selected '$($list.paletteUi.selectedTheme)'"
    Check 'enter/opening-the-list-previews-nothing' (-not $list.previewing)
    $openVariant = $list.paletteUi.elementTheme
    $full = Wait-Rows $s 5
    $fullRows = @($full.paletteUi.rows)
    # Virtualized: the visible rows plus the list's cache buffer, a few
    # screens' worth, never the whole list.
    Check 'full-list/virtualized' ($fullRows.Count -ge 5 -and $fullRows.Count -le 100 -and $fullRows.Count -lt $ExpectedTotal) "$($fullRows.Count) of $ExpectedTotal rows realized"
    Check 'full-list/rows-painted' ($fullRows.Count -gt 0 -and @($fullRows | Where-Object { -not $_.painted }).Count -eq 0)
    Check-ThemeChrome $full 'enter'
    Check-Badges $full 'enter/no-row-claims-a-preview'
    Save-Shot $s 'full-list.png'

    # Typing the filter word a key at a time, faster than the debounce: the
    # answer comes right after the last key, before typing has paused.
    $scale = Row-Scale $full
    $typing = if ($NoPixels) {
        @{ Response = (Seam $s @{ op = 'palette-type'; text = $Token; perCharMs = 40; settle = $false }); Samples = @() }
    } else {
        Run-Sampled $s @{ op = 'palette-type'; text = $Token; perCharMs = 40; settle = $false } $full.terminalRect $full.paletteUi.card $scale
    }
    $mid = $typing.Response
    # Read at the moment the last key landed (the seam's atLastKey): the
    # filter still waiting, the full list, no preview yet.
    $atKey = $mid.atLastKey
    Check 'filter/waits-for-typing-to-pause' ($null -ne $atKey -and $atKey.filterPending -and $atKey.count -eq $ExpectedTotal -and $atKey.previewApplies -eq 0) "at the last key: pending $($atKey.filterPending), count $($atKey.count), previews $($atKey.previewApplies); text '$($mid.paletteUi.searchText)'"
    # Then the pause: the same text again changes nothing, and the answer
    # waits for the filter and the preview it leads to.
    $settling = if ($NoPixels) {
        @{ Response = (Seam $s @{ op = 'palette-type'; text = $Token }); Samples = @() }
    } else {
        Run-Sampled $s @{ op = 'palette-type'; text = $Token } $full.terminalRect $full.paletteUi.card $scale
    }
    $filtered = $settling.Response
    Check 'filter/narrows-to-the-expected-names' ((@($filtered.paletteUi.names) -join "`n") -ceq ($Group -join "`n") -and $filtered.paletteUi.total -eq $ExpectedTotal) "names [$(@($filtered.paletteUi.names) -join ', ')], want [$($Group -join ', ')]"
    Check 'filter/highlight-on-the-best-match' ($filtered.paletteUi.selectedTheme -ceq $Alpha) "'$($filtered.paletteUi.selectedTheme)'"
    Check 'filter/preview-follows-the-highlight' ($filtered.previewing -and $filtered.previewTheme -ceq $Alpha -and $filtered.nativeBackground -eq $Themes[$Alpha].bg) "previewTheme '$($filtered.previewTheme)', native $($filtered.nativeBackground)"
    Check 'filter/one-preview-for-the-word' ($filtered.paletteUi.previewApplies -eq 1) "$($filtered.paletteUi.previewApplies) previews for $($Token.Length) keys"
    Check 'filter/file-untouched' (Same-Bytes $cfgPath $bytes0)
    Check-Samples 'filter/typing' (@($typing.Samples) + @($settling.Samples)) @($basePixel, $Themes[$Alpha].bg) $openVariant
    Check-Pixels $s 'filter' $Themes[$Alpha].bg

    $opened = Wait-Rows $s $Themes.Count
    Check-Rows $s $opened 'filtered'
    Check-Badges $opened 'filtered' -Previewing $Alpha
    Save-Shot $s 'filtered-list.png'

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
            # Each run spans the whole group, first row to last.
            $out = Run-Sampled $s @{ op = 'palette-key'; key = $run; repeat = ($Group.Count - 1); intervalMs = 40 } $look.terminalRect $look.paletteUi.card $scale
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

    # A filter that matches nothing, typed onto the word.
    $noMatchText = $Token + 'zqxj'
    $nm = Seam $s @{ op = 'palette-type'; text = $noMatchText; perCharMs = 30 }
    $wantNoMatch = "No themes match $([char]0x201C)$noMatchText$([char]0x201D)"
    Check 'no-match/empty-list' ($nm.paletteUi.count -eq 0 -and @($nm.paletteUi.names).Count -eq 0 -and $null -eq $nm.paletteUi.selectedTheme) "count $($nm.paletteUi.count)"
    Check 'no-match/says-so' ($nm.paletteUi.noMatch -ceq $wantNoMatch -and $nm.paletteUi.status -ceq 'No themes match') "noMatch '$($nm.paletteUi.noMatch)', status '$($nm.paletteUi.status)'"
    Check 'no-match/preview-stays' ($nm.previewTheme -ceq $Last -and $nm.nativeBackground -eq $Themes[$Last].bg) "previewTheme '$($nm.previewTheme)'"
    Check 'no-match/card-keeps-its-size' ($nm.paletteUi.card.h -eq $filtered.paletteUi.card.h) "card h $($nm.paletteUi.card.h), filtered $($filtered.paletteUi.card.h)"
    Save-Shot $s 'no-match.png'
    $nothing = Seam $s @{ op = 'palette-key'; key = 'enter' }
    Check 'no-match/enter-keeps-nothing' ($nothing.paletteUi.open -and $nothing.configTheme -eq '' -and (Same-Bytes $cfgPath $bytes0)) "open $($nothing.paletteUi.open), configTheme '$($nothing.configTheme)'"

    # Backspace widens: back to the group, then to every theme.
    $back = Seam $s @{ op = 'palette-key'; key = 'backspace'; repeat = 4; intervalMs = 30 }
    Check 'backspace/widens-back-to-the-group' ((@($back.paletteUi.names) -join "`n") -ceq ($Group -join "`n") -and $null -eq $back.paletteUi.noMatch -and $back.paletteUi.selectedTheme -ceq $Alpha -and $back.previewTheme -ceq $Alpha) "text '$($back.paletteUi.searchText)', names [$(@($back.paletteUi.names) -join ', ')], selected '$($back.paletteUi.selectedTheme)'"
    $wide = Seam $s @{ op = 'palette-key'; key = 'backspace'; repeat = $Token.Length; intervalMs = 30 }
    Check 'backspace/widens-to-every-theme' ($wide.paletteUi.count -eq $ExpectedTotal -and $wide.paletteUi.searchText -eq '' -and $wide.paletteUi.selectedTheme -ceq $Catalog[0]) "count $($wide.paletteUi.count), text '$($wide.paletteUi.searchText)'"

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
    Check 'reopen/clean' (-not $again.previewing -and $again.paletteUi.selectedTheme -ceq $Catalog[0] -and $again.paletteUi.searchText -eq '' -and $again.paletteUi.count -eq $ExpectedTotal) "previewing $($again.previewing), selected '$($again.paletteUi.selectedTheme)', text '$($again.paletteUi.searchText)'"
    # Closing unloaded the palette; a reopened one follows the window again.
    Check 'reopen/palette-tracks-the-window-theme' ($again.paletteUi.tracksWindowTheme -eq $true) "tracksWindowTheme $($again.paletteUi.tracksWindowTheme)"
    # A theme's name typed and Enter pressed before typing pauses (in the
    # same dispatcher turn as the last key): Enter keeps what the typed text
    # describes, not the full list's top row from before it.
    $kept = Seam $s @{ op = 'palette-type'; text = $Beta; thenKey = 'enter' }
    Check 'confirm/enter-landed-before-the-pause' ($kept.atLastKey.filterPending -eq $true -and $kept.atLastKey.count -eq $ExpectedTotal) "when Enter landed: pending $($kept.atLastKey.filterPending), count $($kept.atLastKey.count)"
    Check 'confirm/closes' (-not $kept.paletteUi.open)
    Check 'confirm/no-preview-left' (-not $kept.previewing)
    Check 'confirm/config-names-the-theme' ($kept.configTheme -eq $Beta) "configTheme '$($kept.configTheme)'"
    Check 'confirm/terminal-stays' ($kept.nativeBackground -eq $Themes[$Beta].bg) "native $($kept.nativeBackground)"
    Check 'confirm/chrome-stays' ($kept.background -eq $Themes[$Beta].bg) "chrome $($kept.background)"
    Check-Pixels $s 'confirm' $Themes[$Beta].bg
    # The palette was held on its opening variant during the browse; what
    # the user sees next is the palette opened again over the dark window it
    # now floats over, drawn dark, and still tracking the window. (A closed
    # palette is unloaded and drawn nowhere, so there is nothing to read
    # before this open.)
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
    Place-SessionWindow $s
    $boot = Boot-Theme $s
    Check 'restart/config-still-names-the-theme' ($boot.configTheme -eq $Beta) "configTheme '$($boot.configTheme)'"
    Check 'restart/terminal-starts-themed' ($boot.nativeBackground -eq $Themes[$Beta].bg) "native $($boot.nativeBackground)"
    Check 'restart/chrome-starts-themed' ($boot.background -eq $Themes[$Beta].bg) "chrome $($boot.background)"
    Check-Pixels $s 'restart' $Themes[$Beta].bg

    # With a theme configured, the list opens on it, marked Current, and
    # Escape returns to it.
    $bootSnap = Snapshot $boot
    $list2 = Open-ThemeList $s
    Check 'restart/highlight-opens-on-the-configured-theme' ($list2.paletteUi.selectedTheme -eq $Beta) "'$($list2.paletteUi.selectedTheme)'"
    # The filter lands on its best match and previews it; the configured
    # theme keeps its Current mark beside the preview.
    $f2 = Seam $s @{ op = 'palette-type'; text = $Token; perCharMs = 40 }
    Check 'restart/filter-lands-on-the-best-match' ($f2.paletteUi.selectedTheme -ceq $Alpha -and $f2.previewTheme -ceq $Alpha -and $f2.paletteUi.previewApplies -eq 1) "selected '$($f2.paletteUi.selectedTheme)', previewTheme '$($f2.previewTheme)', previews $($f2.paletteUi.previewApplies)"
    $opened2 = Wait-Rows $s $Themes.Count
    Check-Rows $s $opened2 'restart' -Current $Beta
    Check-Badges $opened2 'restart/opens' -Current $Beta -Previewing $Alpha
    [void](Seam $s @{ op = 'palette-key'; key = 'down' })
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

    # ---- run 3: a config change landing mid-browse ----------------------
    # The reload an external edit triggers does the revert's job itself:
    # committed config on every view, chrome derived from it, preview
    # handle released. Escape after that must not paint the browse-open
    # chrome snapshot back over the colours the reload derived, or the
    # chrome disagrees with the terminals until the next config event.
    $known = @($Themes.Values | ForEach-Object bg) +
        @('#282C34', '#F4F6FB', '#FFFFFF', '#000000', $UserOnlyTheme.bg) +
        @($CopyCandidates | ForEach-Object bg)
    $ReloadBg = @('#5A2D0A', '#123F66', '#5E0A3C', '#2D5E0A', '#66235A') |
        Where-Object { $c = $_; -not (@($known | Where-Object { Near $_ $c 24 })) } |
        Select-Object -First 1
    if (-not $ReloadBg) { Write-Host 'HARNESS: no reload background clear of the theme set'; exit 1 }

    # auto-reload-config is off by default in this fork, and the scenario is
    # an edit that lands: the watcher has to be on for this run only.
    $s = Start-SeamSession -ExePath $ExePath -ConfigText ($config + "`nauto-reload-config = true`n") -ExtraFiles $extra
    Place-SessionWindow $s
    $cfg3 = Join-Path $s.TempXdg 'wintty\config.wintty'
    [void](Boot-Theme $s)
    [void](Open-ThemeList $s)
    $pv3 = Seam $s @{ op = 'palette-type'; text = $Token; perCharMs = 40 }
    Check 'mid-reload/previewing-when-the-edit-lands' ($pv3.previewing -and $pv3.previewTheme -ceq $Alpha) "previewTheme '$($pv3.previewTheme)'"

    # The external edit: one background line appended to the config file,
    # the way a save from any editor lands.
    $text3 = [System.IO.File]::ReadAllText($cfg3)
    if (-not $text3.EndsWith("`n")) { $text3 += "`r`n" }
    [System.IO.File]::WriteAllText($cfg3, $text3 + "background = $ReloadBg`r`n", [System.Text.UTF8Encoding]::new($false))
    $bytes3 = [System.IO.File]::ReadAllBytes($cfg3)

    # The watcher's reload: preview released, terminal on the new committed
    # background, chrome derived from it, palette still open.
    $reloaded = $null
    $deadline = (Get-Date).AddSeconds(15)
    do {
        $reloaded = Seam $s @{ op = 'get-theme' }
        if (-not $reloaded.previewing -and $reloaded.nativeBackground -eq $ReloadBg) { break }
        Start-Sleep -Milliseconds 150
    } while ((Get-Date) -lt $deadline)
    Check 'mid-reload/the-reload-lands' (-not $reloaded.previewing -and $reloaded.nativeBackground -eq $ReloadBg) "previewing $($reloaded.previewing), native $($reloaded.nativeBackground), want $ReloadBg"
    Check 'mid-reload/chrome-follows-the-reload' ($reloaded.background -eq $ReloadBg) "chrome $($reloaded.background)"
    Check 'mid-reload/palette-stays-open' ($reloaded.paletteUi.open) "open $($reloaded.paletteUi.open)"
    Check-Pixels $s 'mid-reload' $ReloadBg
    $reloadedSnap = Snapshot $reloaded

    $esc3 = Seam $s @{ op = 'palette-key'; key = 'escape' }
    Check 'mid-reload/escape-closes' (-not $esc3.paletteUi.open)
    $diff3 = Same $reloadedSnap (Snapshot $esc3)
    Check 'mid-reload/escape-keeps-the-reloads-colours' ($diff3.Count -eq 0) ($diff3 -join '; ')
    Check 'mid-reload/chrome-still-the-new-background' ($esc3.background -eq $ReloadBg) "chrome $($esc3.background), want $ReloadBg"
    Check 'mid-reload/file-untouched-by-the-escape' (Same-Bytes $cfg3 $bytes3)
    Check-Pixels $s 'mid-reload-escape' $ReloadBg

    # The preview overlay is written beside the state, never in the config
    # directory; once the preview it fed is gone nothing reads it again, so
    # nothing is left behind in the state tree.
    Start-Sleep -Milliseconds 250
    $overlay = @(Get-ChildItem $stateBase -Recurse -Filter 'theme-preview.conf' -ErrorAction SilentlyContinue)
    Check 'mid-reload/no-preview-overlay-left' ($overlay.Count -eq 0) ($overlay.FullName -join ', ')
    Stop-SeamSession $s
    $s = $null

    # ---- run 4: a light/dark pair ---------------------------------------
    # theme = light:X,dark:Y picks its half when the config is read, so the
    # list opens on the half the OS scheme has live, and Escape returns to
    # that half with the pair still in the file.
    $pairLight = $Omega
    $pairDark = $Beta
    $pairValue = "light:$pairLight,dark:$pairDark"
    $pairText = "theme = $pairValue"
    $s = Start-SeamSession -ExePath $ExePath -ConfigText ($config + "`n$pairText`n") -ExtraFiles $extra
    Place-SessionWindow $s
    $cfg4 = Join-Path $s.TempXdg 'wintty\config.wintty'
    $bytes4 = [System.IO.File]::ReadAllBytes($cfg4)
    $pairBoot = Boot-Theme $s
    $pairSnap = Snapshot $pairBoot
    Check 'pair/config-names-both-halves' ($pairBoot.configTheme -ceq $pairValue) "configTheme '$($pairBoot.configTheme)', want '$pairValue'"
    $live = $null
    if ($pairBoot.nativeBackground -eq $Themes[$pairDark].bg) { $live = $pairDark }
    elseif ($pairBoot.nativeBackground -eq $Themes[$pairLight].bg) { $live = $pairLight }
    Check 'pair/terminal-on-a-half' ($null -ne $live) "native $($pairBoot.nativeBackground), dark $($Themes[$pairDark].bg), light $($Themes[$pairLight].bg)"
    $other = if ($live -eq $pairDark) { $pairLight } else { $pairDark }

    $pairList = Open-ThemeList $s
    Check 'pair/highlight-opens-on-the-live-half' ($pairList.paletteUi.selectedTheme -ceq $live) "selected '$($pairList.paletteUi.selectedTheme)', live '$live'"
    $pairRows = Wait-Rows $s $Themes.Count
    Check-Badges $pairRows 'pair/opens' -Current $live

    # Preview the half the OS scheme did not pick, by typing its whole name.
    $pairPrev = Seam $s @{ op = 'palette-type'; text = $other }
    Check 'pair/previews-the-other-half' ($pairPrev.previewing -and $pairPrev.previewTheme -ceq $other -and $pairPrev.nativeBackground -eq $Themes[$other].bg) "previewTheme '$($pairPrev.previewTheme)', native $($pairPrev.nativeBackground)"
    $pairEsc = Seam $s @{ op = 'palette-key'; key = 'escape' }
    $diff4 = Same $pairSnap (Snapshot $pairEsc)
    Check 'pair/escape-returns-to-the-live-half-exactly' ($diff4.Count -eq 0) ($diff4 -join '; ')
    Check 'pair/file-still-names-the-pair' (([System.IO.File]::ReadAllText($cfg4) -split "\r?\n" -contains $pairText) -and (Same-Bytes $cfg4 $bytes4)) 'the pair line moved or the file changed'
    if ($s.Proc.HasExited) { Check 'pair/alive' $false "exit code $($s.Proc.ExitCode)" }
    Stop-SeamSession $s
    $s = $null

    # ---- runs 5 and 6: explicit light and dark app themes ---------------
    foreach ($appTheme in @('dark', 'light')) {
        $want = if ($appTheme -eq 'dark') { 'Dark' } else { 'Light' }
        $s = Start-SeamSession -ExePath $ExePath -ConfigText ($config + "`nwindow-theme = $appTheme`n") -ExtraFiles $extra
        Place-SessionWindow $s
        [void](Boot-Theme $s)
        [void](Open-ThemeList $s)
        [void](Seam $s @{ op = 'palette-type'; text = $Token; perCharMs = 40 })
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
