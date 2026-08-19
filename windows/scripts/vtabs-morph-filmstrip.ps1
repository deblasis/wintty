#requires -Version 7
# Dense frame capture across a horizontal<->vertical tab layout switch.
#
# The existing vtabs-layout-switch-capture takes one frame per direction,
# which is enough to prove the end states but says nothing about the motion
# in between. This grabs a burst at roughly one frame per compositor tick so
# the travel direction and the active-tab morph can be judged frame by frame.
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\Ghostty\bin\x64\Debug\net10.0-windows10.0.19041.0\Wintty.exe'),
    [string]$OutDir = (Join-Path $PSScriptRoot ("vtabs-morph/run-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [int]$Frames = 26,
    [int]$IntervalMs = 22,
    # Only the strip corner is interesting, and copying the whole window cost
    # more per frame than the interval itself, stretching a 340ms animation
    # across a third of a second of sampling.
    [int]$RegionW = 720,
    [int]$RegionH = 420,
    [switch]$ColorActive,
    [string]$ColorName = 'Red',
    [switch]$Vertical,
    # The pinned rail is where the column tween has real distance to cover,
    # so it is where the strip lagging behind the ghost shows up.
    [switch]$Pinned
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class VtMorph {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    const uint KEYUP = 0x0002;
    const byte VK_CONTROL = 0x11;
    const byte VK_SHIFT = 0x10;
    const byte VK_OEM_COMMA = 0xBC;
    const byte VK_T = 0x54;
    public static IntPtr FindWin(uint pid) {
        IntPtr best = IntPtr.Zero; int bestArea = 0;
        EnumProc cb = (h, lp) => {
            uint p = 0; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256); GetClassName(h, sb, 256);
            if (sb.ToString() != "WinUIDesktopWin32WindowClass") return true;
            RECT r; if (!GetWindowRect(h, out r)) return true;
            int area = Math.Max(0, r.R - r.L) * Math.Max(0, r.B - r.T);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        };
        EnumWindows(cb, IntPtr.Zero);
        return best;
    }
    public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
    // Synthesized keys go to the foreground owner, not to a handle, and
    // SetForegroundWindow fails silently under the foreground lock. Confirm
    // ownership before sending anything or the chord lands in the editor.
    static bool Focus(IntPtr expected) {
        if (expected == IntPtr.Zero || !IsWindow(expected)) return false;
        for (int i = 0; i < 20; i++) {
            if (GetForegroundWindow() == expected) return true;
            SetForegroundWindow(expected);
            Thread.Sleep(50);
        }
        return GetForegroundWindow() == expected;
    }
    static void Chord(byte key, bool shift) {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        if (shift) keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KEYUP, UIntPtr.Zero);
        if (shift) keybd_event(VK_SHIFT, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
    }
    public static bool ToggleLayout(IntPtr h) { if (!Focus(h)) return false; Chord(VK_OEM_COMMA, true); return true; }
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    public static bool Click(uint pid, int x, int y, bool right) {
        SetCursorPos(x, y); Thread.Sleep(60);
        // Probe the live cursor, not the scripted point: the buttons land
        // wherever the cursor is now, and a user mouse move during the
        // settle would divorce the pid check from the click.
        POINT live; if (!GetCursorPos(out live)) return false;
        if (Math.Abs(live.X - x) > 2 || Math.Abs(live.Y - y) > 2) return false;
        uint owner = 0; GetWindowThreadProcessId(WindowFromPoint(live), out owner);
        if (owner != pid) return false;
        uint down = right ? 0x0008u : 0x0002u, up = right ? 0x0010u : 0x0004u;
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        return true;
    }
    public static bool NewTab(IntPtr h) { if (!Focus(h)) return false; Chord(VK_T, false); return true; }
}
'@

# Capture into memory first and save afterwards: encoding PNG inside the
# loop costs more than the frame interval and would smear the timeline.
function Start-Burst([IntPtr]$hwnd, [string]$tag) {
    $r = [VtMorph]::Rect($hwnd)
    $w = [Math]::Min($RegionW, $r.R - $r.L)
    $h = [Math]::Min($RegionH, $r.B - $r.T)
    if ($w -lt 80 -or $h -lt 80) { throw "bad rect for $tag" }
    $shots = New-Object System.Collections.Generic.List[object]
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt $Frames; $i++) {
        $bmp = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
        $g.Dispose()
        $shots.Add([pscustomobject]@{ Bmp = $bmp; At = $sw.ElapsedMilliseconds })
        $due = ($i + 1) * $IntervalMs
        $wait = $due - $sw.ElapsedMilliseconds
        if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
    }
    $sw.Stop()
    for ($i = 0; $i -lt $shots.Count; $i++) {
        $n = '{0}-{1:d2}-t{2:d3}ms' -f $tag, $i, $shots[$i].At
        $shots[$i].Bmp.Save((Join-Path $OutDir "$n.png"))
        $shots[$i].Bmp.Dispose()
    }
    Write-Host "$tag : $($shots.Count) frames, last t=$($shots[$shots.Count-1].At)ms"
}

$tempXdg = Join-Path $env:TEMP "wintty-morph-$([guid]::NewGuid())"
$cfgDir = Join-Path $tempXdg 'wintty'
New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null
@"
vertical-tabs = $($Vertical.IsPresent.ToString().ToLower())
vertical-tabs-pinned = $($Pinned.IsPresent.ToString().ToLower())
windows-single-instance = false
window-theme = wintty
theme = Catppuccin Mocha
"@ | Set-Content -Path (Join-Path $cfgDir 'config.wintty') -Encoding utf8

$origXdg = $env:XDG_CONFIG_HOME
$env:XDG_CONFIG_HOME = $tempXdg
$proc = $null
try {
    $proc = Start-Process -FilePath $ExePath -PassThru
    $dl = (Get-Date).AddSeconds(45)
    $hwnd = [IntPtr]::Zero
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 300
        $proc.Refresh()
        if ($proc.HasExited) { throw "exit $($proc.ExitCode)" }
        $hwnd = [VtMorph]::FindWin([uint32]$proc.Id)
        if ($hwnd -ne [IntPtr]::Zero) { break }
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw 'no hwnd' }
    Start-Sleep -Seconds 4

    [void][VtMorph]::MoveWindow($hwnd, 60, 60, 1280, 820, $true)
    Start-Sleep -Milliseconds 600

    # Three tabs so the strip has something to animate and the active one is
    # not the only row on screen.
    foreach ($i in 1..2) {
        if (-not [VtMorph]::NewTab($hwnd)) { throw 'FOREGROUND_MISS: new tab' }
        Start-Sleep -Milliseconds 900
    }
    Start-Sleep -Milliseconds 800

    if ($ColorActive) {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $ct = if ($Vertical) { 'ListItem' } else { 'TabItem' }
        $hits = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::$ct)))
        if ($hits.Count -gt 0) {
            $r = $hits[$hits.Count - 1].Current.BoundingRectangle
            [void][VtMorph]::Click([uint32]$proc.Id,
                [int]($r.X + $r.Width * 0.4), [int]($r.Y + $r.Height / 2), $true)
            Start-Sleep -Milliseconds 500
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
            foreach ($name in @('Tab Color...', $ColorName)) {
                $el = $root.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
                if ($null -eq $el) { Write-Host "color step '$name' not found"; break }
                $b = $el.Current.BoundingRectangle
                [void][VtMorph]::Click([uint32]$proc.Id,
                    [int]($b.X + $b.Width / 2), [int]($b.Y + $b.Height / 2), $false)
                Start-Sleep -Milliseconds 450
                $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
            }
            Write-Host "active tab coloured $ColorName"
        }
        # UIA leaves focus on whatever it clicked; the layout chord only
        # reaches the router from the terminal surface.
        $wr = [VtMorph]::Rect($hwnd)
        [void][VtMorph]::Click([uint32]$proc.Id,
            [int](($wr.L + $wr.R) / 2), [int](($wr.T + $wr.B) / 2), $false)
        Start-Sleep -Milliseconds 500
    }

    if (-not [VtMorph]::ToggleLayout($hwnd)) { throw 'FOREGROUND_MISS: to horizontal' }
    Start-Burst $hwnd 'A-vertical-to-horizontal'
    Start-Sleep -Milliseconds 900

    if (-not [VtMorph]::ToggleLayout($hwnd)) { throw 'FOREGROUND_MISS: to vertical' }
    Start-Burst $hwnd 'B-horizontal-to-vertical'
    Start-Sleep -Milliseconds 900
}
finally {
    if ($null -ne $origXdg) { $env:XDG_CONFIG_HOME = $origXdg } else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        # The dying process can still hold handles under the temp dir; an
        # immediate delete leaks the GUID dir silently.
        try { $proc.WaitForExit(3000) } catch {}
    }
    Remove-Item -Recurse -Force $tempXdg -ErrorAction SilentlyContinue
}
Write-Host "OUT=$OutDir"
