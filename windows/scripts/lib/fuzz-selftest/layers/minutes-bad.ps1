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
#
# The last four are the classes the refusal answered with somebody else's
# sentence, or did not answer at all. NaN and the infinities are doubles, so the
# range branch claimed them and said a value with no place on a range was off
# the end of one. A table is enumerable, so the list branch told an author to
# drop a @( ) they never wrote. And true and a blank string are not refused
# anywhere: they coerce to 1 and to 0, which is a budget the manifest never
# asked for and nothing downstream can tell from one it did.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier';          script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 'soon';       oracle = 'fixture' }
        @{ name = 'st-tier-negative'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = -3;           oracle = 'fixture' }
        @{ name = 'st-tier-overflow'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 9000000;      oracle = 'fixture' }
        @{ name = 'st-tier-huge';     script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 2147483648;   oracle = 'fixture' }
        @{ name = 'st-tier-list';     script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = @(2);         oracle = 'fixture' }
        @{ name = 'st-tier-nan';      script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = [double]::NaN; oracle = 'fixture' }
        @{ name = 'st-tier-table';    script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = @{};          oracle = 'fixture' }
        @{ name = 'st-tier-bool';     script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = $true;        oracle = 'fixture' }
        @{ name = 'st-tier-blank';    script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = '   ';        oracle = 'fixture' }
    )
}
