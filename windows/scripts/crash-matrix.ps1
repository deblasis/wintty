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
    # ExitCode is asserted, not merely printed. Without it the one row whose
    # entire claim is "this does not crash" could not tell a clean exit from a
    # fail-fast: both produce no envelope and no crash log, so handled-storm
    # passed either way. Since the drain launch IS handled-storm, a mutation
    # that made it crash would have taken down every drain in the run while
    # the table reported all rows matching.
    #
    # The row the in-process backend has to win, and it does: exit 0xE0000001,
    # a crash event and a session marked "crashed", both on the next launch.
    # No managed crash log, and none is wanted: the filter takes the process
    # down, and the CLI has no handler registered anyway.
    @{ Kind = 'native-seh';        Envelope = $true;  CrashLog = $false; ExitCode = -536870911 }
    # An unhandled managed exception, which NativeAOT turns into a fail-fast
    # (exit 0xC0000409, STATUS_STACK_BUFFER_OVERRUN) rather than an exception,
    # so SetUnhandledExceptionFilter never runs and nothing is captured. Same
    # result as env-failfast, by the same mechanism.
    #
    # A crash log, though, and the exit code is what says the fix is real
    # rather than a regression: the process still dies at 0xC0000409 and
    # still produces no envelope, and it now leaves the artifact the
    # ownership rule assigns to a managed exception.
    #
    # This row expected NO log until Program.Main started registering an
    # AppDomain.CurrentDomain.UnhandledException handler of its own. Before
    # that, the only such handler lived in App.xaml.cs, so the CLI - which
    # builds no App - reported this class nowhere at all. That was the gap,
    # not the truth, and it was the whole of issue #442.
    #
    # Read the pair, not the row: a log WITHOUT 0xC0000409 would mean the
    # throw was caught somewhere and the process never crashed, which is how
    # this row passed for the wrong reason once before, back when the trigger
    # threw inline and Main's catch-all turned it into a clean ReportFatal.
    # The trigger throws from its own thread precisely so that cannot recur.
    @{ Kind = 'managed-unhandled'; Envelope = $false; CrashLog = $true;  ExitCode = -1073740791 }
    @{ Kind = 'env-failfast';      Envelope = $false; CrashLog = $false; ExitCode = -1073740791 }
    @{ Kind = 'stack-overflow';    Envelope = $false; CrashLog = $false; ExitCode = -1073741571 }
    # Zero, and it is load-bearing: this row exists to prove a thousand
    # handled exceptions produce no report, which is only meaningful if the
    # process survived to say so.
    @{ Kind = 'handled-storm';     Envelope = $false; CrashLog = $false; ExitCode = 0 }
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

        # A payload that no JSON parser will read is a corrupted report, and
        # this function exists to say so. Swallowing the failure and then
        # classifying the item from its HEADER meant an event whose payload
        # the scrub had mangled still counted as an event and the row passed.
        # That is the one corruption class the writer can still produce, now
        # that it always recomputes the length header.
        #
        # Attachments are exempt: they are not JSON and never claimed to be.
        $body = $null
        $jsonError = $null
        try { $body = $payload | ConvertFrom-Json }
        catch { $jsonError = $_.Exception.Message }
        if ($jsonError -and $parsedHeader.type -ne 'attachment') {
            return [pscustomobject]@{
                Malformed = ("item '{0}' payload is not JSON: {1}" -f `
                    $parsedHeader.type, $jsonError)
                Items = $items
            }
        }

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

# Identify the crash log by its CONTENTS, not by its size.
#
# The reason has changed once already and the stamp survived both, which is
# why it is worth writing down. The managed handler used to call
# File.WriteAllText, which overwrites: detecting a log by growth missed every
# row after the first, because a shorter exception replaced a longer one and
# the length went DOWN. It now appends (each entry delimited and timestamped),
# so growth would work again - but a hash still measures the right thing and a
# length no longer distinguishes two crashes that happen to write the same
# number of bytes. Appending also makes the hash strictly better than it was:
# two identical exceptions in a row used to produce byte-identical files and
# now cannot.
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
        Leaks    = (Get-PrivacyLeaks $names)
    }
}

# Whether a report that actually got written carries anything it should not.
#
# Nothing in this branch checked a real envelope for a username. The scrub's
# unit tests feed it hand-authored JSON containing exactly the keys someone
# already thought of, which is why the shipped leak (`code_file` scrubbed,
# `debug_file` next door still spelling out the home directory) passed a
# "contains no username" check. Only bytes off disk can see that.
#
# Deliberately not keyed on the scrub's own list: this asks whether ANY
# absolute path or the current username survived, so a field nobody has
# thought of yet fails here rather than shipping.
function Get-PrivacyLeaks([string[]]$names) {
    $found = @()
    foreach ($name in $names) {
        $text = [System.IO.File]::ReadAllText((Join-Path $CrashDir $name))

        if ($env:USERNAME -and $text.Contains($env:USERNAME)) {
            $found += "$name carries the username"
        }
        # Both spellings: JSON escapes a backslash, so a Windows path arrives
        # as "C:\\Users\\...", and a raw one would arrive as "C:\Users\...".
        foreach ($pattern in @('[A-Za-z]:\\\\', '[A-Za-z]:\\[^\\]')) {
            if ($text -match $pattern) {
                $sample = ([regex]::Match($text, "$pattern[^`"]{0,40}")).Value
                $found += "$name carries an absolute path ($sample)"
                break
            }
        }
    }

    $found -join '; '
}

$results = @()
$notes   = @()

# The table above is a third copy of the catalogue, and the only one no test
# can scan: CrashKinds guards the CLI and the palette against drifting apart,
# but this is a .ps1. Ask the binary what it knows rather than trusting the
# table. An unknown kind exits 2 after printing both lists.
$probe = Invoke-Wintty '__list__'
$cliKinds = @()
if ($probe.Stderr -match 'crash-trigger: cli-kinds:\s*(.+)') {
    $cliKinds = @($Matches[1] -split ',' | ForEach-Object { $_.Trim() } |
        Where-Object { $_ })
}
if ($cliKinds.Count -eq 0) {
    $notes += "could not read the kind catalogue from the binary, so this " +
        "run cannot tell whether the table below still covers it"
} else {
    $tableKinds = @($matrix | ForEach-Object { $_.Kind })
    $missing = @($cliKinds | Where-Object { $_ -notin $tableKinds })
    $extra   = @($tableKinds | Where-Object { $_ -notin $cliKinds })
    if ($missing.Count -gt 0) {
        $notes += ("the catalogue has CLI kind(s) this table never runs: {0}" -f
            ($missing -join ', '))
    }
    if ($extra.Count -gt 0) {
        $notes += ("this table names kind(s) the catalogue does not: {0}" -f
            ($extra -join ', '))
    }
}

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

    if ($report.Leaks) {
        $notes += ("'{0}': {1}") -f $row.Kind, $report.Leaks
    }

    $results += [pscustomobject]@{
        Kind             = $row.Kind
        ExitExpected     = $row.ExitCode
        ExitCode         = $run.ExitCode
        EnvelopeExpected = $row.Envelope
        EnvelopeGot      = $report.HasEvent
        Captured         = $report.Events
        Sessions         = $report.Sessions
        CrashLogExpected = $row.CrashLog
        CrashLogGot      = $gotLog
        Pass             = (($report.HasEvent -eq $row.Envelope) -and
                            ($gotLog -eq $row.CrashLog) -and
                            ($run.ExitCode -eq $row.ExitCode) -and
                            (-not $report.Malformed) -and
                            (-not $report.Leaks))
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

$results | Format-Table -AutoSize Kind, ExitExpected, ExitCode, EnvelopeExpected,
    EnvelopeGot, Captured, Sessions, CrashLogExpected, CrashLogGot, Pass

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
