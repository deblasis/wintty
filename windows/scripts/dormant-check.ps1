#requires -Version 7
<#
    Dormant surfaces (Tier C), measured against the running app.

    The dormancy machinery has no product trigger yet (the #1051 first
    PR scope): this harness drives it through the surface-dormant seam
    op and asserts the lifecycle the reviewers held it to:

      1. GO: a hidden tab freezes; the is-dormant readback flips and
         the surface-mem census reads all zeros (a dormant terminal
         must not be walked, even for a census).
      2. WAKE BY SHOW (the occlusion transition is a wake): selecting
         the tab unfreezes it -- the renderer's visible-transition
         rebuild reads a LIVE terminal, not freed pages.
      3. WAKE BY DATA: output arriving into a dormant tab rebuilds the
         terminal before it is parsed, and the tab still executes
         (the shell answers with a second title marker).
      4. QUIT DORMANT: stopping the session with a tab still dormant
         tears down cleanly (a dormant close must not double-deinit
         the terminal or leak its snapshot).

    Exits 0 clean, 2 finding, 1 could-not-run.
#>
param([Parameter(Mandatory)][string]$ExePath)
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

# Poll the dormant readback until it flips (or fail).
function Wait-Dormant($Session, [int]$Index, [bool]$Want, [int]$TimeoutMs) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        $r = Invoke-SeamCommandBounded $Session @{ op = 'surface-dormant'; index = $Index; mode = 'check' } 5000
        if ($r.dormant -eq $Want) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

$script:Findings = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null

$ownLock = $null
try {
    . C:\temp\seam-lock.ps1
    $ownLock = Enter-SeamLock -Owner 'dormant-check'

    Assert-NoWintty -Context 'the dormant check'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config -AllowInput
    if (-not (Wait-SeamReady $session.Proc)) { throw 'SEAM_REFUSED: app never announced the pipe' }

    # Same shell-agnostic seeding as the shed harness: seed-tabs spawns
    # cmd.exe, so pwsh is spawned inside the tab, and titles are seeded
    # empty so the OSC markers below are what get-state reads.
    [void](Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = 2; titles = @('', '') })
    Start-Sleep -Seconds 3
    [void](Invoke-SeamCommand $session @{ op = 'send-text'; index = 1; text = 'pwsh -NoProfile' + [char]13 })
    Start-Sleep -Seconds 3
    [void](Invoke-SeamCommand $session @{
        op = 'send-text'
        index = 1
        text = '$Host.UI.RawUI.WindowTitle = "AWAKE-1"' + [char]13
    })
    # Poll for the marker: pwsh's cold start varies by seconds on a
    # loaded machine, and a single fixed sleep made this a coin flip.
    $title = ''
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline -and $title -ne 'AWAKE-1') {
        Start-Sleep -Seconds 1
        $st = Invoke-SeamCommand $session @{ op = 'get-state' }
        $title = $st.state.tabs[1].title
    }
    if ($title -ne 'AWAKE-1') {
        $script:Findings.Add("setup: tab 1 title is '$title', expected AWAKE-1 -- the shell marker never landed; everything below would be blind")
    }

    # Hide the tab under test: dormancy refuses visible surfaces.
    [void](Invoke-SeamCommand $session @{ op = 'select'; index = 0 })
    Start-Sleep -Seconds 1

    # --- 1. GO ---------------------------------------------------------
    $go = Invoke-SeamCommandBounded $session @{ op = 'surface-dormant'; index = 1; mode = 'go' } 5000
    if (-not $go.sent) { $script:Findings.Add("go: the seam reported the request refused outright (active search?)") }
    if (-not (Wait-Dormant $session 1 $true 10000)) {
        $script:Findings.Add("go: is-dormant never flipped true within 10s -- eligibility refused or the freeze died on the IO thread")
    } else {
        Write-Host 'go: dormant'
    }

    # A dormant terminal must read as zero pages in the census: the
    # guard (not the walk) is what answers.
    $mem = Invoke-SeamCommandBounded $session @{ op = 'surface-mem'; index = 1 } 5000
    if ($mem.totalPages -ne 0 -or $mem.activitySerial -ne 0) {
        $script:Findings.Add("census: dormant surface-mem read totalPages=$($mem.totalPages) serial=$($mem.activitySerial); the dormant guard did not answer the census")
    } else {
        Write-Host 'census: dormant reads zero'
    }

    # --- 2. WAKE BY SHOW (occlusion transition) -------------------------
    [void](Invoke-SeamCommand $session @{ op = 'select'; index = 1 })
    if (-not (Wait-Dormant $session 1 $false 10000)) {
        $script:Findings.Add("show-wake: selecting the dormant tab never woke it within 10s -- the renderer's visible transition ran against freed pages or never ran")
    } else {
        Write-Host 'show-wake: awake'
    }

    # --- 3. WAKE BY DATA -------------------------------------------------
    # Hide, freeze again, then let output do the waking; the shell's
    # answer proves the parse landed in a REBUILT terminal. The settle
    # beat after hiding exists because the activation above flushes the
    # hidden-title queue, and output arriving in the same instant as the
    # freeze is a wake racing a dormancy -- the freeze wins on the IO
    # thread, but the readback below should not have to depend on that.
    [void](Invoke-SeamCommand $session @{ op = 'select'; index = 0 })
    Start-Sleep -Seconds 2
    [void](Invoke-SeamCommandBounded $session @{ op = 'surface-dormant'; index = 1; mode = 'go' } 5000
    ) | Out-Null
    Start-Sleep -Seconds 1
    if (-not (Wait-Dormant $session 1 $true 10000)) {
        $script:Findings.Add("data-wake setup: the second freeze never landed")
    } else {
        [void](Invoke-SeamCommand $session @{
            op = 'send-text'
            index = 1
            text = '$Host.UI.RawUI.WindowTitle = "WOKEN-2"' + [char]13
        })
        if (-not (Wait-Dormant $session 1 $false 15000)) {
            $script:Findings.Add("data-wake: output into the dormant tab never woke it within 15s")
        } else {
            # Hidden-tab titles queue until activation (#1035 semantics,
            # pre-dating dormancy), so the shell's answer is read the way
            # a user would: look at the tab, then read its title.
            [void](Invoke-SeamCommand $session @{ op = 'select'; index = 1 })
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            $title = ''
            while ([DateTime]::UtcNow -lt $deadline -and $title -ne 'WOKEN-2') {
                Start-Sleep -Seconds 1
                $st = Invoke-SeamCommand $session @{ op = 'get-state' }
                $title = $st.state.tabs[1].title
            }
            if ($title -ne 'WOKEN-2') {
                $script:Findings.Add("data-wake: woke, but the shell never answered (title '$title') -- the parse after wake did not land in a working terminal")
            } else {
                Write-Host 'data-wake: woke and executing'
            }
        }
    }

    # --- 4. QUIT DORMANT --------------------------------------------------
    # Leave tab 1 frozen on the way out: the session stop tears the app
    # down through the dormant Termio deinit. A clean stop (no crash
    # artifacts) is the assert; the teardown asserts live in the exit.
    # Re-hide first: dormancy refuses visible surfaces, and the data-wake
    # check above left the tab selected.
    [void](Invoke-SeamCommand $session @{ op = 'select'; index = 0 })
    Start-Sleep -Seconds 1
    [void](Invoke-SeamCommandBounded $session @{ op = 'surface-dormant'; index = 1; mode = 'go' } 5000)
    if (-not (Wait-Dormant $session 1 $true 10000)) {
        $script:Findings.Add("quit-dormant setup: the final freeze never landed")
    } else {
        Write-Host 'quit-dormant: frozen; stopping session'
    }
}
catch {
    $harnessError = $_.Exception.Message
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
Write-Host 'dormant-check: clean'
exit 0
