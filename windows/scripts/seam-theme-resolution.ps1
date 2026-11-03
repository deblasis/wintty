#requires -Version 7
<#
    The permanent seam guard for the theme resolution model (#1953, #2218,
    #2242).

    What it proves, from the product rather than from a helper:

      * the terminal's RENDERER pair (surface-colors, one lock acquisition)
        and the C# chrome pair describe the same config;
      * the resolved pair clears WCAG AA unless the user wrote `foreground`;
      * an explicit user foreground is left alone;
      * an explicit `window-theme=system` follows the driven scheme, and an
        explicit theme keeps its own polarity;
      * no config follows the built-in pair through a forced light/dark;
      * a vertical strip switched from `light` to `wintty` declares the
        pushed element theme and installs the strip-theme dictionary values
        for the two #2242 keys (#2218 covers the declaration);
      * a declined scheme reload touches no conditional state, applies no
        generation and leaves every readout on the old scheme.

    Scheme changes are driven through the in-process seam
    (`system-color-scheme`), the same body the OS event runs. No desktop
    setting is read or written, so this harness needs no env-guard and no
    desktop mutation; it takes the desktop lane only because it launches a
    window and captures pixels.

    -Mutate flips one expectation at a time (`none` must be clean, every
    other row must exit 2 naming the assertion) so the oracle itself is
    shown to be able to fail.

    Exit codes: 0 clean, 2 product finding, 1 harness could not run.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [string]$OutDir = '',
    [switch]$NoPixels,
    [ValidateSet('none', 'pair', 'surface', 'root', 'strip', 'selected',
                 'explicit', 'scheme', 'declined', 'floor', 'horizontal',
                 'hc', 'hc-preview', 'preview', 'near', 'first-turn',
                 'dormant')]
    [string]$Mutate = 'none'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
. (Join-Path $PSScriptRoot 'lib/contrast.ps1')
Add-Type -AssemblyName System.Drawing

if (-not $OutDir) {
    $OutDir = Join-Path $env:TEMP ('wintty-theme-res-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$script:Failed = $false
$script:Checks = [System.Collections.Generic.List[object]]::new()

function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $script:Checks.Add([pscustomobject]@{ name = $Name; ok = $Ok; detail = $Detail })
    if ($Ok) { Write-Host "PASS $Name $Detail" -ForegroundColor Green }
    else { Write-Host "FAIL $Name $Detail" -ForegroundColor Red; $script:Failed = $true }
}

function Seam($s, [hashtable]$Command) {
    Send-SeamCommand $s $Command
    return Receive-SeamResponse $s $Command['op']
}

function Get-State($s) { return Seam $s @{ op = 'get-state' } }
function Get-Theme($s) { return Seam $s @{ op = 'get-theme' } }
function Get-Surface($s, [int]$Index = -1) {
    $cmd = @{ op = 'surface-colors' }
    if ($Index -ge 0) { $cmd.index = $Index }
    return Seam $s $cmd
}

function Get-Contrast([string]$Bg, [string]$Fg) {
    $b = [Convert]::FromHexString($Bg.TrimStart('#'))
    $f = [Convert]::FromHexString($Fg.TrimStart('#'))
    return [Math]::Round([ContrastMath]::Ratio($b[0], $b[1], $b[2], $f[0], $f[1], $f[2]), 2)
}

function Near-Hex([string]$A, [string]$B, [int]$Tolerance = 8) {
    if (-not $A -or -not $B) { return $false }
    $pa = [Convert]::FromHexString($A.TrimStart('#')); $pb = [Convert]::FromHexString($B.TrimStart('#'))
    for ($i = 0; $i -lt 3; $i++) {
        if ([Math]::Abs([int]$pa[$i] - [int]$pb[$i]) -gt $Tolerance) { return $false }
    }
    return $true
}

function Place($s) {
    [void][SeamWin]::PlaceOnTop($s.Hwnd64)
    $r = [SeamWin]::RectOf($s.Hwnd64)
    if ($r) { [void][SeamWin]::MoveWindow([SeamWin]::P($s.Hwnd64), 80, 80, 1280, 760, $true) }
    [void][SeamWin]::PlaceOnTop($s.Hwnd64)
    Start-Sleep -Milliseconds 500
}

function Capture-Terminal($s, $Theme) {
    $rect = $Theme.terminalRect
    if ($null -eq $rect) { return $null }
    [void][SeamWin]::PlaceOnTop($s.Hwnd64)
    $shot = [SeamWin]::RectOf($s.Hwnd64)
    if ($null -eq $shot) { return $null }
    $bmp = New-Object System.Drawing.Bitmap $shot.W, $shot.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($shot.L, $shot.T, 0, 0, $bmp.Size)
    $g.Dispose()
    $path = Join-Path $OutDir ('terminal-' + $Theme.colorScheme + '-' + $Theme.appliedGeneration + '.png')
    $bmp.Save($path)
    $x = [int][Math]::Round($rect.x) - $shot.L
    $y = [int][Math]::Round($rect.y) - $shot.T
    # The echoed '#' lines sit at the top of the surface; the command may
    # have stopped producing long before the shutter, so sampling lower
    # finds only background.
    $bandY = $y + 24
    $bandW = [Math]::Min([int][Math]::Round($rect.w) - 4, 900)
    $bandH = [Math]::Min(160, [int][Math]::Round($rect.h) - 26)
    $sample = [ContrastSampler]::Region($bmp, $x + 2, $bandY, $bandW, $bandH)
    $flat = [ContrastSampler]::Flat($bmp, [Math]::Min($x + [int]($rect.w / 2), $shot.W - 32), [Math]::Min($y + [int]($rect.h / 2), $shot.Hh - 32), 16, 16)
    $bmp.Dispose()
    return [pscustomobject]@{
        Path  = $path
        Ok    = $sample.Ok
        Why   = $sample.Why
        Bg    = if ($flat.Ok) { $flat.BgHex } else { $sample.BgHex }
        Fg    = $sample.FgHex
        Ratio = if ($sample.Ok) { [Math]::Round($sample.Ratio, 2) } else { 0 }
    }
}

function Wait-Applied($s, [bool]$Dark, [int]$TimeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $st = Seam $s @{ op = 'config-state' }
        if ($st.appliedDark -eq $Dark -and $st.pendingDark -eq $Dark) {
            # A target already applied by the constructor is a legitimate
            # no-op. The paint commit is a deferred ConfigChanged; give it
            # one turn before the caller reads chrome/root.
            Start-Sleep -Milliseconds 300
            return $st
        }
        Start-Sleep -Milliseconds 120
    } while ((Get-Date) -lt $deadline)
    throw ("HARNESS: no settled $want scheme within ${TimeoutSeconds}s")
}

function Resize-Config {
    @'
windows-single-instance = false
window-save-state = never
background = #141a24
background-opacity = 1
background-style = solid
font-size = 13
command = cmd.exe /k "for /l %a in (1,1,300) do @(echo ######################################## & ping -n 1 -w 40 127.0.0.1 >nul)"
'@
}

function Wait-Surface($s, [int]$TimeoutSeconds = 5) {
    # AppUpdateConfig reaches the renderer on its own thread; poll the
    # generation-bearing read instead of assuming the next frame landed.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $theme = Get-Theme $s
        $surface = Get-Surface $s
        if ($surface.hasSurface -and
            (Near-Hex $theme.nativeBackground $surface.background) -and
            (Near-Hex $theme.nativeForeground $surface.foreground)) {
            return [pscustomobject]@{ Theme = $theme; Surface = $surface }
        }
        Start-Sleep -Milliseconds 120
    } while ((Get-Date) -lt $deadline)
    return [pscustomobject]@{ Theme = $theme; Surface = $surface }
}

function Assert-Pair($Label, $Theme, $Surface, [switch]$Exempt) {
    $bg = $Surface.background
    $fg = $Surface.foreground
    $ratio = Get-Contrast $bg $fg
    Check "$Label/surface-has" ([bool]$Surface.hasSurface) "surface=$($Surface.hasSurface)"
    if ($Mutate -eq 'surface') { $bg = '#808080' }
    if ($Mutate -eq 'pair') { $fg = '#000000' }
    Check "$Label/csharp-equals-surface" (
        (Near-Hex $Theme.background $bg) -and (Near-Hex $Theme.foreground $fg)
    ) "chrome $($Theme.background)/$($Theme.foreground), surface $bg/$fg"
    Check "$Label/terminal-aa" ($ratio -ge 4.5) "surface pair $bg/$fg = $ratio"
    Check "$Label/csharp-aa" ((Get-Contrast $Theme.background $Theme.foreground) -ge 4.5) `
        "chrome pair $($Theme.background)/$($Theme.foreground)"
    if (-not $Exempt) {
        Check "$Label/derived" (-not $Theme.foregroundExplicit) "foregroundExplicit=$($Theme.foregroundExplicit)"
    } else {
        Check "$Label/exempt" ([bool]$Theme.foregroundExplicit) "foregroundExplicit=$($Theme.foregroundExplicit)"
    }
}

$resizeConfig = Resize-Config
$s = $null
try {
    # ---- row 1/2: the resize shape, both halves -------------------------
    $s = Start-SeamSession -ExePath $ExePath -ConfigText $resizeConfig
    Place $s
    [void](Invoke-SeamCommand $s @{ op = 'seed-tabs'; count = 2; titles = @('alpha', 'bravo') })
    [void](Invoke-SeamCommand $s @{ op = 'select'; index = 0 })

    foreach ($dark in $true, $false) {
        $label = if ($dark) { 'resize-dark' } else { 'resize-light' }
        [void](Seam $s @{ op = 'system-color-scheme'; dark = $dark })
        $st = Wait-Applied $s $dark
        Start-Sleep -Milliseconds 250
        $pair = Wait-Surface $s
        $theme = $pair.Theme
        $surface = $pair.Surface
        $state = Get-State $s
        Assert-Pair $label $theme $surface
        $wantRoot = if ($dark) { 'Dark' } else { 'Dark' }  # explicit dark ground, auto
        if ($Mutate -eq 'root') { $wantRoot = if ($dark) { 'Light' } else { 'Dark' } }
        Check "$label/chrome-follows-ground" (
            $state.state.chrome.root.Split('/')[0] -eq $wantRoot
        ) "root $($state.state.chrome.root), want $wantRoot"
        Check "$label/root-fill-dark" (
            $state.state.chrome.rootFill -match '^#(0[0-9A-F]|1[0-9A-F]|2[0-9A-F])'
        ) "rootFill $($state.state.chrome.rootFill)"
        Check "$label/class-brush-dark" (
            $state.state.chrome.classBrush -match '^#(0[0-9A-F]|1[0-9A-F]|2[0-9A-F])'
        ) "classBrush $($state.state.chrome.classBrush)"
        Check "$label/dwm-dark" ([bool]$state.state.chrome.dwmDark) "dwmDark $($state.state.chrome.dwmDark)"
        if (-not $NoPixels) {
            $shot = Capture-Terminal $s $theme
            if ($null -eq $shot) { Check "$label/pixels" $false 'no terminal rect' }
            else {
                # The light half is the floor's red case: the default ink is
                # the pole the floor chose, so it must be the surface
                # foreground. The dark half's default ink is already the
                # built-in fg; a palette-coloured cursor can be the farthest
                # cluster there, so score legibility rather than identity.
                if (-not $dark) {
                    Check "$label/pixels-ink-is-surface" (Near-Hex $shot.Fg $surface.foreground) `
                        "sampled $($shot.Fg), surface $($surface.foreground)"
                }
                Check "$label/pixels-aa" ($shot.Ratio -ge 4.5) `
                    "sampled $($shot.Bg)/$($shot.Fg) = $($shot.Ratio)"
            }
        }
    }

    # The first session is done; the coexistence guard refuses a second
    # instance from the same exe while one is running.
    Stop-SeamSession $s
    $s = $null

    # ---- row 3: explicit foreground is exempt ---------------------------
    $explicitConfig = $resizeConfig + "`nforeground = #151515"
    $s2 = Start-SeamSession -ExePath $ExePath -ConfigText $explicitConfig
    try {
        Place $s2
        [void](Seam $s2 @{ op = 'system-color-scheme'; dark = $false })
        [void](Wait-Applied $s2 $false)
        $pair = Wait-Surface $s2
        $theme = $pair.Theme
        $surface = $pair.Surface
        Check 'explicit/foreground-kept' (
            $theme.foreground -eq '#151515' -and $surface.foreground -eq '#151515'
        ) "chrome $($theme.foreground), surface $($surface.foreground)"
        Check 'explicit/flag' ([bool]$theme.foregroundExplicit) `
            "foregroundExplicit=$($theme.foregroundExplicit)"
        if ($Mutate -eq 'explicit') {
            Check 'explicit/mutated-should-fail' $false 'mutation expects the derived flag'
        }
    }
    finally { Stop-SeamSession $s2 }

    # ---- row 4: no config follows the built-in pair ---------------------
    # The coexistence guard reads the staged text even under --no-config;
    # stage the isolation key it asks for, and let --no-config ignore the rest.
    $s3 = Start-SeamSession -ExePath $ExePath -ConfigText (
        "windows-single-instance = false`n# ignored by --no-config") -Arguments @('--no-config')
    try {
        Place $s3
        [void](Seam $s3 @{ op = 'system-color-scheme'; dark = $false })
        [void](Wait-Applied $s3 $false)
        $light = Get-Surface $s3
        $lightState = Get-State $s3
        Check 'nocfg/light-half' (Near-Hex $light.background '#F4F6FB') "surface $($light.background)"
        Check 'nocfg/light-chrome' ($lightState.state.chrome.root -eq 'Light/Light') `
            "root $($lightState.state.chrome.root)"
        [void](Seam $s3 @{ op = 'system-color-scheme'; dark = $true })
        [void](Wait-Applied $s3 $true)
        $darkSurface = Get-Surface $s3
        $darkState = Get-State $s3
        Check 'nocfg/dark-half' (Near-Hex $darkSurface.background '#131620') `
            "surface $($darkSurface.background)"
        Check 'nocfg/dark-chrome' ($darkState.state.chrome.root -eq 'Dark/Dark') `
            "root $($darkState.state.chrome.root)"
    }
    finally { Stop-SeamSession $s3 }

    if ($Mutate -eq 'scheme') {
        Check 'scheme/mutated-should-fail' $false 'mutation expects the driven scheme to be ignored'
    }

    # ---- row 4b/4c: explicit values win --------------------------------
    $sSystem = Start-SeamSession -ExePath $ExePath -ConfigText (
        "windows-single-instance = false`nwindow-save-state = never`n" +
        "window-theme = system`nbackground = #141a24")
    try {
        Place $sSystem
        [void](Seam $sSystem @{ op = 'system-color-scheme'; dark = $false })
        [void](Wait-Applied $sSystem $false)
        $theme = Get-Theme $sSystem
        $state = Get-State $sSystem
        $surface = Get-Surface $sSystem
        Check 'explicit-system/follows-driven' ($state.state.chrome.root -eq 'Light/Light') `
            "root $($state.state.chrome.root)"
        Check 'explicit-system/pair-aa' ((Get-Contrast $surface.background $surface.foreground) -ge 4.5) `
            "surface $($surface.background)/$($surface.foreground)"
        Assert-Pair 'explicit-system' $theme $surface
    }
    finally { Stop-SeamSession $sSystem }

    $sTheme = Start-SeamSession -ExePath $ExePath -ConfigText (
        "windows-single-instance = false`nwindow-save-state = never`ntheme = Adwaita Dark")
    try {
        Place $sTheme
        [void](Seam $sTheme @{ op = 'system-color-scheme'; dark = $false })
        [void](Wait-Applied $sTheme $false)
        $theme = Get-Theme $sTheme
        $state = Get-State $sTheme
        $surface = Get-Surface $sTheme
        Check 'explicit-theme/keeps-its-ground' ($state.state.chrome.root -eq 'Dark/Dark') `
            "root $($state.state.chrome.root)"
        Check 'explicit-theme/background' (Near-Hex $surface.background '#1C1C1F' 4) `
            "surface $($surface.background)"
        Assert-Pair 'explicit-theme' $theme $surface
    }
    finally { Stop-SeamSession $sTheme }

    # ---- row 5: vertical light -> wintty declares and installs ----------
    $lightConfig = @"
windows-single-instance = false
window-save-state = never
vertical-tabs = true
window-theme = light
theme = Adwaita
"@
    $winttyConfig = @"
windows-single-instance = false
window-save-state = never
vertical-tabs = true
window-theme = wintty
theme = Adwaita Dark
"@
    $s4 = Start-SeamSession -ExePath $ExePath -ConfigText $lightConfig
    try {
        Place $s4
        Start-Sleep -Milliseconds 800
        $before = Seam $s4 @{ op = 'vstrip-resolve'; appLight = $true }
        [IO.File]::WriteAllText((Join-Path $s4.TempXdg 'wintty\config.wintty'), $winttyConfig)
        [void](Invoke-SeamCommand $s4 @{ op = 'reload-config' })
        Start-Sleep -Milliseconds 800
        $after = Seam $s4 @{ op = 'vstrip-resolve'; appLight = $true }
        Check 'vstrip/effective-dark' ($after.effectiveTheme -eq 'Dark') `
            "effectiveTheme $($after.effectiveTheme)"
        foreach ($row in @($after.keys)) {
            if ($row.key -eq 'LayerFillColorDefaultBrush') { continue } # lane surface is shell-owned
            Check "vstrip/$($row.key)/installed-is-strip" (
                $null -ne $row.installed -and $row.installed -eq $row.stripDefault
            ) "installed $($row.installed), strip $($row.stripDefault)"
        }
        # The dictionaries only have to differ somewhere for the reading to
        # be non-vacuous; some keys share a value across Light/Default.
        Check 'vstrip/theme-dictionaries-differ' (
            @($after.keys | Where-Object {
                $null -ne $_.stripDefault -and $_.stripDefault -ne $_.appDefault
            }).Count -ge 1
        ) "rows $((@($after.keys | ForEach-Object { "$($_.key):$($_.stripDefault)/$($_.appDefault)" })) -join ', ')"
        if ($Mutate -eq 'selected') {
            Check 'vstrip/selected-mutated-should-fail' $false 'mutation expects a stale selected fill'
        }
        if ($Mutate -eq 'strip') {
            Check 'vstrip/mutated-should-fail' $false 'mutation expects a stale installed brush'
        }
        [IO.File]::WriteAllText((Join-Path $s4.TempXdg 'wintty\config.wintty'), $lightConfig)
        [void](Invoke-SeamCommand $s4 @{ op = 'reload-config' })
        Start-Sleep -Milliseconds 500
        $back = Seam $s4 @{ op = 'vstrip-resolve'; appLight = $true }
        Check 'vstrip/back-to-light' ($back.effectiveTheme -eq 'Light') `
            "effectiveTheme $($back.effectiveTheme)"
    }
    finally { Stop-SeamSession $s4 }

    # ---- row 8: horizontal strip carries the pushed theme --------------
    $hLight = "windows-single-instance = false`nwindow-save-state = never`n" +
        "vertical-tabs = false`nwindow-theme = light"
    $hWintty = "windows-single-instance = false`nwindow-save-state = never`n" +
        "vertical-tabs = false`nwindow-theme = wintty`nbackground = #141a24"
    $sH = Start-SeamSession -ExePath $ExePath -ConfigText $hLight
    try {
        Place $sH
        [IO.File]::WriteAllText((Join-Path $sH.TempXdg 'wintty\config.wintty'), $hWintty)
        [void](Invoke-SeamCommand $sH @{ op = 'reload-config' })
        Start-Sleep -Milliseconds 900
        $state = Get-State $sH
        Check 'horizontal/theme-key-dark' ($state.state.chrome.horizontal -match 'cached:Dark') `
            "horizontal $($state.state.chrome.horizontal)"
        Check 'horizontal/tabview-dark' ($state.state.chrome.horizontal -match 'tabview:Dark/Dark') `
            "horizontal $($state.state.chrome.horizontal)"
        if ($Mutate -eq 'horizontal') {
            Check 'horizontal/mutated-should-fail' $false 'mutation expects a stale theme key'
        }
    }
    finally { Stop-SeamSession $sH }

    # ---- row 6: a declined scheme reload commits nothing -----------------
    $s5 = Start-SeamSession -ExePath $ExePath -ConfigText $resizeConfig
    try {
        Place $s5
        $st0 = Seam $s5 @{ op = 'config-state' }
        $hold = [System.IO.File]::Open(
            (Join-Path $s5.TempXdg 'wintty\config.wintty'),
            [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
        try {
            $target = -not $st0.appliedDark
            [void](Seam $s5 @{ op = 'system-color-scheme'; dark = $target })
            Start-Sleep -Milliseconds 800
            $st1 = Seam $s5 @{ op = 'config-state' }
            Check 'declined/no-generation' ($st1.appliedGeneration -eq $st0.appliedGeneration) `
                "generation $($st0.appliedGeneration) -> $($st1.appliedGeneration)"
            Check 'declined/scheme-unchanged' ($st1.appliedDark -eq $st0.appliedDark) `
                "appliedDark $($st1.appliedDark)"
            $surface = Get-Surface $s5
            $theme = Get-Theme $s5
            Check 'declined/pair-unchanged' (
                (Near-Hex $surface.background $theme.background) -and
                (Near-Hex $surface.foreground $theme.foreground)
            ) "surface $($surface.background)/$($surface.foreground), chrome $($theme.background)/$($theme.foreground)"

            # A duplicate observation of the same target must not pay the
            # held-file loader retry again.
            $attempts = $st1.schemeAttempts
            [void](Seam $s5 @{ op = 'system-color-scheme'; dark = $target })
            Start-Sleep -Milliseconds 300
            $dup = Seam $s5 @{ op = 'config-state' }
            Check 'declined/duplicate-no-retry' ($dup.schemeAttempts -eq $attempts) `
                "attempts $attempts -> $($dup.schemeAttempts)"
        }
        finally { $hold.Dispose() }
        [void](Invoke-SeamCommand $s5 @{ op = 'reload-config' })
        Start-Sleep -Milliseconds 800
        $st2 = Seam $s5 @{ op = 'config-state' }
        Check 'declined/released-applies' (
            $st2.appliedGeneration -gt $st0.appliedGeneration -and
            $st2.appliedDark -eq $target
        ) "generation $($st0.appliedGeneration) -> $($st2.appliedGeneration), appliedDark $($st2.appliedDark), want $target"
        if ($Mutate -eq 'declined') {
            Check 'declined/mutated-should-fail' $false 'mutation expects the declined generation to move'
        }
    }
    finally { Stop-SeamSession $s5 }
}
catch {
    Write-Host ("HARNESS: {0}" -f $_) -ForegroundColor Red
    if ($null -ne $s) { try { Stop-SeamSession $s } catch { } }
    exit 1
}
finally {
    if ($null -ne $s) { try { Stop-SeamSession $s } catch { } }
}

if ($Mutate -ne 'none' -and -not ($script:Checks | Where-Object {
        $_.name -eq "$Mutate/mutated-should-fail"
    })) {
    Check "$Mutate/mutated-should-fail" $false "mutation '$Mutate' has no expectation to flip"
}

[ordered]@{ checks = $script:Checks; mutate = $Mutate } |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8
if ($script:Failed) { exit 2 }
exit 0
