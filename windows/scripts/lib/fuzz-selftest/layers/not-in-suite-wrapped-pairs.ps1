#requires -Version 7
# The pairs form with a list around it, which is what a manifest author writes
# after typing `harnesses = @( ... )` two lines above. Read as a list of names
# it is answered with "not a list of names", which is a refusal aimed at the
# half that was right: the pairs are pairs, and the wrapper is the mistake.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
    notInSuite = @( @{ 'tier-runner.ps1' = 'a runner the tier ships' } )
}
