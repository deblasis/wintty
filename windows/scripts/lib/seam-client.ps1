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
# Dot-source after lib/wintty-process.ps1 (Assert-NoWintty and the stamp
# helpers live there and are the caller's own preamble).

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
    [switch]$AllowInput
) {
    $tempXdg = Join-Path $env:TEMP "wintty-seam-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path (Join-Path $tempXdg 'wintty') | Out-Null
    $ConfigText | Set-Content (Join-Path $tempXdg 'wintty\config.wintty') -Encoding utf8

    $session = @{
        TempXdg   = $tempXdg
        ExePath   = (Resolve-Path $ExePath).Path
        Stamp     = Get-WinttyLaunchStamp
        Token     = New-SeamToken
        OrigXdg   = if (Test-Path Env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { $null }
        OrigSeam  = if (Test-Path Env:WINTTY_TEST_SEAM) { $env:WINTTY_TEST_SEAM } else { $null }
        OrigInput = if (Test-Path Env:WINTTY_TEST_SEAM_INPUT) { $env:WINTTY_TEST_SEAM_INPUT } else { $null }
        OrigTrace = if (Test-Path Env:WINTTY_TABDRAG_TRACE) { $env:WINTTY_TABDRAG_TRACE } else { $null }
        OrigNoColor = if (Test-Path Env:NO_COLOR) { $env:NO_COLOR } else { $null }
    }
    $env:XDG_CONFIG_HOME = $tempXdg
    # The token travels in the environment block the child inherits, which is
    # readable only by something that could already open this process anyway.
    $env:WINTTY_TEST_SEAM = $session.Token
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

    $startArgs = @{
        FilePath         = $session.ExePath
        PassThru         = $true
        WorkingDirectory = (Split-Path -Parent $session.ExePath)
    }
    if ($Arguments.Count -gt 0) { $startArgs.ArgumentList = $Arguments }
    $proc = Start-Process @startArgs
    $session.Proc = $proc
    # Everything from here can throw -- Wait-SeamReady on a window that never
    # appears or a splash that never drops, Wait-SeamPipe on a Release build
    # with the seam compiled out, Connect-SeamPipe on a timeout -- and the
    # caller does not hold the session yet, so ITS finally cannot clean up.
    # Left alone that strands a running Wintty, and Assert-NoWintty refuses to
    # kill instances it did not start, so one orphan blocks every seam harness
    # on the machine until a human intervenes. Tear down here and rethrow.
    try {
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
        Stop-SeamSession $session
        throw
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
    Write-Host ("OK {0}" -f $Command['op'])
    return $response
}

function Stop-SeamSession([Parameter(Mandatory)]$Session) {
    if ($Session.Writer) { try { $Session.Writer.Dispose() } catch { } }
    if ($Session.Reader) { try { $Session.Reader.Dispose() } catch { } }
    if ($Session.Pipe)   { try { $Session.Pipe.Dispose() } catch { } }
    try {
        Stop-WinttyStartedAfter -Since $Session.Stamp -ExePath $Session.ExePath
    } catch {
        Write-Host ("HARNESS: cleanup could not confirm every process it started: {0}" -f $_.Exception.Message)
    }
    if ($null -ne $Session.OrigXdg) { $env:XDG_CONFIG_HOME = $Session.OrigXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigSeam) { $env:WINTTY_TEST_SEAM = $Session.OrigSeam }
    else { Remove-Item Env:WINTTY_TEST_SEAM -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigInput) { $env:WINTTY_TEST_SEAM_INPUT = $Session.OrigInput }
    else { Remove-Item Env:WINTTY_TEST_SEAM_INPUT -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigTrace) { $env:WINTTY_TABDRAG_TRACE = $Session.OrigTrace }
    else { Remove-Item Env:WINTTY_TABDRAG_TRACE -ErrorAction SilentlyContinue }
    if ($null -ne $Session.OrigNoColor) { $env:NO_COLOR = $Session.OrigNoColor }
    else { Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue }
    Remove-Item $Session.TempXdg -Recurse -Force -ErrorAction SilentlyContinue
}
