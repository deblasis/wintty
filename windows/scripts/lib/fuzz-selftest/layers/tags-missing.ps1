#requires -Version 7
# Every way a tags list passes a key-presence check and still selects nothing.
# The blank one is the case a plain truthiness test cannot see: only '' is falsy
# in PowerShell, so '  ' survives a bare `Where-Object { $_ }` while no -Tag
# argument can ever match it.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier-empty'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @();          outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-null';  script = 'lib/fuzz-selftest/pass.ps1'; tags = $null;        outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-blank'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('', '  '); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
