#requires -Version 7
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\Ghostty\bin\x64\Debug\net10.0-windows10.0.19041.0\Wintty.exe'),
    [string]$OutDir = (Join-Path $PSScriptRoot ("vtabs-layout-switch/run-" + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class VtCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public const uint KEYUP = 0x0002;
    public const byte VK_CONTROL = 0x11;
    public const byte VK_SHIFT = 0x10;
    public const byte VK_OEM_COMMA = 0xBC;
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
    // Synthesized keystrokes go to whatever owns the foreground, not to a
    // handle. SetForegroundWindow fails silently under the foreground lock,
    // so confirm the target actually has it before sending Ctrl+Shift+, --
    // otherwise the chord lands in the developer's editor or browser.
    public static bool ChordToggleLayout(IntPtr expected) {
        if (expected == IntPtr.Zero) return false;
        for (int i = 0; i < 20; i++) {
            if (GetForegroundWindow() == expected) break;
            SetForegroundWindow(expected);
            Thread.Sleep(50);
        }
        if (GetForegroundWindow() != expected) return false;
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
        keybd_event(VK_OEM_COMMA, 0, 0, UIntPtr.Zero);
        keybd_event(VK_OEM_COMMA, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_SHIFT, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
        return true;
    }
}
'@

function Save-Shot([IntPtr]$hwnd, [string]$name) {
    $r = [VtCap]::Rect($hwnd)
    $w = $r.R - $r.L; $h = $r.B - $r.T
    if ($w -lt 80 -or $h -lt 80) { throw "bad rect $name" }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
    $path = Join-Path $OutDir "$name.png"
    $bmp.Save($path); $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $path (${w}x${h})"
}

$tempXdg = Join-Path $env:TEMP "wintty-vtabs-cap-$([guid]::NewGuid())"
$cfgDir = Join-Path $tempXdg 'wintty'
New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null
@'
vertical-tabs = true
windows-single-instance = false
window-theme = wintty
theme = Catppuccin Mocha
'@ | Set-Content -Path (Join-Path $cfgDir 'config.wintty') -Encoding utf8

$origXdg = $env:XDG_CONFIG_HOME
$env:XDG_CONFIG_HOME = $tempXdg
try {
    $proc = Start-Process -FilePath $ExePath -PassThru
    $dl = (Get-Date).AddSeconds(45)
    $hwnd = [IntPtr]::Zero
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 300
        $proc.Refresh()
        if ($proc.HasExited) { throw "exit $($proc.ExitCode)" }
        $hwnd = [VtCap]::FindWin([uint32]$proc.Id)
        if ($hwnd -ne [IntPtr]::Zero) { break }
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw 'no hwnd' }
    Start-Sleep -Seconds 3
    [void][VtCap]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 400
    Save-Shot $hwnd '01-vertical'

    if (-not [VtCap]::ChordToggleLayout($hwnd)) { throw 'FOREGROUND_MISS: layout chord not sent' }
    Start-Sleep -Milliseconds 170
    Save-Shot $hwnd '02-switch-to-horizontal-mid'
    Start-Sleep -Milliseconds 450
    Save-Shot $hwnd '03-horizontal'

    if (-not [VtCap]::ChordToggleLayout($hwnd)) { throw 'FOREGROUND_MISS: layout chord not sent' }
    Start-Sleep -Milliseconds 170
    Save-Shot $hwnd '04-switch-to-vertical-mid'
    Start-Sleep -Milliseconds 450
    Save-Shot $hwnd '05-vertical-again'

    # Crop top-right caption band for inspection
    foreach ($f in Get-ChildItem $OutDir -Filter '*.png') {
        $bmp = [System.Drawing.Image]::FromFile($f.FullName)
        $cw = [Math]::Min(420, $bmp.Width)
        $ch = [Math]::Min(80, $bmp.Height)
        $crop = New-Object System.Drawing.Bitmap $cw, $ch
        $g = [System.Drawing.Graphics]::FromImage($crop)
        $srcRect = New-Object System.Drawing.Rectangle ($bmp.Width - $cw), 0, $cw, $ch
        $dstRect = New-Object System.Drawing.Rectangle 0, 0, $cw, $ch
        $g.DrawImage($bmp, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
        $cropPath = Join-Path $OutDir ($f.BaseName + '-caption.png')
        $crop.Save($cropPath)
        $g.Dispose(); $crop.Dispose(); $bmp.Dispose()
    }
}
finally {
    if ($null -ne $origXdg) { $env:XDG_CONFIG_HOME = $origXdg } else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force $tempXdg -ErrorAction SilentlyContinue
}
Write-Host "OUT=$OutDir"
