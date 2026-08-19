#requires -Version 7
<#
    Scrollback-search fuzz harness.

    The oracle is the terminal itself: TerminalControl's UIA text provider
    exposes the whole screen including scrollback as one document, so the
    harness can read the real corpus and count matches independently of
    libghostty. Every needle it types is checked against that count rather
    than against a hardcoded expectation, which is what lets the needles be
    randomly drawn from live terminal content.

    Search is ASCII case-insensitive (src/terminal/search/sliding_window.zig
    uses std.ascii.indexOfIgnoreCase), so the oracle folds case the same way.

    Failures are recorded and the run continues: one broken invariant should
    not hide the rest.

    Run it with `just search-fuzz`, optionally passing "-Seed N -Iterations N".
    A seed reproduces an entire op sequence, so a finding can be replayed.

    Exit codes, numbered to match verified-input-probe.ps1 and the
    mouse-fuzz scripts:
      0  clean
      2  product findings - see run-<seed>.json and shots/ under -OutDir
      1  the harness could not run (no window, foreground stolen, shell never
         came up); the product was never exercised, so retry rather than file

    A seed replays the op sequence, but not the corpus-slice needles drawn
    from live terminal text: those depend on the shell prompt and the window
    width, so a finding on one is reproducible only in the same environment.

    Findings this harness has caught, kept here as the regression list:
      - reopening the bar left the needle visible with no live search behind
        it, so navigation was inert until the text was edited
      - the counter rendered "0 of -1" once a search had been torn down
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$Seed = 1337,
    [int]$Iterations = 40,
    [switch]$KeepOpen,
    # Launching under a throwaway XDG_CONFIG_HOME turned out to make the app
    # unstable at startup on this machine (repeated 0xc000027b stowed
    # exceptions in CoreMessagingXP), while the user's own config dir is
    # stable. Default to the real environment so the fuzz exercises the app
    # the way it is actually run; -IsolatedConfig opts back into the
    # throwaway dir when a controlled config matters more than stability.
    [switch]$IsolatedConfig
)
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

public static class SFz {
    public const uint KEYEVENTF_KEYUP    = 0x0002;
    public const uint KEYEVENTF_UNICODE  = 0x0004;
    public const uint MOUSEEVENTF_WHEEL  = 0x0800;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_SHIFT   = 0x10;
    public const ushort VK_RETURN  = 0x0D;
    public const ushort VK_ESCAPE  = 0x1B;
    public const ushort VK_BACK    = 0x08;
    public const ushort VK_F       = 0x46;
    public const ushort VK_A       = 0x41;
    public const ushort VK_C       = 0x43;
    public const ushort VK_T       = 0x54;
    public const ushort VK_TAB     = 0x09;

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
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll", SetLastError=true)] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    public class WinRect { public int L,T,R,B; public int W { get { return R-L; } } public int Hh { get { return B-T; } } }

    public static IntPtr P(long hwnd) { return new IntPtr(hwnd); }
    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static uint PidOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }

    public static WinRect RectOf(long hwnd) {
        var h = P(hwnd); RECT r;
        if (!IsWindow(h) || !GetWindowRect(h, out r)) return null;
        var wr = new WinRect { L=r.L,T=r.T,R=r.R,B=r.B };
        return (wr.W < 80 || wr.Hh < 80) ? null : wr;
    }

    // Synthesized input goes to whatever owns the foreground, never to a
    // handle. Under the foreground lock a bare SetForegroundWindow fails
    // silently, so every send confirms the target actually holds it first --
    // otherwise the keystrokes land in whatever app grabbed focus.
    //
    // Attaching to the current foreground thread's input queue lifts that
    // lock for the duration of the call, which is what makes the steal
    // reliable when another app (an editor, a chat client repainting) keeps
    // pulling focus back mid-run.
    public static bool Focus(IntPtr expected) {
        if (expected == IntPtr.Zero) return false;
        for (int i = 0; i < 40; i++) {
            if (GetForegroundWindow() == expected) return true;
            var fg = GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId2(fg);
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

    static uint GetWindowThreadProcessId2(IntPtr h) { uint pid; return GetWindowThreadProcessId(h, out pid); }

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

    // Posted WM_CHAR / WM_KEYDOWN do not reach this app at all: measured
    // zero characters landing across every delay, while the same text sent
    // through SendInput lands in full. Everything here therefore goes
    // through the global input queue behind the foreground guard above.

    /// Type a literal string. KEYEVENTF_UNICODE bypasses the keyboard layout,
    /// so a needle can carry any BMP character without a VK mapping.
    public static bool TypeText(IntPtr expected, string text, int perCharMs) {
        foreach (char c in text) {
            if (!Focus(expected)) return false;
            Send(new INPUT[] { Unicode(c, false), Unicode(c, true) });
            Thread.Sleep(perCharMs);
        }
        return true;
    }

    /// Unmodified key press, still as real input.
    public static bool KeyPress(IntPtr expected, ushort vk, int gapMs) {
        if (!Focus(expected)) return false;
        Send(new INPUT[] { Key(vk, false) });
        Thread.Sleep(gapMs);
        Send(new INPUT[] { Key(vk, true) });
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

    /// A posted WM_CHAR only lands if XAML has a focused element, and the
    /// island does not take focus from the window merely being foreground.
    /// One real click on the app's own pixels is what arms it. The point is
    /// probed before and after the move so a toast or flyout that arrives
    /// mid-settle cannot take the click.
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

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP   = 0x0004;

    public static bool Wheel(uint pid, int x, int y, int notches) {
        var hit = WindowFromPoint(new POINT { X=x, Y=y });
        if (PidOf(hit) != pid) return false;
        if (!SetCursorPos(x, y)) return false;
        Thread.Sleep(30);
        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(notches * 120)), UIntPtr.Zero);
        Thread.Sleep(80);
        return true;
    }

    public static bool Resize(long hwnd, int w, int h) {
        var rc = RectOf(hwnd);
        if (rc == null) return false;
        return MoveWindow(P(hwnd), rc.L, rc.T, w, h, true);
    }
}
'@

# ---- window / UIA plumbing -------------------------------------------------

$UIA = [System.Windows.Automation.AutomationElement]
$TS  = [System.Windows.Automation.TreeScope]

function Get-Main([uint32]$ProcId) {
    $hits = [System.Collections.Generic.List[object]]::new()
    $cb = [SFz+EnumProc]{
        param($h, $lp)
        [uint32]$o = 0; [void][SFz]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -ne $ProcId -or -not [SFz]::IsWindowVisible($h)) { return $true }
        if ([SFz]::ClassOf($h) -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [SFz]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64 = $hwnd64; Area = ($rc.W * $rc.Hh) })
        return $true
    }
    [void][SFz]::EnumWindows($cb, [IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending | Select-Object -First 1
}

function Wait-Ready($proc) {
    $dl = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 300
        $proc.Refresh()
        if ($proc.HasExited) {
            Add-Finding 'crash' "app exited during startup, code $($proc.ExitCode)" @{}
            throw 'app exited during startup'
        }
        $m = Get-Main ([uint32]$proc.Id)
        if ($m) { Start-Sleep -Seconds 2; return $m }
    }
    throw 'no main window appeared'
}

# The captured hwnd can go stale: the launch splash hands off to another
# window, and a killed instance leaves a handle that UIA rejects outright
# ("Unrecognized error"). Re-resolve once from the process before giving up,
# so a window swap does not abort a whole run.
function Get-Root {
    try { return $UIA::FromHandle([SFz]::P($script:Hwnd64)) } catch { }
    $m = Get-Main ([uint32]$script:Proc.Id)
    if ($null -eq $m) { throw 'the app has no main window' }
    $script:Hwnd64 = [int64]$m.Hwnd64
    return $UIA::FromHandle([SFz]::P($script:Hwnd64))
}

function Find-ByType($root, $controlType) {
    if ($null -eq $root) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $controlType)
    $found = $root.FindAll($TS::Descendants, $cond)
    $out = @(); foreach ($i in $found) { $out += $i }; return $out
}

function Find-ByName($root, [string]$name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)
    return $root.FindFirst($TS::Descendants, $cond)
}

# The terminal pane exposes ClassName TerminalControl and supports TextPattern
# over the whole screen document (viewport + scrollback).
function Get-TerminalElements($root) {
    if ($null -eq $root) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ClassNameProperty, 'TerminalControl')
    try { $found = $root.FindAll($TS::Descendants, $cond) } catch { return @() }
    $out = @(); foreach ($i in $found) { $out += $i }; return , $out
}

# UIA FindAll comes back empty while the app is mid-render, so every terminal
# lookup retries rather than indexing whatever the first probe returned.
function Get-Term([int]$timeoutMs = 8000) {
    $dl = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $dl) {
        # No @() here: these helpers already comma-return, and wrapping that
        # in @() yields a one-element array holding the inner array, so the
        # count guard is always true and the retry never retries.
        $els = Get-TerminalElements (Get-Root)
        if ($els.Count -gt 0) { return $els[0] }
        Start-Sleep -Milliseconds 200
    }
    throw 'no TerminalControl in the UIA tree'
}

function Get-TerminalText($el) {
    if ($null -eq $el) { return '' }
    try {
        $tp = $el.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        return $tp.DocumentRange.GetText(-1)
    } catch { return '' }
}

# The counter TextBlock renders "" / "No matches" / "N of M". A window-wide
# scan for that shape is more robust than walking the search bar's layout
# panels, which WinUI does not surface consistently in the control view.
$CounterRe = '^(?:-?\d+ of -?\d+|No matches)$'

function Get-CounterTexts($root) {
    $out = @()
    foreach ($t in (Find-ByType $root ([System.Windows.Automation.ControlType]::Text))) {
        try { $n = $t.Current.Name } catch { continue }
        if ($n -match $CounterRe) { $out += $n }
    }
    # Comma-wrap: a bare single-element return unrolls to a scalar string, and
    # indexing that gives its first character rather than the counter.
    return , $out
}

# Returns the single counter in the window. More than one means more than
# one pane or tab has its bar open; the caller cannot attribute a count in
# that case, so say so rather than silently joining them into one string.
function Get-Counter($root) {
    $all = Get-CounterTexts $root
    if ($all.Count -eq 0) { return '' }
    if ($all.Count -gt 1) { return "<ambiguous: $($all -join ' | ')>" }
    return [string]$all[0]
}

function Get-NeedleBox($root) { return Find-ByName $root 'Search scrollback' }

# Put focus back in the needle box if something moved it. Without this a
# single stray navigation key turns every later "typed X, box holds Y"
# assertion into a false finding.
function Set-NeedleFocus {
    if ((Get-FocusedName) -eq 'Search scrollback') { return $true }
    $box = Get-NeedleBox (Get-Root)
    if ($null -eq $box) { return $false }
    try { $box.SetFocus() } catch { return $false }
    Start-Sleep -Milliseconds 200
    return (Get-FocusedName) -eq 'Search scrollback'
}

function Get-NeedleText($root) {
    $box = Get-NeedleBox $root
    if ($null -eq $box) { return $null }
    try {
        $vp = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        return $vp.Current.Value
    } catch { return $null }
}

function Test-SearchBarOpen($root) { return $null -ne (Get-NeedleBox $root) }

function Get-FocusedName {
    try { return $UIA::FocusedElement.Current.Name } catch { return '<none>' }
}

# ---- oracle ---------------------------------------------------------------

# Non-overlapping, ASCII-case-insensitive occurrence count, matching the way
# the sliding window advances past each hit.
function Measure-Occurrences([string]$haystack, [string]$needle) {
    if ([string]::IsNullOrEmpty($needle)) { return 0 }
    $n = 0; $i = 0
    while ($true) {
        $j = $haystack.IndexOf($needle, $i, [StringComparison]::OrdinalIgnoreCase)
        if ($j -lt 0) { break }
        $n++; $i = $j + $needle.Length
    }
    return $n
}

# Both haystacks unwrap soft wraps: the UIA document comes from
# Screen.selectionString with unwrap = true, and the search window builds its
# haystack with PageFormatter unwrap = true. So a match cannot straddle a
# wrap in one and not the other, and the count is exact rather than a range.
# The only remaining difference is trailing whitespace (the document keeps
# it, the search haystack trims it), which is why needles containing
# whitespace never reach a strict assertion.
#
# An earlier version widened this to a range using a newlines-stripped
# ceiling. That admitted matches spanning a hard line break, which cannot
# exist in the product's haystack: a two-character needle like "0Z" then
# passed against any reported count up to ~18 in the seeded corpus.
function Get-ExpectedCount([string]$doc, [string]$needle) {
    return Measure-Occurrences $doc $needle
}

# ---- assertions -----------------------------------------------------------

$script:Findings = [System.Collections.Generic.List[object]]::new()
$script:Checks = 0

function Add-Finding([string]$kind, [string]$detail, [hashtable]$data) {
    $f = [ordered]@{ kind = $kind; detail = $detail; iteration = $script:Iter }
    if ($data) { foreach ($k in $data.Keys) { $f[$k] = $data[$k] } }
    $script:Findings.Add([pscustomobject]$f)
    Write-Host "  FAIL [$kind] $detail" -ForegroundColor Red
}

function Assert-That([bool]$ok, [string]$kind, [string]$detail, [hashtable]$data) {
    $script:Checks++
    if (-not $ok) { Add-Finding $kind $detail $data }
    return $ok
}

function Shot([string]$name) {
    $rc = [SFz]::RectOf($script:Hwnd64)
    if ($null -eq $rc) { return }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir "shots\$name.png"))
    $g.Dispose(); $bmp.Dispose()
}

# Counting pixels close to the search highlight colors is the only way to
# tell "libghostty found matches" apart from "the renderer painted them".
# Defaults come from Config.zig: search-background #FFE082, and
# search-selected-background #F2A57E.
function Measure-HighlightPixels([int]$r, [int]$g, [int]$b, [int]$tol = 24) {
    $rc = [SFz]::RectOf($script:Hwnd64)
    if ($null -eq $rc) { return -1 }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $gfx.Dispose()
    $rect = New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $data.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    $bmp.Dispose()

    $hits = 0
    for ($i = 0; $i -lt $bytes.Length; $i += 4) {
        # BGRA
        if ([Math]::Abs($bytes[$i + 2] - $r) -le $tol -and
            [Math]::Abs($bytes[$i + 1] - $g) -le $tol -and
            [Math]::Abs($bytes[$i] - $b) -le $tol) { $hits++ }
    }
    return $hits
}

function Assert-Alive([string]$where) {
    $script:Proc.Refresh()
    if ($script:Proc.HasExited) {
        Add-Finding 'crash' "process exited during $where (code $($script:Proc.ExitCode))" @{}
        throw "APP_DIED at $where"
    }
}

# ---- input wrappers -------------------------------------------------------

# Name whoever stole the foreground: without it "FOREGROUND_LOST" says
# nothing about whether the app died, a toast appeared, or the harness is
# fighting its own console.
function Get-ForegroundInfo {
    $h = [SFz]::GetForegroundWindow()
    if ($h -eq [IntPtr]::Zero) { return 'foreground=<none>' }
    $cls = [SFz]::ClassOf($h)
    $fpid = [SFz]::PidOf($h)
    $name = try { (Get-Process -Id $fpid -ErrorAction Stop).ProcessName } catch { '?' }
    $mine = if ($h.ToInt64() -eq $script:Hwnd64) { ' (target)' } else { '' }
    return "foreground=$cls pid=$fpid proc=$name$mine"
}

function Send-Chord([ushort[]]$mods, [ushort]$key, [int]$settleMs = 250) {
    $ok = if ($mods.Count -eq 0) {
        [SFz]::KeyPress([SFz]::P($script:Hwnd64), $key, 40)
    } else {
        [SFz]::Chord([SFz]::P($script:Hwnd64), $mods, $key)
    }
    if (-not $ok) { throw "FOREGROUND_LOST on key: $(Get-ForegroundInfo)" }
    Start-Sleep -Milliseconds $settleMs
}

function Send-Text([string]$text, [int]$perCharMs = 30) {
    if (-not [SFz]::TypeText([SFz]::P($script:Hwnd64), $text, $perCharMs)) {
        throw "FOREGROUND_LOST while typing '$text': $(Get-ForegroundInfo)"
    }
}

# Escape drops the PSReadLine buffer; Ctrl+C aborts a continuation prompt.
# Both are harmless on an already-empty line.
function Clear-CommandLine {
    Send-Chord @() ([SFz]::VK_ESCAPE) 200
    Send-Chord @([SFz]::VK_CONTROL) ([SFz]::VK_C) 300
    Send-Chord @() ([SFz]::VK_RETURN) 500
}

function Open-SearchBar { Send-Chord @([SFz]::VK_CONTROL, [SFz]::VK_SHIFT) ([SFz]::VK_F) 500 }
function Press-Enter     { Send-Chord @() ([SFz]::VK_RETURN) 260 }
function Press-ShiftEnter{ Send-Chord @([SFz]::VK_SHIFT) ([SFz]::VK_RETURN) 260 }
function Press-Escape    { Send-Chord @() ([SFz]::VK_ESCAPE) 300 }
function Clear-Needle {
    # Select-all then Backspace: shorter and less racy than N backspaces.
    Send-Chord @([SFz]::VK_CONTROL) ([SFz]::VK_A) 120
    Send-Chord @() ([SFz]::VK_BACK) 250
}

# Poll until the counter stops changing, so assertions never race the
# progressive search (24ms refresh tick plus incremental feeding).
function Wait-Counter([int]$timeoutMs = 6000, [int]$stableMs = 400) {
    $dl = (Get-Date).AddMilliseconds($timeoutMs)
    $last = $null; $since = Get-Date
    while ((Get-Date) -lt $dl) {
        $cur = Get-Counter (Get-Root)
        if ($cur -ne $last) { $last = $cur; $since = Get-Date }
        elseif (((Get-Date) - $since).TotalMilliseconds -ge $stableMs) { return $cur }
        Start-Sleep -Milliseconds 90
    }
    return $last
}

function Get-CounterParts([string]$text) {
    if ($text -match '^(-?\d+) of (-?\d+)$') { return @([int]$Matches[1], [int]$Matches[2]) }
    if ($text -eq 'No matches') { return @(0, 0) }
    return $null
}

# ---- run ------------------------------------------------------------------

$rng = [System.Random]::new($Seed)
$tempXdg = Join-Path $env:TEMP "wintty-search-fuzz-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path (Join-Path $tempXdg 'wintty') | Out-Null
@'
windows-single-instance = false
window-save-state = never
window-width = 120
window-height = 34
scrollback-limit = 10000000
window-theme = wintty
'@ | Set-Content (Join-Path $tempXdg 'wintty\config.wintty') -Encoding utf8

$origXdg = $env:XDG_CONFIG_HOME
$script:Proc = $null
$script:Iter = 0
$script:ExitCode = 0
$result = [ordered]@{
    seed = $Seed; iterations = $Iterations; ops = @()
}

$script:CrashBaseline = 0
$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
if (Test-Path $crashPath) { $script:CrashBaseline = (Get-Item $crashPath).Length }

try {
    if ($IsolatedConfig) { $env:XDG_CONFIG_HOME = $tempXdg }
    if (-not (Test-Path $ExePath)) { throw "missing exe: $ExePath" }
    Write-Host ("config: {0}" -f $(if ($IsolatedConfig) { $tempXdg } else { 'user environment' }))

    # Never kill by name. Developers keep builds from several worktrees open
    # at once, and a harness that force-kills every Wintty takes down work it
    # has nothing to do with. Refuse to start instead: a running instance
    # would absorb this launch anyway when single-instance is on, so there is
    # no run to be had either way.
    $script:ExeFull = (Resolve-Path $ExePath).Path
    $script:PreExisting = @(Get-Process Wintty -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Id)
    if ($script:PreExisting.Count -gt 0) {
        throw ("close the running Wintty first (pid $($script:PreExisting -join ', ')); " +
               'this harness will not kill instances it did not start')
    }

    $script:Proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path -Parent (Resolve-Path $ExePath))
    $pid32 = [uint32]$script:Proc.Id
    $main = Wait-Ready $script:Proc
    $script:Hwnd64 = [int64]$main.Hwnd64
    [void][SFz]::Focus([SFz]::P($script:Hwnd64))
    Start-Sleep -Milliseconds 800

    $root = Get-Root
    $terms = Get-TerminalElements $root
    if ($terms.Count -lt 1) { throw 'no TerminalControl exposed to UIA' }
    Write-Host "terminal panes: $($terms.Count)"

    # Arm the XAML island (see SFz.Click). Without this every posted
    # character is dropped and the terminal never sees a keystroke.
    $rc0 = [SFz]::RectOf($script:Hwnd64)
    if ($null -eq $rc0) { throw 'window rect is degenerate; cannot arm XAML focus' }
    if (-not [SFz]::Click($pid32, [int]($rc0.L + $rc0.W / 2), [int]($rc0.T + $rc0.Hh * 0.7))) {
        throw 'could not click into the terminal to arm XAML focus'
    }
    Start-Sleep -Milliseconds 400

    # ---- seed the scrollback ---------------------------------------------
    # Every token is assembled at runtime so the echoed command line never
    # contains the needle itself; otherwise the echo would inflate the count
    # in ways the oracle cannot see coming.
    $payload = @(
        "`$a='ZQ'+'XW'; `$b='PL'+'MK'; `$c='VR'+'TN'",
        '1..180 | % { "$a row $_" }',
        '1..47  | % { "$b item $_" }',
        '"$c singleton"',
        '"$a$b adjacent"',
        '"done"+"-marker"'
    ) -join '; '

    Write-Host 'seeding scrollback...'

    # Wait for the shell to actually draw something. Typing into a terminal
    # that has not started yet loses the keystrokes, and every later oracle
    # count is measured against the corpus that typing was supposed to
    # create.
    $dl = (Get-Date).AddSeconds(30)
    $preseed = ''
    while ((Get-Date) -lt $dl) {
        $preseed = Get-TerminalText (Get-Term)
        if ($preseed.Trim().Length -gt 0) { break }
        Start-Sleep -Milliseconds 500
    }
    $preseed | Set-Content (Join-Path $OutDir 'doc-preseed.txt') -Encoding utf8
    if ($preseed.Trim().Length -eq 0) {
        throw 'the terminal never rendered any text; the shell did not start'
    }

    # The payload is PowerShell. Sniffing the prompt is unreliable (a themed
    # prompt looks nothing like "PS >"), so ask the shell what it is and wait
    # for the answer. This doubles as proof that typed keys are landing.
    function Test-Pwsh([int]$timeoutMs) {
        Send-Text '"SHELL"+"OK-$($PSVersionTable.PSEdition)"' 30
        Send-Chord @() ([SFz]::VK_RETURN) 400
        $dl = (Get-Date).AddMilliseconds($timeoutMs)
        while ((Get-Date) -lt $dl) {
            if ((Get-TerminalText (Get-Term)) -match 'SHELLOK-Core') { return $true }
            Start-Sleep -Milliseconds 400
        }
        return $false
    }

    $inPwsh = Test-Pwsh 25000
    if (-not $inPwsh) {
        Clear-CommandLine
        Send-Text 'pwsh -NoLogo -NoProfile' 30
        Send-Chord @() ([SFz]::VK_RETURN) 400
        Start-Sleep -Seconds 6
        $inPwsh = Test-Pwsh 25000
    }
    if (-not $inPwsh) {
        (Get-TerminalText (Get-Term)) | Set-Content (Join-Path $OutDir 'doc-noshell.txt') -Encoding utf8
        throw 'no PowerShell in the terminal (typed keys may not be landing), see doc-noshell.txt'
    }

    # A dropped character can leave PowerShell in continuation mode, where
    # everything typed afterwards is swallowed as more of an unterminated
    # string. Reset the line before committing to the payload.
    Clear-CommandLine

    Send-Text $payload 30
    Start-Sleep -Milliseconds 400
    (Get-TerminalText (Get-Term)) | Set-Content (Join-Path $OutDir 'doc-typed.txt') -Encoding utf8
    Send-Chord @() ([SFz]::VK_RETURN) 400
    Start-Sleep -Seconds 3

    $term = Get-Term
    $doc = ''
    $dl = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $dl) {
        $doc = Get-TerminalText $term
        if ($doc -match 'done-marker') { break }
        Start-Sleep -Milliseconds 500
    }
    $doc | Set-Content (Join-Path $OutDir 'doc-seeded.txt') -Encoding utf8
    if ($doc -notmatch 'done-marker') {
        throw "payload never completed (no done-marker); document is $($doc.Length) chars, see doc-*.txt"
    }
    Start-Sleep -Milliseconds 900
    $doc = Get-TerminalText $term
    Write-Host "corpus: $($doc.Length) chars, $((($doc -split "`n").Count)) rows"
    $result.corpusChars = $doc.Length

    # Needle pool. Strict needles are checked against the oracle; the rest
    # only have to not break an invariant (no crash, counter well-formed).
    $strict = @('ZQXW', 'zqxw', 'ZqXw', 'PLMK', 'VRTN', 'ZQXWPLMK', 'NOTHERE9X', 'row 1', 'item 4')
    # No Tab in this pool: it is a focus-navigation key rather than a
    # character, so typing it moves focus off the needle box and every
    # needle after it lands on a button. That measures XAML focus, not
    # search.
    $weird  = @(':', 'a:b', ' ', '  ', 'row ', '"', '\', '%', '$a', '.*', '[', '(', '?', 'ZQXW ', ' ZQXW',
                'ábç', 'こんにちは', '😀', ('Z' * 200), '-', '--', '0', 'e')

    function Get-RandomNeedle {
        $r = $rng.Next(100)
        if ($r -lt 55) { return $strict[$rng.Next($strict.Count)] }
        if ($r -lt 85) { return $weird[$rng.Next($weird.Count)] }
        # A random slice of the live corpus: the oracle handles it the same way.
        $printable = ($doc -replace "[^\x20-\x7E]", ' ')
        # Both draws happen before any early return, so the stream advances
        # by a fixed amount whatever the corpus looks like.
        $len = $rng.Next(2, 7)
        $pos = $rng.Next(0, 100000)
        if ($printable.Length -le $len) { return 'ZQXW' }
        $at = $pos % ($printable.Length - $len)
        return $printable.Substring($at, $len).Trim()
    }

    function Assert-CounterMatchesOracle([string]$needle, [string]$counter) {
        $parts = Get-CounterParts $counter
        if ($null -eq $parts) {
            Add-Finding 'counter-shape' "needle '$needle' produced counter '$counter'" @{ needle = $needle; counter = $counter }
            return
        }
        $total = $parts[1]
        $expected = Get-ExpectedCount $doc $needle
        $ok = ($total -eq $expected)
        [void](Assert-That $ok 'count-mismatch' `
            "needle '$needle': reported $total, oracle expects $expected" `
            @{ needle = $needle; reported = $total; oracle = $expected; counter = $counter })
        if (-not $ok) { Shot ("mismatch-{0:d3}" -f $script:Iter) }
    }

    # ---- baseline invariants ---------------------------------------------
    Write-Host "`n== baseline ==" -ForegroundColor Cyan
    $docBefore = $doc

    Open-SearchBar
    Assert-Alive 'open'
    $root = Get-Root
    [void](Assert-That (Test-SearchBarOpen $root) 'bar-missing' 'Ctrl+Shift+F did not surface the search bar' @{})

    # Focus must land in the needle box, or the user types into the shell.
    $focused = Get-FocusedName
    [void](Assert-That ($focused -eq 'Search scrollback') 'focus-not-in-needle' `
        "after Ctrl+Shift+F the focused element is '$focused', expected 'Search scrollback'" @{ focused = $focused })

    Send-Text 'ZQXW'
    Start-Sleep -Milliseconds 400
    $c = Wait-Counter
    Write-Host "  counter after typing ZQXW: '$c'"
    Assert-CounterMatchesOracle 'ZQXW' $c

    # A keystroke that reaches the shell changes the document. Compare after
    # the same settle the counter got.
    $docNow = Get-TerminalText (Get-Term)
    [void](Assert-That ($docNow -ceq $docBefore) 'keystroke-leak' `
        'typing the needle changed the terminal document, so keys reached the shell' `
        @{ beforeLen = $docBefore.Length; afterLen = $docNow.Length })

    # Navigation must walk 1..N and wrap.
    $parts = Get-CounterParts $c
    if ($null -ne $parts -and $parts[1] -gt 2) {
        $total = $parts[1]
        $seen = @()
        for ($k = 0; $k -lt 4; $k++) {
            Press-Enter
            $cc = Wait-Counter 4000 250
            $pp = Get-CounterParts $cc
            $seen += if ($null -eq $pp) { -1 } else { $pp[0] }
        }
        Write-Host "  next x4 -> $($seen -join ', ') (of $total)"
        $monotonic = $true
        for ($k = 1; $k -lt $seen.Count; $k++) {
            $prev = $seen[$k - 1]; $cur = $seen[$k]
            # -1 marks a counter that could not be parsed; reporting that as
            # a navigation fault would blame the wrong thing.
            if ($prev -lt 0 -or $cur -lt 0) { continue }
            if ($cur -ne ($prev % $total) + 1) { $monotonic = $false }
        }
        [void](Assert-That ($seen[0] -ge 1) 'nav-no-selection' "first Enter left the index at $($seen[0])" @{ seen = $seen })
        [void](Assert-That $monotonic 'nav-not-sequential' `
            "Enter did not step 1-by-1 with wrap: $($seen -join ',') of $total" @{ seen = $seen; total = $total })

        Press-ShiftEnter
        $cb = Wait-Counter 4000 250
        $pb = Get-CounterParts $cb
        $expectBack = if ($seen[-1] -le 1) { $total } else { $seen[-1] - 1 }
        [void](Assert-That ($null -ne $pb -and $pb[0] -eq $expectBack) 'nav-prev-wrong' `
            "Shift+Enter from $($seen[-1]) gave $cb, expected index $expectBack" @{ from = $seen[-1]; got = $cb })
    }

    # A correct counter proves libghostty found the matches; it says nothing
    # about whether the renderer painted them. The PLMK rows are the ones
    # sitting in the viewport after seeding, so their highlights must show up
    # as pixels in the window.
    Clear-Needle
    Start-Sleep -Milliseconds 600
    # Measure the baseline with no search active. Taking it while the
    # previous needle was still highlighted compared one search against
    # another rather than against an unhighlighted screen.
    $baseHighlight = Measure-HighlightPixels 255 224 130
    Send-Text 'PLMK'
    Start-Sleep -Milliseconds 400
    $cH = Wait-Counter
    Assert-CounterMatchesOracle 'PLMK' $cH
    Start-Sleep -Milliseconds 500
    $litHighlight = Measure-HighlightPixels 255 224 130
    $litSelected = Measure-HighlightPixels 242 165 126
    Write-Host "  highlight pixels: base=$baseHighlight lit=$litHighlight selected=$litSelected"
    Shot 'highlight-plmk'
    [void](Assert-That ($litHighlight -gt ($baseHighlight + 200)) 'no-match-highlight' `
        "search matches are not painted: $baseHighlight highlight-colored pixels before, $litHighlight after" `
        @{ before = $baseHighlight; after = $litHighlight })
    Press-Enter
    Start-Sleep -Milliseconds 700
    $selAfter = Measure-HighlightPixels 242 165 126
    Shot 'highlight-plmk-selected'
    [void](Assert-That ($selAfter -gt ($litSelected + 50)) 'no-selected-highlight' `
        "the selected match is not painted in its own color: $litSelected before Enter, $selAfter after" `
        @{ before = $litSelected; after = $selAfter })

    Press-Escape
    $root = Get-Root
    [void](Assert-That (-not (Test-SearchBarOpen $root)) 'esc-did-not-close' 'Escape left the search bar open' @{})

    # After Escape the terminal must take input again.
    $docPre = Get-TerminalText (Get-Term)
    Send-Text 'echoX'
    Start-Sleep -Milliseconds 600
    $docPost = Get-TerminalText (Get-Term)
    [void](Assert-That ($docPost -ne $docPre) 'focus-not-returned' `
        'after Escape, typing did not reach the terminal' @{})
    Clear-Needle  # harmless if focus is in the shell: Ctrl+A / Backspace
    Send-Chord @() ([SFz]::VK_BACK) 80
    for ($k = 0; $k -lt 8; $k++) { Send-Chord @() ([SFz]::VK_BACK) 30 }

    # ---- randomized sweep ------------------------------------------------
    Write-Host "`n== fuzz ($Iterations iterations, seed $Seed) ==" -ForegroundColor Cyan
    $barOpen = $false
    for ($script:Iter = 1; $script:Iter -le $Iterations; $script:Iter++) {
        Assert-Alive "iteration $script:Iter"
        $root = Get-Root
        $barOpen = Test-SearchBarOpen $root

        # Draw unconditionally, even when the op is forced: letting product
        # state decide whether the RNG advances makes the seed stop
        # replaying the sequence after the first divergence.
        $roll = $rng.Next(100)
        $op = if (-not $barOpen) { 'open' } else {
            if     ($roll -lt 34) { 'type' }
            elseif ($roll -lt 52) { 'next' }
            elseif ($roll -lt 64) { 'prev' }
            elseif ($roll -lt 72) { 'clear' }
            elseif ($roll -lt 80) { 'close' }
            elseif ($roll -lt 86) { 'scroll' }
            elseif ($roll -lt 92) { 'resize' }
            else                  { 'emit' }
        }

        $detail = ''
        switch ($op) {
            'open' {
                Open-SearchBar
                $r2 = Get-Root
                [void](Assert-That (Test-SearchBarOpen $r2) 'bar-missing' 'Ctrl+Shift+F did not open the bar' @{})
                $f = Get-FocusedName
                [void](Assert-That ($f -eq 'Search scrollback') 'focus-not-in-needle' `
                    "focus is '$f' after opening" @{ focused = $f })
                $detail = "focus=$f"

                # The needle survives a close, and reopening re-runs it.
                # The counter next to it therefore has to describe that
                # needle's live results, not whatever the previous search
                # left behind and not a torn-down search's -1.
                $keptNeedle = Get-NeedleText (Get-Root)
                if ($keptNeedle -and $keptNeedle.Length -gt 0) {
                    $c = Wait-Counter 3000 300
                    $detail += " kept='$keptNeedle' counter='$c'"
                    if ($keptNeedle -match '^[\x21-\x7E]{2,}$') {
                        Assert-CounterMatchesOracle $keptNeedle $c
                    }
                }
            }
            'type' {
                $needle = Get-RandomNeedle
                if (-not (Set-NeedleFocus)) {
                    # Typing with focus elsewhere sends the needle to the
                    # shell, which corrupts the corpus every later oracle
                    # count is computed against.
                    Add-Finding 'needle-focus-lost' `
                        "could not put focus in the needle box before typing '$needle'" `
                        @{ needle = $needle; focused = (Get-FocusedName) }
                    break
                }
                Clear-Needle
                if ($needle.Length -gt 0) { Send-Text $needle }
                Start-Sleep -Milliseconds 300
                $c = Wait-Counter
                $detail = "needle='$needle' counter='$c'"
                if ($needle.Trim().Length -gt 0) {
                    # Strict counting needs an ASCII needle with no whitespace:
                    # the oracle folds case with ASCII rules like the search
                    # does, but row padding and wrapping make whitespace counts
                    # differ between the UIA document and the terminal grid for
                    # reasons that are not defects.
                    if ($needle -match '^[\x21-\x7E]{2,}$') { Assert-CounterMatchesOracle $needle $c }
                    else {
                        [void](Assert-That ($c -eq '' -or $null -ne (Get-CounterParts $c)) 'counter-shape' `
                            "non-ASCII needle '$needle' produced '$c'" @{ needle = $needle; counter = $c })
                    }
                }
                $box = Get-NeedleText (Get-Root)
                if ($null -ne $box -and $needle -match '^[\x20-\x7E]+$') {
                    [void](Assert-That ($box -ceq $needle) 'needle-box-drift' `
                        "typed '$needle' but the box holds '$box'" @{ typed = $needle; box = $box })
                }
            }
            'next' {
                $before = Get-CounterParts (Get-Counter (Get-Root))
                Press-Enter
                $c = Wait-Counter 4000 250
                $after = Get-CounterParts $c
                $detail = "next -> '$c'"
                if ($null -ne $before -and $null -ne $after -and $before[1] -gt 0) {
                    $exp = ($before[0] % $before[1]) + 1
                    [void](Assert-That ($after[0] -eq $exp) 'nav-not-sequential' `
                        "next from $($before[0])/$($before[1]) gave $($after[0]), expected $exp" `
                        @{ from = $before; got = $after })
                }
            }
            'prev' {
                $before = Get-CounterParts (Get-Counter (Get-Root))
                Press-ShiftEnter
                $c = Wait-Counter 4000 250
                $after = Get-CounterParts $c
                $detail = "prev -> '$c'"
                if ($null -ne $before -and $null -ne $after -and $before[1] -gt 0) {
                    $exp = if ($before[0] -le 1) { $before[1] } else { $before[0] - 1 }
                    [void](Assert-That ($after[0] -eq $exp) 'nav-not-sequential' `
                        "prev from $($before[0])/$($before[1]) gave $($after[0]), expected $exp" `
                        @{ from = $before; got = $after })
                }
            }
            'clear' {
                [void](Set-NeedleFocus)
                Clear-Needle
                Start-Sleep -Milliseconds 500
                $c = Get-Counter (Get-Root)
                $detail = "cleared -> '$c'"
                [void](Assert-That ($c -eq '') 'counter-not-cleared' `
                    "empty needle left the counter showing '$c'" @{ counter = $c })
            }
            'close' {
                $useEsc = $rng.Next(2) -eq 0
                if ($useEsc) { Press-Escape } else {
                    $btn = Find-ByName (Get-Root) 'Close search'
                    if ($null -ne $btn) {
                        try { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { Press-Escape }
                        Start-Sleep -Milliseconds 350
                    } else { Press-Escape }
                }
                $detail = if ($useEsc) { 'esc' } else { 'button' }
                [void](Assert-That (-not (Test-SearchBarOpen (Get-Root))) 'esc-did-not-close' `
                    "close via $detail left the bar open" @{ via = $detail })

                # A torn-down search must leave no counter at all. Anything
                # else means the teardown sentinel is being rendered as a
                # number again ("0 of -1").
                $after = Get-Counter (Get-Root)
                [void](Assert-That ($after -eq '') 'counter-survived-close' `
                    "closing via $detail left the counter showing '$after'" `
                    @{ via = $detail; counter = $after })
            }
            'scroll' {
                $before = Get-CounterParts (Get-Counter (Get-Root))
                $rc = [SFz]::RectOf($script:Hwnd64)
                $notches = $rng.Next(-6, 7)
                if ($null -eq $rc -or -not [SFz]::Wheel($pid32, [int]($rc.L + $rc.W / 2), [int]($rc.T + $rc.Hh / 2), $notches)) {
                    Add-Finding 'harness' 'wheel did not reach the window' @{}
                    break
                }
                Start-Sleep -Milliseconds 500
                $after = Get-CounterParts (Get-Counter (Get-Root))
                $detail = "wheel $notches"
                if ($null -ne $before -and $null -ne $after) {
                    [void](Assert-That ($after[1] -eq $before[1]) 'total-changed-on-scroll' `
                        "scrolling changed the total from $($before[1]) to $($after[1])" `
                        @{ before = $before[1]; after = $after[1] })
                }
            }
            'resize' {
                $before = Get-CounterParts (Get-Counter (Get-Root))
                $w = $rng.Next(700, 1400); $h = $rng.Next(500, 900)
                if (-not [SFz]::Resize($script:Hwnd64, $w, $h)) {
                    Add-Finding 'harness' "resize to ${w}x${h} failed" @{}
                    break
                }
                Start-Sleep -Milliseconds 900
                $after = Get-CounterParts (Get-Counter (Get-Root))
                $detail = "resize ${w}x${h}"
                # Reflow can legitimately change how content wraps, so the doc
                # is re-read and the oracle recomputed rather than assumed.
                $doc = Get-TerminalText (Get-Term)
                if ($null -ne $before -and $null -ne $after -and $before[1] -ne $after[1]) {
                    $result.ops += [ordered]@{ i = $script:Iter; op = 'resize-total-drift'; before = $before[1]; after = $after[1] }
                }
            }
            'emit' {
                # Append matching content while a search is live; the total
                # must pick it up without reopening the bar.
                $before = Get-CounterParts (Get-Counter (Get-Root))
                $needleBox = Get-NeedleText (Get-Root)
                Press-Escape
                Send-Text ('$a=' + "'ZQ'+'XW'; 1..12 | % { `"`$a extra `$_`" }") 30
                Send-Chord @() ([SFz]::VK_RETURN) 400
                Start-Sleep -Seconds 3
                $doc = Get-TerminalText (Get-Term)
                Open-SearchBar
                Clear-Needle
                Send-Text 'ZQXW'
                Start-Sleep -Milliseconds 400
                $c = Wait-Counter
                $detail = "emit -> '$c'"
                Assert-CounterMatchesOracle 'ZQXW' $c
            }
        }

        $result.ops += [ordered]@{ i = $script:Iter; op = $op; detail = $detail }
        Write-Host ("  {0,3}  {1,-7} {2}" -f $script:Iter, $op, $detail)
    }

    Assert-Alive 'end of run'
}
catch {
    Add-Finding 'harness' $_.Exception.Message @{}
    Write-Host "HARNESS ERROR: $($_.Exception.Message)" -ForegroundColor Yellow
}
finally {

    # crash.log is append-only across every run on this machine, so only the
    # bytes this run added are evidence about this run. Slice as bytes: the
    # baseline is a file length, and using it as a character index reads the
    # wrong text and throws outright once the log contains any non-ASCII.
    try {
        $crashLog = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
        if (Test-Path $crashLog) {
            $now = (Get-Item $crashLog).Length
            if ($now -gt $script:CrashBaseline) {
                $bytes = [System.IO.File]::ReadAllBytes($crashLog)
                $fresh = [System.Text.Encoding]::UTF8.GetString(
                    $bytes, $script:CrashBaseline, $bytes.Length - $script:CrashBaseline)
                $fresh | Set-Content (Join-Path $OutDir "crash-$Seed.log") -Encoding utf8
                Add-Finding 'crash' "crash.log grew by $($now - $script:CrashBaseline) bytes during this run" @{}
            }
        }
    } catch {
        Add-Finding 'harness' "could not read crash.log: $($_.Exception.Message)" @{}
    }

    $result.checks = $script:Checks
    $result.findings = @($script:Findings)
    $result.failed = $script:Findings.Count
    $result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir "run-$Seed.json") -Encoding utf8

    # Tear down only what this run started. The launched process is not
    # always the one left holding the window -- the launch splash can elect a
    # different instance -- so sweep for processes that appeared during the
    # run and came from the exe under test, and leave everything else alone.
    if (-not $KeepOpen) {
        if ($script:Proc) {
            try { $script:Proc.Refresh(); if (-not $script:Proc.HasExited) { $script:Proc.Kill() } } catch { }
            try { [void]$script:Proc.WaitForExit(3000) } catch { }
        }
        foreach ($p in @(Get-Process Wintty -ErrorAction SilentlyContinue)) {
            if ($script:PreExisting -contains $p.Id) { continue }
            $path = try { $p.Path } catch { $null }
            if ($path -ne $script:ExeFull) { continue }
            try { $p.Kill(); [void]$p.WaitForExit(3000) } catch { }
        }
    }
    if ($IsolatedConfig) {
        if ($null -ne $origXdg) { $env:XDG_CONFIG_HOME = $origXdg }
        else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
    }
    Start-Sleep -Milliseconds 500
    Remove-Item -Recurse -Force $tempXdg -ErrorAction SilentlyContinue

    Write-Host ""
    if ($script:Findings.Count -eq 0) {
        Write-Host "PASS  $($script:Checks) checks, 0 findings (seed $Seed)" -ForegroundColor Green
    } else {
        Write-Host "FAIL  $($script:Findings.Count) findings across $($script:Checks) checks (seed $Seed)" -ForegroundColor Red
        $script:Findings | Group-Object kind | Sort-Object Count -Descending | ForEach-Object {
            Write-Host ("  {0,3}x {1}" -f $_.Count, $_.Name)
        }
    }

    # Distinct exit codes so a caller can tell the two failures apart, using
    # the same numbering as verified-input-probe.ps1, mouse-fuzz-loop.ps1 and
    # mouse-fuzz-probe.ps1: 2 means the product is broken, 1 means the run
    # never got far enough to judge it and should be retried rather than
    # filed.
    $product = @($script:Findings | Where-Object { $_.kind -ne 'harness' })
    if ($script:Findings.Count -eq 0) { $script:ExitCode = 0 }
    elseif ($product.Count -gt 0) { $script:ExitCode = 2 }
    else { $script:ExitCode = 1 }
}

exit $script:ExitCode
