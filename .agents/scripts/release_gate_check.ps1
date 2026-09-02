#requires -Version 7
<#
    Does the shipping-build gate actually REFUSE a leak, in a real Release
    evaluation?

    Every other check on this gate reads windows/Directory.Build.targets and
    asserts the property is WRITTEN correctly. That proves the gate is
    spelled right, not that it behaves -- two different claims, and the repo
    has been bitten by the gap twice (#926, #928). The ladder never built
    Release at all, so `#if !DEBUG` facts and the <Error> conditions were
    unenforced here and only ran in the release repo, against a pin that lags
    (#929).

    Nothing is compiled. The gate target is invoked directly, so each probe
    is an MSBuild evaluation of a few seconds rather than a Release build.

    Two halves, because the two routes into a leaking build are different:

      - RefuseAGateLeakIntoARelease catches DemoEnabled/TestSeamEnabled set
        from anywhere that is not a command-line -p: opt-in. Probed below in
        both polarities and by both routes (a -p: on the derived property,
        and an environment variable), because the mechanism that tells a
        command-line opt-in from any other source is a subtle one: the
        targets file reassigns Demo/TestSeam to "not-a-global", which MSBuild
        silently discards for a global property and applies for every other
        kind. If that ever stops working, the sanctioned opt-in starts
        failing and the leak starts passing -- so both directions are pinned.

      - ShippingBuildGateTests covers what the target cannot see: a
        DefineConstants;DEMO written straight into a project file never sets
        DemoEnabled, so no <Error> fires and only the compiled result gives
        it away. Those tests are #if !DEBUG, so they need a real Release
        test run.

    Exits 0 when the gate holds, 1 when it does not or the check could not run.
#>
param(
    # Skip the Release test run; probe the build-time target only. For a
    # caller that has already run it, not for making a red run green.
    [switch]$TargetOnly
)
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$proj = "$repo/windows/Ghostty/Ghostty.csproj"
$tests = "$repo/windows/Ghostty.Tests/Ghostty.Tests.csproj"
$target = 'RefuseAGateLeakIntoARelease'

if (-not (Test-Path $proj)) { Write-Host "HARNESS: missing $proj"; exit 1 }

$failures = 0

function Invoke-Probe {
    param(
        [Parameter(Mandatory)][string]$What,
        [string[]]$BuildArgs = @(),
        # 'OK' for a build that must succeed, or the MSBuild code the refusal
        # must carry. A refusal with the wrong code is a failure: it means
        # something else broke, not that the gate fired.
        [Parameter(Mandatory)][string]$Want,
        [hashtable]$WithEnv = @{}
    )
    $saved = @{}
    foreach ($k in $WithEnv.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k)
        [Environment]::SetEnvironmentVariable($k, $WithEnv[$k])
    }
    try {
        $out = & dotnet build $proj -c Release /p:Platform=x64 -t:$target @BuildArgs 2>&1
        $rc = $LASTEXITCODE
    }
    finally {
        foreach ($k in $WithEnv.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
    $code = ($out | Select-String -Pattern 'WINTTY000[12]' | Select-Object -First 1)
    $seen = if ($code) { ($code.Line -replace '.*(WINTTY000[12]).*', '$1') } else { '-' }

    $ok = if ($Want -eq 'OK') { $rc -eq 0 } else { $rc -ne 0 -and $seen -eq $Want }
    if ($ok) {
        Write-Host ("  ok    {0,-44} (rc={1} code={2})" -f $What, $rc, $seen)
    }
    else {
        Write-Host ("  FAIL  {0,-44} (rc={1} code={2}, wanted {3})" -f $What, $rc, $seen, $Want)
        $script:failures++
    }
}

Write-Host 'release-gate: the build-time refusal'
# Control first. If a plain Release evaluation cannot even run, every refusal
# below would "pass" for the wrong reason.
Invoke-Probe -What 'plain Release evaluates'          -Want 'OK'
Invoke-Probe -What 'sanctioned -p:Demo=true'          -Want 'OK' -BuildArgs @('/p:Demo=true')
Invoke-Probe -What 'sanctioned -p:TestSeam=true'      -Want 'OK' -BuildArgs @('/p:TestSeam=true')
Invoke-Probe -What 'leak: -p:DemoEnabled=true'        -Want 'WINTTY0002' -BuildArgs @('/p:DemoEnabled=true')
Invoke-Probe -What 'leak: -p:TestSeamEnabled=true'    -Want 'WINTTY0001' -BuildArgs @('/p:TestSeamEnabled=true')
Invoke-Probe -What 'leak: Demo=true in the env'       -Want 'WINTTY0002' -WithEnv @{ Demo = 'true' }
Invoke-Probe -What 'leak: TestSeam=true in the env'   -Want 'WINTTY0001' -WithEnv @{ TestSeam = 'true' }

if (-not $TargetOnly) {
    Write-Host ''
    Write-Host 'release-gate: the compiled-result tests (#if !DEBUG, so Release only)'
    $out = & dotnet test $tests -c Release /p:Platform=x64 --nologo `
        --filter 'FullyQualifiedName~ShippingBuildGateTests' 2>&1
    $rc = $LASTEXITCODE
    $summary = ($out | Select-String -Pattern 'Passed!|Failed!|No test matches' | Select-Object -First 1)
    if ($summary) { Write-Host ("  {0}" -f $summary.Line.Trim()) }

    # A filter that matches nothing exits 0. Green on zero tests is exactly
    # the shape this whole issue is about, so the count is asserted, not the
    # exit code alone.
    $ran = ($out | Select-String -Pattern 'Passed:\s+(\d+)' | Select-Object -First 1)
    $count = if ($ran) { [int]($ran.Matches[0].Groups[1].Value) } else { 0 }
    if ($rc -ne 0) {
        Write-Host '  FAIL  the Release gate tests did not pass'
        $script:failures++
    }
    elseif ($count -lt 1) {
        Write-Host '  FAIL  the filter matched no tests, so this leg proved nothing'
        $script:failures++
    }
    else {
        Write-Host ("  ok    {0} Release gate test(s) ran and passed" -f $count)
    }
}

Write-Host ''
if ($failures -gt 0) {
    Write-Host ("release-gate: {0} check(s) failed" -f $failures)
    exit 1
}
Write-Host 'release-gate: the shipping gate refuses what it should and admits what it should'
exit 0
