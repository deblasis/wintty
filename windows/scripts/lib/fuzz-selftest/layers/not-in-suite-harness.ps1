#requires -Version 7
# notInSuite excusing a script the manifest itself names as a harness. This is
# the one door a tier can open on the unclassified-script check, so a lenient
# read of it hands back the silent shrink the strict read exists to stop.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
    notInSuite = @{ 'search-fuzz.ps1' = 'not ours to excuse' }
}
