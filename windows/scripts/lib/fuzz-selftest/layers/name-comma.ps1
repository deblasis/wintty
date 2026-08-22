#requires -Version 7
# The comma rule on the other field it governs. Split-List cuts -Only and -Skip
# on commas exactly as it cuts -Tag, so a name holding one is listed as a real
# harness and neither filter can name it: `-Only 'st,tier'` arrives as two names
# and is refused as a typo, and `-Skip 'st,tier'` skips nothing.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st,tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
