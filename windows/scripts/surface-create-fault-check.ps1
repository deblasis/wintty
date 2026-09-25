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
         retry brings it up. Assert: app alive, the tab ends with a surface.
      2. PERSISTENT FAULT: two more tabs never get a surface. Assert: app
         ALIVE (the old code died exactly here), panes stay surface-less,
         the retry budget runs out, and the app is still alive and serving
         seam commands after the give-up.

    Exits 0 clean, 2 finding, 1 could-not-run.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    # Skip the give-up leg (about 70s of waiting for the retry budget to
    # run out). Off by default; the verification pass runs the full form.
    [switch]$SkipGiveUp
)
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

$script:Findings = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null

function Assert-AppAlive($Session, [string]$When) {
    if ($Session.Proc.HasExited) {
        $code = $Session.Proc.ExitCode
        throw ("APP_DIED {0}: Wintty exited with code {1} (0x{2}) - the surface-creation path killed the process" -f
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
    if (-not (Wait-SeamReady $session.Proc)) { throw 'SEAM_REFUSED: app never announced the pipe' }

    # --- 1. ONE-SHOT FAULT, then RECOVERY --------------------------------
    # Arm one failure and grow to two tabs: the new tab's first creation
    # reports the null surface, the retry (500ms cadence) succeeds.
    [void](Invoke-SeamCommandBounded $session @{ op = 'surface-fault'; count = 1 } 5000)
    [void](Invoke-SeamCommandBounded $session @{ op = 'seed-tabs'; count = 2 } 15000)

    $surfaced = $false
    $sawRetryArmed = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        Assert-AppAlive $session 'during recovery poll'
        Start-Sleep -Milliseconds 150
        $st = Invoke-SeamCommandBounded $session @{ op = 'surface-state'; index = 1 } 5000
        if ($st.retriesLeft -ge 0) { $sawRetryArmed = $true }
        if ($st.hasSurface) { $surfaced = $true; break }
    }
    if (-not $surfaced) {
        $script:Findings.Add('recovery: the faulted tab never gained a surface within 20s - the retry did not land')
    } elseif (-not $sawRetryArmed) {
        Write-Host 'recovery: surfaced (retry window too short to observe, which is fine)'
    } else {
        Write-Host 'recovery: faulted once, retry armed, surface came up'
    }
    Assert-AppAlive $session 'after recovery'

    # --- 2. PERSISTENT FAULT: degrade, never die --------------------------
    # Arm a count no budget can outlive and grow two more tabs. This is the
    # exact shape of the GPU-less startup crash: a creation that cannot
    # succeed. The old code died here; the fixed app must stay up, keep the
    # panes surface-less, exhaust the retry budget, and keep serving.
    [void](Invoke-SeamCommandBounded $session @{ op = 'surface-fault'; count = 100000 } 5000)
    [void](Invoke-SeamCommandBounded $session @{ op = 'seed-tabs'; count = 3 } 20000)

    # Give the first layout pass and a few retry ticks room, then require:
    # alive, no surface, retry visibly armed.
    Start-Sleep -Seconds 3
    Assert-AppAlive $session 'with persistent fault (the defect killed the app here)'
    $sawRetryArmed = $false
    foreach ($index in 1..2) {
        $st = Invoke-SeamCommandBounded $session @{ op = 'surface-state'; index = $index } 5000
        if ($st.hasSurface) {
            $script:Findings.Add("persistent: tab $index reported a surface although every creation is faulted")
        }
        if ($st.retriesLeft -ge 0) { $sawRetryArmed = $true }
    }
    if (-not $sawRetryArmed) {
        $script:Findings.Add('persistent: no retry budget observed on either faulted pane - the retry never armed')
    } else {
        Write-Host 'persistent: panes surface-less, retry armed, app alive'
    }

    if (-not $SkipGiveUp) {
        # --- 3. THE GIVE-UP: budget runs out, pane degrades, app lives ----
        # Budget is 120 ticks at 500ms (~60s); poll to 95s and require the
        # timer to stand down (-1) and the app to still be here.
        Write-Host 'give-up: waiting out the retry budget (about 60s)...'
        $deadline = [DateTime]::UtcNow.AddSeconds(95)
        do {
            Assert-AppAlive $session 'while waiting out the retry budget'
            Start-Sleep -Seconds 2
            $st = Invoke-SeamCommandBounded $session @{ op = 'surface-state'; index = 1 } 5000
        } while ($st.retriesLeft -ge 0 -and [DateTime]::UtcNow -lt $deadline)
        if ($st.retriesLeft -ge 0) {
            $script:Findings.Add('give-up: retry budget still armed after 95s - the pane neither degraded nor died')
        }
        Assert-AppAlive $session 'after the retry budget ran out'

        # Disarm and prove the app still serves: the recovered leg's tab is
        # gone (seed closed it), so check the still-live surface-less panes
        # and one healthy readback round.
        [void](Invoke-SeamCommandBounded $session @{ op = 'surface-fault'; count = 0 } 5000)
        $st = Invoke-SeamCommandBounded $session @{ op = 'get-state' } 10000
        if (@($st.state.tabs).Count -lt 3) {
            $script:Findings.Add("give-up: expected 3 tabs to still be registered, got $(@($st.state.tabs).Count)")
        } else {
            Write-Host 'give-up: budget exhausted, panes degraded, app alive and serving'
        }
    }
}
catch {
    $harnessError = $_.Exception.Message
    if ($session -and $session.Proc -and $session.Proc.HasExited) {
        Show-CrashLog $session
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
