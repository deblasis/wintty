#requires -Version 7
<#
    A WSL profile hosted in this process renders its shell (wintty#1268).

    The bundled conpty.dll opens every pseudoconsole by asking the terminal
    for its primary device attributes (DA1) and holds the child's first
    output until the answer arrives. A WSL child never came out of that on
    the local pane path: wsl.exe ran, the pane stayed blank, and one DA1
    reply typed into it released the prompt. The pty reader now answers the
    request itself (src/termio/conpty_handshake.zig).

    One scenario against a fresh app process: open a staged profile that
    runs `wsl.exe -d <distro>`, and require the pane's own screen to carry
    output within -ReadySeconds. Nothing is typed into the pane: a keystroke
    is exactly what used to unstick it, so sending one would hide the bug.

    The budget alone cannot tell an answered handshake from a timed-out one:
    the pane came up at 46 ms with the answer and at 3 s without it, and both
    are "output within 30 s". So the second assertion is the line the pty
    reader logs when it answers conpty, read out of the app's own log under
    the private state base. A pane that rendered because conpty gave up and
    released the child anyway is the old bug wearing a passing screen.

    WSL-gated: with no distro on the machine, or one that cannot run a
    command, the scenario prints a SKIP line, records it, and exits 3. That
    is the suite's skip code: neither a pass nor a harness failure, and named
    so a run over a machine with no WSL does not report coverage it has none
    of.

    Zero OS input is synthesized; the app runs with a private state tree.
    Exits 0 on pass, 3 on a skip, 2 on a product finding, 1 when the harness
    could not run and nothing is known about the product.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # The daemon renders this profile's prompt in about 3 s; a cold distro
    # start can take longer, and the bug is "never", not "slow".
    [int]$ReadySeconds = 30,
    # The line termio/Exec.zig's writeHandshakeReply logs once the reply has
    # reached conpty. Quoted here rather than only grepped for a phrase so a
    # rename on the Zig side is a scenario failure rather than a silent pass.
    [string]$HandshakeLogLine = 'conpty handshake: answered the startup DA1',
    # The file logger drains its channel on a background task, so the line
    # lands a moment after the write. Short: this is a flush, not a wait.
    [int]$LogSeconds = 15
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Write-Result([string]$Outcome, [string]$Class, [string]$Detail) {
    [ordered]@{
        actuation = 'seam (WINTTY_TEST_SEAM=<session token>); zero synthesized OS input'
        scenario = 'wsl-local'
        outcome = $Outcome
        class = $Class
        detail = $Detail
    } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8
}

# The machine's own answer, never the app's. WSL_UTF8 makes wsl.exe list in
# UTF-8 rather than UTF-16, so the names read back as written.
function Get-WslDistro {
    if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) { return $null }
    $env:WSL_UTF8 = '1'
    $out = & wsl.exe --list --quiet 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    # docker-desktop distros are infrastructure: they run no user shell.
    return @($out |
        ForEach-Object { "$_".Trim([char]0xFEFF, [char]0, ' ', "`t", "`r") } |
        Where-Object { $_ -and $_ -notlike 'docker-desktop*' }) | Select-Object -First 1
}

# A distro that cannot run one command would be read as the bug; bounded,
# because a wedged WSL must not wedge the harness.
function Test-WslDistroRuns([string]$Distro) {
    $job = Start-ThreadJob -ArgumentList $Distro -ScriptBlock {
        param($d)
        $out = & wsl.exe -d $d -- echo wintty-wsl-ok 2>$null
        [pscustomobject]@{ Code = $LASTEXITCODE; Out = "$out" }
    }
    if (-not (Wait-Job $job -Timeout 90)) {
        Stop-Job $job; Remove-Job $job -Force
        return $false
    }
    $r = Receive-Job $job; Remove-Job $job -Force
    return $r.Code -eq 0 -and $r.Out -like '*wintty-wsl-ok*'
}

# The line the app writes when the pty reader's reply has reached conpty,
# read out of the run's own log. Its shape is fixed by the file logger:
# <state base>\Wintty\logs\ghostty-<utc date>[-<roll>].log, one file per day,
# appended. Read with -LiteralPath and Get-ChildItem -File because a bracketed
# checkout would make either a pattern.
#
# Returns @{ found = $true; where = <file name> } or @{ found = $false; why = ... }.
# The caller has to be able to say which of the two it got, because "no log at
# all" is a harness problem and "a log without the line" is the bug, and a bare
# $null or $false collapses the two into the answer that matters least.
function Wait-HandshakeLogLine {
    param([string]$StateBase, [string]$Needle, [int]$Seconds)

    $logDir = Join-Path (Join-Path (Join-Path $StateBase 'Wintty') 'logs')
    $deadline = (Get-Date).AddSeconds($Seconds)
    $sawAnyLog = $false
    while ((Get-Date) -lt $deadline) {
        $logs = @(Get-ChildItem -LiteralPath $logDir -Filter '*.log' -File -ErrorAction SilentlyContinue)
        if ($logs.Count -gt 0) {
            $sawAnyLog = $true
            foreach ($f in $logs) {
                # Read while the app still holds the file open, with
                # -ErrorAction so a share violation is a missing line rather
                # than a terminating error that would read as a finding.
                $text = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction SilentlyContinue
                if ($text -and $text.Contains($Needle)) {
                    return @{ found = $true; where = $f.Name }
                }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $sawAnyLog) {
        return @{ found = $false; why = "the app wrote no log at all under $logDir" }
    }
    return @{ found = $false; why = "no line matching '$Needle' in the app's log under $logDir" }
}

$distro = Get-WslDistro
if (-not $distro) {
    $why = 'no WSL distro on this machine'
    Write-Host "SKIP wsl-local: $why (a skip is not a pass)" -ForegroundColor Yellow
    Write-Result 'skip' '' $why
    exit 3
}
if (-not (Test-WslDistroRuns $distro)) {
    $why = "WSL distro '$distro' cannot run a command"
    Write-Host "SKIP wsl-local: $why (a skip is not a pass)" -ForegroundColor Yellow
    Write-Result 'skip' '' $why
    exit 3
}

$quoted = if ($distro -match '\s') { "`"$distro`"" } else { $distro }
$config = @"
windows-single-instance = false
window-save-state = never
profile.wslprobe.name = WslProbe
profile.wslprobe.command = wsl.exe -d $quoted
"@

$s = $null
try {
    Assert-NoWinttyFrom -ExePath $ExePath -Context "The local WSL pane scenario"
    $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -PrivateStateBase
    $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'wslprobe' }
    $index = [int]$opened.state.active

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $screen = $null
    while ($clock.Elapsed.TotalSeconds -lt $ReadySeconds) {
        try { $screen = Invoke-SeamCommand $s @{ op = 'screen-text'; index = $index; leaf = -1 } }
        catch { $screen = $null } # no live surface yet
        if ($screen -and (("$($screen.text)" -replace '\s', '').Length -gt 0)) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($s.Proc.HasExited) {
        throw ("APP_EXIT: the app exited during the scenario (code {0})" -f $s.Proc.ExitCode)
    }
    $text = if ($screen) { "$($screen.text)" } else { '' }
    if (($text -replace '\s', '').Length -eq 0) {
        $cursor = if ($screen) { "$($screen.cursorRow),$($screen.cursorCol)" } else { 'no surface' }
        throw ("PRODUCT_FAIL: the local WSL pane ('{0}') stayed blank for {1}s with nothing typed into it (cursor {2}); conpty's startup DA1 was not answered (wintty#1268)" -f
            $distro, $ReadySeconds, $cursor)
    }
    $first = (($text -split "`n") | Where-Object { $_.Trim() } | Select-Object -First 1).Trim()
    $rendered = [math]::Round($clock.Elapsed.TotalSeconds, 2)

    # The pane rendered; that alone does not say the reader answered. conpty
    # holds the child's first output until it does, and releases it anyway when
    # its timeout runs out - about 3 s against 46 ms for an answer - so a pane
    # that rendered at any time inside this budget is exactly what the bug
    # looks like once the screen is watched. The reader's own log line is the
    # difference between the two.
    $logged = Wait-HandshakeLogLine -StateBase $s.StateBase -Needle $HandshakeLogLine -Seconds $LogSeconds
    if (-not $logged.found) {
        if ($logged.why -like 'the app wrote no log*') {
            throw ("HARNESS_MISS: {0}; the app's own log is where the handshake line is read from, so this scenario can say nothing about the handshake. A Debug build against a Debug libghostty writes it at the default log level" -f $logged.why)
        }
        throw ("PRODUCT_FAIL: the local WSL pane ('{0}') rendered in {1}s but the pty reader never logged answering conpty's startup DA1 - {2}. conpty released the child on its own timeout, which is the bug this scenario exists for (wintty#1268)" -f
            $distro, $rendered, $logged.why)
    }

    $detail = "distro '$distro' rendered in ${rendered}s with the handshake answered (log $($logged.where)): '$first'"
    Write-Host "PASS wsl-local: $detail" -ForegroundColor Green
    Write-Result 'pass' '' $detail
    exit 0
} catch {
    $msg = "$($_.Exception.Message)"
    $class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
    Write-Host "FAIL wsl-local [$class]: $msg" -ForegroundColor Red
    Write-Result 'fail' $class $msg
    if ($class -eq 'product') { exit 2 } else { exit 1 }
} finally {
    if ($null -ne $s) { Stop-SeamSession $s }
}
