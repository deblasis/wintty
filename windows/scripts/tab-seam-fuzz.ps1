#requires -Version 7
<#
.SYNOPSIS
Checks that the selected tab stays joined to the terminal under churn.

.DESCRIPTION
The selected tab is drawn as a folder: stroked on the three sides that do not
meet the pane, and the pane's own border is covered for exactly the span where
the two meet, so the tab's fill runs into the terminal with no line across it.
That cover is positioned from the selected tab's layout offset, which moves
whenever a tab is opened or closed, the strip is resized, the layout is
switched, or the strip scrolls. This drives all of those and checks the seam
after each one.

The oracle reads the seam straight off a screenshot, so it fails on what the
user would actually see rather than on what the code believed it drew:

  * the pane border runs the width of the pane, broken in exactly ONE place
  * that break is filled with the terminal background, not the strip's colour
  * the break is a plausible tab width

Zero breaks is also a pass: the selected tab can be scrolled out of the strip
entirely, and the correct thing then is an unbroken border.

.PARAMETER Seed
Fixes the action sequence. The same seed always drives the same run.

.EXAMPLE
pwsh -File windows/scripts/tab-seam-fuzz.ps1 -Iterations 40 -Seed 7

.NOTES
Exit codes: 0 all checks passed, 1 a seam check failed (failing frames are
written to OutDir), 2 the harness could not run (no window, lost foreground).

This is a diagnostic, not a gate. It found two real bugs (the cover not drawing
with more than one tab, and drift after a layout switch), but across seeds it
still swings between clean runs and most-of-its-iterations failing with no
product change in between, from its own detection: window geometry, accent
detection, and popups such as the tab switcher occluding the strip. Read a
failure as a prompt to look at the frames it wrote, never as a verdict.
#>
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\Ghostty\bin\x64\Debug\net10.0-windows10.0.19041.0\Wintty.exe'),
    [string]$OutDir = (Join-Path $PSScriptRoot 'tab-seam-fuzz'),
    [int]$Iterations = 30,
    [int]$Seed = 1
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Seam {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int hh,bool r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int c);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a,uint b,bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}

// The oracle runs here rather than in PowerShell. It reads a few hundred
// thousand pixels a frame, and at PowerShell's per-operation cost that is
// tens of seconds an iteration -- slow enough that a run looks hung and gets
// killed before it finds anything.
public static class SeamOracle {
    static int Px(byte[] d, int stride, int x, int y) {
        int i = y * stride + x * 4;
        return (d[i+2] << 16) | (d[i+1] << 8) | d[i];
    }
    static bool Near(int v, int r, int g, int b) {
        return Math.Abs(((v >> 16) & 0xFF) - r) <= 24
            && Math.Abs(((v >> 8) & 0xFF) - g) <= 24
            && Math.Abs((v & 0xFF) - b) <= 24;
    }

    /// Most common colour in the region from (x0,y0) to the bottom-right.
    static int ModalColour(byte[] d, int stride, int w, int h, int x0, int y0) {
        var seen = new System.Collections.Generic.Dictionary<int,int>();
        for (int y = y0; y < h; y += 5)
            for (int x = x0; x < w; x += 5) {
                int v = Px(d, stride, x, y);
                seen[v] = seen.TryGetValue(v, out var c) ? c + 1 : 1;
            }
        int bestV = 0, bestN = 0;
        foreach (var kv in seen) if (kv.Value > bestN) { bestN = kv.Value; bestV = kv.Key; }
        return bestV;
    }

    /// Returns null when the seam is correct, else a description of what is wrong.
    public static string Check(byte[] d, int stride, int w, int h, bool vertical) {
        int scanDepth = Math.Min(120, h);

        // The accent is the most common strongly-saturated colour in the
        // chrome band. Found per frame so the check holds in either half of
        // the theme without hard-coding either palette.
        var counts = new System.Collections.Generic.Dictionary<int,int>();
        for (int y = 0; y < scanDepth; y++)
            for (int x = 0; x < w; x += 2) {
                int v = Px(d, stride, x, y);
                int r = (v >> 16) & 0xFF, g = (v >> 8) & 0xFF, b = v & 0xFF;
                if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 60) continue;
                counts[v] = counts.TryGetValue(v, out var c) ? c + 1 : 1;
            }
        if (counts.Count == 0) return "no accent colour found in the chrome band";

        int accent = 0, best = 0;
        foreach (var kv in counts) if (kv.Value > best) { best = kv.Value; accent = kv.Key; }
        int ar = (accent >> 16) & 0xFF, ag = (accent >> 8) & 0xFF, ab = accent & 0xFF;

        // The seam line is whichever row (or column) carries the most accent.
        int bestIdx = -1; int bestCount = 0;
        if (!vertical) {
            for (int y = 0; y < scanDepth; y++) {
                int n = 0;
                for (int x = 0; x < w; x += 2) if (Near(Px(d, stride, x, y), ar, ag, ab)) n++;
                if (n > bestCount) { bestCount = n; bestIdx = y; }
            }
        } else {
            int limit = Math.Min(400, w);
            for (int x = 0; x < limit; x++) {
                int n = 0;
                for (int y = 0; y < h; y += 4) if (Near(Px(d, stride, x, y), ar, ag, ab)) n++;
                if (n > bestCount) { bestCount = n; bestIdx = x; }
            }
        }
        if (bestIdx < 0 || bestCount < 20) return "no seam line found";

        int len = vertical ? h : w;
        var on = new bool[len];
        for (int i = 0; i < len; i++)
            on[i] = vertical ? Near(Px(d, stride, bestIdx, i), ar, ag, ab)
                             : Near(Px(d, stride, i, bestIdx), ar, ag, ab);

        int first = Array.IndexOf(on, true), last = Array.LastIndexOf(on, true);
        if (first < 0 || last - first < 50) return "seam line too short to judge";

        var gaps = new System.Collections.Generic.List<int[]>();
        int runStart = -1;
        for (int i = first; i <= last; i++) {
            if (!on[i]) { if (runStart < 0) runStart = i; }
            else if (runStart >= 0) { gaps.Add(new[]{runStart, i - runStart}); runStart = -1; }
        }
        if (runStart >= 0) gaps.Add(new[]{runStart, last - runStart + 1});
        gaps.RemoveAll(gp => gp[1] <= 3);

        // The terminal's colour, as the most common colour over the pane
        // rather than one probe pixel. A single probe lands inside whatever
        // dialog, notice or switcher popup happens to be open, and then the
        // filter below has the occlusion's colour as its idea of "terminal"
        // -- it throws away the real break and keeps the overlay's.
        int termProbe = ModalColour(d, stride, w, h,
            vertical ? Math.Min(bestIdx + 6, w - 1) : 0,
            vertical ? 0 : Math.Min(bestIdx + 6, h - 1));
        int tr = (termProbe >> 16) & 0xFF, tg = (termProbe >> 8) & 0xFF, tb = termProbe & 0xFF;

        // A break the cover made is filled with the terminal's own colour.
        // A break made by something drawn over the border -- Ctrl+Tab raises
        // a switcher popup across it, and dialogs and notices do the same --
        // is not, and is nothing to do with the seam. Judging those as extra
        // breaks fails the run for a transient overlay.
        gaps.RemoveAll(gp => {
            int mid = gp[0] + gp[1] / 2;
            int v = vertical ? Px(d, stride, bestIdx, mid) : Px(d, stride, mid, bestIdx);
            return !(Math.Abs(((v >> 16) & 0xFF) - tr) <= 6
                  && Math.Abs(((v >> 8) & 0xFF) - tg) <= 6
                  && Math.Abs((v & 0xFF) - tb) <= 6);
        });

        // Is the selected tab actually on screen? It is the one filled with
        // the terminal's own colour, so look for that fill in the strip just
        // above the seam. Without this the check has a hole big enough to
        // drive the bug through: a cover that never draws leaves the border
        // unbroken, and "no break" would otherwise read as a pass.
        int term = termProbe;
        int stripLine = bestIdx - 4;
        int tabStart = -1, tabLen = 0;
        if (stripLine >= 0) {
            int cur = -1;
            // Only within the seam's own extent. In vertical layout the strip
            // carries a title row above the pane, painted from the same
            // terminal palette, and searching the whole line finds that
            // instead of the selected row -- then every break is reported as
            // misaligned against a band that is not a tab at all.
            for (int i = first; i <= last; i++) {
                int v = vertical ? Px(d, stride, stripLine, i) : Px(d, stride, i, stripLine);
                // Exact, not near. The selected tab is filled with the
                // terminal's own colour and the strip beside it is the
                // window's Mica, which in the light theme lands within three
                // counts of it -- a tolerant match calls the whole strip
                // "tab" and the span check then compares against nonsense.
                bool isTerm = v == term;
                if (isTerm) { if (cur < 0) cur = i; }
                else if (cur >= 0) { Consider(cur, i - cur); cur = -1; }
            }
            if (cur >= 0) Consider(cur, last + 1 - cur);

            // Longest run wins, except one that runs to the far end of the
            // seam. The strip's trailing drag region reaches the window edge
            // and is filled from the same palette, so on a full strip it is
            // longer than any tab and would be taken for the selected one --
            // then every break is reported as misaligned against a band that
            // is not a tab. No tab ever reaches that edge; the footer is
            // always beyond them.
            void Consider(int start, int length) {
                if (start + length >= last - 2) return;
                if (length > tabLen) { tabLen = length; tabStart = start; }
            }
        }
        bool tabOnScreen = tabLen >= 12;

        if (gaps.Count == 0) {
            return tabOnScreen
                ? "seam is unbroken although the selected tab spans " + tabStart + ".." + (tabStart + tabLen) +
                  " -- the cover is not drawing"
                : null;
        }
        if (gaps.Count > 1) {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var gp in gaps) parts.Add("at " + gp[0] + " len " + gp[1]);
            return "seam broken in " + gaps.Count + " places (expected 1): " + string.Join(", ", parts);
        }
        int gapAt = gaps[0][0], gapLen = gaps[0][1];
        if (gapLen < 12) return "break is only " + gapLen + "px, too narrow to be a tab";
        if (gapLen > len * 0.9) return "break is " + gapLen + "px, nearly the whole seam";

        // And the break has to be where the tab is. A cover that drifts -- a
        // stale layout offset, a scrolled strip -- still leaves exactly one
        // break of a plausible width, just in the wrong place.
        if (tabOnScreen) {
            int slack = 6;
            if (Math.Abs(gapAt - tabStart) > slack || Math.Abs((gapAt + gapLen) - (tabStart + tabLen)) > slack)
                return "break at " + gapAt + ".." + (gapAt + gapLen) +
                       " does not line up with the selected tab at " + tabStart + ".." + (tabStart + tabLen);
        }
        return null;
    }
}
'@
[void][Seam]::SetProcessDpiAwarenessContext([IntPtr](-4))

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-ChildItem $OutDir -Filter 'fail-*.png' -ErrorAction SilentlyContinue | Remove-Item -Force

# ---- launch, isolated ------------------------------------------------------
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) "wintty-seam-$([guid]::NewGuid())"
$cfgDir = Join-Path $sandbox 'wintty'
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
$cfgFile = Join-Path $cfgDir 'config.wintty'

function Set-Layout([bool]$vertical) {
    # auto-reload-config lets the layout be switched by rewriting the file,
    # which needs no keyboard and so cannot be swallowed by a lost foreground.
    @"
auto-reload-config = true
windows-single-instance = false
vertical-tabs = $($vertical.ToString().ToLower())
vertical-tabs-pinned = true
"@ | Set-Content -Path $cfgFile -Encoding utf8
}
Set-Layout $false

$env:NO_COLOR = $null
$origXdg = $env:XDG_CONFIG_HOME
$env:XDG_CONFIG_HOME = $sandbox
try {
    $proc = Start-Process -FilePath $ExePath -PassThru `
        -RedirectStandardOutput (Join-Path $sandbox 'out.txt') `
        -RedirectStandardError (Join-Path $sandbox 'err.txt')
} finally {
    if ($null -ne $origXdg) { $env:XDG_CONFIG_HOME = $origXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
}
Write-Host "pid=$($proc.Id)  sandbox=$sandbox"

$h = [IntPtr]::Zero
$deadline = [DateTime]::UtcNow.AddSeconds(45)
while ([DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $proc.Refresh()
    if ($proc.HasExited) { Write-Host "EXITED code=$($proc.ExitCode)"; exit 2 }
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
        $r = New-Object Seam+RECT
        [void][Seam]::GetWindowRect($proc.MainWindowHandle, [ref]$r)
        if (($r.R - $r.L) -gt 400) { $h = $proc.MainWindowHandle; break }
    }
}
if ($h -eq [IntPtr]::Zero) { Write-Host 'NO WINDOW'; Stop-Process -Id $proc.Id -Force; exit 2 }

function Raise-Window {
    $fg = [Seam]::GetForegroundWindow()
    $other = [Seam]::GetWindowThreadProcessId($fg, [IntPtr]::Zero)
    $mine = [Seam]::GetCurrentThreadId()
    [void][Seam]::AttachThreadInput($mine, $other, $true)
    [void][Seam]::BringWindowToTop($h)
    [void][Seam]::SetForegroundWindow($h)
    [void][Seam]::AttachThreadInput($mine, $other, $false)
}
Start-Sleep -Milliseconds 9000

# Re-acquire after the settle. The first non-zero MainWindowHandle is the
# launch splash, which is the right size and then destroyed; latching it gives
# a handle whose rect reads 0x0 for the rest of the run, and every check then
# skips while the run still reports a pass.
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 20; $i++) {
    $proc.Refresh()
    if ($proc.HasExited) { Write-Host "EXITED code=$($proc.ExitCode)"; exit 2 }
    $cand = $proc.MainWindowHandle
    if ($cand -ne [IntPtr]::Zero) {
        $r = New-Object Seam+RECT
        [void][Seam]::GetWindowRect($cand, [ref]$r)
        if (($r.R - $r.L) -gt 400 -and ($r.B - $r.T) -gt 300) { $h = $cand; break }
    }
    Start-Sleep -Milliseconds 800
}
if ($h -eq [IntPtr]::Zero) { Write-Host 'NO MAIN WINDOW after settle'; Stop-Process -Id $proc.Id -Force; exit 2 }

[void][Seam]::ShowWindow($h, 9)
Raise-Window
Start-Sleep -Milliseconds 2500

# Moved to a known position but NOT resized. window-state.json lives outside
# the sandbox, so each run inherits wherever the last one left the window, and
# a run that starts it half off-screen captures desktop instead of chrome --
# which reads as "no seam line found" rather than as the harness having lost
# the window. Position alone raises no SizeChanged, so it cannot re-place the
# seam; a resize can, which is what previously repaired a broken initial
# placement before the first check and made every run pass. Iteration 0 below
# checks the seam as the app first drew it.
[void][Seam]::GetWindowRect($h, [ref]$r)
[void][Seam]::MoveWindow($h, 60, 60, $r.R - $r.L, $r.B - $r.T, $true)

function Send-Chord([byte]$vk) {
    # Clear stuck modifiers first: a dropped keyup from an earlier chord
    # silently breaks every chord after it.
    foreach ($m in 0x10,0x11,0x12) { [Seam]::keybd_event($m, 0, 2, [IntPtr]::Zero) }
    [Seam]::keybd_event(0x11, 0, 0, [IntPtr]::Zero)
    [Seam]::keybd_event($vk, 0, 0, [IntPtr]::Zero)
    [Seam]::keybd_event($vk, 0, 2, [IntPtr]::Zero)
    [Seam]::keybd_event(0x11, 0, 2, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 700
}

function Get-Shot {
    $r = New-Object Seam+RECT
    [void][Seam]::GetWindowRect($h, [ref]$r)
    $w = $r.R - $r.L; $ht = $r.B - $r.T
    if ($w -le 0 -or $ht -le 0) { return $null }
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $ht))
    $g.Dispose()
    return $bmp
}

# ---- the oracle ------------------------------------------------------------
function Test-Seam($bmp, [bool]$vertical) {
    $rect = New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $bytes = New-Object byte[] ($data.Stride * $bmp.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    } finally {
        $bmp.UnlockBits($data)
    }
    return [SeamOracle]::Check($bytes, $data.Stride, $bmp.Width, $bmp.Height, $vertical)
}
# ---- drive -----------------------------------------------------------------
$rand = [System.Random]::new($Seed)
$vertical = $false
$failures = 0
$checked = 0
$skipped = 0
$actions = @('newtab','newtab','newtab','nexttab','closetab','resize','move','layout')

for ($i = 0; $i -le $Iterations; $i++) {
    # Iteration 0 drives nothing: it checks the seam as the app first drew it,
    # which is the state a user actually sees on launch and the one a startup
    # placement bug lives in.
    $act = if ($i -eq 0) { 'launch' } else { $actions[$rand.Next(0, $actions.Length)] }
    switch ($act) {
        'newtab'   { Raise-Window; Send-Chord 0x54 }              # ctrl+t
        'closetab' { Raise-Window; Send-Chord 0x57 }              # ctrl+w
        'nexttab'  { Raise-Window; Send-Chord 0x09 }              # ctrl+tab
        'resize'   { [void][Seam]::MoveWindow($h, 100, 100, $rand.Next(560, 1500), $rand.Next(420, 900), $true); Start-Sleep -Milliseconds 900 }
        'move'     {
            $r = New-Object Seam+RECT; [void][Seam]::GetWindowRect($h, [ref]$r)
            [void][Seam]::MoveWindow($h, $rand.Next(0, 420), $rand.Next(0, 260), $r.R-$r.L, $r.B-$r.T, $true)
            Start-Sleep -Milliseconds 700
        }
        'layout'   { $vertical = -not $vertical; Set-Layout $vertical; Start-Sleep -Milliseconds 2200 }
    }

    $proc.Refresh()
    if ($proc.HasExited) { Write-Host "iter $i ($act): PROCESS EXITED code=$($proc.ExitCode)"; exit 2 }

    Start-Sleep -Milliseconds 500
    $bmp = Get-Shot
    if ($null -eq $bmp) { Write-Host "iter $i ($act): no window rect"; $skipped++; continue }
    $problem = Test-Seam $bmp $vertical
    if ($problem) {
        $failures++
        $path = Join-Path $OutDir ("fail-{0:D3}-{1}.png" -f $i, $act)
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "iter $i ($act, $(if($vertical){'vertical'}else{'horizontal'})): FAIL - $problem  -> $path"
    } else {
        $checked++
        Write-Host "iter $i ($act, $(if($vertical){'vertical'}else{'horizontal'})): ok"
    }
    $bmp.Dispose()
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "seed=$Seed iterations=$Iterations checked=$checked skipped=$skipped failures=$failures"

# A run that checked nothing is not a pass. Reporting one is how a broken
# harness gets mistaken for a working feature.
if ($failures -gt 0) { exit 1 }
if ($checked -lt [Math]::Ceiling(($Iterations + 1) / 2)) {
    Write-Host "harness checked only $checked of $Iterations iterations"
    exit 2
}
exit 0

