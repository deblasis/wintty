#requires -Version 7
# A classified name that opens with a dot. Reducing './x.ps1' by trimming the
# characters '.' and '\' rather than the prefix takes this one down to
# 'helper.ps1', which matches no file on disk - so the run dies at the
# unclassified-script check naming the very file this manifest classifies.
# Rare on Windows, and the same wrong blame as a padded name either way.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
    notInSuite = @{ '.helper.ps1' = 'a dot-prefixed helper the tier ships; not a harness' }
}
