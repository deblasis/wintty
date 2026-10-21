# The seam driver's shared plumbing: launch one Wintty with the in-process
# test seam armed, wait out the splash, connect the named pipe, and speak the
# newline-delimited JSON protocol. No OS input is ever synthesized here -- the
# seam drives the real handlers in-process, so the machine stays usable while
# a harness runs. UIA and pixels remain the harnesses' own read-only oracles.
#
# Arming the seam takes a per-session token, not WINTTY_TEST_SEAM=1. The token
# is the credential and the pipe is named after it, which is what stops a
# process that did not launch this app from either finding the pipe or taking
# its name first. Every consumer goes through New-SeamToken / Wait-SeamPipe /
# Connect-SeamPipe below rather than spelling a pipe name, so there is one
# place that knows how the name is built.
#
# The seam is also compiled out of Release (see windows/Directory.Build.props),
# so a harness pointed at a public build finds no pipe at all. Point them at a
# Debug build, or a Release built with -p:TestSeam=true.
#
# Dot-source after lib/wintty-process.ps1 (Assert-NoWinttyFrom and the
# stamp helpers live there and are the caller's own preamble;
# Start-SeamSession also calls its coexistence guard right before every
# launch).

# 128 bits of hex: the shape TestSeam.IsSessionToken accepts, and the reason
# the pipe name is unguessable. RandomNumberGenerator rather than Get-Random,
# whose default seeding is not something to hang an access decision on.
function New-SeamToken {
    $bytes = [byte[]]::new(16)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return [System.Convert]::ToHexString($bytes).ToLowerInvariant()
}

# The one place the pipe name is built. TestSeam.cs builds the same string from
# PipeNamePrefix; if these two ever disagree the harness fails at the wait
# below with a clear message rather than connecting to something else.
function Get-SeamPipeName([Parameter(Mandatory)][string]$Token) {
    return "wintty-test-seam-$Token"
}

# The session daemon's pipe, minted per launch for builds that carry one.
# In test mode (WINTTY_TEST_CONFIG, armed below) the app refuses to resolve
# the real per-user daemon's name and the daemon refuses to start with its
# state under the real per-edition base, so a seam launch that wants session
# legs has to name a private pipe and give the daemon somewhere private to
# put its files. The spelling is the private test shape the daemon's own
# test suite uses (`winttyd-test-<hex>`); a name carrying a user SID is a
# per-user name, which is exactly what the coexistence guard's pipe rule
# exists to refuse. New-SeamToken supplies the 128 bits, so the name is as
# unguessable as the seam's own.
function New-SeamSessionPipeName {
    return "\\.\pipe\winttyd-test-$(New-SeamToken)"
}

# Wait for the armed app to publish its pipe. Enumerating \\.\pipe\ rather than
# just attempting the connect keeps the failure legible: "never appeared" and
# "appeared but refused us" are different findings.
function Wait-SeamPipe(
    [Parameter(Mandatory)][string]$Token,
    [Parameter(Mandatory)]$Proc,
    [int]$TimeoutSeconds = 90
) {
    $name = Get-SeamPipeName $Token
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        if ($Proc.HasExited) {
            throw ("HARNESS: the app exited (code {0}) before the seam pipe appeared" -f $Proc.ExitCode)
        }
        if ([datetime]::UtcNow -gt $deadline) {
            throw ("HARNESS: the seam pipe '{0}' never appeared. Either the app was " +
                   "not launched with WINTTY_TEST_SEAM set to this session's token, " +
                   'or it is a build with the seam compiled out (Release without ' +
                   '-p:TestSeam=true).') -f $name
        }
        if ([System.IO.Directory]::GetFiles('\\.\pipe\') -contains "\\.\pipe\$name") { return $name }
        Start-Sleep -Milliseconds 150
    }
}

# Connect with CurrentUserOnly, which makes the client refuse a server running
# as anyone else. It is not the whole answer to squatting -- a squatter running
# as this same user still satisfies it, which is what the unguessable token is
# for -- but it is free and it closes the cross-account half.
function Connect-SeamPipe(
    [Parameter(Mandatory)][string]$Token,
    [int]$TimeoutMs = 20000
) {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', (Get-SeamPipeName $Token),
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::CurrentUserOnly -bor
        [System.IO.Pipes.PipeOptions]::Asynchronous)
    $pipe.Connect($TimeoutMs)
    return $pipe
}

# The type guard, not just -ErrorAction, because a second dot-source of this
# file (the tests' mutation rows dot-source copies) finds SeamWin compiled
# and Add-Type's recompile failure is terminating under Stop.
if (-not ('SeamWin' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

// Window plumbing: enumeration, rects, one MoveWindow for a capture
// capture harness that needs known geometry. Deliberately no SendInput,
// no mouse_event, no focus theft -- the seam is the actuator now.
public static class SeamWin {
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int h2, bool repaint);
    // Topmost without activation: pixel oracles read the composited
    // screen, so a capture harness needs z-order above whatever the
    // desktop parks over it -- without stealing focus to get it.
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public static void PlaceOnTop(long hwnd) {
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
        SetWindowPos(P(hwnd), new IntPtr(-1), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }
    // Window-targeted only: close-a-window and the frame-keybind posts.
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    public class WinRect { public int L,T,R,B; public int W { get { return R-L; } } public int Hh { get { return B-T; } } }

    public static IntPtr P(long hwnd) { return new IntPtr(hwnd); }
    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static string TitleOf(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }

    public static WinRect RectOf(long hwnd) {
        var h = P(hwnd); RECT r;
        if (!IsWindow(h) || !GetWindowRect(h, out r)) return null;
        var wr = new WinRect { L=r.L,T=r.T,R=r.R,B=r.B };
        return (wr.W < 80 || wr.Hh < 80) ? null : wr;
    }
}
'@ -ErrorAction SilentlyContinue
}

# The app under test runs on a private desktop: a desktop of its own in the
# interactive window station, so nothing it shows or activates can appear on,
# or take the foreground of, the desktop a person is using. The app keeps its
# normal activating startup there. Window handles on another desktop answer
# user32 reads only while that desktop is open, and GetWindowRect only from a
# thread attached to it, so the window reads below run on a fresh thread per
# desktop. Every launched process joins one job object per harness process,
# created KILL_ON_JOB_CLOSE, so the app, its session daemon and its shells
# die with the harness however it exits. A capture of a private desktop reads
# black (DWM composes only the input desktop), and synthesized input reaches
# only the input desktop, so Start-SeamSession launches there only when the
# harness passes -PrivateDesktop. The desktop name prefix and the AppDomain
# flags set in Start-SeamSession below are the contract the harness repo's
# own launcher shares, so both launch shapes stay interchangeable.
if (-not ('SeamDesk' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class SeamDesk {
    public const string DesktopPrefix = "wintty-seam-";
    const uint CREATE_SUSPENDED = 0x4, CREATE_NO_WINDOW = 0x08000000, CREATE_UNICODE_ENVIRONMENT = 0x400;
    const uint GENERIC_ALL = 0x10000000, DESKTOP_READOBJECTS = 0x1, DESKTOP_ENUMERATE = 0x40;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)] struct POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessInformation(IntPtr proc, int cls, ref POWER_THROTTLING_STATE info, int size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOW {
        public int cb; public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags; public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct BASIC_LIMIT {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)] struct EXTENDED_LIMIT {
        public BASIC_LIMIT Basic; public IO_COUNTERS Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFOW si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObjectW(IntPtr sa, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int cls, ref EXTENDED_LIMIT info, int len);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateDesktopW(string name, IntPtr dev, IntPtr mode, uint flags, uint access, IntPtr sa);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenDesktopW(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetProcessWindowStation();
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)] delegate bool DeskProc(string name, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDesktopsW(IntPtr winsta, DeskProc cb, IntPtr l);
    delegate bool WndProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumDesktopWindows(IntPtr d, WndProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

    static IntPtr _job;
    static int _seq;
    static readonly Dictionary<int, IntPtr> Desktops = new Dictionary<int, IntPtr>();

    public class WinInfo {
        public long Hwnd64; public string Desktop, Class, Title; public bool Visible; public int L, T, R, B;
        public int W { get { return R - L; } }
        public int Hh { get { return B - T; } }
    }

    public static string Quote(string arg) {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
        var sb = new StringBuilder("\""); int slashes = 0;
        foreach (var c in arg) {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes); slashes = 0; sb.Append(c);
        }
        sb.Append('\\', slashes * 2); sb.Append('"'); return sb.ToString();
    }

    static IntPtr Job() {
        if (_job != IntPtr.Zero) return _job;
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObjectW");
        var info = new EXTENDED_LIMIT(); info.Basic.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, 9, ref info, Marshal.SizeOf(typeof(EXTENDED_LIMIT)))) {
            var err = Marshal.GetLastWin32Error(); CloseHandle(job); throw new Win32Exception(err, "SetInformationJobObject");
        }
        _job = job; return job;
    }

    public static Process Start(string filePath, string[] args, string cwd, bool privateDesktop) {
        var line = new StringBuilder(Quote(filePath));
        foreach (var a in args ?? new string[0]) line.Append(' ').Append(Quote(a ?? ""));
        IntPtr desk = IntPtr.Zero, deskPath = IntPtr.Zero;
        var si = new STARTUPINFOW(); si.cb = Marshal.SizeOf(typeof(STARTUPINFOW));
        if (privateDesktop) {
            var name = DesktopPrefix + Process.GetCurrentProcess().Id + "-c" + Interlocked.Increment(ref _seq);
            desk = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
            if (desk == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDesktopW " + name);
            deskPath = Marshal.StringToHGlobalUni("WinSta0\\" + name);
            si.lpDesktop = deskPath;
        }
        PROCESS_INFORMATION pi; bool ok;
        try {
            ok = CreateProcessW(filePath, line, IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero, string.IsNullOrEmpty(cwd) ? null : cwd, ref si, out pi);
        } finally { if (deskPath != IntPtr.Zero) Marshal.FreeHGlobal(deskPath); }
        if (!ok) {
            var err = Marshal.GetLastWin32Error(); if (desk != IntPtr.Zero) CloseDesktop(desk);
            throw new Win32Exception(err, "CreateProcessW failed for " + filePath);
        }
        try {
            Process proc;
            try {
                if (!AssignProcessToJobObject(Job(), pi.hProcess)) throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject");
                // Same opt-out the release launcher applies: a process whose
                // windows sit on a desktop that is not the input desktop is one
                // Windows may power-throttle, and a throttled app's seam answers
                // arrive late. EXECUTION_SPEED (1) and IGNORE_TIMER_RESOLUTION
                // (4) controlled, both off (ProcessPowerThrottling = 4).
                if (privateDesktop)
                {
                    var throttle = new POWER_THROTTLING_STATE { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
                    SetProcessInformation(pi.hProcess, 4, ref throttle, 12);
                }
                proc = Process.GetProcessById(pi.dwProcessId);
                var unused = proc.Handle;
                if (ResumeThread(pi.hThread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread");
            } catch { TerminateProcess(pi.hProcess, 1); if (desk != IntPtr.Zero) CloseDesktop(desk); throw; }
            if (desk != IntPtr.Zero) lock (Desktops) Desktops[proc.Id] = desk;
            return proc;
        } finally { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
    }

    public static void Stop(Process proc) {
        try { if (!proc.HasExited) proc.Kill(true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
        try { proc.WaitForExit(10000); } catch { }
        IntPtr h; lock (Desktops) { if (!Desktops.TryGetValue(proc.Id, out h)) return; Desktops.Remove(proc.Id); }
        CloseDesktop(h);
    }

    public static List<WinInfo> Windows(int pid) {
        var names = new List<string>();
        EnumDesktopsW(GetProcessWindowStation(), (n, p) => { names.Add(n); return true; }, IntPtr.Zero);
        var hits = new List<WinInfo>();
        foreach (var name in names) {
            var d = OpenDesktopW(name, 0, false, DESKTOP_READOBJECTS | DESKTOP_ENUMERATE);
            if (d == IntPtr.Zero) continue;
            try {
                var th = new Thread(() => {
                    bool attached = SetThreadDesktop(d);
                    EnumDesktopWindows(d, (h, l) => {
                        uint owner; GetWindowThreadProcessId(h, out owner);
                        if (owner != (uint)pid) return true;
                        var cls = new StringBuilder(256); GetClassNameW(h, cls, 256);
                        var ttl = new StringBuilder(512); GetWindowTextW(h, ttl, 512);
                        RECT r = new RECT(); if (attached) GetWindowRect(h, out r);
                        hits.Add(new WinInfo { Hwnd64 = h.ToInt64(), Desktop = name, Class = cls.ToString(), Title = ttl.ToString(),
                            Visible = IsWindowVisible(h), L = r.L, T = r.T, R = r.R, B = r.B });
                        return true;
                    }, IntPtr.Zero);
                });
                th.Start(); th.Join();
            } finally { CloseDesktop(d); }
        }
        return hits;
    }
}
'@
}

# The one launch of the app under test (Start-SeamSession calls it; the
# tests stand it in). Every process joins the harness's job object, so the
# tree dies with the harness; -PrivateDesktop puts the app on a desktop of
# its own (SeamDesk above). Returns the Process, its handle held from
# before the resume so ExitCode reads after the exit.
function Start-SeamAppProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [switch]$PrivateDesktop
    )
    return [SeamDesk]::Start($FilePath, [string[]]@($ArgumentList), (Split-Path -Parent $FilePath), [bool]$PrivateDesktop)
}

function Get-SeamWinUiWindows([uint32]$ProcId) {
    # Every desktop of the window station, so an app on a private desktop
    # (Start-SeamSession -PrivateDesktop) is found as surely as one on the
    # default desktop. Each hit carries the desktop it lives on.
    $hits = [System.Collections.Generic.List[object]]::new()
    foreach ($w in [SeamDesk]::Windows([int]$ProcId)) {
        if (-not $w.Visible -or $w.Class -ne 'WinUIDesktopWin32WindowClass') { continue }
        if ($w.W -lt 80 -or $w.Hh -lt 80) { continue }
        $hits.Add([pscustomobject]@{ Hwnd64 = $w.Hwnd64; Title = $w.Title; Area = ($w.W * $w.Hh); Desktop = $w.Desktop })
    }
    return $hits | Sort-Object Area -Descending
}

function Test-SeamSplashVisible([int]$ProcId) {
    foreach ($w in [SeamDesk]::Windows($ProcId)) {
        if ($w.Class -eq 'WinttySplash' -and $w.Visible) { return $true }
    }
    return $false
}

function Wait-SeamReady($proc) {
    $dl = (Get-Date).AddSeconds(40)
    $got = $null
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 250
        $proc.Refresh(); if ($proc.HasExited) { throw "PRODUCT_FAIL startup exit=$($proc.ExitCode)" }
        $got = @(Get-SeamWinUiWindows ([uint32]$proc.Id)) | Select-Object -First 1
        if ($got) { break }
    }
    if (-not $got) { throw 'HARVEST_MISS: no WinUI hwnd' }
    $dl = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $dl) {
        $proc.Refresh(); if ($proc.HasExited) { throw 'PRODUCT_FAIL during splash' }
        if (Test-SeamSplashVisible $proc.Id) { Start-Sleep -Milliseconds 200; continue }
        Start-Sleep -Milliseconds 900
        if (-not (Test-SeamSplashVisible $proc.Id)) { return $got }
    }
    throw 'HARVEST_MISS: splash never dropped'
}

# One app, one pipe, one scenario. The relaunch-per-scenario structure is
# deliberate: repeated churn in a single process trips a 0xC0000005, and a
# fresh process per scenario both stays clear of it and keeps scenarios from
# contaminating each other.
#
# This note used to say "around the seventh cumulative seed" and cite a
# separately-filed issue. Both halves were checked on 2026-09-02 and are
# wrong. Measured: the THIRD seed-tabs of a process, deterministically, and
# only when a group has survived two layout round trips -- five bare seeds in
# a row are harmless. No such issue exists in this repo. The crash faults in
# CThemeResource::SetLastResolvedValue, reached from a synchronous
# NavigationView.SelectedItem assignment inside TabManager.CloseTab.
function Start-SeamSession(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$ConfigText,
    [string]$TraceFile = '',
    # Extra command line for the launch, e.g. --no-config. Kept separate
    # from $ConfigText because the two are not interchangeable: a leg that
    # measures the unconfigured build has to pass the flag AND still get an
    # isolated XDG dir, so nothing the developer has on disk leaks in.
    [string[]]$Arguments = @(),
    # Arm send-text. Off by default and deliberately opt-in per harness:
    # send-text hands arbitrary bytes to a live shell, so a harness that only
    # drags tabs should not be launching an app that can be told to run
    # commands. Only a harness asserting on shell output needs this.
    [switch]$AllowInput,
    # Every session gets a state tree of its own by default: WINTTY_STATE_BASE
    # at a fresh directory inside this session's temp root, so logs,
    # crash.log, session and window state are the run's, and
    # $session.StateBase names it for the crash oracle (Test-SeamCrashLogWritten
    # below). libghostty's native state follows it: XDG_STATE_HOME and
    # XDG_CACHE_HOME point inside the same tree. A tree the caller set is
    # adopted (and left for the caller to remove) only when it is an owned
    # one, its marker matching WINTTY_STATE_BASE_TOKEN
    # (New-WinttyOwnedStateBase); any other inherited WINTTY_STATE_BASE is
    # ignored and a fresh tree minted. Either one is what the coexistence
    # guard asks for before it lets a launch run beside somebody else's
    # Wintty.
    #
    # -SharedStateBase opts out: the launch gets the real per-user tree,
    # with WINTTY_STATE_BASE, XDG_STATE_HOME and XDG_CACHE_HOME removed from
    # its environment whatever the caller had, and the guard then refuses
    # it beside any other Wintty. -PrivateStateBase was the opt-in before
    # the private tree became the default; it still binds, and mints a fresh
    # tree even over an owned one the caller set.
    [switch]$SharedStateBase,
    [switch]$PrivateStateBase,
    # The AUMID the build runs as, for the guard. Only needed where the
    # guard cannot read it: a NativeAOT publish has no Ghostty.Core.dll
    # beside the exe, so its caller reads it off the sibling Release build.
    [string]$AumId = '',
    # Files to stage under the config root before launch, keyed by a path
    # relative to it ('wintty/themes/Name' = text). Written as exact UTF-8
    # bytes with nothing appended, after the config text, so a key naming
    # 'wintty/config.wintty' replaces it byte for byte: that is how a harness
    # relaunches on the very file a previous run wrote. A path that resolves
    # outside the root is refused.
    [hashtable]$ExtraFiles = @{},
    # A hook Stop-SeamSession calls once, after the app has exited and the
    # environment is back and crash.log is read out, and before it deletes
    # the session's temp root. That is the only moment a caller can read the
    # app's own logs, so a harness that wants to assert on them hands the
    # hook in here instead of racing the teardown.
    #
    # It is called as & $hook $context, with one object carrying the launch's
    # roots (StateBase, TempRoot, DaemonLogDir, ExePath, Stamp), whether a
    # process was started and its exit code, and the crash logs already read
    # out. What a log has to say is the hook's business: the library owns
    # when this runs and nothing else. Every hook runs even if an earlier one
    # throws, whatever a hook writes to the pipeline lands in
    # $session.TeardownFindings, and the temp root is deleted either way --
    # a hook that throws fails the teardown, after the tree is gone.
    #
    # A hook passed to Stop-SeamSession runs after this one. The two refusals
    # above the session table (an extra file outside the config root, and
    # -PrivateStateBase with -SharedStateBase) launch nothing and leave no
    # session behind, so neither reaches a hook.
    [scriptblock]$BeforeTeardown = $null,
    # Run the app on a private desktop (SeamDesk above): nothing it shows or
    # activates can reach the desktop a person is using. Not the default
    # yet, because harnesses that synthesize input or sample pixels need the
    # input desktop, which DWM composes and input reaches; without the
    # switch the launch is the plain activating launch it always was.
    [switch]$PrivateDesktop
) {
    $tempXdg = Join-Path $env:TEMP "wintty-seam-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path (Join-Path $tempXdg 'wintty') | Out-Null
    # The quick terminal's hotkey is session-global and defaults to the
    # user's Ctrl+`, so every launch stages the harness chord unless its
    # config binds one (lib/wintty-process.ps1). The text staged here is
    # the text the coexistence guard reads below.
    $ConfigText = Add-WinttyHarnessConfigDefaults $ConfigText
    $ConfigText | Set-Content (Join-Path $tempXdg 'wintty\config.wintty') -Encoding utf8
    # Both sides through GetFullPath: it expands an 8.3 %TEMP%
    # (C:\Users\ALESSA~1\...) to the long form, so comparing a normalized
    # target against the raw root would refuse every file.
    $rootFull = [System.IO.Path]::GetFullPath($tempXdg)
    foreach ($relative in $ExtraFiles.Keys) {
        $target = [System.IO.Path]::GetFullPath((Join-Path $tempXdg $relative))
        if (-not $target.StartsWith($rootFull + [System.IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item $tempXdg -Recurse -Force -ErrorAction SilentlyContinue
            throw "HARNESS: extra file '$relative' resolves outside the config root"
        }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        [System.IO.File]::WriteAllText($target, [string]$ExtraFiles[$relative],
            [System.Text.UTF8Encoding]::new($false))
    }
    # An ExtraFiles entry may have replaced wintty/config.wintty byte for
    # byte, so the guard below reads the config the launch will actually
    # read: the file as it now sits, not the variable it was first written
    # from.
    $ConfigText = [System.IO.File]::ReadAllText((Join-Path $tempXdg 'wintty\config.wintty'))

    $session = @{
        TempXdg   = $tempXdg
        ExePath   = (Resolve-Path $ExePath).Path
        Stamp     = Get-WinttyLaunchStamp
        Token     = New-SeamToken
        OrigXdg   = if (Test-Path Env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { $null }
        OrigTestConfig = if (Test-Path Env:WINTTY_TEST_CONFIG) { $env:WINTTY_TEST_CONFIG } else { $null }
        OrigSeam  = if (Test-Path Env:WINTTY_TEST_SEAM) { $env:WINTTY_TEST_SEAM } else { $null }
        OrigInput = if (Test-Path Env:WINTTY_TEST_SEAM_INPUT) { $env:WINTTY_TEST_SEAM_INPUT } else { $null }
        OrigTrace = if (Test-Path Env:WINTTY_TABDRAG_TRACE) { $env:WINTTY_TABDRAG_TRACE } else { $null }
        OrigNoColor = if (Test-Path Env:NO_COLOR) { $env:NO_COLOR } else { $null }
        OrigStateBase = if (Test-Path Env:WINTTY_STATE_BASE) { $env:WINTTY_STATE_BASE } else { $null }
        OrigStateToken = if (Test-Path Env:WINTTY_STATE_BASE_TOKEN) { $env:WINTTY_STATE_BASE_TOKEN } else { $null }
        OrigXdgState = if (Test-Path Env:XDG_STATE_HOME) { $env:XDG_STATE_HOME } else { $null }
        OrigXdgCache = if (Test-Path Env:XDG_CACHE_HOME) { $env:XDG_CACHE_HOME } else { $null }
        OrigSessionPipe = if (Test-Path Env:WINTTY_SESSIOND_PIPE) { $env:WINTTY_SESSIOND_PIPE } else { $null }
        OrigSessiondDataDir = if (Test-Path Env:WINTTY_SESSIOND_DATA_DIR) { $env:WINTTY_SESSIOND_DATA_DIR } else { $null }
        OrigSessiondLogFile = if (Test-Path Env:WINTTY_SESSIOND_LOG_FILE) { $env:WINTTY_SESSIOND_LOG_FILE } else { $null }
        OrigSessiondBinDir = if (Test-Path Env:WINTTY_SESSIOND_BIN_DIR) { $env:WINTTY_SESSIOND_BIN_DIR } else { $null }
        StateBase = $null
        SessionPipe = New-SeamSessionPipeName
        # Set in the literal, before anything below can throw, so every path
        # that leaves this function after the session is recorded reaches
        # Stop-SeamSession with a list to run and a place to put what a hook
        # wrote.
        TeardownHooks = [System.Collections.Generic.List[scriptblock]]::new()
        TeardownFindings = [System.Collections.Generic.List[object]]::new()
        TeardownError = $null
    }
    if ($BeforeTeardown) { $session.TeardownHooks.Add($BeforeTeardown) }
    if ($PrivateStateBase -and $SharedStateBase) {
        Remove-Item $tempXdg -Recurse -Force -ErrorAction SilentlyContinue
        throw 'HARNESS: -PrivateStateBase and -SharedStateBase ask for opposite state trees'
    }
    # Recorded before anything can throw, so a crash during startup still
    # reaches the run's crash oracle through Stop-SeamSession.
    (Get-SeamRunSessions).Add($session)
    $owned = Test-WinttyOwnedStateBase $session.OrigStateBase $session.OrigStateToken
    if ($SharedStateBase) {
        Remove-Item Env:WINTTY_STATE_BASE, Env:XDG_STATE_HOME, Env:XDG_CACHE_HOME -ErrorAction SilentlyContinue
    }
    else {
        if ($owned -and -not $PrivateStateBase) {
            $session.StateBase = $session.OrigStateBase
        }
        else {
            if ($session.OrigStateBase -and -not $PrivateStateBase) {
                Write-Host ("Start-SeamSession: ignoring the inherited WINTTY_STATE_BASE '{0}': it is not a tree this run owns" -f
                    $session.OrigStateBase)
            }
            $session.StateBase = Join-Path $tempXdg 'state'
            New-Item -ItemType Directory -Force -Path $session.StateBase | Out-Null
            [System.IO.File]::WriteAllText((Join-Path $session.StateBase (Get-WinttyStateOwnerMarkerName)), (New-SeamToken))
        }
        $env:WINTTY_STATE_BASE = $session.StateBase
        $env:XDG_STATE_HOME = Join-Path $session.StateBase 'xdg-state'
        $env:XDG_CACHE_HOME = Join-Path $session.StateBase 'xdg-cache'
    }
    # The ownership token never reaches the app: its panes inherit its
    # environment, and a harness started from one must not prove ownership
    # of this session's tree.
    Remove-Item Env:WINTTY_STATE_BASE_TOKEN -ErrorAction SilentlyContinue
    $env:XDG_CONFIG_HOME = $tempXdg
    # The guard that makes a lost XDG root loud: armed, an app resolving a
    # non-temp config refuses to start (exit code 4, both paths on stderr)
    # instead of silently reading and writing the real config. Every seam
    # consumer arms it here, which is what the source-scan test vouches for.
    $env:WINTTY_TEST_CONFIG = '1'
    # The token travels in the environment block the child inherits, which is
    # readable only by something that could already open this process anyway.
    $env:WINTTY_TEST_SEAM = $session.Token
    # The session daemon the app auto-spawns inherits this block too, so the
    # private pipe and the daemon's own data, log and bin dirs are named here,
    # before anything launches: in test mode the app refuses to resolve a
    # daemon pipe the environment does not name (its PipeName), and the daemon
    # refuses to start armed with a state path under the real per-edition base
    # (its test guard). Both refusals leave the session legs unable to attach,
    # and both are answered by one mint: a fresh private pipe, and the
    # daemon's whole state tree inside this session's temp root, which dies
    # with the run. The log file's parent is created up front because an
    # explicit log override the daemon cannot open is a startup error, not a
    # fallback.
    $session.SessiondRoot = Join-Path $tempXdg 'sessiond'
    foreach ($leaf in 'data', 'logs', 'bin') {
        New-Item -ItemType Directory -Force -Path (Join-Path $session.SessiondRoot $leaf) | Out-Null
    }
    $env:WINTTY_SESSIOND_PIPE = $session.SessionPipe
    $env:WINTTY_SESSIOND_DATA_DIR = Join-Path $session.SessiondRoot 'data'
    $env:WINTTY_SESSIOND_LOG_FILE = Join-Path $session.SessiondRoot 'logs\sessiond.log'
    $env:WINTTY_SESSIOND_BIN_DIR = Join-Path $session.SessiondRoot 'bin'
    if ($AllowInput) { $env:WINTTY_TEST_SEAM_INPUT = '1' }
    else { Remove-Item Env:WINTTY_TEST_SEAM_INPUT -ErrorAction SilentlyContinue }
    # The child inherits this shell's environment block, and NO_COLOR in it is
    # a harness trap rather than a user setting: Claude Code's PowerShell tool
    # exports NO_COLOR=1, so every agent-launched instance inherits it. Wintty
    # answers a set NO_COLOR with an infobar that covers roughly a third of the
    # window and renders terminal content colourless -- it displaces the very
    # chrome a capture harness is aiming at, and takes keyboard focus so the
    # chords that follow are swallowed. Strip it here so no seam consumer has
    # to remember; the original is restored in Stop-SeamSession.
    Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue
    if ($TraceFile) { $env:WINTTY_TABDRAG_TRACE = $TraceFile }
    else { Remove-Item Env:WINTTY_TABDRAG_TRACE -ErrorAction SilentlyContinue }

    # The last word before anything starts: beside a Wintty somebody else is
    # running, only a launch that proves it is fully isolated from it goes
    # ahead (lib/wintty-process.ps1, the coexistence guard). With nothing
    # running this always passes. A refusal starts and stops nothing, and
    # puts the environment back as the caller had it.
    try {
        $coexist = Assert-WinttyCoexistence -ExePath $session.ExePath -ConfigText $ConfigText -AumId $AumId -Context 'Start-SeamSession'
        if (@($coexist.Running).Count -gt 0) {
            Write-Host ("Start-SeamSession: launching beside Wintty pid(s) {0}; isolation and a different edition proven" -f
                ((@($coexist.Running) | ForEach-Object { $_.Id }) -join ', '))
        }
    }
    catch {
        $refusal = $_.Exception.Message
        # The teardown can fail too, when a hook does. The refusal is what the
        # caller came for and stays the head of the message; the teardown's is
        # appended rather than thrown over it.
        $teardown = ''
        try { Stop-SeamSession $session } catch { $teardown = $_.Exception.Message }
        if ($teardown) { throw ("HARNESS: {0} (teardown also failed: {1})" -f $refusal, $teardown) }
        throw "HARNESS: $refusal"
    }

    # Everything from here can throw -- the launch itself, on a missing or
    # locked image, Wait-SeamReady on a window that never appears or a splash
    # that never drops, Wait-SeamPipe on a Release build with the seam
    # compiled out, Connect-SeamPipe on a timeout -- and the caller does not
    # hold the session yet, so ITS finally cannot clean up. Left alone that
    # strands a running Wintty, and Assert-NoWinttyFrom refuses beside it
    # without stopping it, so one orphan blocks every seam harness on this
    # build until a human intervenes; a launch that failed to start
    # stranded the temp root and ran no teardown hook at all, which is why
    # the launch itself sits inside this try. Tear down here and rethrow.
    try {
        # The flags tell the harness's run-health check which kind of
        # desktop this process's launches ran on.
        $proc = Start-SeamAppProcess -FilePath $session.ExePath -ArgumentList $Arguments -PrivateDesktop:$PrivateDesktop
        if ($PrivateDesktop) { [AppDomain]::CurrentDomain.SetData('WinttySeamDesktopLaunch', $true) }
        else { [AppDomain]::CurrentDomain.SetData('WinttyDefaultDesktopLaunch', $true) }
        $session.Proc = $proc
        $session.PrivateDesktop = [bool]$PrivateDesktop
        $main = Wait-SeamReady $proc
        $session.Hwnd64 = [int64]$main.Hwnd64

        # The seam pipe appears once OnLaunched has built the window.
        [void](Wait-SeamPipe -Token $session.Token -Proc $proc)
        $session.Pipe = Connect-SeamPipe -Token $session.Token
        $session.Reader = [System.IO.StreamReader]::new($session.Pipe)
        $session.Writer = [System.IO.StreamWriter]::new(
            $session.Pipe, [System.Text.UTF8Encoding]::new($false))
        $session.Writer.AutoFlush = $true
        $session.Writer.NewLine = "`n"
    }
    catch {
        # Same as the refusal above: what the caller came for leads, and a
        # teardown failure (a throwing hook) follows it in one message
        # rather than replacing it.
        $failure = $_
        $teardown = ''
        try { Stop-SeamSession $session } catch { $teardown = $_.Exception.Message }
        if ($teardown) {
            throw ("{0} (teardown also failed: {1})" -f $failure.Exception.Message, $teardown)
        }
        throw $failure
    }
    return $session
}

# Fire one command and leave the response in the pipe: the filmstrip's
# capture loop runs WHILE a paced drag walks, and collects the answer
# afterwards with Receive-SeamResponse.
function Send-SeamCommand([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][hashtable]$Command) {
    if ($Session.Proc.HasExited) {
        throw ("PRODUCT_EXIT: the app exited (code {0}) before '{1}'" -f
            $Session.Proc.ExitCode, $Command['op'])
    }
    $Session.Writer.WriteLine(($Command | ConvertTo-Json -Compress -Depth 6))
}

function Receive-SeamResponse([Parameter(Mandatory)]$Session, [string]$OpName = '?') {
    $line = $Session.Reader.ReadLine()
    if ($null -eq $line) {
        if ($Session.Proc.HasExited) {
            # Parens close before -f, which is the whole point: -f binds
            # tighter than +, so with the paren at the end it would format
            # only the second fragment. Correct here today purely because
            # both placeholders happen to live in that fragment; moving one
            # up would print it literally. Same bug shipped twice in
            # seam-acceptance.ps1 before anyone saw the message.
            throw (("PRODUCT_EXIT: the seam pipe closed and the app exited " +
                "(code {0}) during '{1}'") -f $Session.Proc.ExitCode, $OpName)
        }
        throw ("HARNESS: the seam closed the connection without a response to '{0}'" -f $OpName)
    }
    $response = $line | ConvertFrom-Json
    if ($null -eq $response) {
        throw ("HARNESS: the seam answered '{0}' with a non-JSON line" -f $OpName)
    }
    if (-not $response.ok) {
        throw ("PRODUCT_FAIL: {0} -> {1}" -f $OpName, $response.error)
    }
    return $response
}

function Invoke-SeamCommand([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][hashtable]$Command) {
    Send-SeamCommand $Session $Command
    $response = Receive-SeamResponse $Session $Command['op']
    # A palette the seam had to re-open after a light dismiss says so, so a
    # recovery never passes silently where a product regression could hide.
    if ($response.recovered) {
        Write-Host ("OK {0} (recovered: the palette was re-opened after a light dismiss)" -f $Command['op'])
    }
    else {
        Write-Host ("OK {0}" -f $Command['op'])
    }
    return $response
}

function Stop-SeamSession([Parameter(Mandatory)]$Session, [scriptblock]$BeforeTeardown = $null) {
    if ($BeforeTeardown) { $Session.TeardownHooks.Add($BeforeTeardown) }
    if ($Session.Writer) { try { $Session.Writer.Dispose() } catch { } }
    if ($Session.Reader) { try { $Session.Reader.Dispose() } catch { } }
    if ($Session.Pipe)   { try { $Session.Pipe.Dispose() } catch { } }
    # The process this session started, by its exact pid, first; then the
    # sweep for anything else it started (matched on start time AND this
    # exe's path, so an instance from any other exe is never touched).
    # SeamDesk.Stop kills the whole tree under it and closes its desktop.
    if ($Session.Proc) {
        try { [SeamDesk]::Stop($Session.Proc) } catch { }
    }
    try {
        Stop-WinttyStartedAfter -Since $Session.Stamp -ExePath $Session.ExePath
    } catch {
        Write-Host ("HARNESS: cleanup could not confirm every process it started: {0}" -f $_.Exception.Message)
    }
    if ($null -ne $Session.OrigXdg) { $env:XDG_CONFIG_HOME = $Session.OrigXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigTestConfig) { $env:WINTTY_TEST_CONFIG = $Session.OrigTestConfig }
    else { Remove-Item Env:WINTTY_TEST_CONFIG -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigSeam) { $env:WINTTY_TEST_SEAM = $Session.OrigSeam }
    else { Remove-Item Env:WINTTY_TEST_SEAM -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigInput) { $env:WINTTY_TEST_SEAM_INPUT = $Session.OrigInput }
    else { Remove-Item Env:WINTTY_TEST_SEAM_INPUT -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigTrace) { $env:WINTTY_TABDRAG_TRACE = $Session.OrigTrace }
    else { Remove-Item Env:WINTTY_TABDRAG_TRACE -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigNoColor) { $env:NO_COLOR = $Session.OrigNoColor }
    else { Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigStateBase) { $env:WINTTY_STATE_BASE = $Session.OrigStateBase }
    else { Remove-Item Env:WINTTY_STATE_BASE -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigStateToken) { $env:WINTTY_STATE_BASE_TOKEN = $Session.OrigStateToken }
    else { Remove-Item Env:WINTTY_STATE_BASE_TOKEN -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigXdgState) { $env:XDG_STATE_HOME = $Session.OrigXdgState }
    else { Remove-Item Env:XDG_STATE_HOME -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigXdgCache) { $env:XDG_CACHE_HOME = $Session.OrigXdgCache }
    else { Remove-Item Env:XDG_CACHE_HOME -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigSessionPipe) { $env:WINTTY_SESSIOND_PIPE = $Session.OrigSessionPipe }
    else { Remove-Item Env:WINTTY_SESSIOND_PIPE -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigSessiondDataDir) { $env:WINTTY_SESSIOND_DATA_DIR = $Session.OrigSessiondDataDir }
    else { Remove-Item Env:WINTTY_SESSIOND_DATA_DIR -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigSessiondLogFile) { $env:WINTTY_SESSIOND_LOG_FILE = $Session.OrigSessiondLogFile }
    else { Remove-Item Env:WINTTY_SESSIOND_LOG_FILE -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigSessiondBinDir) { $env:WINTTY_SESSIOND_BIN_DIR = $Session.OrigSessiondBinDir }
    else { Remove-Item Env:WINTTY_SESSIOND_BIN_DIR -ErrorAction SilentlyContinue }
    # The private state tree dies with the temp root below, so its crash.log
    # is read out first: a harness that checks for crashes after teardown
    # still has the evidence. Once only, so a second Stop on a removed tree
    # cannot overwrite what the first one read.
    if (-not $Session.ContainsKey('CrashLogs')) {
        $Session.CrashLogs = @(Get-SeamCrashLogs $Session)
        foreach ($c in $Session.CrashLogs) {
            Write-Host ("Stop-SeamSession: the app wrote {0}" -f $c.Path) -ForegroundColor Yellow
        }
    }
    # The one moment the app's own logs are still readable: the process is
    # gone, the sweep is done, the environment is back and crash.log is read
    # out, and the tree below has not been touched. So the caller's hooks run
    # here, once per session however many times Stop is called, and the tree
    # dies after them.
    if (-not $Session.TeardownHookRan) {
        $Session.TeardownHookRan = $true
        $launched = $null -ne $Session.Proc
        $exitCode = $null
        if ($launched -and $Session.Proc.HasExited) { try { $exitCode = $Session.Proc.ExitCode } catch { } }
        $context = [pscustomobject]@{
            StateBase    = $Session.StateBase
            TempRoot     = $Session.TempXdg
            DaemonLogDir = if ($Session.SessiondRoot) { Join-Path $Session.SessiondRoot 'logs' } else { $null }
            ExePath      = $Session.ExePath
            Stamp        = $Session.Stamp
            Launched     = $launched
            ExitCode     = $exitCode
            CrashLogs    = @($Session.CrashLogs)
        }
        # Every hook runs, even when an earlier one threw: the delete below is
        # what the next one would be too late for, so a hook that only reads
        # the tree must not be skipped by a hook that only writes. The first
        # failure is the one reported, and the findings every hook emitted are
        # kept on the session either way.
        foreach ($hook in @($Session.TeardownHooks)) {
            try {
                foreach ($finding in @(& $hook $context)) { $Session.TeardownFindings.Add($finding) }
            }
            catch {
                if (-not $Session.TeardownError) { $Session.TeardownError = $_.Exception.Message }
            }
        }
    }
    Remove-Item $Session.TempXdg -Recurse -Force -ErrorAction SilentlyContinue
    # A hook that failed is reported only now: the tree is gone either way,
    # and a caller that reads logs from the hook has had its chance.
    if ($Session.TeardownError) { throw ("HARNESS: the BeforeTeardown hook failed: {0}" -f $Session.TeardownError) }
}

# Every session Start-SeamSession created in this run, in order. Kept in the
# caller's script scope (this file is dot-sourced), so the crash oracle below
# can see sessions that failed to start and were never handed back.
function Get-SeamRunSessions {
    $v = Get-Variable -Name SeamRunSessions -Scope Script -ErrorAction Ignore
    if (-not $v) {
        $script:SeamRunSessions = [System.Collections.Generic.List[object]]::new()
        return , $script:SeamRunSessions
    }
    return , $v.Value
}

# A mark to hand to Test-SeamCrashLogWritten -Since: the sessions started
# after it are the ones a scenario owns.
function Get-SeamSessionMark { return (Get-SeamRunSessions).Count }

# The crash.log files the app of $Session wrote after the session started,
# as { Path, Text }. A stopped session answers with what Stop-SeamSession
# read before removing its tree. A private state tree is searched rather
# than spelled, because the directory under it is named after the edition.
# A session that shares the per-user tree (-SharedStateBase) reads the
# per-user crash.log, where only a write after its own stamp counts.
function Get-SeamCrashLogs([Parameter(Mandatory)]$Session) {
    if ($Session.ContainsKey('CrashLogs')) { return @($Session.CrashLogs) }
    $since = ([datetime]$Session.Stamp).ToUniversalTime()
    $files = if ($Session.StateBase) {
        @(Get-ChildItem -LiteralPath $Session.StateBase -Recurse -File -Filter crash.log -ErrorAction SilentlyContinue)
    }
    else {
        # The per-user tree is named after the edition, read off the build.
        # A name that cannot be read makes the oracle report rather than go
        # quiet: a crash it could not look for is not a clean run.
        $dirName = Get-WinttyBuildStateDirName $Session.ExePath
        if (-not $dirName) {
            return @([pscustomobject]@{
                Path = '(unknown)'
                Text = "HARNESS: the state dir name of $($Session.ExePath) cannot be read (no Ghostty.Core.dll beside it), so its per-user crash.log could not be checked"
            })
        }
        @(Get-Item -LiteralPath (Join-Path (Join-Path $env:LOCALAPPDATA $dirName) 'crash.log') -ErrorAction SilentlyContinue)
    }
    return @($files | Where-Object { $_.Length -gt 0 -and $_.LastWriteTimeUtc -ge $since } | ForEach-Object {
        $text = try { [System.IO.File]::ReadAllText($_.FullName) } catch { '' }
        [pscustomobject]@{ Path = $_.FullName; Text = $text }
    })
}

# Every crash.log written by the sessions started since $Since (a
# Get-SeamSessionMark; 0, the default, is the whole run).
function Get-SeamRunCrashLogs([int]$Since = 0) {
    $sessions = Get-SeamRunSessions
    $found = [System.Collections.Generic.List[object]]::new()
    for ($i = [Math]::Max(0, $Since); $i -lt $sessions.Count; $i++) {
        foreach ($c in @(Get-SeamCrashLogs $sessions[$i])) { $found.Add($c) }
    }
    return , $found
}

# Whether any session started since $Since wrote a crash.log: the oracle
# that replaced watching the shared per-user file, which every other Wintty
# on the machine writes to as well.
function Test-SeamCrashLogWritten([int]$Since = 0) {
    return (Get-SeamRunCrashLogs -Since $Since).Count -gt 0
}
