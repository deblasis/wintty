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
    # Read the constant from PE metadata instead of Assembly.Load. The app
    # targets net10.0-windows10.0.19041.0 and references WindowsAppSDK, so a
    # runtime load would force the pwsh 7 host (which ships on .NET 8 or 9)
    # to resolve references it has no TPA entries for. MetadataReader walks
    # the immutable metadata tables without ever binding the assembly, so it
    # works regardless of host runtime version.
    Add-Type -AssemblyName "System.Reflection.Metadata" | Out-Null
    $resolved = (Resolve-Path -LiteralPath $assemblyPath).Path
    $stream = [System.IO.File]::OpenRead($resolved)
    try {
        $peReader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            # GetMetadataReader is an extension method on PEReader; call it
            # via the static form because PowerShell does not resolve C#
            # extension methods as instance methods.
            $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
            foreach ($fieldHandle in $reader.FieldDefinitions) {
                $field = $reader.GetFieldDefinition($fieldHandle)
                $name = $reader.GetString($field.Name)
                if ($name -ne "IsSponsorBuild") { continue }
                $declaring = $reader.GetTypeDefinition($field.GetDeclaringType())
                $ns = $reader.GetString($declaring.Namespace)
                $type = $reader.GetString($declaring.Name)
                if ($ns -ne "Ghostty" -or $type -ne "BuildFlags") { continue }
                $constantHandle = $field.GetDefaultValue()
                if ($constantHandle.IsNil) {
                    throw "Ghostty.BuildFlags.IsSponsorBuild has no constant value (not a compile-time literal)"
                }
                $constant = $reader.GetConstant($constantHandle)
                $blob = $reader.GetBlobReader($constant.Value)
                return [bool]$blob.ReadBoolean()
            }
            throw "Field Ghostty.BuildFlags.IsSponsorBuild not found in $resolved"
        } finally {
            $peReader.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

function Assert-Equal($expected, $actual, $label) {
    if ($expected -ne $actual) {
        Write-Error "$label : expected=$expected actual=$actual"
        exit 1
    }
    Write-Host "OK  $label : $actual" -ForegroundColor Green
}

# Single output path - the existing Justfile convention.
$dllPath = "windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.dll"

Write-Host "=== Default build (SponsorBuild unset) ===" -ForegroundColor Cyan
dotnet build windows/Ghostty.sln /p:Platform=x64 /p:Configuration=Debug
if ($LASTEXITCODE -ne 0) { throw "default build failed" }
$defaultVal = Get-IsSponsorBuild $dllPath
Assert-Equal $false $defaultVal "default build BuildFlags.IsSponsorBuild"

Write-Host ""
Write-Host "=== Sponsor build (SponsorBuild=true) ===" -ForegroundColor Cyan
# Same Configuration=Debug, only SponsorBuild differs. Overwrites the default output.
dotnet build windows/Ghostty.sln /p:Platform=x64 /p:Configuration=Debug /p:SponsorBuild=true
if ($LASTEXITCODE -ne 0) { throw "sponsor build failed" }
$sponsorVal = Get-IsSponsorBuild $dllPath
Assert-Equal $true $sponsorVal "sponsor build BuildFlags.IsSponsorBuild"

Write-Host ""
Write-Host "Both builds verified. The SPONSOR_BUILD gate is wired correctly." -ForegroundColor Green
Pop-Location
