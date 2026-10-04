#requires -Version 7
# An exit code outside the convention. A harness killed by Ctrl-C or dying on
# an access violation returns one of these, and the runner must file it as
# something it does not understand rather than as a pass.
#
# 42, not 3: 3 is a member of the convention now (skip), so a fixture leaving
# it there would go on asserting the one thing that is no longer true about it.
param([string]$ExePath, [Parameter(Mandatory)][string]$OutDir)
Write-Host 'selftest: exiting 42, which means nothing in this convention'
exit 42