#requires -Version 7
<#
    Close-freeze probe: close one tab while a neighbour keeps the app
    mailbox full, and measure how long the UI thread stops answering the
    seam.

    The shape under test is teardown delivery. Two tabs, real shells:

      tab 0  floods OSC title changes; every title is a set_title message
             on the app mailbox (capacity 64), so the moment the app
             thread stops draining during tab 1's teardown, the queue is
             full for the whole teardown window.
      tab 1  runs ping, a child that emits no OSC, so the only mailbox
             push its teardown makes is the child-exit notice the Windows
             wait thread delivers on the io thread's behalf.

    Closing tab 1 through the seam's close op runs the real CloseTab on
    the UI thread. Measured: the close op's own ack latency, then
    get-state latency for a few passes after it. A close that parks the
    UI thread inside surface teardown shows up as an ack delayed by the
    mailbox's full push budget (240 windows of 250 ms) while a healthy
    close answers in dispatcher time. Per-op latencies land in
    timeline.json; the verdict compares the worst latency against a
    threshold, not against an expectation of speed, so a busy machine can
    only widen healthy latencies, not manufacture a 60 s one.

    Exits 0 clean, 2 finding (worst latency at or over the threshold),
    1 could-not-run. No OS input is synthesized; the seam drives the real
    handlers in-process.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # A healthy close answers in well under a second; the defect parks the
    # UI thread for the whole push budget. The threshold sits between
    # those with an order of magnitude of room on each side.
    [int]$FreezeThresholdSeconds = 30,
    [int]$PostClosePolls = 6,
    [int]$ReadinessTimeoutSeconds = 30,
    # Five minutes: far past the defect's whole 60 s budget twice over, and
    # only there so a never-answering UI thread ends the run, not the day.
    [int]$AckHangGuardSeconds = 300
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$Config = @"
window-save-state = never
confirm-close-surface = false
command = "$pwsh" -NoLogo
"@

$script:Timeline = [System.Collections.Generic.List[object]]::new()
$script:Findings = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null

function Record([string]$Op, [long]$ElapsedMs, [string]$Note) {
    $script:Timeline.Add([pscustomobject]@{
        utc     = [datetime]::UtcNow.ToString('o')
        op      = $Op
        ms      = $ElapsedMs
        note    = $Note
    })
    Write-Host ("{0,-14} {1,8} ms  {2}" -f $Op, $ElapsedMs, $Note)
}

# One op, one timed ack. Latency is measured with a stopwatch around the
# round trip: send, then the response line for exactly this op. The read
# carries a generous hang guard so a UI thread that never comes back fails
# the harness instead of parking it -- the guard is never the check, the
# threshold below is.
function Invoke-Timed([Parameter(Mandatory)]$Session, [hashtable]$Command) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Send-SeamCommand $Session $Command
    $read = $Session.Reader.ReadLineAsync()
    if (-not $read.Wait($AckHangGuardSeconds * 1000)) {
        throw ("HARNESS: no answer to '{0}' within the {1} s hang guard" -f
            $Command['op'], $AckHangGuardSeconds)
    }
    $line = $read.Result
    if ($null -eq $line) {
        throw ("HARNESS: the seam closed the connection during '{0}'" -f $Command['op'])
    }
    $response = $line | ConvertFrom-Json
    if ($null -eq $response -or -not $response.ok) {
        throw ("PRODUCT_FAIL: {0} -> {1}" -f $Command['op'],
            $(if ($response) { $response.error } else { 'non-JSON line' }))
    }
    $sw.Stop()
    return @($response, $sw.ElapsedMilliseconds)
}

# Condition-wait helper: poll $Body until it returns a value, fail after
# $TimeoutSeconds. No sleeps-as-assertions anywhere; the timeout is a
# generous hang guard, not the check.
function Wait-Until([scriptblock]$Body, [int]$TimeoutSeconds, [string]$What) {
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        $hit = & $Body
        if ($hit) { return $hit }
        Start-Sleep -Milliseconds 200
    }
    throw "HARNESS: timed out waiting for $What"
}

# A tab seeded by seed-tabs attaches its surface at its first layout, and
# the op does not wait that out, so a driver that sends text immediately
# races it. Poll screen-text with the "no live surface" answer treated as
# pending rather than failed: it is the attaching state, not a defect.
# Any other error fails the run; a never-attaching tab fails the wait.
function Wait-TabSurfaceLive([Parameter(Mandatory)]$Session, [int]$Index) {
    Wait-Until {
        Send-SeamCommand $Session @{ op = 'screen-text'; index = $Index }
        $read = $Session.Reader.ReadLineAsync()
        if (-not $read.Wait($AckHangGuardSeconds * 1000)) {
            throw ("HARNESS: no answer to the liveness poll for tab {0} within the {1} s hang guard" -f
                $Index, $AckHangGuardSeconds)
        }
        $line = $read.Result
        if ($null -eq $line) {
            throw "HARNESS: the seam closed the connection during the liveness poll"
        }
        $r = $line | ConvertFrom-Json
        if ($r.ok) { return $r }
        if ("$($r.error)" -match 'no live surface') { return $false }
        throw ("PRODUCT_FAIL: screen-text ({0}) -> {1}" -f $Index, $r.error)
    } $ReadinessTimeoutSeconds "tab $Index to attach its surface" | Out-Null
}

try {
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config `
        -AllowInput -PrivateStateBase

    # Deterministic start: exactly two tabs, no groups, nothing pinned.
    [void](Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = 2 })

    # Both tabs attach their surfaces at their first layout; wait each
    # one live before anything is sent to it.
    Wait-TabSurfaceLive $session 0
    Wait-TabSurfaceLive $session 1

    # The quiet child first: its command echo in the grid is the
    # ready-prompt oracle for tab 1, and ping emits no OSC, so tab 1's
    # teardown contributes no streaming pushes of its own.
    Invoke-SeamCommand $session @{ op = 'send-text'; index = 1; text = "ping -n 600 127.0.0.1`r" } | Out-Null
    Wait-Until {
        $r = Invoke-SeamCommand $session @{ op = 'screen-text'; index = 1 }
        ($r.text -split "`n" -match 'ping').Count -gt 0
    } $ReadinessTimeoutSeconds 'tab 1 to echo the ping command' | Out-Null

    # The flood: chunks of distinct OSC titles in a tight write loop. A
    # chunk keeps the pty pipe saturated, so the reader always has a
    # backlog the moment the app thread stops draining, and distinct
    # titles cannot be collapsed by any change-detection on the way to
    # the mailbox. The shell-reported title is the landing proof -- a
    # set_title that made it through the app mailbox and back out to the
    # tab model. The backtick escapes are doubled here so the SHELL's
    # parser expands them (`e is ESC, `a is BEL in pwsh); a raw ESC in
    # the input stream would be PSReadLine's to interpret, not the
    # shell's.
    Invoke-SeamCommand $session @{ op = 'send-text'; index = 0; text =
        "`$c = -join (1..128 | ForEach-Object { `"``e]0;flood-`$_``a`" }); while(`$true){ [Console]::Write(`$c) }`r" } | Out-Null
    $floodTitle = Wait-Until {
        $r = Invoke-SeamCommand $session @{ op = 'tab-labels' }
        $t = $r.labels | Where-Object { $_.index -eq 0 }
        if ($t -and $t.shellTitle -and $t.shellTitle -match 'flood') { $t.shellTitle }
    } $ReadinessTimeoutSeconds 'a flood title to land on tab 0'
    Record 'flood-live' 0 "shell title now '$floodTitle'"

    # The measurement. The close ack returns after CloseTab and one
    # dispatcher pass settled; if the UI thread parks inside teardown the
    # ack is late by exactly the park.
    $closeResult, $closeMs = Invoke-Timed $session @{ op = 'close'; index = 1 }
    Record 'close' $closeMs ("tabs after close: " + @($closeResult.state.tabs).Count)

    # Recovery passes: each is a fresh UI-thread round trip after the
    # close. A park inside the close delays the first of these too (the
    # pipe serves one client in order), so both views of the park land in
    # the timeline.
    for ($i = 0; $i -lt $PostClosePolls; $i++) {
        $st, $ms = Invoke-Timed $session @{ op = 'get-state' }
        Record 'get-state' $ms ("tabCount=" + @($st.state.tabs).Count)
    }

    $worst = ($script:Timeline | Measure-Object -Property ms -Maximum).Maximum
    if ($worst -ge ($FreezeThresholdSeconds * 1000)) {
        $script:Findings.Add(("CLOSE_FREEZE: worst UI-thread ack {0} ms is at or over the {1} s threshold" -f $worst, $FreezeThresholdSeconds))
    }
    else {
        Write-Host ("CLEAN: worst UI-thread ack {0} ms, under the {1} s threshold" -f $worst, $FreezeThresholdSeconds)
    }

    # The teardown still has to be correct: two shells were started, one
    # tab remains, and the app is alive.
    if (@($st.state.tabs).Count -ne 1) {
        $script:Findings.Add(("TABS_LEFT: expected 1 tab after the close, saw {0}" -f @($st.state.tabs).Count))
    }
    if ($session.Proc.HasExited) {
        $script:Findings.Add("APP_EXITED: the app exited during the probe")
    }
}
catch {
    $harnessError = $_.Exception.Message
}
finally {
    if ($session) {
        # The app log carries the mailbox's own account of the close:
        # every give-up warns "app mailbox full, message dropped", which
        # dates the parked push to the second.
        if ($session.StateBase -and (Test-Path $session.StateBase)) {
            $logDir = Join-Path $OutDir 'logs'
            New-Item -ItemType Directory -Force -Path $logDir | Out-Null
            Get-ChildItem -Path $session.StateBase -Recurse -Filter '*.log' -File -ErrorAction SilentlyContinue |
                ForEach-Object { Copy-Item $_.FullName -Destination (Join-Path $logDir $_.Name) -Force }
            $mbLines = Get-ChildItem -Path $logDir -Filter '*.log' -File -ErrorAction SilentlyContinue |
                Get-Content -ErrorAction SilentlyContinue |
                Where-Object { $_ -match 'mailbox|proc-wait|child' }
            $mbLines | Set-Content (Join-Path $OutDir 'mailbox-lines.txt') -ErrorAction SilentlyContinue
        }
        Stop-SeamSession $session
    }
}

$script:Timeline | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'timeline.json')
if ($harnessError) {
    Write-Host "HARNESS: $harnessError"
    exit 1
}
if ($script:Findings.Count -gt 0) {
    $script:Findings | ForEach-Object { Write-Host "FINDING: $_" }
    $script:Findings | Set-Content (Join-Path $OutDir 'findings.txt')
    exit 2
}
exit 0
