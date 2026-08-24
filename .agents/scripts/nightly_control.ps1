# Control panel for the nightly quality run.
#
# A small WinForms window to drive the appliance flow on the build machine:
# launch the suite manually, watch the current status, toggle the
# hibernate-after and fuzz-leg options (saved to nightly-config.json, which
# scheduled runs honor), open the latest log, and register or unregister the
# 23:00 scheduled task.
#
# Run with:  pwsh -File .agents/scripts/nightly_control.ps1
# -SelfTest builds the form and checks the config roundtrip without showing
# any UI, so the script stays verifiable headlessly.

param([switch]$SelfTest)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot '..\..')).Path
$logDir = Join-Path $repoRoot '.agents\nightly-logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$configFile = Join-Path $logDir 'nightly-config.json'
$statusFile = Join-Path $logDir 'status.json'
$runner = Join-Path $scriptRoot 'nightly_fuzz.ps1'
$taskName = 'wintty-nightly-quality'

function Load-Config {
    $cfg = @{ hibernateAfter = $true; runFuzz = $true }
    if (Test-Path $configFile) {
        try {
            (Get-Content $configFile -Raw | ConvertFrom-Json).psobject.Properties |
                ForEach-Object { $cfg[$_.Name] = $_.Value }
        } catch {}
    }
    $cfg
}

function Save-Config($cfg) {
    $cfg | ConvertTo-Json | Set-Content $configFile
}

# The task queries are two COM round-trips, too heavy for every 2s tick on
# the UI thread; they refresh on their own slower cadence.
$script:taskLine = 'Task: checking...'
function Update-TaskLine {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($task) {
        $info = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction SilentlyContinue
        $script:taskLine = "Task: registered ($($task.State)); next run $($info.NextRunTime); last result $($info.LastTaskResult)"
    } else {
        $script:taskLine = 'Task: not registered on this machine'
    }
}

function Get-StatusText {
    $lines = @($script:taskLine)
    if (Test-Path $statusFile) {
        try {
            $s = Get-Content $statusFile -Raw | ConvertFrom-Json
            if ($s) {
                $lines += "Run: $($s.phase)  (started $($s.started), updated $($s.updated))"
                if ($s.sha) { $lines += "Commit: $($s.sha)" }
                if ($s.results) {
                    $s.results.psobject.Properties | ForEach-Object { $lines += "  $($_.Name): $($_.Value)" }
                }
            } else {
                $lines += 'Run: status file empty (run in progress?)'
            }
        } catch { $lines += 'Run: status file unreadable' }
    } else {
        $lines += 'Run: no runs recorded yet'
    }
    $lines -join [Environment]::NewLine
}

function Get-LatestLog {
    Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

$config = Load-Config

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Nightly quality run'
$form.Size = New-Object System.Drawing.Size(520, 420)
$form.MinimumSize = $form.Size
$form.StartPosition = 'CenterScreen'

$statusBox = New-Object System.Windows.Forms.TextBox
$statusBox.Multiline = $true
$statusBox.ReadOnly = $true
$statusBox.ScrollBars = 'Vertical'
$statusBox.Font = New-Object System.Drawing.Font('Consolas', 9)
$statusBox.Location = New-Object System.Drawing.Point(12, 12)
$statusBox.Size = New-Object System.Drawing.Size(480, 210)
$statusBox.Anchor = 'Top,Left,Right,Bottom'
$form.Controls.Add($statusBox)

$hibernateCheck = New-Object System.Windows.Forms.CheckBox
$hibernateCheck.Text = 'Hibernate when a scheduled run finishes'
$hibernateCheck.Checked = [bool]$config.hibernateAfter
$hibernateCheck.Location = New-Object System.Drawing.Point(12, 232)
$hibernateCheck.Size = New-Object System.Drawing.Size(480, 22)
$hibernateCheck.Anchor = 'Left,Bottom'
$form.Controls.Add($hibernateCheck)

$fuzzCheck = New-Object System.Windows.Forms.CheckBox
$fuzzCheck.Text = 'Run the GUI fuzz leg (needs an unlocked, idle desktop)'
$fuzzCheck.Checked = [bool]$config.runFuzz
$fuzzCheck.Location = New-Object System.Drawing.Point(12, 256)
$fuzzCheck.Size = New-Object System.Drawing.Size(480, 22)
$fuzzCheck.Anchor = 'Left,Bottom'
$form.Controls.Add($fuzzCheck)

$onToggle = {
    Save-Config @{ hibernateAfter = $hibernateCheck.Checked; runFuzz = $fuzzCheck.Checked }
}
$hibernateCheck.Add_CheckedChanged($onToggle)
$fuzzCheck.Add_CheckedChanged($onToggle)

function New-Button([string]$text, [int]$x, [int]$y, [int]$w) {
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $text
    $b.Location = New-Object System.Drawing.Point($x, $y)
    $b.Size = New-Object System.Drawing.Size($w, 30)
    $b.Anchor = 'Left,Bottom'
    $form.Controls.Add($b)
    $b
}

$runButton = New-Button 'Run now' 12 290 110
$logButton = New-Button 'Open latest log' 130 290 130
$registerButton = New-Button 'Register 23:00 task' 268 290 150
$unregisterButton = New-Button 'Unregister task' 268 326 150

$runButton.Add_Click({
    # A manual run starts immediately: -Scheduled would drag in the 3-minute
    # idle gate the user just clicked through, so the checkboxes are handed
    # over as explicit switches instead. The runner's own single-instance
    # check keeps this from stomping a scheduled run already in flight.
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $runner)
    if ($hibernateCheck.Checked) { $argList += '-HibernateAfter' }
    if (-not $fuzzCheck.Checked) { $argList += '-NoFuzz' }
    Start-Process pwsh -ArgumentList $argList
})
$logButton.Add_Click({
    $latest = Get-LatestLog
    if ($latest) { Invoke-Item $latest.FullName }
    else { [System.Windows.Forms.MessageBox]::Show('No logs yet.') | Out-Null }
})
$registerButton.Add_Click({
    & (Join-Path $scriptRoot 'register_nightly_fuzz.ps1')
})
$unregisterButton.Add_Click({
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
})

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 2000
$script:tick = 0
$timer.Add_Tick({
    $script:tick++
    if ($script:tick % 5 -eq 0) { Update-TaskLine }
    $statusBox.Text = Get-StatusText
})
$timer.Start()
Update-TaskLine
$statusBox.Text = Get-StatusText

if ($SelfTest) {
    $timer.Stop()
    $failed = $false
    foreach ($c in @($statusBox, $hibernateCheck, $fuzzCheck, $runButton, $logButton, $registerButton, $unregisterButton)) {
        if (-not $form.Controls.Contains($c)) { Write-Host "SELF-TEST FAILED: missing control $($c.Text)"; $failed = $true }
    }
    # Restore what was actually on disk: Load-Config always returns defaults,
    # so its result cannot distinguish "no file" from "file of defaults".
    $hadFile = Test-Path $configFile
    $before = Load-Config
    Save-Config @{ hibernateAfter = $false; runFuzz = $true }
    $cfg = Load-Config
    if ($cfg.hibernateAfter) { Write-Host 'SELF-TEST FAILED: config roundtrip'; $failed = $true }
    if ($hadFile) { Save-Config $before } else { Remove-Item $configFile -ErrorAction SilentlyContinue }
    if (-not (Get-StatusText)) { Write-Host 'SELF-TEST FAILED: empty status text'; $failed = $true }
    $form.Dispose()
    if ($failed) { Write-Host 'SELF-TEST FAILED'; exit 1 }
    Write-Host 'SELF-TEST PASSED'
    exit 0
}

[void]$form.ShowDialog()
$timer.Stop()
