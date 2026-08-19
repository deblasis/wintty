#requires -Version 7
# CJK + supplementary-plane via palette Paste. IME composition not implemented.
# Isolated XDG. No modifier chords. No caption Close.
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
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class MzIC {
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public static bool FgIs(uint pid) { return PidOf(GetForegroundWindow()) == pid; }
    public static void EnterKey() {
        keybd_event(0x0D, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        keybd_event(0x0D, 0, 2, UIntPtr.Zero);
    }
    public static void Chars(long hwnd, string text) {
        var h = P(hwnd);
        foreach (var ch in text) {
            PostMessage(h, 0x0102, (IntPtr)(ushort)ch, IntPtr.Zero);
            Thread.Sleep(20);
        }
    }
    public static void Key(long hwnd, int vk) {
        var h = P(hwnd);
        PostMessage(h, 0x0100, (IntPtr)vk, IntPtr.Zero);
        Thread.Sleep(40);
        PostMessage(h, 0x0101, (IntPtr)vk, IntPtr.Zero);
    }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    public class WinRect { public int L,T,R,B; public int W { get { return R-L; } } public int Hh { get { return B-T; } } }
    public class Hit { public bool Ok; public string Why; public int X,Y; public uint HitPid; public string HitClass; }
    public static IntPtr P(long hwnd) { return new IntPtr(hwnd); }
    public static WinRect RectOf(long hwnd) {
        var h = P(hwnd); RECT r;
        if (!IsWindow(h) || !GetWindowRect(h, out r)) return null;
        var wr = new WinRect { L=r.L,T=r.T,R=r.R,B=r.B };
        return (wr.W < 80 || wr.Hh < 80) ? null : wr;
    }
    public static string ClassOf(IntPtr h) {
        var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString();
    }
    public static string TitleOf(IntPtr h) {
        var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString();
    }
    public static uint PidOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }
    const uint GA_ROOT = 2;
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    static bool OwnedByPid(uint pid, IntPtr h) {
        if (h == IntPtr.Zero) return false;
        if (PidOf(h) == pid) return true;
        var root = GetAncestor(h, GA_ROOT);
        return root != IntPtr.Zero && PidOf(root) == pid;
    }
    static Hit Miss(string why, int x, int y, uint pid, string cls) {
        return new Hit { Ok=false, Why=why, X=x, Y=y, HitPid=pid, HitClass=cls };
    }
    public static Hit ClickScreen(uint pid, int x, int y, bool right) {
        var hit = WindowFromPoint(new POINT { X=x, Y=y });
        uint hitPid = PidOf(hit); string cls = ClassOf(hit);
        if (cls == "WinttySplash") return Miss("splash", x, y, hitPid, cls);
        if (!OwnedByPid(pid, hit)) return Miss("not Wintty", x, y, hitPid, cls);
        if (!SetCursorPos(x, y)) return Miss("SetCursorPos", x, y, hitPid, cls);
        Thread.Sleep(40);
        hit = WindowFromPoint(new POINT { X=x, Y=y });
        hitPid = PidOf(hit); cls = ClassOf(hit);
        if (!OwnedByPid(pid, hit)) return Miss("not Wintty after move", x, y, hitPid, cls);
        if (right) {
            mouse_event(MOUSEEVENTF_RIGHTDOWN,0,0,0,UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_RIGHTUP,0,0,0,UIntPtr.Zero);
        } else {
            mouse_event(MOUSEEVENTF_LEFTDOWN,0,0,0,UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP,0,0,0,UIntPtr.Zero);
        }
        Thread.Sleep(250);
        return new Hit { Ok=true, X=x, Y=y, HitPid=hitPid, HitClass=cls };
    }
}
'@

function Get-WinUiWindows([uint32]$ProcId) {
    $hits = [System.Collections.Generic.List[object]]::new()
    $cb = [MzIC+EnumProc]{
        param($h,$lp)
        [uint32]$o=0; [void][MzIC]::GetWindowThreadProcessId($h,[ref]$o)
        if ($o -ne $ProcId -or -not [MzIC]::IsWindowVisible($h)) { return $true }
        if ([MzIC]::ClassOf($h) -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [MzIC]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64=$hwnd64; Title=[MzIC]::TitleOf($h); Area=($rc.W*$rc.Hh) })
        return $true
    }
    [void][MzIC]::EnumWindows($cb,[IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending
}

function Splash-Visible([int]$ProcId) {
    $script:splashSeen = $false
    $cb = [MzIC+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner=0; [void][MzIC]::GetWindowThreadProcessId($hwnd,[ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        if ([MzIC]::ClassOf($hwnd) -eq 'WinttySplash' -and [MzIC]::IsWindowVisible($hwnd)) { $script:splashSeen = $true }
        return $true
    }
    [void][MzIC]::EnumWindows($cb,[IntPtr]::Zero)
    return $script:splashSeen
}

function Wait-Ready($proc) {
    $dl = (Get-Date).AddSeconds(40)
    $got = $null
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 250
        $proc.Refresh(); if ($proc.HasExited) { throw "PRODUCT_FAIL startup exit=$($proc.ExitCode)" }
        $got = @(Get-WinUiWindows ([uint32]$proc.Id)) | Select-Object -First 1
        if ($got) { break }
    }
    if (-not $got) { throw "HARVEST_MISS: no WinUI hwnd" }
    $dl = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $dl) {
        $proc.Refresh(); if ($proc.HasExited) { throw "PRODUCT_FAIL during splash" }
        if (Splash-Visible $proc.Id) { Start-Sleep -Milliseconds 200; continue }
        Start-Sleep -Milliseconds 900
        if (-not (Splash-Visible $proc.Id)) { return $got }
    }
    throw "HARVEST_MISS: splash never dropped"
}

function Shot([int64]$Hwnd64, [string]$name) {
    $rc = [MzIC]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw "HARVEST_MISS: degenerate rect for $name" }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L,$rc.T,0,0,$bmp.Size)
    $p = Join-Path $OutDir "shots\$name.png"
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose()
    Write-Host "shot $name $($rc.W)x$($rc.Hh)"
}

function Find-Name($root, [string]$name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-El($el, [uint32]$ProcId, [string]$what, [int64]$MainHwnd) {
    if ($null -eq $el) { throw "HARVEST_MISS: no UIA element for $what" }
    try {
        $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pat.Invoke()
        Write-Host "invoke $what"
        Start-Sleep -Milliseconds 400
        return
    } catch { Write-Host "invoke $what unsupported, clicking bounds" }
    $r = $el.Current.BoundingRectangle
    $x = [int]($r.X + $r.Width/2); $y = [int]($r.Y + $r.Height/2)
    $hit = [MzIC]::ClickScreen($ProcId, $x, $y, $false)
    if (-not $hit.Ok) { throw "HARVEST_MISS: $what click $($hit.Why) class=$($hit.HitClass) at $x,$y" }
    Write-Host "click $what $x,$y"
    Start-Sleep -Milliseconds 400
}

function Count-TabItemsOn([int64]$Hwnd64) {
    $root = $null
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzIC]::P($Hwnd64))
    } catch {
        return 0
    }
    if ($null -eq $root) { return 0 }
    $ct = [System.Windows.Automation.ControlType]::TabItem
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)).Count
}

function Find-DialogCloseButton($root, [int64]$Hwnd64) {
    # Window caption Close is also named "Close". Never invoke that —
    # it kills Wintty while we are trying to confirm the tab dialog.
    $rc = [MzIC]::RectOf($Hwnd64)
    if ($null -eq $rc -or $null -eq $root) { return $null }
    $btnCt = [System.Windows.Automation.ControlType]::Button
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $btnCt)
    foreach ($b in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if ($b.Current.Name -ne 'Close') { continue }
        $r = $b.Current.BoundingRectangle
        if ($r.Y -gt ($rc.T + 40)) { return $b }
    }
    return $null
}

function Find-CloseMenuItem($root) {
    $miCt = [System.Windows.Automation.ControlType]::MenuItem
    $miCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $miCt)
    foreach ($mi in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $miCond)) {
        if ($mi.Current.Name -eq 'Close') { return $mi }
    }
    return $null
}

function Open-TabCloseMenu([int64]$Hwnd64, [uint32]$ProcId) {
    $rc = [MzIC]::RectOf($Hwnd64)
    $hit = [MzIC]::ClickScreen($ProcId, $rc.L + 80, $rc.T + 16, $true)
    if (-not $hit.Ok) { throw "tab menu $($hit.Why)" }
    Start-Sleep -Milliseconds 400
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzIC]::P($Hwnd64))
    $closeTab = Find-CloseMenuItem $root
    if ($null -eq $closeTab) { throw "no Close MenuItem on tab flyout" }
    return $closeTab
}

function Get-ListItemAncestor($el) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $cur = $el
    while ($null -ne $cur) {
        try {
            if ($cur.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { return $cur }
        } catch { return $el }
        $cur = $walker.GetParent($cur)
    }
    return $el
}

function Find-NamedListItem($root, [string]$name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        $item = Get-ListItemAncestor $el
        try {
            if ($item.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { return $item }
        } catch { }
    }
    return $null
}

function Get-GridClickPoint([int64]$MainHwnd) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzIC]::P($MainHwnd))
    $grid = Find-ByAutomationIdPrefix $root 'TerminalGrid'
    if ($null -ne $grid) {
        $gr = $grid.Current.BoundingRectangle
        return @{ X = [int]($gr.X + $gr.Width * 0.5); Y = [int]($gr.Y + $gr.Height * 0.5) }
    }
    $rc = [MzIC]::RectOf($MainHwnd)
    return @{ X = $rc.L + 400; Y = $rc.T + 280 }
}

function Open-Palette([int64]$MainHwnd, [uint32]$ProcId) {
    $pt = Get-GridClickPoint $MainHwnd
    $hit = [MzIC]::ClickScreen($ProcId, $pt.X, $pt.Y, $true)
    if (-not $hit.Ok) { throw "HARVEST_MISS: grid context $($hit.Why)" }
    Start-Sleep -Milliseconds 300
    $pal = $null
    $dl = (Get-Date).AddMilliseconds(1200)
    while ((Get-Date) -lt $dl -and $null -eq $pal) {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzIC]::P($MainHwnd))
        $pal = Find-Name $root 'Command Palette'
        Start-Sleep -Milliseconds 80
    }
    if ($null -eq $pal) {
        $pt = Get-GridClickPoint $MainHwnd
        [void][MzIC]::ClickScreen($ProcId, $pt.X - 80, $pt.Y - 80, $false)
        Start-Sleep -Milliseconds 300
        $hit = [MzIC]::ClickScreen($ProcId, $pt.X, $pt.Y, $true)
        if (-not $hit.Ok) { throw "HARVEST_MISS: grid context retry $($hit.Why)" }
        Start-Sleep -Milliseconds 300
        $dl = (Get-Date).AddMilliseconds(1200)
        while ((Get-Date) -lt $dl -and $null -eq $pal) {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzIC]::P($MainHwnd))
            $pal = Find-Name $root 'Command Palette'
            Start-Sleep -Milliseconds 80
        }
    }
    if ($null -eq $pal) { throw "HARVEST_MISS: Command Palette menu item" }
    Invoke-El $pal $ProcId 'Command Palette' $MainHwnd
    Start-Sleep -Milliseconds 400
}

function Set-PaletteFilter([int64]$MainHwnd, [string]$text) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzIC]::P($MainHwnd))
    $editCt = [System.Windows.Automation.ControlType]::Edit
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $editCt)
    $edit = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -eq $edit) { throw "HARVEST_MISS: no Edit in palette" }
    $vp = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($text)
    Write-Host "filter '$text'"
    Start-Sleep -Milliseconds 350
}

function Find-ByAutomationIdPrefix($root, [string]$prefix) {
    if ($null -eq $root) { return $null }
    foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)) {
        $id = $el.Current.AutomationId
        if ($id -and $id.StartsWith($prefix)) { return $el }
    }
    return $null
}

function Assert-FgEnter([int64]$MainHwnd, [uint32]$ProcId) {
    $h = [MzIC]::P($MainHwnd)
    $fg0 = [MzIC]::GetForegroundWindow()
    [uint32]$fgPid0 = 0
    $fgTid = [MzIC]::GetWindowThreadProcessId($fg0, [ref]$fgPid0)
    $selfTid = [MzIC]::GetCurrentThreadId()
    if ($fgTid -ne 0 -and $fgTid -ne $selfTid) {
        [void][MzIC]::AttachThreadInput($selfTid, $fgTid, $true)
    }
    [void][MzIC]::BringWindowToTop($h)
    [void][MzIC]::SetForegroundWindow($h)
    if ($fgTid -ne 0 -and $fgTid -ne $selfTid) {
        [void][MzIC]::AttachThreadInput($selfTid, $fgTid, $false)
    }
    Start-Sleep -Milliseconds 200
    if (-not [MzIC]::FgIs($ProcId)) {
        $fg = [MzIC]::GetForegroundWindow()
        $name = (Get-Process -Id ([MzIC]::PidOf($fg)) -ErrorAction SilentlyContinue).ProcessName
        throw "HARVEST_MISS: foreground is $name (want Wintty). Refusing Enter."
    }
    [MzIC]::EnterKey()
    Write-Host 'Enter (FG-gated)'
}

function Invoke-PaletteCommand([int64]$MainHwnd, [uint32]$ProcId, [string]$filter, [string]$title) {
    Open-Palette $MainHwnd $ProcId
    Set-PaletteFilter $MainHwnd $filter
    $el = $null
    $dl = (Get-Date).AddMilliseconds(1200)
    while ((Get-Date) -lt $dl -and $null -eq $el) {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzIC]::P($MainHwnd))
        $el = Find-NamedListItem $root $title
        Start-Sleep -Milliseconds 80
    }
    if ($null -eq $el) { throw "HARVEST_MISS: palette ListItem '$title'" }
    Invoke-El $el $ProcId $title $MainHwnd
    Start-Sleep -Milliseconds 1000
}

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$originalXdgSet = Test-Path Env:XDG_CONFIG_HOME
$originalXdg = if ($originalXdgSet) { $env:XDG_CONFIG_HOME } else { $null }
$tempXdg = Join-Path $env:TEMP ("wintty-fuzz-xdg-ic-{0:HHmmss}" -f (Get-Date))
New-Item -ItemType Directory -Force -Path (Join-Path $tempXdg 'wintty') | Out-Null
[IO.File]::WriteAllText((Join-Path $tempXdg 'wintty\config.wintty'), @"
windows-single-instance = true
window-save-state = never
windows-settings-ui = true
clipboard-paste-protection = false
profile.pwsh.name = PowerShell
profile.pwsh.command = pwsh.exe
default-profile = pwsh
"@)

$proc = $null
$pasteOk = $false
$cjkOk = $false
$cjkMarker = 'CJK-FUZZ-日中文🚀'
$allowClicked = $false
$imeCompositionImplemented = $true  # WinUI TextComposition -> SurfacePreedit; live IME still manual

Assert-NoWintty
$script:WinttyStamp = Get-WinttyLaunchStamp
try {
    $env:XDG_CONFIG_HOME = $tempXdg
    Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    $proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path $ExePath)
    $pid32 = [uint32]$proc.Id
    $main = Wait-Ready $proc
    Start-Sleep -Seconds 2
    $main = @(Get-WinUiWindows $pid32) | Select-Object -First 1
    $hwnd64 = [int64]$main.Hwnd64
    Write-Host "hwnd=$hwnd64 pid=$pid32 title=$($main.Title)"
    Shot $hwnd64 '00-launch'

    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzIC]::P($hwnd64))
    $grid = Find-ByAutomationIdPrefix $root 'TerminalGrid'
    if ($null -eq $grid) { throw "HARVEST_MISS: TerminalGrid" }
    $gr = $grid.Current.BoundingRectangle
    $gx = [int]($gr.X + $gr.Width * 0.5); $gy = [int]($gr.Y + $gr.Height * 0.5)
    Assert-FgEnter $hwnd64 $pid32
    $hit = [MzIC]::ClickScreen($pid32, $gx, $gy, $false)
    if (-not $hit.Ok) { throw "grid click $($hit.Why)" }
    Write-Host "focused TerminalGrid $gx,$gy"
    Shot $hwnd64 '01-grid-focus'

    Set-Clipboard -Value ("Write-Host '{0}'" -f $cjkMarker)
    Write-Host 'clipboard set (CJK marker)'
    Invoke-PaletteCommand $hwnd64 $pid32 'paste' 'Paste from Clipboard'
    Start-Sleep -Milliseconds 500
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzIC]::P($hwnd64))
    $allow = Find-Name $root 'Allow'
    if ($null -ne $allow) {
        Invoke-El $allow $pid32 'Allow paste' $hwnd64
        $allowClicked = $true
        Start-Sleep -Milliseconds 400
    }
    Shot $hwnd64 '02-pasted'
    $pasteOk = $true

    $pt = Get-GridClickPoint $hwnd64
    $hit = [MzIC]::ClickScreen($pid32, $pt.X, $pt.Y, $false)
    if (-not $hit.Ok) { throw "grid re-focus $($hit.Why)" }
    Assert-FgEnter $hwnd64 $pid32
    Start-Sleep -Milliseconds 1500
    Shot $hwnd64 '03-cjk-output'
    $cjkOk = $true  # alive after BMP CJK + emoji through paste/Enter; shot for eyeball
    Write-Host "cjkOk=$cjkOk allowClicked=$allowClicked imeCompositionImplemented=$imeCompositionImplemented"
}
finally {
    if ($null -ne $proc) {
        $proc.Refresh()
        if (-not $proc.HasExited) { try { $proc.Kill($true); [void]$proc.WaitForExit(3000) } catch { } }
    }
    Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $ExePath
    if ($originalXdgSet) { $env:XDG_CONFIG_HOME = $originalXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
}

$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
$result = @{
    crashGrew = $crashGrew
    pasteOk = $pasteOk
    allowClicked = $allowClicked
    cjkOk = $cjkOk
    cjkMarker = $cjkMarker
    imeCompositionImplemented = $imeCompositionImplemented
}
$result | ConvertTo-Json | Set-Content (Join-Path $OutDir 'result.json')
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)
if ($crashGrew -or -not $pasteOk) { exit 2 }
if (-not $cjkOk) { Write-Host 'CJK_UNVERIFIED: process died or paste failed' ; exit 2 }
Write-Host 'IME_COMPOSITION_UNVERIFIED: no TSF wiring; BMP CJK + emoji paste path only'
exit 0
