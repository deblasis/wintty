# A PowerShell class static method spelled ::Start (the GapSampler shape):
# not a process launch, and not the Process]::Start( form the scan flags
# as its own verb.
class GapSampler {
    static [void] Start($probe, $arm) { }
}
[GapSampler]::Start($probe, $true)
