<#
.SYNOPSIS
    Assert conpty-mode smoke verdict from ghostty log.

.DESCRIPTION
    Reads the most recent ghostty log under -LogDir, extracts the
    validate_transport verdict line and OSC 11 receipt line, and
    asserts the pairing matches the expected outcome for -Row.

    Exit 0 on match, 1 on mismatch, 2 on missing instrumentation or
    unknown row (distinguishes test-infra broken from test-failed).

.PARAMETER Row
    One of: pwsh-auto, pwsh-always, pwsh-never, cmd-auto.

.PARAMETER LogDir
    Directory containing ghostty log files. Defaults to
    %LOCALAPPDATA%\Ghostty\logs.
#>
param(
    [Parameter(Mandatory)][string]$Row,
    [string]$LogDir = "$env:LOCALAPPDATA\Ghostty\logs",
    [string]$Since
)
$ErrorActionPreference = 'Stop'

$Expected = @{
    'pwsh-auto'   = @{ Transport = 'bypass'; Osc11 = 'observed' }
    'pwsh-always' = @{ Transport = 'bypass'; Osc11 = 'observed' }
    'pwsh-never'  = @{ Transport = 'conpty'; Osc11 = 'absent'   }
    'cmd-auto'    = @{ Transport = 'conpty'; Osc11 = 'na'       }
}

if (-not $Expected.ContainsKey($Row)) {
    Write-Host "ERROR: unknown row '$Row'. Valid rows: $($Expected.Keys -join ', ')"
    exit 2
}

if (-not (Test-Path $LogDir)) {
    Write-Host "ERROR: log directory not found: $LogDir"
    exit 2
}

$log = Get-ChildItem $LogDir -Filter '*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $log) {
    Write-Host "ERROR: no log files under $LogDir"
    exit 2
}

$lines = Get-Content -LiteralPath $log.FullName

# Filter to entries emitted on or after -Since so earlier runs in the
# same log file cannot contaminate the result. Log lines begin with
# "YYYY-MM-DDTHH:MM:SS.fffZ |"; compare lexicographically against the
# caller-provided ISO-8601 UTC timestamp.
if ($PSBoundParameters.ContainsKey('Since') -and $Since) {
    $lines = $lines | Where-Object { $_.Length -ge 24 -and $_.Substring(0, 24) -ge $Since }
}

# Verdict: "transport resolved: shell=\"...\" config_mode=<mode> resolved=<transport>"
$verdictMatch = $lines |
    Select-String -Pattern 'transport resolved:.*resolved=(\w+)' |
    Select-Object -Last 1
if (-not $verdictMatch) {
    Write-Host "ERROR: no verdict line in $($log.Name) (validate_transport instrumentation missing or filter excludes it)"
    exit 2
}
$observedTransport = $verdictMatch.Matches[0].Groups[1].Value

# OSC 11 receipt: "osc11 from pty: kind=query"
$osc11Match = $lines | Select-String -Pattern 'osc11 from pty' | Select-Object -Last 1
$observedOsc11 = if ($osc11Match) { 'observed' } else { 'absent' }

$exp = $Expected[$Row]
$transportOk = ($observedTransport -eq $exp.Transport)
$osc11Ok = if ($exp.Osc11 -eq 'na') { $true } else { $observedOsc11 -eq $exp.Osc11 }

if ($transportOk -and $osc11Ok) {
    Write-Host "OK: $Row transport=$observedTransport osc11=$observedOsc11 (log=$($log.Name))"
    exit 0
}

Write-Host "FAIL: $Row"
Write-Host "  expected: transport=$($exp.Transport) osc11=$($exp.Osc11)"
Write-Host "  observed: transport=$observedTransport osc11=$observedOsc11"
Write-Host "  log:      $($log.FullName)"
exit 1
