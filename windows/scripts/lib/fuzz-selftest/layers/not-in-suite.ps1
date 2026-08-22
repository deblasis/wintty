#requires -Version 7
# A layer that ships a runner of its own beside the harnesses and classifies
# it. Without notInSuite the only ways to keep that script from failing the
# unclassified-script check were patching the runner, which is what the layer
# exists to avoid, or calling it a harness, which is worse.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
    notInSuite = @{ 'tier-runner.ps1' = 'a runner the tier ships; not a harness' }
}
