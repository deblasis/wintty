#requires -Version 7
# Tab preset colors: all swatches + None, active/inactive, recolor, layout round-trip.
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
public static class TcFz {
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint KEYUP = 0x0002;
    public const byte VK_CONTROL = 0x11;
    public const byte VK_SHIFT = 0x10;
    public const byte VK_OEM_COMMA = 0xBC;
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    public class WinRect { public int L,T,R,B; public int W { get { return R-L; } } public int Hh { get { return B-T; } } }
    public static IntPtr P(long hwnd) { return new IntPtr(hwnd); }
    public static WinRect RectOf(long hwnd) {
        var h = P(hwnd); RECT r;
        if (!IsWindow(h) || !GetWindowRect(h, out r)) return null;
        var wr = new WinRect { L=r.L,T=r.T,R=r.R,B=r.B };
        return (wr.W < 80 || wr.Hh < 80) ? null : wr;
    }
    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static uint PidOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }
    // Synthesized keystrokes go to whatever owns the foreground, not to a
    // handle. SetForegroundWindow fails silently under the foreground lock,
    // so confirm the target actually has it before sending Ctrl+Shift+, --
    // otherwise the chord lands in the developer's editor or browser.
    public static bool ChordToggleLayout(IntPtr expected) {
        if (expected == IntPtr.Zero) return false;
        for (int i = 0; i < 20; i++) {
            if (GetForegroundWindow() == expected) break;
            SetForegroundWindow(expected);
            Thread.Sleep(50);
        }
        if (GetForegroundWindow() != expected) return false;
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
        keybd_event(VK_OEM_COMMA, 0, 0, UIntPtr.Zero);
        keybd_event(VK_OEM_COMMA, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_SHIFT, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
        return true;
    }
    static bool OwnedByTarget(uint pid, int x, int y) {
        var hit = WindowFromPoint(new POINT { X=x, Y=y });
        return ClassOf(hit) != "WinttySplash" && PidOf(hit) == pid;
    }
    public static bool Click(uint pid, int x, int y, bool right) {
        if (!OwnedByTarget(pid, x, y)) return false;
        if (!SetCursorPos(x, y)) return false;
        Thread.Sleep(40);
        // Re-probe after the settle: a toast, UAC prompt or flyout can
        // take the point during the sleep, and the click would land on
        // whatever arrived instead of the target window.
        if (!OwnedByTarget(pid, x, y)) return false;
        if (right) {
            mouse_event(MOUSEEVENTF_RIGHTDOWN,0,0,0,UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_RIGHTUP,0,0,0,UIntPtr.Zero);
        } else {
            mouse_event(MOUSEEVENTF_LEFTDOWN,0,0,0,UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP,0,0,0,UIntPtr.Zero);
        }
        Thread.Sleep(250);
        return true;
    }
}
'@

function Get-Main([uint32]$ProcId) {
    $hits = [System.Collections.Generic.List[object]]::new()
    $cb = [TcFz+EnumProc]{
        param($h,$lp)
        [uint32]$o=0; [void][TcFz]::GetWindowThreadProcessId($h,[ref]$o)
        if ($o -ne $ProcId -or -not [TcFz]::IsWindowVisible($h)) { return $true }
        if ([TcFz]::ClassOf($h) -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [TcFz]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64=$hwnd64; Area=($rc.W*$rc.Hh) })
        return $true
    }
    [void][TcFz]::EnumWindows($cb,[IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending | Select-Object -First 1
}

function Wait-Ready($proc) {
    $dl = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 300
        $proc.Refresh(); if ($proc.HasExited) { throw "exit $($proc.ExitCode)" }
        $m = Get-Main ([uint32]$proc.Id)
        if ($m) { Start-Sleep -Seconds 1; return $m }
    }
    throw 'no hwnd'
}

function Get-UiaRoot([int64]$Hwnd64) {
    return [System.Windows.Automation.AutomationElement]::FromHandle([TcFz]::P($Hwnd64))
}

function Find-Name($root, [string]$name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-NameRetry($root, [string]$name, [int]$ms = 1200) {
    $dl = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $dl) {
        $el = Find-Name $root $name
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 80
    }
    return $null
}

function Invoke-El($el, [uint32]$ProcId, [string]$what) {
    if ($null -eq $el) { throw "HARVEST_MISS: $what" }
    try {
        $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pat.Invoke(); Start-Sleep -Milliseconds 400; return
    } catch { }
    $r = $el.Current.BoundingRectangle
    $x = [int]($r.X + $r.Width/2); $y = [int]($r.Y + $r.Height/2)
    if (-not [TcFz]::Click($ProcId, $x, $y, $false)) { throw "HARVEST_MISS: click $what" }
}

function Shot([int64]$Hwnd64, [string]$name) {
    $rc = [TcFz]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw "HARVEST_MISS: degenerate rect for $name" }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L,$rc.T,0,0,$bmp.Size)
    $p = Join-Path $OutDir "shots\$name.png"
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose()
    Write-Host "shot $name"
}

function Shot-StripCrop([int64]$Hwnd64, [string]$name, [int]$width, [int]$height) {
    $rc = [TcFz]::RectOf($Hwnd64)
    $w = [Math]::Min($width, $rc.W)
    $h = [Math]::Min($height, $rc.Hh)
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, [System.Drawing.Size]::new($w, $h))
    $p = Join-Path $OutDir "shots\$name.png"
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose()
    Write-Host "crop $name ${w}x${h}"
}

function Invoke-NewTab($root, [uint32]$ProcId) {
    $el = Find-Name $root 'New tab'
    if ($null -eq $el) { throw 'HARVEST_MISS: New tab' }
    Invoke-El $el $ProcId 'New tab'
}

function Get-HorizTabItems($root) {
    $list = $null
    foreach ($listId in @('TabListView', 'TabList')) {
        $listCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $listId)
        $dl = (Get-Date).AddSeconds(3)
        while ((Get-Date) -lt $dl) {
            $list = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCond)
            if ($null -ne $list) { break }
            Start-Sleep -Milliseconds 100
            $root = Get-UiaRoot $script:MainHwnd64
        }
        if ($null -ne $list) { break }
    }
    if ($null -eq $list) { return @() }
    foreach ($ctName in @('TabItem', 'ListItem')) {
        $ct = [System.Windows.Automation.ControlType]::$ctName
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
        $found = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($found.Count -gt 0) {
            $out = @(); foreach ($i in $found) { $out += $i }; return $out
        }
    }
    return @()
}

function Get-HorizTabClickPoint($root, [int]$tabIndex, [double]$xBias = 0.5) {
    $tabs = Get-HorizTabItems $root
    if ($tabs.Count -gt $tabIndex) {
        $r = $tabs[$tabIndex].Current.BoundingRectangle
        if (-not [double]::IsNaN($r.X) -and -not [double]::IsNaN($r.Y) -and $r.Width -gt 2 -and $r.Height -gt 2) {
            return @([int]($r.X + $r.Width * $xBias), [int]($r.Y + $r.Height / 2))
        }
    }
    foreach ($listId in @('TabListView', 'TabList')) {
        $listCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $listId)
        $list = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCond)
        if ($null -eq $list) { continue }
        $lr = $list.Current.BoundingRectangle
        if ([double]::IsNaN($lr.X) -or $lr.Width -le 1) { continue }
        $count = [Math]::Max($tabs.Count, $TabCount)
        $slotW = $lr.Width / $count
        $x = [int]($lr.X + $slotW * $tabIndex + $slotW * $xBias)
        $y = [int]($lr.Y + $lr.Height / 2)
        return @($x, $y)
    }
    throw "HARVEST_MISS: horiz tab point $tabIndex"
}

function Get-VertTabClickPoint($root, [int]$tabIndex) {
    $items = Find-VertNavItems $root
    if ($items.Count -gt $tabIndex) {
        $r = $items[$tabIndex].Current.BoundingRectangle
        if (-not [double]::IsNaN($r.X) -and $r.Width -gt 2 -and $r.Height -gt 2) {
            return @([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
        }
    }
    $navCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NavView')
    $nav = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $navCond)
    if ($null -ne $nav) {
        $nr = $nav.Current.BoundingRectangle
        $rowH = 36
        $x = [int]($nr.X + $nr.Width / 2)
        $y = [int]($nr.Y + 72 + $tabIndex * $rowH)
        return @($x, $y)
    }
    throw "HARVEST_MISS: vert tab point $tabIndex"
}

function Select-HorizTab($root, [uint32]$ProcId, [int]$index) {
    $tabs = Get-HorizTabItems $root
    if ($tabs.Count -gt $index) {
        $r = $tabs[$index].Current.BoundingRectangle
        if (-not [double]::IsNaN($r.X) -and $r.Width -gt 2) {
            Invoke-El $tabs[$index] $ProcId "horiz tab $index"
            return
        }
    }
    $pt = Get-HorizTabClickPoint $root $index
    if (-not [TcFz]::Click($ProcId, $pt[0], $pt[1], $false)) {
        throw "HARVEST_MISS: horiz tab click $index"
    }
}

function Find-VertNavItems($root) {
    $navCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NavView')
    $nav = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $navCond)
    if ($null -eq $nav) { return @() }
    $itemCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $found = $nav.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond)
    $out = @(); foreach ($i in $found) { $out += $i }; return $out
}

function Select-VertTab($root, [uint32]$ProcId, [int]$index) {
    $items = Find-VertNavItems $root
    if ($items.Count -gt $index) {
        $r = $items[$index].Current.BoundingRectangle
        if (-not [double]::IsNaN($r.X) -and $r.Width -gt 2) {
            Invoke-El $items[$index] $ProcId "vert tab $index"
            return
        }
    }
    $pt = Get-VertTabClickPoint $root $index
    if (-not [TcFz]::Click($ProcId, $pt[0], $pt[1], $false)) {
        throw "HARVEST_MISS: vert tab click $index"
    }
}

function Set-TabColor($root, [uint32]$ProcId, [int64]$Hwnd64, [int]$tabIndex, [string]$colorName, [bool]$vertical) {
    Select-Tab $root $ProcId $tabIndex $vertical
    Start-Sleep -Milliseconds 250
    $root = Get-UiaRoot $Hwnd64
    if ($vertical) {
        $pt = Get-VertTabClickPoint $root $tabIndex
    } else {
        $pt = Get-HorizTabClickPoint $root $tabIndex 0.28
    }
    $x = $pt[0]; $y = $pt[1]
    if (-not [TcFz]::Click($ProcId, $x, $y, $true)) { throw "HARVEST_MISS: tab context $tabIndex" }
    Start-Sleep -Milliseconds 350
    $root = Get-UiaRoot $Hwnd64
    $pick = Find-NameRetry $root 'Tab Color...' 2500
    if ($null -eq $pick) { throw "HARVEST_MISS: Tab Color menu $tabIndex" }
    Invoke-El $pick $ProcId 'Tab Color...'
    Start-Sleep -Milliseconds 350
    $root = Get-UiaRoot $Hwnd64
    $sw = Find-NameRetry $root $colorName 1500
    if ($null -eq $sw) { throw "HARVEST_MISS: swatch $colorName" }
    Invoke-El $sw $ProcId "swatch $colorName"
    Start-Sleep -Milliseconds 300
}

function Expand-VertSidebar($root, [uint32]$ProcId) {
    $dl = (Get-Date).AddSeconds(4)
    $el = $null
    while ((Get-Date) -lt $dl) {
        $idCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'PaneToggleButton')
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
        if ($null -ne $el) { break }
        foreach ($n in @('Toggle sidebar', 'Expand sidebar', 'Collapse sidebar')) {
            $el = Find-Name $root $n
            if ($null -ne $el) { break }
        }
        if ($null -ne $el) { break }
        Start-Sleep -Milliseconds 150
        $root = Get-UiaRoot $script:MainHwnd64
    }
    if ($null -eq $el) { throw 'HARVEST_MISS: PaneToggleButton' }
    Invoke-El $el $ProcId 'Expand sidebar'
    Start-Sleep -Milliseconds 600
}

function Find-ByAutomationIdPrefix($root, [string]$prefix) {
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

function Focus-TerminalForShortcuts([int64]$Hwnd64, [uint32]$ProcId) {
    [void][TcFz]::SetForegroundWindow([TcFz]::P($Hwnd64))
    Start-Sleep -Milliseconds 120
    $root = Get-UiaRoot $Hwnd64
    $grid = Find-ByAutomationIdPrefix $root 'TerminalGrid'
    if ($null -ne $grid) {
        $r = $grid.Current.BoundingRectangle
        if (-not [double]::IsNaN($r.X) -and $r.Width -gt 10) {
            $x = [int]($r.X + $r.Width / 2)
            $y = [int]($r.Y + $r.Height / 2)
            if ([TcFz]::Click($ProcId, $x, $y, $false)) { Start-Sleep -Milliseconds 120; return }
        }
    }
    $rc = [TcFz]::RectOf($Hwnd64)
    if ($null -ne $rc) {
        [void][TcFz]::Click($ProcId, $rc.L + 400, $rc.T + 280, $false)
        Start-Sleep -Milliseconds 120
    }
}

function Toggle-Layout([int64]$Hwnd64, [uint32]$ProcId) {
    Focus-TerminalForShortcuts $Hwnd64 $ProcId
    if (-not [TcFz]::ChordToggleLayout([TcFz]::P($Hwnd64))) {
        throw 'FOREGROUND_MISS: layout chord not sent'
    }
    Start-Sleep -Milliseconds 1200
}

function Find-NavPane($root) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NavView')
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-NavPaneWidth($root) {
    $nav = Find-NavPane $root
    if ($null -eq $nav) { return 0 }
    $w = $nav.Current.BoundingRectangle.Width
    if ([double]::IsNaN($w)) { return 0 }
    return [int]$w
}

function Get-HorizStripWidth($root) {
    foreach ($listId in @('TabListView', 'TabList')) {
        $listCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $listId)
        $list = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCond)
        if ($null -eq $list) { continue }
        $w = $list.Current.BoundingRectangle.Width
        if (-not [double]::IsNaN($w) -and $w -gt 0) { return [int]$w }
    }
    return 0
}

function Get-LayoutMode($root) {
    $navW = Get-NavPaneWidth $root
    $tabW = Get-HorizStripWidth $root
    $toggleCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'PaneToggleButton')
    $toggle = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $toggleCond)

    # Collapsed vertical host stays in the tree; use geometry, not mere presence.
    if ($tabW -gt 120) { return 'horizontal' }
    if ($navW -ge 40 -and $null -ne $toggle) { return 'vertical' }
    return 'unknown'
}

function Assert-LayoutMode($root, [string]$want) {
    $mode = Get-LayoutMode $root
    if ($mode -ne $want) {
        throw "PRODUCT_FAIL: layout is '$mode', expected '$want' (navW=$(Get-NavPaneWidth $root) tabW=$(Get-HorizStripWidth $root))"
    }
}

function Wait-VertStripReady([int64]$Hwnd64, [int]$expected) {
    $dl = (Get-Date).AddSeconds(12)
    $best = 0
    while ((Get-Date) -lt $dl) {
        $count = (Find-VertNavItems (Get-UiaRoot $Hwnd64)).Count
        if ($count -gt $best) { $best = $count }
        if ($count -ge $expected) { return $count }
        Start-Sleep -Milliseconds 200
    }
    # MUXC often virtualizes: coordinate fallback still drives every tab index.
    if ($best -lt [Math]::Max(5, $expected - 2)) {
        throw "PRODUCT_FAIL: vertical nav items $best, expected $expected"
    }
    Write-Host "WARN vertical UIA items=$best expected=$expected (continuing with coordinate fallback)" -ForegroundColor Yellow
    return $best
}

function Wait-LayoutMode([int64]$Hwnd64, [string]$want, [int]$seconds = 8) {
    $dl = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $dl) {
        $mode = Get-LayoutMode (Get-UiaRoot $Hwnd64)
        if ($mode -eq $want) { return }
        Start-Sleep -Milliseconds 120
    }
    Assert-LayoutMode (Get-UiaRoot $Hwnd64) $want
}

function Ensure-HorizontalLayout([int64]$Hwnd64, [uint32]$ProcId) {
    if ((Get-LayoutMode (Get-UiaRoot $Hwnd64)) -eq 'horizontal') { return }

    Toggle-Layout $Hwnd64 $ProcId
    Wait-LayoutMode $Hwnd64 'horizontal' 8

    if ((Get-LayoutMode (Get-UiaRoot $Hwnd64)) -eq 'horizontal') { return }

    $root = Get-UiaRoot $Hwnd64
    if ((Get-LayoutMode $root) -eq 'vertical') {
        $nav = Find-NavPane $root
        if ($null -ne $nav) {
            $r = $nav.Current.BoundingRectangle
            $x = [int]($r.X + 12)
            $y = [int]($r.Y + $r.Height - 48)
            [void][TcFz]::Click($ProcId, $x, $y, $true)
            Start-Sleep -Milliseconds 450
            $sw = Find-NameRetry (Get-UiaRoot $Hwnd64) 'Switch to horizontal tabs' 2500
            if ($null -eq $sw) { throw 'HARVEST_MISS: Switch to horizontal tabs' }
            Invoke-El $sw $ProcId 'Switch to horizontal tabs'
            Start-Sleep -Milliseconds 1200
        }
    }

    Wait-LayoutMode $Hwnd64 'horizontal' 8
    $items = Get-HorizTabItems (Get-UiaRoot $Hwnd64)
    if ($items.Count -lt $TabCount) {
        throw "HARVEST_MISS: horizontal tabs $($items.Count), want $TabCount"
    }
}

function Select-Tab($root, [uint32]$ProcId, [int]$index, [bool]$vertical) {
    if ($vertical) { Select-VertTab $root $ProcId $index }
    else { Select-HorizTab $root $ProcId $index }
}

function Collapse-VertSidebar($root, [uint32]$ProcId) {
    $idCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'PaneToggleButton')
    $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
    if ($null -eq $el) { $el = Find-Name $root 'Toggle sidebar' }
    if ($null -eq $el) { throw 'HARVEST_MISS: collapse PaneToggleButton' }
    Invoke-El $el $ProcId 'Collapse sidebar'
    Start-Sleep -Milliseconds 500
}

# Select each tab once; one strip crop shows active + all inactive siblings.
function Shot-ActiveCycle(
    [int64]$Hwnd64,
    [uint32]$ProcId,
    [bool]$vertical,
    [string]$prefix,
    [string[]]$labels,
    [int]$cropW,
    [int]$cropH
) {
    for ($i = 0; $i -lt $labels.Count; $i++) {
        $root = Get-UiaRoot $Hwnd64
        if ($vertical) { Assert-LayoutMode $root 'vertical' }
        else { Assert-LayoutMode $root 'horizontal' }
        Select-Tab $root $ProcId $i $vertical
        Start-Sleep -Milliseconds 200
        $tag = if ([string]::IsNullOrEmpty($labels[$i])) { 'None' } else { $labels[$i] }
        $state = if ($vertical) { 'v' } else { 'h' }
        Shot-StripCrop $Hwnd64 "${prefix}-${state}-a${i}-${tag}" $cropW $cropH
    }
}

function Ensure-VerticalLayout([int64]$Hwnd64, [uint32]$ProcId) {
    if ((Get-LayoutMode (Get-UiaRoot $Hwnd64)) -eq 'vertical') { return }

    Toggle-Layout $Hwnd64 $ProcId
    Wait-LayoutMode $Hwnd64 'vertical' 8
    if ((Get-LayoutMode (Get-UiaRoot $Hwnd64)) -eq 'vertical') { return }

    $root = Get-UiaRoot $Hwnd64
    $tvCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'TabViewControl')
    $tv = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $tvCond)
    if ($null -ne $tv) {
        $r = $tv.Current.BoundingRectangle
        $x = [int]($r.X + $r.Width - 180)
        $y = [int]($r.Y + $r.Height - 8)
        [void][TcFz]::Click($ProcId, $x, $y, $true)
    } else {
        $rc = [TcFz]::RectOf($Hwnd64)
        if ($null -eq $rc) { throw 'HARVEST_MISS: window rect' }
        [void][TcFz]::Click($ProcId, $rc.L + 520, $rc.T + 28, $true)
    }
    Start-Sleep -Milliseconds 450
    $sw = Find-NameRetry (Get-UiaRoot $Hwnd64) 'Switch to vertical tabs' 2500
    if ($null -eq $sw) {
        Toggle-Layout $Hwnd64 $ProcId
        Wait-LayoutMode $Hwnd64 'vertical' 8
        if ((Get-LayoutMode (Get-UiaRoot $Hwnd64)) -eq 'vertical') { return }
        throw 'HARVEST_MISS: Switch to vertical tabs'
    }
    Invoke-El $sw $ProcId 'Switch to vertical tabs'
    Start-Sleep -Milliseconds 1200
    Wait-LayoutMode $Hwnd64 'vertical' 8
    Assert-LayoutMode (Get-UiaRoot $Hwnd64) 'vertical'
}

# Tab 0 stays default (None). Tabs 1..9 get every preset swatch.
$AllPresets = @('Blue', 'Purple', 'Pink', 'Red', 'Orange', 'Yellow', 'Green', 'Teal', 'Graphite')
$TabCount = 1 + $AllPresets.Count   # 10

$tempXdg = Join-Path $env:TEMP "wintty-fuzz-colors-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path (Join-Path $tempXdg 'wintty') | Out-Null
@'
windows-single-instance = true
window-save-state = never
windows-settings-ui = true
vertical-tabs = false
window-theme = wintty
theme = Catppuccin Mocha
no-color-override = strip
'@ | Set-Content (Join-Path $tempXdg 'wintty\config.wintty') -Encoding utf8

$script:FatalWasProduct = $null
$origXdg = $env:XDG_CONFIG_HOME
$origNoColor = $env:NO_COLOR
$proc = $null
$result = [ordered]@{
    tabCount = $TabCount
    presets = $AllPresets
    phases = @()
    recolors = @()
}
function Add-Phase([string]$name, [scriptblock]$body) {
    try {
        & $body
        $script:result.phases += [ordered]@{ name = $name; ok = $true }
        Write-Host "OK $name" -ForegroundColor Green
    } catch {
        $script:result.phases += [ordered]@{ name = $name; ok = $false; error = $_.Exception.Message }
        throw
    }
}
# Above the try, so the refusal message survives: with the gate inside, the
# sweep in the finally would bind a null stamp to a mandatory [datetime] and
# that binding error would replace it, taking the env restores with it.
Assert-NoWintty -Context 'The tab-color fuzz'
$script:WinttyStamp = Get-WinttyLaunchStamp

try {
    Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue
    $env:XDG_CONFIG_HOME = $tempXdg
    if (-not (Test-Path $ExePath)) { throw "missing exe: $ExePath" }
    $proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path -Parent (Resolve-Path $ExePath))
    $pid32 = [uint32]$proc.Id
    $main = Wait-Ready $proc
    $hwnd64 = [int64]$main.Hwnd64
    $script:MainHwnd64 = $hwnd64
    [void][TcFz]::SetForegroundWindow([TcFz]::P($hwnd64))
    Start-Sleep -Milliseconds 400

    # Build tab labels array: index -> color name (empty string = None/default).
    $labels = @('')
    foreach ($c in $AllPresets) { $labels += $c }

    Add-Phase 'spawn-tabs' {
        $root = Get-UiaRoot $hwnd64
        for ($n = 1; $n -lt $TabCount; $n++) {
            Invoke-NewTab (Get-UiaRoot $hwnd64) $pid32
        }
        Start-Sleep -Milliseconds 700
        $tabs = Get-HorizTabItems (Get-UiaRoot $hwnd64)
        if ($tabs.Count -ne $TabCount) {
            throw "HARVEST_MISS: expected $TabCount tabs, have $($tabs.Count)"
        }
        Shot-StripCrop $hwnd64 'h-initial-all-none' 620 40
    }

    Add-Phase 'assign-all-presets' {
        $root = Get-UiaRoot $hwnd64
        for ($i = 1; $i -lt $TabCount; $i++) {
            Set-TabColor $root $pid32 $hwnd64 $i $AllPresets[$i - 1] $false
            $root = Get-UiaRoot $hwnd64
        }
    }

    Add-Phase 'horiz-active-inactive-cycle' {
        Assert-LayoutMode (Get-UiaRoot $hwnd64) 'horizontal'
        Shot-ActiveCycle $hwnd64 $pid32 $false 'horiz' $labels 620 40
    }

    Add-Phase 'switch-to-vertical-preserve' {
        Assert-LayoutMode (Get-UiaRoot $hwnd64) 'horizontal'
        Select-HorizTab (Get-UiaRoot $hwnd64) $pid32 3
        Shot-StripCrop $hwnd64 'h-before-switch-a3-Pink' 620 40
        Ensure-VerticalLayout $hwnd64 $pid32
        $root = Get-UiaRoot $hwnd64
        Assert-LayoutMode $root 'vertical'
        Expand-VertSidebar $root $pid32
        $null = Wait-VertStripReady $hwnd64 $TabCount
        Shot $hwnd64 'v-after-switch-full'
    }

    Add-Phase 'vert-same-colors-as-horiz' {
        Assert-LayoutMode (Get-UiaRoot $hwnd64) 'vertical'
        # Same label set as horizontal -- parity check after layout switch.
        Shot-ActiveCycle $hwnd64 $pid32 $true 'parity' $labels 280 580
    }

    Add-Phase 'switch-back-horizontal-preserve' {
        Ensure-HorizontalLayout $hwnd64 $pid32
        Assert-LayoutMode (Get-UiaRoot $hwnd64) 'horizontal'
        Shot-ActiveCycle $hwnd64 $pid32 $false 'return' $labels 620 40
    }

    Add-Phase 'recolor-existing-tabs' {
        $changes = @(
            @{ i = 1; from = 'Blue';     to = 'Teal' },
            @{ i = 2; from = 'Purple';   to = 'Orange' },
            @{ i = 4; from = 'Red';      to = 'Pink' },
            @{ i = 6; from = 'Yellow';   to = 'None' },
            @{ i = 9; from = 'Graphite'; to = 'Green' }
        )
        $root = Get-UiaRoot $hwnd64
        foreach ($ch in $changes) {
            Set-TabColor $root $pid32 $hwnd64 $ch.i $ch.to $false
            $root = Get-UiaRoot $hwnd64
            $result.recolors += [ordered]@{
                tab = $ch.i; from = $ch.from; to = $ch.to; ok = $true
            }
        }
        $script:labelsAfter = @(
            '',       # 0 default None
            'Teal',   # 1 Blue->Teal
            'Orange', # 2 Purple->Orange
            'Pink',   # 3 unchanged
            'Pink',   # 4 Red->Pink
            'Orange', # 5 unchanged
            '',       # 6 Yellow->None
            'Green',  # 7 unchanged
            'Teal',   # 8 unchanged
            'Green'   # 9 Graphite->Green
        )
        Shot-ActiveCycle $hwnd64 $pid32 $false 'recolor' $script:labelsAfter 620 40
    }

    Add-Phase 'recolor-vert-parity' {
        Ensure-VerticalLayout $hwnd64 $pid32
        Expand-VertSidebar (Get-UiaRoot $hwnd64) $pid32
        Assert-LayoutMode (Get-UiaRoot $hwnd64) 'vertical'
        Shot-ActiveCycle $hwnd64 $pid32 $true 'recolor' $script:labelsAfter 280 580
    }

    Add-Phase 'recolor-horiz-return' {
        Ensure-HorizontalLayout $hwnd64 $pid32
        Assert-LayoutMode (Get-UiaRoot $hwnd64) 'horizontal'
        Shot-ActiveCycle $hwnd64 $pid32 $false 'recolor-return' $script:labelsAfter 620 40
    }

    $result.phases += [ordered]@{ name = 'complete'; ok = $true }
}
catch {
    # Record and fall through: rethrowing here skipped result.json and the
    # exit-2 contract entirely, so a failed run reported an unhandled
    # exception and left no artifact -- exactly when one is most useful.
    if ($null -ne $proc -and -not $proc.HasExited) {
        try { Shot $hwnd64 'fail-state' } catch { }
    }
    $result.phases += [ordered]@{ name = 'fatal'; ok = $false; error = "$_" }
    # Which kind of failure it was decides the exit code. A HARVEST_MISS - a
    # menu item the script could not find, a click the window refused - is a
    # run that judged nothing, and filing it as a product finding means it is
    # never retried and shows up as a defect in the build.
    $script:FatalWasProduct = ("$_" -like 'PRODUCT_FAIL*')
}
finally {
    # Only this run's processes. `Get-Process Wintty | Stop-Process` also
    # kills the developer's real session on the same desktop. Kill the tree:
    # the shell runs as a child and outlives a kill on the parent alone.
    if ($null -ne $proc -and -not $proc.HasExited) {
        try { $proc.Kill($true); [void]$proc.WaitForExit(3000) } catch { }
    }
    if ($null -ne $origXdg) { $env:XDG_CONFIG_HOME = $origXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
    if ($null -ne $origNoColor) { $env:NO_COLOR = $origNoColor }
    else { Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue }
    if ($null -ne $tempXdg -and (Test-Path $tempXdg)) {
        Remove-Item -Recurse -Force $tempXdg -ErrorAction SilentlyContinue
    }
    # After the env restores, not before: a throw in the sweep would otherwise
    # abandon them and leave the shell pointed at a temp profile.
    Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $ExePath
}

$result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir 'result.json')
$fail = @($result.phases | Where-Object { -not $_.ok })
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)
if ($fail.Count -eq 0) { exit 0 }
# A phase that failed without a fatal error is a real assertion failing; a
# fatal PRODUCT_FAIL is too. Anything else got in the way of the run.
if ($null -eq $script:FatalWasProduct -or $script:FatalWasProduct) { exit 2 }
exit 1
