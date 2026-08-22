#requires -Version 7
# Names the unclassified-script check cannot act on. It compares leaf names, so
# one carrying a separator excuses nothing while reading as though it did, and a
# blank one excuses nothing at all. Both are accepted in silence without the
# plain-file-name guard, and the run then fails somewhere else or not at all.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
    notInSuite = @{
        'lib/tier-runner.ps1' = 'a runner the tier keeps in a subdirectory'
        '   '                 = 'nothing at all'
    }
}
