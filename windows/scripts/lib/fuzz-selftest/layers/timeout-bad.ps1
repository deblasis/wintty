#requires -Version 7
# timeoutSeconds is the field minutes' guard was written for and never covered.
# It is optional, so nothing requires it, and it was read straight by the run
# loop: text throws out of the foreach that has no catch, which discards every
# verdict already collected and turns a run with findings into exit 1.
#
# The rest are what a number gets wrong rather than what text does. This field
# is the override that SKIPS the three-minute floor, so nothing else stands
# between a budget of nothing and every attempt being killed the instant it
# starts - reported as a wedged harness, which sends the reader to the product.
# A null is here because it coerces to 0 rather than to nothing, so only the
# floor can see it. The last two are the input classes the refusal used to
# misname: out of Int32 is a range fact, and a list is a number in a wrapper.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier-text';     script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; timeoutSeconds = 'soon';      oracle = 'fixture' }
        @{ name = 'st-tier-zero';     script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; timeoutSeconds = 0;           oracle = 'fixture' }
        @{ name = 'st-tier-negative'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; timeoutSeconds = -5;          oracle = 'fixture' }
        @{ name = 'st-tier-null';     script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; timeoutSeconds = $null;       oracle = 'fixture' }
        @{ name = 'st-tier-huge';     script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; timeoutSeconds = 2147483648;  oracle = 'fixture' }
        @{ name = 'st-tier-list';     script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; timeoutSeconds = @(30);       oracle = 'fixture' }
    )
}
