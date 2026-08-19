#requires -Version 7
# Isolated XDG: vertical-tabs pinned+width via NavigationView pane. Mouse/UIA only.
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
public static class MzVT {
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT {
        public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo;
    }
    const uint INPUT_MOUSE = 0;
    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
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
    public static Hit ProbeScreen(uint pid, int x, int y) {
        var hit = WindowFromPoint(new POINT { X=x, Y=y });
        uint hitPid = PidOf(hit); string cls = ClassOf(hit);
        if (cls == "WinttySplash") return Miss("splash", x, y, hitPid, cls);
        if (hitPid != pid) return Miss("not Wintty", x, y, hitPid, cls);
        return new Hit { Ok=true, X=x, Y=y, HitPid=hitPid, HitClass=cls };
    }
    public static Hit HoverScreen(uint pid, long hwnd, int x, int y) {
        var p = ProbeScreen(pid, x, y);
        if (!p.Ok) return p;
        SetForegroundWindow(P(hwnd));
        if (!SetCursorPos(x, y)) return Miss("SetCursorPos", x, y, p.HitPid, p.HitClass);
        // Real pointer via SendInput so WinUI PointerEntered fires (mouse_event/PostMessage do not).
        int sw = Math.Max(1, GetSystemMetrics(0));
        int sh = Math.Max(1, GetSystemMetrics(1));
        int ax = (int)(x * 65535.0 / Math.Max(1, sw - 1));
        int ay = (int)(y * 65535.0 / Math.Max(1, sh - 1));
        var inp = new INPUT[] {
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT {
                dx = ax, dy = ay, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE } }
        };
        SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(700);
        return ProbeScreen(pid, x, y);
    }
    public static Hit ClickScreen(uint pid, int x, int y) {
        var p = ProbeScreen(pid, x, y);
        if (!p.Ok) return p;
        if (!SetCursorPos(x, y)) return Miss("SetCursorPos", x, y, p.HitPid, p.HitClass);
        Thread.Sleep(40);
        p = ProbeScreen(pid, x, y);
        if (!p.Ok) return p;
        mouse_event(MOUSEEVENTF_LEFTDOWN,0,0,0,UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP,0,0,0,UIntPtr.Zero);
        Thread.Sleep(250);
        return p;
    }
}
'@

function Get-WinUiWindows([uint32]$ProcId) {
    $hits = [System.Collections.Generic.List[object]]::new()
    $cb = [MzVT+EnumProc]{
        param($h,$lp)
        [uint32]$o=0; [void][MzVT]::GetWindowThreadProcessId($h,[ref]$o)
        if ($o -ne $ProcId -or -not [MzVT]::IsWindowVisible($h)) { return $true }
        if ([MzVT]::ClassOf($h) -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [MzVT]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64=$hwnd64; Title=[MzVT]::TitleOf($h); Area=($rc.W*$rc.Hh) })
        return $true
    }
    [void][MzVT]::EnumWindows($cb,[IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending
}

function Splash-Visible([int]$ProcId) {
    $script:splashSeen = $false
    $cb = [MzVT+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner=0; [void][MzVT]::GetWindowThreadProcessId($hwnd,[ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        if ([MzVT]::ClassOf($hwnd) -eq 'WinttySplash' -and [MzVT]::IsWindowVisible($hwnd)) { $script:splashSeen = $true }
        return $true
    }
    [void][MzVT]::EnumWindows($cb,[IntPtr]::Zero)
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
    $rc = [MzVT]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw "HARVEST_MISS: degenerate rect for $name" }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L,$rc.T,0,0,$bmp.Size)
    $p = Join-Path $OutDir "shots\$name.png"
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose()
    Write-Host "shot $name $($rc.W)x$($rc.Hh)"
}

function Get-UiaRoot([int64]$Hwnd64) {
    return [System.Windows.Automation.AutomationElement]::FromHandle([MzVT]::P($Hwnd64))
}

function Find-Name($root, [string]$name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-NavPane($root) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NavView')
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-NavPaneWidth($root) {
    $nav = Find-NavPane $root
    if ($null -eq $nav) { return 0 }
    return [int]$nav.Current.BoundingRectangle.Width
}

function Find-PaneToggle($root) {
    $idCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'PaneToggleButton')
    $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
    if ($null -ne $el) { return $el }
    foreach ($n in @(
            'Expand sidebar', 'Collapse sidebar', 'Toggle sidebar',
            'Toggle navigation pane', 'Close navigation pane', 'Open navigation pane')) {
        $el = Find-Name $root $n
        if ($null -ne $el) { return $el }
    }
    return $null
}

function Invoke-El($el, [uint32]$ProcId, [string]$what) {
    if ($null -eq $el) { throw "HARVEST_MISS: no UIA element for $what" }
    try {
        $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pat.Invoke()
        Write-Host "invoke $what"
        Start-Sleep -Milliseconds 500
        return
    } catch { Write-Host "invoke $what unsupported, clicking bounds" }
    $r = $el.Current.BoundingRectangle
    $x = [int]($r.X + $r.Width/2); $y = [int]($r.Y + $r.Height/2)
    $hit = [MzVT]::ClickScreen($ProcId, $x, $y)
    if (-not $hit.Ok) { throw "HARVEST_MISS: $what click $($hit.Why) class=$($hit.HitClass)" }
    Write-Host "click $what $x,$y"
    Start-Sleep -Milliseconds 500
}

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$originalXdgSet = Test-Path Env:XDG_CONFIG_HOME
$originalXdg = if ($originalXdgSet) { $env:XDG_CONFIG_HOME } else { $null }
$tempXdg = Join-Path $env:TEMP ("wintty-fuzz-xdg-vt-{0:HHmmss}" -f (Get-Date))
New-Item -ItemType Directory -Force -Path (Join-Path $tempXdg 'wintty') | Out-Null
[IO.File]::WriteAllText((Join-Path $tempXdg 'wintty\config.wintty'), @"
windows-single-instance = true
window-save-state = never
windows-settings-ui = true
vertical-tabs = true
window-theme = wintty
theme = Catppuccin Mocha
# Pin/width are session state (PaneToggleButton), not config keys yet.
vertical-tabs-hover-expand = false
"@)

$proc = $null
$pinnedWide = $false
$collapsedNarrow = $false
$widthPinned = 0
$widthCollapsed = 0

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
    Write-Host "hwnd=$hwnd64 pid=$pid32 title=$($main.Title)"
    Shot $hwnd64 '00-launch-collapsed'

    $root = Get-UiaRoot $hwnd64
    $widthCollapsed = Get-NavPaneWidth $root
    $collapsedNarrow = $widthCollapsed -gt 0 -and $widthCollapsed -lt 90
    Write-Host "widthCollapsed=$widthCollapsed collapsedNarrow=$collapsedNarrow"
    if (-not $collapsedNarrow) { throw "PRODUCT_FAIL: launch strip width $widthCollapsed (want <90)" }

    $toggle = Find-PaneToggle $root
    if ($null -eq $toggle) { throw "HARVEST_MISS: pane toggle (Expand sidebar)" }
    Invoke-El $toggle $pid32 'Expand sidebar'
    Start-Sleep -Milliseconds 500
    $root = Get-UiaRoot $hwnd64
    $widthPinned = Get-NavPaneWidth $root
    $pinnedWide = $widthPinned -ge 200
    Write-Host "widthPinned=$widthPinned pinnedWide=$pinnedWide"
    Shot $hwnd64 '01-expanded'
    if (-not $pinnedWide) { throw "PRODUCT_FAIL: expanded strip width $widthPinned (want >=200)" }

    $toggle = Find-PaneToggle $root
    if ($null -eq $toggle) { throw "HARVEST_MISS: pane toggle (Collapse sidebar)" }
    Invoke-El $toggle $pid32 'Collapse sidebar'
    Start-Sleep -Milliseconds 400
    $root = Get-UiaRoot $hwnd64
    $widthCollapsed = Get-NavPaneWidth $root
    $collapsedNarrow = $widthCollapsed -gt 0 -and $widthCollapsed -lt 90
    Write-Host "widthCollapsed=$widthCollapsed collapsedNarrow=$collapsedNarrow"
    Shot $hwnd64 '02-collapsed'
    if (-not $collapsedNarrow) { throw "PRODUCT_FAIL: collapsed strip width $widthCollapsed (want <90)" }

    $toggle = Find-PaneToggle $root
    if ($null -eq $toggle) { throw "HARVEST_MISS: pane toggle (Expand sidebar again)" }
    Invoke-El $toggle $pid32 'Expand sidebar'
    Start-Sleep -Milliseconds 400
    $root = Get-UiaRoot $hwnd64
    $widthReopen = Get-NavPaneWidth $root
    Write-Host "widthReopen=$widthReopen"
    Shot $hwnd64 '03-reopened'
}
finally {
    if ($null -ne $proc) {
        $proc.Refresh()
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    }
    Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $ExePath
    if ($originalXdgSet) { $env:XDG_CONFIG_HOME = $originalXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
}

$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
$result = @{
    crashGrew = $crashGrew
    pinnedWide = $pinnedWide
    collapsedNarrow = $collapsedNarrow
    widthPinned = $widthPinned
    widthCollapsed = $widthCollapsed
}
$result | ConvertTo-Json | Set-Content (Join-Path $OutDir 'result.json')
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)
if ($crashGrew -or -not $pinnedWide -or -not $collapsedNarrow) { exit 2 }
exit 0
