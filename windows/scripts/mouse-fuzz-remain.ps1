#requires -Version 7
# Remaining chrome: tab rename/color/snap, pane zoom/paste, quake.
# UIA scoped to Wintty hwnds only. No desktop-root walk. No modifier chords.
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots') | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class MzD {
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    public static void Key(long hwnd, int vk) {
        var h = P(hwnd);
        PostMessage(h, 0x0100, (IntPtr)vk, IntPtr.Zero);
        Thread.Sleep(40);
        PostMessage(h, 0x0101, (IntPtr)vk, IntPtr.Zero);
    }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    public class WinRect { public int L,T,R,B; public int W { get { return R-L; } } public int Hh { get { return B-T; } } }
    public class Hit { public bool Ok; public string Why; public int X,Y; public uint HitPid; public string HitClass; }
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
    public static uint PidOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }
    static Hit Miss(string why, int x, int y, uint pid, string cls) {
        return new Hit { Ok=false, Why=why, X=x, Y=y, HitPid=pid, HitClass=cls };
    }
    public static Hit ClickScreen(uint pid, int x, int y, bool right) {
        var hit = WindowFromPoint(new POINT { X=x, Y=y });
        uint hitPid = PidOf(hit); string cls = ClassOf(hit);
        if (cls == "WinttySplash") return Miss("splash", x, y, hitPid, cls);
        if (hitPid != pid) return Miss("not Wintty", x, y, hitPid, cls);
        if (!SetCursorPos(x, y)) return Miss("SetCursorPos", x, y, hitPid, cls);
        Thread.Sleep(40);
        hit = WindowFromPoint(new POINT { X=x, Y=y });
        hitPid = PidOf(hit); cls = ClassOf(hit);
        if (hitPid != pid) return Miss("not Wintty after move", x, y, hitPid, cls);
        if (right) {
            mouse_event(MOUSEEVENTF_RIGHTDOWN,0,0,0,UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_RIGHTUP,0,0,0,UIntPtr.Zero);
        } else {
            mouse_event(MOUSEEVENTF_LEFTDOWN,0,0,0,UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP,0,0,0,UIntPtr.Zero);
        }
        Thread.Sleep(250);
        return new Hit { Ok=true, X=x, Y=y, HitPid=hitPid, HitClass=cls };
    }
}
'@

function Get-WinUiWindows([uint32]$ProcId) {
    $hits = [System.Collections.Generic.List[object]]::new()
    $cb = [MzD+EnumProc]{
        param($h,$lp)
        [uint32]$o=0; [void][MzD]::GetWindowThreadProcessId($h,[ref]$o)
        if ($o -ne $ProcId -or -not [MzD]::IsWindowVisible($h)) { return $true }
        if ([MzD]::ClassOf($h) -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [MzD]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64=$hwnd64; Title=[MzD]::TitleOf($h); Area=($rc.W*$rc.Hh) })
        return $true
    }
    [void][MzD]::EnumWindows($cb,[IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending
}

function Splash-Visible([int]$ProcId) {
    $script:splashSeen = $false
    $cb = [MzD+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner=0; [void][MzD]::GetWindowThreadProcessId($hwnd,[ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        if ([MzD]::ClassOf($hwnd) -eq 'WinttySplash' -and [MzD]::IsWindowVisible($hwnd)) { $script:splashSeen = $true }
        return $true
    }
    [void][MzD]::EnumWindows($cb,[IntPtr]::Zero)
    return $script:splashSeen
}

function Wait-Ready($proc) {
    $dl = (Get-Date).AddSeconds(40)
    $got = $null
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 250
        $proc.Refresh(); if ($proc.HasExited) { throw "PRODUCT_FAIL startup exit=$($proc.ExitCode)" }
        $got = @(Get-WinUiWindows ([uint32]$proc.Id)) | Select-Object -First 1
        if ($got) { break }
    }
    if (-not $got) { throw "HARVEST_MISS: no WinUI hwnd" }
    $dl = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $dl) {
        $proc.Refresh(); if ($proc.HasExited) { throw "PRODUCT_FAIL during splash" }
        if (Splash-Visible $proc.Id) { Start-Sleep -Milliseconds 200; continue }
        Start-Sleep -Milliseconds 900
        if (-not (Splash-Visible $proc.Id)) { return $got }
    }
    throw "HARVEST_MISS: splash never dropped"
}

function Shot([int64]$Hwnd64, [string]$name) {
    $rc = [MzD]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw "HARVEST_MISS: degenerate rect for $name" }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L,$rc.T,0,0,$bmp.Size)
    $p = Join-Path $OutDir "shots\$name.png"
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose()
    Write-Host "shot $name $($rc.W)x$($rc.Hh) title=$([MzD]::TitleOf([MzD]::P($Hwnd64)))"
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

# ModeLabel.Text is "Search". Find-Name('Search') hits that TextBlock, not
# the command ListItem. Only accept a match that lives under a ListItem.
function Find-NamedListItem($root, [string]$name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($el in $all) {
        $item = Get-ListItemAncestor $el
        try {
            if ($item.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { return $item }
        } catch { }
    }
    return $null
}

function Invoke-El($el, [uint32]$ProcId, [string]$what, [int64]$MainHwnd = 0) {
    if ($null -eq $el) { throw "HARVEST_MISS: no UIA element for $what" }
    try {
        $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pat.Invoke()
        Write-Host "invoke $what"
        Start-Sleep -Milliseconds 400
        return
    } catch { Write-Host "invoke $what unsupported, clicking bounds" }
    $r = $el.Current.BoundingRectangle
    $x = [int]($r.X + $r.Width/2); $y = [int]($r.Y + $r.Height/2)
    $rc = if ($MainHwnd -ne 0) { [MzD]::RectOf($MainHwnd) } else { $null }
    $inside = $rc -and $x -ge $rc.L -and $x -le $rc.R -and $y -ge $rc.T -and $y -le $rc.B
    if (-not $inside) {
        Write-Host "bounds outside hwnd for $what at $x,$y; Enter"
        if ($MainHwnd -eq 0) { throw "HARVEST_MISS: empty/outside bounds for $what at $x,$y" }
        [MzD]::Key($MainHwnd, 0x0D)
        Start-Sleep -Milliseconds 400
        return
    }
    $hit = [MzD]::ClickScreen($ProcId, $x, $y, $false)
    if (-not $hit.Ok) { throw "HARVEST_MISS: $what click $($hit.Why) class=$($hit.HitClass) at $x,$y" }
    Write-Host "click $what $x,$y"
    Start-Sleep -Milliseconds 400
}

function Open-Palette([int64]$MainHwnd, [uint32]$ProcId) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($MainHwnd))
    $rc = [MzD]::RectOf($MainHwnd)
    $hit = [MzD]::ClickScreen($ProcId, $rc.L + 400, $rc.T + 280, $true)
    if (-not $hit.Ok) { throw "HARVEST_MISS: grid context $($hit.Why) class=$($hit.HitClass)" }
    Start-Sleep -Milliseconds 300
    $pal = $null
    $dl = (Get-Date).AddMilliseconds(1200)
    while ((Get-Date) -lt $dl -and $null -eq $pal) {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($MainHwnd))
        $pal = Find-Name $root 'Command Palette'
        Start-Sleep -Milliseconds 80
    }
    if ($null -eq $pal) {
        # Leftover MenuFlyout eats the next right-click. Left-click the
        # grid to dismiss, then retry once.
        $dismiss = [MzD]::ClickScreen($ProcId, $rc.L + 200, $rc.T + 200, $false)
        Write-Host "palette miss, grid dismiss ok=$($dismiss.Ok)"
        Start-Sleep -Milliseconds 300
        $hit = [MzD]::ClickScreen($ProcId, $rc.L + 400, $rc.T + 280, $true)
        if (-not $hit.Ok) { throw "HARVEST_MISS: grid context retry $($hit.Why) class=$($hit.HitClass)" }
        Start-Sleep -Milliseconds 300
        $dl = (Get-Date).AddMilliseconds(1200)
        while ((Get-Date) -lt $dl -and $null -eq $pal) {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($MainHwnd))
            $pal = Find-Name $root 'Command Palette'
            Start-Sleep -Milliseconds 80
        }
    }
    if ($null -eq $pal) { throw "HARVEST_MISS: Command Palette menu item not under hwnd" }
    Invoke-El $pal $ProcId 'Command Palette' $MainHwnd
    Start-Sleep -Milliseconds 400
}

function Set-PaletteFilter([int64]$MainHwnd, [string]$text) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($MainHwnd))
    $editCt = [System.Windows.Automation.ControlType]::Edit
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $editCt)
    $edit = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -eq $edit) { throw "HARVEST_MISS: no Edit in palette" }
    $vp = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($text)
    Write-Host "filter '$text'"
    Start-Sleep -Milliseconds 350
}

function Invoke-PaletteCommand([int64]$MainHwnd, [uint32]$ProcId, [string]$filter, [string]$title) {
    Open-Palette $MainHwnd $ProcId
    Set-PaletteFilter $MainHwnd $filter
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($MainHwnd))
    $el = $null
    $dl = (Get-Date).AddMilliseconds(1200)
    while ((Get-Date) -lt $dl -and $null -eq $el) {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($MainHwnd))
        $el = Find-NamedListItem $root $title
        Start-Sleep -Milliseconds 80
    }
    if ($null -eq $el) { throw "HARVEST_MISS: palette ListItem '$title' not under hwnd after filter '$filter'" }
    Invoke-El $el $ProcId $title $MainHwnd
    Start-Sleep -Milliseconds 1200
}

function Close-Extras([int64]$MainHwnd, [uint32]$ProcId) {
    foreach ($w in @(Get-WinUiWindows $ProcId)) {
        if ($w.Hwnd64 -eq $MainHwnd) { continue }
        Write-Host "closing extra '$($w.Title)' hwnd=$($w.Hwnd64)"
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($w.Hwnd64))
        $close = Find-Name $root 'Close'
        if ($null -ne $close) { Invoke-El $close $ProcId "Close $($w.Title)" $MainHwnd }
        else { Write-Host "HARVEST_MISS: no Close on extra window" }
        Start-Sleep -Milliseconds 300
    }
}

function Dismiss-Flyout([int64]$MainHwnd, [uint32]$ProcId) {
    $rc = [MzD]::RectOf($MainHwnd)
    if ($null -eq $rc) { return }
    $hit = [MzD]::ClickScreen($ProcId, $rc.L + 200, $rc.T + 200, $false)
    Write-Host "dismiss flyout ok=$($hit.Ok)"
    Start-Sleep -Milliseconds 300
}

function Open-TabMenu([int64]$MainHwnd, [uint32]$ProcId) {
    $rc = [MzD]::RectOf($MainHwnd)
    $x = $rc.L + 80; $y = $rc.T + 16
    $hit = [MzD]::ClickScreen($ProcId, $x, $y, $true)
    if (-not $hit.Ok) { throw "HARVEST_MISS: tab context $($hit.Why) class=$($hit.HitClass) at $x,$y" }
    Start-Sleep -Milliseconds 400
    return [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($MainHwnd))
}

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

Assert-NoWintty
$script:WinttyStamp = Get-WinttyLaunchStamp
Start-Sleep -Milliseconds 400
$proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path $ExePath)
$pid32 = [uint32]$proc.Id
$main = Wait-Ready $proc
Start-Sleep -Seconds 2
$main = @(Get-WinUiWindows $pid32) | Select-Object -First 1
$hwnd64 = [int64]$main.Hwnd64
Write-Host "hwnd=$hwnd64 pid=$pid32 title=$($main.Title)"
Shot $hwnd64 '00-launch'

$overview = $false
try {
    Invoke-PaletteCommand $hwnd64 $pid32 'show all' 'Show all tabs'
    Start-Sleep -Milliseconds 800
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($hwnd64))
    $overview = $null -ne (Find-Name $root 'All tabs')
    Shot $hwnd64 '00b-overview'
    [MzD]::Key($hwnd64, 0x1B)
    Start-Sleep -Milliseconds 400
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($hwnd64))
    if ($null -ne (Find-Name $root 'All tabs')) {
        $rc = [MzD]::RectOf($hwnd64)
        [void][MzD]::ClickScreen($pid32, $rc.L + 200, $rc.T + 200, $false)
        Start-Sleep -Milliseconds 400
    }
} catch {
    Write-Host "overview: $_"
}

$rename = $false
$tabColor = $false
$moveZone = $false
$colorPicker = $false
$renameDialog = $false
$zoomPane = $false
$paste = $false
$quake = $false
$newTab = $false

$root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($hwnd64))
$plus = Find-Name $root 'New tab'
if ($null -ne $plus) {
    Invoke-El $plus $pid32 'New tab' $hwnd64
    $newTab = $true
    Start-Sleep -Milliseconds 600
    Shot $hwnd64 '01-two-tabs'
} else {
    Write-Host "HARVEST_MISS: New tab button"
}

$root = Open-TabMenu $hwnd64 $pid32
$rename = $null -ne (Find-Name $root 'Rename Tab')
$tabColor = $null -ne (Find-Name $root 'Tab Color...')
$moveZone = $null -ne (Find-Name $root 'Move Tab to Zone')
Write-Host "tabMenu rename=$rename color=$tabColor zone=$moveZone"
Shot $hwnd64 '02-tab-menu'

$el = Find-Name $root 'Rename Tab'
if ($null -ne $el) {
    Invoke-El $el $pid32 'Rename Tab' $hwnd64
    Start-Sleep -Milliseconds 500
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($hwnd64))
    $renameDialog = ($null -ne (Find-Name $root 'Rename tab')) -or ($null -ne (Find-Name $root 'Cancel'))
    Write-Host "renameDialog=$renameDialog"
    Shot $hwnd64 '03-rename'
    $cancel = Find-Name $root 'Cancel'
    if ($null -ne $cancel) { Invoke-El $cancel $pid32 'Cancel rename' $hwnd64 }
    else { [MzD]::Key($hwnd64, 0x1B) }
    Start-Sleep -Milliseconds 400
} else {
    Dismiss-Flyout $hwnd64 $pid32
}

$root = Open-TabMenu $hwnd64 $pid32
$el = Find-Name $root 'Tab Color...'
if ($null -ne $el) {
    Invoke-El $el $pid32 'Tab Color...' $hwnd64
    Start-Sleep -Milliseconds 500
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($hwnd64))
    $colorPicker = ($null -ne (Find-Name $root 'Blue')) -or ($null -ne (Find-Name $root 'None'))
    Write-Host "colorPicker uia=$colorPicker"
    Shot $hwnd64 '04-tab-color'
    Dismiss-Flyout $hwnd64 $pid32
} else {
    Dismiss-Flyout $hwnd64 $pid32
}

$root = Open-TabMenu $hwnd64 $pid32
$el = Find-Name $root 'Move Tab to Zone'
if ($null -ne $el) {
    Invoke-El $el $pid32 'Move Tab to Zone' $hwnd64
    Start-Sleep -Milliseconds 500
    Shot $hwnd64 '05-snap-zone'
    Dismiss-Flyout $hwnd64 $pid32
} else {
    Write-Host "HARVEST_MISS: Move Tab to Zone (need 2 tabs? newTab=$newTab)"
    Dismiss-Flyout $hwnd64 $pid32
}

$rc = [MzD]::RectOf($hwnd64)
$menuHit = [MzD]::ClickScreen($pid32, $rc.L + 400, $rc.T + 280, $true)
if (-not $menuHit.Ok) { throw "HARVEST_MISS: pane menu $($menuHit.Why) class=$($menuHit.HitClass)" }
Start-Sleep -Milliseconds 500
$root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($hwnd64))
$zoomPane = $null -ne (Find-Name $root 'Zoom Pane')
$paste = $null -ne (Find-Name $root 'Paste')
Write-Host "paneMenu zoom=$zoomPane paste=$paste"
$miCt = [System.Windows.Automation.ControlType]::MenuItem
$miCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $miCt)
foreach ($mi in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $miCond)) {
    Write-Host ("  menu {0}" -f $mi.Current.Name)
}
Shot $hwnd64 '06-pane-menu'
$el = Find-Name $root 'Zoom Pane'
if ($null -ne $el) {
    Invoke-El $el $pid32 'Zoom Pane' $hwnd64
    Start-Sleep -Milliseconds 500
    Shot $hwnd64 '07-zoomed'
} else {
    Dismiss-Flyout $hwnd64 $pid32
}

$quakeTried = $false
try {
    Invoke-PaletteCommand $hwnd64 $pid32 'quake' 'Toggle Quake Terminal'
    $quakeTried = $true
} catch {
    Write-Host "palette quake: $_"
    $rc = [MzD]::RectOf($hwnd64)
    $menuHit = [MzD]::ClickScreen($pid32, $rc.L + 400, $rc.T + 280, $true)
    if ($menuHit.Ok) {
        Start-Sleep -Milliseconds 400
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($hwnd64))
        $pal = Find-Name $root 'Command Palette'
        if ($null -ne $pal) {
            Invoke-El $pal $pid32 'Command Palette' $hwnd64
            Set-PaletteFilter $hwnd64 'quake'
            $el = Find-NamedListItem ([System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($hwnd64))) 'Toggle Quake Terminal'
            if ($null -ne $el) {
                Invoke-El $el $pid32 'Toggle Quake Terminal' $hwnd64
                $quakeTried = $true
            }
        }
    }
}
Start-Sleep -Seconds 1
Shot-Pid $pid32 '08-quake'
$extras = @(Get-WinUiWindows $pid32 | Where-Object { $_.Hwnd64 -ne $hwnd64 })
$quake = $extras.Count -gt 0
Write-Host "quake extras=$($extras.Count)"
$extras | ForEach-Object { Write-Host "  $($_.Title) $($_.Hwnd64)" }

$proc.Refresh()
$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
@{
    alive = -not $proc.HasExited
    crashGrew = $crashGrew
    newTab = [bool]$newTab
    overview = [bool]$overview
    renameMenu = [bool]$rename
    tabColorMenu = [bool]$tabColor
    moveZoneMenu = [bool]$moveZone
    renameDialog = [bool]$renameDialog
    colorPicker = [bool]$colorPicker
    zoomPane = [bool]$zoomPane
    paste = [bool]$paste
    quakeTried = [bool]$quakeTried
    quakeExtras = $extras.Count
    quake = [bool]$quake
} | ConvertTo-Json | Set-Content (Join-Path $OutDir 'result.json')
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)
if ($proc.HasExited -or $crashGrew -or -not $overview) {
    Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $ExePath
    exit 2
}
Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $ExePath
exit 0
