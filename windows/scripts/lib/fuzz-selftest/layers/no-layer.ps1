#requires -Version 7
# Harnesses and no name to run them under. This is the sharper half of the shape
# check: the merge takes the harness, nothing downstream has a layer name to
# print, and -List reports a suite one bigger than the base set as "base only".
@{
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
