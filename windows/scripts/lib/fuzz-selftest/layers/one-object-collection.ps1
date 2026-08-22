#requires -Version 7
# The wrapper, at length one. A leading comma emits the manifest inside an
# array, which is a collection whatever it holds - and a message that counts the
# objects tells this author it emitted one object when it must emit exactly one.
,@(
    @{
        layer = 'pro'
        harnesses = @(
            @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        )
    }
)
