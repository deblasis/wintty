#requires -Version 7
# Mirrors the shape every mouse-fuzz-* harness has: a script-scope trap, a
# try/finally that restores the environment and sweeps, and a PRODUCT_FAIL
# thrown from inside the try.
#
# Two things must hold, and both have been wrong here before. The run has to
# leave with 2, because a product defect that leaves with 1 is retried and
# then reported as an area nothing is known about. And the finally has to run
# anyway, because that is where XDG_CONFIG_HOME goes back and where the
# process sweep lives.
param([string]$ExePath, [Parameter(Mandatory)][string]$OutDir)
$ErrorActionPreference = 'Stop'

trap {
    if ("$_" -like 'PRODUCT_FAIL*') {
        Write-Host "$_" -ForegroundColor Red
        exit 2
    }
    break
}

try {
    Write-Host 'selftest: product-throw running'
    throw 'PRODUCT_FAIL: the thing under test did not do the thing'
}
finally {
    # The self-test asserts this file exists, so a trap that skipped the
    # finally would be caught rather than assumed.
    Set-Content -Path (Join-Path $OutDir 'finally-ran.txt') -Value 'cleanup happened'
}
