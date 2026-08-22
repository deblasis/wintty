#requires -Version 7
# A script in a sibling directory whose full path opens with this directory's,
# character for character. A prefix test that stops before the separator reads
# it as inside; only one that requires the separator refuses it.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = '../layer-scriptsX/escape.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
