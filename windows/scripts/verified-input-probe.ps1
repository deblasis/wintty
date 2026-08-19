#requires -Version 7
<#
.SYNOPSIS
  Drive Wintty with positive evidence that keys landed in Wintty, not Cursor.

.DESCRIPTION
  Hard gate: every SendInput is preceded by GetForegroundWindow belonging
  to the Wintty pid. No modifier chords (Ctrl+Shift+I/P/F/C, Ctrl+,, F12
  are Cursor / Chromium). Input is KEYEVENTF_UNICODE only plus one
  client-area click to focus the grid.

  Verdicts:
    HARVEST_MISS  keys never reached Wintty (foreground gate or marker
                  absent from the post-type screenshot) — not a product fail
    PRODUCT_FAIL  Wintty died, or crash.log grew, after keys were proven
    PASS          process alive, crash.log unchanged, screenshots captured
                  after a verified-foreground type
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)

. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots') | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Probe {
    public const int INPUT_KEYBOARD = 1;
    public const int INPUT_MOUSE = 0;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public int type; public INPUTUNION u; }
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT {
        public ushort wVk; public ushort wScan; public uint dwFlags;
        public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT {
        public int dx, dy; public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int dwProcessId);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr lp);
    public const uint WM_CHAR = 0x0102;
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);

    public static void Uni(char ch) {
        var down = new INPUT();
        down.type = INPUT_KEYBOARD;
        down.u.ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE };
        var up = down;
        up.u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        SendInput(2, new[]{ down, up }, Marshal.SizeOf<INPUT>());
    }
    public static void Click() {
        var d = new INPUT(); d.type = INPUT_MOUSE;
        d.u.mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN };
        var u = d; u.u.mi.dwFlags = MOUSEEVENTF_LEFTUP;
        SendInput(2, new[]{ d, u }, Marshal.SizeOf<INPUT>());
    }
}
'@

function Get-WinttyMainHwnd([int]$ProcId) {
    $hits = [System.Collections.Generic.List[hashtable]]::new()
    $cb = [Probe+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner = 0
        [void][Probe]::GetWindowThreadProcessId($hwnd, [ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        if (-not [Probe]::IsWindowVisible($hwnd)) { return $true }
        $cls = New-Object System.Text.StringBuilder 256
        $title = New-Object System.Text.StringBuilder 512
        [void][Probe]::GetClassName($hwnd, $cls, 256)
        [void][Probe]::GetWindowText($hwnd, $title, 512)
        $name = $cls.ToString()
        if ($name -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $r = New-Object Probe+RECT
        [void][Probe]::GetWindowRect($hwnd, [ref]$r)
        $hits.Add(@{
            Hwnd = $hwnd; Class = $name; Title = $title.ToString()
            Area = [Math]::Max(0, ($r.Right-$r.Left)*($r.Bottom-$r.Top))
        })
        return $true
    }
    [void][Probe]::EnumWindows($cb, [IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending | Select-Object -First 1
}

function Splash-Alive([int]$ProcId) {
    $alive = $false
    $cb = [Probe+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner = 0
        [void][Probe]::GetWindowThreadProcessId($hwnd, [ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        $cls = New-Object System.Text.StringBuilder 256
        [void][Probe]::GetClassName($hwnd, $cls, 256)
        if ($cls.ToString() -eq 'WinttySplash' -and [Probe]::IsWindowVisible($hwnd)) {
            $script:splashSeen = $true
        }
        return $true
    }
    $script:splashSeen = $false
    [void][Probe]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:splashSeen
}

function Assert-ForegroundIsWintty([int]$ProcId, [IntPtr]$Hwnd) {
    if ([Probe]::IsIconic($Hwnd)) { [void][Probe]::ShowWindow($Hwnd, 9) }
    # Windows blocks SetForegroundWindow from a background pwsh unless we
    # attach to the current foreground thread. Without this, keys go to
    # Cursor (Ctrl+Shift+I opens DevTools).
    $fg0 = [Probe]::GetForegroundWindow()
    [uint32]$fgPid0 = 0
    $fgTid = [Probe]::GetWindowThreadProcessId($fg0, [ref]$fgPid0)
    $selfTid = [Probe]::GetCurrentThreadId()
    if ($fgTid -ne 0 -and $fgTid -ne $selfTid) {
        [void][Probe]::AttachThreadInput($selfTid, $fgTid, $true)
    }
    [void][Probe]::BringWindowToTop($Hwnd)
    [void][Probe]::SetForegroundWindow($Hwnd)
    if ($fgTid -ne 0 -and $fgTid -ne $selfTid) {
        [void][Probe]::AttachThreadInput($selfTid, $fgTid, $false)
    }
    Start-Sleep -Milliseconds 250
    $fg = [Probe]::GetForegroundWindow()
    [uint32]$fgPid = 0
    [void][Probe]::GetWindowThreadProcessId($fg, [ref]$fgPid)
    $name = (Get-Process -Id $fgPid -ErrorAction SilentlyContinue).ProcessName
    if ($fgPid -ne $ProcId -or $name -ne 'Wintty') {
        throw "HARVEST_MISS: foreground is pid=$fgPid name=$name (want Wintty $ProcId). Refusing SendInput."
    }
}

function Shot([IntPtr]$Hwnd, [string]$Name) {
    $r = New-Object Probe+RECT
    [void][Probe]::GetWindowRect($Hwnd, [ref]$r)
    $w = [Math]::Max(1, $r.Right - $r.Left)
    $h = [Math]::Max(1, $r.Bottom - $r.Top)
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $path = Join-Path $OutDir "shots\$Name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return $path
}

function Get-HwndAndChildren([IntPtr]$Root) {
    $all = [System.Collections.Generic.List[IntPtr]]::new()
    $all.Add($Root)
    $cb = [Probe+EnumProc]{
        param($h, $lp)
        $all.Add($h)
        return $true
    }
    [void][Probe]::EnumChildWindows($Root, $cb, [IntPtr]::Zero)
    return $all
}

# Post WM_CHAR into Wintty's HWND tree. Never uses the global key queue,
# so Cursor cannot receive the input even if it stays foreground.
function Post-Chars([IntPtr]$Root, [string]$Text) {
    # Top-level WinUI hwnd only. Broadcasting to children also hits the
    # hidden PseudoConsoleWindow and garbles supplementary-plane units.
    foreach ($ch in $Text.ToCharArray()) {
        $wp = [IntPtr][uint16][char]$ch
        [void][Probe]::PostMessage($Root, [Probe]::WM_CHAR, $wp, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 20
    }
}

# --- launch ---
if (-not (Test-Path -LiteralPath $ExePath)) { throw "exe missing: $ExePath" }
Assert-NoWintty
$script:WinttyStamp = Get-WinttyLaunchStamp
Start-Sleep -Milliseconds 400

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path $ExePath)
$deadline = (Get-Date).AddSeconds(40)
$main = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
    $proc.Refresh()
    if ($proc.HasExited) { throw "PRODUCT_FAIL: exited at startup code=$($proc.ExitCode)" }
    if (Splash-Alive $proc.Id) { continue }
    $main = Get-WinttyMainHwnd $proc.Id
    if ($main) { break }
}
if (-not $main) { throw "HARVEST_MISS: no non-splash Wintty window" }

Start-Sleep -Seconds 1
$again = Get-WinttyMainHwnd $proc.Id
if ($again) { $main = $again }
if (-not $main) { throw "HARVEST_MISS: main hwnd lost after splash" }
$hwnd = [IntPtr]([int64]$main.Hwnd)

# Screenshot the Wintty hwnd rect only. Do not SendInput keyboard —
# that queue belongs to whoever Windows left foreground (Cursor).
$shot0 = Shot $hwnd '00-before'
Write-Host "TARGET class=$($main.Class) title=$($main.Title) hwnd=$hwnd shot=$shot0"

# Mouse click on Wintty's own pixels so the XAML island takes focus.
# Hits the window under the cursor, not the foreground process.
$r = New-Object Probe+RECT
[void][Probe]::GetWindowRect($hwnd, [ref]$r)
[void][Probe]::SetCursorPos(($r.Left + 280), ($r.Top + 220))
Start-Sleep -Milliseconds 80
[Probe]::Click()
Start-Sleep -Milliseconds 200
Shot $hwnd '01-clicked-grid' | Out-Null

$marker = "PROBE$(Get-Random -Minimum 100000 -Maximum 999999)"
Post-Chars $hwnd $marker
Start-Sleep -Milliseconds 500
$shotAscii = Shot $hwnd '02-after-ascii'
$proc.Refresh()
if ($proc.HasExited) { throw "PRODUCT_FAIL: died after ASCII marker" }

$rocket = [char]::ConvertFromUtf32(0x1F680)
Post-Chars $hwnd $rocket
Start-Sleep -Milliseconds 800
$proc.Refresh()
$shotEmoji = Shot $hwnd '03-after-emoji'

$crashGrew = $false
if (Test-Path $crashPath) {
    $item = Get-Item $crashPath
    if ($item.LastWriteTimeUtc -gt $crashStamp) {
        $crashGrew = $true
        Copy-Item $crashPath (Join-Path $OutDir 'crash.log') -Force
    }
}

$result = [ordered]@{
    verdict = $null
    pid = $proc.Id
    exited = $proc.HasExited
    exitCode = if ($proc.HasExited) { $proc.ExitCode } else { $null }
    crashGrew = $crashGrew
    marker = $marker
    shots = @{ focused = $shot0; ascii = $shotAscii; emoji = $shotEmoji }
}

if ($proc.HasExited -or $crashGrew) {
    $result.verdict = 'PRODUCT_FAIL'
} else {
    $result.verdict = 'PASS_PENDING_SCREENSHOT'  # caller must Read the shots
}

$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json')
Write-Host ($result | ConvertTo-Json -Depth 5)

# Leave Wintty up so the shots can be inspected. Do not Alt+F4.
if ($result.verdict -eq 'PRODUCT_FAIL') { exit 2 }
exit 0
