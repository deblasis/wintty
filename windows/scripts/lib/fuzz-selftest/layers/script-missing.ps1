#requires -Version 7
# A script the tier declares and did not ship. The merge itself has nothing to
# say about it - the path is inside the directory and the entry is well formed -
# so what refuses it is integrity check 1, on the merged manifest.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/never-shipped.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
