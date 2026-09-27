#requires -Version 7
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

    SCOPE, stated precisely: the sampler polls the probe's existence and
    length. That is stat space. The reload path does not poll; it opens
    the file, and its verdicts come from opens (existence-by-open is the
    shipped mechanism; the loader answers Absent/Unreadable/Loaded from
    the open, not from a stat). So these numbers compare save shapes with
    each other and targets with each other; they are NOT claims about
    which windows the reload path would settle as deletions. A stat
    listing can disagree with an open in both directions on an SMB share
    (negative-name cache; placeholders that stat fine and fail to open).

    Five save shapes, because editors and writers reach the file in all
    of them and the recorded local measurement covered four:

      rename-create  Move the file away, then create a new file at the
                     path and delete the away copy. The local worst case
                     (22ms): libghostty's own starter write is the create.
                     The create also truncates before it writes, so this
                     shape's present-empty row is the create call's own
                     sliver: expect one near-zero window per save,
                     stall-inflated on a slow target.

      replacefile    Swap a new file over the live one with File.Replace
                     (MoveFileTransacted-era editors, some sync clients'
                     conflict resolution). Atomic where ReplaceFile is -
                     which the certified SMB run showed is not everywhere:
                     it measured multi-second absences on this shape.

      delete-rename  Delete the file, then move the away copy back onto
                     the path. 8.4ms locally.

      movefileex     Write a temp file, then move it over the live name
                     with overwrite. VS Code's atomic write. Left no
                     absence window at all locally - and none is possible:
                     a rename-over never unlinks the live name, so this
                     row cannot come out any other way on any target.

      truncate       Truncate the live file to zero, hold, write the
                     content back. Not an absence: the window measured is
                     the file being PRESENT AND EMPTY, which is what a
                     non-atomic in-place save shows the loader (#1138).

    A sampler thread records the probe file's state in a tight loop while
    the main thread performs the saves. States: 0 absent, 1 present with
    content, 2 present and empty, 3 QUERY ERROR. The probe asks
    File.GetAttributes, which throws where FileInfo.Exists would silently
    answer false: not-found maps to absence, any other failure maps to
    state 3, and state 3 is bridged as unobserved time rather than read
    as a transition, so a dead share cannot masquerade as absence. Every
    sample's probe-call latency is recorded; the shape's report carries
    the sampler's max and mean call latency and the loop-gap percentiles,
    because a slow sampler cannot see short windows and lags long ones,
    and a number without its cadence is not a number.

    Each iteration is bracketed (an iteration mark before each save), so
    the analysis can tell one save's window from two saves' windows that
    merged across the inter-save beat. Two views are reported:

      per-save worst  the longest absent (or empty) stretch CLIPPED to a
                      single iteration, counted over MEASURED iterations
                      only (a dropped save's debris is not a save). This
                      is the number the floor question needs: did one
                      save leave the path in this state for at least the
                      floor. At-floor entries name their iteration.

      raw streaks     uncut stretches across the whole shape run, with
                      the count of streaks crossing an iteration mark
                      (those merge saves; their length is not one save's
                      window). At-floor streaks carry their observation
                      count, because a 900ms window seen twice is not the
                      same evidence as one seen two hundred times.

    Windows BEGINNING inside a harness interval (the per-iteration
    refresh write, the recovery of a dropped save's away copy) are
    excluded and the exclusion count is printed. A window that instead
    ENDS at the harness refresh - the recovery absence a dropped save
    leaves - is counted: the path really was absent, and a loader would
    have seen it.

    Per shape the run reports: saves attempted/measured/dropped, sampler
    cadence, per-save worst and raw-streak stats for absent and for
    present-empty, query-error stretches, and how many entries reached
    the deletion floor. Exit code 0 for a complete measurement; nonzero
    if any shape could not be measured (a shape whose saves all dropped
    counts as failed, and its stats are not a result). This script
    measures; it does not gate behaviour. config-save-race.ps1 is the
    one that gates.

.EXAMPLE
    pwsh -NoProfile -File windows/scripts/config-gap-measure.ps1

.EXAMPLE
    pwsh -NoProfile -File windows/scripts/config-gap-measure.ps1 -TargetPath "$env:OneDrive\wintty-gap-measure"

.EXAMPLE
    pwsh -NoProfile -File windows/scripts/config-gap-measure.ps1 -TargetPath Z:\wintty-gap-measure -Shape truncate -Iterations 300 -DumpCsv C:\temp\gap
#>
[CmdletBinding()]
param(
    # The directory the probe file lives in. Empty means a fresh folder
    # under the temp directory, which is the local baseline. Point it at a
    # synced folder or an SMB path to measure where a redirected profile
    # actually lives. The probe file is always removed; a directory this
    # script created is removed with it. A pre-existing probe or litter
    # file makes the script refuse rather than overwrite.
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
    # anything about the floor. Analysis cost grows with the event count
    # a hold produces, so a -HoldMs-heavy run's per-shape analysis phase
    # can take minutes of CPU after the saves finish.
    [int]$HoldMs = 0,

    # The deletion floor, for the reached-floor count in the report.
    [int]$FloorMs = 900,

    # The probe file's name. Plain file name only: no path, no '..'.
    [ValidatePattern('^[^\\/]+$')]
    [string]$ProbeName = 'config-gap-probe.tmp',

    # Optional path prefix for per-shape CSV dumps: the raw sampler
    # events (ticks,state pairs, headed by a line naming the shape and
    # target), the iteration marks (-marks.csv: iteration,tick,dropped),
    # and the harness intervals (-harness.csv: start,end). The marks and
    # intervals are what the per-save clipping and the harness exclusion
    # are computed from; with them in the dump, every number in the
    # report is re-derivable offline. Off unless given. The dump is the
    # one output this script overwrites without asking, and its
    # directory has to exist.
    [string]$DumpCsv = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Iterations -lt 1) { throw '-Iterations has to be at least 1.' }
if ($ProbeName -in '.', '..' -or $ProbeName -match '[\\/]') {
    throw '-ProbeName has to be a plain file name.'
}

$createdDir = $false
if ($TargetPath -eq '') {
    # PID in the name: two runs started in the same second must not
    # adopt each other's directory. $createdDir is set only when this
    # run actually creates the folder below.
    $TargetPath = Join-Path ([System.IO.Path]::GetTempPath()) `
        ("wintty-gap-measure-$PID-" + (Get-Date -Format 'HHmmss'))
}
if (-not (Test-Path -LiteralPath $TargetPath)) {
    New-Item -ItemType Directory -Force -Path $TargetPath | Out-Null
    $createdDir = $true
}
$probe = Join-Path $TargetPath $ProbeName
$body = "font-size = 21`n"

# Refuse to touch a probe or litter name that is already there: the shapes
# overwrite and delete by name, so first contact with a pre-existing file
# would be an overwrite of something this run did not create.
foreach ($suffix in '', '.away', '.tmp', '.rep', '.bak') {
    $p = $probe + $suffix
    if (Test-Path -LiteralPath $p) {
        throw "refusing to run: $p already exists; remove it or choose another -ProbeName"
    }
}

# The sampler. One background thread per shape, recording (ticks, state)
# pairs on every state transition plus a 5ms heartbeat. States: 0 absent,
# 1 present with content, 2 present and empty, 3 query error. A fresh
# FileInfo per sample: an instance caches what it saw, and only a new one
# asks the disk. Every sample's probe-call latency is kept (max, total,
# count) and reported, because a window measured by a sampler this slow
# says nothing without its cadence. The main thread reads Now() to bracket
# its own writes as harness intervals and to mark iteration boundaries.
if (-not ('Wintty.GapSampler' -as [type])) {
    Add-Type -TypeDefinition @'
namespace Wintty
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;

    public static class GapSampler
    {
        public static readonly List<long> Events = new List<long>(8192);
        public static readonly List<long> Harness = new List<long>(64);
        public static readonly List<long> IterationMarks = new List<long>(512);
        public static readonly List<long> Gaps = new List<long>(65536);
        public static volatile bool Stop;
        public static long MaxLatency;
        public static long TotalLatency;
        public static long Samples;
        public static long Errors;
        private static System.Threading.Thread _thread;
        private static Stopwatch _clock = new Stopwatch();

        // Started and joined from here so PowerShell never has to run
        // script on a thread with no runspace of its own.
        public static void Start(string path, bool withSize)
        {
            Stop = false;
            Events.Clear();
            Harness.Clear();
            IterationMarks.Clear();
            Gaps.Clear();
            MaxLatency = 0;
            TotalLatency = 0;
            Samples = 0;
            Errors = 0;
            _clock = Stopwatch.StartNew();
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

        // The sampler's own timebase, for the main thread's brackets and
        // iteration marks. Same clock the events are stamped on.
        public static long Now() => _clock.Elapsed.Ticks;

        public static void Run(string path, bool withSize)
        {
            var last = -1;
            var lastBeat = 0L;
            var lastT = 0L;
            var latency = Stopwatch.StartNew();
            while (!Stop)
            {
                var st = 0;
                latency.Restart();
                try
                {
                    // Ask with a primitive that THROWS on failure.
                    // FileInfo.Exists swallows I/O errors and answers
                    // false, which would record a dead share - the exact
                    // failure class this state exists for - as absence.
                    // Not-found maps to absence in the catches; any
                    // other throw is a query error.
                    var attrs = System.IO.File.GetAttributes(path);
                    var fi = new System.IO.FileInfo(path);
                    if (fi.Exists)
                    {
                        st = (withSize && fi.Length == 0) ? 2 : 1;
                    }
                }
                catch (System.IO.FileNotFoundException)
                {
                    st = 0;
                }
                catch (System.IO.DirectoryNotFoundException)
                {
                    st = 0;
                }
                catch
                {
                    // A probe call that failed is a query error, not an
                    // absence. Reported as its own state and counted.
                    st = 3;
                    Errors++;
                }
                var callTicks = latency.ElapsedTicks;
                if (callTicks > MaxLatency) MaxLatency = callTicks;
                TotalLatency += callTicks;
                Samples++;

                var t = _clock.Elapsed.Ticks;
                if (lastT != 0) { Gaps.Add(t - lastT); }
                lastT = t;
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

            Events.Add(_clock.Elapsed.Ticks);
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
            # A stale away copy here is this run's own debris from a
            # dropped iteration (the run-start guard proved the name
            # absent); if it survived, every later Move would throw and
            # collapse the shape into drops.
            if ([System.IO.File]::Exists($away)) { [System.IO.File]::Delete($away) }
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

# Put the probe back to present-with-content, bracketed as harness time so
# the recovery write's own windows are excluded from the shape's stats.
function Invoke-Refresh([string]$Path, [string]$Text) {
    $t0 = [Wintty.GapSampler]::Now()
    [System.IO.File]::WriteAllText($Path, $Text)
    $t1 = [Wintty.GapSampler]::Now()
    [Wintty.GapSampler]::Harness.Add($t0)
    [Wintty.GapSampler]::Harness.Add($t1)
}

# Stretches of one state, from the flat (ticks, state) event list. A
# stretch is a transition into the state up to the next transition out; a
# stretch still open at the end of the run closes at the final event
# (without this, the last window of a run is silently lost). Windows that
# BEGIN inside a harness interval are excluded (counted as harness), and
# so are windows of the query-error state's neighbours that begin inside a
# state-3 stretch, because a query error splits one true stretch into
# pieces; those pieces are re-joined across state-3 stretches for the
# state the caller asked about. Returns an object with Windows (the
# lengths), Starts and Closes (the window boundaries, for the merge
# check), Obs (observation counts per window - a window resting on two
# samples is weaker evidence than one resting on two hundred), and
# Harness (the excluded count) - lists returned bare would unroll to
# nothing when empty and take the caller's .Count down with them.
function Get-Windows([long[]]$Events, [int]$State, [long[]]$Harness) {
    $found = [System.Collections.Generic.List[double]]::new()
    $starts = [System.Collections.Generic.List[long]]::new()
    $closes = [System.Collections.Generic.List[long]]::new()
    $obs = [System.Collections.Generic.List[int]]::new()
    $harnessCount = 0
    $start = -1L
    $last = -1L
    $obsCount = 0
    for ($i = 0; $i -lt $Events.Count; $i += 2) {
        $t = $Events[$i]
        $st = [int]$Events[$i + 1]
        $last = $t
        # A state-3 stretch is unobserved time, not a transition: bridge it.
        # It is not a sighting either, so it does not count for Obs.
        if ($st -eq 3) {
            if ($start -ge 0) { continue }
        }
        $obsCount++
        if ($st -eq $State -and $start -lt 0) { $start = $t; $obsCount = 0 }
        elseif ($st -ne $State -and $start -ge 0) {
            $inHarness = $false
            for ($h = 0; $h -lt $Harness.Count; $h += 2) {
                if ($start -ge $Harness[$h] -and $start -lt $Harness[$h + 1]) {
                    $inHarness = $true
                    $harnessCount++
                    break
                }
            }
            if (-not $inHarness) {
                $found.Add(($t - $start) / 10000.0)
                $starts.Add($start)
                $closes.Add($t)
                $obs.Add($obsCount)
            }
            $start = -1L
        }
    }
    # A stretch still open at the end of the run: close at the last event.
    if ($start -ge 0) {
        $inHarness = $false
        for ($h = 0; $h -lt $Harness.Count; $h += 2) {
            if ($start -ge $Harness[$h] -and $start -lt $Harness[$h + 1]) {
                $inHarness = $true
                $harnessCount++
                break
            }
        }
        if (-not $inHarness) {
            $found.Add(($last - $start) / 10000.0)
            $starts.Add($start)
            $closes.Add($last)
            $obs.Add($obsCount)
        }
    }
    return [pscustomobject]@{
        Windows = $found; Starts = $starts; Closes = $closes
        Obs = $obs; Harness = $harnessCount
    }
}

# The longest stretch of one state CLIPPED to a single iteration, per
# MEASURED iteration (a dropped save's debris is not a save; its mark
# still brackets the timeline, but it contributes no entry). Returns an
# object with Worst (one entry per measured iteration: the longest
# stretch inside it, 0 if the state never showed), Iter (the 1-based
# iteration numbers of those entries, so an at-floor entry can be traced
# to its save), and ErrorIters (measured iterations containing
# query-error time; their entries span unobserved time).
function Get-PerSaveWorst([long[]]$Events, [int]$State, [long[]]$Marks, [long]$End, [int[]]$DroppedIters) {
    $worsts = [System.Collections.Generic.List[double]]::new()
    $iters = [System.Collections.Generic.List[int]]::new()
    $errorIters = 0
    for ($m = 0; $m -lt $Marks.Count; $m++) {
        $iterNum = $m + 1
        if ($DroppedIters -contains $iterNum) { continue }
        $from = $Marks[$m]
        $to = if ($m -lt $Marks.Count - 1) { $Marks[$m + 1] } else { $End }
        $worst = 0L
        $start = -1L
        $hadError = $false
        for ($i = 0; $i -lt $Events.Count; $i += 2) {
            $t = $Events[$i]
            if ($t -ge $to) { break }
            $st = [int]$Events[$i + 1]
            if ($t -lt $from) { continue }
            if ($st -eq 3) { $hadError = $true; continue }
            if ($st -eq $State -and $start -lt 0) { $start = $t }
            elseif ($st -ne $State -and $start -ge 0) {
                $len = $t - $start
                if ($len -gt $worst) { $worst = $len }
                $start = -1L
            }
        }
        if ($start -ge 0) {
            $close = if ($to -lt $End) { $to } else { $End }
            $len = $close - $start
            if ($len -gt $worst) { $worst = $len }
        }
        if ($hadError) { $errorIters++ }
        $worsts.Add($worst / 10000.0)
        $iters.Add($iterNum)
    }
    return [pscustomobject]@{
        Worst = $worsts; Iter = $iters; ErrorIters = $errorIters
    }
}

function Show-Stats([string]$Label, [System.Collections.Generic.List[double]]$Windows, [int]$FloorMs) {
    if ($Windows.Count -eq 0) {
        Write-Host ('    {0,-16}: none' -f $Label)
        return
    }
    # @(): with exactly one window, Sort-Object's output unrolls to a
    # bare scalar and .Count dies under StrictMode.
    $sorted = @($Windows | Sort-Object)
    # Median of an even count is the mean of the two middle elements.
    $mid = [int][Math]::Floor(($sorted.Count - 1) / 2)
    $median = if ($sorted.Count % 2 -eq 1) {
        $sorted[$mid]
    } else {
        ($sorted[$mid] + $sorted[$mid + 1]) / 2.0
    }
    $p95 = $sorted[[Math]::Min($sorted.Count - 1, [int][Math]::Ceiling($sorted.Count * 0.95) - 1)]
    $atFloor = @($Windows | Where-Object { $_ -ge $FloorMs }).Count
    Write-Host ('    {0,-16}: {1,4}  min {2,9:N3}  med {3,9:N3}  p95 {4,9:N3}  max {5,9:N3}  ms  (at/over the {6}ms floor: {7})' -f
        $Label, $Windows.Count, $sorted[0], $median, $p95, $sorted[$sorted.Count - 1], $FloorMs, $atFloor)
}

$shapes = if ($Shape -eq 'all') {
    'rename-create', 'replacefile', 'delete-rename', 'movefileex', 'truncate'
}
else { , $Shape }

Write-Host "target=$TargetPath"
Write-Host "iterations=$Iterations hold=${HoldMs}ms floor=${FloorMs}ms probe=$ProbeName"
$failedShapes = @()

# Everything from here to the cleanup below is inside one try/finally: a
# shape failure is caught per shape, but a failure anywhere else must
# still release the sampler thread and the probe litter.
try {
foreach ($one in $shapes) {
    try {
        # The probe starts present with content. delete-rename keeps its
        # away copy beside it from here. The pre-existence guard already
        # refused any stale litter, so these creates are this run's own.
        [System.IO.File]::WriteAllText($probe, $body)
        if ($one -eq 'delete-rename') {
            [System.IO.File]::Copy($probe, "$probe.away")
        }

        [Wintty.GapSampler]::Start($probe, $true)
        Start-Sleep -Milliseconds 200

        $dropped = 0
        $droppedIters = [System.Collections.Generic.List[int]]::new()
        for ($i = 1; $i -le $Iterations; $i++) {
            # Present with content at the top of every iteration - but
            # only rewritten when actually missing or empty (usually a
            # dropped save's debris), because the rewrite itself truncates
            # and its window is harness time, not the shape's. Then a beat
            # of quiet so consecutive saves segment into their own
            # windows.
            $probeInfo = [System.IO.FileInfo]::new($probe)
            if (-not $probeInfo.Exists -or $probeInfo.Length -eq 0) {
                Invoke-Refresh -Path $probe -Text $body
            }
            # delete-rename's away copy is the shape's fuel: one failed
            # restore copy would leave every later iteration throwing
            # before it touches the probe. Re-create it, as harness time,
            # if a drop consumed it.
            if ($one -eq 'delete-rename' -and
                -not [System.IO.File]::Exists("$probe.away")) {
                $t0 = [Wintty.GapSampler]::Now()
                [System.IO.File]::Copy($probe, "$probe.away")
                $t1 = [Wintty.GapSampler]::Now()
                [Wintty.GapSampler]::Harness.Add($t0)
                [Wintty.GapSampler]::Harness.Add($t1)
            }
            Start-Sleep -Milliseconds 40
            [Wintty.GapSampler]::IterationMarks.Add([Wintty.GapSampler]::Now())
            try {
                Invoke-SaveShape -Name $one -Path $probe -Hold $HoldMs -Text $body
            }
            catch {
                # One dropped save is a dropped measurement, not a
                # finding; the next iteration's refresh repairs the probe.
                $dropped++
                $droppedIters.Add($i)
            }
        }

        [Wintty.GapSampler]::Finish()

        # Shape tail work (analysis, dump) inside the same try, so a
        # failure here still marks the shape failed.
        $events = [Wintty.GapSampler]::Events.ToArray()
        $harness = [Wintty.GapSampler]::Harness.ToArray()
        $marks = [Wintty.GapSampler]::IterationMarks.ToArray()
        $end = $events[$events.Count - 2]
        $measured = $Iterations - $dropped

        Write-Host "shape=$one saves=$Iterations measured=$measured dropped=$dropped"
        if ($measured -eq 0) {
            Write-Host "shape ${one}: nothing measured"
            $failedShapes += $one
            continue
        }

        $maxMs = [Wintty.GapSampler]::MaxLatency / 10000.0
        $meanMs = if ([Wintty.GapSampler]::Samples -gt 0) {
            [Wintty.GapSampler]::TotalLatency / [double][Wintty.GapSampler]::Samples / 10000.0
        } else { 0.0 }
        Write-Host ('  sampler: max call {0:N3} ms, mean {1:N3} ms over {2} samples ({3} query errors)' -f
            $maxMs, $meanMs, [Wintty.GapSampler]::Samples, [Wintty.GapSampler]::Errors)
        $gaps = [Wintty.GapSampler]::Gaps.ToArray()
        if ($gaps.Count -gt 0) {
            $sortedGaps = @($gaps | Sort-Object)
            $gapP95 = $sortedGaps[[Math]::Min($sortedGaps.Count - 1, [int][Math]::Ceiling($sortedGaps.Count * 0.95) - 1)] / 10000.0
            $gapP99 = $sortedGaps[[Math]::Min($sortedGaps.Count - 1, [int][Math]::Ceiling($sortedGaps.Count * 0.99) - 1)] / 10000.0
            $gapMax = $sortedGaps[$sortedGaps.Count - 1] / 10000.0
            Write-Host ('  loop gaps (sample to sample): p95 {0:N3}  p99 {1:N3}  max {2:N3}  ms - a stalled sampler cannot see short windows and lags long ones' -f
                $gapP95, $gapP99, $gapMax)
        }

        foreach ($state in 0, 2) {
            $name = if ($state -eq 0) { 'absent' } else { 'present empty' }
            $rawResult = Get-Windows $events $state $harness
            $raw = $rawResult.Windows
            $harnessWindows = $rawResult.Harness
            $psResult = Get-PerSaveWorst $events $state $marks $end $droppedIters.ToArray()
            $perSave = $psResult.Worst
            $errorIters = $psResult.ErrorIters
            # A raw streak is a merged one when the stretch it spans
            # crosses an iteration mark: two saves' windows fused into
            # one streak. (A streak merely LONGER than the longest
            # clipped entry is not the test - merged streaks can sit
            # below an unmerged max.)
            $spanning = 0
            for ($w = 0; $w -lt $rawResult.Windows.Count; $w++) {
                foreach ($mk in $marks) {
                    if ($mk -gt $rawResult.Starts[$w] -and $mk -lt $rawResult.Closes[$w]) {
                        $spanning++
                        break
                    }
                }
            }
            Write-Host "  $name"
            Show-Stats -Label 'per-save worst' -Windows $perSave -FloorMs $FloorMs
            $floorAt = [System.Collections.Generic.List[int]]::new()
            for ($w = 0; $w -lt $perSave.Count; $w++) {
                if ($perSave[$w] -ge $FloorMs) { $floorAt.Add($psResult.Iter[$w]) }
            }
            if ($floorAt.Count -gt 0) {
                Write-Host ('    {0,-16}: iterations {1}' -f 'at the floor', ($floorAt -join ', '))
            }
            Show-Stats -Label 'raw streaks' -Windows $raw -FloorMs $FloorMs
            $floorObs = [System.Collections.Generic.List[int]]::new()
            for ($w = 0; $w -lt $raw.Count; $w++) {
                if ($raw[$w] -ge $FloorMs) { $floorObs.Add($rawResult.Obs[$w]) }
            }
            if ($floorObs.Count -gt 0) {
                Write-Host ('    {0,-16}: {1} (sampler observations backing each at-floor streak)' -f 'floor obs', ($floorObs -join ', '))
            }
            if ($spanning -gt 0) {
                Write-Host ('    {0,-16}: {1} raw streaks span iterations (merged saves; not one save''s window)' -f 'merged', $spanning)
            }
            if ($harnessWindows -gt 0) {
                Write-Host ('    {0,-16}: {1} windows began in harness intervals (excluded)' -f 'harness', $harnessWindows)
            }
            if ($errorIters -gt 0) {
                Write-Host ('    {0,-16}: {1} measured iterations span unobserved time (query errors); their worst is a lower bound' -f 'unobserved', $errorIters)
            }
        }

        if ($DumpCsv -ne '') {
            $evLines = [System.Collections.Generic.List[string]]::new()
            $evLines.Add("shape=$one target=$TargetPath")
            for ($i = 0; $i -lt $events.Count; $i += 2) {
                $evLines.Add("$($events[$i]),$($events[$i + 1])")
            }
            [System.IO.File]::WriteAllLines("$DumpCsv-$one-events.csv", $evLines)
            $mkDropped = [System.Collections.Generic.HashSet[int]]::new([int[]]$droppedIters.ToArray())
            $mkLines = [System.Collections.Generic.List[string]]::new()
            $mkLines.Add('iteration,tick,dropped')
            for ($m = 0; $m -lt $marks.Count; $m++) {
                $isDropped = if ($mkDropped.Contains($m + 1)) { 1 } else { 0 }
                $mkLines.Add("$($m + 1),$($marks[$m]),$isDropped")
            }
            [System.IO.File]::WriteAllLines("$DumpCsv-$one-marks.csv", $mkLines)
            $hnLines = [System.Collections.Generic.List[string]]::new()
            $hnLines.Add('start,end')
            for ($h = 0; $h -lt $harness.Count; $h += 2) {
                $hnLines.Add("$($harness[$h]),$($harness[$h + 1])")
            }
            [System.IO.File]::WriteAllLines("$DumpCsv-$one-harness.csv", $hnLines)
        }
    }
    catch {
        Write-Host "shape ${one}: FAILED: $($_.Exception.Message)"
        Write-Host $_.InvocationInfo.PositionMessage
        $failedShapes += $one
    }
    finally {
        if ('Wintty.GapSampler' -as [type]) { [Wintty.GapSampler]::Finish() }
    }
}
} finally {
    # Cleanup: the probe and its shape litter, and the directory if this
    # run created it. Everything present under these names was created by
    # this run, because the pre-existence guard refused the litter names
    # up front. Best-effort: one removal failing must not skip the rest,
    # and a cleanup failure must not mask the error that got us here.
    foreach ($suffix in '', '.away', '.tmp', '.rep', '.bak') {
        $p = $probe + $suffix
        try { if (Test-Path -LiteralPath $p) { [System.IO.File]::Delete($p) } } catch { }
    }
    if ($createdDir -and (Test-Path -LiteralPath $TargetPath)) {
        try { Remove-Item -LiteralPath $TargetPath -Force } catch { }
    }
}

if ($failedShapes.Count -gt 0) {
    Write-Host ("RESULT: PARTIAL (failed shapes: {0})" -f ($failedShapes -join ', '))
    exit 3
}
Write-Host 'RESULT: MEASURED'
exit 0
