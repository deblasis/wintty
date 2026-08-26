#requires -Version 7
<#
.SYNOPSIS
  Runs each crash trigger in a child process and asserts what lands on disk.

.DESCRIPTION
  Expected results come from the verified coverage map in section 3 of
  docs/2026-08-25-crash-reporting-and-diagnostics-design.md. Rows that expect
  NO report are load-bearing: they pin known gaps so a future change cannot
  silently widen them, and they are why this harness asserts absence.

  Runs against any configuration. +crash used to be compiled out of Release,
  which left the build users install as the one nobody could measure; it now
  ships everywhere, and pointing this at a Release build is the point.

  POINT IT AT A PUBLISHED BUILD, not at bin/x64/Release. Ghostty.csproj sets
  PublishAot, so `dotnet build` produces a CoreCLR binary and `dotnet publish`
  produces the NativeAOT one that ships, and they do not agree about crashes.
  Measured on both: an unhandled managed exception is CAPTURED under CoreCLR,
  which dispatches it as SEH 0xE0434352 through the unhandled filter, and is
  NOT captured under NativeAOT, which fail-fasts with 0xC0000409. A native SEH
  raised from a P/Invoke is caught and swallowed by Program.Main's catch-all
  under CoreCLR, and crashes the process under NativeAOT. Every row here is
  the NativeAOT answer.

    dotnet publish windows/Ghostty/Ghostty.csproj -c Release -r win-x64 `
      -p:Platform=x64 --self-contained true -o <dir>
    pwsh windows/scripts/crash-matrix.ps1 -ExePath <dir>/Wintty.exe

.NOTES
  A crash report arrives ONE LAUNCH LATE, and the harness is built around
  that. sentry-native's inproc backend does not call our transport at crash
  time; it writes the envelope into its own run directory under the database.
  Only the next sentry_init picks it up (sentry__process_old_runs) and pushes
  it through the transport, which is what writes to the crash directory.

  So each row drains before and after: a clean launch that arms the reporter,
  crashes at nothing, and exits. The drain AFTER the trigger is what makes the
  report appear; the drain BEFORE the next row is what proves the previous
  row's drain was enough. An earlier version of this script snapshotted the
  directory around the trigger alone, and so credited every capture to the row
  after the one that produced it.
#>
# Two directories are easy to confuse. Crash envelopes are written by
# ghostty's own transport (src/crash/sentry.zig sendInternal) to the STATE
# dir, xdg.state with subdir "wintty/crash". The sentry database, which is
# where sentry-native keeps its .run bookkeeping and session.json, is the
# CACHE dir, %LOCALAPPDATA%\wintty\sentry. Watching the database counts a
# new .run.lock on every launch and reports an envelope for runs that never
# crashed.
param(
    [Parameter(Mandatory)][string]$ExePath,
    [string]$CrashDir = "$env:LOCALAPPDATA\wintty\crash",
    # The managed handler writes next to the binary, not under LOCALAPPDATA:
    # Program.cs uses Path.Combine(AppContext.BaseDirectory, "ghostty-crash.log").
    [string]$CrashLog = (Join-Path (Split-Path -Parent $ExePath) 'ghostty-crash.log'),
    # Report what happened without asserting it against the coverage map.
    # Use this when re-measuring; use the default when gating a change.
    [switch]$Discover
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) {
    throw "ExePath not found: $ExePath"
}

# Kind, and whether a crash EVENT envelope and a managed crash log are
# expected. "Envelope" means an envelope carrying an event item: the transport
# discards anything else (Transport.shouldDiscard), so a session-only envelope
# never reaches disk no matter how many sentry sends the logs show.
#
# Only the kinds that run in-process. The surface-bound kinds
# (libghostty-main, libghostty-io, renderer-thread) fault inside libghostty
# via a binding action, which has no surface to land on before a window
# exists, so `+crash` refuses them with exit 3 and they are driven from the
# command palette instead. Adding them here would assert absence for a
# trigger that never ran, which is the one result this harness must not
# produce. See CrashKinds in windows/Ghostty.Core/Diagnostics.
$matrix = @(
    # The row the in-process backend has to win, and it does: exit 0xE0000001,
    # a crash event and a session marked "crashed", both on the next launch.
    # No managed crash log, and none is wanted: the filter takes the process
    # down, and the CLI has no handler registered anyway.
    @{ Kind = 'native-seh';        Envelope = $true;  CrashLog = $false }
    # An unhandled managed exception, which NativeAOT turns into a fail-fast
    # (exit 0xC0000409, STATUS_STACK_BUFFER_OVERRUN) rather than an exception,
    # so SetUnhandledExceptionFilter never runs and nothing is captured. Same
    # result as env-failfast, by the same mechanism.
    #
    # No crash log either, and that is the CLI telling the truth rather than a
    # gap: the handler that writes one is registered in App.xaml.cs, which is
    # the GUI, and the CLI path runs in Program.MainImpl before any App
    # exists. That handler is what covers this class in the shipped app, with
    # a managed stack trace, and it is measured through the palette the way
    # the libghostty rows are.
    #
    # This row used to expect a log, and got one, because the throw was caught
    # by Main's catch-all and turned into ReportFatal: the process never
    # crashed and the row passed for the wrong reason. The trigger now throws
    # from its own thread.
    @{ Kind = 'managed-unhandled'; Envelope = $false; CrashLog = $false }
    @{ Kind = 'env-failfast';      Envelope = $false; CrashLog = $false }
    @{ Kind = 'stack-overflow';    Envelope = $false; CrashLog = $false }
    @{ Kind = 'handled-storm';     Envelope = $false; CrashLog = $false }
)

# Parse the envelope framing rather than grepping the bytes. The framing is
# exactly what a scrub that rewrites payloads can corrupt, and a report that
# no parser will read is worth nothing, so reading it here is a second check
# on the writer and not merely a convenience.
function Read-Envelope([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $utf8  = [System.Text.Encoding]::UTF8
    $items = @()
    $pos   = 0

    function Read-Line([byte[]]$b, [ref]$p) {
        $nl = [Array]::IndexOf($b, [byte]10, $p.Value)
        if ($nl -lt 0) { return $null }
        $line = [System.Text.Encoding]::UTF8.GetString($b, $p.Value, $nl - $p.Value)
        $p.Value = $nl + 1
        $line
    }

    $header = Read-Line $bytes ([ref]$pos)
    if ($null -eq $header) {
        return [pscustomobject]@{ Malformed = 'no header line'; Items = @() }
    }

    while ($pos -lt $bytes.Length) {
        $itemHeader = Read-Line $bytes ([ref]$pos)
        if ([string]::IsNullOrWhiteSpace($itemHeader)) { break }

        try { $parsedHeader = $itemHeader | ConvertFrom-Json }
        catch {
            return [pscustomobject]@{
                Malformed = "item header is not JSON: $itemHeader"; Items = $items
            }
        }

        $lenProp = $parsedHeader.PSObject.Properties['length']
        if ($null -eq $lenProp) {
            return [pscustomobject]@{
                Malformed = "item header has no length: $itemHeader"; Items = $items
            }
        }
        $len = [int]$lenProp.Value
        if ($pos + $len -gt $bytes.Length) {
            return [pscustomobject]@{
                Malformed = ("item '{0}' claims {1} bytes, {2} remain" -f `
                    $parsedHeader.type, $len, ($bytes.Length - $pos))
                Items = $items
            }
        }

        $payload = $utf8.GetString($bytes, $pos, $len)
        $pos += $len
        if ($pos -lt $bytes.Length -and $bytes[$pos] -eq 10) { $pos++ }

        $body = $null
        try { $body = $payload | ConvertFrom-Json } catch { }

        # An event without an exception is normal (a message event has none),
        # and so is an attachment that is not JSON at all, so reach for the
        # exception only once it is known to be there.
        $exc = $null
        if ($null -ne $body -and $null -ne $body.PSObject.Properties['exception']) {
            $values = $body.exception.PSObject.Properties['values']
            if ($null -ne $values -and $values.Value.Count -gt 0) {
                $exc = $values.Value[0]
            }
        }

        $items += [pscustomobject]@{
            Type    = $parsedHeader.type
            Length  = $len
            Level   = if ($null -ne $body) { $body.level } else { $null }
            Status  = if ($null -ne $body) { $body.status } else { $null }
            ExcType = if ($null -ne $exc)  { $exc.type } else { $null }
            Mech    = if ($null -ne $exc)  { $exc.mechanism.type } else { $null }
            Payload = $payload
        }
    }

    [pscustomobject]@{ Malformed = $null; Items = $items }
}

function Get-EnvelopeSet {
    if (-not (Test-Path $CrashDir)) { return @() }
    @(Get-ChildItem $CrashDir -File -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Name)
}

# The managed handler uses File.WriteAllText, which OVERWRITES. Detecting a
# crash log by growth therefore misses every row after the first: a shorter
# exception replaces a longer one and the length goes down. Identify the file
# by its contents instead, so "was it rewritten" is what gets measured.
function Get-CrashLogStamp {
    if (-not (Test-Path $CrashLog)) { return '<absent>' }
    $item = Get-Item $CrashLog
    "$($item.LastWriteTimeUtc.Ticks):$($item.Length):" +
        (Get-FileHash $CrashLog -Algorithm SHA256).Hash
}

function Invoke-Wintty([string]$kind) {
    $err = [System.IO.Path]::GetTempFileName()
    try {
        $proc = Start-Process -FilePath $ExePath `
            -ArgumentList '+crash', $kind `
            -PassThru -Wait -NoNewWindow -RedirectStandardError $err
        # The transport writes from within the launch; give the filesystem a
        # beat to settle before enumerating.
        Start-Sleep -Milliseconds 500
        [pscustomobject]@{
            ExitCode = $proc.ExitCode
            Armed    = (Select-String -Path $err -Pattern 'reporter armed' -Quiet) -eq $true
            Stderr   = (Get-Content $err -Raw)
        }
    } finally {
        Remove-Item $err -Force -ErrorAction SilentlyContinue
    }
}

# A launch that arms the reporter, does not crash, and exits. handled-storm is
# used deliberately rather than a mode invented for the harness: it is already
# the row asserting that a thousand handled exceptions produce no report, so
# if the drain were ever to emit one, that row fails and says so.
function Invoke-Drain {
    Invoke-Wintty 'handled-storm' | Out-Null
}

# Describe the envelopes that appeared, so a row says WHAT was captured rather
# than only how many files exist.
function Get-Report([string[]]$names) {
    $events = @()
    $sessions = @()
    $bad = @()
    foreach ($name in $names) {
        $envelope = Read-Envelope (Join-Path $CrashDir $name)
        if ($envelope.Malformed) { $bad += "$name`: $($envelope.Malformed)"; continue }
        foreach ($item in $envelope.Items) {
            switch ($item.Type) {
                'event'   { $events   += ($item.ExcType ?? $item.Level ?? 'event') }
                'session' { $sessions += ($item.Status ?? 'session') }
                default   { }
            }
        }
    }
    [pscustomobject]@{
        HasEvent = $events.Count -gt 0
        Events   = ($events   -join ',')
        Sessions = ($sessions -join ',')
        Malformed = ($bad -join '; ')
    }
}

$results = @()
$notes   = @()

foreach ($row in $matrix) {
    # Drain first. Anything that shows up here belongs to the row before,
    # arriving later than the design says it can, and is reported rather than
    # folded into this row's baseline.
    $preDrain = Get-EnvelopeSet
    Invoke-Drain
    $late = @((Get-EnvelopeSet) | Where-Object { $_ -notin $preDrain })
    if ($late.Count -gt 0) {
        $notes += ("before '{0}': {1} envelope(s) arrived more than one launch " +
            "after the crash that produced them: {2}") -f `
            $row.Kind, $late.Count, ($late -join ',')
    }

    $before    = Get-EnvelopeSet
    $logBefore = Get-CrashLogStamp

    $run = Invoke-Wintty $row.Kind
    if (-not $run.Armed) {
        $notes += ("'{0}': the reporter never armed, so an absent report " +
            "below proves nothing") -f $row.Kind
    }

    # The crash log is written by the crashing process itself, so it is
    # already there. The envelope is not: drain to bring it through.
    $gotLog = (Get-CrashLogStamp) -ne $logBefore
    Invoke-Drain

    $newFiles = @((Get-EnvelopeSet) | Where-Object { $_ -notin $before })
    $report   = Get-Report $newFiles
    if ($report.Malformed) {
        $notes += ("'{0}': {1}") -f $row.Kind, $report.Malformed
    }

    $results += [pscustomobject]@{
        Kind             = $row.Kind
        ExitCode         = $run.ExitCode
        EnvelopeExpected = $row.Envelope
        EnvelopeGot      = $report.HasEvent
        Captured         = $report.Events
        Sessions         = $report.Sessions
        CrashLogExpected = $row.CrashLog
        CrashLogGot      = $gotLog
        Pass             = (($report.HasEvent -eq $row.Envelope) -and
                            ($gotLog -eq $row.CrashLog) -and
                            (-not $report.Malformed))
    }
}

# The last row has no successor to prove its drain was enough, so prove it
# here. Without this the final row is the one place the old bug could hide.
$tailBefore = Get-EnvelopeSet
Invoke-Drain
$tailLate = @((Get-EnvelopeSet) | Where-Object { $_ -notin $tailBefore })
if ($tailLate.Count -gt 0) {
    $notes += ("after the last row: {0} envelope(s) arrived late: {1}") -f `
        $tailLate.Count, ($tailLate -join ',')
}

$results | Format-Table -AutoSize Kind, ExitCode, EnvelopeExpected, EnvelopeGot,
    Captured, Sessions, CrashLogExpected, CrashLogGot, Pass

foreach ($note in $notes) { Write-Warning $note }

if ($Discover) {
    Write-Host 'crash matrix: discovery run, nothing asserted' -ForegroundColor Yellow
    exit 0
}

$failed = @($results | Where-Object { -not $_.Pass })
if ($failed.Count -gt 0 -or $notes.Count -gt 0) {
    Write-Error (("crash matrix: {0} row(s) did not match the expected coverage " +
        "map, {1} note(s)") -f $failed.Count, $notes.Count)
    exit 1
}

Write-Host 'crash matrix: all rows matched the expected coverage map' -ForegroundColor Green
exit 0
