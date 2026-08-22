#requires -Version 7
# The same excuse-a-harness case, written the way a tier writes a path beside
# the runner. notInSuite names a bare leaf, so the manifest's own scripts have
# to be reduced to leaves before the two can be compared: taken as written,
# './tier-runner.ps1' is not 'tier-runner.ps1' and the one door a tier can open
# on the unclassified-script check opens on a script the tier declared.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = './tier-runner.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
    notInSuite = @{ 'tier-runner.ps1' = 'a runner the tier ships; not a harness' }
}
