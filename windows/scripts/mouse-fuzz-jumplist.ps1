#requires -Version 7
# Jump-list CLI under windows-single-instance. Isolated XDG.
# No desktop-root walk. No modifier chords.
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
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

function Count-TabItemsOn([int64]$Hwnd64) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($Hwnd64))
    if ($null -eq $root) { return 0 }
    $ct = [System.Windows.Automation.ControlType]::TabItem
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)).Count
}

function Count-TabItems([uint32]$ProcId) {
    $n = 0
    foreach ($w in @(Get-WinUiWindows $ProcId)) {
        $n += Count-TabItemsOn $w.Hwnd64
    }
    return $n
}

function New-IsolatedConfig {
    $tempXdg = Join-Path $env:TEMP ("wintty-fuzz-xdg-jl-{0:HHmmss}" -f (Get-Date))
    $winttyDir = Join-Path $tempXdg 'wintty'
    New-Item -ItemType Directory -Force -Path $winttyDir | Out-Null
    $dst = Join-Path $winttyDir 'config.wintty'
    $body = @"
windows-single-instance = true
window-save-state = never
windows-settings-ui = true
confirm-close-surface = false
profile.pwsh.name = PowerShell
profile.pwsh.command = pwsh.exe
default-profile = pwsh
"@
    [IO.File]::WriteAllText($dst, $body)
    Write-Host "XDG_CONFIG_HOME=$tempXdg"
    return $tempXdg
}

function Invoke-Secondary([string]$Cli) {
    # Do not name this $Args: that is the automatic leftover-args
    # variable and silently swallows the parameter (every secondary
    # then launches with no jumplist flags and becomes NewWindow).
    Write-Host "secondary $Cli"
    $p = Start-Process -FilePath $ExePath -ArgumentList $Cli -PassThru -WorkingDirectory (Split-Path $ExePath)
    $dl = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $dl) {
        $p.Refresh()
        if ($p.HasExited) {
            Write-Host "secondary exited code=$($p.ExitCode) pid=$($p.Id)"
            return $p
        }
        Start-Sleep -Milliseconds 200
    }
    Write-Host "HARVEST_MISS: secondary still alive pid=$($p.Id)"
    return $p
}

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$originalXdgSet = Test-Path Env:XDG_CONFIG_HOME
$originalXdg = if ($originalXdgSet) { $env:XDG_CONFIG_HOME } else { $null }
$tempXdg = New-IsolatedConfig
$proc = $null
$secondaryAlive = $false
$newWindowOk = $false
$newTabOk = $false
$profileOk = $false
$windowsBefore = 0
$windowsAfterTab = 0
$windowsAfterNew = 0
$tabsBefore = 0
$tabsAfter = 0
$tabsAfterProfile = 0
$defaultProfileOk = $false
$confirmSkipped = $false
$confirmProbed = $false
$tabsBefore = 0
$tabsAfter = 0
$tabsAfterProfile = 0

Assert-NoWintty
$script:WinttyStamp = Get-WinttyLaunchStamp
try {
    $env:XDG_CONFIG_HOME = $tempXdg
    Start-Sleep -Milliseconds 500
    $proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path $ExePath)
    $pid32 = [uint32]$proc.Id
    $main = Wait-Ready $proc
    Start-Sleep -Seconds 2
    $main = @(Get-WinUiWindows $pid32) | Select-Object -First 1
    $hwnd64 = [int64]$main.Hwnd64
    Write-Host "hwnd=$hwnd64 pid=$pid32 title=$($main.Title)"
    Shot $hwnd64 '00-launch'
    $defaultProfileOk = $main.Title -match 'pwsh'
    Write-Host "defaultProfileOk=$defaultProfileOk"

    $windowsBefore = @(Get-WinUiWindows $pid32).Count
    $tabsBefore = Count-TabItemsOn $hwnd64
    Write-Host "before windows=$windowsBefore tabsOnHwnd0=$tabsBefore"

    $sec2 = Invoke-Secondary '--jumplist-action=new-tab'
    Start-Sleep -Seconds 2
    $proc.Refresh()
    if (-not $sec2.HasExited) { $secondaryAlive = $true }
    $windowsAfterTab = @(Get-WinUiWindows $pid32).Count
    $tabsAfter = Count-TabItemsOn $hwnd64
    $newTabOk = ($tabsAfter -gt $tabsBefore) -and ($windowsAfterTab -eq $windowsBefore)
    Write-Host "after new-tab tabsOnHwnd0=$tabsAfter (was $tabsBefore) windows=$windowsAfterTab newTabOk=$newTabOk"
    Shot $hwnd64 '01-new-tab'

    $confirmSkipped = $false
    try {
        $rc = [MzD]::RectOf($hwnd64)
        $menuHit = [MzD]::ClickScreen($pid32, $rc.L + 400, $rc.T + 280, $true)
        if (-not $menuHit.Ok) { throw "pane menu $($menuHit.Why)" }
        Start-Sleep -Milliseconds 400
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($hwnd64))
        $split = Find-Name $root 'Split Right'
        if ($null -eq $split) { throw "no Split Right menu" }
        Invoke-El $split $pid32 'Split Right' $hwnd64
        Start-Sleep -Milliseconds 500
        $rc = [MzD]::RectOf($hwnd64)
        $tabHit = [MzD]::ClickScreen($pid32, $rc.L + 80, $rc.T + 16, $true)
        if (-not $tabHit.Ok) { throw "tab menu $($tabHit.Why)" }
        Start-Sleep -Milliseconds 400
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($hwnd64))
        $closeTab = $null
        $miCt = [System.Windows.Automation.ControlType]::MenuItem
        $miCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $miCt)
        foreach ($mi in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $miCond)) {
            if ($mi.Current.Name -eq 'Close') { $closeTab = $mi; break }
        }
        if ($null -eq $closeTab) { throw "no Close MenuItem on tab flyout" }
        $confirmProbed = $true
        Invoke-El $closeTab $pid32 'Close tab menu' $hwnd64
        Start-Sleep -Milliseconds 500
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzD]::P($hwnd64))
        $dlg = Find-Name $root 'Close tab?'
        $tabsAfterClose = Count-TabItemsOn $hwnd64
        $confirmSkipped = ($null -eq $dlg) -and ($tabsAfterClose -lt $tabsAfter)
        Write-Host "confirm-close-surface=false dialog=$($null -ne $dlg) tabs=$tabsAfterClose confirmSkipped=$confirmSkipped"
        Shot $hwnd64 '01b-close-split-tab'
    } catch {
        Write-Host "confirm-close probe: $_"
    }

    $sec = Invoke-Secondary '--jumplist-action=new-window'
    Start-Sleep -Seconds 2
    $proc.Refresh()
    if (-not $sec.HasExited) { $secondaryAlive = $true }
    $windowsAfterNew = @(Get-WinUiWindows $pid32).Count
    $newWindowOk = (-not $secondaryAlive) -and ($windowsAfterNew -gt $windowsBefore)
    Write-Host "after new-window windows=$windowsAfterNew newWindowOk=$newWindowOk"
    Shot-Pid $pid32 '02-new-window'

    $sec3 = Invoke-Secondary '--jumplist-profile=pwsh'
    Start-Sleep -Seconds 2
    $proc.Refresh()
    if (-not $sec3.HasExited) { $secondaryAlive = $true }
    $windowsAfterProfile = @(Get-WinUiWindows $pid32).Count
    $tabsAfterProfile = Count-TabItems $pid32
    $profileOk = $windowsAfterProfile -gt $windowsAfterNew
    Write-Host "after profile windows=$windowsAfterProfile tabs=$tabsAfterProfile profileOk=$profileOk"
    Shot-Pid $pid32 '03-profile'
}
finally {
    if ($null -ne $proc) {
        $proc.Refresh()
        if (-not $proc.HasExited) {
            try { $proc.Kill($true); [void]$proc.WaitForExit(3000) } catch { }
        }
    }
    if ($originalXdgSet) { $env:XDG_CONFIG_HOME = $originalXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
    # After the env restores, not before: a throw in the sweep would otherwise
    # abandon them and leave the shell pointed at a temp profile.
    Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $ExePath
}

$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
$result = @{
    crashGrew = $crashGrew
    secondaryAlive = $secondaryAlive
    windowsBefore = $windowsBefore
    windowsAfterTab = $windowsAfterTab
    windowsAfterNew = $windowsAfterNew
    newWindowOk = $newWindowOk
    tabsBefore = $tabsBefore
    tabsAfter = $tabsAfter
    newTabOk = $newTabOk
    defaultProfileOk = $defaultProfileOk
    confirmProbed = $confirmProbed
    confirmSkipped = $confirmSkipped
    tabsAfterProfile = $tabsAfterProfile
    profileOk = $profileOk
}
$result | ConvertTo-Json | Set-Content (Join-Path $OutDir 'result.json')
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)
if ($crashGrew -or $secondaryAlive -or -not $newWindowOk -or -not $newTabOk -or -not $profileOk -or -not $defaultProfileOk -or ($confirmProbed -and -not $confirmSkipped)) { exit 2 }
exit 0
