#requires -Version 7
# Mouse-first Wintty fuzz.
#
# Hard rules (the last harness clicked screen 18,18 and activated Claude):
#   - Never SendInput keyboard. Never BringWindowToTop / SetForegroundWindow.
#   - Rects only via C# GetWindowRect (PowerShell [ref] RECT lies after one call).
#   - mouse_event only after WindowFromPoint at that pixel belongs to Wintty's pid.
#   - If the pixel is Claude/Cursor/splash: refuse, do not move the cursor.
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
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint WM_CHAR = 0x0102;
    public const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr lp);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);

    public class WinRect { public int L, T, R, B; public int W { get { return R - L; } } public int Hh { get { return B - T; } } }
    public class Hit {
        public bool Ok;
        public string Why;
        public int X, Y;
        public uint HitPid;
        public string HitClass;
    }

    public static IntPtr P(long hwnd) { return new IntPtr(hwnd); }

    public static WinRect RectOf(long hwnd) {
        var h = P(hwnd);
        RECT r;
        if (!IsWindow(h) || !GetWindowRect(h, out r)) return null;
        var wr = new WinRect { L = r.L, T = r.T, R = r.R, B = r.B };
        if (wr.W < 80 || wr.Hh < 80) return null;
        return wr;
    }

    public static string ClassOf(IntPtr h) {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, 256);
        return sb.ToString();
    }

    public static uint PidOf(IntPtr h) {
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        return pid;
    }

    static Hit Miss(string why, int x, int y, uint pid, string cls) {
        return new Hit { Ok = false, Why = why, X = x, Y = y, HitPid = pid, HitClass = cls };
    }

    // Screen pixel must belong to pid. No click, no cursor move, on miss.
    public static Hit Probe(long hwnd, uint pid, int dx, int dy) {
        var root = P(hwnd);
        if (!IsWindow(root)) return Miss("dead hwnd", 0, 0, 0, "");
        var rc = RectOf(hwnd);
        if (rc == null) return Miss("bad rect", 0, 0, 0, "");
        if (dx < 4 || dy < 4 || dx > rc.W - 4 || dy > rc.Hh - 4)
            return Miss("delta outside hwnd", rc.L + dx, rc.T + dy, 0, "");
        int x = rc.L + dx, y = rc.T + dy;
        var hit = WindowFromPoint(new POINT { X = x, Y = y });
        uint hitPid = PidOf(hit);
        string cls = ClassOf(hit);
        if (cls == "WinttySplash") return Miss("splash still covering pixel", x, y, hitPid, cls);
        if (hitPid != pid) return Miss("WindowFromPoint is not Wintty", x, y, hitPid, cls);
        return new Hit { Ok = true, X = x, Y = y, HitPid = hitPid, HitClass = cls };
    }

    public static Hit Click(long hwnd, uint pid, int dx, int dy, bool right) {
        var p = Probe(hwnd, pid, dx, dy);
        if (!p.Ok) return p;
        if (!SetCursorPos(p.X, p.Y)) return Miss("SetCursorPos failed", p.X, p.Y, p.HitPid, p.HitClass);
        Thread.Sleep(40);
        // Re-check after the cursor move: another window may have popped.
        var again = Probe(hwnd, pid, dx, dy);
        if (!again.Ok) return again;
        if (right) {
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        } else {
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }
        Thread.Sleep(200);
        return again;
    }

    public static Hit WheelAt(long hwnd, uint pid, int dx, int dy, int notches) {
        var p = Probe(hwnd, pid, dx, dy);
        if (!p.Ok) return p;
        if (!SetCursorPos(p.X, p.Y)) return Miss("SetCursorPos failed", p.X, p.Y, p.HitPid, p.HitClass);
        Thread.Sleep(40);
        p = Probe(hwnd, pid, dx, dy);
        if (!p.Ok) return p;
        for (int i = 0; i < notches; i++) {
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(-120)), UIntPtr.Zero);
            Thread.Sleep(40);
        }
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
        $hits.Add([pscustomobject]@{
            Hwnd64=$hwnd64; Title=$t.ToString()
            Area=($rc.W * $rc.Hh)
        })
        return $true
    }
    [void][Mz]::EnumWindows($cb,[IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending | Select-Object -First 1
}

function Wait-Main($proc, $sec) {
    $dl = (Get-Date).AddSeconds($sec)
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 250
        $proc.Refresh(); if ($proc.HasExited) { throw "PRODUCT_FAIL startup exit=$($proc.ExitCode)" }
        $m = Get-Main $proc.Id
        if ($m) { return $m }
    }
    throw "HARVEST_MISS: no WinUI hwnd"
}

function Splash-Visible([int]$ProcId) {
    $script:splashSeen = $false
    $cb = [Mz+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner = 0
        [void][Mz]::GetWindowThreadProcessId($hwnd, [ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        $cls = New-Object System.Text.StringBuilder 256
        [void][Mz]::GetClassName($hwnd, $cls, 256)
        if ($cls.ToString() -eq 'WinttySplash' -and [Mz]::IsWindowVisible($hwnd)) {
            $script:splashSeen = $true
        }
        return $true
    }
    [void][Mz]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:splashSeen
}

function Wait-SplashDown($proc) {
    $dl = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $dl) {
        $proc.Refresh(); if ($proc.HasExited) { throw "PRODUCT_FAIL during splash exit=$($proc.ExitCode)" }
        if (Splash-Visible $proc.Id) { Start-Sleep -Milliseconds 200; continue }
        Start-Sleep -Milliseconds 900
        if (-not (Splash-Visible $proc.Id)) { return }
    }
    throw "HARVEST_MISS: splash never dropped"
}

function Shot([int64]$Hwnd64, [string]$name) {
    $rc = [Mz]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw "HARVEST_MISS: degenerate rect for $name" }
    Write-Host "shot $name $($rc.W)x$($rc.Hh) @ $($rc.L),$($rc.T)"
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $p = Join-Path $OutDir "shots\$name.png"
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose()
    return $p
}

function Invoke-WinttyClick([int64]$Hwnd64, [uint32]$ProcId, [int]$dx, [int]$dy, [string]$what, [switch]$Right) {
    $hit = [Mz]::Click($Hwnd64, $ProcId, $dx, $dy, [bool]$Right)
    $tag = if ($Right) { 'right' } else { 'left' }
    if (-not $hit.Ok) {
        throw "HARVEST_MISS: $what $tag click refused: $($hit.Why) at $($hit.X),$($hit.Y) pid=$($hit.HitPid) class=$($hit.HitClass)"
    }
    Write-Host "click $what $tag $($hit.X),$($hit.Y) hit=$($hit.HitClass)"
}

function Post-Text([int64]$Hwnd64, [string]$text) {
    $root = [Mz]::P($Hwnd64)
    $targets = [System.Collections.Generic.List[IntPtr]]::new()
    $targets.Add($root)
    $cb = [Mz+EnumProc]{ param($ch,$lp) $targets.Add($ch); return $true }
    [void][Mz]::EnumChildWindows($root, $cb, [IntPtr]::Zero)
    foreach ($ch in $text.ToCharArray()) {
        $wp = [IntPtr][uint16][char]$ch
        foreach ($t in $targets) { [void][Mz]::PostMessage($t, [Mz]::WM_CHAR, $wp, [IntPtr]::Zero) }
        Start-Sleep -Milliseconds 12
    }
}

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

Assert-NoWintty
$script:WinttyStamp = Get-WinttyLaunchStamp
Start-Sleep -Milliseconds 400
$proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path $ExePath)
$pid32 = [uint32]$proc.Id
Start-Sleep -Seconds 3
$main = Wait-Main $proc 40
Wait-SplashDown $proc
$main = Get-Main $proc.Id
if (-not $main) { throw "HARVEST_MISS: WinUI hwnd gone after splash" }
$hwnd64 = [int64]$main.Hwnd64
Write-Host "hwnd=$hwnd64 pid=$pid32 title=$($main.Title) isWindow=$([Mz]::IsWindow([Mz]::P($hwnd64)))"
Shot $hwnd64 '00-launch' | Out-Null
$proc.Refresh(); if ($proc.HasExited) { throw "PRODUCT_FAIL after launch" }

$rc = [Mz]::RectOf($hwnd64)
if ($null -eq $rc) { throw "HARVEST_MISS: lost rect after launch shot" }
$H = $rc.Hh

# Coords from the verified 00-launch shot: icon ~18,18; chevron ~20,80;
# tab ~20,140; + ~H-80; profile chevron ~H-30; grid interior ~320,220.
Invoke-WinttyClick $hwnd64 $pid32 18 18 'app-icon'
Shot $hwnd64 '00b-app-menu' | Out-Null
Invoke-WinttyClick $hwnd64 $pid32 320 220 'grid-dismiss'
Shot $hwnd64 '00c-app-menu-dismiss' | Out-Null

Invoke-WinttyClick $hwnd64 $pid32 20 ($H - 80) 'plus'
Shot $hwnd64 '01-click-plus' | Out-Null
$proc.Refresh(); if ($proc.HasExited) { throw "PRODUCT_FAIL after plus" }

Invoke-WinttyClick $hwnd64 $pid32 20 ($H - 80) 'plus-2'
Shot $hwnd64 '02-click-plus-2' | Out-Null

Invoke-WinttyClick $hwnd64 $pid32 20 80 'chevron'
Shot $hwnd64 '03-click-chevron' | Out-Null

Invoke-WinttyClick $hwnd64 $pid32 20 140 'tab'
Shot $hwnd64 '04-click-tab' | Out-Null

Invoke-WinttyClick $hwnd64 $pid32 20 ($H - 30) 'profile-chevron'
Shot $hwnd64 '05-profile-flyout' | Out-Null

Invoke-WinttyClick $hwnd64 $pid32 320 220 'grid-dismiss-2'
Shot $hwnd64 '06-dismiss' | Out-Null

Invoke-WinttyClick $hwnd64 $pid32 400 280 'grid-context' -Right
Shot $hwnd64 '07-context-grid' | Out-Null
Invoke-WinttyClick $hwnd64 $pid32 320 220 'grid-dismiss-3'
Shot $hwnd64 '08-context-dismiss' | Out-Null

Invoke-WinttyClick $hwnd64 $pid32 20 140 'tab-context' -Right
Shot $hwnd64 '09-context-tab' | Out-Null
Invoke-WinttyClick $hwnd64 $pid32 320 220 'grid-dismiss-4'

$wheel = [Mz]::WheelAt($hwnd64, $pid32, 400, 300, 6)
if (-not $wheel.Ok) { throw "HARVEST_MISS: wheel refused: $($wheel.Why) class=$($wheel.HitClass)" }
Shot $hwnd64 '10-wheel' | Out-Null

$rc = [Mz]::RectOf($hwnd64)
if ($null -eq $rc) { throw "HARVEST_MISS: lost rect before resize" }
[void][Mz]::MoveWindow([Mz]::P($hwnd64), $rc.L, $rc.T, 720, 480, $true)
Start-Sleep -Milliseconds 400
Shot $hwnd64 '11-resize-small' | Out-Null
$rc = [Mz]::RectOf($hwnd64)
[void][Mz]::MoveWindow([Mz]::P($hwnd64), $rc.L, $rc.T, 1400, 900, $true)
Start-Sleep -Milliseconds 400
Shot $hwnd64 '12-resize-large' | Out-Null

Post-Text $hwnd64 "mousefuzz"
Start-Sleep -Milliseconds 400
Shot $hwnd64 '13-typed' | Out-Null

Post-Text $hwnd64 " 日本語"
Start-Sleep -Milliseconds 400
Shot $hwnd64 '14-cjk' | Out-Null

$proc.Refresh()
$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
$alive = -not $proc.HasExited
@{
    alive = $alive
    exitCode = if ($proc.HasExited) { $proc.ExitCode } else { $null }
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
