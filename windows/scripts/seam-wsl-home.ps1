#requires -Version 7
<#
    A WSL pane with no starting directory opens in the distro's Linux HOME,
    as reported by `wsl.exe -d <distro> -- printenv HOME`. Two legs: a profile
    whose `sh -c` prints its own $PWD, and an interactive shell asked for it.

    Exits 0 pass, 2 product failure, 1 harness failure, 3 skip (no distro).
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$PromptSeconds = 30
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Write-Result([string]$Outcome, [string]$Class, [string]$Detail, $Legs) {
    [ordered]@{
        actuation = 'seam (WINTTY_TEST_SEAM=<session token>); zero synthesized OS input'
        scenario = 'wsl-home'
        outcome = $Outcome
        class = $Class
        detail = $Detail
        legs = $Legs
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8
}

function Get-Screen($s, [int]$Index) {
    try { return "$((Invoke-SeamCommand $s @{ op = 'screen-text'; index = $Index; leaf = -1 }).text)" }
    catch { return '' } # no live surface yet
}

# The echoed command line has a literal $PWD, never a path starting with "/".
function Wait-Cwd($s, [int]$Index, [int]$Seconds) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        $m = [regex]::Matches((Get-Screen $s $Index), '(?m)^WSL-CWD=(/\S*)\s*$')
        if ($m.Count -gt 0) { return $m[$m.Count - 1].Groups[1].Value }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Invoke-WslBounded([string[]]$WslArgs) {
    $job = Start-ThreadJob -ArgumentList (, $WslArgs) -ScriptBlock {
        param($a)
        $out = & wsl.exe @a 2>$null
        [pscustomobject]@{ Code = $LASTEXITCODE; Out = "$out".Trim() }
    }
    if (-not (Wait-Job $job -Timeout 90)) { Stop-Job $job; Remove-Job $job -Force; return $null }
    $r = Receive-Job $job; Remove-Job $job -Force
    return $r
}

function Get-WslDistro {
    if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) { return $null }
    $env:WSL_UTF8 = '1'
    $out = & wsl.exe --list --quiet 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return @($out |
        ForEach-Object { "$_".Trim([char]0xFEFF, [char]0, ' ', "`t", "`r") } |
        Where-Object { $_ -and $_ -notlike 'docker-desktop*' }) | Select-Object -First 1
}

$legs = [ordered]@{}
$distro = Get-WslDistro
$home_ = if ($distro) { Invoke-WslBounded @('-d', $distro, '--', 'printenv', 'HOME') } else { $null }
if (-not $home_ -or $home_.Code -ne 0 -or $home_.Out -notmatch '^/\S*$') {
    $why = if (-not $distro) { 'no WSL distro on this machine' } else { "distro '$distro' could not report its HOME" }
    Write-Host "SKIP wsl-home: $why" -ForegroundColor Yellow
    Write-Result 'skip' '' $why $legs
    exit 3
}
$expected = $home_.Out
$legs.expected = $expected
$quoted = if ($distro -match '\s') { "`"$distro`"" } else { $distro }

$config = @"
windows-single-instance = false
window-save-state = never
# The startup tab: without a default it would run the first profile below,
# which the scenario then opens again.
default-profile = idle
profile.idle.name = Idle
profile.idle.command = cmd.exe
profile.wslhomecmd.name = WslHomeCommand
profile.wslhomecmd.command = wsl.exe -d $quoted -- sh -c "echo WSL-CWD=`$PWD; exec sleep 60"
profile.wslhomeshell.name = WslHomeShell
profile.wslhomeshell.command = wsl.exe -d $quoted
"@

$s = $null
try {
    Assert-NoWinttyFrom -ExePath $ExePath -Context "The WSL home scenario"
    $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -PrivateStateBase -AllowInput

    $i = [int](Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'wslhomecmd' }).state.active
    $legs.command = Wait-Cwd $s $i $PromptSeconds
    if (-not $legs.command) { throw "HARNESS: the WSL command pane printed no directory within ${PromptSeconds}s; nothing is known about where it opened" }

    $i = [int](Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'wslhomeshell' }).state.active
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $PromptSeconds -and (Get-Screen $s $i) -notmatch '[$#]\s*$') { Start-Sleep -Milliseconds 250 }
    # A key, not a CR in send-text: under bracketed paste a pasted CR is not run.
    Invoke-SeamCommand $s @{ op = 'send-text'; index = $i; text = 'echo WSL-CWD=$PWD' } | Out-Null
    Invoke-SeamCommand $s @{ op = 'terminal-key'; index = $i; vk = 0x0D; char = "`r" } | Out-Null
    $legs.shell = Wait-Cwd $s $i $PromptSeconds
    if (-not $legs.shell) { throw "HARNESS: the interactive WSL pane printed no directory within ${PromptSeconds}s; nothing is known about where it opened" }

    if ($s.Proc.HasExited) { throw ("APP_EXIT: the app exited during the scenario (code {0})" -f $s.Proc.ExitCode) }

    $wrong = @(foreach ($leg in 'command', 'shell') { if ($legs[$leg] -cne $expected) { "$leg pane opened in $($legs[$leg])" } })
    if ($wrong.Count -gt 0) {
        throw ("PRODUCT_FAIL: a WSL pane with no starting directory did not open in the distro HOME $expected - " + ($wrong -join '; '))
    }

    $detail = "both WSL panes opened in the distro HOME ($expected)"
    Write-Host "PASS wsl-home: $detail" -ForegroundColor Green
    Write-Result 'pass' '' $detail $legs
    exit 0
} catch {
    $msg = "$($_.Exception.Message)"
    $class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
    Write-Host "FAIL wsl-home [$class]: $msg" -ForegroundColor Red
    Write-Result 'fail' $class $msg $legs
    if ($class -eq 'product') { exit 2 } else { exit 1 }
} finally {
    if ($null -ne $s) { Stop-SeamSession $s }
}
