#requires -Version 7
# Open a session with many colored tabs and capture the Ctrl+Tab switcher.
#
# Two things to look at in the output: the tiles wrap into a grid instead of
# running off the right edge, and tabs carrying a preset color show it on
# their tile.
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\Ghostty\bin\x64\Debug\net10.0-windows10.0.19041.0\Wintty.exe'),
    [string]$OutDir = (Join-Path $PSScriptRoot ("vtabs-switcher/run-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [int]$TabCount = 14,
    [switch]$Vertical
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class SwCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool r);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    const uint KEYUP = 0x0002;
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
    // Confirm the target owns the foreground before synthesizing anything:
    // SetForegroundWindow fails silently under the foreground lock and the
    // keystrokes would land in whatever is actually in front.
    public static bool Focus(IntPtr e) {
        for (int i = 0; i < 20; i++) {
            if (GetForegroundWindow() == e) return true;
            SetForegroundWindow(e); Thread.Sleep(50);
        }
        return GetForegroundWindow() == e;
    }
    public static bool Chord(IntPtr h, byte key, bool ctrl, bool shift) {
        if (!Focus(h)) return false;
        if (ctrl) keybd_event(0x11, 0, 0, UIntPtr.Zero);
        if (shift) keybd_event(0x10, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KEYUP, UIntPtr.Zero);
        if (shift) keybd_event(0x10, 0, KEYUP, UIntPtr.Zero);
        if (ctrl) keybd_event(0x11, 0, KEYUP, UIntPtr.Zero);
        return true;
    }
    public static bool Click(uint pid, int x, int y, bool right) {
        SetCursorPos(x, y); Thread.Sleep(60);
        var p = new POINT { X = x, Y = y };
        uint owner = 0; GetWindowThreadProcessId(WindowFromPoint(p), out owner);
        if (owner != pid) return false;
        uint down = right ? 0x0008u : 0x0002u, up = right ? 0x0010u : 0x0004u;
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        return true;
    }
}
'@

$script:Hwnd64 = 0
function Get-UiaRoot { [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]::new($script:Hwnd64)) }
function Find-ByName([string]$name, [int]$timeoutMs) {
    $dl = (Get-Date).AddMilliseconds($timeoutMs)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    while ((Get-Date) -lt $dl) {
        $el = (Get-UiaRoot).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 120
    }
    return $null
}
function Invoke-El($el) {
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
    catch { return $false }
}
# The color swatches are Borders carrying only an automation Name, so they
# expose no Invoke pattern and have to be clicked where they sit.
function Click-El($el, [uint32]$ProcId) {
    try {
        $r = $el.Current.BoundingRectangle
        if ($r.Width -le 0 -or $r.Height -le 0) { return $false }
        return [SwCap]::Click($ProcId, [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2), $false)
    } catch { return $false }
}
# Anything driven through UIA leaves focus on the element it touched, and the
# app's chords only reach the router when the terminal surface has focus.
# Every UIA interaction has to be followed by this or the next chord is lost.
function Restore-TerminalFocus([IntPtr]$hwnd, [uint32]$ProcId) {
    $r = [SwCap]::Rect($hwnd)
    [void][SwCap]::Click($ProcId, [int](($r.L + $r.R) / 2), [int](($r.T + $r.B) / 2), $false)
    Start-Sleep -Milliseconds 250
}
function Save-Shot([IntPtr]$hwnd, [string]$name) {
    $r = [SwCap]::Rect($hwnd)
    $w = $r.R - $r.L; $h = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir "$name.png")); $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $name (${w}x${h})"
}

$tempXdg = Join-Path $env:TEMP "wintty-switchercap-$([guid]::NewGuid())"
$cfgDir = Join-Path $tempXdg 'wintty'
New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null
@"
vertical-tabs = $($Vertical.IsPresent.ToString().ToLower())
windows-single-instance = false
window-theme = wintty
theme = Catppuccin Mocha
"@ | Set-Content -Path (Join-Path $cfgDir 'config.wintty') -Encoding utf8

$origXdg = $env:XDG_CONFIG_HOME
$env:XDG_CONFIG_HOME = $tempXdg
$proc = $null
try {
    $proc = Start-Process -FilePath $ExePath -PassThru
    $hwnd = [IntPtr]::Zero
    $dl = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 300
        $proc.Refresh()
        if ($proc.HasExited) { throw "exit $($proc.ExitCode)" }
        $hwnd = [SwCap]::FindWin([uint32]$proc.Id)
        if ($hwnd -ne [IntPtr]::Zero) { break }
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw 'no hwnd' }
    $script:Hwnd64 = $hwnd.ToInt64()
    Start-Sleep -Seconds 4
    [void][SwCap]::MoveWindow($hwnd, 30, 30, 1500, 900, $true)
    Start-Sleep -Milliseconds 700

    # The NO_COLOR infobar takes keyboard focus on launch when the
    # environment sets it, and swallows the chords that follow.
    $dismiss = Find-ByName 'Keep it off' 1500
    if ($null -ne $dismiss) { [void](Invoke-El $dismiss); Write-Host 'dismissed NO_COLOR infobar'; Start-Sleep -Milliseconds 400 }
    Restore-TerminalFocus $hwnd ([uint32]$proc.Id)

    foreach ($i in 2..$TabCount) {
        if (-not [SwCap]::Chord($hwnd, 0x54, $true, $false)) { throw 'FOREGROUND_MISS: new tab' }
        Start-Sleep -Milliseconds 420
    }
    Start-Sleep -Milliseconds 900

    # Color a spread of tabs so the capture shows colored and plain tiles
    # side by side.
    $wanted = @('Red', 'Green', 'Blue', 'Orange', 'Purple', 'Teal')
    $ctName = if ($Vertical) { 'ListItem' } else { 'TabItem' }
    $applied = 0
    $hits = (Get-UiaRoot).FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::$ctName)))
    Write-Host "strip items found: $($hits.Count) (expected $TabCount)"
    if ($hits.Count -lt 2) { throw "only $($hits.Count) tab(s) in the strip: the new-tab chords did not land" }
    for ($i = 0; $i -lt $hits.Count -and $applied -lt $wanted.Count; $i += 2) {
        $r = $hits[$i].Current.BoundingRectangle
        if ($r.Width -le 0) { continue }
        $x = [int]($r.X + $r.Width * 0.4); $y = [int]($r.Y + $r.Height / 2)
        if (-not [SwCap]::Click([uint32]$proc.Id, $x, $y, $true)) {
            Write-Host "  item $i : right-click rejected at $x,$y"
            continue
        }
        Start-Sleep -Milliseconds 400
        $pick = Find-ByName 'Tab Color...' 1800
        if ($null -eq $pick) {
            Write-Host "  item $i : no 'Tab Color...' in the flyout"
            [void][SwCap]::Chord($hwnd, 0x1B, $false, $false); continue
        }
        if (-not (Invoke-El $pick)) {
            Write-Host "  item $i : 'Tab Color...' would not invoke"
            [void][SwCap]::Chord($hwnd, 0x1B, $false, $false); continue
        }
        Start-Sleep -Milliseconds 350
        $sw = Find-ByName $wanted[$applied] 1500
        if ($null -eq $sw) {
            Write-Host "  item $i : swatch '$($wanted[$applied])' not found"
        } elseif (Click-El $sw ([uint32]$proc.Id)) {
            Write-Host "  item $i -> $($wanted[$applied])"
            $applied++
        } else {
            Write-Host "  item $i : swatch would not click"
        }
        Start-Sleep -Milliseconds 300
        [void][SwCap]::Chord($hwnd, 0x1B, $false, $false)
        Restore-TerminalFocus $hwnd ([uint32]$proc.Id)
    }
    Write-Host "colors applied: $applied"

    Save-Shot $hwnd 'strip-with-colors'

    Restore-TerminalFocus $hwnd ([uint32]$proc.Id)

    # The popup auto-dismisses after ~1.2s, so take a burst rather than bet
    # on one moment.
    if (-not [SwCap]::Chord($hwnd, 0x09, $true, $false)) { throw 'FOREGROUND_MISS: ctrl+tab' }
    foreach ($n in 0..3) {
        Start-Sleep -Milliseconds 150
        Save-Shot $hwnd ("switcher-$n")
    }

    Start-Sleep -Milliseconds 1500
    Restore-TerminalFocus $hwnd ([uint32]$proc.Id)
    if (-not [SwCap]::Chord($hwnd, 0x45, $true, $true)) { throw 'FOREGROUND_MISS: ctrl+shift+e' }
    Start-Sleep -Milliseconds 700
    Save-Shot $hwnd 'overview'
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    if ($null -ne $origXdg) { $env:XDG_CONFIG_HOME = $origXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force $tempXdg -ErrorAction SilentlyContinue
}
Write-Host "OUT=$OutDir"
