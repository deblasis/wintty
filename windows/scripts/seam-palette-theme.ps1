#requires -Version 7
<#
    The command palette's theme mode, seam-actuated end to end (issue #1081).

    One app on a temp config root and a temp state base, three staged themes
    with distinctive colours, and no theme configured (the case where the
    user never set one). Each step reads the theme back three ways:

      - the terminal's config, as the live native config states it (the
        preview's while one is up): what every surface renders from;
      - the chrome's resolved colours, all sixteen palette entries included;
      - pixels: a patch of the active terminal's background, captured from
        the screen with the window placed on top without activation.

    Scenario, in one process then a relaunch:

      1. open the palette, run "Change Theme", arrow down twice and back up:
         every move re-themes the live terminal, the config file is untouched;
      2. a held key (30 presses back to back) settles on the last row;
      3. Escape: every readout equals the pre-palette baseline exactly, and the
         config file is byte for byte what it was;
      4. reopen, pick a theme, Enter: the file gains exactly one line,
         `theme = <name>`, and the live views stay on it;
      5. relaunch on that exact file: the theme is still there at startup, the
         highlight opens on it, and a browse-then-Escape returns to it exactly.

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
    # Skip the pixel oracle (a desktop where the window cannot be captured).
    [switch]$NoPixels
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Add-Type -AssemblyName System.Drawing
# One coordinate space for every rect this harness reads (-4 =
# PER_MONITOR_AWARE_V2), matching the seam's screen-pixel rects.
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

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

# Names that sort Alpha < Beta < Gamma and cannot collide with a real theme.
$Themes = [ordered]@{
    'Wintty Probe Alpha' = @{ bg = '#102030'; fg = '#E0E0E0'; p0 = '#202020' }
    'Wintty Probe Beta'  = @{ bg = '#304050'; fg = '#F0E0D0'; p0 = '#404040' }
    'Wintty Probe Gamma' = @{ bg = '#506070'; fg = '#D0F0E0'; p0 = '#606060' }
}
$Alpha, $Beta, $Gamma = @($Themes.Keys)

$extra = @{}
foreach ($name in $Themes.Keys) {
    $t = $Themes[$name]
    $extra["wintty/themes/$name"] =
        "background = $($t.bg)`nforeground = $($t.fg)`npalette = 0=$($t.p0)`npalette = 1=#AA0000`n"
}

$config = @"
windows-single-instance = false
window-save-state = never
background-opacity = 1
"@

$script:Checks = [System.Collections.Generic.List[object]]::new()
$script:Failed = $false
$script:HarnessFault = $false

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

# The dominant colour of a patch near the bottom-right of the active
# terminal: shell output starts top-left, so this is background.
function Sample-TerminalBackground($s) {
    [SeamWin]::PlaceOnTop($s.Hwnd64)
    $r = Seam $s @{ op = 'get-theme' }
    $rect = $r.terminalRect
    if ($null -eq $rect) { throw 'HARNESS: the seam reported no terminal rect' }
    $size = 24
    $x = [int]($rect.x + $rect.w - $size - 16)
    $y = [int]($rect.y + $rect.h - $size - 16)
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size) } finally { $g.Dispose() }
    $counts = @{}
    for ($i = 0; $i -lt $size; $i++) {
        for ($j = 0; $j -lt $size; $j++) {
            $c = $bmp.GetPixel($i, $j)
            $key = '#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B
            $counts[$key] = 1 + ($counts[$key] ?? 0)
        }
    }
    $bmp.Dispose()
    return ($counts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1).Key
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
    Check 'enter/lists-the-staged-themes' ($list.paletteUi.count -ge 3) "count $($list.paletteUi.count)"
    Check 'enter/highlight-starts-at-top' ($list.paletteUi.selectedTheme -eq $Alpha) "selected '$($list.paletteUi.selectedTheme)'"
    Check 'enter/opening-the-list-previews-nothing' (-not $list.previewing)

    foreach ($step in @(
            @{ key = 'down'; want = $Beta },
            @{ key = 'down'; want = $Gamma },
            @{ key = 'up'; want = $Beta })) {
        $r = Seam $s @{ op = 'palette-key'; key = $step.key }
        $t = $Themes[$step.want]
        Check "browse/$($step.key)->$($step.want)/highlight" ($r.paletteUi.selectedTheme -eq $step.want) "'$($r.paletteUi.selectedTheme)'"
        Check "browse/$($step.want)/previewing" ($r.previewing -and $r.previewTheme -eq $step.want) "previewTheme '$($r.previewTheme)'"
        Check "browse/$($step.want)/terminal" ($r.nativeBackground -eq $t.bg -and $r.nativeForeground -eq $t.fg) "native $($r.nativeBackground)/$($r.nativeForeground)"
        Check "browse/$($step.want)/chrome" ($r.background -eq $t.bg -and $r.palette[0] -eq $t.p0) "chrome $($r.background) palette0 $($r.palette[0])"
        Check "browse/$($step.want)/file-untouched" (Same-Bytes $cfgPath $bytes0)
        Check-Pixels $s "browse/$($step.want)" $t.bg
    }

    # A held key: thirty presses back to back settle on the last row.
    $held = Seam $s @{ op = 'palette-key'; key = 'down'; repeat = 30 }
    Check 'held-key/settles-on-the-last-row' ($held.paletteUi.selectedTheme -eq $Gamma -and $held.previewTheme -eq $Gamma -and $held.nativeBackground -eq $Themes[$Gamma].bg) "selected '$($held.paletteUi.selectedTheme)' previewed '$($held.previewTheme)'"

    $esc = Seam $s @{ op = 'palette-key'; key = 'escape' }
    Check 'escape/closes' (-not $esc.paletteUi.open)
    $diff = Same $baseSnap (Snapshot $esc)
    Check 'escape/restores-every-readout-exactly' ($diff.Count -eq 0) ($diff -join '; ')
    Check 'escape/file-byte-for-byte' (Same-Bytes $cfgPath $bytes0)
    if (-not $NoPixels) { Check-Pixels $s 'escape' $basePixel }

    # Reopen: nothing left over from the browse that was cancelled.
    $again = Open-ThemeList $s
    Check 'reopen/clean' (-not $again.previewing -and $again.paletteUi.selectedTheme -eq $Alpha) "previewing $($again.previewing), selected '$($again.paletteUi.selectedTheme)'"
    [void](Seam $s @{ op = 'palette-key'; key = 'down' })
    $kept = Seam $s @{ op = 'palette-key'; key = 'enter' }
    Check 'confirm/closes' (-not $kept.paletteUi.open)
    Check 'confirm/no-preview-left' (-not $kept.previewing)
    Check 'confirm/config-names-the-theme' ($kept.configTheme -eq $Beta) "configTheme '$($kept.configTheme)'"
    Check 'confirm/terminal-stays' ($kept.nativeBackground -eq $Themes[$Beta].bg) "native $($kept.nativeBackground)"
    Check 'confirm/chrome-stays' ($kept.background -eq $Themes[$Beta].bg) "chrome $($kept.background)"
    Check-Pixels $s 'confirm' $Themes[$Beta].bg

    # Compared as lines: the shared config editor writes the whole file back
    # with LF endings on every write (the Settings pages' behaviour too),
    # while the harness staged it with CRLF, so bytes differ on every line.
    $before = @([System.Text.Encoding]::UTF8.GetString($bytes0) -split "\r?\n" | Where-Object { $_ -ne '' })
    $persisted = [System.IO.File]::ReadAllText($cfgPath)
    $after = @($persisted -split "\r?\n" | Where-Object { $_ -ne '' })
    $added = @($after | Where-Object { $before -notcontains $_ })
    $lost = @($before | Where-Object { $after -notcontains $_ })
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

    # With a theme configured, the list opens on it, and Escape returns to it.
    $bootSnap = Snapshot $boot
    $list2 = Open-ThemeList $s
    Check 'restart/highlight-opens-on-the-configured-theme' ($list2.paletteUi.selectedTheme -eq $Beta) "'$($list2.paletteUi.selectedTheme)'"
    $moved = Seam $s @{ op = 'palette-key'; key = 'down' }
    Check 'restart/browse-previews' ($moved.nativeBackground -eq $Themes[$Gamma].bg) "native $($moved.nativeBackground)"
    $esc2 = Seam $s @{ op = 'palette-key'; key = 'escape' }
    $diff2 = Same $bootSnap (Snapshot $esc2)
    Check 'restart/escape-restores-the-configured-theme-exactly' ($diff2.Count -eq 0) ($diff2 -join '; ')
    Check-Pixels $s 'restart-escape' $Themes[$Beta].bg

    if ($s.Proc.HasExited) { Check 'app-alive' $false "exit code $($s.Proc.ExitCode)" }
} catch {
    $msg = "$($_.Exception.Message)"
    if ($msg -like 'PRODUCT_*') { Check 'scenario' $false $msg }
    else { Write-Host "HARNESS: $msg" -ForegroundColor Red; $script:HarnessFault = $true }
    if ($null -ne $s -and -not $s.Proc.HasExited) {
        try {
            $rc = [SeamWin]::RectOf($s.Hwnd64)
            if ($null -ne $rc) {
                $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
                $bmp.Save((Join-Path $OutDir 'fail.png'))
                $g.Dispose(); $bmp.Dispose()
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
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

$passed = @($script:Checks | Where-Object { $_.ok }).Count
Write-Host ''
Write-Host ("{0} of {1} checks passed" -f $passed, $script:Checks.Count)
if ($script:Failed) { exit 2 }
if ($script:HarnessFault) { exit 1 }
exit 0
