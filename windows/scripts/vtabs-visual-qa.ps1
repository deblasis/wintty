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

function Step([string]$name, [scriptblock]$body) {
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    $dir = Join-Path $OutRoot $name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    try {
        & $body $dir
        $results.steps += [ordered]@{ name = $name; ok = $true }
        Write-Host "OK $name" -ForegroundColor Green
    } catch {
        $results.steps += [ordered]@{ name = $name; ok = $false; error = $_.Exception.Message }
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
}

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
$fail = @($results.steps | Where-Object { -not $_.ok })
if ($fail.Count -gt 0) { exit 2 }
exit 0
