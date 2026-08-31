# The seam driver's shared plumbing: launch one Wintty with the in-process
# test seam armed (WINTTY_TEST_SEAM=1), wait out the splash, connect the
# named pipe, and speak the newline-delimited JSON protocol. No OS input is
# ever synthesized here -- the seam drives the real handlers in-process, so
# the machine stays usable while a harness runs. UIA and pixels remain the
# harnesses' own read-only oracles.
#
# Dot-source after lib/wintty-process.ps1 (Assert-NoWintty and the stamp
# helpers live there and are the caller's own preamble).

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

// Read-only window plumbing: enumeration, rects and one MoveWindow for a
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
# deliberate: repeated seed-tabs churn in a single process trips a known,
# separately-filed 0xC0000005 in coreclr around the seventh cumulative
# seed, and a fresh process per scenario both stays clear of that threshold
# and keeps scenarios from contaminating each other.
function Start-SeamSession(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$ConfigText,
    [string]$TraceFile = ''
) {
    $tempXdg = Join-Path $env:TEMP "wintty-seam-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path (Join-Path $tempXdg 'wintty') | Out-Null
    $ConfigText | Set-Content (Join-Path $tempXdg 'wintty\config.wintty') -Encoding utf8

    $session = @{
        TempXdg   = $tempXdg
        ExePath   = (Resolve-Path $ExePath).Path
        Stamp     = Get-WinttyLaunchStamp
        OrigXdg   = if (Test-Path Env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { $null }
        OrigSeam  = if (Test-Path Env:WINTTY_TEST_SEAM) { $env:WINTTY_TEST_SEAM } else { $null }
        OrigTrace = if (Test-Path Env:WINTTY_TABDRAG_TRACE) { $env:WINTTY_TABDRAG_TRACE } else { $null }
    }
    $env:XDG_CONFIG_HOME = $tempXdg
    $env:WINTTY_TEST_SEAM = '1'
    if ($TraceFile) { $env:WINTTY_TABDRAG_TRACE = $TraceFile }
    else { Remove-Item Env:WINTTY_TABDRAG_TRACE -ErrorAction SilentlyContinue }

    $proc = Start-Process -FilePath $session.ExePath -PassThru `
        -WorkingDirectory (Split-Path -Parent $session.ExePath)
    $session.Proc = $proc
    $main = Wait-SeamReady $proc
    $session.Hwnd64 = [int64]$main.Hwnd64

    # The seam pipe appears once OnLaunched has built the window.
    $deadline = [datetime]::UtcNow.AddSeconds(90)
    while ($true) {
        if ($proc.HasExited) {
            throw ("HARNESS: the app exited (code {0}) before the seam pipe appeared" -f $proc.ExitCode)
        }
        if ([datetime]::UtcNow -gt $deadline) {
            throw 'HARNESS: the seam pipe never appeared (WINTTY_TEST_SEAM=1 not seen by the app?)'
        }
        if ([System.IO.Directory]::GetFiles('\\.\pipe\') -contains '\\.\pipe\wintty-test-seam') { break }
        Start-Sleep -Milliseconds 150
    }
    $session.Pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', 'wintty-test-seam', [System.IO.Pipes.PipeDirection]::InOut)
    $session.Pipe.Connect(20000)
    $session.Reader = [System.IO.StreamReader]::new($session.Pipe)
    $session.Writer = [System.IO.StreamWriter]::new(
        $session.Pipe, [System.Text.UTF8Encoding]::new($false))
    $session.Writer.AutoFlush = $true
    $session.Writer.NewLine = "`n"
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
            throw ("PRODUCT_EXIT: the seam pipe closed and the app exited " +
                "(code {0}) during '{1}'" -f $Session.Proc.ExitCode, $OpName)
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
    if ($null -ne $Session.OrigTrace) { $env:WINTTY_TABDRAG_TRACE = $Session.OrigTrace }
    else { Remove-Item Env:WINTTY_TABDRAG_TRACE -ErrorAction SilentlyContinue }
    Remove-Item $Session.TempXdg -Recurse -Force -ErrorAction SilentlyContinue
}
