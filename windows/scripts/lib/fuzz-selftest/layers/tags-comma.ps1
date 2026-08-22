#requires -Version 7
# The other half of the tag rule Split-List sets. It cuts the caller's -Tag on a
# comma, so a tag declared with one in it is listed as a real tag and no -Tag
# argument can reach it: -List shows 'a,b' and `-Tag 'a,b'` arrives as two
# values, neither of which is what was stored. -List joins tags with a comma
# too, so splitting it here instead would be a silent reinterpretation nothing
# downstream could report. The second entry is the same character reached by
# trimming rather than by typing. The third declares a good tag first, so a
# check that reads one tag per harness and stops has something to miss.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier-comma';  script = 'lib/fuzz-selftest/pass.ps1'; tags = @('a,b');       outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-padded'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @(' c,d ');     outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-second'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('ok', 'e,f'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
