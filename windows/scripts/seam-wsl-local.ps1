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

    WSL-gated: with no distro on the machine, or one that cannot run a
    command, the scenario prints a SKIP line, records it, and exits 0. A
    skip is not a pass and says so.

    Zero OS input is synthesized; the app runs with a private state tree.
    Exits 0 on pass or skip, 2 on a product finding, 1 when the harness
    could not run and nothing is known about the product.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # The daemon renders this profile's prompt in about 3 s; a cold distro
    # start can take longer, and the bug is "never", not "slow".
    [int]$ReadySeconds = 30
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

$distro = Get-WslDistro
if (-not $distro) {
    $why = 'no WSL distro on this machine'
    Write-Host "SKIP wsl-local: $why (a skip is not a pass)" -ForegroundColor Yellow
    Write-Result 'skip' '' $why
    exit 0
}
if (-not (Test-WslDistroRuns $distro)) {
    $why = "WSL distro '$distro' cannot run a command"
    Write-Host "SKIP wsl-local: $why (a skip is not a pass)" -ForegroundColor Yellow
    Write-Result 'skip' '' $why
    exit 0
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
    $detail = "distro '$distro' rendered in $([math]::Round($clock.Elapsed.TotalSeconds, 2))s: '$first'"
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
