#requires -Version 7
# Remaining-tab title after split+close must stay on default-profile (pwsh),
# not pick up cmd.exe from the dying split pane.
# Isolated XDG. No modifier chords. No caption Close.
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
public static class MzRT {
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
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
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
    $cb = [MzRT+EnumProc]{
        param($h,$lp)
        [uint32]$o=0; [void][MzRT]::GetWindowThreadProcessId($h,[ref]$o)
        if ($o -ne $ProcId -or -not [MzRT]::IsWindowVisible($h)) { return $true }
        if ([MzRT]::ClassOf($h) -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [MzRT]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64=$hwnd64; Title=[MzRT]::TitleOf($h); Area=($rc.W*$rc.Hh) })
        return $true
    }
    [void][MzRT]::EnumWindows($cb,[IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending
}

function Splash-Visible([int]$ProcId) {
    $script:splashSeen = $false
    $cb = [MzRT+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner=0; [void][MzRT]::GetWindowThreadProcessId($hwnd,[ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        if ([MzRT]::ClassOf($hwnd) -eq 'WinttySplash' -and [MzRT]::IsWindowVisible($hwnd)) { $script:splashSeen = $true }
        return $true
    }
    [void][MzRT]::EnumWindows($cb,[IntPtr]::Zero)
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
    $rc = [MzRT]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw "HARVEST_MISS: degenerate rect for $name" }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L,$rc.T,0,0,$bmp.Size)
    $p = Join-Path $OutDir "shots\$name.png"
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose()
    Write-Host "shot $name $($rc.W)x$($rc.Hh)"
}

function Find-Name($root, [string]$name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-El($el, [uint32]$ProcId, [string]$what, [int64]$MainHwnd) {
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
    $hit = [MzRT]::ClickScreen($ProcId, $x, $y, $false)
    if (-not $hit.Ok) { throw "HARVEST_MISS: $what click $($hit.Why) class=$($hit.HitClass) at $x,$y" }
    Write-Host "click $what $x,$y"
    Start-Sleep -Milliseconds 400
}

function Count-TabItemsOn([int64]$Hwnd64) {
    $root = $null
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzRT]::P($Hwnd64))
    } catch {
        return 0
    }
    if ($null -eq $root) { return 0 }
    $ct = [System.Windows.Automation.ControlType]::TabItem
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)).Count
}

function Find-DialogCloseButton($root, [int64]$Hwnd64) {
    # Window caption Close is also named "Close". Never invoke that —
    # it kills Wintty while we are trying to confirm the tab dialog.
    $rc = [MzRT]::RectOf($Hwnd64)
    if ($null -eq $rc -or $null -eq $root) { return $null }
    $btnCt = [System.Windows.Automation.ControlType]::Button
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $btnCt)
    foreach ($b in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if ($b.Current.Name -ne 'Close') { continue }
        $r = $b.Current.BoundingRectangle
        if ($r.Y -gt ($rc.T + 40)) { return $b }
    }
    return $null
}

function Find-CloseMenuItem($root) {
    $miCt = [System.Windows.Automation.ControlType]::MenuItem
    $miCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $miCt)
    foreach ($mi in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $miCond)) {
        if ($mi.Current.Name -eq 'Close') { return $mi }
    }
    return $null
}

function Open-TabCloseMenu([int64]$Hwnd64, [uint32]$ProcId) {
    $rc = [MzRT]::RectOf($Hwnd64)
    $hit = [MzRT]::ClickScreen($ProcId, $rc.L + 80, $rc.T + 16, $true)
    if (-not $hit.Ok) { throw "tab menu $($hit.Why)" }
    Start-Sleep -Milliseconds 400
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzRT]::P($Hwnd64))
    $closeTab = Find-CloseMenuItem $root
    if ($null -eq $closeTab) { throw "no Close MenuItem on tab flyout" }
    return $closeTab
}

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$originalXdgSet = Test-Path Env:XDG_CONFIG_HOME
$originalXdg = if ($originalXdgSet) { $env:XDG_CONFIG_HOME } else { $null }
$tempXdg = Join-Path $env:TEMP ("wintty-fuzz-xdg-rt-{0:HHmmss}" -f (Get-Date))
New-Item -ItemType Directory -Force -Path (Join-Path $tempXdg 'wintty') | Out-Null
[IO.File]::WriteAllText((Join-Path $tempXdg 'wintty\config.wintty'), @"
windows-single-instance = true
window-save-state = never
windows-settings-ui = true
confirm-close-surface = false
profile.pwsh.name = PowerShell
profile.pwsh.command = pwsh.exe
default-profile = pwsh
"@)

$proc = $null
$launchTitle = ''
$afterSplitTitle = ''
$remainTitle = ''
$launchOk = $false
$remainOk = $false
$tabsAfterNew = 0
$tabsAfterClose = 0

Assert-NoWintty
$script:WinttyStamp = Get-WinttyLaunchStamp
try {
    $env:XDG_CONFIG_HOME = $tempXdg
    Start-Sleep -Milliseconds 500
    $proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path $ExePath)
    $pid32 = [uint32]$proc.Id
    $main = Wait-Ready $proc
    Start-Sleep -Seconds 1
    $main = @(Get-WinUiWindows $pid32) | Select-Object -First 1
    $hwnd64 = [int64]$main.Hwnd64
    $launchTitle = $main.Title
    $launchOk = $launchTitle -match 'pwsh'
    Write-Host "hwnd=$hwnd64 pid=$pid32 launchTitle=$launchTitle launchOk=$launchOk"
    Shot $hwnd64 '00-launch'

    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzRT]::P($hwnd64))
    $newTab = Find-Name $root 'New tab'
    if ($null -eq $newTab) { throw "HARVEST_MISS: New tab" }
    Invoke-El $newTab $pid32 'New tab' $hwnd64
    Start-Sleep -Milliseconds 600
    $tabsAfterNew = Count-TabItemsOn $hwnd64
    if ($tabsAfterNew -lt 2) { throw "PRODUCT_FAIL: expected 2 tabs, got $tabsAfterNew" }
    Shot $hwnd64 '01-two-tabs'

    $rc = [MzRT]::RectOf($hwnd64)
    $hit = [MzRT]::ClickScreen($pid32, $rc.L + 400, $rc.T + 280, $true)
    if (-not $hit.Ok) { throw "pane menu $($hit.Why)" }
    Start-Sleep -Milliseconds 400
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzRT]::P($hwnd64))
    $split = Find-Name $root 'Split Right'
    if ($null -eq $split) { throw "HARVEST_MISS: Split Right" }
    Invoke-El $split $pid32 'Split Right' $hwnd64
    Start-Sleep -Milliseconds 800
    $afterSplitTitle = [MzRT]::TitleOf([MzRT]::P($hwnd64))
    Write-Host "afterSplitTitle=$afterSplitTitle"
    Shot $hwnd64 '02-split'

    $closeTab = Open-TabCloseMenu $hwnd64 $pid32
    Invoke-El $closeTab $pid32 'Close tab menu' $hwnd64
    Start-Sleep -Milliseconds 800
    $tabsAfterClose = Count-TabItemsOn $hwnd64
    $remainTitle = [MzRT]::TitleOf([MzRT]::P($hwnd64))
    $remainOk = ($tabsAfterClose -eq 1) -and ($remainTitle -match 'pwsh') -and ($remainTitle -notmatch 'cmd\.exe')
    Write-Host "remainTitle=$remainTitle tabs=$tabsAfterClose remainOk=$remainOk"
    Shot $hwnd64 '03-remain'
}
finally {
    if ($null -ne $proc) {
        $proc.Refresh()
        if (-not $proc.HasExited) { try { $proc.Kill($true); [void]$proc.WaitForExit(3000) } catch { } }
    }
    Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $ExePath
    if ($originalXdgSet) { $env:XDG_CONFIG_HOME = $originalXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
}

$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
$result = @{
    crashGrew = $crashGrew
    launchTitle = $launchTitle
    afterSplitTitle = $afterSplitTitle
    remainTitle = $remainTitle
    launchOk = $launchOk
    remainOk = $remainOk
    tabsAfterNew = $tabsAfterNew
    tabsAfterClose = $tabsAfterClose
}
$result | ConvertTo-Json | Set-Content (Join-Path $OutDir 'result.json')
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)
if ($crashGrew -or -not $launchOk -or -not $remainOk) { exit 2 }
exit 0
