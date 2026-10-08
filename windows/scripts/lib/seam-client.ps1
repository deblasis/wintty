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

function Get-SeamWinUiWindows([uint32]$ProcId) {
    $hits = [System.Collections.Generic.List[object]]::new()
    $cb = [SeamWin+EnumProc]{
        param($h, $lp)
        [uint32]$o = 0; [void][SeamWin]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -ne $ProcId -or -not [SeamWin]::IsWindowVisible($h)) { return $true }
        if ([SeamWin]::ClassOf($h) -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [SeamWin]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64 = $hwnd64; Title = [SeamWin]::TitleOf($h); Area = ($rc.W * $rc.Hh) })
        return $true
    }
    [void][SeamWin]::EnumWindows($cb, [IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending
}

function Test-SeamSplashVisible([int]$ProcId) {
    $script:seamSplashSeen = $false
    $cb = [SeamWin+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner = 0; [void][SeamWin]::GetWindowThreadProcessId($hwnd, [ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        if ([SeamWin]::ClassOf($hwnd) -eq 'WinttySplash' -and [SeamWin]::IsWindowVisible($hwnd)) { $script:seamSplashSeen = $true }
        return $true
    }
    [void][SeamWin]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:seamSplashSeen
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
    [scriptblock]$BeforeTeardown = $null
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

    $startArgs = @{
        FilePath         = $session.ExePath
        PassThru         = $true
        WorkingDirectory = (Split-Path -Parent $session.ExePath)
    }
    if ($Arguments.Count -gt 0) { $startArgs.ArgumentList = $Arguments }
    # Everything from here can throw -- Start-Process itself, on a missing or
    # locked image, Wait-SeamReady on a window that never appears or a splash
    # that never drops, Wait-SeamPipe on a Release build with the seam
    # compiled out, Connect-SeamPipe on a timeout -- and the caller does not
    # hold the session yet, so ITS finally cannot clean up. Left alone that
    # strands a running Wintty, and Assert-NoWinttyFrom refuses beside it
    # without stopping it, so one orphan blocks every seam harness on this
    # build until a human intervenes; a launch that failed at Start-Process
    # stranded the temp root and ran no teardown hook at all, which is why
    # the launch itself sits inside this try. Tear down here and rethrow.
    try {
        $proc = Start-Process @startArgs
        $session.Proc = $proc
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
    if ($Session.Proc -and -not $Session.Proc.HasExited) {
        try { $Session.Proc.Kill($true); [void]$Session.Proc.WaitForExit(3000) } catch { }
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
