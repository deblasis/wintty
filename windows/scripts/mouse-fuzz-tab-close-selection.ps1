#requires -Version 7
<#
    Randomized open/close against both tab strips, checking that the tab the
    user was on stays the tab the strip says it is on AND the tab the strip
    paints as selected.

    The defect this was written for: with vertical tabs, closing a row ABOVE
    the active one left the selected-row fill on the slot the closed tab
    vacated. The logical selection was right the whole time - UIA reported the
    correct tab selected - so an oracle that only asks UIA would have passed a
    build with the fill a row out of place. That is why there are two checks
    per close and why the second one reads pixels.

    Identity is the tab's title. Every tab here runs the same shell, so every
    tab arrives with the same title and the strip names them all alike, which
    is why the harness gives each one a title of its own before it measures
    anything. A title belongs to the tab, follows it across a reflow, and is
    what the strip publishes as the accessible name.

    Not the UIA RuntimeId, which is what this used to match on. That is the id
    of the item CONTAINER, and nothing promises a container stays with a tab
    across a reflow: a strip is free to hand a container to whichever tab
    lands in its slot, and then the id of the tab that was active is found
    alive on the strip belonging to a different tab, and the oracle reports a
    move that never happened. The old setup control could not see that either
    way, because it removed the LAST row with the FIRST one active and nothing
    shifted. The control below removes a row that DOES shift the rest: it
    requires the titles to survive that, and it measures the RuntimeIds over
    the same close and reports what it found instead of asserting on it. Every
    round reports the same measurement as idDrift, so a build where the ids do
    start following slots says so in the log rather than in a verdict.

    Both checks read the strip only once it has stopped changing after the
    close. A removal takes several frames, and a selection sampled inside one
    describes a strip that has not finished deciding - which is not a defect,
    and reporting it as one costs a person an afternoon. Settling is on the
    strip holding still, never on it holding the expected answer, so a
    selection that lands wrong and stays wrong is still a finding.

    Both layouts get their own launch with their own config rather than a
    runtime toggle: the switch is animated and has its own harness (morph), and
    a second process is cheaper to reason about than a settled animation.

    Input: SendInput only, behind a foreground steal that attaches to the
    current foreground thread's input queue first. Posted WM_CHAR/WM_KEYDOWN
    land zero characters in this app, and the XAML island ignores keyboard
    input until one real click on the app's own pixels has armed it.

    Elements are located by AutomationId ('NavView', 'TabViewControl') and by
    name ('Close tab'), never by being the first of their type on screen.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$Seed = 1337
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
$ErrorActionPreference = 'Stop'

# PRODUCT_FAIL leaves with 2 so the suite records a finding in the build under
# test. Anything else rethrows and becomes 1, which the suite retries: a
# refused foreground or a window that never appeared says nothing about the
# product.
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

public static class MzTC {
    public const uint KEYEVENTF_KEYUP     = 0x0002;
    public const uint KEYEVENTF_UNICODE    = 0x0004;
    public const uint MOUSEEVENTF_MOVE     = 0x0001;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP   = 0x0004;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_RETURN  = 0x0D;
    public const ushort VK_T       = 0x54;

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

    // Synthesized input goes to whatever owns the foreground, never to a
    // handle, and under the foreground lock a bare SetForegroundWindow fails
    // silently. Attaching to the current foreground thread's input queue lifts
    // that lock for the duration of the call, which is what makes the steal
    // hold against an app that keeps repainting itself back into focus.
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

    static uint ThreadOf(IntPtr h) { uint pid; return GetWindowThreadProcessId(h, out pid); }

    static void Send(INPUT[] inputs) {
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
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

    // Type a line into the terminal and press Return. KEYEVENTF_UNICODE
    // instead of VK codes because the text carries digits and a hyphen,
    // whose VK mapping depends on the active keyboard layout, and the
    // foreground is re-taken per character because the app can repaint
    // itself back into focus mid-line.
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

    // One real click on the app's own pixels. The XAML island does not take
    // focus from the window merely being foreground, and until it has, every
    // keystroke is dropped. The point is probed before and after the move so a
    // flyout arriving mid-settle cannot take the click.
    public static bool Click(uint pid, int x, int y) {
        var hit = WindowFromPoint(new POINT { X=x, Y=y });
        if (ClassOf(hit) == "WinttySplash" || PidOf(hit) != pid) return false;
        if (!SetCursorPos(x, y)) return false;
        Thread.Sleep(60);
        hit = WindowFromPoint(new POINT { X=x, Y=y });
        if (PidOf(hit) != pid) return false;
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(200);
        return true;
    }

    // Absolute SendInput, because WinUI raises PointerEntered for that and not
    // for mouse_event or a posted message. Revealing the horizontal strip's
    // close button needs a real hover.
    public static bool Hover(int x, int y) {
        if (!SetCursorPos(x, y)) return false;
        int sw = Math.Max(1, GetSystemMetrics(0));
        int sh = Math.Max(1, GetSystemMetrics(1));
        var i = new INPUT { type = 0 };
        i.U.mi = new MOUSEINPUT {
            dx = (int)(x * 65535.0 / Math.Max(1, sw - 1)),
            dy = (int)(y * 65535.0 / Math.Max(1, sh - 1)),
            dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
        };
        Send(new INPUT[] { i });
        return true;
    }
}
'@

$UIA = [System.Windows.Automation.AutomationElement]
$TREE = [System.Windows.Automation.TreeScope]::Descendants
$CTRL = [System.Windows.Automation.ControlType]

# Two samples this far apart in any one channel are different fills; anything
# closer is the same fill. One boundary rather than two, so no measurement can
# land in a band where the harness has no answer.
$ChannelDelta = 20

function Get-WinUiWindows([uint32]$ProcId) {
    $hits = [System.Collections.Generic.List[object]]::new()
    $cb = [MzTC+EnumProc]{
        param($h, $lp)
        [uint32]$o = 0; [void][MzTC]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -ne $ProcId -or -not [MzTC]::IsWindowVisible($h)) { return $true }
        if ([MzTC]::ClassOf($h) -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [MzTC]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64 = $hwnd64; Title = [MzTC]::TitleOf($h); Area = ($rc.W * $rc.Hh) })
        return $true
    }
    [void][MzTC]::EnumWindows($cb, [IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending
}

function Test-SplashVisible([int]$ProcId) {
    $script:splashSeen = $false
    $cb = [MzTC+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner = 0; [void][MzTC]::GetWindowThreadProcessId($hwnd, [ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        if ([MzTC]::ClassOf($hwnd) -eq 'WinttySplash' -and [MzTC]::IsWindowVisible($hwnd)) { $script:splashSeen = $true }
        return $true
    }
    [void][MzTC]::EnumWindows($cb, [IntPtr]::Zero)
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

function Get-UiaRoot([int64]$Hwnd64) {
    return $UIA::FromHandle([MzTC]::P($Hwnd64))
}

function Find-ById($root, [string]$Id) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        $UIA::AutomationIdProperty, $Id)
    return $root.FindFirst($TREE, $cond)
}

function Find-ByName($root, [string]$Name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        $UIA::NameProperty, $Name)
    return $root.FindFirst($TREE, $cond)
}

function Get-RuntimeKey($el) {
    return (($el.GetRuntimeId()) -join '.')
}

# One row per tab, ordered the way the strip lays them out, each carrying the
# identity handle the oracle matches on.
function Get-TabRows($root, [bool]$Vertical) {
    $hostId = if ($Vertical) { 'NavView' } else { 'TabViewControl' }
    $stripEl = Find-ById $root $hostId
    if ($null -eq $stripEl) { throw "HARVEST_MISS: no strip with AutomationId $hostId" }

    $ct = if ($Vertical) { $CTRL::ListItem } else { $CTRL::TabItem }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        $UIA::ControlTypeProperty, $ct)
    $found = $stripEl.FindAll($TREE, $cond)

    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($el in $found) {
        $r = $el.Current.BoundingRectangle
        if ($r.Width -le 0 -or $r.Height -le 0) { continue }
        $selected = $null
        try {
            $pat = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $selected = [bool]$pat.Current.IsSelected
        } catch { $selected = $null }
        $rows.Add([pscustomobject]@{
            El       = $el
            Key      = Get-RuntimeKey $el
            Name     = $el.Current.Name
            Rect     = $r
            Selected = $selected
        })
    }
    if ($rows.Count -eq 0) { throw "HARVEST_MISS: no tab items under $hostId" }
    if ($rows | Where-Object { $null -eq $_.Selected }) {
        throw 'HARVEST_MISS: a tab item exposes no SelectionItem pattern, so selection cannot be read'
    }
    $sorted = if ($Vertical) { $rows | Sort-Object { $_.Rect.Y } } else { $rows | Sort-Object { $_.Rect.X } }
    return @($sorted)
}

function Select-TabRow($row, [uint32]$ProcId, [int64]$Hwnd64) {
    try {
        $pat = $row.El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $pat.Select()
        Start-Sleep -Milliseconds 450
        return
    } catch { }
    $r = $row.Rect
    $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
    if (-not [MzTC]::Click($ProcId, $x, $y)) { throw "HARVEST_MISS: could not click tab at $x,$y" }
    Start-Sleep -Milliseconds 450
}

function Close-TabRow($row, [uint32]$ProcId) {
    # Hover first: the horizontal strip only reveals a non-selected tab's close
    # button under the pointer, and hover is the state a real close starts in
    # for both strips.
    $r = $row.Rect
    [void][MzTC]::Hover([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    Start-Sleep -Milliseconds 400

    $btn = Find-ById $row.El 'CloseButton'
    if ($null -eq $btn) { $btn = Find-ByName $row.El 'Close tab' }
    if ($null -eq $btn) { throw 'HARVEST_MISS: no close button on the tab row' }

    try {
        $pat = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pat.Invoke()
    } catch {
        $br = $btn.Current.BoundingRectangle
        $bx = [int]($br.X + $br.Width / 2); $by = [int]($br.Y + $br.Height / 2)
        if (-not [MzTC]::Click($ProcId, $bx, $by)) { throw "HARVEST_MISS: close button click refused at $bx,$by" }
    }
    Start-Sleep -Milliseconds 700
}

# Clicking the terminal is what makes the app accept a chord at all: the XAML
# island drops synthesized keys until a real click lands on its own pixels,
# and ANY chrome interaction -- the pane toggle, a tab close button -- moves
# focus off the terminal and un-arms it again.
#
# So this is called before every chord rather than once at startup. Arming
# once was enough to open the first tabs and then silently stopped working
# after the first close, which surfaced as "the new-tab chord is not landing"
# and reads exactly like a broken keybinding.
function Enable-Chords {
    if (-not $script:ArmPid) { return }
    if (-not [MzTC]::Click($script:ArmPid, $script:ArmX, $script:ArmY)) {
        throw 'HARVEST_MISS: could not click the terminal to arm input'
    }
    Start-Sleep -Milliseconds 250
}

function Add-Tab([int64]$Hwnd64) {
    if (-not [MzTC]::Chord([MzTC]::P($Hwnd64), @([MzTC]::VK_CONTROL), [MzTC]::VK_T)) {
        throw 'FOREGROUND_MISS: could not take the foreground to open a tab'
    }
    Start-Sleep -Milliseconds 800
}

# Bounded, because a chord that never lands would otherwise wedge the run
# holding the foreground until the suite's wall-clock budget kills it, and the
# cause would be invisible in the log.
function Add-TabsUpTo([int64]$Hwnd64, [bool]$Vertical, [int]$Want) {
    Enable-Chords
    for ($i = 0; $i -lt ($Want * 2 + 4); $i++) {
        $have = (Get-TabRows (Get-UiaRoot $Hwnd64) $Vertical).Count
        if ($have -ge $Want) { return $have }
        Add-Tab $Hwnd64
    }
    throw "HARVEST_MISS: ctrl+t did not reach $Want tabs; the new-tab chord is not landing"
}

# Give every tab a title nothing else on the strip carries, so the oracle can
# say which tab it means. The counter never resets: a tab opened in a later
# round must not inherit the title of one closed in an earlier one, or a
# verdict about "the tab that was active" could be talking about its
# replacement.
$script:TitleSeq = 0
$TitleMark = '^fuzztab-\d+$'

function Set-RowTitles([int64]$Hwnd64, [bool]$Vertical, [uint32]$ProcId) {
    # A pass can leave work behind: titling a tab needs it selected, and
    # selecting it is a chance for the strip to be re-read mid-change.
    for ($pass = 0; $pass -lt 4; $pass++) {
        $rows = Get-TabRows (Get-UiaRoot $Hwnd64) $Vertical
        $plain = @($rows | Where-Object { $_.Name -notmatch $TitleMark })
        if ($plain.Count -eq 0) { return $rows }

        foreach ($row in $plain) {
            $script:TitleSeq++
            $want = "fuzztab-$($script:TitleSeq)"
            Select-TabRow $row $ProcId $Hwnd64
            Enable-Chords
            if (-not [MzTC]::TypeLine([MzTC]::P($Hwnd64), "title $want")) {
                throw 'FOREGROUND_MISS: could not take the foreground to title a tab'
            }
            $dl = (Get-Date).AddSeconds(6)
            $named = @()
            while ((Get-Date) -lt $dl) {
                Start-Sleep -Milliseconds 250
                $named = @(Get-TabRows (Get-UiaRoot $Hwnd64) $Vertical |
                           Where-Object { $_.Name -eq $want })
                if ($named.Count -eq 1) { break }
            }
            if ($named.Count -ne 1) {
                throw ("HARVEST_MISS: '$want' never showed up as a tab title, so the shell " +
                       'is not reporting titles and tabs cannot be told apart')
            }
        }
    }
    throw 'HARVEST_MISS: tabs kept arriving untitled'
}

# Identity is only identity while it is unique. A duplicate means the seeding
# above did not take, which makes every verdict below meaningless, so it is
# exit 1 (the corpus could not be established) and not a finding.
function Assert-DistinctTitles($rows, [string]$Where) {
    $dupes = @($rows | Group-Object Name | Where-Object { $_.Count -gt 1 })
    if ($dupes.Count -gt 0) {
        throw ("HARVEST_MISS: $Where has $($dupes[0].Count) tabs titled '$($dupes[0].Name)', " +
               'so a title cannot stand for tab identity')
    }
}

<#
    Wait for the strip to stop moving after a close, and say how long it took.

    A close is not instant: the item leaves the collection, MUXC re-arranges
    the pane, and the app puts the manager's active tab back on the item that
    now holds it. Sampling anywhere in there reads a strip that has not
    finished deciding, and a selection read from it is not a claim about
    anything.

    Stop MOVING, not become correct. The loop exits on two consecutive reads
    that agree, whatever they say, so a strip that settles on the wrong tab
    still fails every check below; only the transit is skipped. Waiting for
    the expected answer instead would be an oracle that cannot fail.
#>
function Wait-StripSettled([int64]$Hwnd64, [bool]$Vertical) {
    $prev = $null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 2500) {
        $rows = Get-TabRows (Get-UiaRoot $Hwnd64) $Vertical
        $shape = (($rows | ForEach-Object { "$($_.Name)=$($_.Selected)" }) -join '|')
        if ($shape -eq $prev) {
            return [pscustomobject]@{ Rows = $rows; SettleMs = [int]$sw.ElapsedMilliseconds }
        }
        $prev = $shape
        Start-Sleep -Milliseconds 300
    }
    # Still changing. Measured anyway, on the last read, with -1 in the record
    # so the log says the numbers came off a strip that never held still.
    return [pscustomobject]@{
        Rows = (Get-TabRows (Get-UiaRoot $Hwnd64) $Vertical); SettleMs = -1
    }
}

function Get-WindowShot([int64]$Hwnd64) {
    $rc = [MzTC]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw 'HARVEST_MISS: degenerate window rect' }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $g.Dispose()
    return [pscustomobject]@{ Bmp = $bmp; L = $rc.L; T = $rc.T }
}

function Get-PixelAt($shot, [int]$X, [int]$Y) {
    $px = $X - $shot.L; $py = $Y - $shot.T
    if ($px -lt 0 -or $py -lt 0 -or $px -ge $shot.Bmp.Width -or $py -ge $shot.Bmp.Height) { return $null }
    $c = $shot.Bmp.GetPixel($px, $py)
    return [pscustomobject]@{ R = [int]$c.R; G = [int]$c.G; B = [int]$c.B }
}

function Get-ColorDelta($a, $b) {
    if ($null -eq $a -or $null -eq $b) { return -1 }
    $dr = [Math]::Abs($a.R - $b.R); $dg = [Math]::Abs($a.G - $b.G); $db = [Math]::Abs($a.B - $b.B)
    return [Math]::Max($dr, [Math]::Max($dg, $db))
}

# Where in a row's rect the selection fill is, and text and glyphs are not.
# Vertical: a few px in from the left edge, past the 4px strip inset and well
# left of the icon lane. Horizontal: horizontally centered, near the top of the
# handle, above the title baseline.
function Get-ProbePoint($row, [bool]$Vertical) {
    $r = $row.Rect
    if ($Vertical) {
        return @([int]($r.X + 8), [int]($r.Y + $r.Height / 2))
    }
    return @([int]($r.X + $r.Width / 2), [int]($r.Y + [Math]::Max(3.0, $r.Height * 0.2)))
}

<#
    Read the fill under every row and decide whether exactly one row is painted
    as selected and whether it is the right one.

    Relative, not absolute: the harness never learns which color a selected row
    is meant to be, only that the selected row's fill differs from the others'
    and that the others all agree with each other. That is what makes it a
    check on placement rather than on theming.
#>
function Test-SelectionFill($shot, $rows, [bool]$Vertical) {
    $selected = @($rows | Where-Object { $_.Selected })
    $others = @($rows | Where-Object { -not $_.Selected })
    if ($selected.Count -ne 1) {
        return "the strip paints $($selected.Count) rows as selected"
    }
    if ($others.Count -lt 1) { return $null }

    $samples = @{}
    foreach ($row in $rows) {
        $pt = Get-ProbePoint $row $Vertical
        $c = Get-PixelAt $shot $pt[0] $pt[1]
        if ($null -eq $c) { throw "HARVEST_MISS: probe point $($pt[0]),$($pt[1]) is outside the window" }
        $samples[$row.Key] = $c
    }

    # A row other than the selected one carrying a different fill from its
    # peers is the fill left behind on a slot it no longer belongs to.
    for ($i = 0; $i -lt $others.Count; $i++) {
        for ($j = $i + 1; $j -lt $others.Count; $j++) {
            $d = Get-ColorDelta $samples[$others[$i].Key] $samples[$others[$j].Key]
            if ($d -ge $ChannelDelta) {
                return ("two unselected rows are painted differently (delta $d): " +
                        "'$($others[$i].Name)' vs '$($others[$j].Name)'")
            }
        }
    }

    foreach ($o in $others) {
        $d = Get-ColorDelta $samples[$selected[0].Key] $samples[$o.Key]
        if ($d -lt $ChannelDelta) {
            return ("the selected row '$($selected[0].Name)' is painted the same as unselected " +
                    "'$($o.Name)' (delta $d)")
        }
    }
    return $null
}

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$rng = [System.Random]::new($Seed)
$findings = [System.Collections.Generic.List[object]]::new()
$rounds = [System.Collections.Generic.List[object]]::new()
# How many rows came back under another tab's container id in each layout's
# control close. Reported, not asserted on: it says what the strips do with
# their containers, which is why identity is matched on the title.
$script:IdDrift = [ordered]@{}
$TabTarget = 4
$RoundsPerLayout = 6

$originalXdgSet = Test-Path Env:XDG_CONFIG_HOME
$originalXdg = if ($originalXdgSet) { $env:XDG_CONFIG_HOME } else { $null }

function Write-HarnessConfig([string]$Dir, [bool]$Vertical) {
    New-Item -ItemType Directory -Force -Path (Join-Path $Dir 'wintty') | Out-Null
    $verticalLine = if ($Vertical) { 'vertical-tabs = true' } else { 'vertical-tabs = false' }
    # cmd is pinned rather than left to the default shell because the harness
    # titles each tab with the shell's own `title` builtin, and which builtin
    # that is depends on the shell.
    [IO.File]::WriteAllText((Join-Path $Dir 'wintty\config.wintty'), @"
command = cmd.exe
windows-single-instance = true
window-save-state = never
$verticalLine
vertical-tabs-hover-expand = false
window-theme = wintty
theme = Catppuccin Mocha
"@)
}

function Invoke-Layout([bool]$Vertical) {
    $label = if ($Vertical) { 'vertical' } else { 'horizontal' }
    $xdg = Join-Path $env:TEMP ("wintty-fuzz-xdg-tcs-{0}-{1:HHmmss}" -f $label, (Get-Date))
    Write-HarnessConfig $xdg $Vertical

    $proc = $null
    try {
        $env:XDG_CONFIG_HOME = $xdg
        Start-Sleep -Milliseconds 500
        $proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path $ExePath)
        $pid32 = [uint32]$proc.Id
        [void](Wait-Ready $proc)
        Start-Sleep -Seconds 1
        $main = @(Get-WinUiWindows $pid32) | Select-Object -First 1
        if ($null -eq $main) { throw 'HARVEST_MISS: main window vanished after startup' }
        $hwnd64 = [int64]$main.Hwnd64
        Write-Host "$label hwnd=$hwnd64 pid=$pid32"

        $rc = [MzTC]::RectOf($hwnd64)
        $restX = [int]($rc.L + $rc.W * 0.75)
        $restY = [int]($rc.T + $rc.Hh * 0.75)
        if (-not [MzTC]::Focus([MzTC]::P($hwnd64))) { throw 'FOREGROUND_MISS: window would not come forward' }
        $script:ArmPid = $pid32
        $script:ArmX = $restX
        $script:ArmY = $restY
        Enable-Chords

        if ($Vertical) {
            # The per-row close button only exists in the expanded pane, and a
            # wide pane also gives the fill probe room away from the icon lane.
            $toggle = Find-ById (Get-UiaRoot $hwnd64) 'PaneToggleButton'
            if ($null -eq $toggle) { throw 'HARVEST_MISS: no PaneToggleButton' }
            $pat = $toggle.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $pat.Invoke()
            Start-Sleep -Milliseconds 900
        }

        [void](Add-TabsUpTo $hwnd64 $Vertical $TabTarget)
        [void](Set-RowTitles $hwnd64 $Vertical $pid32)

        # Establish the corpus the oracle measures against, using the case the
        # oracle actually asserts on: a removal that SHIFTS rows. The last row
        # is made active and the first one closed, so every survivor moves up
        # a slot.
        #
        # Titles have to survive that, because they are what identity is
        # matched on; a build where they do not gets exit 1 rather than a
        # finding. Container RuntimeIds are measured over the same close but
        # only reported, because they are the id of the slot's container and
        # not of the tab that happens to be in it.
        $before = Get-TabRows (Get-UiaRoot $hwnd64) $Vertical
        Select-TabRow $before[-1] $pid32 $hwnd64
        $before = Get-TabRows (Get-UiaRoot $hwnd64) $Vertical
        Assert-DistinctTitles $before 'the strip before the control close'
        $keyByTitle = @{}
        foreach ($r in $before) { $keyByTitle[$r.Name] = $r.Key }
        Close-TabRow $before[0] $pid32

        $after = Get-TabRows (Get-UiaRoot $hwnd64) $Vertical
        Assert-DistinctTitles $after 'the strip after the control close'
        $wantTitles = @($before | Select-Object -Skip 1 | ForEach-Object { $_.Name })
        $gotTitles = @($after | ForEach-Object { $_.Name })
        if (@(Compare-Object $wantTitles $gotTitles).Count -ne 0) {
            throw ('HARVEST_MISS: tab titles did not survive a shifting removal (wanted ' +
                   "$($wantTitles -join ', '); got $($gotTitles -join ', ')), " +
                   'so they cannot stand for tab identity')
        }

        $drifted = @($after | Where-Object { $keyByTitle[$_.Name] -ne $_.Key })
        $script:IdDrift[$label] = $drifted.Count
        if ($drifted.Count -eq 0) {
            Write-Host ("$label titles survive a shifting removal; container RuntimeIds " +
                        'happened to as well, but are still not what identity is matched on')
        }
        else {
            Write-Host ("$label titles survive a shifting removal; container RuntimeIds do NOT " +
                        "($($drifted.Count) of $($after.Count) rows came back under another " +
                        "tab's id), which is why identity is matched on the title")
        }

        for ($round = 0; $round -lt $RoundsPerLayout; $round++) {
            # Four is the floor the paint check needs: after the close there is
            # one selected row and two unselected ones, so "the unselected rows
            # all agree with each other" is a claim about more than one row.
            [void](Add-TabsUpTo $hwnd64 $Vertical $TabTarget)
            $rows = Set-RowTitles $hwnd64 $Vertical $pid32
            Assert-DistinctTitles $rows "round $round before the close"

            $keep = $rng.Next(0, $rows.Count)
            Select-TabRow $rows[$keep] $pid32 $hwnd64

            $rows = Get-TabRows (Get-UiaRoot $hwnd64) $Vertical
            $expected = @($rows | Where-Object { $_.Selected })
            if ($expected.Count -ne 1) {
                throw "HARVEST_MISS: $($expected.Count) rows selected after asking for one"
            }
            $expectedName = $expected[0].Name

            $victims = @($rows | Where-Object { $_.Name -ne $expectedName })
            $victim = $victims[$rng.Next(0, $victims.Count)]
            $victimName = $victim.Name
            $victimAbove = ($rows.IndexOf($victim) -lt $rows.IndexOf($expected[0]))
            $keyByTitle = @{}
            foreach ($r in $rows) { $keyByTitle[$r.Name] = $r.Key }
            Close-TabRow $victim $pid32

            $settled = Wait-StripSettled $hwnd64 $Vertical
            $rows = $settled.Rows
            Assert-DistinctTitles $rows "round $round after the close"
            # Recorded per round, not asserted on, so the log carries the
            # evidence for matching on the title: a round with drift above zero
            # is one where a container id would have named the wrong tab.
            $idDrift = @($rows | Where-Object { $keyByTitle[$_.Name] -ne $_.Key }).Count
            $stillThere = @($rows | Where-Object { $_.Name -eq $expectedName })
            $nowSelected = @($rows | Where-Object { $_.Selected })

            $verdicts = [System.Collections.Generic.List[string]]::new()
            if ($stillThere.Count -ne 1) {
                $verdicts.Add("the active tab '$expectedName' is gone after closing '$victimName'; " +
                              "the strip now holds $($rows.Name -join ', ')")
            }
            elseif ($nowSelected.Count -ne 1) {
                $verdicts.Add("$($nowSelected.Count) rows report themselves selected after closing " +
                              "'$victimName': $($nowSelected.Name -join ', ')")
            }
            elseif ($nowSelected[0].Name -ne $expectedName) {
                $verdicts.Add("selection moved off '$expectedName' onto '$($nowSelected[0].Name)' " +
                              "after closing '$victimName'")
            }

            # Park the pointer off the strip: a hovered row carries the hover
            # fill and would read as a second painted row.
            [void][MzTC]::Hover($restX, $restY)
            Start-Sleep -Milliseconds 450
            $shot = Get-WindowShot $hwnd64
            try {
                $paint = Test-SelectionFill $shot $rows $Vertical
                if ($paint) {
                    $verdicts.Add("the painted selection disagrees with the strip: $paint")
                    $shot.Bmp.Save((Join-Path $OutDir "shots\$label-round$round-paint.png"))
                }
            } finally { $shot.Bmp.Dispose() }

            $rounds.Add([pscustomobject]@{
                layout = $label; round = $round; kept = $expectedName
                closed = $victimName
                closedAbove = $victimAbove; remaining = $rows.Count
                idDrift = $idDrift; settleMs = $settled.SettleMs
                verdicts = @($verdicts)
            })
            foreach ($v in $verdicts) {
                $findings.Add("[$label round $round" + $(if ($victimAbove) { ', closed above' } else { ', closed below' }) + "] $v")
            }
            Write-Host ("$label round $round kept='$expectedName' closed='$victimName' " +
                        "closedAbove=$victimAbove remaining=$($rows.Count) " +
                        "idDrift=$idDrift settleMs=$($settled.SettleMs) " +
                        "verdicts=$($verdicts.Count)")
        }
    }
    finally {
        if ($null -ne $proc) {
            $proc.Refresh()
            if (-not $proc.HasExited) { try { $proc.Kill($true); [void]$proc.WaitForExit(3000) } catch { } }
        }
        Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $ExePath
        Start-Sleep -Milliseconds 700
    }
}

Assert-NoWintty
$script:WinttyStamp = Get-WinttyLaunchStamp
try {
    Invoke-Layout $true
    Invoke-Layout $false
}
finally {
    Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $ExePath
    if ($originalXdgSet) { $env:XDG_CONFIG_HOME = $originalXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
}

$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
$result = @{
    crashGrew        = $crashGrew
    seed             = $Seed
    containerIdDrift = $script:IdDrift
    rounds           = @($rounds)
    findings         = @($findings)
}
$result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir 'result.json')
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)

if ($findings.Count -gt 0) {
    foreach ($f in $findings) { Write-Host "PRODUCT_FAIL $f" -ForegroundColor Red }
    exit 2
}
if ($crashGrew) {
    Write-Host 'PRODUCT_FAIL crash.log grew during the run' -ForegroundColor Red
    exit 2
}
exit 0
