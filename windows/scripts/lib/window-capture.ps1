# Film one window at compositor rate, out of process.
#
# The instrument every motion harness in this repo needs and none of them
# had. Graphics.CopyFromScreen -- what the older capture harnesses use --
# costs about 175ms per grab on the machine this was written for,
# REGARDLESS of region size (measured identically at 1280x820, 640x820,
# 1280x200 and 400x400, and unchanged by CAPTUREBLT). Five frames a second
# cannot judge a 340ms animation.
#
# WindowCapture.exe replaces it with Windows.Graphics.Capture: frames from
# the compositor, delivered on a pool thread, in a SEPARATE PROCESS. The
# separation is not tidiness. The apps these harnesses film block their own
# UI thread for hundreds of milliseconds at a time, so an in-process camera
# would be looking through the stall it is trying to observe.
#
# Usage:
#     . lib/window-capture.ps1
#     Assert-WindowCaptureReady
#     $cap = Start-WindowCapture -Hwnd $h -OutDir $dir -Tag 'leg-1' -DurationMs 1500
#     ... do the thing worth filming ...
#     $film = Stop-WindowCapture $cap
#     $film.Frames | ForEach-Object { $_.file, $_.atMs }
#
# Or, to measure the instrument itself:
#     Measure-WindowCapture -Hwnd $h -DurationMs 1000

$script:WindowCaptureRoot = Join-Path $PSScriptRoot 'WindowCapture'
$script:WindowCaptureExe = Join-Path $script:WindowCaptureRoot `
    'bin\Release\net10.0-windows10.0.22621.0\win-x64\WindowCapture.exe'

# Build on demand, once. The tool is deliberately not in Ghostty.sln -- a
# product build should not pay for test tooling -- so something has to
# build it, and the harness that needs it is the honest owner of that.
function Assert-WindowCaptureReady {
    param([switch]$Force)
    if (-not $Force -and (Test-Path $script:WindowCaptureExe)) {
        return $script:WindowCaptureExe
    }
    Write-Host 'window-capture: building WindowCapture.exe (first use)'
    $log = & dotnet build (Join-Path $script:WindowCaptureRoot 'WindowCapture.csproj') `
        -c Release 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $script:WindowCaptureExe)) {
        $log | Select-Object -Last 20 | ForEach-Object { Write-Host $_ }
        throw 'HARNESS: WindowCapture.exe could not be built'
    }
    return $script:WindowCaptureExe
}

# Start filming and return once the camera is actually rolling. The tool
# prints READY after StartCapture, and waiting for it is the difference
# between filming a transition and filming the tail of one: the first
# frames are the ones a switch is judged on.
function Start-WindowCapture {
    param(
        [Parameter(Mandatory)][int64]$Hwnd,
        [Parameter(Mandatory)][string]$OutDir,
        [string]$Tag = 'frame',
        [int]$DurationMs = 1500,
        [int]$MaxFrames = 240,
        [switch]$Probe
    )
    $exe = Assert-WindowCaptureReady
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe
    foreach ($a in @('--hwnd', $Hwnd, '--ms', $DurationMs, '--max-frames', $MaxFrames, '--tag', $Tag)) {
        $psi.ArgumentList.Add([string]$a)
    }
    if ($Probe) { $psi.ArgumentList.Add('--probe') }
    else { $psi.ArgumentList.Add('--out'); $psi.ArgumentList.Add($OutDir) }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    $ready = $proc.StandardOutput.ReadLine()
    if ($ready -notlike 'READY*') {
        $err = $proc.StandardError.ReadToEnd()
        try { $proc.Kill($true) } catch { }
        throw ("HARNESS: the capture tool did not start (said '{0}'){1}" -f $ready, "`n$err")
    }
    # The tool's own clock at StartCapture. Subtract it from a frame's atMs
    # to get milliseconds since THIS function returned, which is the clock
    # the caller is about to start timing its own work on.
    $readyMs = 0.0
    $parts = $ready -split '\s+'
    if ($parts.Count -gt 1) { [void][double]::TryParse($parts[1], [ref]$readyMs) }
    return @{
        Proc = $proc; OutDir = $OutDir; Tag = $Tag
        Probe = [bool]$Probe; ReadyMs = $readyMs
    }
}

# Wait for the film to finish and hand back what it caught. The tool stops
# on its own clock, so this blocks for the remainder of the duration the
# caller asked for rather than cutting the capture short.
function Stop-WindowCapture {
    param([Parameter(Mandatory)]$Capture, [int]$TimeoutMs = 30000)
    $proc = $Capture.Proc
    if (-not $proc.WaitForExit($TimeoutMs)) {
        try { $proc.Kill($true) } catch { }
        throw 'HARNESS: the capture tool did not exit'
    }
    $summary = $proc.StandardOutput.ReadToEnd().Trim()
    $stderr = $proc.StandardError.ReadToEnd().Trim()
    if ($proc.ExitCode -ne 0) {
        throw ("HARNESS: the capture tool failed ({0}): {1}" -f $proc.ExitCode, $stderr)
    }

    $stats = @{}
    foreach ($pair in ($summary -replace '^SUMMARY\s+', '') -split '\s+') {
        $kv = $pair -split '=', 2
        if ($kv.Count -eq 2) { $stats[$kv[0]] = $kv[1] }
    }

    $frames = @()
    if (-not $Capture.Probe) {
        $index = Join-Path $Capture.OutDir ($Capture.Tag + '-index.json')
        if (Test-Path $index) {
            $frames = @(Get-Content $index -Raw | ConvertFrom-Json)
        }
    }
    # Every frame carries a second timestamp on the CALLER's clock, so a
    # film can be lined up against whatever else the caller was recording
    # without anyone having to guess the offset.
    foreach ($f in $frames) {
        Add-Member -InputObject $f -NotePropertyName 'sinceStartMs' `
            -NotePropertyValue ([Math]::Round($f.atMs - $Capture.ReadyMs, 1)) -Force
    }
    return [pscustomobject]@{
        Summary = $summary
        Stats   = $stats
        Frames  = $frames
        Fps     = [double]$stats['fps']
        Dropped = [int]$stats['dropped']
        OutDir  = $Capture.OutDir
        ReadyMs = $Capture.ReadyMs
    }
}

# Prove the instrument before trusting it. Reports the frame rate actually
# achieved against a live window and the mean GPU-to-CPU copy cost, which
# is the only part of the path that scales with region size.
function Measure-WindowCapture {
    param(
        [Parameter(Mandatory)][int64]$Hwnd,
        [int]$DurationMs = 1000
    )
    $cap = Start-WindowCapture -Hwnd $Hwnd -OutDir $env:TEMP -DurationMs $DurationMs `
        -MaxFrames 100000 -Probe
    return Stop-WindowCapture $cap
}
