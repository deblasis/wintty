#requires -Version 7
# Names a script in a subdirectory whose leaf matches an unclassified script
# sitting at the top level. Comparing leaves rather than relative paths would
# read this as classifying that one.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
