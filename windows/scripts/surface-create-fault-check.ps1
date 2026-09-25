#requires -Version 7
<#
    The GPU-less startup crash (surface creation returning a null surface),
    reproduced on healthy hardware through the seam and asserted against the
    graceful path.

    What the defect was: when the renderer device cannot be built (a GPU-less
    guest, or a cold-composition boot window on one), libghostty reports a
    zero surface handle from SurfaceNew. The code then either dereferenced it
    natively (an access violation at the swap-chain query) or, after the
    null-check landed, threw out of the layout handler - both killed the app
    at startup.

    This harness arms the same precondition through the surface-fault seam op
    and drives real new tabs through it:

      1. ONE-SHOT FAULT, RECOVERY: one new tab's first creation fails; the
         retry brings it up. Assert: app alive, the tab ends with a surface,
         and the retry budget was observed armed (which tab was faulted is
         not assumed: the startup race can hand the armed fault to the
         surviving first tab, so every index is polled and any armed tab
         counts).
      2. PERSISTENT FAULT: two more tabs never get a surface. Assert: app
         ALIVE (the old code died exactly here), panes stay surface-less,
         and a retry budget is observed armed. The tab whose budget is seen
         armed is pinned and followed through leg 3 - a bare retriesLeft
         readback cannot tell "never attempted" from "gave up", so the
         pinned tab must be one that demonstrably armed.
      3. THE GIVE-UP: the pinned tab's budget runs out (attempted=true,
         retriesLeft=-1) and the app is still alive and serving seam
         commands afterwards.

    All waits are condition-waits against state readbacks; wall-clock limits
    are generous outer hang guards only (the give-up guard is 3x the
    product's 60 s budget, because each DispatcherQueue tick needs the UI
    thread and a loaded desktop stretches the cadence).

    Exits 0 clean, 2 product finding (including an app death - the strongest
    finding this harness can produce), 1 could-not-run.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    # Skip the give-up leg (about 60s of waiting for the retry budget to
    # run out). Off by default; the verification pass runs the full form.
    [switch]$SkipGiveUp
)

trap {
    if ("$_" -like 'PRODUCT_FAIL*') { Write-Host "$_"; exit 2 }
    break
}

. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

$Config = @"
windows-single-instance = true
window-save-state = never
"@

function Invoke-SeamCommandBounded($Session, [hashtable]$Command, [int]$TimeoutMs) {
    Send-SeamCommand $Session $Command
    $readTask = $Session.Reader.ReadLineAsync()
    if (-not $readTask.Wait($TimeoutMs)) {
        throw ("HANG: no response to '{0}' within {1}ms" -f $Command['op'], $TimeoutMs)
    }
    $line = $readTask.Result
    if ($null -eq $line) { throw ("closed without response to '{0}'" -f $Command['op']) }
    $response = $line | ConvertFrom-Json
    if (-not $response.ok) { throw ("{0} -> {1}" -f $Command['op'], $response.error) }
    return $response
}

# Arm the surface-fault op and require the app to confirm the armed count
# it actually took: an armed=0 echoed back would silently run every later
# leg against healthy first-try creations.
function Invoke-ArmSurfaceFault($Session, [int]$Count) {
    $response = Invoke-SeamCommandBounded $Session @{ op = 'surface-fault'; count = $Count } 5000
    if ($response.armed -ne $Count) {
        throw ("surface-fault asked for {0} but the app armed {1}" -f $Count, $response.armed)
    }
    return $response
}

$script:Findings = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null

function Assert-AppAlive($Session, [string]$When) {
    if ($Session.Proc.HasExited) {
        $code = $Session.Proc.ExitCode
        throw ("PRODUCT_FAIL: APP_DIED {0}: Wintty exited with code {1} (0x{2}) - the surface-creation path killed the process" -f
            $When, $code, ('{0:x}' -f $code))
    }
}

function Show-CrashLog($Session) {
    if (-not $Session.StateBase) { return }
    $crashLog = Get-ChildItem -Path $Session.StateBase -Recurse -Filter 'crash.log' `
        -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($crashLog) {
        Write-Host "--- crash.log ($($crashLog.FullName)), last 40 lines:"
        Get-Content $crashLog.FullName -Tail 40 | ForEach-Object { Write-Host "    $_" }
    } else {
        Write-Host '--- no crash.log found under the session state base'
    }
}

# Poll surface-state for the pane the retry budget was observed armed on.
# Every index is scanned: which tab consumed the armed fault is a startup
# race (the surviving first tab may still be pre-creation when the fault is
# armed), and a fixed index would sometimes watch a pane that never tried.
function Find-ArmedPane($Session, [int]$TabCount, [int]$DeadlineSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($DeadlineSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        Assert-AppAlive $Session 'while looking for the armed retry budget'
        foreach ($index in 0..($TabCount - 1)) {
            $st = Invoke-SeamCommandBounded $Session @{ op = 'surface-state'; index = $index } 5000
            if ($st.retriesLeft -ge 0) { return $index }
        }
        Start-Sleep -Milliseconds 250
    }
    return -1
}

$ownLock = $null
try {
    # The machine-level seam lock, where the local helper exists: one seam
    # harness at a time shares the desktop with the rest of the machine.
    if (Test-Path 'C:\temp\seam-lock.ps1') {
        . C:\temp\seam-lock.ps1
        $ownLock = Enter-SeamLock -Owner 'surface-create-fault-check'
    }

    Assert-NoWintty -Context 'the surface-create fault check'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config -PrivateStateBase

    # --- 1. ONE-SHOT FAULT, then RECOVERY --------------------------------
    # Arm one failure and grow to two tabs: the new tab's first creation
    # reports the null surface, the retry (500ms cadence) succeeds.
    [void](Invoke-ArmSurfaceFault $session 1)
    [void](Invoke-SeamCommandBounded $session @{ op = 'seed-tabs'; count = 2 } 15000)

    $survivorSurfaced = $false
    $surfaced = $false
    $sawRetryArmed = $false
    $tickDriven = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        Assert-AppAlive $session 'during recovery poll'
        Start-Sleep -Milliseconds 150
        foreach ($index in 0..1) {
            $st = Invoke-SeamCommandBounded $session @{ op = 'surface-state'; index = $index } 5000
            if ($st.retriesLeft -ge 0) { $sawRetryArmed = $true }
            if ($st.hasSurface) {
                # Index 0 is the surviving first tab: its creation predates
                # this harness, so its surface is asserted too - in the rare
                # ordering where it is the pane that needs the retry, its
                # return to hasSurface is still checked here.
                if ($index -eq 0) { $survivorSurfaced = $true }
                if ($index -eq 1) {
                    $surfaced = $true
                    # First sight of the faulted pane's surface. The polls
                    # are observers (they force no layout pass) and the
                    # layout driver is latched out once the faulted first
                    # attempt has run, so a surface already present with a
                    # spent budget (attempted, retriesLeft -1) can only have
                    # been recovered by the retry tick between two polls:
                    # the first poll was simply late. Accept that pair in
                    # place of a sighting of the armed budget; a LATER
                    # surfacing still requires the budget seen armed.
                    if (-not $tickDriven) {
                        $tickDriven = [bool]$st.attempted -and $st.retriesLeft -lt 0
                    }
                }
            }
        }
        if ($surfaced -and $survivorSurfaced) { break }
    }
    if (-not $surfaced -or -not $survivorSurfaced) {
        $script:Findings.Add('recovery: the faulted tab never gained a surface within 30s - the retry did not land')
    } elseif (-not $sawRetryArmed -and -not $tickDriven) {
        $script:Findings.Add('recovery: the tab surfaced but no retry budget was ever observed armed - the retry path did not drive this recovery')
    } else {
        Write-Host 'recovery: faulted once, retry armed, surface came up'
    }
    Assert-AppAlive $session 'after recovery'

    # --- 2. PERSISTENT FAULT: degrade, never die --------------------------
    # Arm a count no budget can outlive and grow two more tabs. This is the
    # exact shape of the GPU-less startup crash: a creation that cannot
    # succeed. The old code died here; the fixed app must stay up, keep the
    # panes surface-less, and arm budgets the give-up leg can follow.
    [void](Invoke-ArmSurfaceFault $session 100000)
    [void](Invoke-SeamCommandBounded $session @{ op = 'seed-tabs'; count = 3 } 20000)

    $armedIndex = Find-ArmedPane $session 3 30
    Assert-AppAlive $session 'with persistent fault (the defect killed the app here)'
    if ($armedIndex -lt 0) {
        $script:Findings.Add('persistent: no retry budget observed on any faulted pane within 30s - the retry never armed')
    } else {
        Write-Host "persistent: retry armed on tab $armedIndex"
    }
    # Every pane created under the fault stays surface-less. The faulted
    # tabs are the NewTab growth (indices 1..2); the survivor's healthy
    # surface predates the arming and is not polled here.
    foreach ($index in 1..2) {
        $st = Invoke-SeamCommandBounded $session @{ op = 'surface-state'; index = $index } 5000
        if ($st.hasSurface) {
            $script:Findings.Add("persistent: tab $index reported a surface although every creation is faulted")
        }
    }

    if (-not $SkipGiveUp -and $armedIndex -ge 0) {
        # --- 3. THE GIVE-UP: budget runs out, pane degrades, app lives ----
        # Budget is 120 attempts at 500ms (~60s); the guard is 180s (3x) so
        # a loaded desktop cannot turn slow tick delivery into a finding.
        # The pinned tab must END attempted with no budget: that pair is
        # the give-up, and it is the only way to tell it from a pane that
        # never attempted at all.
        Write-Host "give-up: waiting out the retry budget on tab $armedIndex (about 60s)..."
        $givenUp = $false
        $deadline = [DateTime]::UtcNow.AddSeconds(180)
        while ([DateTime]::UtcNow -lt $deadline) {
            Assert-AppAlive $session 'while waiting out the retry budget'
            Start-Sleep -Seconds 2
            $st = Invoke-SeamCommandBounded $session @{ op = 'surface-state'; index = $armedIndex } 5000
            if ($st.retriesLeft -lt 0) { $givenUp = $true; break }
        }
        if (-not $givenUp) {
            $script:Findings.Add("give-up: retry budget on tab $armedIndex still armed after 180s - the pane neither degraded nor died")
        }
        $st = Invoke-SeamCommandBounded $session @{ op = 'surface-state'; index = $armedIndex } 5000
        if (-not $st.attempted) {
            $script:Findings.Add("give-up: tab $armedIndex reads as never-attempted, not given-up - the followed pane was the wrong one")
        }
        Assert-AppAlive $session 'after the retry budget ran out'

        # Disarm and prove the app still serves: check all tabs are still
        # registered and one healthy readback round completes.
        [void](Invoke-ArmSurfaceFault $session 0)
        $st = Invoke-SeamCommandBounded $session @{ op = 'get-state' } 10000
        if (@($st.state.tabs).Count -lt 3) {
            $script:Findings.Add("give-up: expected 3 tabs to still be registered, got $(@($st.state.tabs).Count)")
        } else {
            Write-Host 'give-up: budget exhausted, pane degraded, app alive and serving'
        }
    }
}
catch {
    $harnessError = $_.Exception.Message
    if ($session -and $session.Proc -and $session.Proc.HasExited) {
        # Death is a product finding, not a could-not-run: show the oracle,
        # then let the trap classify the run as exit 2.
        Show-CrashLog $session
        throw "PRODUCT_FAIL: the app under test died - $harnessError"
    }
}
finally {
    if ($session) { Stop-SeamSession $session }
    if ($ownLock) { Exit-SeamLock $ownLock }
}

if ($harnessError) {
    Write-Host "HARNESS ERROR: $harnessError"
    exit 1
}
if ($script:Findings.Count -gt 0) {
    Write-Host 'FINDINGS:'
    $script:Findings | ForEach-Object { Write-Host "  - $_" }
    exit 2
}
Write-Host 'surface-create-fault-check: clean'
exit 0
