#requires -Version 7
# Live-fuzz Wintty inspector: open/close, present, mouse, resize, tab-dismiss.
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
public static class MzI {
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    public static void FocusForInput(long hwnd) {
        var h = P(hwnd);
        var fg0 = GetForegroundWindow();
        uint fgPid0 = 0;
        var fgTid = GetWindowThreadProcessId(fg0, out fgPid0);
        var selfTid = GetCurrentThreadId();
        if (fgTid != 0 && fgTid != selfTid) AttachThreadInput(selfTid, fgTid, true);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        if (fgTid != 0 && fgTid != selfTid) AttachThreadInput(selfTid, fgTid, false);
        Thread.Sleep(250);
    }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
    public static void CloseWindow(long hwnd) {
        PostMessage(P(hwnd), 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
        Thread.Sleep(400);
    }
    public static void Chars(long hwnd, string text) {
        var h = P(hwnd);
        foreach (var ch in text) {
            PostMessage(h, 0x0102, (IntPtr)(ushort)ch, IntPtr.Zero);
            Thread.Sleep(20);
        }
    }
    public static void Key(long hwnd, int vk) {
        var h = P(hwnd);
        PostMessage(h, 0x0100, (IntPtr)vk, IntPtr.Zero);
        Thread.Sleep(40);
        PostMessage(h, 0x0101, (IntPtr)vk, IntPtr.Zero);
    }
    public static void Wheel(int delta) {
        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)delta, UIntPtr.Zero);
        Thread.Sleep(200);
    }
    public static void CtrlWheel(int delta) {
        keybd_event(0x11, 0, 0, UIntPtr.Zero); // VK_CONTROL down
        Thread.Sleep(30);
        Wheel(delta);
        keybd_event(0x11, 0, 2, UIntPtr.Zero); // KEYEVENTF_KEYUP
    }
    public static void CtrlKey(long hwnd, int vk) {
        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        Key(hwnd, vk);
        Thread.Sleep(30);
        keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
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
    const uint GA_ROOT = 2;
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    static bool OwnedByPid(uint pid, IntPtr h) {
        if (h == IntPtr.Zero) return false;
        if (PidOf(h) == pid) return true;
        var root = GetAncestor(h, GA_ROOT);
        return root != IntPtr.Zero && PidOf(root) == pid;
    }
    static Hit Miss(string why, int x, int y, uint pid, string cls) {
        return new Hit { Ok=false, Why=why, X=x, Y=y, HitPid=pid, HitClass=cls };
    }
    public static Hit ClickScreen(uint pid, int x, int y, bool right = false) {
        var hit = WindowFromPoint(new POINT { X=x, Y=y });
        uint hitPid = PidOf(hit); string cls = ClassOf(hit);
        if (cls == "WinttySplash") return Miss("splash", x, y, hitPid, cls);
        if (!OwnedByPid(pid, hit)) return Miss("not Wintty", x, y, hitPid, cls);
        if (!SetCursorPos(x, y)) return Miss("SetCursorPos", x, y, hitPid, cls);
        Thread.Sleep(40);
        hit = WindowFromPoint(new POINT { X=x, Y=y });
        hitPid = PidOf(hit); cls = ClassOf(hit);
        if (!OwnedByPid(pid, hit)) return Miss("not Wintty after move", x, y, hitPid, cls);
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
    $cb = [MzI+EnumProc]{
        param($h,$lp)
        [uint32]$o=0; [void][MzI]::GetWindowThreadProcessId($h,[ref]$o)
        if ($o -ne $ProcId -or -not [MzI]::IsWindowVisible($h)) { return $true }
        if ([MzI]::ClassOf($h) -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [MzI]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64=$hwnd64; Title=[MzI]::TitleOf($h); Area=($rc.W*$rc.Hh) })
        return $true
    }
    [void][MzI]::EnumWindows($cb,[IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending
}

function Get-InspectorWindow([uint32]$ProcId, [int64]$MainHwnd) {
    return @(Get-WinUiWindows $ProcId | Where-Object {
        $_.Hwnd64 -ne $MainHwnd -and $_.Title -match 'Inspector'
    }) | Select-Object -First 1
}

function Splash-Visible([int]$ProcId) {
    $script:splashSeen = $false
    $cb = [MzI+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner=0; [void][MzI]::GetWindowThreadProcessId($hwnd,[ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        if ([MzI]::ClassOf($hwnd) -eq 'WinttySplash' -and [MzI]::IsWindowVisible($hwnd)) { $script:splashSeen = $true }
        return $true
    }
    [void][MzI]::EnumWindows($cb,[IntPtr]::Zero)
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
    $rc = [MzI]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw "HARVEST_MISS: degenerate rect for $name" }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L,$rc.T,0,0,$bmp.Size)
    $p = Join-Path $OutDir "shots\$name.png"
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose()
    Write-Host "shot $name $($rc.W)x$($rc.Hh)"
    return $p
}

function Get-ShotStats([string]$path) {
    $bmp = [System.Drawing.Bitmap]::FromFile($path)
    $seen = @{}
    $nonDark = 0
    $samples = 0
    $stepX = [Math]::Max(1, [int]($bmp.Width / 36))
    $stepY = [Math]::Max(1, [int]($bmp.Height / 36))
    for ($y = 0; $y -lt $bmp.Height; $y += $stepY) {
        for ($x = 0; $x -lt $bmp.Width; $x += $stepX) {
            $c = $bmp.GetPixel($x, $y)
            $k = "$($c.R),$($c.G),$($c.B)"
            if (-not $seen.ContainsKey($k)) { $seen[$k] = 1 }
            if ($c.R -gt 35 -or $c.G -gt 35 -or $c.B -gt 35) { $nonDark++ }
            $samples++
        }
    }
    $bmp.Dispose()
    return @{ unique = $seen.Count; nonDark = $nonDark; samples = $samples }
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
    $hit = [MzI]::ClickScreen($ProcId, $x, $y)
    if (-not $hit.Ok) { throw "HARVEST_MISS: $what click $($hit.Why)" }
    Write-Host "click $what $x,$y"
    Start-Sleep -Milliseconds 400
}

function Open-Palette([int64]$MainHwnd, [uint32]$ProcId) {
    [MzI]::FocusForInput($MainHwnd)
    $rc = [MzI]::RectOf($MainHwnd)
    # Focus main terminal before opening palette (inspector may have focus).
    [void][MzI]::ClickScreen($ProcId, $rc.L + 200, $rc.T + 200, $false)
    Start-Sleep -Milliseconds 300
    $hit = [MzI]::ClickScreen($ProcId, $rc.L + 400, $rc.T + 280, $true)
    if (-not $hit.Ok) { throw "HARVEST_MISS: grid context $($hit.Why)" }
    Start-Sleep -Milliseconds 400
    $pal = $null
    $dl = (Get-Date).AddMilliseconds(1500)
    while ((Get-Date) -lt $dl -and $null -eq $pal) {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzI]::P($MainHwnd))
        $pal = Find-Name $root 'Command Palette'
        Start-Sleep -Milliseconds 80
    }
    if ($null -eq $pal) {
        [void][MzI]::ClickScreen($ProcId, $rc.L + 200, $rc.T + 200, $false)
        Start-Sleep -Milliseconds 300
        $hit = [MzI]::ClickScreen($ProcId, $rc.L + 400, $rc.T + 280, $true)
        if (-not $hit.Ok) { throw "HARVEST_MISS: grid context retry $($hit.Why)" }
        Start-Sleep -Milliseconds 400
        $dl = (Get-Date).AddMilliseconds(1500)
        while ((Get-Date) -lt $dl -and $null -eq $pal) {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzI]::P($MainHwnd))
            $pal = Find-Name $root 'Command Palette'
            Start-Sleep -Milliseconds 80
        }
    }
    if ($null -eq $pal) { throw "HARVEST_MISS: Command Palette" }
    Invoke-El $pal $ProcId 'Command Palette' $MainHwnd
}

function Set-PaletteFilter([int64]$MainHwnd, [string]$text) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzI]::P($MainHwnd))
    $editCt = [System.Windows.Automation.ControlType]::Edit
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $editCt)
    $edit = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -eq $edit) { throw "HARVEST_MISS: palette Edit" }
    $vp = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($text)
    Start-Sleep -Milliseconds 350
}

function Invoke-PaletteCommand([int64]$MainHwnd, [uint32]$ProcId, [string]$filter, [string]$title) {
    Open-Palette $MainHwnd $ProcId
    Set-PaletteFilter $MainHwnd $filter
    $el = $null
    $dl = (Get-Date).AddMilliseconds(1500)
    while ((Get-Date) -lt $dl -and $null -eq $el) {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzI]::P($MainHwnd))
        $el = Find-NamedListItem $root $title
        Start-Sleep -Milliseconds 80
    }
    if ($null -eq $el) { throw "HARVEST_MISS: palette '$title'" }
    Invoke-El $el $ProcId $title $MainHwnd
    Start-Sleep -Milliseconds 1200
}

function Close-InspectorWindow([int64]$InspHwnd, [uint32]$ProcId) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzI]::P($InspHwnd))
    $close = Find-Name $root 'Close'
    if ($null -ne $close) {
        Invoke-El $close $ProcId 'Inspector Close' $InspHwnd
        Start-Sleep -Milliseconds 600
        return
    }
    # WinUI titlebar Close often isn't named in UIA; WM_CLOSE is fine.
    [MzI]::CloseWindow($InspHwnd)
}

function Focus-Main([int64]$MainHwnd, [uint32]$ProcId) {
    $rc = [MzI]::RectOf($MainHwnd)
    [void][MzI]::ClickScreen($ProcId, $rc.L + 40, $rc.T + 40, $false)
    Start-Sleep -Milliseconds 400
}

function Toggle-Inspector([int64]$MainHwnd, [uint32]$ProcId) {
    Focus-Main $MainHwnd $ProcId
    Invoke-PaletteCommand $MainHwnd $ProcId 'inspector' 'Toggle Inspector'
}

function Inspector-NoticeVisible([int64]$MainHwnd) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzI]::P($MainHwnd))
    return $null -ne (Find-Name $root 'Inspector unavailable')
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
Write-Host "main hwnd=$hwnd64 pid=$pid32"
[MzI]::FocusForInput($hwnd64)

# Seed terminal output so inspector has surface state.
[MzI]::Chars($hwnd64, 'echo INSPECTOR-FUZZ')
Start-Sleep -Milliseconds 200
[MzI]::Key($hwnd64, 0x0D)
Start-Sleep -Milliseconds 800

Toggle-Inspector $hwnd64 $pid32
Start-Sleep -Seconds 2

$insp = Get-InspectorWindow $pid32 $hwnd64
if ($null -eq $insp) { throw "PRODUCT_FAIL: no Inspector window after toggle open" }
Write-Host "inspector hwnd=$($insp.Hwnd64) title=$($insp.Title)"

$noticeOpen = Inspector-NoticeVisible $hwnd64
$p1 = Shot $insp.Hwnd64 '01-open'
$stats1 = Get-ShotStats $p1
Write-Host "open stats unique=$($stats1.unique) nonDark=$($stats1.nonDark)"

# Mouse: click center + scroll wheel in inspector panel area.
$rc = [MzI]::RectOf($insp.Hwnd64)
$cx = [int]($rc.L + $rc.W * 0.5)
$cy = [int]($rc.T + $rc.Hh * 0.55)
$hit = [MzI]::ClickScreen($pid32, $cx, $cy)
if (-not $hit.Ok) { throw "HARVEST_MISS: inspector click $($hit.Why)" }
[MzI]::Wheel(-120)
[MzI]::Wheel(-120)
Start-Sleep -Seconds 1
$p2 = Shot $insp.Hwnd64 '02-after-mouse'
$stats2 = Get-ShotStats $p2

# Ctrl+wheel zoom in, then reset with Ctrl+0 via keyboard.
$hit2 = [MzI]::ClickScreen($pid32, $cx, $cy)
if (-not $hit2.Ok) { throw "HARVEST_MISS: inspector refocus $($hit2.Why)" }
[MzI]::CtrlWheel(120)
[MzI]::CtrlWheel(120)
Start-Sleep -Milliseconds 800
$pZoom = Shot $insp.Hwnd64 '02b-after-ctrl-wheel'
$statsZoom = Get-ShotStats $pZoom
$zoomChanged = ($statsZoom.unique -ne $stats2.unique) -or ([Math]::Abs($statsZoom.nonDark - $stats2.nonDark) -ge 15)
Write-Host "zoomChanged=$zoomChanged zoomUnique=$($statsZoom.unique) baseUnique=$($stats2.unique)"
# Ctrl+0 reset via PostMessage (no bare keybd_event in PS scope).
[MzI]::CtrlKey($insp.Hwnd64, 0x30)
Start-Sleep -Milliseconds 400

# Resize inspector (exercises surface_resize + present).
$nw = [Math]::Max(640, [int]($rc.W * 0.75))
$nh = [Math]::Max(480, [int]($rc.Hh * 0.8))
[void][MzI]::MoveWindow([MzI]::P($insp.Hwnd64), $rc.L, $rc.T, $nw, $nh, $true)
Start-Sleep -Seconds 2
$p3 = Shot $insp.Hwnd64 '03-after-resize'
$stats3 = Get-ShotStats $p3
$rc2 = [MzI]::RectOf($insp.Hwnd64)
Write-Host "resize $($rc.W)x$($rc.Hh) -> $($rc2.W)x$($rc2.Hh)"

# Close via titlebar (palette toggle needs main focus; inspector steals it).
Close-InspectorWindow $insp.Hwnd64 $pid32
Start-Sleep -Milliseconds 800
$inspAfterClose = Get-InspectorWindow $pid32 $hwnd64
$closedOk = $null -eq $inspAfterClose
Write-Host "toggleClose gone=$closedOk"

# Re-open.
Toggle-Inspector $hwnd64 $pid32
Start-Sleep -Seconds 2
$insp2 = Get-InspectorWindow $pid32 $hwnd64
if ($null -eq $insp2) { throw "PRODUCT_FAIL: re-open failed" }
$p4 = Shot $insp2.Hwnd64 '04-reopen'
$stats4 = Get-ShotStats $p4

function Get-TabItemCount([int64]$MainHwnd) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzI]::P($MainHwnd))
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond).Count
}

function Wait-InspectorGone([uint32]$ProcId, [int64]$MainHwnd, [int]$seconds) {
    $dl = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $dl) {
        if ($null -eq (Get-InspectorWindow $ProcId $MainHwnd)) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

function New-TabViaStrip([int64]$MainHwnd, [uint32]$ProcId) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzI]::P($MainHwnd))
    $btn = Find-Name $root 'New tab'
    if ($null -eq $btn) { $btn = Find-Name $root 'New Tab' }
    if ($null -eq $btn) { throw 'HARVEST_MISS: New tab strip button' }
    Invoke-El $btn $ProcId 'New tab' $MainHwnd
    Start-Sleep -Milliseconds 800
}

# Tab change should auto-close inspector (MainWindow wiring).
New-TabViaStrip $hwnd64 $pid32
$tabCount = Get-TabItemCount $hwnd64
if ($tabCount -lt 2) {
    Start-Sleep -Milliseconds 600
    New-TabViaStrip $hwnd64 $pid32
    $tabCount = Get-TabItemCount $hwnd64
}
$tabDismissOk = Wait-InspectorGone $pid32 $hwnd64 4
Write-Host "tabCount=$tabCount tabDismiss gone=$tabDismissOk"

$proc.Refresh()
$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
$alive = -not $proc.HasExited
$renderOk = ($stats1.unique -ge 4) -and ($stats1.nonDark -ge 3)

$result = @{
    alive = $alive
    crashGrew = $crashGrew
    inspectorOpened = $true
    inspectorNotice = $noticeOpen
    renderOk = $renderOk
    statsOpen = $stats1
    statsMouse = $stats2
    statsZoom = $statsZoom
    ctrlWheelZoomOk = $zoomChanged
    statsResize = $stats3
    statsReopen = $stats4
    titlebarCloseOk = $closedOk
    tabCount = $tabCount
    tabDismissOk = $tabDismissOk
    resizeW = $rc2.W
    resizeH = $rc2.Hh
}
$result | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json')
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)

Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $ExePath

# All of these are defects in the build under test, so they are 2, not 1.
# They used to exit 1, which under the suite convention means "the harness
# could not run" -- a code the runners retry and then report as unknown
# coverage. A real inspector regression was therefore retried and, if it
# passed the second time, buried.
if (-not $alive -or $crashGrew) { exit 2 }
if ($noticeOpen -or -not $renderOk -or -not $closedOk) { exit 2 }
if (-not $tabDismissOk) { Write-Host 'PRODUCT_FAIL: inspector survived tab switch'; exit 2 }
if ($tabCount -lt 2) { Write-Host "WARN: tabCount=$tabCount (UIA count may flake on AOT; dismissOk=$tabDismissOk)" }
exit 0
