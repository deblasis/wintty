#requires -Version 7
<#
    Palette-driven dialogs: the pane menu, Search Scrollback, Toggle
    Inspector, About and Keyboard Shortcuts.

    Seam-actuated: the harness synthesizes no OS input and never takes the
    foreground. The pane menu opens through the seam's menu op (the
    right-click's own press and release halves at the pane's centre), and
    the palette through focus{frame} + Ctrl+Shift+P, the window's real chord
    routing. Everything inside - menu items, palette rows, the search bar,
    the dialogs' buttons - is UIA, with a loud HARVEST_MISS where a bounds
    click used to be. The inspector window is closed with a WM_CLOSE posted
    to that window, and the cheat sheet with its own Close button.

    Gated: the pane menu lists Close Pane and Change Tab Title..., Search
    Scrollback shows the search bar, the inspector toggle opens a window or
    says it cannot, About opens a window, Keyboard Shortcuts shows its
    search box, and the app survives it all without crash.log growing.

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
$harnessError = ''
$session = $null
$paneMenuClose = $false
$paneMenuChange = $false
$searchBar = $false
$inspectorOpened = $false
$inspectorNotice = $false
$aboutOpened = $false
$cheatsheet = $false
$copyMd = $false
$saveMd = $false
$extraTitles = @()

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

function Open-Palette($Session) {
    [void](Invoke-SeamCommand $Session @{ op = 'focus'; target = 'frame' })
    $r = Invoke-SeamCommand $Session @{ op = 'chord'; key = 0x50; ctrl = $true; shift = $true }
    if (-not $r.dispatched) {
        throw "HARVEST_MISS: the palette chord was not dispatched (focus was '$($r.focus)')"
    }
    Start-Sleep -Milliseconds 400
}

function Set-PaletteFilter([int64]$Hwnd64, [string]$Text) {
    # By AutomationId: the terminal's 1x1 IME sink is also an Edit and sorts
    # ahead of the palette in the tree.
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SearchBox')
    $edit = (Get-Root $Hwnd64).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -eq $edit) { throw 'HARVEST_MISS: no SearchBox in the palette' }
    $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
    Write-Host "filter '$Text'"
    Start-Sleep -Milliseconds 350
}

function Invoke-PaletteRow([int64]$Hwnd64, [string]$Filter, [string]$Title) {
    Set-PaletteFilter $Hwnd64 $Filter
    $el = Wait-Name $Hwnd64 $Title 1500
    if ($null -eq $el) { throw "HARVEST_MISS: palette row '$Title' not found after filter '$Filter'" }
    Invoke-El (Get-ListItemAncestor $el) $Title
    Start-Sleep -Milliseconds 1200
}

function Invoke-PaletteCommand($Session, [int64]$Hwnd64, [string]$Filter, [string]$Title) {
    Open-Palette $Session
    Invoke-PaletteRow $Hwnd64 $Filter $Title
}

# The caption's Close is also named "Close"; a dialog's own sits below it.
function Find-DialogCloseButton([int64]$Hwnd64) {
    $rc = [SeamWin]::RectOf($Hwnd64)
    $root = Get-Root $Hwnd64
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

function Get-Extras([uint32]$ProcId, [int64]$MainHwnd) {
    return @(Get-SeamWinUiWindows $ProcId | Where-Object { $_.Hwnd64 -ne $MainHwnd })
}

function Wait-Extras([uint32]$ProcId, [int64]$MainHwnd, [int]$Ms = 5000) {
    $deadline = (Get-Date).AddMilliseconds($Ms)
    do {
        $extras = Get-Extras $ProcId $MainHwnd
        if ($extras.Count -gt 0) { return $extras }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    return @()
}

function Close-Extras([uint32]$ProcId, [int64]$MainHwnd) {
    foreach ($w in (Get-Extras $ProcId $MainHwnd)) {
        Write-Host "closing extra '$($w.Title)' hwnd=$($w.Hwnd64)"
        # WM_CLOSE posted to that window: window-targeted, no focus taken.
        [void][SeamWin]::PostMessage([SeamWin]::P($w.Hwnd64), 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    }
    $deadline = (Get-Date).AddSeconds(4)
    while ((Get-Extras $ProcId $MainHwnd).Count -gt 0 -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
}

try {
    Assert-NoWintty -Context 'The dialogs harness'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config
    $pid32 = [uint32]$session.Proc.Id
    $hwnd64 = [int64]$session.Hwnd64
    [SeamWin]::PlaceOnTop($hwnd64)
    Write-Host "hwnd=$hwnd64 pid=$pid32"
    Shot $hwnd64 '00-launch'

    # The pane menu, and the palette from its own item.
    $menu = Invoke-SeamCommand $session @{ op = 'menu'; target = 'pane' }
    $items = @($menu.menus | ForEach-Object { $_.items } | ForEach-Object { $_.text })
    $paneMenuClose = $items -contains 'Close Pane'
    $paneMenuChange = $items -contains 'Change Tab Title...'
    Write-Host "paneMenu ClosePane=$paneMenuClose ChangeTabTitle=$paneMenuChange items=$($items -join '|')"
    Shot $hwnd64 '00b-pane-menu'
    $pal = Wait-Name $hwnd64 'Command Palette'
    Invoke-El $pal 'Command Palette'
    Invoke-PaletteRow $hwnd64 'search' 'Search Scrollback'

    $searchBar = $null -ne (Wait-Name $hwnd64 'Search scrollback')
    Write-Host "searchBar=$searchBar"
    Shot $hwnd64 '01-search'
    $closeSearch = Find-Name (Get-Root $hwnd64) 'Close search'
    if ($null -ne $closeSearch) { Invoke-El $closeSearch 'Close search' }

    Invoke-PaletteCommand $session $hwnd64 'inspector' 'Toggle Inspector'
    $extras = Wait-Extras $pid32 $hwnd64 3000
    Shot-Pid $pid32 '02-inspector'
    $inspectorOpened = @($extras | Where-Object { $_.Title -match 'Inspector' }).Count -gt 0
    $inspectorNotice = $null -ne (Find-Name (Get-Root $hwnd64) 'Inspector unavailable')
    Write-Host "inspector window=$inspectorOpened notice=$inspectorNotice"
    $extraTitles += @($extras | ForEach-Object { $_.Title })
    Close-Extras $pid32 $hwnd64

    Invoke-PaletteCommand $session $hwnd64 'about' 'About'
    $extras = Wait-Extras $pid32 $hwnd64
    $aboutOpened = $extras.Count -gt 0
    $extraTitles += @($extras | ForEach-Object { $_.Title })
    Write-Host "about window=$aboutOpened"
    Shot-Pid $pid32 '03-about'
    Close-Extras $pid32 $hwnd64

    Invoke-PaletteCommand $session $hwnd64 'keyboard' 'Keyboard Shortcuts'
    $cheatsheet = $null -ne (Wait-Name $hwnd64 'Search shortcuts...' 4000)
    $copyMd = $null -ne (Find-Name (Get-Root $hwnd64) 'Copy as Markdown')
    $saveMd = $null -ne (Find-Name (Get-Root $hwnd64) 'Save...')
    Write-Host "cheatsheet=$cheatsheet copy=$copyMd save=$saveMd"
    Shot $hwnd64 '04-cheatsheet'
    $close = Find-DialogCloseButton $hwnd64
    if ($null -eq $close) { throw 'HARVEST_MISS: no Close on the Keyboard Shortcuts dialog' }
    Invoke-El $close 'cheat sheet Close'
    if ($null -eq [SeamWin]::RectOf($hwnd64)) { throw 'PRODUCT_FAIL: the main window died after closing the cheat sheet' }
    Shot $hwnd64 '05-cheatsheet-dismiss'

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
if (-not $harnessError) {
    if (-not $paneMenuClose) { $script:Findings.Add('the pane menu has no Close Pane') }
    if (-not $paneMenuChange) { $script:Findings.Add('the pane menu has no Change Tab Title...') }
    if (-not $searchBar) { $script:Findings.Add('Search Scrollback did not show the search bar') }
    if (-not ($inspectorOpened -or $inspectorNotice)) { $script:Findings.Add('Toggle Inspector opened no window and said nothing') }
    if (-not $aboutOpened) { $script:Findings.Add('About opened no window') }
    if (-not $cheatsheet) { $script:Findings.Add('Keyboard Shortcuts did not show its search box') }
}

[ordered]@{
    actuation = 'seam (WINTTY_TEST_SEAM=<session token>); menu and palette by the seam, everything inside by UIA'
    alive = $alive
    crashGrew = $crashGrew
    paneMenuClosePane = $paneMenuClose
    paneMenuChangeTabTitle = $paneMenuChange
    searchBar = $searchBar
    inspectorWindow = $inspectorOpened
    inspectorNotice = $inspectorNotice
    aboutWindow = $aboutOpened
    extraTitles = $extraTitles
    cheatsheet = $cheatsheet
    cheatsheetCopy = $copyMd
    cheatsheetSave = $saveMd
    findings = $script:Findings
    harness = $harnessError
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)

if ($script:Findings.Count -gt 0) { exit 2 }
if ($harnessError) { exit 1 }
exit 0
