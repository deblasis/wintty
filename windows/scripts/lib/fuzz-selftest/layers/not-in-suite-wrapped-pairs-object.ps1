#requires -Version 7
# The wrapped pairs again, written the other way a manifest writes an object.
# A hashtable inside the list answers IDictionary; this one answers neither
# that nor anything else the shape test asks about by name, so the arm that
# looks for a custom object is the only thing standing between it and being
# told it wrote a list of names - which is the refusal aimed at the half the
# author got right.
@{
    layer = 'pro'
    harnesses = @(
        @{ name = 'st-tier'; script = 'lib/fuzz-selftest/pass.ps1'; tags = @('tier'); outDir = $true; seed = $false; minutes = 1; oracle = 'fixture' }
    )
    notInSuite = @( [pscustomobject]@{ 'tier-runner.ps1' = 'a runner the tier ships' } )
}
