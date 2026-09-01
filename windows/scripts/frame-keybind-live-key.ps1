#requires -Version 7
<#
    The framework hop for issue #868, observed rather than argued.

    frame-keybind-check.ps1 drives the window's frame-chord router directly
    over the seam. That proves the router and its two gates, but not the hop
    ABOVE the router: that a real key press, with focus on the frame, is
    delivered by WinUI as a KeyDown on Window.Content at all. Only a key the
    harness did not hand to the router itself can show that.

    How the key is delivered, and why:

      * By default, PostMessage to the app's own InputSiteWindowClass HWND.
        That is window-targeted -- it steals no focus, touches no other
        application, and leaves the owner's keyboard alone. In WinUI 3 the
        top-level WinUIDesktopWin32WindowClass does not receive keyboard
        input; the lifted island's input site does (the same fact
        Hosting/SysCharBeepSuppressor.cs subclasses for), so that is where
        the message goes.
      * A posted message carries no modifier state -- GetKeyState is
        maintained by the system for input it queued itself, not for
        messages another process posts -- so the chord under test is a bare
        function key. The config binds f9=new_tab, and the frame's shape
        rule admits an unmodified function key precisely because nothing on
        the frame and no text entry can claim one. The action is then
        visible in the tab count.
      * -WithSyntheticChord adds two legs that press a real Ctrl+Shift+,
        through SendInput, for the modifier-carrying chord the report names.
        Off by default: it is six key events into whatever is foreground, so
        do not run it while someone is at the keyboard.

    One click IS synthesized in the caption and empty-chrome legs, because
    only a real click reproduces "the user clicked there". It is a single
    left click on this app's own window, after raising it.

    The oracle is twofold, both halves from the product: routedKeyDowns (a
    counter the window bumps on every KeyDown reaching its content, so the
    hop is visible even when nothing acts on the key) and the manager state
    (so the ACTION is visible too).

    Legs:
      1. caption:      focus a strip row, click the bare title-bar caption
         -- the surface the report names -- then post F9. Focus must survive
         the click and a tab must be born.
      2. empty-chrome: click empty strip chrome below the rows, then post
         F9. The strip takes focus and a tab must be born.
      3. strip-row:    focus a strip row over the seam, then post F9.
      4. pane-typing:  with the pane focused, post a bare letter. It must
         reach the window content (the counter moves, so the frame handler
         genuinely saw it) and change nothing -- the live form of the
         greedy-accelerator guard.
      5. caption-chord / strip-chord (-WithSyntheticChord only): a real
         Ctrl+Shift+, must flip the layout from the caption and from a
         focused strip row.

    Exits 0 on pass, 2 on a product finding, 1 on a harness failure.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    [switch]$WithSyntheticChord
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

// Key delivery for the hop proof. PostKey is the default path and is
// window-targeted; Click and CtrlShift are the two places that have to be
// real OS input, and both are kept to a single gesture.
public static class LiveKey {
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {
        public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT {
        public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION {
        [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }

    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyW(uint c, uint t);

    const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const ushort VK_CONTROL = 0x11, VK_SHIFT = 0x10;

    static string ClassOf(IntPtr h) {
        var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString();
    }

    // The island's input site is a grandchild, not a direct child, so the
    // walk recurses.
    public static IntPtr FindInputSite(IntPtr top) {
        IntPtr found = IntPtr.Zero;
        Walk(top, ref found);
        return found;
    }
    static void Walk(IntPtr parent, ref IntPtr found) {
        var kids = new List<IntPtr>();
        EnumChildWindows(parent, (h, lp) => { kids.Add(h); return true; }, IntPtr.Zero);
        foreach (var k in kids) {
            if (found != IntPtr.Zero) return;
            if (ClassOf(k) == "InputSiteWindowClass") { found = k; return; }
        }
        foreach (var k in kids) {
            if (found != IntPtr.Zero) return;
            Walk(k, ref found);
        }
    }

    public static void PostKey(IntPtr h, ushort vk) {
        uint sc = MapVirtualKeyW(vk, 0);
        IntPtr down = (IntPtr)(long)(1u | (sc << 16));
        IntPtr up   = (IntPtr)(long)(1u | (sc << 16) | 0xC0000000u);
        PostMessage(h, WM_KEYDOWN, (IntPtr)vk, down);
        PostMessage(h, WM_KEYUP,   (IntPtr)vk, up);
    }

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        var a = new INPUT[2];
        a[0].type = INPUT_MOUSE; a[0].u.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
        a[1].type = INPUT_MOUSE; a[1].u.mi.dwFlags = MOUSEEVENTF_LEFTUP;
        SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
    }

    static INPUT Key(ushort vk, bool up) {
        var i = new INPUT(); i.type = INPUT_KEYBOARD;
        i.u.ki.wVk = vk; i.u.ki.wScan = (ushort)MapVirtualKeyW(vk, 0);
        i.u.ki.dwFlags = up ? KEYEVENTF_KEYUP : 0;
        return i;
    }

    public static void CtrlShift(ushort vk) {
        var a = new INPUT[6];
        a[0] = Key(VK_CONTROL, false); a[1] = Key(VK_SHIFT, false);
        a[2] = Key(vk, false);         a[3] = Key(vk, true);
        a[4] = Key(VK_SHIFT, true);    a[5] = Key(VK_CONTROL, true);
        SendInput(6, a, Marshal.SizeOf(typeof(INPUT)));
    }
}
'@ -ErrorAction SilentlyContinue

$VK_F9 = 0x78
$VK_A = 0x41
$VK_COMMA = 0xBC

# f9=new_tab is the fixture: an unmodified key, bound to something the seam
# state shows plainly, so a posted message (which carries no modifiers) can
# still exercise a real binding.
$Config = @'
windows-single-instance = true
window-save-state = never
vertical-tabs = true
window-theme = wintty
vertical-tabs-hover-expand = false
keybind = f9=new_tab
'@

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$script:Legs = [System.Collections.Generic.List[object]]::new()

function Assert-Hop($Before, $After, [string]$What) {
    $delta = $After.routedKeyDowns - $Before.routedKeyDowns
    if ($delta -lt 1) {
        throw "PRODUCT_FAIL: $What never reached the window content (routedKeyDowns did not move)"
    }
    return $delta
}

function Assert-NewTab($Before, $After, [string]$What) {
    if ($After.state.tabs.Count -ne $Before.state.tabs.Count + 1) {
        throw ("PRODUCT_FAIL: {0}: tabs went {1} -> {2}, wanted one more -- the key reached the window but fired nothing" -f
            $What, $Before.state.tabs.Count, $After.state.tabs.Count)
    }
}

function Assert-Toggled($Before, $After, [string]$What) {
    if ($Before.state.vertical -eq $After.state.vertical) {
        throw ("PRODUCT_FAIL: {0}: the layout is still {1} -- the chord did not fire" -f
            $What, $(if ($After.state.vertical) { 'vertical' } else { 'horizontal' }))
    }
}

function Invoke-Leg([string]$Name, [scriptblock]$Body) {
    $crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }
    $s = $null
    $entry = [ordered]@{ name = $Name; ok = $false; class = ''; error = '' }
    Write-Host "=== leg $Name ==="
    try {
        Assert-NoWintty -Context "The live-key leg '$Name'"
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $Config
        [void](Invoke-SeamCommand $s @{ op = 'seed-tabs'; count = 2; titles = @('live-1', 'live-2') })
        $rect = [SeamWin]::RectOf($s.Hwnd64)
        if ($null -eq $rect) { throw 'HARVEST_MISS: the window has no usable rect' }
        $site = [LiveKey]::FindInputSite([IntPtr]$s.Hwnd64)
        if ($site -eq [IntPtr]::Zero) { throw 'HARVEST_MISS: no InputSiteWindowClass under the window' }
        [void][LiveKey]::SetForegroundWindow([IntPtr]$s.Hwnd64)
        Start-Sleep -Milliseconds 400
        & $Body $s $rect $site
        if ($s.Proc.HasExited) {
            throw ("APP_EXIT: the app exited during '{0}' (code {1})" -f $Name, $s.Proc.ExitCode)
        }
        $entry.ok = $true
        Write-Host "PASS $Name" -ForegroundColor Green
    } catch {
        $msg = "$($_.Exception.Message)"
        $entry.error = $msg
        $entry.class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
        Write-Host "FAIL $Name [$($entry.class)]: $msg" -ForegroundColor Red
    } finally {
        if ($null -ne $s) { Stop-SeamSession $s }
    }
    if ((Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)) {
        $entry.ok = $false
        $entry.class = 'product'
        $entry.error = ($entry.error + ' crash.log grew during the leg').Trim()
        Write-Host "FAIL $Name [product]: crash.log grew" -ForegroundColor Red
    }
    $script:Legs.Add($entry)
}

# ---- legs ------------------------------------------------------------------

Invoke-Leg 'caption' {
    param($s, $rect, $site)
    [void](Invoke-SeamCommand $s @{ op = 'focus'; target = 'frame' })
    # The bare caption, mid-window: clear of the strip lane on the left and
    # of the OS caption buttons on the right.
    [LiveKey]::Click(($rect.L + [int]($rect.W / 2)), ($rect.T + 12))
    Start-Sleep -Milliseconds 500
    $before = Invoke-SeamCommand $s @{ op = 'probe' }
    if ($before.focus -ne 'frame') {
        throw "PRODUCT_FAIL: a click on the bare caption moved focus to '$($before.focus)'"
    }
    [LiveKey]::PostKey($site, $VK_F9)
    Start-Sleep -Milliseconds 1200
    $after = Invoke-SeamCommand $s @{ op = 'probe' }
    Write-Host ("  routed delta = {0}" -f (Assert-Hop $before $after 'the caption key'))
    Assert-NewTab $before $after 'F9 on the bare caption'
}

Invoke-Leg 'empty-chrome' {
    param($s, $rect, $site)
    # Empty strip chrome, below the two seeded rows, inside the strip lane.
    [LiveKey]::Click(($rect.L + 24), ($rect.T + 420))
    Start-Sleep -Milliseconds 500
    $before = Invoke-SeamCommand $s @{ op = 'probe' }
    if ($before.focus -ne 'frame') {
        throw "PRODUCT_FAIL: a click on empty strip chrome left focus at '$($before.focus)'"
    }
    [LiveKey]::PostKey($site, $VK_F9)
    Start-Sleep -Milliseconds 1200
    $after = Invoke-SeamCommand $s @{ op = 'probe' }
    Write-Host ("  routed delta = {0}" -f (Assert-Hop $before $after 'the empty-chrome key'))
    Assert-NewTab $before $after 'F9 on empty strip chrome'
}

Invoke-Leg 'strip-row' {
    param($s, $rect, $site)
    $before = Invoke-SeamCommand $s @{ op = 'focus'; target = 'frame' }
    [LiveKey]::PostKey($site, $VK_F9)
    Start-Sleep -Milliseconds 1200
    $after = Invoke-SeamCommand $s @{ op = 'probe' }
    Write-Host ("  routed delta = {0}" -f (Assert-Hop $before $after 'the strip-row key'))
    Assert-NewTab $before $after 'F9 on a focused strip row'
}

Invoke-Leg 'pane-typing' {
    param($s, $rect, $site)
    $before = Invoke-SeamCommand $s @{ op = 'focus'; target = 'pane' }
    [LiveKey]::PostKey($site, $VK_A)
    Start-Sleep -Milliseconds 900
    $after = Invoke-SeamCommand $s @{ op = 'probe' }
    # The counter moving is the point: the letter travelled the whole routed
    # chain, the frame handler included, and was still let go.
    Write-Host ("  routed delta = {0}" -f (Assert-Hop $before $after 'a bare letter typed into the pane'))
    if ($before.state.vertical -ne $after.state.vertical) {
        throw 'PRODUCT_FAIL: typing a letter into the pane changed the layout'
    }
    if ($before.state.tabs.Count -ne $after.state.tabs.Count) {
        throw 'PRODUCT_FAIL: typing a letter into the pane changed the tab count'
    }
    if ($after.focus -ne 'pane') {
        throw "PRODUCT_FAIL: typing a letter moved focus out of the pane to '$($after.focus)'"
    }
}

Invoke-Leg 'pane-fires-once' {
    param($s, $rect, $site)
    # F9 IS bound, and with the pane focused the pane's own key path owns
    # it. Exactly one tab must appear: two would mean the pane path and the
    # frame router both answered the same press, which is the
    # double-dispatch that got KeyboardAccelerators removed in issue #165.
    $before = Invoke-SeamCommand $s @{ op = 'focus'; target = 'pane' }
    [LiveKey]::PostKey($site, $VK_F9)
    Start-Sleep -Milliseconds 1200
    $after = Invoke-SeamCommand $s @{ op = 'probe' }
    $grew = $after.state.tabs.Count - $before.state.tabs.Count
    if ($grew -ne 1) {
        throw ("PRODUCT_FAIL: F9 with the pane focused made {0} tab(s), wanted exactly 1" -f $grew)
    }
}

if ($WithSyntheticChord) {
    Invoke-Leg 'caption-chord' {
        param($s, $rect, $site)
        [void](Invoke-SeamCommand $s @{ op = 'focus'; target = 'frame' })
        [LiveKey]::Click(($rect.L + [int]($rect.W / 2)), ($rect.T + 12))
        Start-Sleep -Milliseconds 500
        $before = Invoke-SeamCommand $s @{ op = 'probe' }
        if ($before.focus -ne 'frame') {
            throw "PRODUCT_FAIL: a click on the bare caption moved focus to '$($before.focus)'"
        }
        [LiveKey]::CtrlShift($VK_COMMA)
        Start-Sleep -Milliseconds 1200
        $after = Invoke-SeamCommand $s @{ op = 'probe' }
        Write-Host ("  routed delta = {0}" -f (Assert-Hop $before $after 'the caption chord'))
        Assert-Toggled $before $after 'Ctrl+Shift+, on the bare caption'
    }

    Invoke-Leg 'strip-chord' {
        param($s, $rect, $site)
        $before = Invoke-SeamCommand $s @{ op = 'focus'; target = 'frame' }
        [LiveKey]::CtrlShift($VK_COMMA)
        Start-Sleep -Milliseconds 1200
        $after = Invoke-SeamCommand $s @{ op = 'probe' }
        Write-Host ("  routed delta = {0}" -f (Assert-Hop $before $after 'the strip-row chord'))
        Assert-Toggled $before $after 'Ctrl+Shift+, on a focused strip row'
    }
}

# ---- verdict ---------------------------------------------------------------

$result = [ordered]@{
    actuation = if ($WithSyntheticChord) {
        'PostMessage to the app InputSite, plus one click per leg and a synthesized Ctrl+Shift+,'
    } else {
        'PostMessage to the app InputSite, plus one click per leg; no synthesized keyboard input'
    }
    legs = $script:Legs
}
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

Write-Host ''
Write-Host 'leg                           verdict'
Write-Host '----------------------------  -------'
foreach ($leg in $script:Legs) {
    $verdict = if ($leg.ok) { 'PASS' } else { "FAIL ($($leg.class))" }
    Write-Host ("{0,-29} {1}" -f $leg.name, $verdict)
}

$product = @($script:Legs | Where-Object { -not $_.ok -and $_.class -eq 'product' })
$harness = @($script:Legs | Where-Object { -not $_.ok -and $_.class -eq 'harness' })
if ($product.Count -gt 0) { exit 2 }
if ($harness.Count -gt 0) { exit 1 }
exit 0
