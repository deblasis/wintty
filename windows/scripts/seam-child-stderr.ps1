#requires -Version 7
<#
    A shell this app starts gets a console on all three standard handles.

    The app points its own stderr at a log file. A pseudoconsole spawn that
    does not set STARTF_USESTDHANDLES lets Windows copy that handle into the
    child, where it is not a console and not valid either: whatever the child
    writes to stderr is lost. Most shells never notice, because they draw
    their prompt on stdout. wsl.exe does: it maps a standard handle onto the
    Linux pty only when that handle is a console, so an interactive shell in
    a WSL pane got a pipe for fd 2, drew its prompt there, and the pane
    stayed blank while the shell ran.

    The decisive legs need no WSL, so this scenario can always fail. A
    PowerShell child reports [Console]::Is{Input,Output,Error}Redirected:

      direct     the app spawns the child as a profile command
      via-cmd    a cmd pane spawns it, so it inherits cmd's handles

    Both must report False for all three. The WSL legs run when the machine
    has a distro that can run a command, and then fail the scenario like any
    other leg; without one they are recorded as skipped, and the scenario
    still runs and decides on the two legs above:

      wsl-tty     `sh -c` reports whether fd 2 is a tty
      wsl-prompt  an interactive `wsl.exe -d <distro>` pane is not blank
                  within the budget, with nothing typed into it. A blank pane
                  is what the bug looked like: the shell drew its prompt on
                  stderr. The leg checks that output arrived, not what the
                  prompt says, since a prompt's shape is the user's.

    Exits 0 on pass, 2 on a product finding, 1 when the harness could not
    run and nothing is known about the product.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # A PowerShell child prints its three flags in about a second; a cold
    # distro can take far longer to show a prompt.
    [int]$ProbeSeconds = 20,
    [int]$PromptSeconds = 30
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Write-Result([string]$Outcome, [string]$Class, [string]$Detail, $Legs) {
    [ordered]@{
        actuation = 'seam (WINTTY_TEST_SEAM=<session token>); zero synthesized OS input'
        scenario = 'child-stderr'
        outcome = $Outcome
        class = $Class
        detail = $Detail
        legs = $Legs
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8
}

# The probe a PowerShell child runs on itself. Each flag prints on its own
# line as NAME=True|False, a shape the echoed command line cannot match.
$probe = "'IN-REDIR=' + [Console]::IsInputRedirected; 'OUT-REDIR=' + [Console]::IsOutputRedirected; 'ERR-REDIR=' + [Console]::IsErrorRedirected"
$psDirect = "powershell.exe -NoLogo -NoProfile -Command `"$probe; Start-Sleep 60`""

function Get-Screen($s, [int]$Index) {
    try { return "$((Invoke-SeamCommand $s @{ op = 'screen-text'; index = $Index; leaf = -1 }).text)" }
    catch { return '' } # no live surface yet
}

# The three flags off a screen, or $null until all three have printed.
function Read-Flags([string]$Text) {
    $m = @{}
    foreach ($x in [regex]::Matches($Text, '\b(IN|OUT|ERR)-REDIR=(True|False)\b')) { $m[$x.Groups[1].Value] = $x.Groups[2].Value }
    if ($m.Count -lt 3) { return $null }
    return [ordered]@{ in = $m.IN; out = $m.OUT; err = $m.ERR }
}

# Before calling a silent leg a harness failure: a pane that printed nothing
# because the app died is a product finding, and Get-Screen swallows that.
function Assert-AppAlive($s) {
    if ($s.Proc.HasExited) { throw ("APP_EXIT: the app exited during the scenario (code {0})" -f $s.Proc.ExitCode) }
}

function Wait-Flags($s, [int]$Index, [int]$Seconds) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        $flags = Read-Flags (Get-Screen $s $Index)
        if ($flags) { return $flags }
        Start-Sleep -Milliseconds 250
    }
    return $null
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

# Bounded, because a wedged WSL must not wedge the harness.
function Test-WslDistroRuns([string]$Distro) {
    $job = Start-ThreadJob -ArgumentList $Distro -ScriptBlock {
        param($d)
        $out = & wsl.exe -d $d -- echo wintty-wsl-ok 2>$null
        [pscustomobject]@{ Code = $LASTEXITCODE; Out = "$out" }
    }
    if (-not (Wait-Job $job -Timeout 90)) { Stop-Job $job; Remove-Job $job -Force; return $false }
    $r = Receive-Job $job; Remove-Job $job -Force
    return $r.Code -eq 0 -and $r.Out -like '*wintty-wsl-ok*'
}

$distro = Get-WslDistro
if ($distro -and -not (Test-WslDistroRuns $distro)) { $distro = $null }
$quoted = if ($distro -and $distro -match '\s') { "`"$distro`"" } else { $distro }

$config = @"
windows-single-instance = false
window-save-state = never
profile.stderrdirect.name = StderrDirect
profile.stderrdirect.command = $psDirect
profile.stderrcmd.name = StderrViaCmd
profile.stderrcmd.command = cmd.exe
"@
if ($distro) {
    $config += @"

profile.stderrwsltty.name = StderrWslTty
profile.stderrwsltty.command = wsl.exe -d $quoted -- sh -c "[ -t 2 ] && echo WSL-ERR-TTY || echo WSL-ERR-PIPE; exec sleep 60"
profile.stderrwslprompt.name = StderrWslPrompt
profile.stderrwslprompt.command = wsl.exe -d $quoted
"@
}

$legs = [ordered]@{}
$findings = [System.Collections.Generic.List[string]]::new()
$s = $null
try {
    Assert-NoWinttyFrom -ExePath $ExePath -Context "The child stderr scenario"
    # send-text is armed only for the via-cmd leg, which has to type the probe.
    $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -PrivateStateBase -AllowInput

    # direct: the app spawns the child itself.
    $i = [int](Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'stderrdirect' }).state.active
    $direct = Wait-Flags $s $i $ProbeSeconds
    if (-not $direct) { Assert-AppAlive $s; throw "HARNESS: the direct PowerShell child printed no flags within ${ProbeSeconds}s; nothing is known about its handles" }
    $legs.direct = $direct

    # via-cmd: a cmd pane spawns the same probe, inheriting cmd's handles.
    $i = [int](Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'stderrcmd' }).state.active
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $ProbeSeconds -and (Get-Screen $s $i) -notmatch '>\s*$') { Start-Sleep -Milliseconds 250 }
    Invoke-SeamCommand $s @{ op = 'send-text'; index = $i; text = "powershell.exe -NoLogo -NoProfile -Command `"$probe`"`r" } | Out-Null
    $viaCmd = Wait-Flags $s $i $ProbeSeconds
    if (-not $viaCmd) { Assert-AppAlive $s; throw "HARNESS: the PowerShell child cmd started printed no flags within ${ProbeSeconds}s; nothing is known about its handles" }
    $legs.viaCmd = $viaCmd

    foreach ($leg in @('direct', 'viaCmd')) {
        $f = $legs[$leg]
        if ($f.in -ne 'False' -or $f.out -ne 'False' -or $f.err -ne 'False') {
            $findings.Add("$leg child: IN-REDIR=$($f.in) OUT-REDIR=$($f.out) ERR-REDIR=$($f.err)")
        }
    }

    if ($distro) {
        $i = [int](Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'stderrwsltty' }).state.active
        $clock = [System.Diagnostics.Stopwatch]::StartNew(); $tty = $null
        while ($clock.Elapsed.TotalSeconds -lt $PromptSeconds -and -not $tty) {
            $m = [regex]::Match((Get-Screen $s $i), 'WSL-ERR-(TTY|PIPE)')
            if ($m.Success) { $tty = $m.Groups[1].Value } else { Start-Sleep -Milliseconds 250 }
        }
        if (-not $tty) { Assert-AppAlive $s; throw "HARNESS: the WSL fd 2 probe printed no answer within ${PromptSeconds}s; nothing is known about its stderr" }
        $legs.wslTty = "fd 2 is a $($tty.ToLower())"
        if ($tty -ne 'TTY') { $findings.Add("wsl '$distro': sh reports fd 2 is a pipe, not the pty") }

        $i = [int](Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'stderrwslprompt' }).state.active
        # Only the timing is recorded: the prompt itself carries the user and
        # host names and a local path, and result.json gets pasted around.
        $clock = [System.Diagnostics.Stopwatch]::StartNew(); $shown = $false
        while ($clock.Elapsed.TotalSeconds -lt $PromptSeconds) {
            if (((Get-Screen $s $i) -replace '\s', '').Length -gt 0) { $shown = $true; break }
            Start-Sleep -Milliseconds 250
        }
        $legs.wslPrompt = if ($shown) { "output after $([math]::Round($clock.Elapsed.TotalSeconds, 2))s" } else { "blank after ${PromptSeconds}s" }
        if (-not $shown) { Assert-AppAlive $s; $findings.Add("wsl '$distro': an interactive pane stayed blank for ${PromptSeconds}s with nothing typed into it") }
    } else {
        $legs.wsl = 'skipped: no WSL distro on this machine that can run a command'
    }

    Assert-AppAlive $s

    if ($findings.Count -gt 0) {
        throw ("PRODUCT_FAIL: a shell this app starts does not get a console on every standard handle - " + ($findings -join '; '))
    }

    $detail = "direct and via-cmd children report all three handles as consoles" + $(if ($distro) { "; wsl '$distro' fd 2 is the pty and the interactive pane is not blank" } else { '; wsl legs skipped (no distro)' })
    Write-Host "PASS child-stderr: $detail" -ForegroundColor Green
    Write-Result 'pass' '' $detail $legs
    exit 0
} catch {
    $msg = "$($_.Exception.Message)"
    $class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
    Write-Host "FAIL child-stderr [$class]: $msg" -ForegroundColor Red
    Write-Result 'fail' $class $msg $legs
    if ($class -eq 'product') { exit 2 } else { exit 1 }
} finally {
    if ($null -ne $s) { Stop-SeamSession $s }
}
