#requires -Version 7
# The pairs form with nothing in the second half. A bare list of names is
# refused because the reason is the only place a tier says why a script of its
# own is not a harness; an empty reason says exactly as much, and an empty
# oracle is refused a few lines earlier on the same argument.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
    notInSuite = @{ 'tier-runner.ps1' = '' }
}
