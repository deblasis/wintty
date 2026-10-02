#requires -Version 7
# Smoke-test ReleaseFast DLL + Release shell + optional NativeAOT publish.
param(
    [switch]$SkipAot,
    [switch]$SkipLaunch
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/test-config.ps1')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $repo
$releaseExe = Join-Path $repo 'windows/Ghostty/bin/x64/Release/net10.0-windows10.0.19041.0/Wintty.exe'
$pubExeDefault = Join-Path $repo 'windows/Ghostty/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/publish/Wintty.exe'

# Before the builds, not after: a ReleaseFast libghostty plus a Release shell
# is minutes of work to then be told to close a window, and dotnet cannot
# overwrite a locked Wintty.exe anyway. Only the two exes this run builds
# are locked by it; any other Wintty is the coexistence guard's call at
# launch time.
Assert-NoWinttyFrom -ExePath $releaseExe -Context 'The release smoke'
if (-not $SkipAot) { Assert-NoWinttyFrom -ExePath $pubExeDefault -Context 'The release smoke' }

# Single-instance stays at the shipped default (on) whenever it can, so the
# trimmed and AOT builds still run the election and the forwarding server.
# Only beside another Wintty is it turned off, because the guard refuses a
# launch that could forward to or take forwards from somebody else's
# instance. The mode is printed so a run says which path it covered.
$others = @(Get-WinttyOtherInstances -ExePath $releaseExe -Instances (Get-WinttyInstances))
$singleInstance = if ($others.Count -gt 0) { 'false' } else { 'true' }
Write-Host ("single-instance: {0} ({1})" -f $singleInstance,
    $(if ($others.Count -gt 0) { "Wintty pid(s) $(($others | ForEach-Object { $_.Id }) -join ', ') running beside the smoke" } else { 'no other Wintty running, the shipped default' }))

# Both launch smokes run against a per-run random temp config root with the
# WINTTY_TEST_CONFIG guard armed: a Release/AOT launch used to read and
# write the user's real config. Entered before the builds so the finally
# below always pairs it; nothing in the build reads XDG_CONFIG_HOME. The
# private state tree and the staged quick-terminal line are what let a
# launch run beside another Wintty.
$smokeConfig = Add-WinttyHarnessConfigDefaults "windows-single-instance = $singleInstance`nwindow-save-state = never`n"

# The AUMID the launches run as, for the guard: a NativeAOT publish has no
# Ghostty.Core.dll beside it to read one from, and the constant is the
# Release build's. Read off an earlier Release build when there is one, so
# the verdict can be taken now, before minutes of building; read again
# after the build below either way.
$script:SmokeAumId = if (Test-Path -LiteralPath $releaseExe) { Get-WinttyBuildAumid $releaseExe } else { $null }
if ($others.Count -gt 0) {
    if ($script:SmokeAumId) {
        $legs = @($releaseExe) + $(if (-not $SkipAot) { @($pubExeDefault) } else { @() })
        foreach ($leg in $legs) {
            $v = Test-WinttyCoexistencePreflight -ExePath $leg -AumId $script:SmokeAumId -ConfigText $smokeConfig
            if (-not $v.Allowed) {
                throw ("The release smoke will not launch $leg beside the running Wintty:`n  - " +
                    ($v.Reasons -join "`n  - ") + "`nNothing was built and nothing was launched.")
            }
        }
    }
    else {
        Write-Host 'coexistence: no earlier Release build to read the AUMID from, so the guard decides after the build'
    }
}

$testConfig = Enter-WinttyTestConfig -ConfigText $smokeConfig -PrivateStateBase

# One entry per launch, each with its own stamp. A single stamp taken at
# script start would be minutes stale by the time anything launches, and
# every Wintty the developer opened while the builds ran would look like
# this run's.
$script:Launched = @()

function Invoke-LaunchSmoke {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(Mandatory)][string]$Label,
        # The published exe must have loaded the Windows App SDK Insights
        # resource dll (#1086): Register() runs on every launch and loads it
        # by name, so its presence among the live process's modules is the
        # direct observation that toast registration got past the resource
        # load. Without the dll in the publish folder Register() throws
        # "Unable to load resource dll" and every toast is dead.
        [switch]$RequireInsightsDll
    )

    $since = Get-WinttyLaunchStamp
    $script:Launched += [pscustomobject]@{ Since = $since; Exe = $Exe }
    if ($RequireInsightsDll) {
        $dll = Join-Path (Split-Path $Exe) 'Microsoft.WindowsAppRuntime.Insights.Resource.dll'
        if (-not (Test-Path $dll)) {
            throw ("publish folder lacks Microsoft.WindowsAppRuntime.Insights.Resource.dll " +
                   "($dll): AppNotificationManager.Register() throws on every launch and " +
                   "every toast is dead (#1086). The Ghostty.csproj publish target that " +
                   "extracts it from the Windows App SDK runtime package did not run.")
        }
    }
    [void](Assert-WinttyCoexistence -ExePath $Exe -ConfigText $smokeConfig -AumId "$script:SmokeAumId" -Context "The $Label smoke")
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory (Split-Path $Exe)
    $insightsLoaded = $false
    $deadline = (Get-Date).AddSeconds(10)
    while (-not $proc.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $proc.Refresh()
        if (-not $RequireInsightsDll) { continue }
        foreach ($m in $proc.Modules) {
            if ($m.ModuleName -ieq 'Microsoft.WindowsAppRuntime.Insights.Resource.dll') {
                $insightsLoaded = $true
                break
            }
        }
        if ($insightsLoaded) { break }
    }
    if ($proc.HasExited) { throw "$Label Wintty exited early code=$($proc.ExitCode)" }
    if ($RequireInsightsDll -and -not $insightsLoaded) {
        try { $proc.Kill($true) } catch { }
        Stop-WinttyStartedAfter -Since $since -ExePath $Exe
        throw ("Microsoft.WindowsAppRuntime.Insights.Resource.dll was NOT loaded by the " +
               "published app: AppNotificationManager.Register() threw and every toast " +
               "from this build is dead (#1086). The dll file is present but WinAppRT " +
               "did not load it.")
    }
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

    if (-not (Test-Path $releaseExe)) { throw "missing $releaseExe" }
    $script:SmokeAumId = Get-WinttyBuildAumid $releaseExe
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
        $pubExe = $pubExeDefault
        if (-not (Test-Path $pubExe)) {
            # SDK may place publish under a slightly different RID folder.
            $pubExe = Get-ChildItem -Path (Join-Path $repo 'windows/Ghostty/bin') -Recurse -Filter Wintty.exe |
                Where-Object { $_.FullName -match '\\publish\\' } |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1 -ExpandProperty FullName
        }
        if (-not $pubExe -or -not (Test-Path $pubExe)) { throw 'NativeAOT publish exe not found' }
        Write-Host "aot publish ok: $pubExe"
        # #1086 gate, file half: the publish must carry the Insights
        # resource dll before anything launches.
        $insightsDll = Join-Path (Split-Path $pubExe) 'Microsoft.WindowsAppRuntime.Insights.Resource.dll'
        if (-not (Test-Path $insightsDll)) {
            throw ("publish folder lacks Microsoft.WindowsAppRuntime.Insights.Resource.dll " +
                   "($insightsDll): AppNotificationManager.Register() throws on every " +
                   "launch and every toast is dead (#1086). The " +
                   "Ghostty.csproj publish target that extracts it from the Windows App " +
                   "SDK runtime package did not run.")
        }
        Write-Host "insights resource dll ok: $insightsDll"
        if (-not $SkipLaunch) {
            Write-Host '== launch NativeAOT smoke (module check) =='
            Start-Sleep -Milliseconds 400
            Invoke-LaunchSmoke -Exe $pubExe -Label 'aot' -RequireInsightsDll
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
    Exit-WinttyTestConfig $testConfig
    Pop-Location
}
