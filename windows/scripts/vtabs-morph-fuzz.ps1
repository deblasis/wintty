#requires -Version 7
# Randomized stress for the horizontal<->vertical tab layout switch.
#
# The layout switch has to hold up with a strip full of tabs, tabs whose
# titles and icons keep changing under it, per-tab colors, and switches that
# arrive before the previous one has finished. This drives all of that from a
# seeded RNG so a failure can be replayed with -Seed.
#
# Pass/fail is not "did it crash": the app emits a morph trace whenever the
# WINTTY_MORPH_TRACE env var names a log file (set per run below), and every
# SWITCH end line must report ghosts=0 and morph=null. A ghost left parked on
# the morph layer is the artifact this whole change exists to remove, so it
# is the thing worth asserting.
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\Ghostty\bin\x64\Debug\net10.0-windows10.0.19041.0\Wintty.exe'),
    [int]$Seed = 0,
    [int]$Iterations = 60,
    [switch]$StartHorizontal
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
$ErrorActionPreference = 'Stop'

# Same convention as the mouse-fuzz harnesses: a PRODUCT_FAIL leaves with 2,
# anything else is a run that could not judge the product and leaves with 1
# for the runner to retry. FOREGROUND_MISS and a missing trace are the latter
# - a stolen foreground says nothing about the build.
trap {
    if ("$_" -like 'PRODUCT_FAIL*') {
        Write-Host "$_" -ForegroundColor Red
        exit 2
    }
    break
}
if ($Seed -eq 0) { $Seed = Get-Random -Minimum 1 -Maximum 999999 }
$rng = [System.Random]::new($Seed)
Write-Host "seed=$Seed iterations=$Iterations"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class MFz {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool r);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
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
    // Synthesized input goes to whoever owns the foreground, not to a
    // handle, and SetForegroundWindow fails silently under the foreground
    // lock. Every send confirms ownership first so a stray chord cannot land
    // in the developer's editor.
    public static bool Focus(IntPtr expected) {
        if (expected == IntPtr.Zero || !IsWindow(expected)) return false;
        for (int i = 0; i < 20; i++) {
            if (GetForegroundWindow() == expected) return true;
            SetForegroundWindow(expected);
            Thread.Sleep(50);
        }
        return GetForegroundWindow() == expected;
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
    public static bool Type(IntPtr h, string text) {
        if (!Focus(h)) return false;
        foreach (char c in text) {
            // The burst runs for hundreds of ms; re-confirm ownership per
            // character so an alt-tab (or app crash) cannot route the tail
            // of a shell command into a foreign window.
            if (GetForegroundWindow() != h) return false;
            short vk = VkKeyScan(c);
            bool shift = (vk & 0x100) != 0;
            byte k = (byte)(vk & 0xFF);
            if (shift) keybd_event(0x10, 0, 0, UIntPtr.Zero);
            keybd_event(k, 0, 0, UIntPtr.Zero);
            keybd_event(k, 0, KEYUP, UIntPtr.Zero);
            if (shift) keybd_event(0x10, 0, KEYUP, UIntPtr.Zero);
            Thread.Sleep(6);
        }
        // Enter is the keystroke that executes; never send it blind.
        if (GetForegroundWindow() != h) return false;
        keybd_event(0x0D, 0, 0, UIntPtr.Zero);
        keybd_event(0x0D, 0, KEYUP, UIntPtr.Zero);
        return true;
    }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern short VkKeyScan(char c);
    // Re-probe after the cursor settles: the point that was over a tab when
    // the rect was measured may be over a flyout by the time the click lands.
    public static bool Click(uint pid, int x, int y, bool right) {
        SetCursorPos(x, y);
        Thread.Sleep(60);
        // The buttons land wherever the cursor is NOW, not at the scripted
        // point: a user mouse move during the settle would divorce the pid
        // probe from the click. Probe the live position and require it to
        // still be ours.
        POINT live; if (!GetCursorPos(out live)) return false;
        if (Math.Abs(live.X - x) > 2 || Math.Abs(live.Y - y) > 2) return false;
        IntPtr under = WindowFromPoint(live);
        uint owner = 0; GetWindowThreadProcessId(under, out owner);
        if (owner != pid) return false;
        uint down = right ? 0x0008u : 0x0002u;
        uint up = right ? 0x0010u : 0x0004u;
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        return true;
    }
}
'@

$VK = @{ T = 0x54; W = 0x57; Comma = 0xBC; Tab = 0x09; Esc = 0x1B }
$Colors = @('Red', 'Orange', 'Yellow', 'Green', 'Teal', 'Blue', 'Purple', 'Pink', 'None')
# Each spawns a different foreground process, so tab icons and titles differ
# and the morph ghost has to copy something other than a default cmd tab.
$Shells = @('powershell -NoLogo', 'cmd', 'powershell -NoLogo -Command "$host.UI.RawUI.WindowTitle=''fuzz''; cmd"')

function Get-UiaRoot([int64]$h) {
    return [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]::new($h))
}
function Find-ByName($root, [string]$name, [int]$timeoutMs) {
    $dl = (Get-Date).AddMilliseconds($timeoutMs)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    while ((Get-Date) -lt $dl) {
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 120
        $root = Get-UiaRoot $script:Hwnd64
    }
    return $null
}
function Invoke-El($el) {
    try {
        $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pat.Invoke()
        return $true
    } catch { return $false }
}

# Per-run path: a fixed name would interleave lines from a concurrently
# running instrumented instance in another worktree and corrupt the oracle.
$log = Join-Path $env:TEMP "wintty-morph-$([guid]::NewGuid()).log"

$tempXdg = Join-Path $env:TEMP "wintty-morphfuzz-$([guid]::NewGuid())"
$cfgDir = Join-Path $tempXdg 'wintty'
New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null
$startVertical = -not $StartHorizontal
@"
vertical-tabs = $($startVertical.ToString().ToLower())
windows-single-instance = false
window-theme = wintty
theme = Catppuccin Mocha
"@ | Set-Content -Path (Join-Path $cfgDir 'config.wintty') -Encoding utf8

$origXdg = $env:XDG_CONFIG_HOME
$env:XDG_CONFIG_HOME = $tempXdg
$origTrace = $env:WINTTY_MORPH_TRACE
$env:WINTTY_MORPH_TRACE = $log
$proc = $null
$actions = @{}
$chordMisses = 0
$failures = New-Object System.Collections.Generic.List[string]
# Above the try so a refusal keeps its own message: with the gate inside, the
# sweep in the finally would bind a null stamp to a mandatory [datetime] and
# that error would replace it, taking the env restores with it.
Assert-NoWintty -Context 'The layout-morph fuzz'
$script:WinttyStamp = Get-WinttyLaunchStamp

try {
    $proc = Start-Process -FilePath $ExePath -PassThru
    $hwnd = [IntPtr]::Zero
    $dl = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 300
        $proc.Refresh()
        if ($proc.HasExited) { throw "PRODUCT_FAIL: exited during startup, code $($proc.ExitCode)" }
        $hwnd = [MFz]::FindWin([uint32]$proc.Id)
        if ($hwnd -ne [IntPtr]::Zero) { break }
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw 'no hwnd' }
    $script:Hwnd64 = $hwnd.ToInt64()
    Start-Sleep -Seconds 4
    [void][MFz]::MoveWindow($hwnd, 40, 40, 1400, 860, $true)
    Start-Sleep -Milliseconds 700

    # Seed the strip so the very first switches already have a crowd.
    foreach ($i in 1..7) {
        if (-not [MFz]::Chord($hwnd, $VK.T, $true, $false)) { throw 'FOREGROUND_MISS: seed tab' }
        Start-Sleep -Milliseconds 500
    }
    $tabs = 8

    # One deterministic toggle up front proves the oracle is alive before
    # minutes of fuzzing are spent on a build that cannot report it.
    if (-not [MFz]::Chord($hwnd, $VK.Comma, $true, $true)) { throw 'FOREGROUND_MISS: probe toggle' }
    Start-Sleep -Milliseconds 1200
    if (-not (Test-Path $log)) {
        throw "no morph trace at $log - the app ignored WINTTY_MORPH_TRACE; this build predates the built-in trace"
    }

    for ($i = 0; $i -lt $Iterations; $i++) {
        if (-not [MFz]::IsWindow($hwnd)) { throw "PRODUCT_FAIL: window gone at iteration $i" }
        $proc.Refresh()
        if ($proc.HasExited) { throw "PRODUCT_FAIL: process exited at iteration $i (code $($proc.ExitCode))" }

        $roll = $rng.Next(100)
        if ($roll -lt 45) {
            $act = 'toggle'
            # A flyout left open by an earlier color pick would swallow the
            # chord, and a swallowed toggle is a fuzz iteration that tested
            # nothing.
            [void][MFz]::Chord($hwnd, $VK.Esc, $false, $false)
            Start-Sleep -Milliseconds 90
            if (-not [MFz]::Chord($hwnd, $VK.Comma, $true, $true)) { $chordMisses++ }
            # Sometimes toggle again before the switch has landed: the
            # coordinator must not leave a ghost behind when interrupted.
            if ($rng.Next(100) -lt 30) {
                Start-Sleep -Milliseconds $rng.Next(40, 320)
                [void][MFz]::Chord($hwnd, $VK.Comma, $true, $true)
                $act = 'toggle-interrupted'
            }
        }
        elseif ($roll -lt 58) {
            $act = 'new-tab'
            [void][MFz]::Chord($hwnd, $VK.T, $true, $false)
            $tabs++
        }
        elseif ($roll -lt 66 -and $tabs -gt 3) {
            # Dismiss any confirmation dialog left from an earlier close, then
            # trust the strip over the shadow counter: with a stale counter the
            # fuzz eventually closes the last tab, which closes the window.
            [void][MFz]::Chord($hwnd, $VK.Esc, $false, $false)
            Start-Sleep -Milliseconds 120
            $actual = 0
            try {
                $root = Get-UiaRoot $script:Hwnd64
                foreach ($ct in @('TabItem', 'ListItem')) {
                    $n = $root.FindAll(
                        [System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition(
                            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                            [System.Windows.Automation.ControlType]::$ct))).Count
                    if ($n -gt $actual) { $actual = $n }
                }
            } catch { $actual = 0 }
            if ($actual -gt 3) {
                $act = 'close-tab'
                [void][MFz]::Chord($hwnd, $VK.W, $true, $false)
                $tabs = $actual - 1
            } else {
                $act = 'close-skipped'
                if ($actual -gt 0) { $tabs = $actual }
            }
        }
        elseif ($roll -lt 78) {
            $act = 'switch-tab'
            [void][MFz]::Chord($hwnd, $VK.Tab, $true, ($rng.Next(2) -eq 0))
        }
        elseif ($roll -lt 90) {
            $act = 'spawn-shell'
            # A pending confirmation dialog would treat the trailing Enter as
            # its accept button; clear it before typing.
            [void][MFz]::Chord($hwnd, $VK.Esc, $false, $false)
            Start-Sleep -Milliseconds 100
            [void][MFz]::Type($hwnd, $Shells[$rng.Next($Shells.Count)])
        }
        else {
            $act = 'tab-color'
            $color = $Colors[$rng.Next($Colors.Count)]
            try {
                $root = Get-UiaRoot $script:Hwnd64
                # The strip is TabItems in the header and ListItems on the
                # rail, so the color path has to work either way round.
                $nav = $null
                foreach ($ct in @('TabItem', 'ListItem')) {
                    $hits = $root.FindAll(
                        [System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition(
                            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                            [System.Windows.Automation.ControlType]::$ct)))
                    if ($hits.Count -gt 0) {
                        $nav = $hits[$rng.Next($hits.Count)]
                        break
                    }
                }
                if ($null -eq $nav) {
                    $act = 'tab-color-skipped'
                } else {
                    $r = $nav.Current.BoundingRectangle
                    $x = [int]($r.X + $r.Width * 0.4); $y = [int]($r.Y + $r.Height / 2)
                    if ([MFz]::Click([uint32]$proc.Id, $x, $y, $true)) {
                        Start-Sleep -Milliseconds 400
                        $root = Get-UiaRoot $script:Hwnd64
                        $pick = Find-ByName $root 'Tab Color...' 1800
                        if ($null -ne $pick -and (Invoke-El $pick)) {
                            Start-Sleep -Milliseconds 350
                            $root = Get-UiaRoot $script:Hwnd64
                            $sw = Find-ByName $root $color 1500
                            if ($null -ne $sw) { [void](Invoke-El $sw) } else { $act = 'tab-color-miss' }
                        } else { $act = 'tab-color-miss' }
                        Start-Sleep -Milliseconds 250
                        [void][MFz]::Chord($hwnd, $VK.Esc, $false, $false)
                        # UIA leaves focus on the swatch it clicked, and the
                        # layout chord only reaches the router from the
                        # terminal surface. Without this the fuzz spends the
                        # rest of the run sending toggles nobody handles.
                        $wr = [MFz]::Rect($hwnd)
                        [void][MFz]::Click([uint32]$proc.Id,
                            [int](($wr.L + $wr.R) / 2), [int](($wr.T + $wr.B) / 2), $false)
                        Start-Sleep -Milliseconds 200
                    } else { $act = 'tab-color-skipped' }
                }
            } catch {
                $act = 'tab-color-error'
                [void][MFz]::Chord($hwnd, $VK.Esc, $false, $false)
            }
        }

        $actions[$act] = 1 + ($actions[$act] ?? 0)
        Start-Sleep -Milliseconds $rng.Next(160, 700)
    }

    # Let the last switch land before reading the trace.
    Start-Sleep -Milliseconds 1200
}
finally {
    if ($proc -and -not $proc.HasExited) { try { $proc.Kill($true); [void]$proc.WaitForExit(3000) } catch { } }
    Start-Sleep -Milliseconds 600
    Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $ExePath
    if ($null -ne $origXdg) { $env:XDG_CONFIG_HOME = $origXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
    if ($null -ne $origTrace) { $env:WINTTY_MORPH_TRACE = $origTrace }
    else { Remove-Item Env:WINTTY_MORPH_TRACE -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force $tempXdg -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'actions:'
$actions.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Host ("  {0,-20} {1}" -f $_.Key, $_.Value) }

if (-not (Test-Path $log)) { throw "no morph trace at $log - the app ignored WINTTY_MORPH_TRACE" }
$lines = Get-Content $log
$begins = @($lines | Where-Object { $_ -like 'SWITCH begin*' }).Count
$ends = @($lines | Where-Object { $_ -like 'SWITCH end*' }).Count
$immediate = @($lines | Where-Object { $_ -like 'MORPH immediate*' }).Count
$deferred = @($lines | Where-Object { $_ -like 'MORPH deferred*' }).Count
$waiting = @($lines | Where-Object { $_ -like 'MORPH waiting*' }).Count
$none = @($lines | Where-Object { $_ -like 'MORPH none*' }).Count
$leaked = @($lines | Where-Object { $_ -match 'ghosts=[1-9]|morph=LEAKED' })

Write-Host ''
Write-Host "chord misses   : $chordMisses"
Write-Host "switches begun : $begins"
Write-Host "switches ended : $ends"
Write-Host "morph immediate: $immediate"
Write-Host "morph deferred : $deferred  (waited: $waiting)"
Write-Host "morph none     : $none"

if ($leaked.Count -gt 0) {
    $failures.Add("$($leaked.Count) switch(es) ended with a ghost still on the morph layer")
    $leaked | Select-Object -First 8 | ForEach-Object { Write-Host "  LEAK: $_" }
}
if ($begins -ne $ends) {
    $failures.Add("switch begin/end mismatch: $begins vs $ends (a switch never finished)")
}
if ($immediate -eq 0 -and $begins -gt 0) {
    $failures.Add('no switch ever staged a morph immediately')
}

Write-Host ''
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    Write-Host "reproduce with: -Seed $Seed"
    # 2, not 1: these are layout-switch defects, and 1 is reserved for a run
    # that never got to assert anything.
    exit 2
}
Write-Host "PASS (seed $Seed)"
