#requires -Version 7
<#
.SYNOPSIS
Launch Wintty hosting vttest over WSL/ConPTY for visual VT-compliance testing.

.DESCRIPTION
vttest is interactive and visual: unlike esctest it produces no machine-readable
log, so this runner only stands up the host. It launches Wintty with an isolated
XDG_CONFIG_HOME whose `command` spawns vttest inside a WSL distro -- the same
`wsl.exe -> ConPTY -> libghostty` path the esctest harness uses. vttest then
renders its menu in the Wintty pane; drive the menus from the keyboard and
screenshot each section to assess rendering (see vttest-results.md).

vttest reads menu selections from its controlling tty, so navigation needs real
keyboard input to the Wintty window -- it cannot be driven by piping stdin
(vttest puts the tty in raw mode via termios and a pipe is not a tty).

Build vttest first with build-vttest.sh (see that script's header). The runner
prints the launched PID; stop it with `Stop-Process -Id <pid>` when done.

.EXAMPLE
./run-vttest.ps1 -WinttyExe C:\path\to\Wintty.exe
#>
[CmdletBinding()] param(
    [Parameter(Mandatory)][string]$WinttyExe,        # path to built Wintty.exe
    [string]$Distro = 'Ubuntu-24.04',
    [string]$VttestPath = '~/vttest',                 # vttest path inside the distro
    [string]$OutDir = "$env:TEMP\vttest-run"          # holds the temp ghostty config
)
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force $OutDir | Out-Null

# The WinUI app ignores --command; XDG_CONFIG_HOME + `command =` is the proven
# launch harness (see the esctest runner / #494 OSC7 work). ghostty reads
# $XDG_CONFIG_HOME/ghostty/config -- the `ghostty/` subdir is required, or the
# surface silently falls back to the default shell. The command splits on spaces
# into wsl.exe argv; `bash -lc ~/vttest` runs vttest as a login shell so tilde
# expansion and PATH are sane, and all paths here are space-free.
$cfgDir = Join-Path $OutDir 'cfg'
$cfgGhostty = Join-Path $cfgDir 'ghostty'
New-Item -ItemType Directory -Force $cfgGhostty | Out-Null
"command = wsl.exe -d $Distro -- bash -lc $VttestPath" |
    Set-Content -LiteralPath (Join-Path $cfgGhostty 'config') -Encoding utf8

# Inherit the full environment and add only the XDG override (rather than
# -Environment, whose merge-vs-replace semantics vary). Restore afterward.
$prevXdg = $env:XDG_CONFIG_HOME
$proc = $null
try {
    $env:XDG_CONFIG_HOME = $cfgDir
    $proc = Start-Process -FilePath $WinttyExe -PassThru
}
finally {
    $env:XDG_CONFIG_HOME = $prevXdg
}

Write-Host "Wintty PID $($proc.Id) is hosting vttest ($Distro)."
Write-Host "Drive the menus from the keyboard and screenshot each section."
Write-Host "Stop it with: Stop-Process -Id $($proc.Id)"
$proc.Id
