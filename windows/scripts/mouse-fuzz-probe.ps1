#requires -Version 7
<#
    Liveness probe: walk the everyday chrome gestures once and check the app
    survived them and crash.log did not grow.

    Seam-actuated: the harness synthesizes no OS input and never takes the
    foreground, so it can run beside someone using the machine. Each gesture
    the old click stream made goes through the seam op that drives the same
    handler:

      plus (x2)         the New tab button, UIA Invoke
      sidebar chevron   toggle-sidebar                     (vertical only)
      tab click         select
      grid right-click  menu{pane} (the right-click's press and release
                        halves), then menu{dismiss}
      tab right-click   menu{tab}, then menu{dismiss}      (horizontal only)
      wheel             scroll
      resize            MoveWindow on the app's own window (not input)

    Two steps are gone rather than moved. The app-icon click opened the
    Win32 system menu, which only real input reaches. The typed text was
    posted WM_CHAR, which never reaches Wintty (windows/scripts/README.md,
    "Driving input"), so it exercised nothing.

    New tab is also gated now: two presses must leave two more tabs.

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
window-save-state = never
'@

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$script:Findings = [System.Collections.Generic.List[string]]::new()
$script:Steps = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null

function Shot($Session, [string]$Name) {
    $rc = [SeamWin]::RectOf($Session.Hwnd64)
    if ($null -eq $rc) { return }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir "shots\$Name.png"))
    $g.Dispose(); $bmp.Dispose()
}

function Step($Session, [string]$Name, [hashtable]$Command) {
    $r = Invoke-SeamCommand $Session $Command
    $script:Steps.Add($Name)
    Shot $Session $Name
    return $r
}

# The strip's "+" button, the control the old click landed on, invoked over
# UIA. Both hosts build one and the hidden host stays in the tree, so only an
# on-screen button counts.
function New-TabByButton($Session, [string]$Name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'New tab')
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([SeamWin]::P($Session.Hwnd64))
    $button = $null
    foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if (-not $el.Current.IsOffscreen) { $button = $el; break }
    }
    if ($null -eq $button) { throw 'HARVEST_MISS: no on-screen New tab button' }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 600
    return Step $Session $Name @{ op = 'get-state' }
}

try {
    Assert-NoWintty -Context 'The probe harness'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config
    Write-Host "hwnd=$($session.Hwnd64) pid=$($session.Proc.Id)"
    [SeamWin]::PlaceOnTop($session.Hwnd64)
    Shot $session '00-launch'

    $start = Invoke-SeamCommand $session @{ op = 'get-state' }
    $tabsBefore = @($start.state.tabs).Count

    [void](New-TabByButton $session '01-new-tab')
    $afterPlus = New-TabByButton $session '02-new-tab-2'
    $tabsAfterPlus = @($afterPlus.state.tabs).Count
    if ($tabsAfterPlus -ne $tabsBefore + 2) {
        $script:Findings.Add("two New tab clicks took the tab count from $tabsBefore to $tabsAfterPlus")
    }

    if ($afterPlus.state.vertical) {
        [void](Step $session '03-sidebar-toggle' @{ op = 'toggle-sidebar' })
        [void](Step $session '03b-sidebar-back' @{ op = 'toggle-sidebar' })
    }

    [void](Step $session '04-select-tab' @{ op = 'select'; index = 0 })

    $pane = Step $session '05-pane-menu' @{ op = 'menu'; target = 'pane' }
    if (@($pane.menus).Count -eq 0) { $script:Findings.Add('the pane context menu did not open') }
    [void](Step $session '06-pane-menu-dismiss' @{ op = 'menu'; target = 'dismiss' })

    if (-not $afterPlus.state.vertical) {
        $tab = Step $session '07-tab-menu' @{ op = 'menu'; target = 'tab'; index = 0 }
        if (@($tab.menus).Count -eq 0) { $script:Findings.Add('the tab context menu did not open') }
        [void](Step $session '08-tab-menu-dismiss' @{ op = 'menu'; target = 'dismiss' })
    }

    [void](Step $session '09-wheel' @{ op = 'scroll'; notches = -6 })

    $rc = [SeamWin]::RectOf($session.Hwnd64)
    if ($null -eq $rc) { throw 'HARVEST_MISS: lost the window rect before resize' }
    [void][SeamWin]::MoveWindow([SeamWin]::P($session.Hwnd64), $rc.L, $rc.T, 720, 480, $true)
    Start-Sleep -Milliseconds 400
    [void](Step $session '10-resize-small' @{ op = 'get-state' })
    $rc = [SeamWin]::RectOf($session.Hwnd64)
    [void][SeamWin]::MoveWindow([SeamWin]::P($session.Hwnd64), $rc.L, $rc.T, 1400, 900, $true)
    Start-Sleep -Milliseconds 400
    [void](Step $session '11-resize-large' @{ op = 'get-state' })

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
    $alive = ($null -ne $session) -and ($null -ne $session.Proc) -and -not $session.Proc.HasExited
    if ($null -ne $session) { Stop-SeamSession $session }
}

$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
if ($crashGrew) { $script:Findings.Add('crash.log grew during the run') }

[ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=<session token>); no synthesized OS input'
    alive     = $alive
    crashGrew = $crashGrew
    steps     = $script:Steps
    findings  = $script:Findings
    harness   = $harnessError
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8
Write-Host "alive=$alive crashGrew=$crashGrew findings=$($script:Findings.Count)"

if ($script:Findings.Count -gt 0) { exit 2 }
if ($harnessError) { exit 1 }
exit 0
