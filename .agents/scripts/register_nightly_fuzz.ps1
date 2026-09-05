# Registers (or refreshes) the nightly quality-control scheduled task.
# Run once per machine, from the main checkout: paths are derived from this
# script's location. The task runs in the interactive user session (the
# fuzz leg needs a real desktop) daily at 23:00, wakes the machine from
# sleep or hibernation to run, and starts as soon as possible if the 23:00
# slot was missed. The run itself hibernates the machine afterwards when
# the saved config allows it (see nightly_fuzz.ps1 / nightly_control.ps1),
# which makes the box a self-contained nightly appliance.

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$target = Join-Path $scriptRoot 'nightly_fuzz.ps1'
if (-not (Test-Path $target)) { throw "nightly_fuzz.ps1 not found next to this script" }

$action = New-ScheduledTaskAction -Execute 'pwsh.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$target`" -Scheduled"
# The limit must clear the worst case: up to 3h of idle-wait, then up to 2h
# of incoda queue wait per wrapped test leg (two of them), then the full
# ladder, the fuzz suite's own idle-wait, and the hibernate wait. The two
# waits correlate: a box busy enough to keep the user active is a box with
# a deep build lane. Task Scheduler ends an overrun by killing the process
# tree, which is a nightly that dies filing nothing, so this budget is
# deliberately loose rather than tight.
$trigger = New-ScheduledTaskTrigger -Daily -At 23:00
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -WakeToRun `
    -ExecutionTimeLimit (New-TimeSpan -Hours 10) `
    -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName 'wintty-nightly-quality' `
    -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
Write-Host "Registered scheduled task 'wintty-nightly-quality' -> $target (daily 23:00, WakeToRun, StartWhenAvailable)"
