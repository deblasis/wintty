<#
.SYNOPSIS
    Run one conpty-mode smoke row end-to-end.

.DESCRIPTION
    Copies dev-configs/validate-transport/<Row>.conf into an isolated
    XDG_CONFIG_HOME directory (as ghostty/config.ghostty), launches
    the built Ghostty.exe with XDG_CONFIG_HOME pointing at it, waits
    for exit, then invokes scripts/validate-transport-assert.ps1 -Row
    <Row> and exits with its exit code.

    If the app does not exit within -TimeoutMs (default 10 seconds), it
    is killed and the script exits with code 2 (infra failure). The app
    should exit within a second or two after the shell exits; a timeout
    indicates a regression in ConPTY/bypass teardown.

    The WinUI shell does not honor a --config-file CLI flag - it calls
    ghostty_config_load_default_files which reads from the XDG path.
    So we isolate via environment instead of argv.

    Assumes the app is already built. Call from `just
    validate-transport-smoke <Row>` which chains the build.

.PARAMETER Row
    One of: pwsh-auto, pwsh-always, pwsh-never, cmd-auto.

.PARAMETER TimeoutMs
    Safety timeout. Defaults to 10000 (10 seconds).

.PARAMETER ExePath
    Path to the built Ghostty.exe. Defaults to the Debug x64 output.
#>
param(
    [Parameter(Mandatory)][string]$Row,
    [int]$TimeoutMs = 10000,
    [string]$ExePath = './windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Ghostty.exe'
)
$ErrorActionPreference = 'Stop'

$fixturePath = "dev-configs/validate-transport/$Row.conf"
if (-not (Test-Path $fixturePath)) {
    Write-Host "ERROR: fixture not found: $fixturePath"
    exit 2
}
if (-not (Test-Path $ExePath)) {
    Write-Host "ERROR: exe not found: $ExePath (run ``just build-dll build-win`` first)"
    exit 2
}

# Isolated XDG_CONFIG_HOME: Ghostty looks up its default config at
# $XDG_CONFIG_HOME/ghostty/config.ghostty. Stage our fixture there
# and point the env var at the temp dir.
$tempXdg = Join-Path $env:TEMP "ghostty-validate-xdg-$Row-$((New-Guid).Guid)"
$ghosttyDir = Join-Path $tempXdg 'ghostty'
New-Item -ItemType Directory -Path $ghosttyDir -Force | Out-Null
$configPath = Join-Path $ghosttyDir 'config.ghostty'
Copy-Item -LiteralPath $fixturePath -Destination $configPath -Force

$originalXdgSet = Test-Path Env:XDG_CONFIG_HOME
$originalXdg = if ($originalXdgSet) { $env:XDG_CONFIG_HOME } else { $null }

try {
    $env:XDG_CONFIG_HOME = $tempXdg
    # Capture UTC start time at millisecond precision so the assertion
    # can filter log entries to just this run's window. Log lines are
    # "YYYY-MM-DDTHH:MM:SS.fffZ | ..." so a lexical >= comparison works.
    $runStart = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    Write-Host "launching: $ExePath (XDG_CONFIG_HOME=$tempXdg) since=$runStart"
    $proc = Start-Process -FilePath $ExePath -PassThru
    $exited = $proc.WaitForExit($TimeoutMs)
    if (-not $exited) {
        # Unexpected hang: the app should exit within a second or two
        # once the shell's command completes. If we hit this branch the
        # ConPTY/bypass teardown path has regressed (see # 293's fix via
        # PR # 297 for the prior working state). Kill the app and fail
        # with exit 2 so the regression is loud, not papered over.
        Write-Host "FAIL: $Row (app did not exit within ${TimeoutMs}ms; possible regression of child-exit teardown)"
        try { Stop-Process -Id $proc.Id -Force } catch {}
        exit 2
    }

    Write-Host "app exited with code $($proc.ExitCode); running assertion"
    pwsh -NoProfile -File scripts/validate-transport-assert.ps1 -Row $Row -Since $runStart
    exit $LASTEXITCODE
}
finally {
    if ($originalXdgSet) {
        $env:XDG_CONFIG_HOME = $originalXdg
    } else {
        Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue
    }
    if (Test-Path $tempXdg) {
        Remove-Item -LiteralPath $tempXdg -Recurse -Force -ErrorAction SilentlyContinue
    }
}
