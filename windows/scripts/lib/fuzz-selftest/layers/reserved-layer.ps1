#requires -Version 7
# The layer name the runner gives its own harnesses. Taking it would make the
# base and tier counts in the summary indistinguishable.
@{
    layer = 'base'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
