#requires -Version 7
# Palette Open Config with windows-settings-ui=true (isolated XDG).
# UIA scoped to Wintty hwnds only. No desktop-root walk. No modifier chords.
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots') | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class MzS {
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
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
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
    static Hit Miss(string why, int x, int y, uint pid, string cls) {
        return new Hit { Ok=false, Why=why, X=x, Y=y, HitPid=pid, HitClass=cls };
    }
    public static Hit ClickScreen(uint pid, int x, int y, bool right) {
        var hit = WindowFromPoint(new POINT { X=x, Y=y });
        uint hitPid = PidOf(hit); string cls = ClassOf(hit);
        if (cls == "WinttySplash") return Miss("splash", x, y, hitPid, cls);
        if (hitPid != pid) return Miss("not Wintty", x, y, hitPid, cls);
        if (!SetCursorPos(x, y)) return Miss("SetCursorPos", x, y, hitPid, cls);
        Thread.Sleep(40);
        hit = WindowFromPoint(new POINT { X=x, Y=y });
        hitPid = PidOf(hit); cls = ClassOf(hit);
        if (hitPid != pid) return Miss("not Wintty after move", x, y, hitPid, cls);
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
    $cb = [MzS+EnumProc]{
        param($h,$lp)
        [uint32]$o=0; [void][MzS]::GetWindowThreadProcessId($h,[ref]$o)
        if ($o -ne $ProcId -or -not [MzS]::IsWindowVisible($h)) { return $true }
        if ([MzS]::ClassOf($h) -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [MzS]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64=$hwnd64; Title=[MzS]::TitleOf($h); Area=($rc.W*$rc.Hh) })
        return $true
    }
    [void][MzS]::EnumWindows($cb,[IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending
}

function Splash-Visible([int]$ProcId) {
    $script:splashSeen = $false
    $cb = [MzS+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner=0; [void][MzS]::GetWindowThreadProcessId($hwnd,[ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        if ([MzS]::ClassOf($hwnd) -eq 'WinttySplash' -and [MzS]::IsWindowVisible($hwnd)) { $script:splashSeen = $true }
        return $true
    }
    [void][MzS]::EnumWindows($cb,[IntPtr]::Zero)
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
    $rc = [MzS]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw "HARVEST_MISS: degenerate rect for $name" }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L,$rc.T,0,0,$bmp.Size)
    $p = Join-Path $OutDir "shots\$name.png"
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose()
    Write-Host "shot $name $($rc.W)x$($rc.Hh) title=$([MzS]::TitleOf([MzS]::P($Hwnd64)))"
}

function Shot-Pid([uint32]$ProcId, [string]$prefix) {
    $i = 0
    foreach ($w in @(Get-WinUiWindows $ProcId)) {
        $safe = ($w.Title -replace '[^A-Za-z0-9]+','-').Trim('-')
        if (-not $safe) { $safe = 'untitled' }
        Shot $w.Hwnd64 ("{0}-{1}-{2}" -f $prefix, $i, $safe)
        $i++
    }
    Write-Host "pid windows: $i"
}

function Find-Name($root, [string]$name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
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

function Invoke-El($el, [uint32]$ProcId, [string]$what) {
    if ($null -eq $el) { throw "HARVEST_MISS: no UIA element for $what" }
    try {
        $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pat.Invoke()
        Write-Host "invoke $what"
        Start-Sleep -Milliseconds 400
        return
    } catch { Write-Host "invoke $what unsupported, clicking bounds" }
    $r = $el.Current.BoundingRectangle
    if ($r.Width -lt 4 -or $r.Height -lt 4) { throw "HARVEST_MISS: empty bounds for $what" }
    $x = [int]($r.X + $r.Width/2); $y = [int]($r.Y + $r.Height/2)
    $hit = [MzS]::ClickScreen($ProcId, $x, $y, $false)
    if (-not $hit.Ok) { throw "HARVEST_MISS: $what click $($hit.Why) class=$($hit.HitClass) at $x,$y" }
    Write-Host "click $what $x,$y"
    Start-Sleep -Milliseconds 400
}

function Select-Nav($root, [uint32]$ProcId, [string]$name) {
    $el = Find-Name $root $name
    if ($null -eq $el) { throw "HARVEST_MISS: nav '$name'" }
    try {
        $pat = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $pat.Select()
        Write-Host "select $name"
        Start-Sleep -Milliseconds 700
        return
    } catch { Write-Host "select $name unsupported" }
    Invoke-El $el $ProcId $name
    Start-Sleep -Milliseconds 300
}

function Open-Palette([int64]$MainHwnd, [uint32]$ProcId) {
    $rc = [MzS]::RectOf($MainHwnd)
    $hit = [MzS]::ClickScreen($ProcId, $rc.L + 400, $rc.T + 280, $true)
    if (-not $hit.Ok) { throw "HARVEST_MISS: grid context $($hit.Why) class=$($hit.HitClass)" }
    Start-Sleep -Milliseconds 300
    $pal = $null
    $dl = (Get-Date).AddMilliseconds(1200)
    while ((Get-Date) -lt $dl -and $null -eq $pal) {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzS]::P($MainHwnd))
        $pal = Find-Name $root 'Command Palette'
        Start-Sleep -Milliseconds 80
    }
    if ($null -eq $pal) { throw "HARVEST_MISS: Command Palette menu item not under hwnd" }
    Invoke-El $pal $ProcId 'Command Palette'
    Start-Sleep -Milliseconds 400
}

function Set-PaletteFilter([int64]$MainHwnd, [string]$text) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzS]::P($MainHwnd))
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

function Invoke-PaletteCommand([int64]$MainHwnd, [uint32]$ProcId, [string]$filter, [string]$title) {
    Open-Palette $MainHwnd $ProcId
    Set-PaletteFilter $MainHwnd $filter
    $el = $null
    $dl = (Get-Date).AddMilliseconds(1200)
    while ((Get-Date) -lt $dl -and $null -eq $el) {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzS]::P($MainHwnd))
        $el = Find-Name $root $title
        Start-Sleep -Milliseconds 80
    }
    if ($null -eq $el) { throw "HARVEST_MISS: palette item '$title' not under hwnd after filter '$filter'" }
    $el = Get-ListItemAncestor $el
    Invoke-El $el $ProcId $title
    Start-Sleep -Milliseconds 1200
}

function Wait-SettingsWindow([uint32]$ProcId, [int64]$MainHwnd) {
    $dl = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $dl) {
        $w = @(Get-WinUiWindows $ProcId | Where-Object {
            $_.Hwnd64 -ne $MainHwnd -and $_.Title -match 'Settings'
        })
        if ($w.Count -gt 0) { return $w[0] }
        Start-Sleep -Milliseconds 200
    }
    $titles = @(Get-WinUiWindows $ProcId | ForEach-Object { $_.Title })
    throw "HARVEST_MISS: no Settings window (Open Config probably shelled the file). titles=$($titles -join '|')"
}

function New-IsolatedConfig {
    $tempXdg = Join-Path $env:TEMP ("wintty-fuzz-xdg-{0:HHmmss}" -f (Get-Date))
    $winttyDir = Join-Path $tempXdg 'wintty'
    New-Item -ItemType Directory -Force -Path $winttyDir | Out-Null
    $dst = Join-Path $winttyDir 'config.wintty'
    $src = Join-Path $env:APPDATA 'Ghostty\config'
    $raw = if (Test-Path -LiteralPath $src) { [IO.File]::ReadAllText($src) } else { "command = pwsh.exe`n" }
    if ($raw -notmatch '(?m)^windows-settings-ui\s*=\s*true\s*$') {
        $raw = "windows-settings-ui = true`n" + $raw
    }
    $header = "# fuzz isolated XDG copy; windows-settings-ui forced true`nwindows-settings-ui = true`n"
    # Last-wins if the copy already has the key; header first then body is fine
    # because body already contains the true assignment. Keep a leading force
    # anyway so GetFileValue's top-level cache cannot miss it if the copy is empty.
    [IO.File]::WriteAllText($dst, $header + $raw)
    Write-Host "XDG_CONFIG_HOME=$tempXdg"
    Write-Host "config=$dst"
    return $tempXdg
}

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$originalXdgSet = Test-Path Env:XDG_CONFIG_HOME
$originalXdg = if ($originalXdgSet) { $env:XDG_CONFIG_HOME } else { $null }
$tempXdg = New-IsolatedConfig
$proc = $null
$keybindKilled = $false
$settingsTitle = $null
$pages = @()
$script:vtabFound = @()

Assert-NoWintty
$script:WinttyStamp = Get-WinttyLaunchStamp
try {
    $env:XDG_CONFIG_HOME = $tempXdg
    Start-Sleep -Milliseconds 400
    $proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path $ExePath)
    $pid32 = [uint32]$proc.Id
    $main = Wait-Ready $proc
    Start-Sleep -Seconds 2
    $main = @(Get-WinUiWindows $pid32) | Select-Object -First 1
    $hwnd64 = [int64]$main.Hwnd64
    Write-Host "hwnd=$hwnd64 pid=$pid32 title=$($main.Title)"
    Shot $hwnd64 '00-launch'

    Invoke-PaletteCommand $hwnd64 $pid32 'open config' 'Open Config'
    $settings = Wait-SettingsWindow $pid32 $hwnd64
    $settingsHwnd = [int64]$settings.Hwnd64
    $settingsTitle = $settings.Title
    Write-Host "settings hwnd=$settingsHwnd title=$settingsTitle"
    Shot $settingsHwnd '01-settings-open'
    Shot-Pid $pid32 '01b-all'

    $sroot = [System.Windows.Automation.AutomationElement]::FromHandle([MzS]::P($settingsHwnd))
    # Keybindings last: ItemsSource of internal row types can kill the UI thread.
    $nav = @(
        @{ Name = 'Appearance'; Shot = '02-appearance' },
        @{ Name = 'Profiles'; Shot = '03-profiles' },
        @{ Name = 'Colors'; Shot = '04-colors' },
        @{ Name = 'Terminal'; Shot = '05-terminal' },
        @{ Name = 'Advanced'; Shot = '06-advanced' },
        @{ Name = 'Raw Editor'; Shot = '07-raw' },
        @{ Name = 'General'; Shot = '08-general' }
    )
    foreach ($n in $nav) {
        $proc.Refresh(); if ($proc.HasExited) { throw "PRODUCT_FAIL died before $($n.Name) exit=$($proc.ExitCode)" }
        $sroot = [System.Windows.Automation.AutomationElement]::FromHandle([MzS]::P($settingsHwnd))
        Select-Nav $sroot $pid32 $n.Name
        $pages += $n.Name
        Shot $settingsHwnd $n.Shot
    }

    $sroot = [System.Windows.Automation.AutomationElement]::FromHandle([MzS]::P($settingsHwnd))
    $vtabCards = @(
        'Vertical tab width',
        'Pin vertical tabs expanded',
        'Expand vertical tabs on hover'
    )
    $script:vtabFound = @()
    foreach ($name in $vtabCards) {
        if ($null -ne (Find-Name $sroot $name)) { $script:vtabFound += $name }
        else { Write-Host "HARVEST_MISS: settings card '$name'" }
    }
    Write-Host "vtabCards=$($script:vtabFound -join ',')/$($vtabCards.Count)"

    $sroot = [System.Windows.Automation.AutomationElement]::FromHandle([MzS]::P($settingsHwnd))
    $search = Find-Name $sroot 'Search settings'
    if ($null -eq $search) {
        $editCt = [System.Windows.Automation.ControlType]::Edit
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $editCt)
        $search = $sroot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    }
    if ($null -ne $search) {
        try {
            $vp = $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $vp.SetValue('font')
            Write-Host "search font"
            Start-Sleep -Milliseconds 800
            Shot $settingsHwnd '09-search-font'
            $pages += 'search:font'
            # Nav while _pendingQuery is set is a no-op (results pane stays).
            # Clear via ValuePattern so Keybindings actually constructs.
            $vp.SetValue('')
            Write-Host "search cleared"
            [MzS]::Key($settingsHwnd, 0x1B)
            Start-Sleep -Milliseconds 500
            Shot $settingsHwnd '09b-search-cleared'
        } catch { Write-Host "search set failed: $_" }
    } else {
        Write-Host "HARVEST_MISS: no Search settings box"
    }

    try {
        $sroot = [System.Windows.Automation.AutomationElement]::FromHandle([MzS]::P($settingsHwnd))
        Select-Nav $sroot $pid32 'Keybindings'
        # Loaded → ApplyFilter → ItemsSource can kill the UI thread after Select returns.
        Start-Sleep -Seconds 2
        $proc.Refresh()
        if ($proc.HasExited -or $null -eq [MzS]::RectOf($settingsHwnd)) {
            $keybindKilled = $true
            Write-Host "PRODUCT_FAIL: died after Keybindings nav (ItemsSource ABI)"
        } else {
            $pages += 'Keybindings'
            Shot $settingsHwnd '10-keybindings'
        }
    } catch {
        $proc.Refresh()
        $keybindKilled = $true
        Write-Host "keybindings nav: $_ killed=$($proc.HasExited)"
    }

    $proc.Refresh()
    if (-not $proc.HasExited -and -not $keybindKilled) {
        try {
            $sroot = [System.Windows.Automation.AutomationElement]::FromHandle([MzS]::P($settingsHwnd))
            $close = Find-Name $sroot 'Close'
            if ($null -ne $close) { Invoke-El $close $pid32 'Close Wintty Settings' }
            else { Write-Host "HARVEST_MISS: no Close on Settings" }
        } catch { Write-Host "close settings: $_" }
        Start-Sleep -Milliseconds 400
        if ($null -ne [MzS]::RectOf($hwnd64)) { Shot $hwnd64 '11-after-settings-close' }
        else { Write-Host "main hwnd gone after settings close" }
    }
}
finally {
    if ($null -ne $proc) {
        $proc.Refresh()
        if (-not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 300
        }
    }
    Stop-WinttyStartedAfter -Since $script:WinttyStamp -ExePath $ExePath
    if ($originalXdgSet) { $env:XDG_CONFIG_HOME = $originalXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
}

$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
$result = @{
    aliveAtEnd = $false
    keybindKilled = $keybindKilled
    crashGrew = $crashGrew
    settingsTitle = $settingsTitle
    pages = $pages
    vtabCards = $script:vtabFound
    xdg = $tempXdg
}
$result | ConvertTo-Json | Set-Content (Join-Path $OutDir 'result.json')
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)
if ($keybindKilled -or $crashGrew -or ($script:vtabFound.Count -lt 3)) { exit 2 }
exit 0
