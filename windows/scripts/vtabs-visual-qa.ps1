#requires -Version 7
<#
.SYNOPSIS
    Build smoke + fuzz pass for vertical/horizontal tab chrome.
#>
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\Ghostty\bin\x64\Debug\net10.0-windows10.0.19041.0\Wintty.exe'),
    [string]$OutRoot = (Join-Path $PSScriptRoot ("fuzz-out/qa-" + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null

if (-not (Test-Path $ExePath)) { throw "missing exe: $ExePath" }

$results = [ordered]@{ outRoot = $OutRoot; steps = @() }

# A step used to be judged on whether its body threw. A sub-script that
# exits 2 does not throw, so a run that found real defects printed all
# green - the exact failure this file was being trusted not to have. The
# exit code is the verdict; the catch is only for the harness never
# starting.
# NoVerdict is for the capture scripts, which produce frames for a human and
# never call exit; whatever native command ran last inside them is what
# $LASTEXITCODE would be read from, so reading it would invent a verdict.
function Step([string]$name, [scriptblock]$body, [switch]$NoVerdict) {
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    $dir = Join-Path $OutRoot $name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    try {
        $global:LASTEXITCODE = 0
        & $body $dir
        if ($NoVerdict) {
            $results.steps += [ordered]@{ name = $name; ok = $true; exit = $null; verdict = 'captured' }
            Write-Host "OK $name (captured, no verdict)" -ForegroundColor Green
            Start-Sleep -Milliseconds 400
            return
        }
        $code = $LASTEXITCODE
        $verdict = switch ($code) { 0 { 'pass' } 2 { 'findings' } 1 { 'harness' } default { 'error' } }
        $results.steps += [ordered]@{ name = $name; ok = ($code -eq 0); exit = $code; verdict = $verdict }
        if ($code -eq 0) {
            Write-Host "OK $name" -ForegroundColor Green
        } else {
            Write-Host "$verdict $name (exit $code)" -ForegroundColor Red
        }
    } catch {
        $results.steps += [ordered]@{ name = $name; ok = $false; exit = $null; verdict = 'harness'; error = $_.Exception.Message }
        Write-Host "FAIL $name : $($_.Exception.Message)" -ForegroundColor Red
    }
    # Each sub-script owns and tears down the process it started. A blanket
    # `Get-Process Wintty | Stop-Process` here would also kill the
    # developer's real session on the same desktop.
    Start-Sleep -Milliseconds 400
}

Step 'layout-switch-capture' {
    param($dir)
    & (Join-Path $PSScriptRoot 'vtabs-layout-switch-capture.ps1') -ExePath $ExePath -OutDir $dir
} -NoVerdict

Step 'mouse-fuzz-tab-colors' {
    param($dir)
    & (Join-Path $PSScriptRoot 'mouse-fuzz-tab-colors.ps1') -ExePath $ExePath -OutDir $dir
}

Step 'mouse-fuzz-vertical-tabs' {
    param($dir)
    & (Join-Path $PSScriptRoot 'mouse-fuzz-vertical-tabs.ps1') -ExePath $ExePath -OutDir $dir
}

Step 'mouse-fuzz-loop' {
    param($dir)
    & (Join-Path $PSScriptRoot 'mouse-fuzz-loop.ps1') -ExePath $ExePath -OutDir $dir
}

$summaryPath = Join-Path $OutRoot 'summary.json'
$results | ConvertTo-Json -Depth 5 | Set-Content $summaryPath
Write-Host "`nOUT=$OutRoot"
# Same numbering as the harnesses themselves: findings outrank a harness
# that could not run, and a step that could not run is not a pass.
$findings = @($results.steps | Where-Object { $_.verdict -eq 'findings' })
$broken = @($results.steps | Where-Object { -not $_.ok -and $_.verdict -ne 'findings' })
if ($findings.Count -gt 0) { exit 2 }
if ($broken.Count -gt 0) { exit 1 }
exit 0
