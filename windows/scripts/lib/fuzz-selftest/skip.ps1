#requires -Version 7
# The area did not run, and nothing is wrong. A harness whose subject the
# machine does not have - no WSL distro, say - exits 3: neither a pass, which
# would print PASS for an area nobody looked at, nor a 1, which would put the
# whole run at "could not run" over a machine that simply has no subject.
# Not retried: there is nothing transient about a machine that has no WSL.
param([string]$ExePath, [Parameter(Mandatory)][string]$OutDir)
Write-Host 'selftest: skipping, no subject on this machine'
exit 3