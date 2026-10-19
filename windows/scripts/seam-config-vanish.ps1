#requires -Version 7
<#
    A deleted config file, seam-actuated against the real ConfigService.
    auto-reload-config is left at its default, off, so there is no file
    watcher: the watched file's deletion is concluded through load verdicts
    alone, and what it causes is the missing-config notice and the settings
    the session already had (wintty#1158). Absence of the file is not an
    instruction any more, so a High Contrast ask over a deleted file stays
    declined instead of healing onto a defaults config.

    Scenario 1, the watched file:
      1. launch on wintty/config.wintty with a background of its own;
      2. delete the file, then ask for a High Contrast palette ONCE, the
         call HighContrastMonitor makes (no OS flip);
      3. the ask is declined, and STAYS declined: no applied reload carries
         the palette within the window, and the live native background is
         still the one the file had. The settings the session had keep
         running, and the missing-config banner is up (the notices op);
      4. recreate the file with another background and reload once: that
         background is live, and the banner is down. The reappearance, not
         the absence, is the instruction.

    Scenario 2, one of two layered files (a user migrated from Ghostty):
      1. launch on ghostty/config.ghostty AND wintty/config.wintty;
      2. delete wintty/config.wintty, ask for the palette once;
      3. within a few seconds an applied reload carries it, and the running
         config is the legacy file's: a count shrink past the floor is the
         user's configuration as it now stands, and it still applies.

    Applied reloads are read from the seam's config-change log, recorded
    when each reload's ConfigChanged fan-out runs, because on a desktop with
    High Contrast off the app's own HighContrastMonitor asks for no palette
    right after every applied reload: the palette is on screen for that one
    reload, and the log is where it is visible. The fan-out is posted, so
    the log cannot say whether the request's own reload declined; that is
    read from the op's answer, which is the live config right after it.
    Scenario 2's heal must also take at least -FloorAtLeastMs: a heal sooner
    than the floor means the first report was believed.

    What it does not cover: the watcher path (auto-reload-config on), a
    locked or unreadable file, the load-defaults action's own click, and
    anything that needs the OS High Contrast actually on.

    Zero OS input is synthesized. The config root and the state base are
    temp directories of this run, single-instance is off, and cleanup stops
    only processes started from -ExePath after this run began.

    Exits 0 on pass, 2 on a product finding, 1 when the harness could not
    run and nothing is known about the product.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # How long an applied reload carrying the palette may take (scenario 2),
    # and how long scenario 1 must STAY declined. The floor is 900ms and the
    # shrink budget three 300ms looks; the rest is headroom for a loaded
    # machine.
    [int]$HealWithinMs = 4000,
    # The least scenario 2's heal may take. The vanish floor is 900ms and
    # the shrink budget three 300ms looks, so a heal sooner than this did
    # not wait for either: the first report was believed, which is #1146.
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

# The scenario 1 half, inverted for wintty#1158: for the whole window, no
# applied reload carries the palette and the live native background stays
# the deleted file's. Answers what actually happened if the ask healed
# anyway, or $null when the decline held for the whole window.
function Wait-PaletteStaysDeclined($s, [int]$Since, [string]$Original) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $HealWithinMs) {
        $st = Seam $s @{ op = 'config-state' }
        $changes = @($st.changes)
        for ($i = $Since; $i -lt $changes.Count; $i++) {
            if ($changes[$i].highContrastBackground -ne $null) {
                return [pscustomobject]@{ Ms = $sw.ElapsedMilliseconds; State = $st;
                                          How = 'an applied reload carried the palette' }
            }
        }
        if ($st.nativeBackground -ne $Original) {
            return [pscustomobject]@{ Ms = $sw.ElapsedMilliseconds; State = $st;
                                      How = "the live background moved to $($st.nativeBackground)" }
        }
        Start-Sleep -Milliseconds 50
    }
    return $null
}

# Poll the notices op until a banner with $Key is up (Want $true) or gone
# (Want $false), or the budget runs out. Answers what was on screen at the
# last look.
function Wait-Notice($s, [string]$Key, [bool]$Want) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $last = $null
    while ($sw.ElapsedMilliseconds -lt $HealWithinMs) {
        $n = Seam $s @{ op = 'notices' }
        $last = $n
        $up = @($n.notices | Where-Object { $_.dedupKey -eq $Key }).Count -gt 0
        if ($up -eq $Want) {
            return [pscustomobject]@{ Ms = $sw.ElapsedMilliseconds; Seen = $up; Last = $n }
        }
        Start-Sleep -Milliseconds 50
    }
    return [pscustomobject]@{ Ms = $sw.ElapsedMilliseconds; Seen = $false; Last = $last }
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

    # The ask stays declined for the whole window, and the live background
    # stays the deleted file's: the settings the session had keep running,
    # and the palette is not healed onto a defaults config. An applied
    # reload here would be the absence being treated as an instruction,
    # which is the silent reset wintty#1158 removed.
    $broke = Wait-PaletteStaysDeclined $s $before '#102030'
    Check 's1.hold: the ask stays declined and the settings stand' ($null -eq $broke) $(
        if ($broke) { "after $($broke.Ms)ms: $($broke.How)" }
        else { "declined for the whole ${HealWithinMs}ms, background unchanged" })

    # The conclusion is told, not silent: the missing-config banner is up,
    # with the owner's copy and the load-defaults action.
    $notice = Wait-Notice $s 'config-file-missing' $true
    $up = @($notice.Last.notices | Where-Object { $_.dedupKey -eq 'config-file-missing' }) | Select-Object -First 1
    Check 's1.notice: the missing-config banner is up' ($null -ne $up -and $up.title -eq 'Config file missing' -and $up.actions -ge 1) $(
        if ($up) { "title=$($up.title) actions=$($up.actions), after $($notice.Ms)ms" }
        else { "no config-file-missing banner within ${HealWithinMs}ms" })

    Seam $s @{ op = 'high-contrast'; off = $true } | Out-Null
    [System.IO.File]::WriteAllText($cfg, "windows-single-instance = false`nwindow-save-state = never`nbackground = #405060`n")
    $re = Seam $s @{ op = 'reload-config' }
    Check 's1.recreate: the new file applies on one reload' ($re.applied -and $re.nativeBackground -eq '#405060') "applied=$($re.applied) native=$($re.nativeBackground)"

    # The banner's premise ended with the file coming back: it is down.
    $gone = Wait-Notice $s 'config-file-missing' $false
    Check 's1.cleared: the banner came down with the file' ($gone.Seen -eq $false) $(
        if ($gone.Seen) { "still up after $($gone.Ms)ms" } else { "cleared after $($gone.Ms)ms" })

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
