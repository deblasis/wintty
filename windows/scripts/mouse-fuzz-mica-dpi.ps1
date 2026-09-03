#requires -Version 7
# Appearance backdrop (Mica/Acrylic/Crystal) + DPI + palette backdrop.
# Seam-launched (#930): the palette opens through focus{frame} +
# chord{0x50,ctrl,shift} rather than a right-click on the pane grid, and the
# combo is driven by ExpandCollapsePattern alone - the bounds click it fell
# back to was always followed by an Expand() anyway, and was this harness's
# last synthesized input. Zero OS input now.
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

# A PRODUCT_FAIL throw is a defect in the build under test, so it has to leave
# with 2. Thrown, it escapes to pwsh and becomes exit 1 - "the harness could
# not run" - which the suite retries and then reports as an area nothing is
# known about. Every finally below still runs: exit from a trap unwinds
# through them, and `break` rethrows anything that is not a product failure so
# a genuine harness failure still leaves with 1.
trap {
    if ("$_" -like 'PRODUCT_FAIL*') {
        Write-Host "$_" -ForegroundColor Red
        exit 2
    }
    break
}
New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots') | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class MzMD {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    public class WinRect { public int L,T,R,B; public int W { get { return R-L; } } public int Hh { get { return B-T; } } }
    public static IntPtr P(long hwnd) { return new IntPtr(hwnd); }
    public static WinRect RectOf(long hwnd) {
        var h = P(hwnd); RECT r;
        if (!IsWindow(h) || !GetWindowRect(h, out r)) return null;
        var wr = new WinRect { L=r.L,T=r.T,R=r.R,B=r.B };
        return (wr.W < 80 || wr.Hh < 80) ? null : wr;
    }
    public static string ClassOf(IntPtr h) {
        var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString();
    }
    public static string TitleOf(IntPtr h) {
        var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString();
    }
}
'@

function Get-WinUiWindows([uint32]$ProcId) {
    $hits = [System.Collections.Generic.List[object]]::new()
    $cb = [MzMD+EnumProc]{
        param($h,$lp)
        [uint32]$o=0; [void][MzMD]::GetWindowThreadProcessId($h,[ref]$o)
        if ($o -ne $ProcId -or -not [MzMD]::IsWindowVisible($h)) { return $true }
        if ([MzMD]::ClassOf($h) -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [MzMD]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64=$hwnd64; Title=[MzMD]::TitleOf($h); Area=($rc.W*$rc.Hh) })
        return $true
    }
    [void][MzMD]::EnumWindows($cb,[IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending
}

function Shot([int64]$Hwnd64, [string]$name) {
    $rc = [MzMD]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw "HARVEST_MISS: degenerate rect for $name" }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L,$rc.T,0,0,$bmp.Size)
    $p = Join-Path $OutDir "shots\$name.png"
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose()
    Write-Host "shot $name $($rc.W)x$($rc.Hh) title=$([MzMD]::TitleOf([MzMD]::P($Hwnd64)))"
}

function Shot-Pid([uint32]$ProcId, [string]$prefix) {
    $i = 0
    foreach ($w in @(Get-WinUiWindows $ProcId)) {
        $safe = ($w.Title -replace '[^A-Za-z0-9]+','-').Trim('-')
        if (-not $safe) { $safe = 'untitled' }
        Shot $w.Hwnd64 ("{0}-{1}-{2}" -f $prefix, $i, $safe)
        $i++
    }
    Write-Host "pid windows: $i"
}

function Find-Name($root, [string]$name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-ListItemAncestor($el) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $cur = $el
    while ($null -ne $cur) {
        try {
            if ($cur.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { return $cur }
        } catch { return $el }
        $cur = $walker.GetParent($cur)
    }
    return $el
}

function Invoke-El($el, [string]$what) {
    if ($null -eq $el) { throw "HARVEST_MISS: no UIA element for $what" }
    # InvokePattern or a loud miss - never a bounds click (#930).
    $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pat.Invoke()
    Write-Host "invoke $what"
    Start-Sleep -Milliseconds 400
}

function Read-ConfigKey([string]$path, [string]$key) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        if ($line -match ('^\s*' + [regex]::Escape($key) + '\s*=\s*(.+?)\s*$')) {
            return $Matches[1]
        }
    }
    return $null
}

function Show-El($el) {
    if ($null -eq $el) { return }
    try {
        $pat = $el.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)
        $pat.ScrollIntoView()
        Start-Sleep -Milliseconds 300
        Write-Host 'scrolled into view'
    } catch { }
}

function Find-NameOnPid([uint32]$ProcId, [string]$name) {
    $found = $null
    $cb = [MzMD+EnumProc]{
        param($h,$lp)
        [uint32]$o=0; [void][MzMD]::GetWindowThreadProcessId($h,[ref]$o)
        if ($o -ne $ProcId -or -not [MzMD]::IsWindowVisible($h)) { return $true }
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
            $el = Find-Name $root $name
            if ($null -ne $el) { $script:foundEl = $el }
        } catch { }
        return $true
    }
    $script:foundEl = $null
    [void][MzMD]::EnumWindows($cb,[IntPtr]::Zero)
    return $script:foundEl
}

function Find-ComboNearName($root, [string]$name) {
    $el = Find-Name $root $name
    if ($null -eq $el) { return $null }
    Show-El $el
    $er = $el.Current.BoundingRectangle
    $comboCt = [System.Windows.Automation.ControlType]::ComboBox
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $comboCt)
    $best = $null
    $bestDy = 1e9
    foreach ($combo in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        $cr = $combo.Current.BoundingRectangle
        if ($cr.Width -lt 8 -or $cr.Height -lt 8) { continue }
        $dy = [Math]::Abs(($cr.Y + $cr.Height/2) - ($er.Y + $er.Height/2))
        if ($dy -lt $bestDy) { $bestDy = $dy; $best = $combo }
    }
    if ($null -ne $best) { Write-Host ("combo near '{0}' dy={1:N0}" -f $name, $bestDy) }
    return $best
}

function Dump-NamesMatching($root, [string]$needle) {
    if ($null -eq $root) { return }
    foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)) {
        $n = $el.Current.Name
        if ($n -and $n -match $needle) {
            Write-Host ("UIA '{0}' {1}" -f $n, $el.Current.ControlType.ProgrammaticName)
        }
    }
}

# AutomationId first, proximity only as a fallback. Locating a control by how
# close it sits to a label works right up until the layout moves: this failed
# on its second pass over 'Backdrop preset' after the first one changed the
# backdrop and reflowed the page. The combos carry x:Name, which WinUI exposes
# as the AutomationId, so there is a stable handle to use instead.
function Find-ComboById($root, [string]$automationId) {
    if ([string]::IsNullOrEmpty($automationId)) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Select-ComboItem($root, [string]$card, [string]$item, [string]$AutomationId) {
    $named = Find-Name $root $card
    Show-El $named
    $combo = Find-ComboById $root $AutomationId
    if ($null -ne $combo) {
        Write-Host "combo by id '$AutomationId'"
    } else {
        $combo = Find-ComboNearName $root $card
    }
    if ($null -eq $combo) { throw "HARVEST_MISS: ComboBox near '$card'" }
    Show-El $combo
    # ExpandCollapsePattern is the only opener. The bounds click this used to
    # try first was always followed by an Expand() anyway, and a click is OS
    # input this harness no longer synthesizes (#930).
    $exp = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $exp.Expand()
    Start-Sleep -Milliseconds 400
    Dump-NamesMatching $root 'Acrylic|Frosted|Crystal|Mica|Opaque|Solid|backdrop'
    $itemEl = Find-Name $root $item
    if ($null -eq $itemEl) { $itemEl = Find-NameOnPid $pid32 $item }
    if ($null -eq $itemEl) { throw "HARVEST_MISS: combo item '$item' under '$card'" }
    try {
        $exp = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $exp.Expand()
        Start-Sleep -Milliseconds 350
    } catch { Write-Host "expand '$card' unsupported" }
    $itemEl = Find-Name $root $item
    if ($null -eq $itemEl) {
        # Flyout may still be under the same hwnd after expand.
        $itemEl = Find-Name $root $item
    }
    if ($null -eq $itemEl) { throw "HARVEST_MISS: combo item '$item' under '$card'" }
    try {
        $sel = $itemEl.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sel.Select()
        Write-Host "combo $card -> $item"
        Start-Sleep -Milliseconds 500
        return
    } catch { }
    Invoke-El $itemEl $item
}

function Select-Nav($root, [string]$name) {
    $el = Find-Name $root $name
    if ($null -eq $el) { throw "HARVEST_MISS: nav '$name'" }
    $pat = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pat.Select()
    Write-Host "select $name"
    Start-Sleep -Milliseconds 700
}

function Open-Palette($Session) {
    # The palette chord through the seam - the window's real routing, one
    # call below the framework, where the menu item landed.
    [void](Invoke-SeamCommand $Session @{ op = 'focus'; target = 'frame' })
    $r = Invoke-SeamCommand $Session @{ op = 'chord'; key = 0x50; ctrl = $true; shift = $true }
    if (-not $r.dispatched) {
        throw "HARVEST_MISS: the palette chord was not dispatched (focus was '$($r.focus)')"
    }
    Start-Sleep -Milliseconds 400
}

function Set-PaletteFilter([int64]$MainHwnd, [string]$text) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzMD]::P($MainHwnd))
    # By AutomationId, not "the first Edit under the window". The terminal
    # keeps a 1x1 IME sink TextBox focused whenever a pane has focus, and it
    # sorts ahead of the palette in the tree - so FindFirst(Edit) returned the
    # sink, SetValue typed into it, and the palette never filtered. The list
    # then still held every command, so the lookup below failed on a command
    # that was present the whole time.
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SearchBox')
    $edit = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -eq $edit) { throw "HARVEST_MISS: no SearchBox in palette" }
    $vp = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($text)
    Write-Host "filter '$text'"
    Start-Sleep -Milliseconds 350
}

function Invoke-PaletteCommand($Session, [int64]$MainHwnd, [string]$filter, [string]$title) {
    Open-Palette $Session
    Set-PaletteFilter $MainHwnd $filter
    $el = $null
    $dl = (Get-Date).AddMilliseconds(1200)
    while ((Get-Date) -lt $dl -and $null -eq $el) {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzMD]::P($MainHwnd))
        $el = Find-Name $root $title
        Start-Sleep -Milliseconds 80
    }
    if ($null -eq $el) { throw "HARVEST_MISS: palette item '$title' not under hwnd after filter '$filter'" }
    $el = Get-ListItemAncestor $el
    Invoke-El $el $title
    Start-Sleep -Milliseconds 1200
}

function Wait-SettingsWindow([uint32]$ProcId, [int64]$MainHwnd) {
    $dl = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $dl) {
        $w = @(Get-WinUiWindows $ProcId | Where-Object {
            $_.Hwnd64 -ne $MainHwnd -and $_.Title -match 'Settings'
        })
        if ($w.Count -gt 0) { return $w[0] }
        Start-Sleep -Milliseconds 200
    }
    $titles = @(Get-WinUiWindows $ProcId | ForEach-Object { $_.Title })
    throw "HARVEST_MISS: no Settings window (Open Config probably shelled the file). titles=$($titles -join '|')"
}

function New-ConfigText {
    return @"
windows-single-instance = true
window-save-state = never
windows-settings-ui = true
background-style = solid
background-opacity = 0.80
command-palette-background = mica
profile.pwsh.name = PowerShell
profile.pwsh.command = pwsh.exe
default-profile = pwsh
"@
}

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$session = $null
$proc = $null
$settingsTitle = $null
$dpi = 0
$perMonitorV2 = $false
$appearanceCards = @()
$styleAfterFrosted = $null
$styleAfterCrystal = $null
$paletteBackdropAfter = $null

Assert-NoWintty -Context 'The mica-dpi harness'
try {
    $session = Start-SeamSession -ExePath $ExePath -ConfigText (New-ConfigText)
    $proc = $session.Proc
    $pid32 = [uint32]$proc.Id
    $hwnd64 = [int64]$session.Hwnd64
    # Read-back path, resolved after the session exists (it owns the temp XDG).
    $configPath = Join-Path $session.TempXdg 'wintty\config.wintty'
    $dpi = [MzMD]::GetDpiForWindow([MzMD]::P($hwnd64))
    $manifest = Join-Path (Split-Path $ExePath) '..\..\..\..\app.manifest'
    # Runtime copy: fall back to the source tree if not next to the exe.
    # Resolved from this script's location so it works in any checkout.
    $manifestSrc = if (Test-Path $manifest) { $manifest }
                   else { Join-Path $PSScriptRoot '..\Ghostty\app.manifest' }
    $perMonitorV2 = (Get-Content $manifestSrc -Raw) -match 'PerMonitorV2'
    Write-Host "hwnd=$hwnd64 pid=$pid32 dpi=$dpi perMonitorV2=$perMonitorV2"
    Shot $hwnd64 '00-launch-mica-solid'

    Invoke-PaletteCommand $session $hwnd64 'open config' 'Open Config'
    $settings = Wait-SettingsWindow $pid32 $hwnd64
    $settingsHwnd = [int64]$settings.Hwnd64
    $settingsTitle = $settings.Title
    Write-Host "settings hwnd=$settingsHwnd title=$settingsTitle"
    Shot $settingsHwnd '01-settings-open'

    $sroot = [System.Windows.Automation.AutomationElement]::FromHandle([MzMD]::P($settingsHwnd))
    Select-Nav $sroot 'Appearance'
    Shot $settingsHwnd '02-appearance'
    $sroot = [System.Windows.Automation.AutomationElement]::FromHandle([MzMD]::P($settingsHwnd))
    foreach ($name in @('Window mode', 'Backdrop preset', 'Background opacity')) {
        if ($null -ne (Find-Name $sroot $name)) { $appearanceCards += $name }
        else { Write-Host "HARVEST_MISS: card '$name'" }
    }
    if ($appearanceCards.Count -lt 3) { throw "PRODUCT_FAIL: missing appearance cards $($appearanceCards -join ',')" }

    Select-ComboItem $sroot 'Backdrop preset' 'Frosted (Acrylic)' -AutomationId 'BackgroundStyleCombo'
    Start-Sleep -Milliseconds 400
    $styleAfterFrosted = Read-ConfigKey $configPath 'background-style'
    Write-Host "styleAfterFrosted=$styleAfterFrosted"
    Shot $settingsHwnd '03-frosted'
    if ($styleAfterFrosted -ne 'frosted') { throw "PRODUCT_FAIL: expected background-style=frosted got '$styleAfterFrosted'" }

    $sroot = [System.Windows.Automation.AutomationElement]::FromHandle([MzMD]::P($settingsHwnd))
    Select-ComboItem $sroot 'Backdrop preset' 'Crystal (Zero blur)' -AutomationId 'BackgroundStyleCombo'
    Start-Sleep -Milliseconds 400
    $styleAfterCrystal = Read-ConfigKey $configPath 'background-style'
    Write-Host "styleAfterCrystal=$styleAfterCrystal"
    Shot $settingsHwnd '04-crystal'
    if ($styleAfterCrystal -ne 'crystal') { throw "PRODUCT_FAIL: expected background-style=crystal got '$styleAfterCrystal'" }

    $sroot = [System.Windows.Automation.AutomationElement]::FromHandle([MzMD]::P($settingsHwnd))
    Select-Nav $sroot 'General'
    Shot $settingsHwnd '05-general'
    $sroot = [System.Windows.Automation.AutomationElement]::FromHandle([MzMD]::P($settingsHwnd))
    if ($null -eq (Find-Name $sroot 'Command palette backdrop')) {
        throw "PRODUCT_FAIL: missing Command palette backdrop card"
    }
    Select-ComboItem $sroot 'Command palette backdrop' 'Opaque'
    Start-Sleep -Milliseconds 400
    $paletteBackdropAfter = Read-ConfigKey $configPath 'command-palette-background'
    Write-Host "paletteBackdropAfter=$paletteBackdropAfter"
    Shot $settingsHwnd '06-palette-opaque'
    if ($paletteBackdropAfter -ne 'opaque') { throw "PRODUCT_FAIL: expected command-palette-background=opaque got '$paletteBackdropAfter'" }

    $sroot = [System.Windows.Automation.AutomationElement]::FromHandle([MzMD]::P($settingsHwnd))
    $close = Find-Name $sroot 'Close'
    if ($null -ne $close) { Invoke-El $close 'Close Wintty Settings' }
    Start-Sleep -Milliseconds 400
    if ($null -ne [MzMD]::RectOf($hwnd64)) { Shot $hwnd64 '07-after-settings-close' }
}
finally {
    if ($null -ne $session) { Stop-SeamSession $session }
}

$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
$result = @{
    crashGrew = $crashGrew
    dpi = $dpi
    perMonitorV2 = $perMonitorV2
    appearanceCards = $appearanceCards
    styleAfterFrosted = $styleAfterFrosted
    styleAfterCrystal = $styleAfterCrystal
    paletteBackdropAfter = $paletteBackdropAfter
    settingsTitle = $settingsTitle
    trayImplemented = $true
}
$result | ConvertTo-Json | Set-Content (Join-Path $OutDir 'result.json')
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)
if ($crashGrew -or $dpi -eq 0 -or -not $perMonitorV2) { exit 2 }
if ($styleAfterFrosted -ne 'frosted' -or $styleAfterCrystal -ne 'crystal' -or $paletteBackdropAfter -ne 'opaque') { exit 2 }
exit 0
