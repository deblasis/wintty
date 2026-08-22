#requires -Version 7
# minutes that no arithmetic can use. Left alone it reaches the run loop's
# timeout arithmetic and throws out of the foreach that has no catch.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 'soon'; oracle = 'fixture' }
    )
}
