#requires -Version 7
# Smoke-test ReleaseFast DLL + Release shell + optional NativeAOT publish.
param(
    [switch]$SkipAot,
    [switch]$SkipLaunch
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $repo
try {
    Write-Host '== build-dll-release (ReleaseFast libghostty) =='
    just build-dll-release
    if ($LASTEXITCODE -ne 0) { throw "build-dll-release failed exit=$LASTEXITCODE" }

    Write-Host '== build-win-release =='
    just build-win-release
    if ($LASTEXITCODE -ne 0) { throw "build-win-release failed exit=$LASTEXITCODE" }

    $releaseExe = Join-Path $repo 'windows/Ghostty/bin/x64/Release/net10.0-windows10.0.19041.0/Wintty.exe'
    if (-not (Test-Path $releaseExe)) { throw "missing $releaseExe" }
    Write-Host "release exe ok: $releaseExe"

    if (-not $SkipLaunch) {
        Write-Host '== launch Release smoke (3s) =='
        Get-Process Wintty -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 400
        $proc = Start-Process -FilePath $releaseExe -PassThru -WorkingDirectory (Split-Path $releaseExe)
        Start-Sleep -Seconds 3
        $proc.Refresh()
        if ($proc.HasExited) { throw "Release Wintty exited early code=$($proc.ExitCode)" }
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Write-Host 'release launch ok'
    }

    if (-not $SkipAot) {
        Write-Host '== dotnet publish NativeAOT =='
        dotnet publish windows/Ghostty/Ghostty.csproj `
            -c Release -r win-x64 /p:Platform=x64 `
            --no-restore 2>&1 | Write-Host
        if ($LASTEXITCODE -ne 0) { throw "NativeAOT publish failed exit=$LASTEXITCODE" }
        $pubExe = Join-Path $repo 'windows/Ghostty/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/publish/Wintty.exe'
        if (-not (Test-Path $pubExe)) {
            # SDK may place publish under a slightly different RID folder.
            $pubExe = Get-ChildItem -Path (Join-Path $repo 'windows/Ghostty/bin') -Recurse -Filter Wintty.exe |
                Where-Object { $_.FullName -match '\\publish\\' } |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1 -ExpandProperty FullName
        }
        if (-not $pubExe -or -not (Test-Path $pubExe)) { throw 'NativeAOT publish exe not found' }
        Write-Host "aot publish ok: $pubExe"
        if (-not $SkipLaunch) {
            Write-Host '== launch NativeAOT smoke (3s) =='
            Get-Process Wintty -ErrorAction SilentlyContinue | Stop-Process -Force
            Start-Sleep -Milliseconds 400
            $proc = Start-Process -FilePath $pubExe -PassThru -WorkingDirectory (Split-Path $pubExe)
            Start-Sleep -Seconds 3
            $proc.Refresh()
            if ($proc.HasExited) { throw "NativeAOT Wintty exited early code=$($proc.ExitCode)" }
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            Write-Host 'aot launch ok'
        }
    }

    @{ releaseExe = $releaseExe; ok = $true } | ConvertTo-Json | Write-Output
}
finally {
    Pop-Location
}
