#requires -Version 7
# A classification whose name carries padding. It passes every check in the
# merge, goes into the collection padded, and the unclassified-script check then
# compares the name of a file on disk against it and misses - so the run dies
# blaming the tier for a file it classified, which is the failure normalising
# the pairs was added to remove, arriving through the door normalising left
# open. Padded on both sides: the rule is symmetric, and a fixture that pads one
# end leaves half of it unpinned.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
    notInSuite = @{ ' tier-runner.ps1 ' = 'a runner the tier ships; not a harness' }
}
