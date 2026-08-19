#requires -Version 7
# A harness that takes no -OutDir, like vtabs-morph-fuzz.ps1 and
# splash-single-instance-race.ps1. Binding -OutDir to it would fail.
param([string]$ExePath, [int]$Seed = 0)
if ($Seed -ne 4242) { Write-Host "selftest: expected -Seed 4242, got $Seed"; exit 1 }
Write-Host 'selftest: no-outdir ok'
exit 0
