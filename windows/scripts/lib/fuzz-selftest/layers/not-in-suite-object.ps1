#requires -Version 7
# notInSuite written the other way a manifest reasonably gets written. A
# PSCustomObject has no .Keys, so an unnormalised read is a silent no-op and the
# run dies at the unclassified-script check naming the file this classifies.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
    notInSuite = [pscustomobject]@{ 'tier-runner.ps1' = 'a runner the tier ships; not a harness' }
}
