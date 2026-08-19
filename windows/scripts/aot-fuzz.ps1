#requires -Version 7
# Run mouse/UIA fuzz suite against NativeAOT-published Wintty.exe.
param(
    [string]$PublishExe = (Join-Path $PSScriptRoot '../Ghostty/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/publish/Wintty.exe'),
    [switch]$SkipPublish,
    [string[]]$Scripts = @(
        'mouse-fuzz-inspector.ps1',
        'mouse-fuzz-dialogs.ps1',
        'mouse-fuzz-vertical-tabs.ps1',
        'mouse-fuzz-ime-cjk.ps1',
        'mouse-fuzz-loop.ps1'
    )
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
$ErrorActionPreference = 'Stop'
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
function Stop-Wintty {
    Stop-WinttyStartedAfter -Since $script:WinttyStamp
    Start-Sleep -Milliseconds 1200
    Stop-WinttyStartedAfter -Since $script:WinttyStamp
    Start-Sleep -Milliseconds 600
}

Assert-NoWintty
$script:WinttyStamp = Get-WinttyLaunchStamp
Stop-Wintty
$idx = 0
foreach ($s in $Scripts) {
    $idx++
    if ($idx -gt 1) { Start-Sleep -Seconds 3 }
    $name = [IO.Path]::GetFileNameWithoutExtension($s)
    $out = Join-Path $base $name
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    Write-Host "`n========== $s (AOT) =========="
    $attempts = 2
    $code = 1
    for ($try = 1; $try -le $attempts; $try++) {
        if ($try -gt 1) {
            Write-Host "retry $try/$attempts for $name"
            Stop-Wintty
            Start-Sleep -Seconds 2
        }
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot $s) -ExePath $PublishExe -OutDir $out
        $code = $LASTEXITCODE
        if ($code -eq 0) { break }
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
if ($results | Where-Object { $_.exit -ne 0 }) { exit 1 }
exit 0
