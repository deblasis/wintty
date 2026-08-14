<#
.SYNOPSIS
    Reproduce the launch splash / single-instance race.

.DESCRIPTION
    With `windows-single-instance` on, a second launch is supposed to forward
    itself to the primary and exit without ever putting anything on screen.
    The launch splash is created before WinUI exists, so it goes up long
    before the single-instance gate runs -- and it is an opaque, full-size,
    topmost window. A secondary that shows one covers the primary's window,
    which is the user's actual terminal, until it reaches the gate and exits.

    This script launches two instances a few hundred milliseconds apart --
    close enough that the second one starts while the first is still in its
    pre-gate startup -- and samples the top-level window list throughout,
    looking for a splash window (class WinttySplash) owned by the secondary.
    It captures the primary's window rect alongside each sighting so the
    overlap is measurable rather than asserted.

    FAIL means the race reproduced: the secondary painted over the primary.

    Config is isolated. XDG_CONFIG_HOME is redirected to a scratch directory
    for the launched processes, so this never reads or writes the config
    file you actually use.

    The election namespace is NOT isolated, and cannot be: the mutex name is
    derived from the exe path. Any Wintty already running from the same
    binary owns it, so the script refuses to run while one exists rather
    than measure two launches that both forward to it.

    This demonstrates the defect; it does not certify its absence. Sampling
    is on the order of 30-40ms, so a splash shown for less than that would
    be missed.

.PARAMETER ExePath
    Wintty.exe to test. Defaults to the x64 Debug build.

.PARAMETER DelayMs
    Gap between the two launches. The race window is the primary's whole
    pre-gate startup (config load, logger factory, NO_COLOR resolution), so
    the default sits well inside it. Raise it to walk the window and find
    where the race closes.

.PARAMETER Iterations
    Launch pairs to run. The race is timing-dependent; one clean pass proves
    nothing.

.PARAMETER SecondaryFeatureOff
    Launch the second instance with `windows-single-instance` OFF, against a
    primary that has it on, and INVERT the expectation: that launch is an
    ordinary independent window and must show its splash.

    This is the other half of the defect. "Does the mutex exist" and "is
    single-instance on for this process" are different questions, and a
    primary holds its mutex for its whole lifetime however the config is
    edited afterwards. Anything answering the first question suppresses a
    splash that should have appeared.

.EXAMPLE
    ./windows/scripts/splash-single-instance-race.ps1 -Iterations 5

.EXAMPLE
    ./windows/scripts/splash-single-instance-race.ps1 -SecondaryFeatureOff
#>
[CmdletBinding()]
param(
    [string]$ExePath = "$PSScriptRoot/../Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe",
    [int]$DelayMs = 300,
    [int]$Iterations = 3,
    [int]$SampleMs = 25,
    [int]$TimeoutMs = 20000,
    [switch]$SecondaryFeatureOff
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) {
    throw "Wintty.exe not found at $ExePath. Build it first: just build-dll && just build-win"
}
$ExePath = (Resolve-Path $ExePath).Path

# A Wintty already running from this exe path owns the single-instance mutex,
# so BOTH launches below would forward to it and neither would ever show a
# splash. That reports PASS while measuring nothing, which is the worst thing a
# repro harness can do -- refuse instead.
# Path is unreadable for a process running elevated or as another user, and
# those are filtered out rather than assumed to match. They would own the mutex
# just the same, so a run that reports PASS with one of those around is still
# measuring nothing -- this catches the case that actually happens (your own
# Wintty, left open while working on Wintty), not every case.
$alreadyRunning = @(
    Get-Process -Name 'Wintty' -ErrorAction SilentlyContinue |
        Where-Object { try { $_.Path -eq $ExePath } catch { $false } })
if ($alreadyRunning.Count -gt 0) {
    throw ("$($alreadyRunning.Count) Wintty process(es) are already running from $ExePath " +
           "(pids: $($alreadyRunning.Id -join ', ')). They own the single-instance mutex for " +
           "this exe path, so both launches would forward to them and this run would measure " +
           "nothing. Close them first.")
}

# The window enumeration lives in C# rather than being driven from PowerShell:
# the callback is an argument to a synchronous P/Invoke, so the interop
# marshaller keeps it alive for the duration of the call. It is never stored,
# and never invoked after EnumWindows returns.
if (-not ('SplashProbe' -as [type])) {
    Add-Type -Language CSharp @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class SplashProbe
{
    public struct Win
    {
        public int Pid;
        public string Klass;
        public int Left, Top, Right, Bottom;
        public bool Visible;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lp);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, char[] buf, int max);
    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    public static Win[] TopLevel()
    {
        var list = new List<Win>();
        var buf = new char[256];
        EnumWindows((hwnd, lp) =>
        {
            int n = GetClassNameW(hwnd, buf, buf.Length);
            int pid;
            GetWindowThreadProcessId(hwnd, out pid);
            RECT r;
            GetWindowRect(hwnd, out r);
            list.Add(new Win
            {
                Pid = pid,
                Klass = n > 0 ? new string(buf, 0, n) : "",
                Left = r.L, Top = r.T, Right = r.R, Bottom = r.B,
                Visible = IsWindowVisible(hwnd),
            });
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

}
'@
}

$SplashClass = 'WinttySplash'

function Get-OverlapArea($a, $b) {
    $w = [Math]::Min($a.Right, $b.Right) - [Math]::Max($a.Left, $b.Left)
    $h = [Math]::Min($a.Bottom, $b.Bottom) - [Math]::Max($a.Top, $b.Top)
    if ($w -le 0 -or $h -le 0) { return 0 }
    return $w * $h
}

# Scratch config, one XDG root per role. Normally both roles point at the
# same "on" config; -SecondaryFeatureOff gives the second launch its own with
# the key off, which is what makes the two questions differ. The mutex name
# is derived from the exe path, not the config, so the two still contend for
# the same election.
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "wintty-splash-race-$PID"

function New-ScratchConfig([string]$name, [string]$value) {
    $root = Join-Path $scratch $name
    $dir = Join-Path $root 'wintty'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Set-Content -Path (Join-Path $dir 'config.wintty') -Encoding utf8 -Value @"
# Scratch config for splash-single-instance-race.ps1.
windows-single-instance = $value
"@
    return $root
}

$primaryXdg = New-ScratchConfig 'primary' 'true'
$secondaryXdg = if ($SecondaryFeatureOff) { New-ScratchConfig 'secondary' 'false' } else { $primaryXdg }

# With the feature off, the second launch is an ordinary independent window
# and SHOULD put its splash up. Suppressing it is the defect.
$expectSecondarySplash = [bool]$SecondaryFeatureOff

# Process objects, not PIDs. Start-Process -PassThru returns a Process holding
# an open handle, and an open handle is what stops Windows recycling the PID --
# a forwarded secondary exits within a second, so a stored PID could name an
# unrelated process by the time this runs.
$launched = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Stop-Launched {
    foreach ($p in $launched) {
        try {
            if (-not $p.HasExited) { $p.Kill() }
            # Kill is asynchronous. Without the wait, a primary still unwinding
            # keeps holding the mutex, and the next iteration's "primary"
            # elects itself secondary against a dying process -- which the
            # harness would score as the defect.
            [void]$p.WaitForExit(10000)
        } catch {
            # Already gone: a forwarded secondary exits on its own, and that
            # is the outcome this script is measuring.
        }
    }
    $launched.Clear()
}

$results = [System.Collections.Generic.List[object]]::new()
$previousXdg = $env:XDG_CONFIG_HOME

try {
    Write-Host "exe    : $ExePath"
    Write-Host "primary  config: $primaryXdg\wintty\config.wintty (windows-single-instance = true)"
    Write-Host "secondary config: $secondaryXdg\wintty\config.wintty (windows-single-instance = $(if ($SecondaryFeatureOff) { 'false' } else { 'true' }))"
    Write-Host "expect : secondary $(if ($expectSecondarySplash) { 'SHOWS' } else { 'shows NO' }) splash"
    Write-Host "delay  : ${DelayMs}ms between launches, $Iterations iteration(s)`n"

    for ($i = 1; $i -le $Iterations; $i++) {
        # Waits for exit, so the previous pair's mutex is genuinely gone and
        # this iteration elects from scratch.
        Stop-Launched

        # Set per launch: a child inherits the environment as it stands when
        # it is created, and the two roles can need different configs.
        $env:XDG_CONFIG_HOME = $primaryXdg
        $primary = Start-Process -FilePath $ExePath -PassThru
        $launched.Add($primary)

        Start-Sleep -Milliseconds $DelayMs

        $env:XDG_CONFIG_HOME = $secondaryXdg
        $secondary = Start-Process -FilePath $ExePath -PassThru
        $launched.Add($secondary)
        $t0 = [System.Diagnostics.Stopwatch]::StartNew()

        $sightings = [System.Collections.Generic.List[object]]::new()
        $secondaryExitedAt = $null
        # Whether the launch we called "primary" ever actually became one. If it
        # crashed, or lost the election to the other launch, then the secondary
        # legitimately owns the session and everything below describes a
        # different experiment.
        $primaryWindowSeen = $false

        while ($t0.ElapsedMilliseconds -lt $TimeoutMs) {
            $windows = [SplashProbe]::TopLevel()

            # The primary's real window, for the overlap measurement. Matched
            # by PID and visibility rather than by class, so this does not
            # depend on which WinUI class name the shell happens to use.
            $primaryWindow = $windows |
                Where-Object { $_.Pid -eq $primary.Id -and $_.Visible -and $_.Klass -ne $SplashClass } |
                Sort-Object { ($_.Right - $_.Left) * ($_.Bottom - $_.Top) } -Descending |
                Select-Object -First 1
            if ($null -ne $primaryWindow) { $primaryWindowSeen = $true }

            foreach ($w in $windows) {
                if ($w.Klass -ne $SplashClass) { continue }
                if ($w.Pid -ne $secondary.Id) { continue }
                if (-not $w.Visible) { continue }

                $overlap = 0
                $primaryRect = $null
                if ($null -ne $primaryWindow) {
                    $overlap = Get-OverlapArea $w $primaryWindow
                    $primaryRect = "$($primaryWindow.Left),$($primaryWindow.Top) $($primaryWindow.Right - $primaryWindow.Left)x$($primaryWindow.Bottom - $primaryWindow.Top)"
                }

                $sightings.Add([pscustomobject]@{
                    AtMs        = [int]$t0.ElapsedMilliseconds
                    SplashRect  = "$($w.Left),$($w.Top) $($w.Right - $w.Left)x$($w.Bottom - $w.Top)"
                    PrimaryRect = $primaryRect
                    OverlapPx   = $overlap
                })
            }

            $secondary.Refresh()
            if ($secondary.HasExited -and $null -eq $secondaryExitedAt) {
                $secondaryExitedAt = [int]$t0.ElapsedMilliseconds
            }

            # Not "stop as soon as the secondary exits". A forwarded secondary
            # exits at the gate, which runs before the primary has a window --
            # so stopping there would leave PrimaryWindowSeen false and discard
            # the iteration as inconclusive. Both facts have to be in hand.
            if ($primaryWindowSeen -and
                ($null -ne $secondaryExitedAt -or
                 ($expectSecondarySplash -and $sightings.Count -gt 0))) {
                break
            }

            Start-Sleep -Milliseconds $SampleMs
        }

        $covered = @($sightings | Where-Object { $_.OverlapPx -gt 0 })
        $firstSeen = $null
        $lastSeen = $null
        if ($sightings.Count -gt 0) {
            $firstSeen = $sightings[0].AtMs
            $lastSeen = $sightings[$sightings.Count - 1].AtMs
        }
        $maxOverlap = 0
        if ($covered.Count -gt 0) {
            $maxOverlap = ($covered | Measure-Object OverlapPx -Maximum).Maximum
        }

        $result = [pscustomobject]@{
            Iteration       = $i
            PrimaryPid      = $primary.Id
            SecondaryPid    = $secondary.Id
            SecondaryExitMs = $secondaryExitedAt
            SplashSightings = $sightings.Count
            FirstSeenMs     = $firstSeen
            LastSeenMs      = $lastSeen
            CoveredPrimary  = $covered.Count -gt 0
            MaxOverlapPx    = $maxOverlap
            Inconclusive    = -not $primaryWindowSeen
        }
        $results.Add($result)

        $sawSplash = $result.SplashSightings -gt 0
        if ($result.Inconclusive) {
            # Without a primary window there was no primary to paint over, and a
            # splash from the other launch is correct behaviour rather than the
            # defect. Scoring it either way would be reporting an experiment
            # that did not run.
            $verdict = 'INCONCLUSIVE (the primary never showed a window)'
        }
        elseif ($expectSecondarySplash) {
            $verdict = if ($sawSplash) { 'ok (splash shown as it should be)' }
                       else { 'REPRODUCED (splash suppressed for an independent launch)' }
        }
        else {
            $verdict = 'clean'
            if ($result.CoveredPrimary) { $verdict = 'REPRODUCED (covered the primary)' }
            elseif ($sawSplash) { $verdict = 'REPRODUCED (splash shown, overlap not measured)' }
        }

        $exitText = 'never (timeout)'
        if ($null -ne $secondaryExitedAt) { $exitText = "${secondaryExitedAt}ms" }

        Write-Host ("iteration {0}: {1} -- secondary pid {2}, {3} sighting(s), exited at {4}" -f `
            $i, $verdict, $secondary.Id, $result.SplashSightings, $exitText)

        # Prefer the samples that actually measured an overlap. The earliest
        # sightings usually predate the primary's own window, so they carry no
        # rect to compare against and say the least about the defect.
        $shown = $covered
        if ($shown.Count -eq 0) { $shown = $sightings }
        if ($shown.Count -gt 0) {
            $shown | Select-Object -First 6 | Format-Table -AutoSize | Out-String | Write-Host
            if ($shown.Count -gt 6) {
                Write-Host ("  ... {0} more sample(s)`n" -f ($shown.Count - 6))
            }
        }
    }
}
finally {
    Stop-Launched
    $env:XDG_CONFIG_HOME = $previousXdg
    Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
}

Write-Host "`n=== summary ==="
$results | Format-Table -AutoSize | Out-String | Write-Host

$scored = @($results | Where-Object { -not $_.Inconclusive })
$skipped = $results.Count - $scored.Count
if ($skipped -gt 0) {
    Write-Host "$skipped/$Iterations iteration(s) were INCONCLUSIVE and are not scored." -ForegroundColor Yellow
}
if ($scored.Count -eq 0) {
    Write-Host "FAIL: no iteration produced a usable measurement." -ForegroundColor Red
    exit 1
}

if ($expectSecondarySplash) {
    $bad = @($scored | Where-Object { $_.SplashSightings -eq 0 })
    if ($bad.Count -gt 0) {
        Write-Host "FAIL: an independent launch was denied its splash in $($bad.Count)/$($scored.Count) scored iteration(s)." -ForegroundColor Red
        exit 1
    }
    Write-Host "PASS: the independent launch showed its splash in all $($scored.Count) scored iteration(s)." -ForegroundColor Green
    exit 0
}

$bad = @($scored | Where-Object { $_.SplashSightings -gt 0 })
if ($bad.Count -gt 0) {
    Write-Host "FAIL: the secondary put a splash on screen in $($bad.Count)/$($scored.Count) scored iteration(s)." -ForegroundColor Red
    exit 1
}

Write-Host "PASS: no secondary showed a splash in any of $($scored.Count) scored iteration(s)." -ForegroundColor Green
exit 0
