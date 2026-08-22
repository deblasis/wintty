#requires -Version 7
# Keys that are present and empty, which a key-presence check accepts. Kept
# apart from missing-key.ps1 because an entry cannot both omit a key and hold an
# empty value for it, and a fixture named for one holding the other reads wrong.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = '';                     script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-empty-script'; script = '';                           tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-empty-oracle'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = $null }
    )
}
