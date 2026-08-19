#requires -Version 7
# Fails to start once, then runs. Proves a retry actually re-runs rather
# than replaying the first verdict.
param([string]$ExePath, [Parameter(Mandatory)][string]$OutDir)
# Beside the run directory, not inside it: the runner preserves a failed
# attempt by renaming its directory, so a marker kept in $OutDir would vanish
# between attempts and this fixture would never recover.
$marker = Join-Path (Split-Path -Parent $OutDir) 'flaky.seen'
Add-Content -Path (Join-Path $OutDir 'attempts.txt') -Value 'x'
if (Test-Path $marker) { Write-Host 'selftest: flaky recovered'; exit 0 }
Set-Content -Path $marker -Value 'seen'
Write-Host 'selftest: flaky first attempt'
exit 1
