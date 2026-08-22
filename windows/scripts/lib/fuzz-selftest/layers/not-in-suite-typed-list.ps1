#requires -Version 7
# The list form written as a typed collection rather than as an array literal.
# A test asking "is it System.Array" answers no to this, to an ArrayList and to
# anything else enumerable, so it fell through to the object read and had Count
# and Capacity taken off it as file names - the tier is then told to classify a
# script it classified, which is the wrong blame the pairs-form refusal exists
# to close, still live for one shape.
#
# The top-level manifest check next to it is deliberately NOT written this way:
# that value comes off a pipeline, which unrolls any enumerable and collects it
# again as an array, so there is no other shape for it to arrive in. This one is
# read as a property and keeps whatever type the manifest gave it.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
    notInSuite = [System.Collections.Generic.List[string]]@('tier-runner.ps1')
}
