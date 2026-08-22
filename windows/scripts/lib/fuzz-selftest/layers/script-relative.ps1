#requires -Version 7
# A script named the way a path beside the runner is often written. The
# unclassified-script check compares bare leaf names, so './tier-relative.ps1'
# has to be reduced to one before it can match the file on disk - taken as it
# is written it classifies nothing, and the run dies telling the tier author to
# classify a script their own manifest names as a harness.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = './tier-relative.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
