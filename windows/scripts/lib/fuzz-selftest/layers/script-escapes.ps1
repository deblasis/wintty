#requires -Version 7
# A script that climbs out of the directory the suite covers. The target is
# made to exist and to declare the parameters the manifest passes it, so the
# integrity checks downstream have nothing to say about it: only the guard on
# the resolved path can refuse this one.
# The second entry names the same file by its absolute path, which the guard did
# not see at all: joining an absolute path onto this directory produced
# 'C:\<here>\C:\...\escape.ps1', which is inside it, so the guard passed and the
# run was refused for naming a script that does not exist instead.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = '../layer-escape/escape.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
        @{ name = 'st-tier-absolute'
           script = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../layer-escape/escape.ps1'))
           tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
