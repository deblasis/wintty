#requires -Version 7
# notInSuite written as a list of names rather than name = reason pairs. Read
# through PSObject.Properties an array answers with Length, Rank and the rest,
# so it classifies nothing while looking like it classified something.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
    notInSuite = @('tier-runner.ps1')
}
