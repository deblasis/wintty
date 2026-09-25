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
    get-state latency for a few passes after it. The seam serves one
    command at a time, so the ack is the earliest signal there is; it
    returns after CloseTab's synchronous teardown plus one Low-priority
    settle, and the flood is bounded and long over by the time a healthy
    close settles, so a healthy ack is dispatcher time. A close that
    parks the UI thread inside surface teardown shows up as an ack
    delayed by the mailbox's full push budget (240 windows of 250 ms)
    instead. Per-op latencies land in timeline.json; the verdict compares
    the worst latency against a threshold, not against an expectation of
    speed, so a busy machine can only widen healthy latencies, not
    manufacture a 60 s one.

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

# Best-effort evidence when the UI thread never answers: a mini dump of
# the hung app (comsvcs; its SYSTEM-only ACL is a known limit, the file is
# kept anyway) and cdb's all-thread stacks attached live. Observation only
# -- this changes nothing about the run's sequencing, it just refuses to
# let a hang go unexplained. Every step is guarded; a tooling failure must
# not mask the finding that triggered it.
function Capture-HangStacks([Parameter(Mandatory)]$Session) {
    try {
        $pid0 = $Session.Proc.Id
        $dump = Join-Path $OutDir ("hang-{0}.dmp" -f $pid0)
        Start-Process -FilePath rundll32.exe `
            -ArgumentList "comsvcs.dll,MiniDump", $pid0, $dump, "mini" `
            -Wait -NoNewWindow | Out-Null
        $cdb = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe'
        if (-not (Test-Path $cdb)) { return }
        $stacks = Join-Path $OutDir 'hang-stacks.txt'
        # Attach, print every thread's stack, then detach: a park and a
        # starved dispatcher look identical from the pipe, and the stack is
        # the only thing that tells them apart. A local-only symbol path so
        # the debugger never waits on a symbol server.
        $env:_NT_SYMBOL_PATH = $OutDir
        & $cdb -p $pid0 -g -G -y $OutDir -c "~*k; qd" *> $stacks
        Record 'hang-dump' 0 ("thread stacks captured from pid {0}" -f $pid0)
    }
    catch {
        Record 'hang-dump' 0 ("stack capture failed: {0}" -f $_.Exception.Message)
    }
}

# One guarded read of one response line. The guard is never the check; it
# exists so a UI thread that never comes back fails the harness instead of
# parking it. When it fires, the stacks are captured first, the finding is
# recorded so the run exits 2 (finding) rather than 1 (could-not-run).
function Read-SeamGuarded([Parameter(Mandatory)]$Session, [string]$OpName) {
    $read = $Session.Reader.ReadLineAsync()
    if (-not $read.Wait($AckHangGuardSeconds * 1000)) {
        Capture-HangStacks $Session
        $script:Findings.Add(("UI_THREAD_HANG: no answer to '{0}' within the {1} s guard; the UI thread never came back after the close" -f
            $OpName, $AckHangGuardSeconds))
        Record $OpName ($AckHangGuardSeconds * 1000) "HANG: no ack within the guard"
        throw ("HARNESS: no answer to '{0}' within the {1} s hang guard" -f
            $OpName, $AckHangGuardSeconds)
    }
    $line = $read.Result
    if ($null -eq $line) {
        throw ("HARNESS: the seam closed the connection during '{0}'" -f $OpName)
    }
    $response = $line | ConvertFrom-Json
    if ($null -eq $response -or -not $response.ok) {
        throw ("PRODUCT_FAIL: {0} -> {1}" -f $OpName,
            $(if ($response) { $response.error } else { 'non-JSON line' }))
    }
    return $response
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

    # The flood: chunks of distinct OSC titles, bounded and paced. The
    # bounds matter more than the volume: an unbounded pipe-saturating
    # loop was tried first and it collapses the app on its own (XAML
    # churn ballooned RSS past 10 GB, every thread crawled, the close
    # took 104 s on the FIXED build -- a resource story, not the park
    # under test). What the park needs is far cheaper: 64 mailbox slots
    # filled within a few ms of the app thread stopping its drain, which
    # a paced 8k-titles/s delivers, and the chunks keep coming through
    # the whole teardown window. Distinct titles defeat any
    # change-detection on the way to the mailbox; the shell-reported
    # title is the landing proof. The backtick escapes are doubled here
    # so the SHELL's parser expands them (`e is ESC, `a is BEL in pwsh);
    # a raw ESC in the input stream would be PSReadLine's to interpret,
    # not the shell's.
    Invoke-SeamCommand $session @{ op = 'send-text'; index = 0; text =
        "`$c = -join (1..128 | ForEach-Object { `"``e]0;flood-`$_``a`" }); 1..300 | ForEach-Object { [Console]::Write(`$c); Start-Sleep -Milliseconds 15 }`r" } | Out-Null
    $floodTitle = Wait-Until {
        $r = Invoke-SeamCommand $session @{ op = 'tab-labels' }
        $t = $r.labels | Where-Object { $_.index -eq 0 }
        if ($t -and $t.shellTitle -and $t.shellTitle -match 'flood') { $t.shellTitle }
    } $ReadinessTimeoutSeconds 'a flood title to land on tab 0'
    Record 'flood-live' 0 "shell title now '$floodTitle'"

    # The measurement. The seam serves one command at a time -- the
    # connection loop awaits each response before reading the next line
    # (TestSeam.ServeConnectionAsync) -- so nothing can be measured
    # alongside an in-flight close; the close's own ack, arriving after
    # CloseTab's synchronous teardown plus its Low-priority settle, IS
    # the oracle. The flood is bounded and long over by the time a
    # healthy close settles, so Low priority drains and a healthy ack is
    # dispatcher-time; a teardown that parks the UI thread inside the
    # joins shows up as an ack late by the park.
    $closeSw = [System.Diagnostics.Stopwatch]::StartNew()
    Send-SeamCommand $session @{ op = 'close'; index = 1 }
    $closeResult = Read-SeamGuarded $session 'close'
    $closeSw.Stop()
    Record 'close' $closeSw.ElapsedMilliseconds ("tabs after close: " + @($closeResult.state.tabs).Count)
    $st = $closeResult

    # Recovery passes: each is a fresh UI-thread round trip after the
    # close, on a connection whose queue is empty again.
    for ($i = 0; $i -lt $PostClosePolls; $i++) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        Send-SeamCommand $session @{ op = 'get-state' }
        $st = Read-SeamGuarded $session 'get-state'
        $sw.Stop()
        Record 'get-state' $sw.ElapsedMilliseconds ("tabCount=" + @($st.state.tabs).Count)
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
if ($script:Findings.Count -gt 0) {
    $script:Findings | ForEach-Object { Write-Host "FINDING: $_" }
    $script:Findings | Set-Content (Join-Path $OutDir 'findings.txt')
    exit 2
}
if ($harnessError) {
    Write-Host "HARNESS: $harnessError"
    exit 1
}
exit 0
