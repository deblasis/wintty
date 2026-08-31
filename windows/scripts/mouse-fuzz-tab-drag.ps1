#requires -Version 7
<#
    Tab drag end to end: both strips, the pin boundary, group collapse and
    the drop-on-chip join, the run label's drag refusal, and the leak
    oracle the product's own drag trace feeds.

    Scenarios, in the order the state is built up:

      1. Vertical reorder (motion on): drag one seeded row past its
         neighbour, assert the final order through the UIA names.
      2. The same gesture with the order first restored by the inverse
         drag, run again with Windows' client-area animations OFF, and
         assert the final order is identical. The OS toggle goes through
         lib/env-guard (snapshot, set, read-back, restore), and the drag
         trace is what proves the app really saw it: the session's begin
         line must read motion=off, the drop must settle NOTHING (the
         gate's no-op polarity, checked end to end), and the order must
         still land.
      3. Pin boundary: a body row dragged into the pinned zone and released
         back outside must stay unpinned and leave the order alone; the
         same drag released inside the zone must pin it, with the flight
         the trace records.
      4. Group collapse: a click on the vertical header row folds the run;
         a drag dropped onto the folded header joins the tab to the group
         and the group must re-open by itself (the manager owns the
         auto-expand).
      5. Horizontal reorder: a TabView drag of a grouped member across its
         neighbour, final order through UIA names again.
      6. The horizontal run label: hovering a grouped member shows it
         (probed first, so the probe is proved able to see it), then a live
         drag must have it gone from the UIA tree for the whole gesture -
         the drag-refusal contract, checked through the window rather than
         through a rules unit test. Then collapse the group from the
         strip's own context menu and expand it again from the chip's.

    The leak oracle is WINTTY_TABDRAG_TRACE, the per-run log the vertical
    strip writes when the env var names a file: sessions pair DRAG
    begin/end, and every ghosts=N the strip reports at or after a
    session's end must be zero. A drop on a motion-on session must be
    answered by a settle (or, for a pin drop, by the flight); a drop on a
    motion-off session must not be answered by a settle at all.

    Identity is the tab's title, seeded per tab through the shell's own
    `title` builtin, because every tab here runs the same shell and would
    otherwise be named alike. Input is SendInput only behind a foreground
    steal, and the XAML island is armed with a real click before chords.

    Exits 0 on pass, 2 on a product finding (the build under test did
    something wrong), 1 when the harness could not run and nothing is
    known about the product - a refused click, a window that never
    appeared, a machine whose state could not be restored.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$Seed = 1337
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/env-guard.ps1')
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

public static class MzTD {
    public const uint KEYEVENTF_KEYUP  = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint MOUSEEVENTF_MOVE     = 0x0001;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP   = 0x0004;
    public const ushort VK_CONTROL   = 0x11;
    public const ushort VK_SHIFT     = 0x10;
    public const ushort VK_RETURN    = 0x0D;
    public const ushort VK_T         = 0x54;
    public const ushort VK_OEM_COMMA = 0xBC;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {
        public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] public struct HARDWAREINPUT {
        public uint uMsg; public ushort wParamL; public ushort wParamH;
    }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint n, INPUT[] inputs, int cb);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int h2, bool repaint);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    public class WinRect { public int L,T,R,B; public int W { get { return R-L; } } public int Hh { get { return B-T; } } }

    public static IntPtr P(long hwnd) { return new IntPtr(hwnd); }
    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static string TitleOf(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }
    public static uint PidOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }

    public static WinRect RectOf(long hwnd) {
        var h = P(hwnd); RECT r;
        if (!IsWindow(h) || !GetWindowRect(h, out r)) return null;
        var wr = new WinRect { L=r.L,T=r.T,R=r.R,B=r.B };
        return (wr.W < 80 || wr.Hh < 80) ? null : wr;
    }

    public static long TargetHwnd;
    public static bool GuardTripped;

    // The per-injection guard: SendInput lands on the FOCUSED window, and
    // focus drifts across UIA waits, menu opens, and the window
    // deactivations this harness itself causes. Every injection batch
    // re-verifies; on a mismatch it re-steals foreground with the
    // AttachThreadInput recipe and, if the steal still fails, refuses to
    // inject blind -- the leg aborts.
    public static void EnsureForeground() {
        if (TargetHwnd == 0) return;
        IntPtr target = P(TargetHwnd);
        if (GetForegroundWindow() == target) return;
        for (int i = 0; i < 3; i++) {
            IntPtr fg = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, out _);
            uint myThread = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
            try {
                BringWindowToTop(target);
                SetForegroundWindow(target);
                SetFocus(target);
            } finally { if (attached) AttachThreadInput(myThread, fgThread, false); }
            Thread.Sleep(120);
            if (GetForegroundWindow() == target) return;
        }
        GuardTripped = true;
        throw new InvalidOperationException("GUARD: foreground mismatch after re-steal -- refusing to inject blind");
    }

    // A refused injection returns a short count and no exception, and the
    // harness would then blame the app for ignoring input it never got.
    static void Send(INPUT[] inputs) {
        EnsureForeground();
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        if (sent != inputs.Length) {
            throw new InvalidOperationException(
                "HARVEST_MISS: SendInput delivered " + sent + " of " + inputs.Length +
                " event(s), win32 error " + Marshal.GetLastWin32Error() +
                "; the input never reached the app, so nothing the app did next is evidence");
        }
    }

    static INPUT Key(ushort vk, bool up) {
        var i = new INPUT { type = 1 };
        i.U.ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = up ? KEYEVENTF_KEYUP : 0, time = 0, dwExtraInfo = IntPtr.Zero };
        return i;
    }

    static INPUT Unicode(char c, bool up) {
        var i = new INPUT { type = 1 };
        i.U.ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0), time = 0, dwExtraInfo = IntPtr.Zero };
        return i;
    }

    // Type a line into the terminal and press Return. The foreground is
    // re-taken per character because the app can repaint itself back into
    // focus mid-line.
    public static bool TypeLine(IntPtr expected, string text) {
        foreach (char c in text) {
            if (!Focus(expected)) return false;
            Send(new INPUT[] { Unicode(c, false), Unicode(c, true) });
            Thread.Sleep(12);
        }
        if (!Focus(expected)) return false;
        Send(new INPUT[] { Key(VK_RETURN, false), Key(VK_RETURN, true) });
        return true;
    }

    public static bool Chord(IntPtr expected, ushort[] mods, ushort key) {
        if (!Focus(expected)) return false;
        var seq = new System.Collections.Generic.List<INPUT>();
        foreach (var m in mods) seq.Add(Key(m, false));
        seq.Add(Key(key, false));
        seq.Add(Key(key, true));
        for (int i = mods.Length - 1; i >= 0; i--) seq.Add(Key(mods[i], true));
        Send(seq.ToArray());
        return true;
    }

    // Synthesized input goes to whatever owns the foreground, and under the
    // foreground lock a bare SetForegroundWindow fails silently. Attaching
    // to the current foreground thread's input queue lifts that lock.
    public static bool Focus(IntPtr expected) {
        if (expected == IntPtr.Zero) return false;
        for (int i = 0; i < 40; i++) {
            if (GetForegroundWindow() == expected) return true;
            var fg = GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : ThreadOf(fg);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try {
                SetForegroundWindow(expected);
                BringWindowToTop(expected);
                SetFocus(expected);
            } finally {
                if (attached) AttachThreadInput(me, fgThread, false);
            }
            Thread.Sleep(60);
        }
        return GetForegroundWindow() == expected;
    }

    static uint ThreadOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }

    static bool Owned(uint pid, int x, int y) {
        var hit = WindowFromPoint(new POINT { X=x, Y=y });
        if (ClassOf(hit) == "WinttySplash") return false;
        return PidOf(hit) == pid;
    }

    // One real click on the app's own pixels: the XAML island drops
    // synthesized keys until one has landed. Probed before and after, so a
    // flyout arriving mid-settle cannot take the click.
    public static bool Click(uint pid, int x, int y) {
        if (!Owned(pid, x, y)) return false;
        if (!SetCursorPos(x, y)) return false;
        Thread.Sleep(60);
        if (!Owned(pid, x, y)) return false;
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(200);
        return true;
    }

    public static bool RightClick(uint pid, int x, int y) {
        if (!Owned(pid, x, y)) return false;
        if (!SetCursorPos(x, y)) return false;
        Thread.Sleep(60);
        if (!Owned(pid, x, y)) return false;
        mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(350);
        return true;
    }

    // Absolute SendInput moves, because WinUI raises PointerEntered for
    // those and not for mouse_event or a posted message - and the drag
    // machine is entered through exactly that event.
    static INPUT MoveInput(int x, int y) {
        int sw = Math.Max(1, GetSystemMetrics(0));
        int sh = Math.Max(1, GetSystemMetrics(1));
        var i = new INPUT { type = 0 };
        i.U.mi = new MOUSEINPUT {
            dx = (int)(x * 65535.0 / Math.Max(1, sw - 1)),
            dy = (int)(y * 65535.0 / Math.Max(1, sh - 1)),
            dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
        };
        return i;
    }

    public static bool Hover(int x, int y) {
        if (!SetCursorPos(x, y)) return false;
        Send(new INPUT[] { MoveInput(x, y) });
        return true;
    }

    // The drag stream: press at a probed point, moves as absolute
    // SendInput, release. Every event is a real input event, so the whole
    // gesture reads to the app as one pointer.
    public static bool DragPress(uint pid, int x, int y) {
        if (!Owned(pid, x, y)) return false;
        if (!SetCursorPos(x, y)) return false;
        Thread.Sleep(60);
        if (!Owned(pid, x, y)) return false;
        Send(new INPUT[] { MoveInput(x, y) });
        Thread.Sleep(40);
        Send(new INPUT[] {
            new INPUT { type = 0, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } } }
        });
        Thread.Sleep(90);
        return true;
    }

    public static bool DragMove(int x, int y) {
        Send(new INPUT[] { MoveInput(x, y) });
        return true;
    }

    public static bool DragRelease() {
        Send(new INPUT[] {
            new INPUT { type = 0, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } } }
        });
        Thread.Sleep(150);
        return true;
    }
}
'@

# The desktop runs mixed-DPI monitors and the product's restored window
# geometry can span them: a DPI-unaware harness reads virtualized rects
# while SendInput and WindowFromPoint answer in other spaces, and the arm
# point lands where no window of ours is. Opt in to per-monitor physical
# coordinates (-4 = PER_MONITOR_AWARE_V2) so every read and every
# injection share one space.
[void][MzTD]::SetProcessDpiAwarenessContext([IntPtr](-4))

$UIA = [System.Windows.Automation.AutomationElement]
$TREE = [System.Windows.Automation.TreeScope]::Descendants
$CTRL = [System.Windows.Automation.ControlType]

# The machine commits a crossing only when the dragged center passes the
# neighbour's center PLUS this token (TabDragReorder.Evaluate's strict
# inequality), so a scripted drag that stops AT the boundary row's center
# never arms a pin. Read from the product's own source rather than
# hard-coded: the boundary legs overshoot by it, and a token change must
# move the legs with it, not silently fall behind.
$script:HysteresisPx = -1
$motionCs = Join-Path $PSScriptRoot '../Ghostty.Core/Tabs/TabStripMotion.cs'
$motionSrc = Get-Content $motionCs -Raw
if ($motionSrc -match 'CrossingHysteresisPx\s*=\s*(\d+)') {
    $script:HysteresisPx = [int]$Matches[1]
}
if ($script:HysteresisPx -le 0) {
    throw 'HARVEST_MISS: could not read CrossingHysteresisPx from TabStripMotion.cs'
}

# The seeded RNG. The run is a scripted set of drags, not a random walk -
# the seed perturbs the grab point, the step count and the dwells inside
# each gesture, so a replay exercises a slightly different pointer path
# against the same assertions. Every draw is recorded in result.json.
$rng = [System.Random]::new($Seed)
$script:Draws = [System.Collections.Generic.List[object]]::new()
function Draw([string]$What, [int]$Min, [int]$Max) {
    $v = $rng.Next($Min, $Max + 1)
    $script:Draws.Add([ordered]@{ what = $What; value = $v })
    return $v
}

function Get-WinUiWindows([uint32]$ProcId) {
    $hits = [System.Collections.Generic.List[object]]::new()
    $cb = [MzTD+EnumProc]{
        param($h, $lp)
        [uint32]$o = 0; [void][MzTD]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -ne $ProcId -or -not [MzTD]::IsWindowVisible($h)) { return $true }
        if ([MzTD]::ClassOf($h) -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [MzTD]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64 = $hwnd64; Title = [MzTD]::TitleOf($h); Area = ($rc.W * $rc.Hh) })
        return $true
    }
    [void][MzTD]::EnumWindows($cb, [IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending
}

function Test-SplashVisible([int]$ProcId) {
    $script:splashSeen = $false
    $cb = [MzTD+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner = 0; [void][MzTD]::GetWindowThreadProcessId($hwnd, [ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        if ([MzTD]::ClassOf($hwnd) -eq 'WinttySplash' -and [MzTD]::IsWindowVisible($hwnd)) { $script:splashSeen = $true }
        return $true
    }
    [void][MzTD]::EnumWindows($cb, [IntPtr]::Zero)
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
    if (-not $got) { throw 'HARVEST_MISS: no WinUI hwnd' }
    $dl = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $dl) {
        $proc.Refresh(); if ($proc.HasExited) { throw 'PRODUCT_FAIL during splash' }
        if (Test-SplashVisible $proc.Id) { Start-Sleep -Milliseconds 200; continue }
        Start-Sleep -Milliseconds 900
        if (-not (Test-SplashVisible $proc.Id)) { return $got }
    }
    throw 'HARVEST_MISS: splash never dropped'
}

function Get-UiaRoot([int64]$Hwnd64) { return $UIA::FromHandle([MzTD]::P($Hwnd64)) }

function Find-ById($root, [string]$Id) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $Id)
    return $root.FindFirst($TREE, $cond)
}

function Find-ByNameRetry($root, [string]$Name, [int]$ms = 2200) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $Name)
    $dl = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $dl) {
        $el = $root.FindFirst($TREE, $cond)
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 90
        $root = Get-UiaRoot $script:MainHwnd64
    }
    return $null
}

# One row per tab, ordered the way the strip paints them. The vertical
# header rides this list too - it is a list item named after the group -
# and each row carries its ItemStatus beside the name, because pinned and
# collapsed are state the oracles read and never identity. Rows with no
# area are dropped: a folded group's members are hidden in place, so their
# absence from this list IS the fold, and nothing downstream should have to
# distinguish "hidden" from "gone".
function Get-StripRows([bool]$Vertical) {
    $root = Get-UiaRoot $script:MainHwnd64
    $hostId = if ($Vertical) { 'NavView' } else { 'TabViewControl' }
    # The UIA tree hiccups under harness load; a bounded retry keeps a
    # transient miss from killing an otherwise green run.
    $stripEl = $null
    for ($try = 0; $try -lt 3 -and $null -eq $stripEl; $try++) {
        $stripEl = Find-ById $root $hostId
        if ($null -eq $stripEl) { Start-Sleep -Milliseconds 250 }
    }
    if ($null -eq $stripEl) { throw "HARVEST_MISS: no strip with AutomationId $hostId" }
    $ct = if ($Vertical) { $CTRL::ListItem } else { $CTRL::TabItem }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $ct)
    $found = $stripEl.FindAll($TREE, $cond)
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($el in $found) {
        $r = $el.Current.BoundingRectangle
        if ($r.Width -le 0 -or $r.Height -le 0) { continue }
        $rows.Add([pscustomobject]@{
            El     = $el
            Name   = $el.Current.Name
            Status = $el.Current.ItemStatus
            Rect   = $r
        })
    }
    if ($rows.Count -eq 0) { throw "HARVEST_MISS: no rows under $hostId" }
    $sorted = if ($Vertical) { $rows | Sort-Object { $_.Rect.Y } } else { $rows | Sort-Object { $_.Rect.X } }
    return @($sorted)
}

function Get-Order([bool]$Vertical) { return @(Get-StripRows $Vertical | ForEach-Object { $_.Name }) }

function Get-Row([bool]$Vertical, [string]$Name) {
    $row = Get-StripRows $Vertical | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
    if ($null -eq $row) { throw "HARVEST_MISS: no visible row named '$Name' in the $(if ($Vertical) {'vertical'} else {'horizontal'}) strip" }
    return $row
}

# The one lookup that wants hidden rows: a collapsed chip or a header keeps
# its name and its ItemStatus while the fold hides the tabs under it, and
# the chip round-trip has to read that state. Presence is still area-gated -
# an element the strip replaced reports no rect and counts as gone.
function Find-RowAny([bool]$Vertical, [string]$Name) {
    $root = Get-UiaRoot $script:MainHwnd64
    $hostId = if ($Vertical) { 'NavView' } else { 'TabViewControl' }
    $stripEl = Find-ById $root $hostId
    if ($null -eq $stripEl) { return $null }
    foreach ($ct in @($CTRL::TabItem, $CTRL::ListItem)) {
        $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $ct)
        $found = $stripEl.FindAll($TREE, $cond)
        foreach ($el in $found) {
            if ($el.Current.Name -ne $Name) { continue }
            $r = $el.Current.BoundingRectangle
            if ($r.Width -le 0 -or $r.Height -le 0) { continue }
            return [pscustomobject]@{ Name = $Name; Status = $el.Current.ItemStatus; Rect = $r }
        }
    }
    return $null
}

function Wait-Order([bool]$Vertical, [string[]]$Want, [int]$seconds = 6) {
    $dl = (Get-Date).AddSeconds($seconds)
    $got = @()
    while ((Get-Date) -lt $dl) {
        $got = Get-Order $Vertical
        if ($got.Count -eq $Want.Count) {
            $same = $true
            for ($i = 0; $i -lt $Want.Count; $i++) { if ($got[$i] -ne $Want[$i]) { $same = $false; break } }
            if ($same) { return $got }
        }
        Start-Sleep -Milliseconds 150
    }
    throw "PRODUCT_FAIL: $(if ($Vertical) {'vertical'} else {'horizontal'}) order is [$($got -join ', ')], expected [$($Want -join ', ')]"
}

function Assert-RowStatus([bool]$Vertical, [string]$Name, [scriptblock]$Ok, [string]$What) {
    $row = Get-Row $Vertical $Name
    if (-not (& $Ok $row.Status)) {
        throw "PRODUCT_FAIL: row '$Name' $What but ItemStatus is '$($row.Status)'"
    }
}

function Select-Row([object]$Row, [uint32]$ProcId) {
    try {
        $pat = $Row.El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $pat.Select()
        Start-Sleep -Milliseconds 350
        return
    } catch { }
    $r = $Row.Rect
    if (-not [MzTD]::Click($ProcId, [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))) {
        throw "HARVEST_MISS: could not click row '$($Row.Name)'"
    }
    Start-Sleep -Milliseconds 350
}

# Clicking the terminal is what makes the app accept a chord at all, and ANY
# chrome interaction un-arms it again - so this runs before every chord.
function Enable-Chords([uint32]$ProcId) {
    $rc = [MzTD]::RectOf($script:MainHwnd64)
    if ($null -eq $rc) { throw 'HARVEST_MISS: window rect for arming' }
    # The launch leaves the window wherever the desktop's z-order put it,
    # and Owned reads the window under the cursor -- so take the
    # foreground first (best-effort; a refused steal must not veto a
    # click that would land), then walk inward from the offsets that have
    # historically been clear of chrome, taking the first point the
    # window owns. The center is the last point rect skew can take from
    # us.
    [void][MzTD]::Focus([MzTD]::P($script:MainHwnd64))
    $candidates = @(
        @{ fx = 0.50; fy = 0.50 },
        @{ fx = 0.62; fy = 0.55 },
        @{ fx = 0.35; fy = 0.50 },
        @{ fx = 0.50; fy = 0.30 }
    )
    $armed = $false
    foreach ($c in $candidates) {
        $x = [int]($rc.L + $rc.W * $c.fx)
        $y = [int]($rc.T + $rc.Hh * $c.fy)
        if ([MzTD]::Click($ProcId, $x, $y)) { $armed = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $armed) {
        throw 'HARVEST_MISS: could not click the terminal to arm input'
    }
    Start-Sleep -Milliseconds 200
}

function Add-Tab([int64]$Hwnd64, [uint32]$ProcId) {
    Enable-Chords $ProcId
    if (-not [MzTD]::Chord([MzTD]::P($Hwnd64), @([MzTD]::VK_CONTROL), [MzTD]::VK_T)) {
        throw 'FOREGROUND_MISS: could not take the foreground to open a tab'
    }
    Start-Sleep -Milliseconds 800
}

function Set-RowTitle([bool]$Vertical, [object]$Row, [string]$Want, [uint32]$ProcId, [int64]$Hwnd64) {
    Select-Row $Row $ProcId
    Enable-Chords $ProcId
    if (-not [MzTD]::TypeLine([MzTD]::P($Hwnd64), "title $Want")) {
        throw 'FOREGROUND_MISS: could not take the foreground to title a tab'
    }
    $dl = (Get-Date).AddSeconds(6)
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 250
        $named = @(Get-StripRows $Vertical | Where-Object { $_.Name -eq $Want })
        if ($named.Count -eq 1) { return }
    }
    throw ("HARVEST_MISS: '$Want' never showed up as a tab title, so the shell " +
           'is not reporting titles and tabs cannot be told apart')
}

function Invoke-MenuItem([string]$Name, [uint32]$ProcId, [string]$What) {
    $el = Find-ByNameRetry (Get-UiaRoot $script:MainHwnd64) $Name 2500
    if ($null -eq $el) { throw "HARVEST_MISS: context menu item '$Name' ($What)" }
    # Task #32: log the RESOLUTION -- a menu resolved by name after the pin
    # legs reordered the strip is the prime mis-addressing suspect, and the
    # log must name exactly what was resolved before anything is invoked.
    Write-Host ("DIAG menu resolve '{0}' -> AutoId='{1}' class={2} ({3})" -f
        $Name, $el.Current.AutomationId, $el.Current.ClassName, $What)
    try {
        $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pat.Invoke()
        Write-Host ("DIAG menu invoked '{0}' via InvokePattern" -f $Name)
        Start-Sleep -Milliseconds 450
        return
    } catch { }
    $r = $el.Current.BoundingRectangle
    if (-not [MzTD]::Click($ProcId, [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))) {
        throw "HARVEST_MISS: click on menu item '$Name'"
    }
    Start-Sleep -Milliseconds 450
}

function Show-RowMenu([bool]$Vertical, [string]$Name, [uint32]$ProcId, [double]$xBias = 0.4) {
    $row = Get-Row $Vertical $Name
    $r = $row.Rect
    $x = [int]($r.X + $r.Width * $xBias)
    $y = [int]($r.Y + $r.Height / 2)
    if (-not [MzTD]::RightClick($ProcId, $x, $y)) {
        throw "HARVEST_MISS: right-click refused on '$Name' at $x,$y"
    }
}

# One drag gesture, waypointed and timed. The seed draws the grab jitter,
# the step count and the per-step dwell, so the pointer path differs run to
# run while the assertion does not.
function Invoke-Drag([uint32]$ProcId, [object]$FromRect, [double]$FromBias, [int]$ToX, [int]$ToY, [int]$HoldMs = 0) {
    $fromX = [int]($FromRect.X + $FromRect.Width * $FromBias) + (Draw 'grab jitter x' -2 2)
    $fromY = [int]($FromRect.Y + $FromRect.Height / 2) + (Draw 'grab jitter y' -2 2)
    $steps = (Draw 'drag steps' 7 11)
    $stepMs = (Draw 'step dwell ms' 24 40)
    if (-not [MzTD]::DragPress($ProcId, $fromX, $fromY)) {
        throw "HARVEST_MISS: drag press refused at $fromX,$fromY"
    }
    for ($i = 1; $i -le $steps; $i++) {
        $x = $fromX + [int](($ToX - $fromX) * $i / $steps)
        $y = $fromY + [int](($ToY - $fromY) * $i / $steps)
        [void][MzTD]::DragMove($x, $y)
        Start-Sleep -Milliseconds $stepMs
    }
    if ($HoldMs -gt 0) { Start-Sleep -Milliseconds $HoldMs }
    [void][MzTD]::DragRelease()
    # The grab threshold is 4px and the drop's own work is async; this wait
    # lets the gesture finish (flight, glide or nothing) before anything
    # reads the strip or the trace.
    Start-Sleep -Milliseconds 750
}

function Toggle-Layout([int64]$Hwnd64, [uint32]$ProcId) {
    Enable-Chords $ProcId
    if (-not [MzTD]::Chord([MzTD]::P($Hwnd64), @([MzTD]::VK_CONTROL, [MzTD]::VK_SHIFT), [MzTD]::VK_OEM_COMMA)) {
        throw 'FOREGROUND_MISS: layout chord not sent'
    }
    Start-Sleep -Milliseconds 1400
}

function Get-HorizStripWidth($root) {
    $list = Find-ById $root 'TabListView'
    if ($null -eq $list) { $list = Find-ById $root 'TabList' }
    if ($null -eq $list) { return 0 }
    $w = $list.Current.BoundingRectangle.Width
    if ([double]::IsNaN($w)) { return 0 }
    return [int]$w
}

function Get-LayoutMode($root) {
    $nav = Find-ById $root 'NavView'
    $navW = 0
    if ($null -ne $nav) {
        $navW = $nav.Current.BoundingRectangle.Width
        if ([double]::IsNaN($navW)) { $navW = 0 }
    }
    if ((Get-HorizStripWidth $root) -gt 120) { return 'horizontal' }
    $toggle = Find-ById $root 'PaneToggleButton'
    if ($navW -ge 40 -and $null -ne $toggle) { return 'vertical' }
    return 'unknown'
}

function Wait-LayoutMode([string]$want, [int]$seconds = 8) {
    $dl = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $dl) {
        if ((Get-LayoutMode (Get-UiaRoot $script:MainHwnd64)) -eq $want) { return }
        Start-Sleep -Milliseconds 150
    }
    throw "HARVEST_MISS: layout never became $want (is $(Get-LayoutMode (Get-UiaRoot $script:MainHwnd64)))"
}

function Expand-Sidebar([uint32]$ProcId) {
    $root = Get-UiaRoot $script:MainHwnd64
    if ((Get-LayoutMode $root) -ne 'vertical') { return }
    # Already wide enough to grab rows from: do not touch the toggle, whose
    # only other direction is to fold the pane this run needs.
    $nav = Find-ById $root 'NavView'
    if ($null -ne $nav) {
        $w = $nav.Current.BoundingRectangle.Width
        if (-not [double]::IsNaN($w) -and $w -ge 200) { return }
    }
    $el = Find-ById $root 'PaneToggleButton'
    if ($null -eq $el) { $el = Find-ByNameRetry $root 'Expand sidebar' 1200 }
    if ($null -eq $el) { throw 'HARVEST_MISS: PaneToggleButton' }
    try {
        $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pat.Invoke()
    } catch {
        $r = $el.Current.BoundingRectangle
        if (-not [MzTD]::Click($ProcId, [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))) {
            throw 'HARVEST_MISS: pane toggle click'
        }
    }
    Start-Sleep -Milliseconds 700
}

# Any visible element named $Name whose control type is Text. The run label
# carries no automation properties of its own on purpose, but its title
# TextBlock is exactly this, and nothing else in the tree is a Text named
# after a group while the group is expanded - the tabs name themselves from
# their titles, the header is a list item and the chip is a tab item, not
# Text.
function Find-TextNamed([string]$Name) {
    $nameCond = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $Name)
    $ctCond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $CTRL::Text)
    $both = New-Object System.Windows.Automation.AndCondition($nameCond, $ctCond)
    $found = (Get-UiaRoot $script:MainHwnd64).FindAll($TREE, $both)
    $out = @()
    foreach ($el in $found) {
        $r = $el.Current.BoundingRectangle
        if ($r.Width -le 0 -or $r.Height -le 0) { continue }
        $out += $el
    }
    return @($out)
}

# ---- the drag trace oracle -------------------------------------------------

# Sessions are split on DRAG begin; within one, commits, drops, pin drops,
# flights and settles are counted, and every ghosts=N is filed as the end's
# own count, mid-drag (glide lines can legitimately report a superseded
# batch's entries still riding the newer batch - not a leak), or after the
# end, where anything above zero IS a leak.
function Read-TraceSessions([string]$Path) {
    $lines = if (Test-Path $Path) { @(Get-Content $Path) } else { @() }
    $sessions = [System.Collections.Generic.List[object]]::new()
    $current = $null
    foreach ($line in $lines) {
        if ($line -like 'DRAG begin*') {
            if ($null -ne $current) { $sessions.Add($current) }
            $motion = if ($line -match 'motion=(\w+)') { $Matches[1] } else { 'unknown' }
            $current = [ordered]@{
                begin = $line; motion = $motion; end = $null; canceled = $false
                commits = 0; drops = 0; pinDrops = 0; flights = 0
                settleAfterDrop = $false; dropAnswered = $false
                postEndGhosts = [System.Collections.Generic.List[int]]::new()
                midGhosts = [System.Collections.Generic.List[int]]::new()
                endGhosts = -1
                raw = [System.Collections.Generic.List[string]]::new()
            }
        }
        if ($null -eq $current) { continue }
        [void]$current.raw.Add($line)
        if ($line -like 'DRAG commit*') { $current.commits++ }
        elseif ($line -like 'DRAG drop*') { $current.drops++; $current.dropAnswered = $false }
        elseif ($line -like 'DRAG pin drop*') { $current.pinDrops++; $current.dropAnswered = $false }
        elseif ($line -like 'DRAG flight start*') { $current.flights++ }
        elseif ($line -like 'DRAG settle*') { $current.settleAfterDrop = $true; $current.dropAnswered = $true }
        elseif ($line -like 'DRAG cancel*') { $current.canceled = $true }
        elseif ($line -match 'ghosts=(\d+)') {
            $n = [int]$Matches[1]
            if ($line -like 'DRAG end*') {
                $current.end = $line; $current.endGhosts = $n; $current.dropAnswered = $true
            } elseif ($null -ne $current.end) {
                [void]$current.postEndGhosts.Add($n)
            } else {
                [void]$current.midGhosts.Add($n)
            }
        }
    }
    if ($null -ne $current) { $sessions.Add($current) }
    return $sessions
}

# Every session must pair its begin with an end, must not leak at or after
# the end, and must answer its drop the way its motion flag says: a settle
# when motion is on (the flight for a pin drop), and NO settle when motion
# is off - the gate's cut polarity, read out of the product's own log.
function Assert-TraceSession([object]$Session, [string]$Label, [int]$MinCommits, [string]$WantMotion) {
    if ($null -eq $Session) { throw "PRODUCT_FAIL: no trace session recorded for $Label" }
    if ($Session.canceled) { throw "PRODUCT_FAIL: $Label drag was canceled, not completed" }
    if ($null -eq $Session.end) { throw "PRODUCT_FAIL: $Label drag never ended (begin: $($Session.begin))" }
    if ($Session.endGhosts -ne 0) {
        throw "PRODUCT_FAIL: $Label leaked $($Session.endGhosts) motion(s) at end: $($Session.end)"
    }
    if ($WantMotion -ne '' -and $Session.motion -ne $WantMotion) {
        throw "PRODUCT_FAIL: $Label ran with motion=$($Session.motion), expected $WantMotion - the gate did not see what the harness set"
    }
    if ($Session.commits -lt $MinCommits) {
        throw "PRODUCT_FAIL: $Label committed $($Session.commits) crossing(s), expected at least $MinCommits"
    }
    foreach ($g in $Session.postEndGhosts) {
        if ($g -gt 0) { throw "PRODUCT_FAIL: $Label leaked $g motion(s) after the drag ended" }
    }
    if ($Session.drops -gt 0 -or $Session.pinDrops -gt 0) {
        if ($Session.motion -eq 'on' -and -not $Session.dropAnswered) {
            throw "PRODUCT_FAIL: $Label dropped and nothing settled, flew or ended after it - the release was never answered"
        }
        if ($Session.motion -eq 'on' -and $Session.pinDrops -eq 0 -and -not $Session.settleAfterDrop) {
            throw "PRODUCT_FAIL: $Label motion-on drop was never settled"
        }
        if ($Session.motion -eq 'off' -and $Session.settleAfterDrop) {
            throw "PRODUCT_FAIL: $Label ran motion-off but its drop was still settled - the gate's cut is not total"
        }
    }
}

# ---- run -------------------------------------------------------------------

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$tracePath = Join-Path $OutDir 'tabdrag.trace'
$tempXdg = Join-Path $env:TEMP "wintty-fuzz-tabdrag-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path (Join-Path $tempXdg 'wintty') | Out-Null
@'
windows-single-instance = true
window-save-state = never
windows-settings-ui = true
vertical-tabs = true
window-theme = wintty
theme = Catppuccin Mocha
vertical-tabs-hover-expand = false
'@ | Set-Content (Join-Path $tempXdg 'wintty\config.wintty') -Encoding utf8

$script:MainHwnd64 = 0
$origXdgSet = Test-Path Env:XDG_CONFIG_HOME
$origXdg = if ($origXdgSet) { $env:XDG_CONFIG_HOME } else { $null }
$traceSet = Test-Path Env:WINTTY_TABDRAG_TRACE
$origTrace = if ($traceSet) { $env:WINTTY_TABDRAG_TRACE } else { $null }
$proc = $null
$guardSnapshot = Join-Path $OutDir 'env-snapshot.json'
$script:Phases = [System.Collections.Generic.List[object]]::new()
$script:MotionLegs = [System.Collections.Generic.List[object]]::new()
$script:Orders = [ordered]@{}
$script:nextName = $null
    # Task #32: the expected VISIBLE row count after each phase, asserted
    # between phases -- a phase that starts with the wrong count fails here
    # with the count in the error.
    function Assert-TabCount([bool]$Vertical, [int]$want, [string]$what) {
        $order = Get-Order $Vertical
        if ($order.Count -ne $want) {
            throw ("COUNT_MISS: {0} expects {1} visible rows, sees {2} [{3}]" -f
                $what, $want, $order.Count, ($order -join ','))
        }
    }

    function Add-Phase([string]$name, [scriptblock]$body) {
    try {
        & $body
        $script:Phases.Add([ordered]@{ name = $name; ok = $true })
        Write-Host "OK $name" -ForegroundColor Green
    } catch {
        $script:Phases.Add([ordered]@{ name = $name; ok = $false; error = $_.Exception.Message })
        throw
    }
    # Task #32: a phase that kills the app must die HERE with the exit
    # code and the phase name, not three legs later against a dead
    # process.
    if ($proc -and $proc.HasExited) {
        throw ("APP_EXIT: the app exited during phase '{0}' (code {1})" -f
            $name, $proc.ExitCode)
    }
}

# Above the try, so the refusal survives a finally that would otherwise bind
# a null stamp to a mandatory parameter.
Assert-NoWintty -Context 'The tab drag fuzz'
$script:WinttyStamp = Get-WinttyLaunchStamp
$script:FatalWasProduct = $null

try {
    if (-not (Test-Path $ExePath)) { throw "missing exe: $ExePath" }
    $env:XDG_CONFIG_HOME = $tempXdg
    $env:WINTTY_TABDRAG_TRACE = $tracePath
    $proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path -Parent (Resolve-Path $ExePath))
    $pid32 = [uint32]$proc.Id
    $main = Wait-Ready $proc
    $script:MainHwnd64 = [int64]$main.Hwnd64
    [MzTD]::TargetHwnd = $script:MainHwnd64
    $hwnd64 = $script:MainHwnd64
    [void][MzTD]::MoveWindow([MzTD]::P($hwnd64), 60, 60, 1280, 820, $true)
    Start-Sleep -Milliseconds 600
    Write-Host "hwnd=$hwnd64 pid=$pid32 trace=$tracePath seed=$Seed"

    $V = $true
    $H = $false

    Add-Phase 'launch-and-seed' {
        Expand-Sidebar $pid32
        Enable-Chords $pid32
        for ($n = 1; $n -le 4; $n++) { Add-Tab $hwnd64 $pid32 }
        $names = @('fuzzdrag-1', 'fuzzdrag-2', 'fuzzdrag-3', 'fuzzdrag-4', 'fuzzdrag-5')
        for ($pass = 0; $pass -lt 4; $pass++) {
            $plain = @(Get-StripRows $V | Where-Object { $_.Name -notlike 'fuzzdrag-*' })
            if ($plain.Count -eq 0) { break }
            foreach ($row in $plain) {
                if ($null -eq $script:nextName) { $script:nextName = $names[0] }
                Set-RowTitle $V $row $script:nextName $pid32 $hwnd64
                $next = [array]::IndexOf($names, $script:nextName) + 1
                $script:nextName = if ($next -lt $names.Count) { $names[$next] } else { $null }
            }
        }
        $rows = Get-StripRows $V
        $untitled = @($rows | Where-Object { $_.Name -notlike 'fuzzdrag-*' })
        if ($untitled.Count -gt 0) { throw 'HARVEST_MISS: tabs kept arriving untitled' }
        $dupes = @($rows | Group-Object Name | Where-Object { $_.Count -gt 1 })
        if ($dupes.Count -gt 0) {
            throw "HARVEST_MISS: $($dupes[0].Count) tabs titled '$($dupes[0].Name)', so a title cannot stand for identity"
        }
        $script:Orders.seed = (Get-Order $V) -join ','
        if ($script:Orders.seed -ne ($names -join ',')) {
            throw "PRODUCT_FAIL: seeded order is [$($script:Orders.seed)], expected [$($names -join ',')]"
        }
    }

    # 1. The vertical reorder, motion on. Row 2 dragged down past row 3.
    Add-Phase 'vertical-reorder-motion-on' {
        $row = Get-Row $V 'fuzzdrag-2'
        $below = Get-Row $V 'fuzzdrag-3'
        $toY = [int]($below.Rect.Y + $below.Rect.Height * 0.85)
        Invoke-Drag $pid32 $row.Rect 0.35 $row.Rect.X $toY
        Wait-Order $V @('fuzzdrag-1', 'fuzzdrag-3', 'fuzzdrag-2', 'fuzzdrag-4', 'fuzzdrag-5')
        $script:Orders.verticalOn = (Get-Order $V) -join ','
    }

    # The inverse drag puts the order back, so the motion-off leg runs the
    # SAME gesture over the SAME start order and the two final orders can
    # be asserted identical.
    Add-Phase 'vertical-reorder-inverse' {
        $row = Get-Row $V 'fuzzdrag-2'
        $above = Get-Row $V 'fuzzdrag-3'
        $toY = [int]($above.Rect.Y + $above.Rect.Height * 0.15)
        Invoke-Drag $pid32 $row.Rect 0.35 $row.Rect.X $toY
        Wait-Order $V @('fuzzdrag-1', 'fuzzdrag-2', 'fuzzdrag-3', 'fuzzdrag-4', 'fuzzdrag-5')
    }

    # 2. The same gesture with Windows' animations off. The OS toggle goes
    # through the env guard; the trace's motion=off line is what proves the
    # app actually saw it, and the identical final order is the point.
    Add-Phase 'vertical-reorder-motion-off' {
        if (-not (Save-EnvSnapshot -Path $guardSnapshot)) { throw 'HARVEST_MISS: env guard snapshot failed' }
        $before = Get-SpiUint ([uint32]0x1042)
        Set-SpiUint ([uint32]0x1043) ([uint32]0)
        $after = Get-SpiUint ([uint32]0x1042)
        if ($after -ne 0) { throw "HARVEST_MISS: animation toggle read back $after, not 0" }
        Write-Host "animations: $before -> 0 (read back)"
        try {
            $row = Get-Row $V 'fuzzdrag-2'
            $below = Get-Row $V 'fuzzdrag-3'
            $toY = [int]($below.Rect.Y + $below.Rect.Height * 0.85)
            Invoke-Drag $pid32 $row.Rect 0.35 $row.Rect.X $toY
            Wait-Order $V @('fuzzdrag-1', 'fuzzdrag-3', 'fuzzdrag-2', 'fuzzdrag-4', 'fuzzdrag-5')
            $script:Orders.verticalOff = (Get-Order $V) -join ','
            if ($script:Orders.verticalOff -ne $script:Orders.verticalOn) {
                throw "PRODUCT_FAIL: motion-off landed [$($script:Orders.verticalOff)] but motion-on landed [$($script:Orders.verticalOn)] - the gate changed the outcome, not just the animation"
            }
            $script:MotionLegs.Add([ordered]@{
                gesture = 'fuzzdrag-2 down past fuzzdrag-3'
                motionOn = [ordered]@{ order = $script:Orders.verticalOn }
                motionOff = [ordered]@{ order = $script:Orders.verticalOff }
                identical = ($script:Orders.verticalOff -eq $script:Orders.verticalOn)
            })
        }
        finally {
            Restore-EnvSnapshot -Path $guardSnapshot
            Write-Host "animations restored to $(Get-SpiUint ([uint32]0x1042)) (read-back verified by the guard)"
        }
    }

    # 3. Pin boundary. Row 1 is pinned through its own menu first, so the
    # zone exists; then the top body row crosses into the zone and comes
    # back out (must stay unpinned, order untouched), and finally crosses
    # and releases inside it (must pin, with the flight in the trace).
    Add-Phase 'pin-zone-setup' {
        Show-RowMenu $V 'fuzzdrag-1' $pid32
        Invoke-MenuItem 'Pin Tab' $pid32 'pin fuzzdrag-1'
        Start-Sleep -Milliseconds 500
        Assert-RowStatus $V 'fuzzdrag-1' { param($s) $s -match 'Pinned' } 'was pinned'
        $order = Get-Order $V
        if ($order[0] -ne 'fuzzdrag-1') { throw "PRODUCT_FAIL: pinned row is not first: [$($order -join ',')]" }
    }

    Add-Phase 'pin-boundary-out' {
        $row = Get-Row $V 'fuzzdrag-3'
        $zone = Get-Row $V 'fuzzdrag-1'
        $homeY = [int]($row.Rect.Y + $row.Rect.Height / 2)
        $fromX = [int]($row.Rect.X + $row.Rect.Width * 0.35)
        $fromY = [int]($row.Rect.Y + $row.Rect.Height / 2)
        $zoneY = [int]($zone.Rect.Y + $zone.Rect.Height / 2)
        # The up-path must OVERSHOOT the zone row's center by the
        # hysteresis token plus margin: the machine's crossing fires only
        # when the dragged center passes the neighbour's center PLUS
        # CrossingHysteresisPx, so stopping AT the center never arms the
        # pin and the leg would exercise nothing.
        $overshootY = $zoneY - ($script:HysteresisPx + 12)
        if (-not [MzTD]::DragPress($pid32, $fromX, $fromY)) { throw 'HARVEST_MISS: drag press refused (boundary leg)' }
        for ($i = 1; $i -le 8; $i++) {
            [void][MzTD]::DragMove($fromX, $fromY + [int](($overshootY - $fromY) * $i / 8))
            Start-Sleep -Milliseconds 30
        }
        # In the zone long enough for the pin preview to be live before the
        # pointer leaves it again.
        Start-Sleep -Milliseconds 500
        for ($i = 1; $i -le 8; $i++) {
            [void][MzTD]::DragMove($fromX, $overshootY + [int](($homeY - $overshootY) * $i / 8))
            Start-Sleep -Milliseconds 30
        }
        # Carry one full row BELOW the zone edge: the release must be
        # unambiguously outside the shelf bounds for the release-classified
        # unpin to fire (a release ON the edge reads as in-zone).
        [void][MzTD]::DragMove($fromX, $homeY + 40)
        Start-Sleep -Milliseconds 120
        [void][MzTD]::DragRelease()
        Start-Sleep -Milliseconds 750
        # Release-classified grammar: the row unpins and lands at the body
        # slot under the release point -- one row deeper than its pre-leg
        # home (the return carried a full row past the zone edge).
        Wait-Order $V @('fuzzdrag-1', 'fuzzdrag-2', 'fuzzdrag-3', 'fuzzdrag-4', 'fuzzdrag-5')
        Assert-RowStatus $V 'fuzzdrag-3' { param($s) $s -notmatch 'Pinned' } 'crossed into the pin zone and back out, so it must not be pinned'
        $script:Orders.boundaryOut = (Get-Order $V) -join ','
    }

    Add-Phase 'pin-legs-count' {
        Assert-TabCount $true 5 'after the pin boundary legs (5 tabs, none grouped)'
    }

    Add-Phase 'pin-boundary-drop' {
        $row = Get-Row $V 'fuzzdrag-3'
        $zone = Get-Row $V 'fuzzdrag-1'
        $fromX = [int]($row.Rect.X + $row.Rect.Width * 0.35)
        $fromY = [int]($row.Rect.Y + $row.Rect.Height / 2)
        $toY = [int]($zone.Rect.Y + $zone.Rect.Height / 2)
        # Same overshoot discipline as boundary-out: arm the crossing past
        # the zone row's center by hysteresis plus margin, then come back
        # to the zone center so the RELEASE lands provably inside the
        # shelf bounds (the in-zone landing keeps the pin).
        $overshootY = $toY - ($script:HysteresisPx + 12)
        if (-not [MzTD]::DragPress($pid32, $fromX, $fromY)) { throw 'HARVEST_MISS: drag press refused (boundary drop leg)' }
        for ($i = 1; $i -le 8; $i++) {
            [void][MzTD]::DragMove($fromX, $fromY + [int](($overshootY - $fromY) * $i / 8))
            Start-Sleep -Milliseconds 30
        }
        Start-Sleep -Milliseconds 400
        for ($i = 1; $i -le 4; $i++) {
            [void][MzTD]::DragMove($fromX, $overshootY + [int](($toY - $overshootY) * $i / 4))
            Start-Sleep -Milliseconds 30
        }
        Start-Sleep -Milliseconds 300
        [void][MzTD]::DragRelease()
        Start-Sleep -Milliseconds 750
        # The crossing's slot IS the drop position: the overshoot drags the
        # row's center past the zone row's, so the row takes the slot it
        # crossed into -- above the neighbour. Append-after-the-pinned was
        # the pre-rung release-pin grammar's answer; the one-grammar
        # contract (pin-in by crossing, at the crossing's slot) puts the
        # crossed row first.
        Wait-Order $V @('fuzzdrag-3', 'fuzzdrag-1', 'fuzzdrag-2', 'fuzzdrag-4', 'fuzzdrag-5')
        Assert-RowStatus $V 'fuzzdrag-3' { param($s) $s -match 'Pinned' } 'was dropped in the pin zone'
        $script:Orders.boundaryDrop = (Get-Order $V) -join ','
    }

    # 4. Group collapse and the drop-on-chip join. A click on the header
    # row folds the run; a drag released on the folded header joins the
    # dropped tab and the group must re-open by itself.
    Add-Phase 'group-legs-count' {
        Assert-TabCount $true 5 'after the group legs'
    }

    Add-Phase 'group-collapse' {
        Show-RowMenu $V 'fuzzdrag-5' $pid32
        Invoke-MenuItem 'New Group With Tab' $pid32 'group fuzzdrag-5'
        Start-Sleep -Milliseconds 500
        $null = Get-Row $V 'New group'
        Assert-RowStatus $V 'fuzzdrag-5' { param($s) $s -match 'Group New group' } 'joined the new group'
        $header = Get-Row $V 'New group'
        if (-not [MzTD]::Click($pid32, [int]($header.Rect.X + $header.Rect.Width / 2), [int]($header.Rect.Y + $header.Rect.Height / 2))) {
            throw 'HARVEST_MISS: header click refused'
        }
        Start-Sleep -Milliseconds 700
        Assert-RowStatus $V 'New group' { param($s) $s -match 'Collapsed' } 'did not collapse on its header click'
        # The folded member is hidden in place, so it must be GONE from the
        # visible rows - its absence is the fold.
        $stillVisible = @(Get-StripRows $V | Where-Object { $_.Name -eq 'fuzzdrag-5' })
        if ($stillVisible.Count -ne 0) {
            throw 'PRODUCT_FAIL: fuzzdrag-5 is still a visible row after its group collapsed'
        }
        $script:Orders.collapsed = (Get-Order $V) -join ','
    }

    Add-Phase 'drop-on-chip-join' {
        $row = Get-Row $V 'fuzzdrag-4'
        $header = Get-Row $V 'New group'
        $toY = [int]($header.Rect.Y + $header.Rect.Height / 2)
        Invoke-Drag $pid32 $row.Rect 0.35 $row.Rect.X $toY 250
        Assert-RowStatus $V 'fuzzdrag-4' { param($s) $s -match 'Group New group' } 'was dropped on the folded group'
        Assert-RowStatus $V 'New group' { param($s) $s -notmatch 'Collapsed' } 'must have re-opened when the drop joined it'
        # The joined order, with the run re-opened by the drop itself: the
        # header back above its two members, all visible.
        Wait-Order $V @('fuzzdrag-1', 'fuzzdrag-3', 'fuzzdrag-2', 'New group', 'fuzzdrag-4', 'fuzzdrag-5')
        $script:Orders.joined = (Get-Order $V) -join ','
    }

    # 5. Horizontal: a TabView drag of a grouped member across its
    # neighbour, judged by the final order again.
    Add-Phase 'horizontal-reorder' {
        Toggle-Layout $hwnd64 $pid32
        Wait-LayoutMode 'horizontal'
        $script:Orders.horizStart = (Get-Order $H) -join ','
        $row = Get-Row $H 'fuzzdrag-4'
        $right = Get-Row $H 'fuzzdrag-5'
        $toX = [int]($right.Rect.X + $right.Rect.Width * 0.85)
        $toY = [int]($row.Rect.Y + $row.Rect.Height / 2)
        Invoke-Drag $pid32 $row.Rect 0.5 $toX $toY
        Wait-Order $H @('fuzzdrag-1', 'fuzzdrag-3', 'fuzzdrag-2', 'fuzzdrag-5', 'fuzzdrag-4')
        $script:Orders.horizReordered = (Get-Order $H) -join ','
    }

    # 6. The run label. Hover first and require the label to SHOW, so the
    # probe is proved able to see it; then a live drag must have it gone
    # for the whole gesture. Then the horizontal collapse round-trip.
    Add-Phase 'run-label-drag-refusal' {
        $member = Get-Row $H 'fuzzdrag-5'
        $r = $member.Rect
        $hx = [int]($r.X + $r.Width * 0.4)
        $hy = [int]($r.Y + $r.Height / 2)
        [void][MzTD]::Hover($hx, $hy)
        Start-Sleep -Milliseconds 900
        if ((Find-TextNamed 'New group').Count -eq 0) {
            throw 'PRODUCT_FAIL: the run label never showed over an expanded grouped member, so the drag assert below would be vacuous'
        }
        Write-Host 'label shown on hover (probe is live)'
        if (-not [MzTD]::DragPress($pid32, $hx, $hy)) { throw 'HARVEST_MISS: label-drag press refused' }
        [void][MzTD]::DragMove($hx + 12, $hy + 14)
        Start-Sleep -Milliseconds 450
        if ((Find-TextNamed 'New group').Count -ne 0) {
            [void][MzTD]::DragRelease()
            throw 'PRODUCT_FAIL: the run label was still in the UIA tree mid-drag - the drag cut did not land'
        }
        [void][MzTD]::DragMove($hx + 20, $hy + 22)
        Start-Sleep -Milliseconds 200
        if ((Find-TextNamed 'New group').Count -ne 0) {
            [void][MzTD]::DragRelease()
            throw 'PRODUCT_FAIL: the run label came back while the drag was still live'
        }
        [void][MzTD]::DragRelease()
        Start-Sleep -Milliseconds 750
        Wait-Order $H @('fuzzdrag-1', 'fuzzdrag-3', 'fuzzdrag-2', 'fuzzdrag-5', 'fuzzdrag-4')
    }

    Add-Phase 'horizontal-chip-roundtrip' {
        Show-RowMenu $H 'fuzzdrag-5' $pid32 0.3
        Invoke-MenuItem 'Collapse Group' $pid32 'collapse the group from the strip'
        Start-Sleep -Milliseconds 500
        $chip = Find-RowAny $H 'New group'
        if ($null -eq $chip) { throw 'PRODUCT_FAIL: no chip appeared after Collapse Group' }
        if ($chip.Status -notmatch 'Collapsed') {
            throw "PRODUCT_FAIL: chip ItemStatus is '$($chip.Status)', expected the collapsed count"
        }
        Show-RowMenu $H 'New group' $pid32 0.3
        Invoke-MenuItem 'Expand Group' $pid32 'expand the group from the chip'
        Start-Sleep -Milliseconds 500
        $again = Find-RowAny $H 'fuzzdrag-4'
        if ($null -eq $again -or $again.Status -match 'Collapsed') {
            throw 'PRODUCT_FAIL: the group did not expand back from the chip'
        }
    }

    # The trace oracle, over the whole run's sessions. Six vertical-machine
    # drags: the two motion-on reorders, the motion-off reorder, the
    # boundary out-and-back, the pin drop, the drop-on-chip join. The
    # horizontal TabView drags do not go through this machine and leave no
    # session.
    Add-Phase 'trace-oracle' {
        $sessions = @(Read-TraceSessions $tracePath)
        Write-Host ("trace: {0} session(s)" -f $sessions.Count)
        foreach ($s in $sessions) {
            Write-Host ("  begin [{0}] commits={1} drops={2} pinDrops={3} flights={4} settle={5} endGhosts={6} postEnd=[{7}] mid=[{8}]" -f `
                $s.begin, $s.commits, $s.drops, $s.pinDrops, $s.flights, $s.settleAfterDrop, $s.endGhosts,
                (($s.postEndGhosts | ForEach-Object { "$_" }) -join ','), (($s.midGhosts | ForEach-Object { "$_" }) -join ','))
        }
        if ($sessions.Count -lt 6) {
            throw "PRODUCT_FAIL: only $($sessions.Count) drag session(s) in the trace; the vertical drags did not all reach the machine"
        }
        Assert-TraceSession $sessions[0] 'the motion-on reorder' 1 'on'
        Assert-TraceSession $sessions[1] 'the inverse reorder' 1 'on'
        Assert-TraceSession $sessions[2] 'the motion-off reorder' 1 'off'
        Assert-TraceSession $sessions[3] 'the boundary out-and-back' 0 'on'
        Assert-TraceSession $sessions[4] 'the pin drop' 0 'on'
        if ($sessions[4].pinDrops -lt 1) { throw 'PRODUCT_FAIL: the pin drop never reached the trace' }
        if ($sessions[4].flights -lt 1) { throw 'PRODUCT_FAIL: the pin drop was not answered by a flight' }
        Assert-TraceSession $sessions[5] 'the drop-on-chip join' 0 'on'
    }
}
catch {
    if ($null -ne $proc -and -not $proc.HasExited) {
        try {
            $rc = [MzTD]::RectOf($script:MainHwnd64)
            if ($null -ne $rc) {
                $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
                $bmp.Save((Join-Path $OutDir 'shots\fail-state.png'))
                $g.Dispose(); $bmp.Dispose()
            }
        } catch { }
    }
    $script:Phases.Add([ordered]@{ name = 'fatal'; ok = $false; error = "$_" })
    $script:FatalWasProduct = ("$_" -like 'PRODUCT_FAIL*')
}
finally {
    if ($null -ne $proc -and -not $proc.HasExited) {
        try { $proc.Kill($true); [void]$proc.WaitForExit(3000) } catch { }
    }
    if ($origXdgSet) { $env:XDG_CONFIG_HOME = $origXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
    if ($traceSet) { $env:WINTTY_TABDRAG_TRACE = $origTrace }
    else { Remove-Item Env:WINTTY_TABDRAG_TRACE -ErrorAction SilentlyContinue }
    if (Test-Path $guardSnapshot) {
        # A mid-run crash must still give the machine its animations back;
        # the read-back inside the restore is what turns a silent miss into
        # a loud harness failure. A restore that fails cannot be the
        # product's finding: this run leaves knowing nothing.
        try { Restore-EnvSnapshot -Path $guardSnapshot }
        catch { Write-Host "$_" -ForegroundColor Red; $script:FatalWasProduct = $false }
    }
    if ((Test-Path $tempXdg)) {
        Remove-Item -Recurse -Force $tempXdg -ErrorAction SilentlyContinue
    }
    Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $ExePath
}

$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
$result = [ordered]@{
    seed = $Seed
    draws = $script:Draws
    crashGrew = $crashGrew
    phases = $script:Phases
    orders = $script:Orders
    motionLegs = $script:MotionLegs
    trace = (Join-Path $OutDir 'tabdrag.trace')
}
$result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)

$failed = @($script:Phases | Where-Object { -not $_.ok })
if ($failed.Count -eq 0 -and -not $crashGrew) { exit 0 }
if ($null -eq $script:FatalWasProduct -or $script:FatalWasProduct -or $crashGrew) { exit 2 }
exit 1
