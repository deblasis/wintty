#requires -Version 7
# Keys that are present and empty, which a key-presence check accepts. Kept
# apart from missing-key.ps1 because an entry cannot both omit a key and hold an
# empty value for it, and a fixture named for one holding the other reads wrong.
# The last entry is empty the way a manifest is more likely to write it: the
# guard trims before testing, so whitespace has to be here or the trim is
# unpinned and a value of spaces reaches -List as a harness whose oracle claims
# a pass rules something out without saying what.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = '';                     script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-empty-script'; script = '';                           tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-empty-oracle'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = $null }
        @{ name = 'st-tier-blank-oracle'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = '   ' }
    )
}
