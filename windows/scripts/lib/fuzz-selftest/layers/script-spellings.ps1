#requires -Version 7
# Four spellings of a path beside the runner that a prefix strip does not
# reduce. Every one of them names a file the tier really ships, and unreduced
# every one of them fails the unclassified-script check naming the very file the
# manifest declared - the wrong blame './name.ps1' was reduced to remove, still
# live for every other way of writing the same path. The fourth is the same
# reduction on a padded value, which the emptiness test trimmed and threw away.
#
# The fifth is not a spelling of anything: it is a plain file name that happens
# to hold a wildcard character. Read by an existence test that takes a pattern
# rather than a path it matches nothing, so a harness sitting on disk with a
# correct param block was refused as a script that does not exist - the same
# wrong blame arriving without the manifest having written anything unusual.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier-dots';    script = '.\.\tier-a.ps1';                                  tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-updown';  script = 'lib/../tier-b.ps1';                               tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-outback'; script = "../$(Split-Path -Leaf $PSScriptRoot)/tier-c.ps1"; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-padded';  script = ' lib/fuzz-selftest/pass.ps1 ';                    tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-bracket'; script = 'tier-d[1].ps1';                                   tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
