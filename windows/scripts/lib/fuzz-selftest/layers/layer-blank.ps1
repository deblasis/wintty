#requires -Version 7
# A layer name that is only padding, which a truthiness test reads as declared.
# The name is what -List prints and what -RequireLayer matches, so a blank one
# merges harnesses under a layer with nothing to call it and -List reports a
# suite bigger than the base set as base-only - the same event as no-layer.ps1,
# reached through a key that is present.
@{
    layer = '   '
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
