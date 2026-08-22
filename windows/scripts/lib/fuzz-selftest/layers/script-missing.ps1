#requires -Version 7
# A script the tier declares and did not ship. The merge itself has nothing to
# say about it - the path is inside the directory and the entry is well formed -
# so what refuses it is integrity check 1, on the merged manifest.
#
# The second is the other thing that check answers, and the one a bare existence
# test takes for a script: a directory. It is not empty, and it is not missing;
# it is the wrong kind of thing. Left to pass, it reaches the parameter check,
# where the syntax tree of a directory has no param block - so every parameter
# reads as undeclared and the run blames a harness that was never written.
# Both refusals are collected by the same check, so both are read off one run.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier';           script = 'lib/fuzz-selftest/never-shipped.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-directory'; script = 'lib';                                 tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
