#requires -Version 7
# Never returns. A harness that wedges holding the foreground is the worst
# case for a runner without a timeout: the run stops dead and the desktop is
# unusable until someone finds the window.
param([string]$ExePath, [Parameter(Mandatory)][string]$OutDir)
Write-Host 'selftest: hanging on purpose'
while ($true) { Start-Sleep -Seconds 5 }
