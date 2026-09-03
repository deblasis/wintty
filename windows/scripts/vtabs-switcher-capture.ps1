#requires -Version 7
<#
    The Ctrl+Tab switcher with many coloured tabs, seam-actuated, and the
    wrap claim GATED for the first time (#930).

    The headline this file always described and never asserted: with more
    tabs than fit one row, the tiles wrap into a grid instead of running
    off the right edge. The oracle now reads the popup's own tiles over
    UIA the instant cycle{forward} raises them - the op dispatches the
    chord's real RequestMruCycle, and the popup auto-dismisses on a 1.2s
    timer, so the read races the timer and the pictures follow it. Two
    gates: every tile's right edge inside the window, and at least two
    distinct rows once the count exceeds what one row holds.

    Everything that used to be synthesized input is a seam op now: the
    tabs are real new-tab chords (default-bound ctrl+t, focus on the
    frame first - a new tab re-homes focus into a pane), so the tiles
    carry real shell titles rather than the UserOverrideTitle seed-tabs
    would stamp; the colours are tab-color ops driving the picker's own
    assignment; the overview is the ctrl+shift+e chord. Zero OS input.

    Exits 0 when the wrap holds and the run staged, 2 a finding (a tile
    outside the window, one row holding everything, a colour that did not
    land), 1 could-not-run.
#>
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\Ghostty\bin\x64\Debug\net10.0-windows10.0.19041.0\Wintty.exe'),
    [string]$OutDir = (Join-Path $PSScriptRoot ("vtabs-switcher/run-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [int]$TabCount = 14,
    [switch]$Vertical
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$Config = @"
vertical-tabs = $($Vertical.IsPresent.ToString().ToLower())
windows-single-instance = false
window-theme = wintty
theme = Catppuccin Mocha
"@

$script:Findings = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null

function Save-Shot($Session, [string]$Name) {
    $r = [SeamWin]::RectOf($Session.Hwnd64)
    if ($null -eq $r) { return }
    $bmp = New-Object System.Drawing.Bitmap $r.W, $r.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir "$Name.png")); $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $Name ($($r.W)x$($r.Hh))"
}

function Invoke-Chord($Session, [int]$Key, [switch]$Plain) {
    [void](Invoke-SeamCommand $Session @{ op = 'focus'; target = 'frame' })
    $r = Invoke-SeamCommand $Session @{ op = 'chord'; key = $Key; ctrl = $true; shift = -not $Plain.IsPresent }
    if (-not $r.dispatched) {
        throw ("HARVEST_MISS: chord 0x{0:X2} was not dispatched (focus was '{1}')" -f $Key, $r.focus)
    }
}

# The popup's tiles as rects. The cells expose no container of their own in
# UIA - each tile surfaces as its icon Image inside the popup window - so
# the icon rects ARE the tile grid (probed live: same Xs stepping down the
# Ys is the wrap itself). Null when the popup is not up.
function Get-SwitcherTiles([int64]$Hwnd64) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([SeamWin]::P($Hwnd64))
    $winCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $popups = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $winCond)
    $popup = $null
    foreach ($w in $popups) {
        if ($w.Current.ControlType.ProgrammaticName -eq 'ControlType.Window' -and
            $w.Current.Name -eq 'Pop-up') { $popup = $w; break }
    }
    if ($null -eq $popup) { return $null }
    $imgCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Image)
    $icons = $popup.FindAll([System.Windows.Automation.TreeScope]::Descendants, $imgCond)
    if ($icons.Count -lt 1) { return $null }
    $tiles = foreach ($t in $icons) {
        $r = $t.Current.BoundingRectangle
        [pscustomobject]@{ L = [double]$r.X; T = [double]$r.Y; R = [double]($r.X + $r.Width); B = [double]($r.Y + $r.Height) }
    }
    return @($tiles)
}

try {
    Assert-NoWintty -Context 'The switcher capture'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config
    $hwnd64 = [int64]$session.Hwnd64
    Start-Sleep -Milliseconds 500
    [void][SeamWin]::MoveWindow([SeamWin]::P($hwnd64), 30, 30, 1500, 900, $true)
    Start-Sleep -Milliseconds 700

    # Real new tabs: the default-bound ctrl+t through the chord path, so the
    # tiles carry the shells' own titles.
    for ($i = 2; $i -le $TabCount; $i++) {
        Invoke-Chord $session 0x54 -Plain
        Start-Sleep -Milliseconds 420
    }
    $tabs = @(Invoke-SeamCommand $session @{ op = 'get-state' })
    $tabTotal = @($tabs[0].state.tabs).Count
    Write-Host "tabs=$tabTotal (wanted $TabCount)"
    if ($tabTotal -lt 4) {
        throw "HARVEST_MISS: only $tabTotal tabs - the new-tab chords did not land"
    }

    # Colour a spread so the tiles show coloured and plain side by side.
    $wanted = @('Red', 'Green', 'Blue', 'Orange', 'Purple', 'Teal')
    $applied = 0
    for ($i = 0; $i -lt $wanted.Count; $i++) {
        $index = ($i + 1) * 2
        if ($index -ge $tabTotal) { break }
        $null = Invoke-SeamCommand $session @{ op = 'tab-color'; index = $index; color = $wanted[$i] }
        $applied++
    }
    Write-Host "colors applied: $applied"
    # The colour landing is part of the claim: read them back.
    $st = Invoke-SeamCommand $session @{ op = 'get-state' }
    $coloured = @($st.state.tabs | Where-Object { $_.color -and $_.color -ne 'None' }).Count
    if ($coloured -lt $applied) {
        $script:Findings.Add("tab-colour ops answered ok but the state shows $coloured of $applied coloured tabs")
    }

    Save-Shot $session 'strip-with-colours'

    # The switcher, the wrap oracle, then the pictures - in that order,
    # because the popup's 1.2s timer is already running.
    $null = Invoke-SeamCommand $session @{ op = 'cycle'; forward = $true }
    $tiles = Get-SwitcherTiles $hwnd64
    if ($null -eq $tiles) {
        throw 'HARVEST_MISS: the switcher popup raised no tile icons before its timer closed it'
    }
    $win = [SeamWin]::RectOf($hwnd64)
    $outside = @($tiles | Where-Object { $_.R -gt ($win.L + $win.W - 4) })
    $rows = @($tiles | ForEach-Object { [Math]::Round($_.T) } | Sort-Object -Unique)
    Write-Host ("tiles={0} rows={1} outside={2}" -f $tiles.Count, $rows.Count, $outside.Count)
    if ($outside.Count -gt 0) {
        $script:Findings.Add("$($outside.Count) switcher tile(s) run past the window's right edge instead of wrapping")
    }
    if ($tiles.Count -ge 8 -and $rows.Count -lt 2) {
        $script:Findings.Add("$($tiles.Count) tiles sit on ONE row - the grid wrap did not happen")
    }
    foreach ($n in 0..2) {
        Start-Sleep -Milliseconds 120
        Save-Shot $session ("switcher-$n")
    }

    # The overview, same chord family.
    Start-Sleep -Milliseconds 1500
    Invoke-Chord $session 0x45
    Start-Sleep -Milliseconds 700
    Save-Shot $session 'overview'

    if ($session.Proc.HasExited) {
        throw "APP_EXIT: the app exited during the run (code $($session.Proc.ExitCode))"
    }
}
catch {
    $msg = "$($_.Exception.Message)"
    if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { $script:Findings.Add($msg) }
    else { $harnessError = $msg }
    Write-Host "ERROR: $msg" -ForegroundColor Red
}
finally {
    if ($null -ne $session) { Stop-SeamSession $session }
}

[ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=<session token>); tabs/colours/switcher/overview via ops and chords'
    findings  = $script:Findings
    harness   = $harnessError
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

if ($script:Findings.Count -gt 0) { exit 2 }
if ($harnessError) { exit 1 }
Write-Host "OUT=$OutDir"
exit 0
