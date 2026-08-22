#requires -Version 7
# One entry per key the runner reads later without checking again, each short a
# different one. The loop collects every missing key before the run exits, so
# covering all seven costs one child run rather than seven - and a key dropped
# from the list it checks is invisible against a fixture that omits only one.
#
# The entry with no name is the one worth keeping honest about: the message
# names the offender, and that one has nothing to name itself with.
@{
    layer = 'pro'
    harnesses = @(
        @{                             script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-no-script';                                        tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-no-tags';    script = 'lib/fuzz-selftest/pass.ps1';                   outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-no-outdir';  script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier');                 seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-no-seed';    script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true;                minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-no-minutes'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false;              oracle = 'fixture' }
        @{ name = 'st-tier-no-oracle';  script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1                     }
    )
}
