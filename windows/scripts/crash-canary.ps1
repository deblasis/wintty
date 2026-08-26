#requires -Version 7
<#
.SYNOPSIS
  Crashes Wintty with known canary strings in play, then greps the resulting
  envelope for them.

.DESCRIPTION
  Answers one question: does a Wintty crash envelope actually contain
  terminal-adjacent process strings? If it does, the opt-in split in section 7
  of docs/2026-08-25-crash-reporting-and-diagnostics-design.md is justified. If
  it does not, that split costs a support round trip for no privacy gain and
  the sealed-envelope decision (D3) should be revisited.

  This is a MEASUREMENT, not a gate: it exits 0 whether or not canaries are
  found, and only fails if it could not measure at all.

  Runs against any configuration. +crash used to be compiled out of Release;
  it now ships everywhere, and pointing this at a published build is the
  point, because that is the one whose reports users would be sending.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [string]$CrashDir = "$env:LOCALAPPDATA\wintty\crash"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) {
    throw "ExePath not found: $ExePath"
}

# Two independent channels into the child's process memory: the environment
# block (inherited by Start-Process) and argv (the +crash handler reads only
# args[1] as the kind, so a third argument rides along unused but present).
$canaryEnv = 'CANARY_ENV_8f3a1c'
$canaryArg = 'CANARY_ARG_5d9e2b'

function Get-EnvelopeSet {
    if (-not (Test-Path $CrashDir)) { return @() }
    @(Get-ChildItem $CrashDir -File -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Name)
}

# A launch that arms the reporter, does not crash, and exits. See below for
# why this is needed twice.
function Invoke-Drain {
    Start-Process -FilePath $ExePath -ArgumentList '+crash', 'handled-storm' `
        -PassThru -Wait -NoNewWindow | Out-Null
    Start-Sleep -Milliseconds 300
}

# A crash report arrives ONE LAUNCH LATE. sentry-native's inproc backend does
# not call our transport at crash time; it writes into its own run directory,
# and only the next sentry_init replays it. So a canary-carrying crash leaves
# NOTHING in the crash directory when its process dies.
#
# This script used to snapshot around the crash alone and read $new[0]. What
# it actually read was whatever the crashing launch DRAINED on the way up,
# i.e. the report from whichever crash came before it. Run after
# crash-matrix.ps1, or after any real crash, it opened a canary-free envelope
# and printed "no canary found" -- which is the input to decision D3, so the
# answer would have been arrived at from the wrong file.
#
# Drain first so the directory is quiet, then crash, then drain again to bring
# THIS crash's report through.
Invoke-Drain
$before = Get-EnvelopeSet

$env:WINTTY_CANARY = $canaryEnv
try {
    Start-Process -FilePath $ExePath `
        -ArgumentList '+crash', 'native-seh', $canaryArg `
        -PassThru -Wait -NoNewWindow | Out-Null
} finally {
    Remove-Item Env:\WINTTY_CANARY -ErrorAction SilentlyContinue
}

Invoke-Drain

$after = Get-EnvelopeSet
$new   = @($after | Where-Object { $_ -notin $before })

if ($new.Count -eq 0) {
    throw 'canary: no envelope was produced, so nothing could be measured. Run crash-matrix.ps1 first to confirm native-seh is captured at all.'
}
if ($new.Count -gt 1) {
    # More than one means something else crashed alongside this, and $new[0]
    # would be an arbitrary pick. Say so rather than measure the wrong file.
    throw ("canary: {0} envelopes appeared, so which one belongs to this " +
        "crash is ambiguous: {1}" -f $new.Count, ($new -join ', '))
}

# Envelopes are part text, part binary; scan raw bytes decoded as Latin1
# rather than reading as UTF-8 text, which would choke on non-text spans.
$path  = Join-Path $CrashDir $new[0]
$bytes = [System.IO.File]::ReadAllBytes($path)
$text  = [System.Text.Encoding]::Latin1.GetString($bytes)

$findings = foreach ($c in @($canaryEnv, $canaryArg)) {
    [pscustomobject]@{
        Canary = $c
        Found  = $text.Contains($c)
    }
}

Write-Host "envelope: $path ($($bytes.Length) bytes)"
$findings | Format-Table -AutoSize

if (@($findings | Where-Object { $_.Found }).Count -gt 0) {
    Write-Host 'RESULT: envelopes DO carry process strings. The opt-in split in section 7 is justified.' -ForegroundColor Yellow
} else {
    Write-Host 'RESULT: no canary found. Revisit decision D3 before building the opt-in split.' -ForegroundColor Cyan
}

exit 0
