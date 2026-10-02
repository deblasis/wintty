#requires -Version 7
# Run mouse/UIA fuzz suite against NativeAOT-published Wintty.exe.
param(
    [string]$PublishExe = (Join-Path $PSScriptRoot '../Ghostty/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/publish/Wintty.exe'),
    [switch]$SkipPublish,
    [string[]]$Scripts = @(
        'mouse-fuzz-inspector.ps1',
        'mouse-fuzz-dialogs.ps1',
        'mouse-fuzz-ime-cjk.ps1'
    )
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
$ErrorActionPreference = 'Stop'

# A harness exiting non-zero is this runner's input, not an error. When
# $PSNativeCommandUseErrorActionPreference is on - it is the default on some
# hosts and a profile can set it - $ErrorActionPreference = 'Stop' turns the
# first harness that reports findings into a terminating error, and the run
# ends with no summary and no verdict. Assign it only where it exists.
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$PublishExe = (Resolve-Path -LiteralPath $PublishExe -ErrorAction SilentlyContinue)?.Path
if (-not $PublishExe) {
    $PublishExe = Get-ChildItem -Path (Join-Path $repo 'windows/Ghostty/bin') -Recurse -Filter Wintty.exe |
        Where-Object { $_.FullName -match '\\publish\\' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $PublishExe -or -not (Test-Path $PublishExe)) {
    throw 'NativeAOT publish Wintty.exe not found; run release-smoke.ps1 first'
}

# Before the publish, which cannot overwrite the exe while it runs. Only that
# exe is refused here: whether each harness below may run beside any other
# Wintty is its own coexistence guard's call.
Assert-NoWinttyFrom -ExePath $PublishExe -Context 'The AOT fuzz'

# A NativeAOT publish has no Ghostty.Core.dll beside it, so the guard cannot
# read the AUMID it runs as and would refuse it beside any Wintty. The
# constant is the same one the sibling Release build carries (publish/ sits
# two levels under that build's output), so it is read there and handed to
# every harness. Then the verdict is taken once, before the publish, so a
# launch the guard will refuse costs seconds rather than a build.
$releaseSibling = Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PublishExe))) 'Wintty.exe'
$aotAumId = Get-WinttyBuildAumid $releaseSibling
if (-not $aotAumId) { Write-Host "AUMID: not readable beside $releaseSibling; the harnesses will refuse beside any other Wintty" }
$preflight = Test-WinttyCoexistencePreflight -ExePath $PublishExe -AumId "$aotAumId"
if (-not $preflight.Allowed) {
    throw ("The AOT fuzz will not launch beside the running Wintty:`n  - " + ($preflight.Reasons -join "`n  - ") +
        "`nNothing was built and nothing was launched.")
}

if (-not $SkipPublish) {
    Write-Host '== refresh NativeAOT publish =='
    & (Join-Path $PSScriptRoot 'release-smoke.ps1') -SkipLaunch | Write-Host
}

Write-Host "== AOT fuzz target: $PublishExe =="
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$base = Join-Path $PSScriptRoot "fuzz-out/aot-$stamp"
New-Item -ItemType Directory -Force -Path $base | Out-Null

$results = @()
# Twice with a pause between: a Wintty that is mid-startup can outlive the
# first sweep.
#
# -ExePath is not optional here even though the stamp alone would find the
# leaks. Without it the sweep matches on time only, and this runs for tens of
# minutes across three harnesses: any Wintty the developer opens from any
# worktree while it works gets tree-killed with its shell.
function Stop-Wintty {
    Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $script:PublishExe
    Start-Sleep -Milliseconds 1200
    Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $script:PublishExe
    Start-Sleep -Milliseconds 600
}

$script:PublishExe = $PublishExe
$script:WinttyStamp = Get-WinttyLaunchStamp
$idx = 0
foreach ($s in $Scripts) {
    $idx++
    if ($idx -gt 1) { Start-Sleep -Seconds 3 }
    $name = [IO.Path]::GetFileNameWithoutExtension($s)
    $out = Join-Path $base $name
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    Write-Host "`n========== $s (AOT) =========="
    # Retry only exit 1, "the harness could not run": nothing was learned,
    # and the causes are transient. This used to retry every non-zero exit
    # and keep the last attempt, so a product finding that passed on the
    # second run was reported as clean.
    $attempts = 2
    $code = 1
    for ($try = 1; $try -le $attempts; $try++) {
        if ($try -gt 1) {
            Write-Host "retry $try/$attempts for $name (could not run)"
            Stop-Wintty
            Start-Sleep -Seconds 2
        }
        $aumArgs = if ($aotAumId) { @('-AumId', $aotAumId) } else { @() }
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot $s) -ExePath $PublishExe -OutDir $out @aumArgs
        $code = $LASTEXITCODE
        if ($code -ne 1) { break }
    }
    Stop-Wintty
    $row = [ordered]@{ script = $name; exit = $code; aot = $true }
    $rj = Join-Path $out 'result.json'
    if (Test-Path $rj) { $row.result = Get-Content $rj -Raw | ConvertFrom-Json }
    $results += [pscustomobject]$row
    Write-Host "== $name exit=$code =="
}

$summary = Join-Path $base 'summary.json'
[ordered]@{
    publishExe = $PublishExe
    ok = -not ($results | Where-Object { $_.exit -ne 0 })
    results = $results
} | ConvertTo-Json -Depth 6 | Set-Content $summary
Write-Host "`nAOT SUMMARY -> $summary"
$results | Format-Table script, exit -AutoSize
if ($results | Where-Object { $_.exit -eq 2 }) { exit 2 }
if ($results | Where-Object { $_.exit -ne 0 }) { exit 1 }
exit 0
