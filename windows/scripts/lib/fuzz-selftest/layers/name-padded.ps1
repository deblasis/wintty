#requires -Version 7
# Names a check trimmed to test and then stored raw. ' search ' is the name of a
# base harness, and the collision check compares what was stored - so untrimmed
# the two never meet, `-Only search` selects the base one, `-Skip search` does
# not skip this one, and the tier harness is declared, listed and permanently
# unreachable. The pair below it is the same event inside one layer, where the
# duplicate check is what misses.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = ' search '; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'dupe';     script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = ' dupe ';   script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
