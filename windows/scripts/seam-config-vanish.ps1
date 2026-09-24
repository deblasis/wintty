#requires -Version 7
<#
    A deleted config file, seam-actuated against the real ConfigService
    (wintty#1155). auto-reload-config is left at its default, off, so there
    is no file watcher: every look the service takes after a declined reload
    is one it schedules for itself.

    Scenario 1, the watched file:
      1. launch on wintty/config.wintty with a background of its own;
      2. delete the file, then ask for a High Contrast palette ONCE, the
         call HighContrastMonitor makes (no OS flip);
      3. within a few seconds, with nothing else touching the app, an
         applied reload carries that palette: the live native background is
         the palette's, and so is the service's HighContrastBackground;
      4. recreate the file with another background and reload once: that
         background is live.

    Scenario 2, one of two layered files (a user migrated from Ghostty):
      1. launch on ghostty/config.ghostty AND wintty/config.wintty;
      2. delete wintty/config.wintty, ask for the palette once;
      3. within a few seconds an applied reload carries it, and the running
         config is the legacy file's.

    Applied reloads are read from the seam's config-change log, recorded
    when each reload's ConfigChanged fan-out runs, because on a desktop with
    High Contrast off the app's own HighContrastMonitor asks for no palette
    right after every applied reload: the palette is on screen for that one
    reload, and the log is where it is visible. The fan-out is posted, so
    the log cannot say whether the request's own reload declined; that is
    read from the op's answer, which is the live config right after it.
    Each heal must also take at least -FloorAtLeastMs: a heal sooner than
    the floor means the first report was believed.

    What it does not cover: the watcher path (auto-reload-config on), a
    locked or unreadable file, and anything that needs the OS High Contrast
    actually on.

    Zero OS input is synthesized. The config root and the state base are
    temp directories of this run, single-instance is off, and cleanup stops
    only processes started from -ExePath after this run began.

    Exits 0 on pass, 2 on a product finding, 1 when the harness could not
    run and nothing is known about the product.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # How long an applied reload carrying the palette may take. The floor is
    # 900ms and the shrink budget three 300ms looks; the rest is headroom
    # for a loaded machine.
    [int]$HealWithinMs = 4000,
    # The least a heal may take. The vanish floor is 900ms and the shrink
    # budget three 300ms looks, so a heal sooner than this did not wait for
    # either: the first report was believed, which is #1146.
    [int]$FloorAtLeastMs = 700
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$script:Checks = [System.Collections.Generic.List[object]]::new()
$script:Failed = $false

function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $script:Checks.Add([pscustomobject]@{ name = $Name; ok = $Ok; detail = $Detail })
    if ($Ok) { Write-Host "PASS $Name $Detail" -ForegroundColor Green }
    else { Write-Host "FAIL $Name $Detail" -ForegroundColor Red; $script:Failed = $true }
}

function Seam($s, [hashtable]$Command) {
    Send-SeamCommand $s $Command
    return Receive-SeamResponse $s $Command['op']
}

# The palette asked for: distinctive, and far from any background staged.
$Hc = @{
    op = 'high-contrast'
    background = '#0A1B2C'; foreground = '#F0E0D0'
    selectionBackground = '#3D4E5F'; selectionForeground = '#FFFFFF'
}

# Whether the High Contrast op's own answer shows its reload declined. That
# answer is read synchronously after the op's Reload returned, so it is the
# live config that reload left; the change log is posted and would show
# nothing yet whether the reload declined or applied.
function Test-Declined($Answer) {
    return $Answer.nativeBackground -eq '#102030' -and $null -eq $Answer.highContrastBackground
}

# Poll the change log until an applied reload after $Since entries carries
# the palette's background, or the budget runs out. Answers the entry index
# and the elapsed milliseconds, or $null.
function Wait-PaletteApplied($s, [int]$Since, [string]$Background) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $HealWithinMs) {
        $st = Seam $s @{ op = 'config-state' }
        $changes = @($st.changes)
        for ($i = $Since; $i -lt $changes.Count; $i++) {
            if ($changes[$i].nativeBackground -eq $Background -and
                $changes[$i].highContrastBackground -eq $Background) {
                return [pscustomobject]@{ Index = $i; Ms = $sw.ElapsedMilliseconds; State = $st }
            }
        }
        Start-Sleep -Milliseconds 50
    }
    return $null
}

$baseConfig = @"
windows-single-instance = false
window-save-state = never
background = #102030
"@

$exitCode = 0
$s = $null
try {
    # ---- Scenario 1: the watched file ----
    $s = Start-SeamSession -ExePath $ExePath -ConfigText $baseConfig -PrivateStateBase
    $cfg = Join-Path $s.TempXdg 'wintty\config.wintty'

    $st = Seam $s @{ op = 'config-state' }
    Check 's1.start: the file is live' ($st.nativeBackground -eq '#102030') "native=$($st.nativeBackground)"

    Remove-Item $cfg -Force
    $before = @((Seam $s @{ op = 'config-state' }).changes).Count
    $asked = Seam $s $Hc
    Check 's1.toggle: the first reload is declined' (Test-Declined $asked) "native=$($asked.nativeBackground) hc=$($asked.highContrastBackground)"

    $got = Wait-PaletteApplied $s $before $Hc.background
    Check 's1.heal: one request lands with no further input' ($null -ne $got) $(
        if ($got) { "after $($got.Ms)ms" } else { "no applied reload carried $($Hc.background) within ${HealWithinMs}ms" })
    Check 's1.floor: the heal waited out the floor' ($null -ne $got -and $got.Ms -ge $FloorAtLeastMs) $(
        if ($got) { "after $($got.Ms)ms, at least ${FloorAtLeastMs}ms required" } else { 'no heal to time' })

    Seam $s @{ op = 'high-contrast'; off = $true } | Out-Null
    [System.IO.File]::WriteAllText($cfg, "windows-single-instance = false`nwindow-save-state = never`nbackground = #405060`n")
    $re = Seam $s @{ op = 'reload-config' }
    Check 's1.recreate: the new file applies on one reload' ($re.applied -and $re.nativeBackground -eq '#405060') "applied=$($re.applied) native=$($re.nativeBackground)"

    Stop-SeamSession $s
    $s = $null

    # ---- Scenario 2: one of two layered files ----
    $legacy = "windows-single-instance = false`nwindow-save-state = never`nbackground = #203040`n"
    $s = Start-SeamSession -ExePath $ExePath -ConfigText $baseConfig -PrivateStateBase `
        -ExtraFiles @{ 'ghostty/config.ghostty' = $legacy }
    $cfg = Join-Path $s.TempXdg 'wintty\config.wintty'

    $st = Seam $s @{ op = 'config-state' }
    Check 's2.start: the newer file wins' ($st.nativeBackground -eq '#102030') "native=$($st.nativeBackground)"

    Remove-Item $cfg -Force
    $before = @((Seam $s @{ op = 'config-state' }).changes).Count
    $asked = Seam $s $Hc
    Check 's2.toggle: the first reload is declined' (Test-Declined $asked) "native=$($asked.nativeBackground) hc=$($asked.highContrastBackground)"

    $got = Wait-PaletteApplied $s $before $Hc.background
    Check 's2.heal: one request lands with no further input' ($null -ne $got) $(
        if ($got) { "after $($got.Ms)ms" } else { "no applied reload carried $($Hc.background) within ${HealWithinMs}ms" })
    Check 's2.floor: the heal waited out its budget' ($null -ne $got -and $got.Ms -ge $FloorAtLeastMs) $(
        if ($got) { "after $($got.Ms)ms, at least ${FloorAtLeastMs}ms required" } else { 'no heal to time' })

    Seam $s @{ op = 'high-contrast'; off = $true } | Out-Null
    $re = Seam $s @{ op = 'reload-config' }
    Check 's2.after: the legacy file is what runs' ($re.nativeBackground -eq '#203040') "applied=$($re.applied) native=$($re.nativeBackground)"
}
catch {
    Write-Host "HARNESS: $($_.Exception.Message)" -ForegroundColor Yellow
    if ($_.Exception.Message -match '^PRODUCT_') { $script:Failed = $true }
    else { $exitCode = 1 }
}
finally {
    if ($null -ne $s) { Stop-SeamSession $s }
}

$summary = [ordered]@{
    harness = 'seam-config-vanish'
    exe = $ExePath
    actuation = 'seam (WINTTY_TEST_SEAM=<session token>); zero synthesized OS input'
    checks = $script:Checks
}
$summary | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8
if ($exitCode -ne 0) { exit $exitCode }
if ($script:Failed) { exit 2 }
exit 0
