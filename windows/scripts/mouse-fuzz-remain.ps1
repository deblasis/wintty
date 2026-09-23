#requires -Version 7
<#
    Remaining chrome: the tab overview, the tab menu (rename, colour, snap
    zone), the pane menu (zoom, paste) and the quake terminal.

    Seam-actuated: the harness synthesizes no OS input and never takes the
    foreground. The palette opens through focus{frame} + Ctrl+Shift+P, the
    window's real chord routing; the tab and pane menus through the seam's
    menu op, the keyboard's context request that reaches the same handler a
    right-click does; a click outside a flyout is menu{dismiss}, and the
    overview's Escape is overview-key, its own key handler. Everything inside
    - palette rows, menu items, the rename dialog's Cancel - is UIA, with a
    loud HARVEST_MISS where a bounds click used to be.

    Gated, as before: the overview opens and closes, and the app survives
    it all without crash.log growing. Rename, colour, snap, zoom, paste and
    quake are driven and logged but not gated, so a build missing all six
    still passes.

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

# The tab menu is the horizontal strip's; the vertical one builds its own.
$Config = @'
window-save-state = never
vertical-tabs = false
'@

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$script:Findings = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null
$overview = $false
$overviewClosed = $false
$newTab = $false
$rename = $false
$tabColor = $false
$moveZone = $false
$renameDialog = $false
$colorPicker = $false
$zoomPane = $false
$zoomed = $false
$paste = $false
$quakeTried = $false
$quakeExtras = 0

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

function Get-Root([int64]$Hwnd64) {
    return [System.Windows.Automation.AutomationElement]::FromHandle([SeamWin]::P($Hwnd64))
}

function Find-Name($Root, [string]$Name) {
    if ($null -eq $Root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Wait-Name([int64]$Hwnd64, [string]$Name, [int]$Ms = 3000) {
    $deadline = (Get-Date).AddMilliseconds($Ms)
    do {
        $el = Find-Name (Get-Root $Hwnd64) $Name
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 120
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Get-ListItemAncestor($El) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $cur = $El
    while ($null -ne $cur) {
        try {
            if ($cur.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { return $cur }
        } catch { return $El }
        $cur = $walker.GetParent($cur)
    }
    return $El
}

function Invoke-El($El, [string]$What) {
    if ($null -eq $El) { throw "HARVEST_MISS: no UIA element for $What" }
    # InvokePattern or a loud miss - never a bounds click.
    $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Host "invoke $What"
    Start-Sleep -Milliseconds 400
}

function Invoke-PaletteCommand($Session, [int64]$Hwnd64, [string]$Filter, [string]$Title) {
    [void](Invoke-SeamCommand $Session @{ op = 'focus'; target = 'frame' })
    $r = Invoke-SeamCommand $Session @{ op = 'chord'; key = 0x50; ctrl = $true; shift = $true }
    if (-not $r.dispatched) {
        throw "HARVEST_MISS: the palette chord was not dispatched (focus was '$($r.focus)')"
    }
    Start-Sleep -Milliseconds 400
    # By AutomationId: the terminal's 1x1 IME sink is also an Edit.
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SearchBox')
    $edit = (Get-Root $Hwnd64).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -eq $edit) { throw 'HARVEST_MISS: no SearchBox in the palette' }
    $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Filter)
    Start-Sleep -Milliseconds 350
    $el = Wait-Name $Hwnd64 $Title 1500
    if ($null -eq $el) { throw "HARVEST_MISS: palette row '$Title' not found after filter '$Filter'" }
    Invoke-El (Get-ListItemAncestor $el) $Title
    Start-Sleep -Milliseconds 1200
}

function Get-MenuItems($Response) {
    return @($Response.menus | ForEach-Object { $_.items } | ForEach-Object { $_.text })
}

function Dismiss-Menus($Session) {
    $r = Invoke-SeamCommand $Session @{ op = 'menu'; target = 'dismiss' }
    if (@($r.menus).Count -gt 0) { throw 'HARVEST_MISS: a flyout stayed open after menu{dismiss}' }
}

try {
    Assert-NoWintty -Context 'The remain harness'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config
    $pid32 = [uint32]$session.Proc.Id
    $hwnd64 = [int64]$session.Hwnd64
    [SeamWin]::PlaceOnTop($hwnd64)
    Write-Host "hwnd=$hwnd64 pid=$pid32"
    Shot $hwnd64 '00-launch'

    # The overview: the one gated feature here.
    Invoke-PaletteCommand $session $hwnd64 'show all' 'Show all tabs'
    $overview = $null -ne (Wait-Name $hwnd64 'All tabs')
    Shot $hwnd64 '00b-overview'
    if ($overview) {
        $r = Invoke-SeamCommand $session @{ op = 'overview-key'; key = 'escape' }
        $overviewClosed = -not $r.open
    }
    Write-Host "overview=$overview closed=$overviewClosed"

    # A second tab, so the snap-zone item has something to move.
    $plus = Find-Name (Get-Root $hwnd64) 'New tab'
    if ($null -ne $plus) {
        Invoke-El $plus 'New tab'
        $newTab = @((Invoke-SeamCommand $session @{ op = 'get-state' }).state.tabs).Count -ge 2
        Shot $hwnd64 '01-two-tabs'
    } else {
        Write-Host 'HARVEST_MISS: New tab button'
    }

    $menu = Invoke-SeamCommand $session @{ op = 'menu'; target = 'tab'; index = 0 }
    $items = Get-MenuItems $menu
    $rename = $items -contains 'Rename Tab'
    $tabColor = $items -contains 'Tab Color...'
    $moveZone = $items -contains 'Move Tab to Zone'
    Write-Host "tabMenu rename=$rename color=$tabColor zone=$moveZone"
    Shot $hwnd64 '02-tab-menu'
    if ($rename) {
        Invoke-El (Wait-Name $hwnd64 'Rename Tab') 'Rename Tab'
        $cancel = Wait-Name $hwnd64 'Cancel'
        $renameDialog = ($null -ne (Find-Name (Get-Root $hwnd64) 'Rename tab')) -or ($null -ne $cancel)
        Write-Host "renameDialog=$renameDialog"
        Shot $hwnd64 '03-rename'
        if ($null -ne $cancel) { Invoke-El $cancel 'Cancel rename' }
    } else {
        Dismiss-Menus $session
    }

    $menu = Invoke-SeamCommand $session @{ op = 'menu'; target = 'tab'; index = 0 }
    if ((Get-MenuItems $menu) -contains 'Tab Color...') {
        Invoke-El (Wait-Name $hwnd64 'Tab Color...') 'Tab Color...'
        $colorPicker = ($null -ne (Wait-Name $hwnd64 'Blue' 1500)) -or ($null -ne (Find-Name (Get-Root $hwnd64) 'None'))
        Write-Host "colorPicker=$colorPicker"
        Shot $hwnd64 '04-tab-color'
    }
    Dismiss-Menus $session

    $menu = Invoke-SeamCommand $session @{ op = 'menu'; target = 'tab'; index = 0 }
    if ((Get-MenuItems $menu) -contains 'Move Tab to Zone') {
        Invoke-El (Wait-Name $hwnd64 'Move Tab to Zone') 'Move Tab to Zone'
        Start-Sleep -Milliseconds 500
        Shot $hwnd64 '05-snap-zone'
    } else {
        Write-Host "HARVEST_MISS: Move Tab to Zone (need 2 tabs? newTab=$newTab)"
    }
    Dismiss-Menus $session

    $menu = Invoke-SeamCommand $session @{ op = 'menu'; target = 'pane' }
    $items = Get-MenuItems $menu
    $zoomPane = $items -contains 'Zoom Pane'
    $paste = $items -contains 'Paste'
    Write-Host "paneMenu zoom=$zoomPane paste=$paste items=$($items -join '|')"
    Shot $hwnd64 '06-pane-menu'
    if ($zoomPane) {
        Invoke-El (Wait-Name $hwnd64 'Zoom Pane') 'Zoom Pane'
        Start-Sleep -Milliseconds 500
        $zoomed = $true
        Shot $hwnd64 '07-zoomed'
    } else {
        Dismiss-Menus $session
    }

    try {
        Invoke-PaletteCommand $session $hwnd64 'quake' 'Toggle Quake Terminal'
        $quakeTried = $true
    } catch {
        Write-Host "palette quake: $_"
    }
    Start-Sleep -Seconds 1
    Shot-Pid $pid32 '08-quake'
    $extras = @(Get-SeamWinUiWindows $pid32 | Where-Object { $_.Hwnd64 -ne $hwnd64 })
    $quakeExtras = $extras.Count
    Write-Host "quake extras=$quakeExtras"
    $extras | ForEach-Object { Write-Host "  $($_.Title) $($_.Hwnd64)" }

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

# A dead process or a grown crash.log is evidence about the build whatever
# else happened, so it is filed even when the run could not finish.
$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
if ($crashGrew) { $script:Findings.Add('crash.log grew during the run') }
if (-not $harnessError) {
    if (-not $overview) { $script:Findings.Add('Show all tabs did not open the overview') }
    elseif (-not $overviewClosed) { $script:Findings.Add('Escape in the overview did not close it') }
}

[ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=<session token>); palette, menus and overview by the seam, everything inside by UIA'
    alive = $alive
    crashGrew = $crashGrew
    newTab = $newTab
    overview = $overview
    overviewClosed = $overviewClosed
    renameMenu = $rename
    tabColorMenu = $tabColor
    moveZoneMenu = $moveZone
    renameDialog = $renameDialog
    colorPicker = $colorPicker
    zoomPane = $zoomPane
    zoomed = $zoomed
    paste = $paste
    quakeTried = $quakeTried
    quakeExtras = $quakeExtras
    quake = $quakeExtras -gt 0
    findings = $script:Findings
    harness = $harnessError
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)

if ($script:Findings.Count -gt 0) { exit 2 }
if ($harnessError) { exit 1 }
exit 0
