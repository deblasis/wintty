#requires -Version 7
# The same excuse-a-harness case, written the way a tier writes a path beside
# the runner. notInSuite names a bare file name, so the manifest's own scripts
# have to be resolved before the two can be compared: taken as written,
# './tier-runner.ps1' is not 'tier-runner.ps1' and the one door a tier can open
# on the unclassified-script check opens on a script the tier declared.
# The second entry is a spelling a leading-'./' strip does not reduce either,
# and there are many of those: reducing by prefix closed one of them.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier';       script = './tier-runner.ps1';       tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-asset'; script = 'lib/../tier-asset.ps1';   tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
    notInSuite = @{
        'tier-runner.ps1' = 'a runner the tier ships; not a harness'
        'tier-asset.ps1'  = 'an asset the tier ships; not a harness'
    }
}
