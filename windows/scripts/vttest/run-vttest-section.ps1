#requires -Version 7
<#
.SYNOPSIS
Launch Wintty hosting a single vttest menu section, driven without GUI keyboard.

.DESCRIPTION
A companion to run-vttest.ps1 for capturing individual vttest sections during a
visual VT-compliance pass. Synthetic keyboard input does not reach the Wintty
window (WinUI lifted-input focus cannot be forced from a detached process), so
this uses vttest-section.sh: vttest runs under an inner `script` pty inside
Wintty's pane and its menu choice is auto-fed from a pipe. The selected screen
stays up (vttest-section.sh holds it) so you can screenshot and assess it.

Build vttest first with build-vttest.sh. The runner prints the launched PID;
stop it with `Stop-Process -Id <pid>` when done.

.EXAMPLE
# Character-set test (menu 3):
./run-vttest-section.ps1 -WinttyExe C:\path\to\Wintty.exe -Section 3
#>
[CmdletBinding()] param(
    [Parameter(Mandatory)][string]$WinttyExe,
    [Parameter(Mandatory)][ValidatePattern('^\d+$')][string]$Section,  # vttest menu digits, e.g. "3"
    [int]$Pages = 0,                                    # paging RETURNs within the test
    [string]$Distro = 'Ubuntu-24.04',
    [string]$OutDir = "$env:TEMP\vttest-section"
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

# LF-normalize the committed driver into the distro: a Windows checkout may carry
# CRLF, which bash rejects. Translate the script's own path to its /mnt mount.
$srcSh = Join-Path $PSScriptRoot 'vttest-section.sh'
$srcDrive = $srcSh.Substring(0, 1).ToLower()
$srcMnt = "/mnt/$srcDrive" + ($srcSh.Substring(2) -replace '\\', '/')
wsl.exe -d $Distro -- bash -lc "tr -d '\r' < '$srcMnt' > /tmp/vttest-section.sh" | Out-Null

# Temp ghostty config (same XDG harness as run-vttest.ps1; the `ghostty/` subdir
# is required). The command splits on spaces into wsl.exe argv; Section/Pages are
# bare digits.
$cfgDir = Join-Path $OutDir 'cfg'
$cfgGhostty = Join-Path $cfgDir 'ghostty'
New-Item -ItemType Directory -Force $cfgGhostty | Out-Null
"command = wsl.exe -d $Distro -- bash -l /tmp/vttest-section.sh $Section $Pages" |
    Set-Content -LiteralPath (Join-Path $cfgGhostty 'config') -Encoding utf8

$prevXdg = $env:XDG_CONFIG_HOME
$proc = $null
try {
    $env:XDG_CONFIG_HOME = $cfgDir
    $proc = Start-Process -FilePath $WinttyExe -PassThru
}
finally {
    $env:XDG_CONFIG_HOME = $prevXdg
}

Write-Host "Wintty PID $($proc.Id) hosting vttest section $Section ($Distro)."
Write-Host "Move the window to the primary monitor, screenshot, then: Stop-Process -Id $($proc.Id)"
$proc.Id
