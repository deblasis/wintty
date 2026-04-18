# Verifies the SPONSOR_BUILD compile gate in windows/Ghostty/Ghostty.csproj.
#
# Runs two builds back-to-back against the same output path (default, then
# sponsor) and reads BuildFlags.IsSponsorBuild from each resulting assembly.
# The second build overwrites the first; we read the DLL immediately after
# each build, so no separate output directory is needed.
#
# Using the same Configuration=Debug avoids triggering any other
# Configuration-conditional properties in the csproj (e.g. Channel defaults).
#
# Run from the repo root:
#   pwsh scripts/verify-sponsor-gate.ps1
#
# Exit code: 0 if both checks pass; non-zero otherwise.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

function Get-IsSponsorBuild([string]$assemblyPath) {
    if (-not (Test-Path $assemblyPath)) {
        throw "Assembly not found: $assemblyPath"
    }
    # Load into a throwaway context. Reflection sees internal members fine.
    $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $assemblyPath))
    $asm = [System.Reflection.Assembly]::Load($bytes)
    $type = $asm.GetType("Ghostty.BuildFlags", $true)
    $field = $type.GetField("IsSponsorBuild",
        [System.Reflection.BindingFlags]::Public -bor
        [System.Reflection.BindingFlags]::NonPublic -bor
        [System.Reflection.BindingFlags]::Static)
    return [bool]$field.GetValue($null)
}

function Assert-Equal($expected, $actual, $label) {
    if ($expected -ne $actual) {
        Write-Error "$label : expected=$expected actual=$actual"
        exit 1
    }
    Write-Host "OK  $label : $actual" -ForegroundColor Green
}

# Single output path - the existing Justfile convention.
$dllPath = "windows/Ghostty/bin/x64/Debug/net9.0-windows10.0.19041.0/Ghostty.dll"

Write-Host "=== Default build (SponsorBuild unset) ===" -ForegroundColor Cyan
dotnet build windows/Ghostty/Ghostty.sln /p:Platform=x64 /p:Configuration=Debug
if ($LASTEXITCODE -ne 0) { throw "default build failed" }
$defaultVal = Get-IsSponsorBuild $dllPath
Assert-Equal $false $defaultVal "default build BuildFlags.IsSponsorBuild"

Write-Host ""
Write-Host "=== Sponsor build (SponsorBuild=true) ===" -ForegroundColor Cyan
# Same Configuration=Debug, only SponsorBuild differs. Overwrites the default output.
dotnet build windows/Ghostty/Ghostty.sln /p:Platform=x64 /p:Configuration=Debug /p:SponsorBuild=true
if ($LASTEXITCODE -ne 0) { throw "sponsor build failed" }
$sponsorVal = Get-IsSponsorBuild $dllPath
Assert-Equal $true $sponsorVal "sponsor build BuildFlags.IsSponsorBuild"

Write-Host ""
Write-Host "Both builds verified. The SPONSOR_BUILD gate is wired correctly." -ForegroundColor Green
Pop-Location
