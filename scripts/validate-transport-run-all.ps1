<#
.SYNOPSIS
    Run all four conpty-mode smoke rows and aggregate results.

.DESCRIPTION
    Invokes scripts/validate-transport-run.ps1 for each of the four
    rows, captures the exit code per row, prints a summary table,
    and exits non-zero if any row failed or timed out.
#>
param(
    [int]$TimeoutMs = 15000
)
$ErrorActionPreference = 'Stop'

$rows = @('pwsh-auto', 'pwsh-always', 'pwsh-never', 'cmd-auto')
$results = [ordered]@{}

foreach ($r in $rows) {
    Write-Host "===== $r ====="
    pwsh -NoProfile -File scripts/validate-transport-run.ps1 -Row $r -TimeoutMs $TimeoutMs
    $results[$r] = switch ($LASTEXITCODE) {
        0 { 'pass' }
        1 { 'fail' }
        default { "infra ($LASTEXITCODE)" }
    }
}

Write-Host ''
Write-Host '=== Summary ==='
foreach ($r in $rows) {
    Write-Host ("{0,-14} {1}" -f $r, $results[$r])
}

$anyFailed = ($results.Values | Where-Object { $_ -ne 'pass' }).Count -gt 0
if ($anyFailed) { exit 1 }
exit 0
