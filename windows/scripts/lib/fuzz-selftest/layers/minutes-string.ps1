#requires -Version 7
# minutes as text, which is what a hand-written manifest tends to produce.
# '2.6' rather than another '2' so the coerced value reads differently from
# the text it came from: -List is the only output that shows minutes at all,
# so a string that renders the same either way would assert nothing.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier-a'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = '2';   oracle = 'fixture' }
        @{ name = 'st-tier-b'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = '2.6'; oracle = 'fixture' }
    )
}
