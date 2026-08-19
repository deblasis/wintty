#requires -Version 7
# Iterate mouse/UIA fuzz until chrome actions have on-screen evidence.
# Mouse only after WindowFromPoint pid == Wintty. Prefer UIA Invoke.
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

public static class Mz {
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint WM_CHAR = 0x0102;
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
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr lp);
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
    public static Hit ProbeScreen(uint pid, int x, int y) {
        var hit = WindowFromPoint(new POINT { X=x, Y=y });
        uint hitPid = PidOf(hit);
        string cls = ClassOf(hit);
        if (cls == "WinttySplash") return Miss("splash covering", x, y, hitPid, cls);
        if (!OwnedByPid(pid, hit)) return Miss("WindowFromPoint is not Wintty", x, y, hitPid, cls);
        return new Hit { Ok=true, X=x, Y=y, HitPid=hitPid, HitClass=cls };
    }
    public static Hit ClickScreen(uint pid, int x, int y, bool right) {
        var p = ProbeScreen(pid, x, y);
        if (!p.Ok) return p;
        if (!SetCursorPos(x, y)) return Miss("SetCursorPos failed", x, y, p.HitPid, p.HitClass);
        Thread.Sleep(40);
        p = ProbeScreen(pid, x, y);
        if (!p.Ok) return p;
        if (right) {
            mouse_event(MOUSEEVENTF_RIGHTDOWN,0,0,0,UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_RIGHTUP,0,0,0,UIntPtr.Zero);
        } else {
            mouse_event(MOUSEEVENTF_LEFTDOWN,0,0,0,UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP,0,0,0,UIntPtr.Zero);
        }
        Thread.Sleep(250);
        return p;
    }
}
'@

function Get-Main([int]$ProcId) {
    $hits = [System.Collections.Generic.List[object]]::new()
    $cb = [Mz+EnumProc]{
        param($h,$lp)
        [uint32]$o=0; [void][Mz]::GetWindowThreadProcessId($h,[ref]$o)
        if ($o -ne $ProcId -or -not [Mz]::IsWindowVisible($h)) { return $true }
        $c = New-Object System.Text.StringBuilder 256
        $t = New-Object System.Text.StringBuilder 512
        [void][Mz]::GetClassName($h,$c,256); [void][Mz]::GetWindowText($h,$t,512)
        if ($c.ToString() -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [Mz]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64=$hwnd64; Title=$t.ToString(); Area=($rc.W*$rc.Hh) })
        return $true
    }
    [void][Mz]::EnumWindows($cb,[IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending | Select-Object -First 1
}

function Splash-Visible([int]$ProcId) {
    $script:splashSeen = $false
    $cb = [Mz+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner=0; [void][Mz]::GetWindowThreadProcessId($hwnd,[ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        $cls = New-Object System.Text.StringBuilder 256
        [void][Mz]::GetClassName($hwnd,$cls,256)
        if ($cls.ToString() -eq 'WinttySplash' -and [Mz]::IsWindowVisible($hwnd)) { $script:splashSeen = $true }
        return $true
    }
    [void][Mz]::EnumWindows($cb,[IntPtr]::Zero)
    return $script:splashSeen
}

function Wait-Ready($proc) {
    $dl = (Get-Date).AddSeconds(40)
    $got = $null
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 250
        $proc.Refresh(); if ($proc.HasExited) { throw "PRODUCT_FAIL startup exit=$($proc.ExitCode)" }
        $got = Get-Main $proc.Id
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
    $h = $Hwnd64
    $rc = $null
    for ($i = 0; $i -lt 4; $i++) {
        if ($script:FuzzPid -ne 0) {
            $proc = Get-Process -Id $script:FuzzPid -ErrorAction SilentlyContinue
            if ($null -eq $proc) { throw "PRODUCT_FAIL: process gone during $name" }
            $proc.Refresh()
            if ($proc.HasExited) { throw "PRODUCT_FAIL: process exit=$($proc.ExitCode) during $name" }
            $main = Get-Main $script:FuzzPid
            if ($null -eq $main) { Start-Sleep -Milliseconds 350; continue }
            $h = [int64]$main.Hwnd64
        }
        $rc = [Mz]::RectOf($h)
        if ($null -ne $rc) { break }
        Start-Sleep -Milliseconds 350
    }
    if ($null -eq $rc) { throw "HARVEST_MISS: degenerate rect for $name" }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L,$rc.T,0,0,$bmp.Size)
    $p = Join-Path $OutDir "shots\$name.png"
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose()
    Write-Host "shot $name $($rc.W)x$($rc.Hh)"
    return $p
}

function Get-UiaRoot([int64]$Hwnd64) {
    return [System.Windows.Automation.AutomationElement]::FromHandle([Mz]::P($Hwnd64))
}

function Dump-Uia([System.Windows.Automation.AutomationElement]$root, [string]$path) {
    $lines = [System.Collections.Generic.List[string]]::new()
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    function Walk($el, $depth) {
        if ($null -eq $el -or $depth -gt 10) { return }
        try {
            $c = $el.Current
            $r = $c.BoundingRectangle
            $name = $c.Name
            $ct = $c.ControlType.ProgrammaticName
            $auto = $c.AutomationId
            if ($name -or $ct -match 'Button|Menu|List|Tab|Split|Hyperlink|Tree') {
                $lines.Add(("{0}{1} name='{2}' id='{3}' {4:N0},{5:N0} {6:N0}x{7:N0}" -f ('  '*$depth), $ct, $name, $auto, $r.X, $r.Y, $r.Width, $r.Height))
            }
        } catch { }
        try {
            $ch = $walker.GetFirstChild($el)
            while ($null -ne $ch) {
                Walk $ch ($depth+1)
                $ch = $walker.GetNextSibling($ch)
            }
        } catch { }
    }
    Walk $root 0
    $lines | Set-Content -Path $path -Encoding utf8
    Write-Host "uia dump $($lines.Count) lines -> $path"
}

function Find-UiaByName([System.Windows.Automation.AutomationElement]$root, [string]$name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-UiaByNameRetry([System.Windows.Automation.AutomationElement]$root, [string]$name, [int]$ms = 800) {
    $dl = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $dl) {
        $el = Find-UiaByName $root $name
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 80
    }
    return $null
}

function Find-ByAutomationIdPrefix([System.Windows.Automation.AutomationElement]$root, [string]$prefix) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $stack = [System.Collections.Generic.Stack[object]]::new()
    $stack.Push($root)
    while ($stack.Count -gt 0) {
        $el = $stack.Pop()
        try {
            $id = $el.Current.AutomationId
            if ($id -and $id.StartsWith($prefix)) { return $el }
        } catch { }
        try {
            $ch = $walker.GetFirstChild($el)
            while ($null -ne $ch) { $stack.Push($ch); $ch = $walker.GetNextSibling($ch) }
        } catch { }
    }
    return $null
}

function Get-TabCloseButtons([System.Windows.Automation.AutomationElement]$root) {
    $listCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'TabList')
    $list = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCond)
    if ($null -eq $list) { return @() }
    $btnCt = [System.Windows.Automation.ControlType]::Button
    $btnCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $btnCt)
    $found = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    $out = @()
    foreach ($b in $found) { $out += $b }
    return $out
}

function Find-StripChevron([System.Windows.Automation.AutomationElement]$root) {
    $named = $null
    foreach ($n in @('Expand sidebar', 'Collapse sidebar')) {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $n)
        $named = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($null -ne $named) { return $named }
    }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ChevronButton')
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($el in $all) {
        # Strip chevron used to have empty Name; profile chevron is "Open profile menu".
        if ([string]::IsNullOrEmpty($el.Current.Name)) { return $el }
    }
    return $null
}

function Ensure-VerticalTabs([int64]$MainHwnd, [uint32]$ProcId) {
    $root = Get-UiaRoot $MainHwnd
    if ($null -ne (Find-StripChevron $root)) { return }
    Write-Host "no strip chevron (horizontal tabs?); switching layout"
    $tab = Find-UiaByName $root 'Application menu'
    $rc = [Mz]::RectOf($MainHwnd)
    # Empty tab-strip chrome, not a TabViewItem (those get the per-tab
    # menu with no layout switch). + and profile sit ~290px from the left.
    $x = $rc.L + 420; $y = $rc.T + 16
    $hit = [Mz]::ClickScreen($ProcId, $x, $y, $true)
    if (-not $hit.Ok) { throw "HARVEST_MISS: tab-strip context $($hit.Why) class=$($hit.HitClass)" }
    Start-Sleep -Milliseconds 350
    $sw = Find-UiaByNameRetry (Get-UiaRoot $MainHwnd) 'Switch to vertical tabs' 1000
    if ($null -eq $sw) {
        Dump-Uia (Get-UiaRoot $MainHwnd) (Join-Path $OutDir 'uia-layout-miss.txt')
        throw "HARVEST_MISS: Switch to vertical tabs not under hwnd"
    }
    Invoke-UiaOrClick $sw $ProcId 'Switch to vertical tabs'
    Start-Sleep -Milliseconds 700
    if ($null -eq (Find-StripChevron (Get-UiaRoot $MainHwnd))) {
        throw "HARVEST_MISS: still no strip chevron after Switch to vertical tabs"
    }
}

function Invoke-UiaOrClick([System.Windows.Automation.AutomationElement]$el, [uint32]$ProcId, [string]$what) {
    if ($null -eq $el) { throw "HARVEST_MISS: no UIA element for $what" }
    $inv = [System.Windows.Automation.InvokePattern]::Pattern
    try {
        $pat = $el.GetCurrentPattern($inv)
        if ($null -ne $pat) {
            $pat.Invoke()
            Start-Sleep -Milliseconds 350
            Write-Host "invoke $what"
            return 'invoke'
        }
    } catch { Write-Host "invoke $what failed: $_" }
    $r = $el.Current.BoundingRectangle
    if ($r.Width -le 1 -or $r.Height -le 1 -or [double]::IsNaN($r.X) -or [double]::IsNaN($r.Y)) {
        Write-Host "HARVEST_MISS: $what degenerate UIA bounds; skipping click"
        return 'skip'
    }
    $x = [int]($r.X + $r.Width/2)
    $y = [int]($r.Y + $r.Height/2)
    $hit = [Mz]::ClickScreen($ProcId, $x, $y, $false)
    if (-not $hit.Ok) { throw "HARVEST_MISS: $what click refused: $($hit.Why) class=$($hit.HitClass) at $x,$y" }
    Write-Host "click $what $($hit.X),$($hit.Y) hit=$($hit.HitClass)"
    return 'click'
}

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

Assert-NoWintty
$script:WinttyStamp = Get-WinttyLaunchStamp
Start-Sleep -Milliseconds 400
$proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path $ExePath)
$pid32 = [uint32]$proc.Id
$script:FuzzPid = $pid32
$main = Wait-Ready $proc
$hwnd64 = [int64]$main.Hwnd64
Write-Host "hwnd=$hwnd64 pid=$pid32 title=$($main.Title)"
[Mz]::FocusForInput($hwnd64)
Shot $hwnd64 '00-launch' | Out-Null

$root = Get-UiaRoot $hwnd64
if ($null -eq $root) { throw "HARVEST_MISS: UIA FromHandle null" }
Dump-Uia $root (Join-Path $OutDir 'uia-00.txt')

$newTab = Find-UiaByName $root 'New tab'
Invoke-UiaOrClick $newTab $pid32 'New tab'
Shot $hwnd64 '01-new-tab' | Out-Null

$root = Get-UiaRoot $hwnd64
$newTab = Find-UiaByName $root 'New tab'
Invoke-UiaOrClick $newTab $pid32 'New tab 2'
Shot $hwnd64 '02-new-tab-2' | Out-Null

$root = Get-UiaRoot $hwnd64
$profile = Find-UiaByName $root 'Open profile menu'
Invoke-UiaOrClick $profile $pid32 'Open profile menu'
Shot $hwnd64 '03-profile' | Out-Null

# Dismiss flyout by clicking grid interior (relative, gated).
$rc = [Mz]::RectOf($hwnd64)
$gx = $rc.L + 320; $gy = $rc.T + 220
$hit = [Mz]::ClickScreen($pid32, $gx, $gy, $false)
if (-not $hit.Ok) { throw "HARVEST_MISS: grid dismiss refused: $($hit.Why) class=$($hit.HitClass)" }
Shot $hwnd64 '04-dismiss' | Out-Null

Ensure-VerticalTabs $hwnd64 $pid32
Shot $hwnd64 '05-vertical' | Out-Null

# Expand via the strip ChevronButton.
$root = Get-UiaRoot $hwnd64
Dump-Uia $root (Join-Path $OutDir 'uia-05.txt')
$chev = Find-StripChevron $root
Invoke-UiaOrClick $chev $pid32 'strip ChevronButton'
Start-Sleep -Milliseconds 600
Shot $hwnd64 '06-expanded' | Out-Null
$root = Get-UiaRoot $hwnd64
Dump-Uia $root (Join-Path $OutDir 'uia-06.txt')

# Grid context -> Command Palette / Split Right (menu is a desktop popup).
$rc = [Mz]::RectOf($hwnd64)
$gx = $rc.L + 400; $gy = $rc.T + 280
$hit = [Mz]::ClickScreen($pid32, $gx, $gy, $true)
if (-not $hit.Ok) { throw "HARVEST_MISS: grid context refused: $($hit.Why) class=$($hit.HitClass)" }
Start-Sleep -Milliseconds 350
Shot $hwnd64 '07-grid-menu' | Out-Null
$pal = Find-UiaByNameRetry (Get-UiaRoot $hwnd64) 'Command Palette' 1000
if ($null -eq $pal) { Write-Host 'HARVEST_MISS: Command Palette menu item not in UIA; skipping invoke' }
else {
    Invoke-UiaOrClick $pal $pid32 'Command Palette'
    Start-Sleep -Milliseconds 400
}
Shot $hwnd64 '08-palette' | Out-Null

# Dismiss palette by clicking the terminal grid, not the strip.
$grid = Find-ByAutomationIdPrefix (Get-UiaRoot $hwnd64) 'TerminalGrid'
if ($null -ne $grid) {
    $r = $grid.Current.BoundingRectangle
    $hit = [Mz]::ClickScreen($pid32, [int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2), $false)
    if (-not $hit.Ok) { Write-Host "palette dismiss refused: $($hit.Why)" }
} else {
    Write-Host 'HARVEST_MISS: no TerminalGrid for palette dismiss'
}
Start-Sleep -Milliseconds 200
$rc = [Mz]::RectOf($hwnd64)
$hit = [Mz]::ClickScreen($pid32, $rc.L + 400, $rc.T + 280, $true)
if (-not $hit.Ok) { throw "HARVEST_MISS: grid context 2 refused: $($hit.Why) class=$($hit.HitClass)" }
Start-Sleep -Milliseconds 350
Shot $hwnd64 '09-grid-menu-2' | Out-Null
$split = Find-UiaByNameRetry (Get-UiaRoot $hwnd64) 'Split Right' 1000
$splitSkipped = $false
if ($null -eq $split) {
    Write-Host 'HARVEST_MISS: Split Right menu item not in UIA; skipping invoke'
    $splitSkipped = $true
}
else {
    $mode = Invoke-UiaOrClick $split $pid32 'Split Right'
    if ($mode -eq 'skip') {
        Write-Host 'HARVEST_MISS: Split Right not actionable; skipping invoke'
        $splitSkipped = $true
    }
    else {
        Start-Sleep -Milliseconds 2000
    }
}
if ($splitSkipped) {
    $main = Get-Main $pid32
    if ($null -ne $main) {
        $hwnd64 = [int64]$main.Hwnd64
        [Mz]::FocusForInput($hwnd64)
        $rc = [Mz]::RectOf($hwnd64)
        if ($null -ne $rc) {
            [void][Mz]::ClickScreen($pid32, $rc.L + 400, $rc.T + 280, $false)
            Start-Sleep -Milliseconds 350
        }
    }
}
$main = Get-Main $pid32
if ($null -ne $main) { $hwnd64 = [int64]$main.Hwnd64 }
Shot $hwnd64 '10-split-right' | Out-Null
Shot $hwnd64 '11-split-settle' | Out-Null

# Close the first tab via the expanded-row X (22x22 unnamed button).
$root = Get-UiaRoot $hwnd64
$closes = @(Get-TabCloseButtons $root)
Write-Host "tab close buttons: $($closes.Count)"
if ($closes.Count -ge 2) {
    Invoke-UiaOrClick $closes[0] $pid32 'close first tab'
    Start-Sleep -Milliseconds 400
}
Shot $hwnd64 '12-after-close' | Out-Null
Dump-Uia (Get-UiaRoot $hwnd64) (Join-Path $OutDir 'uia-12.txt')

# Switch layout via strip context menu.
$root = Get-UiaRoot $hwnd64
$listCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'TabList')
$tabList = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCond)
if ($null -ne $tabList) {
    $r = $tabList.Current.BoundingRectangle
    $cx = [int]($r.X + 20); $cy = [int]($r.Y + 10)
    $hit = [Mz]::ClickScreen($pid32, $cx, $cy, $true)
    if (-not $hit.Ok) { Write-Host "strip list context refused: $($hit.Why)" }
    else { Write-Host "strip list context $($hit.X),$($hit.Y)" }
    Start-Sleep -Milliseconds 350
    Shot $hwnd64 '13-strip-menu' | Out-Null
    $sw = Find-UiaByNameRetry (Get-UiaRoot $hwnd64) 'Switch to horizontal tabs' 1000
    if ($null -eq $sw) { Write-Host 'HARVEST_MISS: Switch to horizontal tabs not in UIA' }
    else {
        Invoke-UiaOrClick $sw $pid32 'Switch to horizontal tabs'
        Start-Sleep -Milliseconds 700
    }
}
Shot $hwnd64 '14-horizontal' | Out-Null
Dump-Uia (Get-UiaRoot $hwnd64) (Join-Path $OutDir 'uia-14.txt')

# Inspector from grid menu.
$grid = Find-ByAutomationIdPrefix (Get-UiaRoot $hwnd64) 'TerminalGrid'
if ($null -ne $grid) {
    $r = $grid.Current.BoundingRectangle
    $gx = [int]($r.X + $r.Width*0.6); $gy = [int]($r.Y + $r.Height*0.6)
    $hit = [Mz]::ClickScreen($pid32, $gx, $gy, $true)
    if (-not $hit.Ok) { Write-Host "grid context 3 refused: $($hit.Why)" }
    Start-Sleep -Milliseconds 350
    Shot $hwnd64 '15-grid-menu-3' | Out-Null
    $insp = Find-UiaByNameRetry (Get-UiaRoot $hwnd64) 'Toggle Inspector' 1000
    if ($null -eq $insp) { Write-Host 'HARVEST_MISS: Toggle Inspector not in UIA' }
    else {
        Invoke-UiaOrClick $insp $pid32 'Toggle Inspector'
        Start-Sleep -Milliseconds 800
    }
}
Shot $hwnd64 '16-inspector' | Out-Null

$proc.Refresh()
$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
$alive = -not $proc.HasExited
@{
    alive = $alive
    crashGrew = $crashGrew
    hwnd = "$hwnd64"
} | ConvertTo-Json | Set-Content (Join-Path $OutDir 'result.json')
Write-Host "alive=$alive crashGrew=$crashGrew"
# Leave nothing behind: every other harness here refuses to start while a
# Wintty is running, and this script used to rely on the next one's
# blanket kill to reap it.
Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $ExePath
if (-not $alive -or $crashGrew) { exit 2 }
exit 0
