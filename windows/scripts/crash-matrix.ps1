#requires -Version 7
<#
.SYNOPSIS
  Runs each crash trigger in a child process and asserts what lands on disk.

.DESCRIPTION
  Expected results come from the verified coverage map in section 3 of
  docs/2026-08-25-crash-reporting-and-diagnostics-design.md. Rows that expect
  NO envelope are load-bearing: they pin known gaps so a future change cannot
  silently widen them, and they are why this harness asserts absence.

  Requires a DEBUG build: +crash is compiled out of Release.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [string]$CrashDir = "$env:LOCALAPPDATA\wintty\sentry",
    [string]$CrashLog = "$env:LOCALAPPDATA\Wintty\crash.log"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) {
    throw "ExePath not found: $ExePath"
}

# Kind, whether a crash envelope is expected, whether crash.log should grow.
#
# Only the kinds that run in-process. The surface-bound kinds
# (libghostty-main, libghostty-io, renderer-thread) fault inside libghostty
# via a binding action, which has no surface to land on before a window
# exists, so `+crash` refuses them with exit 3 and they are driven from the
# command palette instead. Adding them here would assert absence for a
# trigger that never ran, which is the one result this harness must not
# produce. See CrashKinds in windows/Ghostty.Core/Diagnostics.
$matrix = @(
    @{ Kind = 'native-seh';        Envelope = $true;  CrashLog = $false }
    @{ Kind = 'managed-unhandled'; Envelope = $false; CrashLog = $true  }
    @{ Kind = 'env-failfast';      Envelope = $false; CrashLog = $false }
    @{ Kind = 'stack-overflow';    Envelope = $false; CrashLog = $false }
    @{ Kind = 'handled-storm';     Envelope = $false; CrashLog = $false }
)

function Get-EnvelopeSet {
    if (-not (Test-Path $CrashDir)) { return @() }
    @(Get-ChildItem $CrashDir -File -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Name)
}

function Get-CrashLogLength {
    if (-not (Test-Path $CrashLog)) { return 0 }
    (Get-Item $CrashLog).Length
}

$results = @()
foreach ($row in $matrix) {
    $before    = Get-EnvelopeSet
    $logBefore = Get-CrashLogLength

    $proc = Start-Process -FilePath $ExePath `
        -ArgumentList '+crash', $row.Kind `
        -PassThru -Wait -NoNewWindow
    $exit = $proc.ExitCode

    # The envelope is written by the crashing process before it dies; give
    # the filesystem a beat to settle before enumerating.
    Start-Sleep -Milliseconds 500

    $after    = Get-EnvelopeSet
    $newFiles = @($after | Where-Object { $_ -notin $before })
    $gotEnv   = $newFiles.Count -gt 0
    $gotLog   = (Get-CrashLogLength) -gt $logBefore

    $results += [pscustomobject]@{
        Kind             = $row.Kind
        ExitCode         = $exit
        EnvelopeExpected = $row.Envelope
        EnvelopeGot      = $gotEnv
        CrashLogExpected = $row.CrashLog
        CrashLogGot      = $gotLog
        Pass             = (($gotEnv -eq $row.Envelope) -and ($gotLog -eq $row.CrashLog))
        NewEnvelopes     = ($newFiles -join ',')
    }
}

$results | Format-Table -AutoSize

$failed = @($results | Where-Object { -not $_.Pass })
if ($failed.Count -gt 0) {
    Write-Error "crash matrix: $($failed.Count) row(s) did not match the expected coverage map"
    exit 1
}

Write-Host 'crash matrix: all rows matched the expected coverage map' -ForegroundColor Green
exit 0
