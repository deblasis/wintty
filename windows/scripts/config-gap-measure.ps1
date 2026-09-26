<#
.SYNOPSIS
    Measure the absence window a save shape leaves at a path.

.DESCRIPTION
    Manual harness for issue #1158. The reload's deletion floor is 900ms
    (ConfigVanishConfirmer.DefaultFloor), and it is judged against the
    widest save window measured anywhere: 22ms on a local disk, held in
    ConfigVanishConfirmer.WidestMeasuredSaveWindow. The config file lives
    under the roaming profile, which folder redirection can put on an SMB
    share, and sync clients and security software hold saves in ways a
    local NTFS run never sees. This script measures the same quantity the
    floor was judged against, at any path, so the floor can be re-judged
    where the config actually lives.

    Five save shapes, because editors and writers reach the file in all
    of them and the recorded local measurement covered four:

      rename-create  Move the file away, then create a new file at the
                     path and delete the away copy. The local worst case
                     (22ms): libghostty's own starter write is the create.

      replacefile    Atomically swap a new file over the live one with
                     File.Replace (MoveFileTransacted-era editors, some
                     sync clients' conflict resolution).

      delete-rename  Delete the file, then move the away copy back onto
                     the path. 8.4ms locally.

      movefileex     Write a temp file, then move it over the live name
                     with overwrite. VS Code's atomic write. Left no
                     absence window at all locally.

      truncate       Truncate the live file to zero, hold, write the
                     content back. Not an absence: the window measured is
                     the file being PRESENT AND EMPTY, which is what a
                     non-atomic in-place save shows the loader (#1138).

    A sampler thread records the probe file's state (absent, empty, or
    present with content) in a tight loop while the main thread performs
    the saves. A window is one continuous stretch of absent (or of empty)
    samples, and the reported duration is that stretch on the sampler's
    monotonic clock. Transitions are recorded exactly; a 5ms heartbeat
    bounds the resolution of everything else.

    Per shape the run reports: windows seen, min, median, 95th percentile,
    max, and how many windows reached the deletion floor. Exit code 0 for
    a completed measurement. This script measures; it does not gate
    behaviour. config-save-race.ps1 is the one that gates.

.EXAMPLE
    pwsh -NoProfile -File windows/scripts/config-gap-measure.ps1

.EXAMPLE
    pwsh -NoProfile -File windows/scripts/config-gap-measure.ps1 -TargetPath "$env:OneDrive\wintty-gap-measure"

.EXAMPLE
    pwsh -NoProfile -File windows/scripts/config-gap-measure.ps1 -TargetPath Z:\wintty-gap-measure -Shape truncate -Iterations 300
#>
[CmdletBinding()]
param(
    # The directory the probe file lives in. Empty means a fresh folder
    # under the temp directory, which is the local baseline. Point it at a
    # synced folder or an SMB path to measure where a redirected profile
    # actually lives. The probe file is always removed; a directory this
    # script created is removed with it.
    [string]$TargetPath = '',

    # One save shape, or all five.
    [ValidateSet('all', 'rename-create', 'replacefile', 'delete-rename',
        'movefileex', 'truncate')]
    [string]$Shape = 'all',

    # Saves per shape. The recorded local measurement used 150.
    [int]$Iterations = 150,

    # How long the middle of the save is held. 0 measures the natural
    # visibility window of the transition alone; larger values study a
    # stalled writer. Only a run at 0 (or well under the floor) says
    # anything about the floor.
    [int]$HoldMs = 0,

    # The deletion floor, for the reached-floor count in the report.
    [int]$FloorMs = 900,

    [string]$ProbeName = 'config-gap-probe.tmp'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Iterations -lt 1) { throw '-Iterations has to be at least 1.' }

$createdDir = $false
if ($TargetPath -eq '') {
    $TargetPath = Join-Path ([System.IO.Path]::GetTempPath()) `
        ("wintty-gap-measure-" + (Get-Date -Format 'HHmmss'))
    $createdDir = $true
}
New-Item -ItemType Directory -Force -Path $TargetPath | Out-Null
$probe = Join-Path $TargetPath $ProbeName
$body = "font-size = 21`n"

# The sampler. One background thread per shape, recording (ticks, state)
# pairs on every state transition plus a 5ms heartbeat. States: 0 absent,
# 1 present with content, 2 present and empty. A fresh FileInfo per sample:
# an instance caches what it saw, and only a new one asks the disk.
if (-not ('Wintty.GapSampler' -as [type])) {
    Add-Type -TypeDefinition @'
namespace Wintty
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;

    public static class GapSampler
    {
        public static readonly List<long> Events = new List<long>(4096);
        public static volatile bool Stop;
        private static System.Threading.Thread _thread;

        // Started and joined from here so PowerShell never has to run
        // script on a thread with no runspace of its own.
        public static void Start(string path, bool withSize)
        {
            Stop = false;
            Events.Clear();
            _thread = new System.Threading.Thread(
                () => Run(path, withSize))
            {
                IsBackground = true
            };
            _thread.Start();
        }

        public static void Finish()
        {
            Stop = true;
            if (_thread != null) { _thread.Join(); }
        }

        public static void Run(string path, bool withSize)
        {
            var sw = Stopwatch.StartNew();
            var last = -1;
            var lastBeat = 0L;
            while (!Stop)
            {
                var st = 0;
                try
                {
                    var fi = new System.IO.FileInfo(path);
                    if (fi.Exists)
                    {
                        st = (withSize && fi.Length == 0) ? 2 : 1;
                    }
                }
                catch
                {
                    st = 0;
                }

                var t = sw.Elapsed.Ticks;
                if (st != last)
                {
                    Events.Add(t);
                    Events.Add(st);
                    last = st;
                }
                else if (t - lastBeat > 50000)
                {
                    Events.Add(t);
                    Events.Add(st);
                    lastBeat = t;
                }
            }

            Events.Add(sw.Elapsed.Ticks);
            Events.Add(last);
        }
    }
}
'@
}

# One save of the named shape, on the live probe file. Every shape ends
# with the probe present at the path with content.
function Invoke-SaveShape([string]$Name, [string]$Path, [int]$Hold, [string]$Text) {
    switch ($Name) {
        'rename-create' {
            $away = "$Path.away"
            [System.IO.File]::Move($Path, $away)
            if ($Hold -gt 0) { Start-Sleep -Milliseconds $Hold }
            [System.IO.File]::WriteAllText($Path, $Text)
            [System.IO.File]::Delete($away)
        }
        'replacefile' {
            $rep = "$Path.rep"
            $bak = "$Path.bak"
            [System.IO.File]::WriteAllText($rep, $Text)
            [System.IO.File]::Replace($rep, $Path, $bak)
            [System.IO.File]::Delete($bak)
        }
        'delete-rename' {
            $away = "$Path.away"
            [System.IO.File]::Delete($Path)
            if ($Hold -gt 0) { Start-Sleep -Milliseconds $Hold }
            [System.IO.File]::Move($away, $Path)
            # The shape consumed the away copy; put it back so the next
            # iteration has one to move.
            [System.IO.File]::Copy($Path, $away)
        }
        'movefileex' {
            $tmp = "$Path.tmp"
            [System.IO.File]::WriteAllText($tmp, $Text)
            if ($Hold -gt 0) { Start-Sleep -Milliseconds $Hold }
            # The move-over races any reader that holds the destination
            # without share-delete for an instant; that is a dropped
            # measurement, not a finding, so give it a few chances before
            # the iteration is dropped.
            for ($try = 1; ; $try++) {
                try {
                    [System.IO.File]::Move($tmp, $Path, $true)
                    break
                }
                catch [System.IO.IOException], [System.Management.Automation.MethodInvocationException] {
                    if ($try -ge 3) { throw }
                    Start-Sleep -Milliseconds 2
                }
            }
        }
        'truncate' {
            $stream = [System.IO.File]::Open(
                $Path,
                [System.IO.FileMode]::Truncate,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::ReadWrite)
            try {
                if ($Hold -gt 0) { Start-Sleep -Milliseconds $Hold }
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
        }
    }
}

# Windows seen for one state, from the flat (ticks, state) event list.
# A window is a transition into the state up to the next transition out;
# heartbeats only bound the sampling, so the duration is the transition
# pair, not the beat spacing.
function Get-Windows([long[]]$Events, [int]$State) {
    $found = [System.Collections.Generic.List[double]]::new()
    $start = -1L
    for ($i = 0; $i -lt $Events.Count; $i += 2) {
        $t = $Events[$i]
        $st = [int]$Events[$i + 1]
        if ($st -eq $State -and $start -lt 0) { $start = $t }
        elseif ($st -ne $State -and $start -ge 0) {
            $found.Add(($t - $start) / 10000.0)
            $start = -1L
        }
    }
    return ,$found
}

function Show-Stats([string]$Label, [System.Collections.Generic.List[double]]$Windows) {
    if ($Windows.Count -eq 0) {
        Write-Host ('  {0,-18}: no windows' -f $Label)
        return
    }
    $sorted = $Windows | Sort-Object
    $mid = [int][Math]::Floor(($sorted.Count - 1) / 2)
    $p95 = $sorted[[Math]::Min($sorted.Count - 1, [int][Math]::Ceiling($sorted.Count * 0.95) - 1)]
    $atFloor = @($Windows | Where-Object { $_ -ge $FloorMs }).Count
    Write-Host ('  {0,-18}: {1,4} windows  min {2,9:N3}  med {3,9:N3}  p95 {4,9:N3}  max {5,9:N3}  ms  (at/over the {6}ms floor: {7})' -f
        $Label, $Windows.Count, $sorted[0], $sorted[$mid], $p95, $sorted[$sorted.Count - 1], $FloorMs, $atFloor)
}

$shapes = if ($Shape -eq 'all') {
    'rename-create', 'replacefile', 'delete-rename', 'movefileex', 'truncate'
}
else { , $Shape }

Write-Host "target=$TargetPath"
Write-Host "iterations=$Iterations hold=${HoldMs}ms floor=${FloorMs}ms probe=$ProbeName"
$failedShapes = @()

foreach ($one in $shapes) {
    # The probe starts present with content. delete-rename keeps its away
    # copy beside it from here.
    [System.IO.File]::WriteAllText($probe, $body)
    if ($one -eq 'delete-rename') {
        $away = "$probe.away"
        if (Test-Path -LiteralPath $away) { [System.IO.File]::Delete($away) }
        [System.IO.File]::Copy($probe, $away)
    }

    [Wintty.GapSampler]::Start($probe, $true)
    Start-Sleep -Milliseconds 200

    $dropped = 0
    try {
        for ($i = 1; $i -le $Iterations; $i++) {
            # Present with content at the top of every iteration - but
            # only rewritten when actually missing or empty, because the
            # rewrite itself truncates and would show up as an empty
            # window belonging to no shape. Then a beat of quiet so
            # consecutive saves segment into their own windows.
            $probeInfo = [System.IO.FileInfo]::new($probe)
            if (-not $probeInfo.Exists -or $probeInfo.Length -eq 0) {
                [System.IO.File]::WriteAllText($probe, $body)
            }
            Start-Sleep -Milliseconds 40
            try {
                Invoke-SaveShape -Name $one -Path $probe -Hold $HoldMs -Text $body
            }
            catch {
                # One dropped save is a dropped measurement, not a
                # finding; a shape that cannot run at all gets caught
                # further down.
                $dropped++
            }
        }
    }
    catch {
        Write-Host "shape ${one}: the saves did not complete: $($_.Exception.Message)"
        $failedShapes += $one
    }

    [Wintty.GapSampler]::Finish()

    $events = [Wintty.GapSampler]::Events.ToArray()
    Write-Host "shape=$one dropped=$dropped"
    Show-Stats -Label 'absent' -Windows (Get-Windows $events 0)
    Show-Stats -Label 'present empty' -Windows (Get-Windows $events 2)
}

# Cleanup: the probe and its shape litter, and the directory if this run
# created it. The away copy too, whatever the last shape left.
foreach ($suffix in '', '.away', '.tmp', '.rep', '.bak') {
    $p = $probe + $suffix
    if (Test-Path -LiteralPath $p) { [System.IO.File]::Delete($p) }
}
if ($createdDir) { Remove-Item -LiteralPath $TargetPath -Force }
Write-Host 'RESULT: MEASURED'
exit 0
