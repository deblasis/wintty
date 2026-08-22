#requires -Version 7
# Fails to start once, then runs. Proves a retry actually re-runs rather
# than replaying the first verdict.
param([string]$ExePath, [Parameter(Mandatory)][string]$OutDir)
# Beside the run directory, not inside it: the runner preserves a failed
# attempt by renaming its directory, so a marker kept in $OutDir would vanish
# between attempts and this fixture would never recover.
#
# Read and written with -LiteralPath, like every path the runner handles. A run
# root holding a '[' turns a bare -Path into a pattern: the marker is written
# somewhere else and the read never finds it, so this fixture reports its first
# attempt twice and the retry it exists to prove looks broken.
$marker = Join-Path (Split-Path -Parent $OutDir) 'flaky.seen'
Add-Content -LiteralPath (Join-Path $OutDir 'attempts.txt') -Value 'x'
if (Test-Path -LiteralPath $marker) { Write-Host 'selftest: flaky recovered'; exit 0 }
Set-Content -LiteralPath $marker -Value 'seen'
Write-Host 'selftest: flaky first attempt'
exit 1
