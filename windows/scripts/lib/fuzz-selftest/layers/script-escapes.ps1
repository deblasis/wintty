#requires -Version 7
# A script that climbs out of the directory the suite covers. The target is
# made to exist and to declare the parameters the manifest passes it, so the
# integrity checks downstream have nothing to say about it: only the guard on
# the resolved path can refuse this one.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = '../layer-escape/escape.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
