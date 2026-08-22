#requires -Version 7
# Scripts that are there and are scripts, and do not declare what the manifest
# passes them. `pwsh -File` drops an argument the script does not declare
# without a word, so a renamed -ExePath leaves every harness testing its own
# default build while the suite reports on the exe that was asked for - which
# is integrity check 3's whole reason for existing.
#
# Two entries, because the check reads three parameters off two different
# sources: -ExePath, which every harness is passed, and -OutDir and -Seed,
# which are passed only because the manifest row says so. The first ships a
# param block that declares one of the three; the second ships none at all.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier-partial'; script = 'tier-partial.ps1'; tags = @('tier'); outDir = $true;  seed = $true;  minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-bare';    script = 'tier-bare.ps1';    tags = @('tier'); outDir = $false; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
