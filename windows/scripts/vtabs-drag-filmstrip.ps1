#requires -Version 7
<#
    A per-frame film of a live vertical drag, judged in pixels.

    The spec's oracle for the drag motion is visual: when a crossing
    commits, the gap must open within 2 frames, and the displaced rows'
    offsets must converge within 500ms. A UIA read cannot see either --
    the accessibility tree reports the settled layout, never the glide --
    so this harness films the strip while a scripted drag crosses one
    neighbour, and measures the frames.

    The tracked pixel is the SELECTED row's accent band, chosen on purpose.
    The row being dragged is deliberately an unselected one: in any theme
    the unselected rows sit nearly transparent on the strip background and
    cannot be told apart in pixels, but the selected row is filled with the
    accent and is unmistakable. So row 3 keeps the selection and row 2 does
    the dragging, and when the drag crosses row 3 the product's answer is
    to slide row 3's band up one slot to open the gap. The band's Y over
    time IS the offset animation the oracle is about:

      - gap open: the first frame after the scheduled crossing whose band
        top has risen at least 5px, minus the crossing time, must be
        within 2 frames.
      - convergence: the band must be within 2px of its final position
        for 6 consecutive frames within 500ms of the crossing.
      - travel: the band must end at least 60% of a row height above
        where it started, so a no-op drag cannot pass.
      - and the layout must really have swapped: the final UIA order is
        read back and asserted.

    Calibration comes from frame 0, not from hard-coded colours: the band
    reference is sampled inside row 3's own rect, and if frame 0 does not
    show the band where row 3 says it is, the harness refuses rather than
    track a guess.

    The frame capture and the input share one thread and one stopwatch --
    SendInput waypoints and CopyFromScreen frames interleaved on the same
    clock -- so a frame's timestamp is honest relative to the input it
    was taken between.

    This harness needs a machine whose client-area animations are ON: with
    them off the product cuts every glide, the offsets converge in one
    frame, and the timings above measure nothing. That is exit 1, not a
    product finding. The motion-on/motion-off identity pair lives in the
    drag harness, which asserts order; this one asserts motion.

    Exits 0 on pass, 2 on a product finding, 1 when the harness could not
    run -- a refused click, a calibration that found no band, animations
    disabled on this machine.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$IntervalMs = 60,
    [int]$MaxFrames = 46
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'frames') | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class VtDF {
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

    static void Send(INPUT[] inputs) {
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

    public static bool DragPress(uint pid, int x, int y) {
        if (!Owned(pid, x, y)) return false;
        if (!SetCursorPos(x, y)) return false;
        Thread.Sleep(50);
        if (!Owned(pid, x, y)) return false;
        Send(new INPUT[] { MoveInput(x, y) });
        Thread.Sleep(40);
        Send(new INPUT[] {
            new INPUT { type = 0, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } } }
        });
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
[void][VtDF]::SetProcessDpiAwarenessContext([IntPtr](-4))

$UIA = [System.Windows.Automation.AutomationElement]
$TREE = [System.Windows.Automation.TreeScope]::Descendants
$CTRL = [System.Windows.Automation.ControlType]

function Get-WinUiWindows([uint32]$ProcId) {
    $hits = [System.Collections.Generic.List[object]]::new()
    $cb = [VtDF+EnumProc]{
        param($h, $lp)
        [uint32]$o = 0; [void][VtDF]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -ne $ProcId -or -not [VtDF]::IsWindowVisible($h)) { return $true }
        if ([VtDF]::ClassOf($h) -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [VtDF]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64 = $hwnd64; Title = [VtDF]::TitleOf($h); Area = ($rc.W * $rc.Hh) })
        return $true
    }
    [void][VtDF]::EnumWindows($cb, [IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending
}

function Test-SplashVisible([int]$ProcId) {
    $script:splashSeen = $false
    $cb = [VtDF+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner = 0; [void][VtDF]::GetWindowThreadProcessId($hwnd, [ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        if ([VtDF]::ClassOf($hwnd) -eq 'WinttySplash' -and [VtDF]::IsWindowVisible($hwnd)) { $script:splashSeen = $true }
        return $true
    }
    [void][VtDF]::EnumWindows($cb, [IntPtr]::Zero)
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

function Get-UiaRoot([int64]$Hwnd64) { return $UIA::FromHandle([VtDF]::P($Hwnd64)) }

function Find-ById($root, [string]$Id) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $Id)
    return $root.FindFirst($TREE, $cond)
}

# The vertical strip's rows in paint order, area-gated like the drag
# harness's reader.
function Get-StripRows {
    $root = Get-UiaRoot $script:MainHwnd64
    $stripEl = Find-ById $root 'NavView'
    if ($null -eq $stripEl) { throw 'HARVEST_MISS: no strip with AutomationId NavView' }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $CTRL::ListItem)
    $found = $stripEl.FindAll($TREE, $cond)
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($el in $found) {
        $r = $el.Current.BoundingRectangle
        if ($r.Width -le 0 -or $r.Height -le 0) { continue }
        $rows.Add([pscustomobject]@{ El = $el; Name = $el.Current.Name; Rect = $r })
    }
    if ($rows.Count -eq 0) { throw 'HARVEST_MISS: no rows under NavView' }
    return @($rows | Sort-Object { $_.Rect.Y })
}

function Enable-Chords([uint32]$ProcId) {
    $rc = [VtDF]::RectOf($script:MainHwnd64)
    if ($null -eq $rc) { throw 'HARVEST_MISS: window rect for arming' }
    # The arm click is refused while another window sits on top of the
    # point (Owned reads the window under the cursor), so try to take the
    # foreground first -- best-effort: the click's own z-order check is
    # the gate that matters -- and then walk inward from the offsets that
    # have historically been clear of chrome, taking the first point the
    # window owns. The center is the last point rect skew can take from
    # us.
    [void][VtDF]::Focus([VtDF]::P($script:MainHwnd64))
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
        if ([VtDF]::Click($ProcId, $x, $y)) { $armed = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $armed) {
        throw 'HARVEST_MISS: could not click the terminal to arm input'
    }
    Start-Sleep -Milliseconds 200
}

function Add-Tab([int64]$Hwnd64, [uint32]$ProcId) {
    Enable-Chords $ProcId
    if (-not [VtDF]::Chord([VtDF]::P($Hwnd64), @([VtDF]::VK_CONTROL), [VtDF]::VK_T)) {
        throw 'FOREGROUND_MISS: could not take the foreground to open a tab'
    }
    Start-Sleep -Milliseconds 800
}

function Set-RowTitle([object]$Row, [string]$Want, [uint32]$ProcId, [int64]$Hwnd64) {
    try {
        $pat = $Row.El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $pat.Select()
        Start-Sleep -Milliseconds 300
    } catch {
        throw "HARVEST_MISS: row '$($Row.Name)' has no selection pattern"
    }
    Enable-Chords $ProcId
    if (-not [VtDF]::TypeLine([VtDF]::P($Hwnd64), "title $Want")) {
        throw 'FOREGROUND_MISS: could not take the foreground to title a tab'
    }
    $dl = (Get-Date).AddSeconds(6)
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 250
        $named = @(Get-StripRows | Where-Object { $_.Name -eq $Want })
        if ($named.Count -eq 1) { return }
    }
    throw "HARVEST_MISS: '$Want' never showed up as a tab title"
}

function Expand-Sidebar([uint32]$ProcId) {
    $root = Get-UiaRoot $script:MainHwnd64
    $nav = Find-ById $root 'NavView'
    if ($null -ne $nav) {
        $w = $nav.Current.BoundingRectangle.Width
        if (-not [double]::IsNaN($w) -and $w -ge 200) { return }
    }
    $el = Find-ById $root 'PaneToggleButton'
    if ($null -eq $el) { throw 'HARVEST_MISS: PaneToggleButton' }
    try {
        $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pat.Invoke()
    } catch {
        $r = $el.Current.BoundingRectangle
        if (-not [VtDF]::Click($ProcId, [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))) {
            throw 'HARVEST_MISS: pane toggle click'
        }
    }
    Start-Sleep -Milliseconds 700
}

# ---- the pixel oracle -------------------------------------------------------

# Nearest-reference classification over one cropped column strip. $Refs is
# an ordered map of label -> [r,g,b]; every pixel in the crop is labelled by
# its nearest reference within $Tol per channel, or 'none'.
function Get-Pixels([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    return @{ bytes = $bytes; stride = $data.Stride; w = $w; h = $h }
}

function Get-Pixel([hashtable]$Px, [int]$X, [int]$Y) {
    $o = $Y * $Px.stride + $X * 4
    return @($Px.bytes[$o + 2], $Px.bytes[$o + 1], $Px.bytes[$o])
}

# Topmost y in the crop whose pixel is within $Tol of $Ref on every
# channel, over the column band [x - HalfW, x + HalfW] when $X is given,
# the full width otherwise. -1 when there is none. Column-scoped is what
# the band tracking wants: the calibrated colour is only known
# discriminating at the column it was sampled at, and a full-width scan
# happily matches ink or chrome on other rows first. Inlined byte access
# rather than a per-pixel helper call: this runs over every frame's crop.
function Find-BandTop([hashtable]$Px, [array]$Ref, [int]$Tol, [int]$From = 0, [int]$X = -1, [int]$HalfW = -1) {
    $bytes = $Px.bytes
    $stride = $Px.stride
    $x0 = if ($X -ge 0 -and $HalfW -ge 0) { [Math]::Max(0, $X - $HalfW) } else { 0 }
    $x1 = if ($X -ge 0 -and $HalfW -ge 0) { [Math]::Min($Px.w - 1, $X + $HalfW) } else { $Px.w - 1 }
    for ($y = $From; $y -lt $Px.h; $y++) {
        $rowOff = $y * $stride
        for ($x = $x0; $x -le $x1; $x++) {
            $o = $rowOff + $x * 4
            if ([math]::Abs($bytes[$o + 2] - $Ref[0]) -le $Tol -and
                [math]::Abs($bytes[$o + 1] - $Ref[1]) -le $Tol -and
                [math]::Abs($bytes[$o] - $Ref[2]) -le $Tol) {
                return $y
            }
        }
    }
    return -1
}

# ---- run -------------------------------------------------------------------

# The machine commits a crossing only when the dragged center passes the
# neighbour's center PLUS this token (TabDragReorder.Evaluate's strict
# inequality). The gesture's waypoints must overshoot that line by real
# margin or the commit never fires and the oracle measures a gesture that
# ordered nothing. Read from the product's own source, never hard-coded:
# a token change must move this script with it.
$script:HysteresisPx = -1
$motionCs = Join-Path $PSScriptRoot '../Ghostty.Core/Tabs/TabStripMotion.cs'
$motionSrc = Get-Content $motionCs -Raw
if ($motionSrc -match 'CrossingHysteresisPx\s*=\s*(\d+)') {
    $script:HysteresisPx = [int]$Matches[1]
}
if ($script:HysteresisPx -le 0) {
    throw 'HARVEST_MISS: could not read CrossingHysteresisPx from TabStripMotion.cs'
}

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$tempXdg = Join-Path $env:TEMP "wintty-fuzz-dragfilm-$([guid]::NewGuid().ToString('N'))"
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
$proc = $null
$script:FatalWasProduct = $null

# Above the try, so the refusal survives a finally that would otherwise bind
# a null stamp to a mandatory parameter.
Assert-NoWintty -Context 'The drag filmstrip'
$script:WinttyStamp = Get-WinttyLaunchStamp

# The machine's animation gate, read directly: this oracle measures the
# glide, and a machine running with animations off would make every timing
# verdict here describe a cut. Exit 1 - an environment the oracle is not
# for - not a product finding.
$SPI_GETCLIENTAREAANIMATION = [uint32]0x1042
if (-not ('WinttyDragFilm.Spi' -as [type])) {
    Add-Type -Namespace WinttyDragFilm -Name Spi -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)]
public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);
'@
}
[uint32]$anim = 0
[void][WinttyDragFilm.Spi]::SystemParametersInfo($SPI_GETCLIENTAREAANIMATION, 0, [ref]$anim, 0)
if ($anim -eq 0) {
    Write-Host 'HARVEST_MISS: client-area animations are off on this machine; the glide oracle measures nothing. Turn "animate controls and elements" back on to run this harness.'
    if ($origXdgSet) { $env:XDG_CONFIG_HOME = $origXdg }
    exit 1
}

$frames = [System.Collections.Generic.List[object]]::new()

try {
    if (-not (Test-Path $ExePath)) { throw "missing exe: $ExePath" }
    $env:XDG_CONFIG_HOME = $tempXdg
    $proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path -Parent (Resolve-Path $ExePath))
    $pid32 = [uint32]$proc.Id
    $main = Wait-Ready $proc
    $script:MainHwnd64 = [int64]$main.Hwnd64
    $hwnd64 = $script:MainHwnd64
    [void][VtDF]::MoveWindow([VtDF]::P($hwnd64), 60, 60, 1280, 820, $true)
    Start-Sleep -Milliseconds 600
    Write-Host "hwnd=$hwnd64 pid=$pid32 interval=${IntervalMs}ms frames=$MaxFrames"

    Enable-Chords $pid32
    for ($n = 1; $n -le 3; $n++) { Add-Tab $hwnd64 $pid32 }
    $names = @('fuzzfilm-1', 'fuzzfilm-2', 'fuzzfilm-3', 'fuzzfilm-4')
    foreach ($want in $names) {
        $row = @(Get-StripRows | Where-Object { $_.Name -notlike 'fuzzfilm-*' } | Select-Object -First 1)
        if ($row.Count -eq 0) { break }
        Set-RowTitle $row[0] $want $pid32 $hwnd64
    }
    $rows = Get-StripRows
    if (@($rows | Where-Object { $_.Name -notlike 'fuzzfilm-*' }).Count -gt 0) {
        throw 'HARVEST_MISS: tabs kept arriving untitled'
    }
    $gotNames = @($rows | ForEach-Object { $_.Name })
    if (($gotNames -join ',') -ne ($names -join ',')) {
        throw "PRODUCT_FAIL: seeded order is [$($gotNames -join ',')], expected [$($names -join ',')]"
    }

    # Row 3 keeps the selection; row 2 does the dragging. The band to track
    # is row 3's.
    $row3 = $rows | Where-Object { $_.Name -eq 'fuzzfilm-3' }
    $selPat = $row3.El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selPat.Select()
    Start-Sleep -Milliseconds 500

    $rows = Get-StripRows
    $row2 = $rows | Where-Object { $_.Name -eq 'fuzzfilm-2' }
    $row3 = $rows | Where-Object { $_.Name -eq 'fuzzfilm-3' }
    $row4 = $rows | Where-Object { $_.Name -eq 'fuzzfilm-4' }
    if ($null -eq $row2 -or $null -eq $row3 -or $null -eq $row4) { throw 'HARVEST_MISS: expected rows not found before the drag' }

    $rowH = [int]((@($row2.Rect.Height, $row3.Rect.Height, $row4.Rect.Height) | Sort-Object)[1])
    $cropX = [int]$rows[0].Rect.X - 8
    $cropY = [int]$rows[0].Rect.Y - 12
    $cropW = [int]($rows[0].Rect.Width + 16)
    $cropH = [int]($row4.Rect.Y + $row4.Rect.Height - $cropY + 16)
    if ($rowH -lt 16 -or $cropW -lt 40) { throw 'HARVEST_MISS: strip geometry too small to film' }
    Write-Host "crop=${cropX},${cropY} ${cropW}x${cropH} rowH=$rowH"

    # Frame 0, before any input: the band reference comes from inside row
    # 3's own rect, and the calibration is only good if that reference
    # actually finds the band there.
    $full = [System.Drawing.Bitmap]::new($cropW, $cropH)
    $g = [System.Drawing.Graphics]::FromImage($full)
    $g.CopyFromScreen($cropX, $cropY, 0, 0, $full.Size)
    $g.Dispose()
    $px0 = Get-Pixels $full
    # Sampled right of centre: the title text ends well before that, and a
    # sample on the text ink would calibrate to the wrong colour.
    #
    # The selected row's chrome in the current theme is a RING (a rounded
    # bright stroke around the row), not a filled band, so its colour
    # cannot be sampled from the row's interior -- the interior is the
    # strip background, and scanning for it finds the whole strip. The
    # edge is found instead by diffing the selected row's column against
    # the UNSELECTED row directly above it (same geometry, same fill):
    # their colours agree everywhere except where the selection chrome
    # draws. The stroke's colour is then sampled AT that edge, in frame 0
    # -- the calibration still comes from frame 0, not hard-coded colours.
    $colX = [int]($full.Width * 0.72)
    $r3Top = [int]($row3.Rect.Y - $cropY)
    $rowH = [int]($row3.Rect.Height)
    $r2Top = $r3Top - $rowH
    $bestY = -1
    $bestD = -1
    for ($y = $r2Top; $y -lt $r3Top + $rowH; $y++) {
        if ($y -lt 0 -or ($y + $rowH) -ge $full.Height) { continue }
        $a = Get-Pixel $px0 $colX $y
        $b = Get-Pixel $px0 $colX ($y - $rowH)
        $d = [math]::Abs($a[0] - $b[0]) + [math]::Abs($a[1] - $b[1]) + [math]::Abs($a[2] - $b[2])
        if ($d -gt $bestD) { $bestD = $d; $bestY = $y }
    }
    if ($bestY -lt 0 -or $bestD -lt 60 -or [math]::Abs($bestY - $r3Top) -gt 10) {
        Write-Host ("calibration: selection edge not distinct at x={0} (best delta {1} at y={2}, row 3 top {3})" -f $colX, $bestD, $bestY, $r3Top)
        # Leave the evidence behind: the calibration frame is what a
        # re-author needs to find the current chrome's distinct feature.
        $diag = Join-Path $OutDir 'calibration-frame0.png'
        $full.Save($diag, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "calibration: frame 0 saved to $diag"
        throw 'HARVEST_MISS: could not calibrate the selection edge in frame 0 - the selected row is not pixel-distinct in this theme'
    }
    $bandRef = Get-Pixel $px0 $colX $bestY
    # The sampled colour must not be the strip's UNSELECTED background:
    # whether the selection chrome is a ring or a fill, the tracked colour
    # has to differ from what the other rows show, or the per-frame scan
    # matches every row and tracks nothing. (The row's OWN interior is the
    # wrong yardstick -- a fill chrome agrees with itself by design.)
    # Fail here, with the sampled RGB in the message, rather than film a
    # run the tracker cannot see.
    $bgRef = Get-Pixel $px0 $colX ($r2Top + [int]($rowH * 0.5))
    if (([math]::Abs($bandRef[0] - $bgRef[0]) -le 12) -and
        ([math]::Abs($bandRef[1] - $bgRef[1]) -le 12) -and
        ([math]::Abs($bandRef[2] - $bgRef[2]) -le 12)) {
        Write-Host ("calibration: band sample rgb({0}) matches the unselected background rgb({1}) at x={2}" -f ($bandRef -join ','), ($bgRef -join ','), $colX)
        $diag = Join-Path $OutDir 'calibration-frame0.png'
        $full.Save($diag, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "calibration: frame 0 saved to $diag"
        throw 'HARVEST_MISS: the sampled selection colour is the background colour - the tracker would have nothing to follow'
    }

    # bandTop0 and the scan origin come from the SAME pass the per-frame
    # tracker uses, cross-checked against the diff edge above: if the
    # tracker's frame-0 reading disagrees with the feature the diff found,
    # the colour is not discriminating (text ink or chrome elsewhere
    # matches it first) and the run would measure the wrong feature. The
    # scan is column-scoped for the same reason -- the colour is only
    # known discriminating at the column it was sampled at.
    $trackerTop0 = Find-BandTop $px0 $bandRef 24 ([Math]::Max(1, $bestY - $rowH)) $colX 8
    if ($trackerTop0 -lt 0 -or [math]::Abs($trackerTop0 - $bestY) -gt 4) {
        Write-Host ("calibration: tracker found band at y={0} but the diff edge is y={1} (band rgb({2}) at x={3})" -f $trackerTop0, $bestY, ($bandRef -join ','), $colX)
        $diag = Join-Path $OutDir 'calibration-frame0.png'
        $full.Save($diag, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "calibration: frame 0 saved to $diag"
        throw 'HARVEST_MISS: the tracker cannot reproduce the calibrated edge in frame 0 - the band colour matches something else first'
    }
    $bandTop0 = $trackerTop0
    $expectedTop = $r3Top
    Write-Host "calibrated: selection stroke rgb($($bandRef -join ',')) at y=$bandTop0 (delta $bestD)"

    # The scripted gesture, on one clock with the capture. The press, the
    # waypoints and the release are scheduled at fixed times; the crossing
    # time is the first waypoint whose y passes row 3's centre, and the
    # oracle's windows are measured from it. The grab sits at 35% of the
    # row's width: the same actuation point the tab-drag harness's
    # committing legs use, not the row's dead centre.
    $grabX = [int]($row2.Rect.X + $row2.Rect.Width * 0.35)
    $grabY = [int]($row2.Rect.Y + $row2.Rect.Height / 2)
    # The earliest y the crossing can register at: the neighbour's midpoint
    # plus the machine's crossing hysteresis. Measuring the windows from
    # here, the first waypoint past it, is the conservative reading - if
    # the commit actually fired later, the measured gap only looks better
    # than it is, never worse.
    $crossY = [int]($row3.Rect.Y + $row3.Rect.Height / 2 + $script:HysteresisPx)
    # The waypoints must OVERSHOOT that line by real margin - the machine
    # needs the dragged center strictly past it - while stopping short of
    # row 4's own hysteresis band so the release cannot commit a second
    # crossing past it.
    $overshoot = [Math]::Max(12, [int]($rowH * 0.5))
    $endY = [int]($row3.Rect.Y + $row3.Rect.Height / 2 + $script:HysteresisPx + $overshoot)
    $schedule = [System.Collections.Generic.List[object]]::new()
    $schedule.Add([pscustomobject]@{ at = 300; act = 'press'; x = $grabX; y = $grabY })
    $moveT = 380; $crossAt = -1
    while ($moveT -le 1660) {
        $y = $grabY + [int](($endY - $grabY) * ($moveT - 380) / (1660 - 380))
        $schedule.Add([pscustomobject]@{ at = $moveT; act = 'move'; x = $grabX; y = $y })
        if ($crossAt -lt 0 -and $y -ge $crossY) { $crossAt = $moveT }
        $moveT += 70
    }
    $releaseAt = 1740
    $schedule.Add([pscustomobject]@{ at = $releaseAt; act = 'release' })
    if ($crossAt -lt 0) { throw 'HARVEST_MISS: gesture never schedules a crossing of row 3' }
    Write-Host "gesture: press@300 cross@$crossAt release@$releaseAt"

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $schedIdx = 0
    for ($i = 0; $i -lt $MaxFrames; $i++) {
        while ($schedIdx -lt $schedule.Count -and $sw.ElapsedMilliseconds -ge $schedule[$schedIdx].at) {
            $s = $schedule[$schedIdx]
            switch ($s.act) {
                'press'   { if (-not [VtDF]::DragPress($pid32, $s.x, $s.y)) { throw "HARVEST_MISS: drag press refused at $($s.x),$($s.y)" } }
                'move'    { [void][VtDF]::DragMove($s.x, $s.y) }
                'release' { [void][VtDF]::DragRelease() }
            }
            $schedIdx++
        }
        $full = [System.Drawing.Bitmap]::new($cropW, $cropH)
        $g = [System.Drawing.Graphics]::FromImage($full)
        $g.CopyFromScreen($cropX, $cropY, 0, 0, $full.Size)
        $g.Dispose()
        $frames.Add([pscustomobject]@{ t = $sw.ElapsedMilliseconds; bmp = $full })
        $remain = (($i + 1) * $IntervalMs) - $sw.ElapsedMilliseconds
        if ($remain -gt 0) { Start-Sleep -Milliseconds $remain }
    }
    Write-Host ("captured {0} frames over {1}ms" -f $frames.Count, $sw.ElapsedMilliseconds)

    # Analysis: band top per frame, saved as PNGs for the transcript. The
    # scan starts one row above the calibrated position: the crop's top
    # chrome (the window border) carries bright pixels that would match a
    # white stroke from y=0 and pin the tracker to the border forever.
    $scanFrom = [Math]::Max(1, $bandTop0 - $rowH)
    $tops = New-Object int[] $frames.Count
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $px = Get-Pixels $frames[$i].bmp
        $tops[$i] = Find-BandTop $px $bandRef 24 $scanFrom $colX 8
        $frames[$i].bmp.Save((Join-Path $OutDir ("frames\frame-{0:d3}-{1:d4}ms.png" -f $i, $frames[$i].t)))
    }
    for ($i = 0; $i -lt $frames.Count; $i++) {
        Write-Host ("frame {0:d3} t={1:d4}ms bandTop={2}" -f $i, $frames[$i].t, $tops[$i])
    }

    $bad = @($tops | Where-Object { $_ -lt 0 })
    if ($bad.Count -gt 2) {
        throw "PRODUCT_FAIL: the band left the crop in $($bad.Count) frames - the tracked row did not stay on screen"
    }
    # A frame that lost the band carries its predecessor's reading rather
    # than poisoning the run detectors with a -1 spike.
    for ($i = 1; $i -lt $tops.Count; $i++) { if ($tops[$i] -lt 0) { $tops[$i] = $tops[$i - 1] } }

    # Gap open: the first frame whose band top has risen 5px or more off
    # its pre-crossing position, measured from the scheduled crossing.
    $commitFrame = -1
    for ($i = 0; $i -lt $frames.Count; $i++) { if ($frames[$i].t -ge $crossAt) { $commitFrame = $i; break } }
    if ($commitFrame -lt 0) { throw 'HARVEST_MISS: no frame at or after the crossing' }
    $gapFrame = -1
    for ($i = $commitFrame; $i -lt $frames.Count; $i++) {
        if ($tops[$i] -le ($bandTop0 - 5)) { $gapFrame = $i; break }
    }
    $gapMs = if ($gapFrame -ge 0) { $frames[$gapFrame].t - $crossAt } else { -1 }
    Write-Host "gap: commitFrame=$commitFrame gapFrame=$gapFrame gapMs=$gapMs"

    # Convergence: the band within 2px of its final position for 6
    # consecutive frames, within 500ms of the crossing.
    $tail = @($tops | Select-Object -Last 6)
    $finalTop = [int](($tail | Measure-Object -Average).Average)
    $settledMs = -1
    $run = 0
    for ($i = $commitFrame; $i -lt $frames.Count; $i++) {
        if ([math]::Abs($tops[$i] - $finalTop) -le 2) { $run++ } else { $run = 0 }
        if ($run -ge 6) { $settledMs = $frames[$i].t - $crossAt; break }
    }
    Write-Host "converge: finalTop=$finalTop settledMs=$settledMs"

    $travel = $bandTop0 - $tops[$frames.Count - 1]

    # The layout really swapped.
    Start-Sleep -Milliseconds 600
    $after = @(Get-StripRows | ForEach-Object { $_.Name })
    $wantOrder = (@($names[0], $names[2], $names[1], $names[3]) -join ',')
    $orderOk = ($after -join ',') -eq $wantOrder
    Write-Host "order after: $($after -join ',')"

    $result = [ordered]@{
        intervalMs = $IntervalMs
        frames = $frames.Count
        bandRef = $bandRef
        bandTop0 = $bandTop0
        crossAt = $crossAt
        releaseAt = $releaseAt
        tops = $tops
        gapOpenMs = $gapMs
        settledMs = $settledMs
        travelPx = $travel
        rowHeight = $rowH
        orderAfter = ($after -join ',')
        orderOk = $orderOk
        animations = $anim
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

    if (-not $orderOk) { throw "PRODUCT_FAIL: order after the drag is [$($after -join ',')], expected [$wantOrder]" }
    if ($travel -lt [int]($rowH * 0.6)) {
        throw "PRODUCT_FAIL: the band travelled only ${travel}px over a $rowH px row - the drag never displaced row 3, so the timings measure nothing"
    }
    if ($gapFrame -lt 0) {
        throw "PRODUCT_FAIL: the gap never opened - row 3's band never left its slot after the crossing"
    }
    if ($gapMs -gt (2 * $IntervalMs + 40)) {
        throw "PRODUCT_FAIL: the gap opened ${gapMs}ms after the crossing; the oracle allows 2 frames ($((2 * $IntervalMs + 40))ms)"
    }
    if ($settledMs -lt 0) {
        throw 'PRODUCT_FAIL: the band never settled within the film - offsets did not converge'
    }
    if ($settledMs -gt 500) {
        throw "PRODUCT_FAIL: offsets converged ${settledMs}ms after the crossing; the oracle allows 500ms"
    }
    Write-Host "PASS gap=${gapMs}ms settled=${settledMs}ms travel=${travel}px order=$($after -join ',')"
}
catch {
    if ($null -ne $proc -and -not $proc.HasExited) {
        try {
            $rc = [VtDF]::RectOf($script:MainHwnd64)
            if ($null -ne $rc) {
                $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
                $bmp.Save((Join-Path $OutDir 'shots\fail-state.png'))
                $g.Dispose(); $bmp.Dispose()
            }
        } catch { }
    }
    $script:FatalWasProduct = ("$_" -like 'PRODUCT_FAIL*')
    Write-Host "$_" -ForegroundColor Red
}
finally {
    if ($null -ne $proc -and -not $proc.HasExited) {
        try { $proc.Kill($true); [void]$proc.WaitForExit(3000) } catch { }
    }
    if ($origXdgSet) { $env:XDG_CONFIG_HOME = $origXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
    if ((Test-Path $tempXdg)) {
        Remove-Item -Recurse -Force $tempXdg -ErrorAction SilentlyContinue
    }
    Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $ExePath
}

$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
if ($crashGrew) {
    Write-Host 'PRODUCT_FAIL: crash.log grew during the run' -ForegroundColor Red
    exit 2
}
if ($script:FatalWasProduct) { exit 2 }
if ($script:FatalWasProduct -eq $false) { exit 1 }
exit 0
