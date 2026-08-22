#requires -Version 7
# A name the base set already uses.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'search'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
