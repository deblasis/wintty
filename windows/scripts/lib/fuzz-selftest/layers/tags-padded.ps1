#requires -Version 7
# Tags that a truthiness test reads as declared and no -Tag argument can reach.
# Split-List trims the caller's side, so ' tier ' listed as a real tag while
# `-Tag ' tier '` arrived as 'tier' and matched nothing - the very condition the
# no-tags guard's message describes, passing the guard. The numeric one is here
# because the fix compares tags as text: 0 is as selectable as '0', so it stays.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier-padded';  script = 'lib/fuzz-selftest/pass.ps1'; tags = @(' tier ', 'x'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-numeric'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @(0);             outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
