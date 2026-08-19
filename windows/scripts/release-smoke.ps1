#requires -Version 7
# Smoke-test ReleaseFast DLL + Release shell + optional NativeAOT publish.
param(
    [switch]$SkipAot,
    [switch]$SkipLaunch
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $repo
# Before the builds, not after: a ReleaseFast libghostty plus a Release shell
# is minutes of work to then be told to close a window, and dotnet cannot
# overwrite a locked Wintty.exe anyway.
Assert-NoWintty -Context 'The release smoke'

# One entry per launch, each with its own stamp. A single stamp taken at
# script start would be minutes stale by the time anything launches, and
# every Wintty the developer opened while the builds ran would look like
# this run's.
$script:Launched = @()

function Invoke-LaunchSmoke {
    param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Label)

    $since = Get-WinttyLaunchStamp
    $script:Launched += [pscustomobject]@{ Since = $since; Exe = $Exe }
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory (Split-Path $Exe)
    Start-Sleep -Seconds 3
    $proc.Refresh()
    if ($proc.HasExited) { throw "$Label Wintty exited early code=$($proc.ExitCode)" }
    # Kill the tree: the shell runs as a child and a wedged one outlives a
    # Stop-Process on the parent alone.
    try { $proc.Kill($true); [void]$proc.WaitForExit(3000) } catch { }
    Stop-WinttyStartedAfter -Since $since -ExePath $Exe
    Write-Host "$Label launch ok"
}

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
        Start-Sleep -Milliseconds 400
        Invoke-LaunchSmoke -Exe $releaseExe -Label 'release'
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
            Start-Sleep -Milliseconds 400
            Invoke-LaunchSmoke -Exe $pubExe -Label 'aot'
        }
    }

    @{ releaseExe = $releaseExe; ok = $true } | ConvertTo-Json | Write-Output
}
finally {
    # A throw between Start-Process and the kill above leaves a smoke window
    # on the desktop for the next harness to refuse over. Sweeping per
    # recorded launch is a no-op when the happy path already reaped it.
    foreach ($l in $script:Launched) {
        Stop-WinttyStartedAfter -Since $l.Since -ExePath $l.Exe
    }
    Pop-Location
}
