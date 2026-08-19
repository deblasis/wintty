#requires -Version 7
# Product findings. Must NOT be retried: re-running a real defect until it
# passes is how a flaky-looking regression gets buried.
param([string]$ExePath, [Parameter(Mandatory)][string]$OutDir)
Add-Content -Path (Join-Path $OutDir 'attempts.txt') -Value 'x'
Write-Host 'selftest: findings'
exit 2
