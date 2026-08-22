#requires -Version 7
# The same layer written the other way a manifest reasonably gets written.
# A PSCustomObject answers neither .Contains() nor an assignment to a property
# it does not already have, so an entry that arrives as one has to be
# normalised before anything else touches it.
@{
    layer = 'pro'
    harnesses = @(
        [pscustomobject]@{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
