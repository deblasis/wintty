#requires -Version 7
# An unhandled terminating error, which is how a gate refusal surfaces in
# every harness that puts Assert-NoWintty above its try.
param([string]$ExePath, [Parameter(Mandatory)][string]$OutDir)
throw 'selftest: unhandled'
