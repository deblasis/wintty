#requires -Version 7
<#
    Restart-on-exit, seam-actuated (deblasis/wintty#1462).

    A profile whose command is not a shell (the issue's `ssh devbox`) exits
    and the pane stays open on libghostty's exit notice, because the host
    forces `wait-after-command` for every host-provided command. Today any
    key closes that pane; the only way back is [+] and the named profile,
    which loses the scrollback.

    The wanted contract:

      - the notice names the restart: "Press Enter to restart, any other
        key to close";
      - Enter starts the SAME command in the SAME surface, so the notice
        and everything above it stay in the scrollback;
      - any other encoded key closes the pane, as it does today.

    Actuation is the in-process test seam: `open-profile` runs the real
    new-tab funnel, `screen-text` reads the viewport libghostty renders,
    `screen-scroll` scrolls through the real binding-action path, and
    `terminal-key` forwards one KeyDown through SendKeyCore -- no OS input is
    synthesized and nothing holds the foreground. The staged command numbers
    each run, so the restarted run's marker is distinct without a
    scrollback-total read.

    Each scenario gets a FRESH app process. Exits 0 on pass, 2 on a product
    finding, 1 when the harness could not run (nothing is known either way).
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$VK_ENTER = 0x0D
$VK_ESCAPE = 0x1B

# Staged executables. `repro.exe` is a copy of cmd, so the deleted-exe
# scenario can remove the command the profile resolved at open time; the alt
# scenario is a raw-bytes file copied by `cmd /c type` because the config
# parser eats backslash escapes and the console hosts consume a written ESC.
$ReproExe = Join-Path $OutDir 'repro.exe'
$ReproAltTxt = Join-Path $OutDir 'repro-alt.txt'
$ReproMain = Join-Path $OutDir 'repro-main.cmd'
$ReproLive = Join-Path $OutDir 'repro-live.cmd'
$ReproCount = Join-Path $OutDir 'repro-count.txt'
Remove-Item -Force -LiteralPath $ReproCount -ErrorAction SilentlyContinue
# The config's string parser eats backslash escapes, and a cmd/PowerShell
# console host consumes a raw ESC written by the child. A staged file copied
# by `cmd /c type` hands the ConPTY the exact bytes, ESC included.
$altBytes = [System.Text.Encoding]::UTF8.GetBytes(
    "PRIMARY-ONLY`n" + [char]27 + "[?1049h" + "ALT-ONLY`n")
[System.IO.File]::WriteAllBytes($ReproAltTxt, $altBytes)
# The first line and the marker are numbered per run: after the restart a
# wiped buffer shows RUN-2 at row 0 / mid-scrollback, not RUN-1, so the
# history assertions discriminate. The 59 filler lines push the first line
# out of a <=61-row bottom viewport.
$mainText = @'
@echo off
setlocal EnableDelayedExpansion
set "N=0"
if exist "__REPRO_COUNT__" set /p N=<"__REPRO_COUNT__"
set /a N+=1
>"__REPRO_COUNT__" echo !N!
echo PRE-EXIT-ABOVE-RUN-!N!
for /l %%i in (1,1,59) do @echo FILLER-%%i
echo RESTART-MARKER-RUN-!N!
'@
$mainText = $mainText.Replace('__REPRO_COUNT__', $ReproCount)
[System.IO.File]::WriteAllText(
    $ReproMain, $mainText, [System.Text.UTF8Encoding]::new($false))
# Reads one line, echoes GOT-<line>, and exits: the first run's exit shows
# the notice, the restarted run waits for the second nonce, and that Enter
# proves the key reached a live child (a stale offer would restart instead).
$liveText = @'
@echo off
setlocal EnableDelayedExpansion
echo LIVE-MARKER
set /p "L="
echo GOT-!L!
'@
[System.IO.File]::WriteAllText(
    $ReproLive, $liveText, [System.Text.UTF8Encoding]::new($false))

# `direct:` is the argv form: the pane runs cmd.exe itself, with no shell
# wrapper to re-parse.
#
# `wait-after-command` holds the pane open, and `abnormal-command-exit-runtime
# = 0` keeps every exit on the normal path: these profiles exit in well under
# the 250 ms default, which would otherwise show the abnormal-exit notice and
# never offer anything. `scrollback-limit-lines` is non-zero so the page list
# cannot collapse the 60-line pre-exit output into the active page (the
# scrollback-growth proof needs real history). `window-height`/`window-width`
# pin the viewport so the 60 pre-exit lines scroll above it and the notice
# still fits (the probe asserts 12 <= rows < 62 as a guard).
#
# `repro` prints 60 lines above the viewport and one visible marker, so the
# row-0 read and the bottom read are distinct. `repro-live` reads a line and
# echoes it, which is the non-vacuous second-Enter check. `repro-env` shows
# the config `env` value the reload revokes. `repro-del` runs the staged
# copy that the failed-restart scenario deletes.
$Config = @"
windows-single-instance = false
window-save-state = never
mux-attach = false
command = C:\Windows\System32\cmd.exe
wait-after-command = true
abnormal-command-exit-runtime = 0
scrollback-limit-lines = 5000
window-width = 120
window-height = 40
env = PATH=C:\Windows\System32;C:\Windows;C:\Windows\System32\Wbem
env = SECRET=1
profile.repro.name = Repro
profile.repro.command = direct:"$ReproMain"
profile.repro.retry-on-exit = true
profile.repro-live.name = Repro Live
profile.repro-live.command = direct:"$ReproLive"
profile.repro-live.retry-on-exit = true
profile.repro-env.name = Repro Env
profile.repro-env.command = direct:C:\Windows\System32\cmd.exe /c "if defined SECRET (echo SECRET-SET) else (echo SECRET-UNSET)"
profile.repro-env.retry-on-exit = true
profile.repro-alt.name = Repro Alt
profile.repro-alt.command = direct:C:\Windows\System32\cmd.exe /c type "$ReproAltTxt"
profile.repro-alt.retry-on-exit = true
profile.repro-del.name = Repro Deleted
profile.repro-del.command = direct:"$ReproExe" /c "echo DELETED-EXE-MARKER"
profile.repro-del.retry-on-exit = true
"@

# The same staged config with repro's opt-in removed: the disabled
# contract (today's notice, any key closes).
$ConfigNoRetry = $Config.Replace("profile.repro.retry-on-exit = true`n", "")

# The per-scenario key override for the chord row.
$ConfigCtrlR = $Config + "`nretry-on-exit-key = ctrl+r`n"

# The -e row needs a flagged default profile; the other rows must not get one
# (a launch tab running repro would exit and shift their tab counts).
$ConfigEdge = $Config + "`ndefault-profile = repro`n"

$script:Scenarios = [System.Collections.Generic.List[object]]::new()

# The exact sentence the offer owns. Pinned here, where the product decision
# lives, so a wording drift reds instead of matching "some notice".
$RestartSentence = 'Process exited. Press Enter to restart, any other key to close.'
$CtrlRSentence = 'Process exited. Press Ctrl+R to restart, any other key to close.'
$CloseSentence = 'Process exited. Press any key to close the terminal.'

function Get-Screen($s, [Parameter(Mandatory)][int]$Index) {
    # The polling read is deliberately not Invoke-SeamCommand: its per-answer
    # OK line would flood the log at 10 Hz. The protocol is the same.
    Send-SeamCommand $s @{ op = 'screen-text'; index = $Index }
    return (Receive-SeamResponse $s 'screen-text').text
}

function Get-ScreenScroll($s, [Parameter(Mandatory)][int]$Index, [int]$Row = -1) {
    # The surface can be between a swap and a live handle for a beat; a
    # read refusal there is a harness timing condition, not a product
    # verdict. Poll through it the way Wait-ScreenContains does.
    $deadline = [datetime]::UtcNow.AddSeconds(5)
    $last = ''
    while ([datetime]::UtcNow -lt $deadline) {
        try {
            Send-SeamCommand $s @{ op = 'screen-scroll'; index = $Index; row = $Row }
            return (Receive-SeamResponse $s 'screen-scroll')
        } catch {
            $last = "$($_.Exception.Message)"
            if ($s.Proc.HasExited -or
                $last -like 'PRODUCT_EXIT*' -or
                $last -like 'APP_EXIT*') {
                throw ("APP_EXIT: the app exited while reading the viewport (index {0}, row {1}): {2}" -f
                    $Index, $Row, $last)
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw ("HARNESS: screen-scroll never became readable (index {0}, row {1}): {2}" -f
        $Index, $Row, $last)
}

function Wait-ScreenContains(
    $s,
    [Parameter(Mandatory)][string]$Needle,
    [Parameter(Mandatory)][int]$Index,
    [int]$TimeoutSeconds = 20
) {
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = ''
    $lastError = ''
    $readable = $false
    $rows = 0
    $cols = 0
    while ([datetime]::UtcNow -lt $deadline) {
        try {
            Send-SeamCommand $s @{ op = 'screen-text'; index = $Index }
            $r = Receive-SeamResponse $s 'screen-text'
            $last = $r.text
            $rows = $r.rows
            $cols = $r.cols
            $readable = $true
        } catch {
            # A pane's surface appears a beat after its tab does; that is a
            # state to poll through, not a reason to give up on the first
            # answer. Keep the message so a real refusal is legible. An app
            # exit is not a transient surface state: classify it as product.
            $lastError = "$($_.Exception.Message)"
            if ($s.Proc.HasExited -or
                $lastError -like 'PRODUCT_EXIT*' -or
                $lastError -like 'APP_EXIT*') {
                throw ("APP_EXIT: the app exited while waiting for '{0}': {1}" -f
                    $Needle, $lastError)
            }
        }
        if ($last.Contains($Needle)) { return $last }
        Start-Sleep -Milliseconds 100
    }
    if ($readable) {
        # A readable screen that never showed the needle is a product
        # finding; only an unreadable one is a harness problem.
        throw ("PRODUCT_FAIL: the pane never showed '{0}' (rows={1} cols={2}). Last screen:`n{3}" -f
            $Needle, $rows, $cols, $last)
    }
    throw ("HARNESS: the pane never became readable while waiting for '{0}'. Last error: {1}" -f
        $Needle, $lastError)
}

function Count-Line([string]$Text, [Parameter(Mandatory)][string]$Needle) {
    return @($Text -split "`n" | Where-Object { $_.Contains($Needle) }).Count
}

function Assert-FirstLine(
    [string]$Text,
    [Parameter(Mandatory)][string]$Needle,
    [Parameter(Mandatory)][string]$Which
) {
    $first = @($Text -split "`n" | Select-Object -First 1)
    if ($first.Count -eq 0 -or -not $first[0].Trim().StartsWith($Needle)) {
        throw ("PRODUCT_FAIL: {0} does not start with '{1}'. The first row reads: '{2}'" -f
            $Which, $Needle, ($first | Select-Object -First 1))
    }
}

function Assert-Profile($s, [Parameter(Mandatory)][string]$Id) {
    $p = Invoke-SeamCommand $s @{ op = 'profiles' }
    $profile = @($p.profiles | Where-Object { $_.id -eq $Id }) | Select-Object -First 1
    if ($null -eq $profile) {
        throw ("HARNESS: the staged config produced no '{0}' profile. Registry: {1}" -f
            $Id, (($p.profiles | ForEach-Object { "$($_.id)=$($_.command)" }) -join '; '))
    }
    Write-Host ("{0} profile command: '{1}'" -f $Id, $profile.command)
}

function Get-State($s) {
    return (Invoke-SeamCommand $s @{ op = 'get-state' }).state
}

function Assert-PaneAlive($s, [int]$Index, [string]$Which) {
    # The design's RED rule: read `get-state` FIRST. If the Enter closed the
    # pane, the tab is gone and that is a product failure, not a harness
    # timeout in the screen read that comes next.
    $state = Get-State $s
    if ($state.tabs.Count -le $Index) {
        throw ("PRODUCT_FAIL: the pane closed on Enter ({0}); {1} tab(s) remain" -f
            $Which, $state.tabs.Count)
    }
}

function Assert-ViewportBounds($s, [int]$Index) {
    $r = Get-ScreenScroll $s $Index
    if ($r.rows -lt 13 -or $r.rows -ge 62) {
        throw (("HARNESS: the pane viewport is {0} rows; the probe needs 13 <= rows < 62 " +
                "so the notice and the visible marker fit while the 60-line pre-exit " +
                "output stays above the viewport") -f $r.rows)
    }
    Write-Host ("viewport: {0} rows x {1} cols" -f $r.rows, $r.cols)
}

function Press-Enter($s, [int]$Index) {
    # Every Enter carries char="\r": the KeyDown alone buffers a key, and a
    # WM_CHAR-less Enter never reaches the encoded path the pane's close
    # contract lives on.
    [void](Invoke-SeamCommand $s @{
        op = 'terminal-key'; tab = $Index; vk = $VK_ENTER; action = 'press'; char = "`r"
    })
}

function Press-CtrlR($s, [int]$Index) {
    # The chord the custom-key row configures: KeyDown R with Ctrl, then the
    # WM_CHAR half a real Ctrl+R delivers (0x12, not 'r'). The KeyDown alone
    # carries no text; a printable 'r' after it is a separate text event the
    # dead pane reads as a plain r, which closes it.
    [void](Invoke-SeamCommand $s @{
        op = 'terminal-key'; tab = $Index; vk = 0x52; ctrl = $true; action = 'press'; char = ([string][char]0x12)
    })
}

function Press-Escape($s, [int]$Index) {
    [void](Invoke-SeamCommand $s @{
        op = 'terminal-key'; tab = $Index; vk = $VK_ESCAPE; action = 'press'
    })
}

function Wait-TabCountBelow($s, [int]$Below, [string]$Which, [int]$TimeoutSeconds = 20) {
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        $state = Get-State $s
        if ($state.tabs.Count -lt $Below) { return }
        Start-Sleep -Milliseconds 100
    }
    throw ("PRODUCT_FAIL: {0} left {1} tab(s); the exit pane did not close" -f
        $Which, (Get-State $s).tabs.Count)
}

function Invoke-Scenario([string]$Name, [scriptblock]$Body, [string]$ConfigText = $Config, [string[]]$Arguments = @(), [switch]$ExpectAppExit) {
    $crashMark = Get-SeamSessionMark
    $s = $null
    $entry = [ordered]@{ name = $Name; ok = $false; class = ''; error = '' }
    Write-Host "=== scenario $Name ==="
    try {
        Assert-NoWinttyFrom -ExePath $ExePath -Context "The restart-on-exit scenario '$Name'"
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $ConfigText -Arguments $Arguments -AllowInput
        & $Body $s
        if ($s.Proc.HasExited -and -not $ExpectAppExit) {
            throw ("APP_EXIT: the app exited during '{0}' (code {1})" -f $Name, $s.Proc.ExitCode)
        }
        $entry.ok = $true
        Write-Host "PASS $Name" -ForegroundColor Green
    } catch {
        $msg = "$($_.Exception.Message)"
        $entry.error = $msg
        # Only a product finding counts as red, and an app that exited or
        # crashed while driving a scenario IS the product failing. A seam
        # read refusal is a harness timing condition (the read wrappers
        # already poll through the transient ones).
        $entry.class = if ($msg -like 'PRODUCT_FAIL*' -or
                           $msg -like 'PRODUCT_EXIT*' -or
                           $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
        Write-Host "FAIL $Name [$($entry.class)]: $msg" -ForegroundColor Red
    } finally {
        if ($null -ne $s) {
            # Keep the app's own logs for a failing scenario: the session's
            # state base is deleted by Stop-SeamSession.
            if (-not $entry.ok -and $s.StateBase -and (Test-Path -LiteralPath $s.StateBase)) {
                try {
                    Copy-Item -Recurse -Force -LiteralPath $s.StateBase `
                        -Destination (Join-Path $OutDir ("state-" + $Name))
                } catch {
                    Write-Host ("HARNESS: could not copy the state base: {0}" -f $_.Exception.Message)
                }
            }
            Stop-SeamSession $s
        }
    }
    if ((Test-SeamCrashLogWritten -Since $crashMark)) {
        $entry.ok = $false
        $entry.class = 'product'
        $entry.error = ($entry.error + ' crash.log grew during the scenario').Trim()
        Write-Host "FAIL $Name [product]: crash.log grew" -ForegroundColor Red
    }
    $script:Scenarios.Add($entry)
}

# The exit notice the issue quotes, and the one this fix owns. Kept as the
# exact sentence so a wording drift reds here, where the product decision
# lives, instead of in a screenshot nobody diffs.
function Assert-RestartNotice([string]$screen, [string]$which) {
    if (-not $screen.Contains($RestartSentence)) {
        $line = ($screen -split "`n" | Where-Object { $_ -like '*Process exited*' } | Select-Object -First 1)
        throw ("PRODUCT_FAIL: {0} does not offer the Enter restart. The notice reads: '{1}'" -f
            $which, "$line".Trim())
    }
}

function Assert-ProfileRetry($s, [string]$Id, [bool]$Expected) {
    $p = Invoke-SeamCommand $s @{ op = 'profiles' }
    $profile = @($p.profiles | Where-Object { $_.id -eq $Id }) | Select-Object -First 1
    if ($null -eq $profile) {
        throw ("HARNESS: no '{0}' profile in the registry" -f $Id)
    }
    if ([bool]$profile.retriesOnExit -ne $Expected) {
        throw ("PRODUCT_FAIL: profile {0} resolved retriesOnExit={1}, expected {2}" -f
            $Id, $profile.retriesOnExit, $Expected)
    }
}

function Assert-Notice([string]$screen, [string]$Sentence, [string]$which) {
    if ($screen.Contains($Sentence)) { return }
    $line = ($screen -split "`n" | Where-Object { $_ -like '*Process exited*' } | Select-Object -First 1)
    throw ("PRODUCT_FAIL: {0} shows the wrong notice. Expected '{1}'; reads: '{2}'" -f
        $which, $Sentence, "$line".Trim())
}

Invoke-Scenario 'enter-restarts-the-same-command' {
    param($s)
    Assert-Profile $s 'repro'
    Assert-ProfileRetry $s 'repro' $true
    $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'repro' }
    $index = [int]$opened.state.active
    Write-Host ("opened: active={0} tabs={1} title={2}" -f $index, $opened.state.tabs.Count,
        $opened.state.tabs[$index].title)

    $screen = Wait-ScreenContains $s 'Process exited.' -Index $index
    Assert-RestartNotice $screen 'the first exit'
    Assert-ViewportBounds $s $index

    # The current (bottom-following) viewport is where the exit notice was
    # just read; the first run's numbered marker sits above it. The number
    # is what makes the restarted run distinguishable without a
    # scrollback-total read.
    $bottom = Wait-ScreenContains $s 'RESTART-MARKER-RUN-1' -Index $index

    Press-Enter $s $index
    # get-state first: a closed tab is a product failure, not a harness
    # timeout in the read that follows.
    Assert-PaneAlive $s $index 'the restart'

    # Wait for the second run to finish: its distinct numbered marker
    # reached the (auto-following) bottom and its notice is the one in the
    # viewport. A do-nothing restart would still be showing RUN-1 and would
    # never satisfy this wait.
    $deadline = [datetime]::UtcNow.AddSeconds(20)
    $after = $null
    while ([datetime]::UtcNow -lt $deadline) {
        $r = Get-ScreenScroll $s $index
        if ($r.text.Contains('RESTART-MARKER-RUN-2') -and
            $r.text.Contains('Process exited.')) {
            $after = $r
            break
        }
        Start-Sleep -Milliseconds 100
    }
    if ($null -eq $after) {
        throw 'PRODUCT_FAIL: Enter did not run the profile command again (no RUN-2 marker/notice at the bottom)'
    }
    Assert-RestartNotice $after.text 'the restarted exit'

    # Row 0 still opens with the first run's first line: the history was
    # kept, not repainted. The second run's marker is the bottom viewport's
    # own read (the wait above); the first run's marker is out of it.
    $top = Get-ScreenScroll $s $index 0
    Assert-FirstLine $top.text 'PRE-EXIT-ABOVE-RUN-1' 'the top of the scrollback after the restart'
    if ($after.text.Contains('RESTART-MARKER-RUN-1')) {
        throw 'PRODUCT_FAIL: the first run marker is still in the viewport after the restart'
    }
    # Positive history proof: row 50 sits inside the first run's output
    # (FILLER-50..59, then RUN-1 at row 60). A restart that recreated the
    # terminal would show run 2's filler/RUN-2 here instead.
    $mid = Get-ScreenScroll $s $index 50
    if (-not $mid.text.Contains('RESTART-MARKER-RUN-1')) {
        throw 'PRODUCT_FAIL: the mid-scrollback read lost the first run marker; the restart did not preserve history'
    }
    if (-not $mid.text.Contains($RestartSentence)) {
        throw 'PRODUCT_FAIL: the first exit notice is not in the retained history'
    }
    Write-Host 'restart appended RUN-2 below the preserved RUN-1 history'
}

Invoke-Scenario 'second-enter-does-not-restart-a-live-run' {
    param($s)
    Assert-Profile $s 'repro-live'
    $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'repro-live' }
    $index = [int]$opened.state.active
    [void](Wait-ScreenContains $s 'LIVE-MARKER' -Index $index)

    # First run: the nonce line makes it echo and exit, so this is a real
    # exit that offers the restart.
    $first = "FIRST-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    [void](Invoke-SeamCommand $s @{ op = 'send-text'; index = $index; text = $first })
    Press-Enter $s $index
    [void](Wait-ScreenContains $s "GOT-$first" -Index $index)
    $screen = Wait-ScreenContains $s 'Process exited.' -Index $index
    Assert-RestartNotice $screen 'the live profile exit'
    $marksBefore = Count-Line $screen 'LIVE-MARKER'

    # Restart: the second run prints its marker and waits for a line.
    Press-Enter $s $index
    Assert-PaneAlive $s $index 'the live restart'
    $deadline = [datetime]::UtcNow.AddSeconds(20)
    $marks = 0
    while ([datetime]::UtcNow -lt $deadline) {
        $screen = Get-Screen $s $index
        $marks = Count-Line $screen 'LIVE-MARKER'
        if ($marks -ge ($marksBefore + 1)) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($marks -lt ($marksBefore + 1)) {
        throw ("PRODUCT_FAIL: the restart did not start the live profile again " +
               "(markers {0} -> {1})" -f $marksBefore, $marks)
    }
    # The old notice is above the new LIVE-MARKER in the same viewport; the
    # live child's marker must be the LAST line, not the notice.
    $lines = @($screen -split "`n" | Where-Object { $_.Trim().Length -gt 0 })
    if ($lines.Count -gt 0 -and $lines[-1].Contains($RestartSentence)) {
        throw 'PRODUCT_FAIL: the restarted live profile is showing the exit notice'
    }

    # A fresh nonce reaches the live child, and the Enter that sends it does
    # not restart anything: no third marker appears.
    $nonce = "NONCE-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    [void](Invoke-SeamCommand $s @{ op = 'send-text'; index = $index; text = $nonce })
    Press-Enter $s $index
    [void](Wait-ScreenContains $s "GOT-$nonce" -Index $index)
    Assert-PaneAlive $s $index 'the live nonce'

    # Let the echo's own exit settle; a stale restart path would have
    # consumed the nonce and either echoed nothing or started a third run.
    Start-Sleep -Milliseconds 500
    $screen = Get-Screen $s $index
    $marksAfter = Count-Line $screen 'LIVE-MARKER'
    if ($marksAfter -ne ($marksBefore + 1)) {
        throw ("PRODUCT_FAIL: Enter on the live run restarted it again " +
               "(expected {0} markers, saw {1})" -f ($marksBefore + 1), $marksAfter)
    }
    Write-Host ("nonce echo: GOT-{0}, markers {1} -> {2}" -f $nonce, $marksBefore, $marksAfter)
}

Invoke-Scenario 'another-key-still-closes' {
    param($s)
    Assert-Profile $s 'repro'
    $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'repro' }
    $index = [int]$opened.state.active
    $screen = Wait-ScreenContains $s 'Process exited.' -Index $index
    Assert-RestartNotice $screen 'the escape exit'
    $before = (Get-State $s).tabs.Count
    if ($before -lt 2) {
        throw ("HARNESS: expected the launch tab plus the profile tab, saw {0}" -f $before)
    }
    Press-Escape $s $index
    Wait-TabCountBelow $s $before 'Escape'
}

Invoke-Scenario 'alt-screen-exit-keeps-the-notice' {
    param($s)
    Assert-Profile $s 'repro-alt'
    $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'repro-alt' }
    $index = [int]$opened.state.active
    $screen = Wait-ScreenContains $s 'Process exited.' -Index $index
    Assert-RestartNotice $screen 'the alternate-screen exit'
    Assert-PaneAlive $s $index 'the alternate-screen exit'

    # The read is the ACTIVE screen. The command printed PRIMARY-ONLY on the
    # primary screen, switched to alternate, printed ALT-ONLY there and
    # exited; the notice must land on primary (the scrollback's screen). If
    # the switch never ran, the read would show ALT-ONLY and no PRIMARY-ONLY.
    $screen = Get-Screen $s $index
    if (-not $screen.Contains('PRIMARY-ONLY')) {
        throw 'PRODUCT_FAIL: the exit notice is not on the primary screen'
    }
    if ($screen.Contains('ALT-ONLY')) {
        throw ("PRODUCT_FAIL: the read is still the alternate screen; the notice did not switch to primary. " +
               "Screen:`n{0}" -f $screen)
    }
}

Invoke-Scenario 'failed-restart-rearms' {
    param($s)
    # A copy of cmd the profile resolves at open time; deleting it after the
    # first exit makes the next spawn fail. (An invalid working directory
    # would NOT fail: Subprocess.start falls back to the inherited cwd.)
    Copy-Item -Force -LiteralPath (Join-Path $env:SystemRoot 'System32\cmd.exe') -Destination $ReproExe
    Assert-Profile $s 'repro-del'
    $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'repro-del' }
    $index = [int]$opened.state.active
    $screen = Wait-ScreenContains $s 'Process exited.' -Index $index
    Assert-RestartNotice $screen 'the deleted-exe first exit'
    [void](Wait-ScreenContains $s 'DELETED-EXE-MARKER' -Index $index)
    Assert-PaneAlive $s $index 'the deleted-exe first exit'

    Remove-Item -Force -LiteralPath $ReproExe
    Press-Enter $s $index
    Assert-PaneAlive $s $index 'the failed restart'
    $screen = Wait-ScreenContains $s 'Restart failed:' -Index $index
    # The reason line starts on its own line below the notice, and the
    # notice is still on the same primary read.
    Assert-RestartNotice $screen 'the failed restart'
    if ((Count-Line $screen 'Restart failed:') -lt 1) {
        throw 'PRODUCT_FAIL: the failed restart printed no reason line'
    }

    # Enter retries: the executable is still gone, so a second reason line
    # appears and the offer survives.
    $before = Count-Line $screen 'Restart failed:'
    Press-Enter $s $index
    Assert-PaneAlive $s $index 'the restart retry'
    $deadline = [datetime]::UtcNow.AddSeconds(20)
    $retried = $false
    while ([datetime]::UtcNow -lt $deadline) {
        $screen = Get-Screen $s $index
        if ((Count-Line $screen 'Restart failed:') -ge ($before + 1)) {
            $retried = $true
            break
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $retried) {
        throw 'PRODUCT_FAIL: Enter after a failed restart did not retry (no second reason line)'
    }
    Assert-RestartNotice $screen 'the retried restart'

    # Escape still closes.
    $tabs = (Get-State $s).tabs.Count
    Press-Escape $s $index
    Wait-TabCountBelow $s $tabs 'Escape after a failed restart'
}

Invoke-Scenario 'env-revocation' {
    param($s)
    Assert-Profile $s 'repro-env'
    $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'repro-env' }
    $index = [int]$opened.state.active
    [void](Wait-ScreenContains $s 'SECRET-SET' -Index $index)
    $screen = Wait-ScreenContains $s 'Process exited.' -Index $index
    Assert-RestartNotice $screen 'the env exit'
    Assert-PaneAlive $s $index 'the env exit'

    # Revoke SECRET in the SAME staged config the app is reading, then
    # reload. The reload must refresh the restart spec's env override: the
    # spec was captured from the config arena at surface init, and without
    # the refresh the restarted child would still see SECRET=1.
    $configPath = Join-Path $s.TempXdg 'wintty\config.wintty'
    $generation0 = (Invoke-SeamCommand $s @{ op = 'config-state' }).appliedGeneration
    $revoked = (Get-Content -Raw -LiteralPath $configPath) -replace '(?m)^[ \t]*env[ \t]*=[ \t]*SECRET=1[ \t]*\r?\n?', ''
    [System.IO.File]::WriteAllText(
        $configPath, $revoked, [System.Text.UTF8Encoding]::new($false))
    $reloaded = Invoke-SeamCommand $s @{ op = 'reload-config' }
    $deadline = [datetime]::UtcNow.AddSeconds(20)
    while ((Invoke-SeamCommand $s @{ op = 'config-state' }).appliedGeneration -eq $generation0) {
        if ([datetime]::UtcNow -gt $deadline) {
            throw 'HARNESS: the config reload never applied'
        }
        Start-Sleep -Milliseconds 100
    }

    # The service generation is not the surface's application: let the
    # surface's change_config message land before the restart reads the spec.
    Start-Sleep -Milliseconds 1000

    # Discriminator: a FRESH tab reads the reloaded config directly. If it
    # still sees SECRET, the reload retained the env; if it sees SECRET-[],
    # the reloaded config is clean and the restart spec is the stale half.
    $fresh = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'repro-env' }
    $freshIndex = [int]$fresh.state.active
    $freshScreen = Wait-ScreenContains $s 'SECRET-' -Index $freshIndex
    $freshLine = @($freshScreen -split "`n" | Where-Object { $_.Contains('SECRET-') } |
        Select-Object -First 1)
    Write-Host ("fresh-tab env: '{0}'" -f ("$freshLine".Trim()))
    if ("$freshLine".Contains('SECRET-SET')) {
        throw 'PRODUCT_FAIL: the reloaded config still defines SECRET'
    }

    Press-Enter $s $index
    Assert-PaneAlive $s $index 'the env restart'
    $deadline = [datetime]::UtcNow.AddSeconds(20)
    $screen = ''
    while ([datetime]::UtcNow -lt $deadline) {
        $screen = Get-Screen $s $index
        if ($screen.Contains('SECRET-UNSET')) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $screen.Contains('SECRET-UNSET')) {
        throw 'PRODUCT_FAIL: the restarted child still sees the revoked SECRET value'
    }
    $lastSecret = @($screen -split "`n" | Where-Object { $_.Contains('SECRET-') } |
        Select-Object -Last 1)
    if ($lastSecret.Count -gt 0 -and -not $lastSecret[0].Contains('SECRET-UNSET')) {
        throw ("PRODUCT_FAIL: the newest SECRET marker is still the old value: '{0}'" -f
            $lastSecret[0].Trim())
    }
    Write-Host ("env revoked: reload applied generation {0}" -f $generation0)
}

Invoke-Scenario 'disabled-profile-keeps-the-old-close' {
    param($s)
    Assert-ProfileRetry $s 'repro' $false
    $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'repro' }
    $index = [int]$opened.state.active
    $before = $opened.state.tabs.Count
    $screen = Wait-ScreenContains $s 'Process exited.' -Index $index
    Assert-Notice $screen $CloseSentence 'the disabled exit'
    Press-Enter $s $index
    Wait-TabCountBelow $s $before 'Enter on a disabled exit'
    Write-Host 'a profile without the opt-in closed on Enter'
} -ConfigText $ConfigNoRetry

Invoke-Scenario 'custom-key-restarts' {
    param($s)
    Assert-ProfileRetry $s 'repro' $true
    $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'repro' }
    $index = [int]$opened.state.active
    $screen = Wait-ScreenContains $s 'Process exited.' -Index $index
    Assert-Notice $screen $CtrlRSentence 'the custom-key exit'
    # The run counter is GLOBAL across every scenario in this probe run (one
    # count file under $OutDir), so this scenario's first run is not RUN-1.
    # Capture the number the first exit printed; the restart must print N+1.
    $firstRun = [int]([regex]::Match($screen, 'RESTART-MARKER-RUN-(\d+)').Groups[1].Value)
    if ($firstRun -lt 1) { throw "HARNESS: no RESTART-MARKER-RUN-n in the first exit screen" }
    $next = $firstRun + 1
    $wantMarker = "RESTART-MARKER-RUN-$next"
    Press-CtrlR $s $index
    Assert-PaneAlive $s $index 'the chord restart'
    $deadline = [datetime]::UtcNow.AddSeconds(20)
    $sawNext = $false
    while ([datetime]::UtcNow -lt $deadline) {
        $r = Get-ScreenScroll $s $index
        if ($r.text.Contains($wantMarker)) { $sawNext = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $sawNext) { throw "PRODUCT_FAIL: Ctrl+R did not restart the profile command (no $wantMarker in the scroll)" }
    $notice2 = Wait-ScreenContains $s 'Process exited.' -Index $index
    Assert-Notice $notice2 $CtrlRSentence 'the restarted exit' 
    $before = (Get-State $s).tabs.Count
    Press-Enter $s $index
    Wait-TabCountBelow $s $before 'Enter after a chord restart'
    Write-Host 'Ctrl+R restarted; Enter closed the restarted exit'
} -ConfigText $ConfigCtrlR

Invoke-Scenario 'ad-hoc-launch-has-no-offer' {
    param($s)
    Assert-ProfileRetry $s 'repro' $true
    $st = Get-State $s
    $titles = ($st.tabs | ForEach-Object { $_.title }) -join '; '
    Write-Host ("launch state: tabs={0} titles=[{1}]" -f $st.tabs.Count, $titles)
    $screen = Wait-ScreenContains $s 'Process exited.' -Index 0
    Assert-Notice $screen $CloseSentence 'the -e exit'
    Press-Enter $s 0
    if (-not $s.Proc.WaitForExit(10000)) {
        throw 'PRODUCT_FAIL: Enter on a -e exit did not close the pane (the app is still running)'
    }
    Write-Host '-e exit showed the disabled notice and closed'
} -ConfigText $ConfigEdge -Arguments @('-e', 'C:\Windows\System32\cmd.exe', '/c', 'exit 1') -ExpectAppExit

# ---- verdict ---------------------------------------------------------------

$result = [ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=<session token>); zero synthesized OS input'
    scenarios = $script:Scenarios
}
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

Write-Host ''
Write-Host 'scenario                                    verdict'
Write-Host '------------------------------------------  -------'
foreach ($sc in $script:Scenarios) {
    $verdict = if ($sc.ok) { 'PASS' } else { "FAIL ($($sc.class))" }
    Write-Host ("{0,-42} {1}" -f $sc.name, $verdict)
}

$product = @($script:Scenarios | Where-Object { -not $_.ok -and $_.class -eq 'product' })
$harness = @($script:Scenarios | Where-Object { -not $_.ok -and $_.class -eq 'harness' })
if ($product.Count -gt 0) { exit 2 }
if ($harness.Count -gt 0) { exit 1 }
exit 0
