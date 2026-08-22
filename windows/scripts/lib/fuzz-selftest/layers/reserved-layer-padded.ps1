#requires -Version 7
# The reserved name with padding on it. The refusal compares the name it was
# given, so untrimmed this took the name the runner gives its own harnesses and
# -List reported 'layers: base (19) +  base  (1)' - two layers whose counts
# nothing downstream can tell apart, which is what the refusal exists to stop.
@{
    layer = ' base '
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
}
