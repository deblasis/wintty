<#
.SYNOPSIS
    Run one mouse-smoke cell.

.DESCRIPTION
    Copies dev-configs/mouse-smoke/<Cell>.conf into an isolated
    XDG_CONFIG_HOME (as ghostty/config.ghostty), launches Wintty.exe
    pointed at it, and waits for the user to manually exercise the
    cell's click checklist and quit the TUI. Exit code reflects clean
    process teardown only; click pass/fail is recorded by the tester.

    Mirrors scripts/validate-transport-run.ps1 — WinUI shell does NOT
    honor --config-file; isolation must go through env.

.PARAMETER Cell
    Cell stem (no extension), e.g. "01-wsl2-mc". Must match a file in
    dev-configs/mouse-smoke/.

.PARAMETER TimeoutMs
    Safety timeout. Default 900000 (15 minutes) — generous because the
    tester drives the TUI manually.

.PARAMETER ExePath
    Path to built Wintty.exe.
#>
param(
    [Parameter(Mandatory)][string]$Cell,
    [int]$TimeoutMs = 900000,
    [string]$ExePath = './windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe'
)
$ErrorActionPreference = 'Stop'

$fixture = "dev-configs/mouse-smoke/$Cell.conf"
if (-not (Test-Path $fixture)) {
    Write-Host "ERROR: fixture not found: $fixture"; exit 2
}
if (-not (Test-Path $ExePath)) {
    Write-Host "ERROR: exe not found: $ExePath (run 'just build-dll build-win' first)"; exit 2
}

$xdg = Join-Path $env:TEMP "wintty-mouse-smoke-$Cell-$(Get-Random)"
$ghosttyDir = Join-Path $xdg 'ghostty'
New-Item -ItemType Directory -Path $ghosttyDir -Force | Out-Null
Copy-Item $fixture (Join-Path $ghosttyDir 'config.ghostty')

$env:XDG_CONFIG_HOME = $xdg
Write-Host "Launching cell '$Cell' with XDG_CONFIG_HOME=$xdg"
Write-Host "Click checklist for this cell is in dev-configs/mouse-smoke/$Cell.conf header."
Write-Host "Quit the TUI when done; the runner returns when Wintty exits."

$proc = Start-Process -FilePath $ExePath -PassThru
if (-not $proc.WaitForExit($TimeoutMs)) {
    Write-Host "TIMEOUT after ${TimeoutMs}ms — killing process"
    try { $proc.Kill() } catch {}
    Remove-Item -Recurse -Force $xdg
    exit 2
}

Remove-Item -Recurse -Force $xdg
Write-Host "Wintty exit code: $($proc.ExitCode)"
exit $proc.ExitCode
