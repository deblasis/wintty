#requires -Version 7
<#
    The idle badge, checked against the running app.

    TabIdleTrackerTests prove the state machine and TabIdleWiringTests
    pin the hops, but "the moon is painted and the row is dim" is a
    claim about pixels, and pixels are only observable on a live window.
    The `tab-idle` seam op writes the property the sweep owns, so this
    harness drives the exact INPC chain the product's one-minute sweep
    drives -- without waiting a minute per leg.

    The oracle is layered, because no one signal survives every theme:

      - state readback: the op answers with tabs[n].idle, proving the
        property round trip;
      - geometry: `header-rect` part "idle" returns the moon's screen
        rect only when the glyph is laid out, so hidden <=> no rect is a
        real signal in the horizontal strip;
      - pixels: the moon's rect is cropped from a settled screenshot and
        must hold glyph ink when idle and none when awake; the title
        rect's mean luminance must drop when the row dims.

    The vertical strips have no header-rect equivalent for the moon, so
    their legs capture settled screenshots for image analysis and keep
    the seam readback as the machine oracle.

    Seam-driven throughout; no synthesized input. Exits 0 clean, 2
    findings, 1 could-not-run.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots') | Out-Null
Add-Type -AssemblyName System.Drawing
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$Config = @'
windows-single-instance = true
window-save-state = never
vertical-tabs = false
profile.pwsh.name = PowerShell
profile.pwsh.command = pwsh.exe -NoProfile
default-profile = pwsh
'@

$script:Findings = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null

function Shot([string]$Name) {
    $rc = [SeamWin]::RectOf($session.Hwnd64)
    if ($null -eq $rc) { throw "HARVEST_MISS: degenerate rect for $Name" }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $p = Join-Path $OutDir "shots\$Name.png"
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose()
    return $p
}

# Std-dev of luminance over a screen rect cropped out of a full-window
# shot. The rect arrives in screen px from the seam; the shot starts at
# the window's left/top, so the crop origin is rect - window.
#
# Std-dev, not the mean, because the mean's direction under the dim
# depends on the theme: fading dark text over a light strip BRIGHTENS
# the region, fading light text over a dark strip darkens it. What the
# dim always does is compress the contrast between the text ink and the
# strip background, and the region's spread measures exactly that.
function StdDev-Luminance([string]$pngPath, [double]$sx, [double]$sy, [double]$sw, [double]$sh) {
    $bmp = [System.Drawing.Bitmap]::FromFile($pngPath)
    try {
        $rc = [SeamWin]::RectOf($session.Hwnd64)
        $x0 = [int][Math]::Max(0, $sx - $rc.L); $y0 = [int][Math]::Max(0, $sy - $rc.T)
        $x1 = [int][Math]::Min($bmp.Width, $x0 + [Math]::Max(1, $sw))
        $y1 = [int][Math]::Min($bmp.Height, $y0 + [Math]::Max(1, $sh))
        $sum = 0.0; $sumSq = 0.0; $n = 0
        for ($y = $y0; $y -lt $y1; $y++) {
            for ($x = $x0; $x -lt $x1; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $l = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
                $sum += $l; $sumSq += $l * $l; $n++
            }
        }
        if ($n -eq 0) { return -1.0 }
        $mean = $sum / $n
        return [Math]::Sqrt([Math]::Max(0.0, $sumSq / $n - $mean * $mean))
    } finally { $bmp.Dispose() }
}

function Rect-Has-Ink([string]$pngPath, [double]$sx, [double]$sy, [double]$sw, [double]$sh) {
    $bmp = [System.Drawing.Bitmap]::FromFile($pngPath)
    try {
        $rc = [SeamWin]::RectOf($session.Hwnd64)
        $x0 = [int][Math]::Max(0, $sx - $rc.L); $y0 = [int][Math]::Max(0, $sy - $rc.T)
        $x1 = [int][Math]::Min($bmp.Width, $x0 + [Math]::Max(1, $sw))
        $y1 = [int][Math]::Min($bmp.Height, $y0 + [Math]::Max(1, $sh))
        $minL = 255.0; $maxL = 0.0
        for ($y = $y0; $y -lt $y1; $y++) {
            for ($x = $x0; $x -lt $x1; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $l = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
                if ($l -lt $minL) { $minL = $l }
                if ($l -gt $maxL) { $maxL = $l }
            }
        }
        # Ink means contrast: a glyph has bright and dark pixels; an
        # empty background patch does not. 30 is comfortably above
        # antialiasing on a flat fill and below any real glyph.
        return ($maxL - $minL) -ge 30.0
    } finally { $bmp.Dispose() }
}

function Get-IdleRect([int]$Index) {
    # Absence is the answer this exists to detect, but the seam (and
    # Invoke-SeamCommand) treats an error reply as fatal so a caller
    # cannot miss one. "No rect" arrives as a throw; catch it here and
    # translate to null, which is what the callers below reason about.
    try {
        $r = Invoke-SeamCommand $session @{ op = 'header-rect'; index = $Index; part = 'idle' }
    }
    catch { return $null }
    if (-not $r.ok) { return $null }
    return $r
}

# ---- run --------------------------------------------------------------

# The machine-wide seam lock, same contract as every capture harness:
# PlaceOnTop plus screenshots are window-global side effects, and two
# harnesses doing them at once corrupt both films.
$ownLock = $null
if (-not $env:WINTTY_SEAM_LOCK_HELD) {
    . C:\temp\seam-lock.ps1
    $ownLock = Enter-SeamLock -Owner 'idle-badge-check'
}

try {
    Assert-NoWintty -Context 'the idle badge check'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config
    if (-not (Wait-SeamReady $session.Proc)) { throw 'SEAM_REFUSED: app never announced the pipe' }

    # One deterministic window rect so the shots are comparable leg to
    # leg regardless of what the desktop did before.
    [void][SeamWin]::MoveWindow([SeamWin]::P([int64]$session.Hwnd64), 60, 60, 1000, 700, $true)
    Start-Sleep -Milliseconds 500

    # Three tabs: 0 active, 1 and 2 the audience.
    [void](Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = 3 })
    Start-Sleep -Milliseconds 800

    # ---- horizontal strip -------------------------------------------
    # Awake baseline: no moon rect, full-contrast title.
    if ($null -ne (Get-IdleRect 1)) {
        $script:Findings.Add('horizontal: the moon reports a rect while the tab is awake')
    }
    $awake = Shot('h-awake')
    $titleRect = Invoke-SeamCommand $session @{ op = 'header-rect'; index = 1; part = 'title' }
    $awakeSpread = StdDev-Luminance $awake $titleRect.x $titleRect.y $titleRect.w $titleRect.h

    # Idle: property round trip, moon rect exists and holds ink.
    $st = Invoke-SeamCommand $session @{ op = 'tab-idle'; index = 1; idle = $true }
    if (-not $st.state.tabs[1].idle) {
        $script:Findings.Add('horizontal: tab-idle op did not report the property back')
    }
    $moon = Get-IdleRect 1
    if ($null -eq $moon) {
        $script:Findings.Add('horizontal: no idle rect while the tab is idle (glyph not laid out)')
    } else {
        $idleShot = Shot('h-idle')
        if (-not (Rect-Has-Ink $idleShot $moon.x $moon.y $moon.w $moon.h)) {
            $script:Findings.Add('horizontal: the moon rect holds no ink (glyph not painted)')
        }
        $dimSpread = StdDev-Luminance $idleShot $titleRect.x $titleRect.y $titleRect.w $titleRect.h
        # The title dims to 0.45 opacity, which compresses the ink-vs-
        # background contrast the region's spread measures. 0.8 leaves
        # room for antialiasing and is far above noise on a settled
        # frame.
        if ($dimSpread -ge 0.0 -and $awakeSpread -ge 0.0 -and $dimSpread -gt ($awakeSpread * 0.8)) {
            $script:Findings.Add("horizontal: title contrast did not compress (awake=$awakeSpread idle=$dimSpread)")
        }
    }

    # Awake again: the moon rect must be gone.
    [void](Invoke-SeamCommand $session @{ op = 'tab-idle'; index = 1; idle = $false })
    Start-Sleep -Milliseconds 300
    if ($null -ne (Get-IdleRect 1)) {
        $script:Findings.Add('horizontal: the moon rect survives clearing the idle state')
    }

    # ---- vertical strip ---------------------------------------------
    [void](Invoke-SeamCommand $session @{ op = 'toggle-layout'; await = $true })
    Start-Sleep -Milliseconds 600
    [void](Shot('v-awake'))
    $st = Invoke-SeamCommand $session @{ op = 'tab-idle'; index = 1; idle = $true }
    if (-not $st.state.tabs[1].idle) {
        $script:Findings.Add('vertical: tab-idle op did not report the property back')
    }
    Start-Sleep -Milliseconds 300
    [void](Shot('v-idle-body'))

    # A pinned idle square: pin tab 2, idle it, shoot the band.
    [void](Invoke-SeamCommand $session @{ op = 'pin'; index = 2 })
    [void](Invoke-SeamCommand $session @{ op = 'tab-idle'; index = 2; idle = $true })
    Start-Sleep -Milliseconds 300
    [void](Shot('v-idle-pinned'))
}
catch {
    $harnessError = $_.Exception.Message
}
finally {
    if ($null -ne $session) { Stop-SeamSession $session }
    if ($ownLock) { Exit-SeamLock $ownLock }
}

if ($harnessError) {
    Write-Host "HARNESS-ERROR: $harnessError"
    exit 1
}
if ($script:Findings.Count -gt 0) {
    $script:Findings | ForEach-Object { Write-Host "FINDING: $_" }
    exit 2
}
Write-Host 'idle-badge-check: clean (moon, dim, and state round trip verified)'
exit 0
