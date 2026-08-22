#requires -Version 7
# minutes that no arithmetic can use, in every shape the manifest can write one.
# Left alone the first reaches the run loop's timeout arithmetic and throws out
# of the foreach that has no catch, taking every verdict already collected with
# it; the rest are what a NUMBER gets wrong rather than what text does, and the
# refusal has to tell them apart. Out of Int32 is a range fact and the range
# branch is where it belongs - said as "non-numeric" it printed a number and
# called it not a number. A list is not a number at all: it is one someone
# wrapped, and the value it printed was the one inside the wrapper.
#
# The overflow entry is the one that got furthest. Coerced but unchecked it
# passed the merge, -List printed a total for it, and the run died in the
# budget's [math]::Max - which takes Int32 and gets the widened product of
# minutes * 60 * 4.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier';          script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 'soon';       oracle = 'fixture' }
        @{ name = 'st-tier-negative'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = -3;           oracle = 'fixture' }
        @{ name = 'st-tier-overflow'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 9000000;      oracle = 'fixture' }
        @{ name = 'st-tier-huge';     script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 2147483648;   oracle = 'fixture' }
        @{ name = 'st-tier-list';     script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = @(2);         oracle = 'fixture' }
    )
}
