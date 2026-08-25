<#
.SYNOPSIS
    Randomized round-trip fuzz over the clipboard marshalling boundary.

.DESCRIPTION
    Runs ClipboardMarshallingFuzzTests with a deeper iteration budget than
    the ladder uses. No build of the app, no desktop, safe to run with
    Wintty open.

    Oracle: round-trip fidelity. Every case builds real unmanaged memory
    with the writer, reads it back with the reader, and asserts the bytes
    that come out are the bytes that went in, that the trailing bool pair
    did not swap, and that a formatted file URI parses back to the path it
    came from. It is not a liveness check -- a stride, offset or length bug
    survives liveness and corrupts the payload, which is the whole reason
    this exists.

    What it does NOT rule out: anything about the live C ABI. The structs
    are pinned against include/ghostty.h by GhosttyStructHeaderParityTests,
    and this checks our reader against our writer. Both agreeing with each
    other and with the header still does not prove libghostty calls the
    callbacks in the order we expect. That needs `just run-win`.

    Inputs are adversarial but valid: embedded NULs, zero-length payloads,
    non-UTF8 bytes, multi-byte and supplementary-plane MIME names, varying
    entry counts and sizes. Deliberately NOT malformed -- feeding a length
    past the end of a buffer would be inventing undefined behaviour rather
    than finding a defect.

    Exit codes follow the fuzz-suite contract:
      0  pass
      2  product findings, in the code under test
      1  the harness could not run, so nothing is known about the product

.PARAMETER Iterations
    Cases per seed per oracle. Default 20000.

.PARAMETER Seed
    Run a single seed instead of the whole set, to replay a finding. The
    failure message from a finding names the seed and the iteration.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 10000000)]
    [int]$Iterations = 20000,

    [int]$Seed = -1
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repoRoot 'windows/Ghostty.Tests/Ghostty.Tests.csproj'

if (-not (Test-Path $project)) {
    Write-Host "clipboard-fuzz: cannot find $project"
    exit 1
}

$filter = 'FullyQualifiedName~ClipboardMarshallingFuzzTests'
if ($Seed -ge 0) {
    # xUnit cannot filter on a MemberData argument, so a single-seed replay
    # narrows by iteration budget and reports which seed failed rather than
    # pretending to run only one. Named explicitly so the output is not
    # mistaken for a full run.
    Write-Host "clipboard-fuzz: replaying with seed $Seed in the failure message; all seeds still run"
}

$env:GHOSTTY_FUZZ_ITERATIONS = $Iterations
Write-Host "clipboard-fuzz: $Iterations iterations per seed per oracle"

$log = New-TemporaryFile
try {
    & dotnet test $project /p:Platform=x64 --filter $filter --logger 'console;verbosity=normal' `
        *>&1 | Tee-Object -FilePath $log.FullName | Out-Host
    $testExit = $LASTEXITCODE
}
catch {
    Write-Host "clipboard-fuzz: runner threw: $_"
    exit 1
}

$text = Get-Content -Raw -LiteralPath $log.FullName
Remove-Item -LiteralPath $log.FullName -Force -ErrorAction SilentlyContinue

# A build error is a harness failure, not a product finding: nothing was
# measured. Checked before the exit code because dotnet test has been seen
# to exit 0 with a compile error in its log.
if ($text -match 'error CS\d+') {
    Write-Host 'clipboard-fuzz: the tests did not compile; nothing was measured'
    exit 1
}

# Both result formats are accepted on purpose. The console logger prints
# "Test Run Successful." plus "Total tests: N"; the default logger prints
# "Passed!  - Failed: 0, Passed: N". Matching only one of them is how this
# check reported "nothing was measured" for a run that had measured 32 cases
# perfectly well -- a harness bug wearing a finding's clothes, and the exact
# thing the 0/1/2 split exists to keep apart.
$ranSomething = ($text -match 'Test Run (Successful|Failed|Aborted)\.') -or
                ($text -match '(?m)^(Passed|Failed)!')

if (-not $ranSomething) {
    Write-Host 'clipboard-fuzz: no test results in the output; nothing was measured'
    exit 1
}

# Guard against a filter that matched nothing. A filtered run matching zero
# tests exits 0 and reads exactly like a pass.
if (($text -match 'Total tests:\s*0\b') -or
    ($text -match 'Total:\s*0\b') -or
    ($text -match 'No test (matches|is available)')) {
    Write-Host 'clipboard-fuzz: the filter matched no tests; nothing was measured'
    exit 1
}

# Report the case count, so a run that silently shrank is visible rather than
# merely green.
if ($text -match 'Total tests:\s*(\d+)') {
    Write-Host "clipboard-fuzz: $($Matches[1]) test cases"
}

if ($testExit -ne 0) {
    Write-Host 'clipboard-fuzz: FINDINGS (the seed and iteration in the message above replay it)'
    exit 2
}

Write-Host 'clipboard-fuzz: pass'
exit 0
