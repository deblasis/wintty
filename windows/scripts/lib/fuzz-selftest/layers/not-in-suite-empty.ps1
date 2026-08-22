#requires -Version 7
# An empty collection, which is the list form with nothing in it. Read for
# truthiness rather than presence it is skipped entirely, so a tier that wrote
# its classifications the wrong shape was told to classify a script rather than
# told the shape was wrong.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
    notInSuite = @()
}
