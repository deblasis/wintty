#requires -Version 7
<#
    confirm-close-surface=always: closing a tab must raise the
    confirmation dialog, Cancel must keep the tab, Close must drop it.

    Seam-actuated (#930): seed two tabs, put focus on the frame, and raise
    the close through chord{0x57,ctrl,shift} - Ctrl+Shift+W, the window's
    real routing, the same TabCloseConfirmation.RequestAsync every close
    path shares. The dialog is then found and answered by UIA exactly as
    before; the harness synthesizes zero OS input.

    Two entry-point changes come with that, both stated: the close used to
    be the tab menu's Close item and closed the LEFTMOST tab the
    right-click landed on; the chord closes the ACTIVE tab. And the tab
    counts are the seam's manager truth now, not a UIA tree walk - the
    question is whether tabs survive, and the manager is the authority.

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
confirm-close-surface = always
'@

$titles = @('confirm-a', 'confirm-b')
$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$script:Findings = [System.Collections.Generic.List[string]]::new()
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

function Find-Name($root, [string]$name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-El($el, [string]$what) {
    if ($null -eq $el) { throw "HARVEST_MISS: no UIA element for $what" }
    $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pat.Invoke()
    Write-Host "invoke $what"
    Start-Sleep -Milliseconds 400
}

# The caption's Close is also named "Close"; invoking it kills Wintty. The
# dialog's button sits below the caption band.
function Find-DialogCloseButton($root, [int64]$Hwnd64) {
    $rc = [SeamWin]::RectOf($Hwnd64)
    if ($null -eq $rc -or $null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    foreach ($b in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if ($b.Current.Name -ne 'Close') { continue }
        if ($b.Current.BoundingRectangle.Y -gt ($rc.T + 40)) { return $b }
    }
    return $null
}

function TabCount($Session) {
    # Response assigned first: a member chain on a hashtable LITERAL in
    # argument mode parses as separate arguments and binds $Command null.
    $r = Invoke-SeamCommand $Session @{ op = 'get-state' }
    return @($r.state.tabs).Count
}

try {
    Assert-NoWintty -Context 'The confirm-always harness'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config
    $hwnd64 = [int64]$session.Hwnd64
    Write-Host "hwnd=$hwnd64 pid=$($session.Proc.Id)"
    Shot $session '00-launch'

    [void](Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = 2; titles = $titles })
    $tabsAfterNew = TabCount $session
    Write-Host "tabsAfterNew=$tabsAfterNew"
    if ($tabsAfterNew -ne 2) { $script:Findings.Add("seed left $tabsAfterNew tabs, wanted 2") }
    Shot $session '01-two-tabs'

    function Invoke-CloseChord {
        [void](Invoke-SeamCommand $session @{ op = 'focus'; target = 'frame' })
        $r = Invoke-SeamCommand $session @{ op = 'chord'; key = 0x57; ctrl = $true; shift = $true }
        if (-not $r.dispatched) {
            throw "HARVEST_MISS: the close chord was not dispatched (focus was '$($r.focus)')"
        }
        # The dialog is shown asynchronously; poll rather than read once,
        # so a busy machine's slow ContentDialog is not filed as a finding.
        $deadline = (Get-Date).AddSeconds(5)
        do {
            Start-Sleep -Milliseconds 250
            $root = [System.Windows.Automation.AutomationElement]::FromHandle([SeamWin]::P($hwnd64))
            if ($null -ne (Find-Name $root 'Close tab?')) { return $root }
        } while ((Get-Date) -lt $deadline)
        return $root
    }

    # First close: the dialog must appear.
    $root = Invoke-CloseChord
    $dlg = Find-Name $root 'Close tab?'
    $dialogShown = $null -ne $dlg
    Write-Host "dialogShown=$dialogShown"
    Shot $session '02-dialog'
    if (-not $dialogShown) {
        $script:Findings.Add('confirm-close-surface=always did not show the Close tab? dialog for the chord close')
    }
    else {
        $cancel = Find-Name $root 'Cancel'
        if ($null -eq $cancel) { throw "HARVEST_MISS: Cancel on close dialog" }
        Invoke-El $cancel 'Cancel'
        Start-Sleep -Milliseconds 400
        $tabsAfterCancel = TabCount $session
        Write-Host "tabsAfterCancel=$tabsAfterCancel"
        Shot $session '03-after-cancel'
        if ($tabsAfterCancel -ne $tabsAfterNew) {
            $script:Findings.Add("Cancel dropped a tab: $tabsAfterCancel of $tabsAfterNew")
        }
    }

    # Second close: answer it.
    $root = Invoke-CloseChord
    $closeBtn = Find-DialogCloseButton $root $hwnd64
    if ($null -eq $closeBtn) { throw "HARVEST_MISS: Close on confirm dialog (below caption)" }
    Invoke-El $closeBtn 'dialog Close'
    Start-Sleep -Milliseconds 800
    $tabsAfterClose = TabCount $session
    Write-Host "tabsAfterClose=$tabsAfterClose"
    Shot $session '04-after-close'
    if ($tabsAfterClose -ge $tabsAfterNew) {
        $script:Findings.Add("the confirmed close left $tabsAfterClose tabs of $tabsAfterNew")
    }

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
    if ($null -ne $session) { Stop-SeamSession $session }
}

if ((Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)) {
    $script:Findings.Add('crash.log grew during the run')
}

[ordered]@{
    actuation       = 'seam (WINTTY_TEST_SEAM=<session token>); close via focus+chord, dialog answered by UIA'
    dialogShown     = $(if ($null -ne $dlg) { $true } else { $false })
    tabsAfterNew    = $tabsAfterNew
    tabsAfterCancel = $tabsAfterCancel
    tabsAfterClose  = $tabsAfterClose
    findings        = $script:Findings
    harness         = $harnessError
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

if ($script:Findings.Count -gt 0) { exit 2 }
if ($harnessError) { exit 1 }
exit 0
