# Registers (or refreshes) the nightly quality-control scheduled task.
# Run once per machine, from any checkout: paths are derived from this
# script's location. The task runs in the interactive user session (the
# fuzz leg needs a real desktop) daily at 03:30, or as soon after as the
# machine is available.

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$target = Join-Path $scriptRoot 'nightly_fuzz.ps1'
if (-not (Test-Path $target)) { throw "nightly_fuzz.ps1 not found next to this script" }

$action = New-ScheduledTaskAction -Execute 'pwsh.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$target`""
$trigger = New-ScheduledTaskTrigger -Daily -At 03:30
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Hours 4) `
    -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName 'wintty-nightly-quality' `
    -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
Write-Host "Registered scheduled task 'wintty-nightly-quality' -> $target (daily 03:30, StartWhenAvailable)"
