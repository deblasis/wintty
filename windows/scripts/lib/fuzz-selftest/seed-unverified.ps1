#requires -Version 7
# Mirrors search-fuzz.ps1's tail rather than a real harness's trap: findings
# are collected with a kind, the verdict is derived from those kinds in the
# finally, and the script exits on it.
#
# The case being pinned is a seed line that could not be read back. The
# harness never established the corpus every oracle count is measured
# against, so it knows nothing about the product and must leave with the
# retryable 1 - filing that as a defect would put a broken harness in front
# of real findings. cannot-run.ps1 covers a literal `exit 1`; this covers a 1
# that comes out of a throw, a catch and a classification, which is where it
# can go wrong: the same throw reaching a PRODUCT_FAIL trap would leave with
# 2 and never be retried.
param([string]$ExePath, [Parameter(Mandatory)][string]$OutDir)
$ErrorActionPreference = 'Stop'

$findings = [System.Collections.Generic.List[object]]::new()
$code = 0

try {
    Write-Host 'selftest: seed line could not be read back'
    throw 'the seed payload never landed on the input row'
}
catch {
    $findings.Add([pscustomobject]@{ kind = 'harness'; detail = $_.Exception.Message })
}
finally {
    $product = @($findings | Where-Object { $_.kind -ne 'harness' })
    if ($findings.Count -eq 0) { $code = 0 }
    elseif ($product.Count -gt 0) { $code = 2 }
    else { $code = 1 }

    # The self-test asserts this file, so a tail that skipped the cleanup is
    # caught rather than assumed. In the real harness this is where the app
    # is taken back down and XDG_CONFIG_HOME is restored, and a seeding
    # failure aborts the run from the middle of the op loop.
    Set-Content -LiteralPath (Join-Path $OutDir 'finally-ran.txt') -Value 'cleanup happened'
}

exit $code
