#requires -Version 7
<#
    Does the shipping-build gate actually REFUSE a leak, in a real Release
    evaluation?

    Every other check on this gate reads windows/Directory.Build.targets and
    asserts the property is WRITTEN correctly. That proves the gate is spelled
    right, not that it behaves -- two different claims, and the repo has been
    bitten by the gap twice (#926, #928). The ladder never built Release at
    all, so the #if !DEBUG facts and the <Error> conditions were unenforced
    here and ran only in the release repo, against a pin that lags (#929).

    Three probe sets, because there are three different ways a leak gets in.

    1. THE REFUSAL. RefuseAGateLeakIntoARelease catches DemoEnabled or
       TestSeamEnabled set from anywhere that is not a command-line -p:
       opt-in. Probed in both polarities and by both routes, because the
       mechanism separating a command-line opt-in from every other source is
       subtle: the targets file reassigns Demo/TestSeam to "not-a-global",
       which MSBuild discards for a global property and applies for
       everything else. If that stops working the sanctioned opt-in starts
       failing AND the leak starts passing, so both directions are pinned.

    2. THE EVALUATED CONSTANTS. The target reads DemoEnabled, never
       DefineConstants -- so a `DefineConstants;DEMO` written straight into a
       project file is invisible to it. Nothing else looks either: the test
       projects deliberately do not reference Ghostty.csproj, so no leg builds
       or loads Wintty.dll. This asks MSBuild what Release actually evaluates
       DefineConstants to, for every project under windows/, and refuses DEMO
       or TESTSEAM. It carries its own control -- under -p:Demo=true the token
       MUST appear -- because "no DEMO found" is also what a query returning
       nothing looks like.

    3. THE COMPILED RESULT. ShippingBuildGateTests covers what neither of the
       above can: sources compiled in with no property saying so. Those facts
       are #if !DEBUG, so they need a real Release test run, and each is
       asserted BY NAME with a count of exactly one. A floor of one does not
       work here -- the class has four facts and only two are Release-only, so
       a floor is satisfied by the two that already run in Debug, and
       inverting the #if !DEBUG guard would pass.

    What this does NOT prove, stated so the leg is not read as wider than it
    is: that the target is still WIRED into a build (it is invoked by name
    here, so a broken BeforeTargets would not show -- the file-text checks in
    ShippingBuildGateTests cover that, and this complements them rather than
    superseding them); that a Release build compiles at all; and nothing about
    solution-level configuration mapping in Ghostty.sln.

    Exits 0 when the gate holds, 1 when it does not or the check could not run.
#>
param(
    # Skip the Release test run; probe MSBuild only. For a caller that has
    # already run it, not for making a red run green.
    [switch]$NoTestRun
)
$ErrorActionPreference = 'Stop'

# VSTest and the SDK localise their output, and this parses it. Pin English so
# a non-English host fails on the gate rather than on the language.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$app = Join-Path $repo 'windows/Ghostty/Ghostty.csproj'
$tests = Join-Path $repo 'windows/Ghostty.Tests/Ghostty.Tests.csproj'
$target = 'RefuseAGateLeakIntoARelease'
if (-not (Test-Path $app)) { Write-Host "HARNESS: missing $app"; exit 1 }

$script:failures = 0
function Fail([string]$Text) { Write-Host "  FAIL  $Text"; $script:failures++ }
function Pass([string]$Text) { Write-Host "  ok    $Text" }

# ---- 1. the refusal ------------------------------------------------------

function Invoke-Refusal {
    param(
        [Parameter(Mandatory)][string]$What,
        [string[]]$BuildArgs = @(),
        # 'OK', or the MSBuild code the refusal must carry. A refusal with the
        # wrong code is a failure: it means something else broke, not that the
        # gate fired.
        [Parameter(Mandatory)][string]$Want,
        [hashtable]$WithEnv = @{}
    )
    $saved = @{}
    foreach ($k in $WithEnv.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k)
        [Environment]::SetEnvironmentVariable($k, $WithEnv[$k])
    }
    try {
        $out = & dotnet build $app -c Release /p:Platform=x64 -t:$target @BuildArgs 2>&1
        $rc = $LASTEXITCODE
    }
    finally {
        foreach ($k in $WithEnv.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }

    $line = $out | Select-String -Pattern 'WINTTY000[12]' | Select-Object -First 1
    # Match, not a greedy replace: a line naming both codes reports the first.
    $seen = if ($line) { [regex]::Match($line.Line, 'WINTTY000[12]').Value } else { '-' }

    if ($Want -eq 'OK') {
        if ($rc -eq 0) { Pass "$What (rc=0)" }
        else { Fail "$What (rc=$rc code=$seen, wanted success)" }
    }
    elseif ($rc -ne 0 -and $seen -eq $Want) { Pass "$What (refused $seen)" }
    else { Fail "$What (rc=$rc code=$seen, wanted $Want)" }
}

Write-Host 'release-gate 1/3: the build-time refusal'
Invoke-Refusal -What 'plain Release evaluates'        -Want 'OK'
Invoke-Refusal -What 'sanctioned -p:Demo=true'        -Want 'OK' -BuildArgs @('/p:Demo=true')
Invoke-Refusal -What 'sanctioned -p:TestSeam=true'    -Want 'OK' -BuildArgs @('/p:TestSeam=true')
Invoke-Refusal -What 'leak: -p:DemoEnabled=true'      -Want 'WINTTY0002' -BuildArgs @('/p:DemoEnabled=true')
Invoke-Refusal -What 'leak: -p:TestSeamEnabled=true'  -Want 'WINTTY0001' -BuildArgs @('/p:TestSeamEnabled=true')
Invoke-Refusal -What 'leak: Demo=true in the env'     -Want 'WINTTY0002' -WithEnv @{ Demo = 'true' }
Invoke-Refusal -What 'leak: TestSeam=true in the env' -Want 'WINTTY0001' -WithEnv @{ TestSeam = 'true' }

# ---- 2. the evaluated constants ------------------------------------------

function Get-Constants {
    param([Parameter(Mandatory)][string]$Proj, [string[]]$Extra = @())
    $out = & dotnet msbuild $Proj -p:Configuration=Release -p:Platform=x64 `
        -getProperty:DefineConstants @Extra 2>&1
    if ($LASTEXITCODE -ne 0) { return $null }
    # Split into tokens rather than substring-matching: TESTSEAM_OPTIN
    # contains TESTSEAM, and DEMO_OPTIN contains DEMO.
    return @(($out | Select-Object -Last 1).ToString().Split(';') |
        ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

Write-Host ''
Write-Host 'release-gate 2/3: what a Release evaluation actually defines'

$sep = [IO.Path]::DirectorySeparatorChar
$projects = Get-ChildItem (Join-Path $repo 'windows') -Recurse -Filter '*.csproj' |
    Where-Object {
        $parts = $_.FullName.Split($sep)
        ($parts -notcontains 'obj') -and ($parts -notcontains 'bin')
    } | Sort-Object Name

if ($projects.Count -lt 1) { Fail 'found no project files to evaluate' }

# The control first. If the query cannot see a constant that IS there, every
# clean result below means nothing.
$withOptIn = Get-Constants -Proj $app -Extra @('-p:Demo=true')
if ($null -eq $withOptIn) { Fail 'the DefineConstants query failed on the app project' }
elseif ($withOptIn -notcontains 'DEMO') { Fail 'the query cannot see DEMO even under -p:Demo=true, so it proves nothing' }
else { Pass 'control: the query does see DEMO under -p:Demo=true' }

foreach ($p in $projects) {
    $c = Get-Constants -Proj $p.FullName
    if ($null -eq $c) { Fail ('could not evaluate DefineConstants for {0}' -f $p.Name); continue }
    $leaked = @($c | Where-Object { $_ -eq 'DEMO' -or $_ -eq 'TESTSEAM' })
    if ($leaked.Count -gt 0) { Fail ('{0} defines {1} in Release' -f $p.Name, ($leaked -join ',')) }
    else { Pass ('{0}: neither DEMO nor TESTSEAM' -f $p.Name) }
}

# ---- 3. the compiled result ----------------------------------------------

if (-not $NoTestRun) {
    Write-Host ''
    Write-Host 'release-gate 3/3: the compiled-result facts (#if !DEBUG, so Release only)'
    foreach ($fact in 'A_shipping_build_carries_no_demo_code', 'A_shipping_build_carries_no_test_seam') {
        $out = & dotnet test $tests -c Release /p:Platform=x64 --nologo `
            --filter "FullyQualifiedName~$fact" 2>&1
        $rc = $LASTEXITCODE
        $m = $out | Select-String -Pattern 'Passed:\s+(\d+)' | Select-Object -First 1
        $n = if ($m) { [int]$m.Matches[0].Groups[1].Value } else { 0 }
        # By name, and exactly one. A filter matching nothing exits 0, and this
        # class also holds facts that run in Debug -- so neither the exit code
        # nor a count floor can carry this claim.
        if ($rc -ne 0) { Fail "$fact did not pass" }
        elseif ($n -ne 1) { Fail "$fact ran $n time(s), wanted exactly 1 -- is it still #if !DEBUG?" }
        else { Pass "$fact ran and passed" }
    }
}

Write-Host ''
if ($script:failures -gt 0) {
    Write-Host ('release-gate: {0} check(s) failed' -f $script:failures)
    exit 1
}
Write-Host 'release-gate: the shipping gate refuses what it should and admits what it should'
exit 0
