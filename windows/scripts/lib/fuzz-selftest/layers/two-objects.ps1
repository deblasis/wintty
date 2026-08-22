#requires -Version 7
# Two manifests in one file: an overlay that appended rather than replaced, or a
# merge conflict resolved by keeping both sides. Member enumeration across the
# array reads it as one manifest whose layer name is both names joined and whose
# harnesses are both sets, so it merges rather than refusing.
@{
    layer = 'common'
    harnesses = @(
        @{ name = 'st-tier-common'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier-pro'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
