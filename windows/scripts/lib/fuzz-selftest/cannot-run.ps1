#requires -Version 7
# The harness could not run. Retryable: nothing was learned about the product.
param([string]$ExePath, [Parameter(Mandatory)][string]$OutDir)
Add-Content -Path (Join-Path $OutDir 'attempts.txt') -Value 'x'
Write-Host 'selftest: cannot run'
exit 1
