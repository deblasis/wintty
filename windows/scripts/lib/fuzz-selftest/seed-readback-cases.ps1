#requires -Version 7
# Exercises the seed read-back rules directly, with no window and no build.
#
# seed-unverified.ps1 next door pins what the RUNNER does with a harness that
# could not establish its corpus. This pins the decision that gets it there.
# Without it, Test-SeedLanded could be replaced by `return 'landed'` and the
# whole self-test would stay green while the harness went back to measuring
# its oracle against text it never typed - which is the bug the read-back was
# added to fix.
#
# Exits 2 rather than 1 on a failure: a rule here being wrong is a defect in
# shipped logic, not a harness that could not run, and it must not be retried
# until it passes.
param([string]$ExePath, [Parameter(Mandatory)][string]$OutDir)
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '../seed-readback.ps1')

$fails = @()

function Check([string]$name, $expected, $actual) {
    if ($expected -ne $actual) { $script:fails += "$name : expected '$expected', got '$actual'" }
}

# The line was typed: one more copy than before.
Check 'rise-is-landed' 'landed' (Test-SeedLanded 'prompt> ' 'prompt>  echo ZQXW' 'echo ZQXW')

# The emit op retypes the same line every few iterations, so an older copy is
# already in the scrollback. Presence is not the question; the count rising is.
Check 'same-count-is-missing' 'missing' `
    (Test-SeedLanded 'old echo ZQXW' 'old echo ZQXW' 'echo ZQXW')

# The case this file exists for. Get-TerminalText answers '' on any UIA fault,
# and a baseline of '' counts zero occurrences - which turns the question back
# into bare presence and blesses a send that landed nothing.
Check 'unreadable-before' 'unreadable' (Test-SeedLanded '' 'old echo ZQXW' 'echo ZQXW')
Check 'unreadable-after'  'unreadable' (Test-SeedLanded 'prompt> ' '' 'echo ZQXW')

# A partly typed line does not contain the whole needle.
Check 'truncated-is-missing' 'missing' (Test-SeedLanded 'prompt> ' 'prompt>  echo ZQX' 'echo ZQXW')

# The read-back is ordinal on purpose: a line that came back in a different
# case did not come back.
Check 'case-change-is-missing' 'missing' `
    (Test-SeedLanded 'prompt> ' 'prompt>  ECHO ZQXW' 'echo ZQXW')

# Nothing to verify is not a failure to verify.
Check 'empty-text-is-landed' 'landed' (Test-SeedLanded 'a' 'a' '')

# The oracle's own counter: non-overlapping, and case-folding by default.
Check 'count-non-overlapping' 2 (Measure-Occurrences 'aaaa' 'aa')
Check 'count-folds-case'      2 (Measure-Occurrences 'ZqXw zqxw' 'ZQXW')
Check 'count-ordinal-strict'  1 `
    (Measure-Occurrences 'ZQXW zqxw' 'ZQXW' ([StringComparison]::Ordinal))

Set-Content -LiteralPath (Join-Path $OutDir 'finally-ran.txt') -Value 'cases ran'

if ($fails.Count -gt 0) {
    $fails | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host "seed read-back rules: $($fails.Count) case(s) wrong" -ForegroundColor Red
    exit 2
}

Write-Host 'seed read-back rules: all cases hold'
exit 0
