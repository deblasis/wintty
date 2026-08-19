#requires -Version 7
# An exit code outside the convention. A harness killed by Ctrl-C or dying on
# an access violation returns one of these, and the runner must file it as
# something it does not understand rather than as a pass.
param([string]$ExePath, [Parameter(Mandatory)][string]$OutDir)
Write-Host 'selftest: exiting 3, which means nothing in this convention'
exit 3
