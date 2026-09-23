#requires -Version 7
<#
    Jump-list CLI under windows-single-instance: the arguments a jump-list
    entry launches Wintty with must reach the running primary and open what
    they name.

    What is tested is the command line, not the shell's jump list: each
    secondary is started with the same --jumplist-* arguments an entry
    carries, and must hand them to the primary and exit. Explorer and the
    taskbar are never touched.

    Seam-launched: the primary is a seam session, tab counts are the
    manager's, and the harness synthesizes no OS input. The
    confirm-close-surface=false check opens the pane menu and the tab menu
    through the seam's menu op (the keyboard's context request) instead of
    right-clicks, and invokes Split Right and Close by UIA as before.

    Checks, all gated:
      default profile   the primary's title names the default profile
      new-tab           --jumplist-action=new-tab adds a tab, no window
      confirm-skipped   closing a split tab raises no dialog and drops it
      new-window        --jumplist-action=new-window adds a window
      profile           --jumplist-profile=pwsh adds a window
    and every secondary must exit rather than stay up as its own app.

    Exits 0 clean, 2 findings, 1 could-not-run.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots') | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$Config = @'
windows-single-instance = true
window-save-state = never
windows-settings-ui = true
confirm-close-surface = false
vertical-tabs = false
profile.pwsh.name = PowerShell
profile.pwsh.command = pwsh.exe
default-profile = pwsh
'@

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$session = $null
$harnessError = ''
$script:Findings = [System.Collections.Generic.List[string]]::new()
$secondaryAlive = $false
$newWindowOk = $false
$newTabOk = $false
$profileOk = $false
$defaultProfileOk = $false
$confirmProbed = $false
$confirmSkipped = $false
$windowsBefore = 0
$windowsAfterTab = 0
$windowsAfterNew = 0
$windowsAfterProfile = 0
$tabsBefore = 0
$tabsAfter = 0
$tabsAfterClose = 0
$tabsAfterProfile = 0

function Shot([int64]$Hwnd64, [string]$Name) {
    $rc = [SeamWin]::RectOf($Hwnd64)
    if ($null -eq $rc) { return }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir "shots\$Name.png"))
    $g.Dispose(); $bmp.Dispose()
}

function Shot-Pid([uint32]$ProcId, [string]$Prefix) {
    $i = 0
    foreach ($w in @(Get-SeamWinUiWindows $ProcId)) {
        $safe = ($w.Title -replace '[^A-Za-z0-9]+', '-').Trim('-')
        if (-not $safe) { $safe = 'untitled' }
        Shot $w.Hwnd64 ("{0}-{1}-{2}" -f $Prefix, $i, $safe)
        $i++
    }
}

# Tabs across every window, for the profile step's report only: the seam
# serves the first window, and the entry under test opens another.
function Count-TabItems([uint32]$ProcId) {
    $n = 0
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    foreach ($w in @(Get-SeamWinUiWindows $ProcId)) {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([SeamWin]::P($w.Hwnd64))
        if ($null -ne $root) { $n += @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)).Count }
    }
    return $n
}

function Find-MenuItem([int64]$Hwnd64, [string]$Name) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::MenuItem)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
    $deadline = (Get-Date).AddSeconds(3)
    do {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([SeamWin]::P($Hwnd64))
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 150
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Invoke-MenuItem([int64]$Hwnd64, [string]$Name) {
    $el = Find-MenuItem $Hwnd64 $Name
    if ($null -eq $el) { throw "HARVEST_MISS: no '$Name' menu item under the window" }
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Host "invoke $Name"
    Start-Sleep -Milliseconds 500
}

function Invoke-Secondary([string]$Cli) {
    # Do not name this $Args: that is the automatic leftover-args
    # variable and silently swallows the parameter (every secondary
    # then launches with no jumplist flags and becomes NewWindow).
    Write-Host "secondary $Cli"
    # The secondary reads config before it forwards, so it must see the
    # session's temp root: refuse if the session is not up, and re-arm the
    # guard for the child explicitly rather than trust the inherited value.
    if ($null -eq $session -or $env:XDG_CONFIG_HOME -ne $session.TempXdg) {
        throw 'HARNESS: a secondary must launch inside the seam session''s config root'
    }
    $env:WINTTY_TEST_CONFIG = '1'
    $p = Start-Process -FilePath $session.ExePath -ArgumentList $Cli -PassThru `
        -WorkingDirectory (Split-Path $session.ExePath)
    $dl = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $dl) {
        $p.Refresh()
        if ($p.HasExited) {
            Write-Host "secondary exited code=$($p.ExitCode) pid=$($p.Id)"
            return $p
        }
        Start-Sleep -Milliseconds 200
    }
    Write-Host "secondary still alive pid=$($p.Id)"
    return $p
}

function Wait-WindowCount([uint32]$ProcId, [int]$AtLeast) {
    $deadline = (Get-Date).AddSeconds(8)
    do {
        $n = @(Get-SeamWinUiWindows $ProcId).Count
        if ($n -ge $AtLeast) { return $n }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    return $n
}

try {
    Assert-NoWintty -Context 'The jump-list harness'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config
    $pid32 = [uint32]$session.Proc.Id
    $hwnd64 = [int64]$session.Hwnd64
    $title = [SeamWin]::TitleOf([SeamWin]::P($hwnd64))
    Write-Host "hwnd=$hwnd64 pid=$pid32 title=$title"
    Shot $hwnd64 '00-launch'
    $defaultProfileOk = $title -match 'pwsh'

    $windowsBefore = @(Get-SeamWinUiWindows $pid32).Count
    $tabsBefore = @((Invoke-SeamCommand $session @{ op = 'get-state' }).state.tabs).Count

    $sec2 = Invoke-Secondary '--jumplist-action=new-tab'
    if (-not $sec2.HasExited) { $secondaryAlive = $true }
    $deadline = (Get-Date).AddSeconds(8)
    do {
        Start-Sleep -Milliseconds 250
        $tabsAfter = @((Invoke-SeamCommand $session @{ op = 'get-state' }).state.tabs).Count
    } while ($tabsAfter -le $tabsBefore -and (Get-Date) -lt $deadline)
    $windowsAfterTab = @(Get-SeamWinUiWindows $pid32).Count
    $newTabOk = ($tabsAfter -gt $tabsBefore) -and ($windowsAfterTab -eq $windowsBefore)
    Write-Host "after new-tab tabs=$tabsAfter (was $tabsBefore) windows=$windowsAfterTab newTabOk=$newTabOk"
    Shot $hwnd64 '01-new-tab'

    # confirm-close-surface=false: a split tab closes without a dialog.
    $menu = Invoke-SeamCommand $session @{ op = 'menu'; target = 'pane' }
    if (@($menu.menus).Count -eq 0) { throw 'PRODUCT_FAIL: the pane context menu did not open' }
    Invoke-MenuItem $hwnd64 'Split Right'
    $state = Invoke-SeamCommand $session @{ op = 'get-state' }
    $active = [int]$state.state.active
    $leaves = [int]$state.state.tabs[$active].leaves
    if ($leaves -lt 2) { throw "PRODUCT_FAIL: Split Right left the active tab with $leaves leaf" }
    [void](Invoke-SeamCommand $session @{ op = 'menu'; target = 'tab'; index = $active })
    $confirmProbed = $true
    Invoke-MenuItem $hwnd64 'Close'
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([SeamWin]::P($hwnd64))
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Close tab?')
    $dlg = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $tabsAfterClose = @((Invoke-SeamCommand $session @{ op = 'get-state' }).state.tabs).Count
    $confirmSkipped = ($null -eq $dlg) -and ($tabsAfterClose -lt $tabsAfter)
    Write-Host "confirm-close-surface=false dialog=$($null -ne $dlg) tabs=$tabsAfterClose confirmSkipped=$confirmSkipped"
    Shot $hwnd64 '01b-close-split-tab'

    $sec = Invoke-Secondary '--jumplist-action=new-window'
    if (-not $sec.HasExited) { $secondaryAlive = $true }
    $windowsAfterNew = Wait-WindowCount $pid32 ($windowsBefore + 1)
    $newWindowOk = (-not $secondaryAlive) -and ($windowsAfterNew -gt $windowsBefore)
    Write-Host "after new-window windows=$windowsAfterNew newWindowOk=$newWindowOk"
    Shot-Pid $pid32 '02-new-window'

    $sec3 = Invoke-Secondary '--jumplist-profile=pwsh'
    if (-not $sec3.HasExited) { $secondaryAlive = $true }
    $windowsAfterProfile = Wait-WindowCount $pid32 ($windowsAfterNew + 1)
    $tabsAfterProfile = Count-TabItems $pid32
    $profileOk = $windowsAfterProfile -gt $windowsAfterNew
    Write-Host "after profile windows=$windowsAfterProfile tabs=$tabsAfterProfile profileOk=$profileOk"
    Shot-Pid $pid32 '03-profile'

    if ($session.Proc.HasExited) {
        throw "APP_EXIT: the app exited during the run (code $($session.Proc.ExitCode))"
    }
}
catch {
    $msg = "$($_.Exception.Message)"
    if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { $script:Findings.Add($msg) }
    else { $harnessError = $msg }
    Write-Host "ERROR: $msg" -ForegroundColor Red
}
finally {
    # The sweep inside takes every secondary this run started, too.
    if ($null -ne $session) { Stop-SeamSession $session }
}

$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
if (-not $harnessError) {
    if ($crashGrew) { $script:Findings.Add('crash.log grew during the run') }
    if ($secondaryAlive) { $script:Findings.Add('a secondary stayed up instead of handing its arguments to the primary') }
    if (-not $defaultProfileOk) { $script:Findings.Add('the primary title does not name the default profile') }
    if (-not $newTabOk) { $script:Findings.Add('--jumplist-action=new-tab did not add a tab to the primary') }
    if ($confirmProbed -and -not $confirmSkipped) { $script:Findings.Add('confirm-close-surface=false still asked, or kept the split tab') }
    if (-not $newWindowOk) { $script:Findings.Add('--jumplist-action=new-window did not add a window') }
    if (-not $profileOk) { $script:Findings.Add('--jumplist-profile=pwsh did not add a window') }
}

[ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=<session token>); secondaries by command line, menus by the seam, items by UIA'
    crashGrew = $crashGrew
    secondaryAlive = $secondaryAlive
    windowsBefore = $windowsBefore
    windowsAfterTab = $windowsAfterTab
    windowsAfterNew = $windowsAfterNew
    windowsAfterProfile = $windowsAfterProfile
    newWindowOk = $newWindowOk
    tabsBefore = $tabsBefore
    tabsAfter = $tabsAfter
    newTabOk = $newTabOk
    defaultProfileOk = $defaultProfileOk
    confirmProbed = $confirmProbed
    confirmSkipped = $confirmSkipped
    tabsAfterClose = $tabsAfterClose
    tabsAfterProfile = $tabsAfterProfile
    profileOk = $profileOk
    findings = $script:Findings
    harness = $harnessError
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)

if ($script:Findings.Count -gt 0) { exit 2 }
if ($harnessError) { exit 1 }
exit 0
