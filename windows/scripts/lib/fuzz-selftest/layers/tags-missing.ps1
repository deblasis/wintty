#requires -Version 7
# Both ways a tags list passes a key-presence check and still selects nothing.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier-empty'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @();   outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-null';  script = 'lib/fuzz-selftest/pass.ps1'; tags = $null; outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
