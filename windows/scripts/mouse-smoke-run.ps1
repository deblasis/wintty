<#
.SYNOPSIS
    Run one mouse-smoke cell.

.DESCRIPTION
    Copies windows/dev-configs/mouse-smoke/<Cell>.conf into an isolated
    XDG_CONFIG_HOME (as ghostty/config.ghostty), launches Wintty.exe
    pointed at it, and waits for the operator to manually exercise the
    cell's click checklist and quit the TUI. Exit code reflects clean
    process teardown only; click pass/fail is recorded by the operator.

    The WinUI shell does NOT honor --config-file; isolation must go
    through env. The caller's pre-existing XDG_CONFIG_HOME is restored
    on exit.

.PARAMETER Cell
    Cell stem (no extension), e.g. "01-wsl2-mc". Must match a file in
    windows/dev-configs/mouse-smoke/.

.PARAMETER TimeoutMs
    Safety timeout. Default 900000 (15 minutes) - generous because the
    operator drives the TUI manually.

.PARAMETER ExePath
    Path to built Wintty.exe. Defaults to the Debug x64 output resolved
    relative to this script's location, so the runner works from any cwd.

.OUTPUTS
    Exit code 0 when Wintty exits cleanly. Exit code 2 on setup error
    (fixture or exe not found) or timeout. Otherwise propagates Wintty's
    own exit code.
#>
param(
    [Parameter(Mandatory)][string]$Cell,
    [int]$TimeoutMs = 900000,
    [string]$ExePath
)
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $ExePath) {
    $ExePath = Join-Path $repoRoot 'windows\Ghostty\bin\x64\Debug\net10.0-windows10.0.19041.0\Wintty.exe'
}

$fixturePath = Join-Path $repoRoot "windows\dev-configs\mouse-smoke\$Cell.conf"
if (-not (Test-Path -LiteralPath $fixturePath)) {
    Write-Error "fixture not found: $fixturePath"
    exit 2
}
if (-not (Test-Path -LiteralPath $ExePath)) {
    Write-Error "exe not found: $ExePath (run ``just build-dll build-win`` first)"
    exit 2
}

$tempXdg = Join-Path $env:TEMP "wintty-mouse-smoke-$Cell-$((New-Guid).Guid)"
$ghosttyDir = Join-Path $tempXdg 'ghostty'
New-Item -ItemType Directory -Path $ghosttyDir -Force | Out-Null
$configPath = Join-Path $ghosttyDir 'config.ghostty'
Copy-Item -LiteralPath $fixturePath -Destination $configPath -Force

$originalXdgSet = Test-Path Env:XDG_CONFIG_HOME
$originalXdg = if ($originalXdgSet) { $env:XDG_CONFIG_HOME } else { $null }

try {
    $env:XDG_CONFIG_HOME = $tempXdg
    Write-Host "Launching cell '$Cell' with XDG_CONFIG_HOME=$tempXdg"
    Write-Host "Click checklist for this cell is in windows/dev-configs/mouse-smoke/$Cell.conf header."
    Write-Host "Quit the TUI when done; the runner returns when Wintty exits."

    $proc = Start-Process -FilePath $ExePath -PassThru
    $exited = $proc.WaitForExit($TimeoutMs)
    if (-not $exited) {
        Write-Error "TIMEOUT after ${TimeoutMs}ms - killing process"
        # Kill the tree: the TUI runs as a child and would outlive a kill on
        # the parent alone, and an orphan does not trip Assert-NoWintty.
        try { $proc.Kill($true); [void]$proc.WaitForExit(3000) }
        catch [System.InvalidOperationException] {}
        exit 2
    }

    Write-Host "Wintty exit code: $($proc.ExitCode)"
    exit $proc.ExitCode
}
finally {
    if ($originalXdgSet) {
        $env:XDG_CONFIG_HOME = $originalXdg
    } else {
        Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $tempXdg) {
        Remove-Item -LiteralPath $tempXdg -Recurse -Force -ErrorAction SilentlyContinue
    }
}
