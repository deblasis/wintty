#requires -Version 7
# Palette Open Config with windows-settings-ui=true (isolated XDG).
# Seam-launched (#930): the palette opens through focus{frame} +
# chord{0x50,ctrl,shift} - the window's real routing, one call below the
# framework - instead of a right-click on the pane grid. Everything inside
# the palette and the Settings window was already UIA and stays UIA; the
# old bounds-click fallback on an element without InvokePattern is gone
# (a loud HARVEST_MISS now), so this harness synthesizes zero OS input.
# No desktop-root walk. UIA scoped to Wintty hwnds only.
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
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
public static class MzS {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    // Posted to the app's own window only - window-targeted, no foreground
    // steal, the same pattern frame-keybind-live-key uses.
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

function Invoke-El($el, [string]$what) {
    if ($null -eq $el) { throw "HARVEST_MISS: no UIA element for $what" }
    # InvokePattern or a loud miss - never a bounds click. A click is OS
    # input this harness no longer synthesizes (#930), and a silent
    # fallback would hide a control that stopped being invokable.
    $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pat.Invoke()
    Write-Host "invoke $what"
    Start-Sleep -Milliseconds 400
    return
}

function Select-Nav($root, [string]$name) {
    $el = Find-Name $root $name
    if ($null -eq $el) { throw "HARVEST_MISS: nav '$name'" }
    # Selection or a loud miss - no click fallback, same rule as Invoke-El.
    $pat = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pat.Select()
    Write-Host "select $name"
    Start-Sleep -Milliseconds 700
}

function Open-Palette($Session) {
    # The palette chord through the seam: focus{frame} + chord{0x50,ctrl,shift}
    # runs the window's real routing (focus gate, residual table, libghostty
    # match, dispatch) - the same path the menu item landed in, one call
    # below the framework. The dispatched answer says whether a chord was
    # taken; the palette element itself is then found by UIA as before.
    [void](Invoke-SeamCommand $Session @{ op = 'focus'; target = 'frame' })
    $r = Invoke-SeamCommand $Session @{ op = 'chord'; key = 0x50; ctrl = $true; shift = $true }
    if (-not $r.dispatched) {
        throw "HARVEST_MISS: the palette chord was not dispatched (focus was '$($r.focus)')"
    }
    Start-Sleep -Milliseconds 400
}

function Set-PaletteFilter([int64]$MainHwnd, [string]$text) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([MzS]::P($MainHwnd))
    # By AutomationId, not "the first Edit under the window". The terminal
    # keeps a 1x1 IME sink TextBox focused whenever a pane has focus, and it
    # sorts ahead of the palette in the tree - so FindFirst(Edit) returned the
    # sink, SetValue typed into it, and the palette never filtered. The list
    # then still held every command, so the lookup below failed on a command
    # that was present the whole time.
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SearchBox')
    $edit = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -eq $edit) { throw "HARVEST_MISS: no SearchBox in palette" }
    $vp = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($text)
    Write-Host "filter '$text'"
    Start-Sleep -Milliseconds 350
}

function Invoke-PaletteCommand($Session, [int64]$MainHwnd, [string]$filter, [string]$title) {
    Open-Palette $Session
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
    Invoke-El $el $title
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

function New-ConfigText {
    # Same content policy the isolated XDG copy had: the developer's real
    # config, with windows-settings-ui forced on - Start-SeamSession stages
    # it as the whole of XDG_CONFIG_HOME.
    $src = Join-Path $env:APPDATA 'Ghostty\config'
    $raw = if (Test-Path -LiteralPath $src) { [IO.File]::ReadAllText($src) } else { "command = pwsh.exe`n" }
    if ($raw -notmatch '(?m)^windows-settings-ui\s*=\s*true\s*$') {
        $raw = "windows-settings-ui = true`n" + $raw
    }
    return "windows-settings-ui = true`n" + $raw
}

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$session = $null
$keybindKilled = $false
$settingsTitle = $null
$pages = @()
$script:vtabFound = @()

Assert-NoWintty -Context 'The settings harness'
try {
    $session = Start-SeamSession -ExePath $ExePath -ConfigText (New-ConfigText)
    $proc = $session.Proc
    $pid32 = [uint32]$proc.Id
    $hwnd64 = [int64]$session.Hwnd64
    Write-Host "hwnd=$hwnd64 pid=$pid32"
    Shot $hwnd64 '00-launch'

    Invoke-PaletteCommand $session $hwnd64 'open config' 'Open Config'
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
        Select-Nav $sroot $n.Name
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
        Select-Nav $sroot 'Keybindings'
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
            if ($null -ne $close) { Invoke-El $close 'Close Wintty Settings' }
            else { Write-Host "HARVEST_MISS: no Close on Settings" }
        } catch { Write-Host "close settings: $_" }
        Start-Sleep -Milliseconds 400
        if ($null -ne [MzS]::RectOf($hwnd64)) { Shot $hwnd64 '11-after-settings-close' }
        else { Write-Host "main hwnd gone after settings close" }
    }
}
finally {
    if ($null -ne $session) { Stop-SeamSession $session }
}

$crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
$result = @{
    aliveAtEnd = $false
    keybindKilled = $keybindKilled
    crashGrew = $crashGrew
    settingsTitle = $settingsTitle
    pages = $pages
    vtabCards = $script:vtabFound
}
$result | ConvertTo-Json | Set-Content (Join-Path $OutDir 'result.json')
Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)
if ($keybindKilled -or $crashGrew -or ($script:vtabFound.Count -lt 3)) { exit 2 }
exit 0
