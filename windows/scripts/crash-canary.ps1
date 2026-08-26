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

  Requires a DEBUG build: +crash is compiled out of Release.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [string]$CrashDir = "$env:LOCALAPPDATA\wintty\sentry"
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

$before = Get-EnvelopeSet

$env:WINTTY_CANARY = $canaryEnv
try {
    Start-Process -FilePath $ExePath `
        -ArgumentList '+crash', 'native-seh', $canaryArg `
        -PassThru -Wait -NoNewWindow | Out-Null
} finally {
    Remove-Item Env:\WINTTY_CANARY -ErrorAction SilentlyContinue
}

# The envelope is written by the crashing process before it dies; give the
# filesystem a beat to settle before enumerating.
Start-Sleep -Milliseconds 500

$after = Get-EnvelopeSet
$new   = @($after | Where-Object { $_ -notin $before })

if ($new.Count -eq 0) {
    throw 'canary: no envelope was produced, so nothing could be measured. Run crash-matrix.ps1 first to confirm native-seh is captured at all.'
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
