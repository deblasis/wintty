#requires -Version 7
<#
    Live-fuzz Wintty inspector: open/close, present, resize, tab-dismiss.

    Seam-actuated (#930). The toggle is chord{0x49,ctrl,shift} - Ctrl+Shift+I
    through the window's real routing - where the old harness right-clicked
    the pane, walked the context menu to the palette, filtered and invoked.
    The shell seeding is the seam's send-text op (armed per-harness with
    -AllowInput): the old WM_CHAR posts almost certainly never delivered,
    so the inspector had whatever default content it paints, and the render
    stats were measuring an unseeded surface. The dismissal oracle is a
    config-bound Ctrl+T (keybind = ctrl+t:new_tab) through the same chord
    path, replacing the strip's New tab button.

    Dropped, ungated (issue #930, MUST_STAY_REAL_INPUT): the
    inspector-centre click, the plain wheel scroll, the ctrl+wheel zoom and
    the Ctrl+0 reset. None was read by an exit condition - the zoom leg's
    verdict went to result.json and nowhere else - and the zoom gate reads
    the live keyboard modifier, which a seam op enters below. Recorded on
    #866 rather than preserved through a weaker op.

    Kept as real input, deliberately: MoveWindow for the resize leg (the
    frame's own response to a real WM_SIZE), the titlebar Close through
    UIA Invoke, and CopyFromScreen for the render stats.

    Exits 0 clean, 2 findings, 1 could-not-run.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots') | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$Config = @'
windows-single-instance = true
window-save-state = never
keybind = ctrl+t:new_tab
'@

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$script:Findings = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null

function Shot([int64]$Hwnd64, [string]$name) {
    $rc = [SeamWin]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw "HARVEST_MISS: degenerate rect for $name" }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $p = Join-Path $OutDir "shots\$name.png"
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose()
    Write-Host "shot $name $($rc.W)x$($rc.Hh)"
    return $p
}

function Get-ShotStats([string]$path) {
    $bmp = [System.Drawing.Image]::FromFile($path)
    try {
        $uniq = [System.Collections.Generic.HashSet[int]]::new()
        $nonDark = 0
        $step = 4
        for ($y = 0; $y -lt $bmp.Height; $y += $step) {
            for ($x = 0; $x -lt $bmp.Width; $x += $step) {
                $c = $bmp.GetPixel($x, $y)
                [void]$uniq.Add(($c.R * 65536) + ($c.G * 256) + $c.B)
                if ($c.R -gt 40 -or $c.G -gt 40 -or $c.B -gt 40) { $nonDark++ }
            }
        }
        return @{ unique = $uniq.Count; nonDark = $nonDark }
    }
    finally { $bmp.Dispose() }
}

function Find-Name($root, [string]$name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-El($el, [string]$what) {
    if ($null -eq $el) { throw "HARVEST_MISS: no UIA element for $what" }
    $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pat.Invoke()
    Write-Host "invoke $what"
    Start-Sleep -Milliseconds 400
}

function Get-InspectorWindow([uint32]$ProcId, [int64]$MainHwnd) {
    $w = @(Get-SeamWinUiWindows $ProcId) | Where-Object {
        $_.Hwnd64 -ne $MainHwnd -and "$($_.Title)" -match 'Inspector'
    }
    return $w | Select-Object -First 1
}

function Wait-InspectorGone([uint32]$ProcId, [int64]$MainHwnd, [int]$seconds) {
    $dl = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $dl) {
        if ($null -eq (Get-InspectorWindow $ProcId $MainHwnd)) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

# Every chord here is Ctrl+Shift except Ctrl+T, the config-bound new_tab,
# which is plain Ctrl.
function Invoke-Chord($Session, [int]$Key, [switch]$Plain) {
    [void](Invoke-SeamCommand $Session @{ op = 'focus'; target = 'frame' })
    $r = Invoke-SeamCommand $Session @{ op = 'chord'; key = $Key; ctrl = $true; shift = -not $Plain.IsPresent }
    if (-not $r.dispatched) {
        throw ("HARVEST_MISS: chord 0x{0:X2} was not dispatched (focus was '{1}')" -f $Key, $r.focus)
    }
}

try {
    Assert-NoWintty -Context 'The inspector harness'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config -AllowInput
    $proc = $session.Proc
    $pid32 = [uint32]$proc.Id
    $hwnd64 = [int64]$session.Hwnd64
    Write-Host "main hwnd=$hwnd64 pid=$pid32"

    # Seed the shell so the inspector has surface state to show.
    [void](Invoke-SeamCommand $session @{ op = 'send-text'; text = "echo INSPECTOR-FUZZ`r" })
    Start-Sleep -Milliseconds 800

    Invoke-Chord $session 0x49
    Start-Sleep -Seconds 2
    $insp = Get-InspectorWindow $pid32 $hwnd64
    if ($null -eq $insp) { $script:Findings.Add('no Inspector window after the toggle chord') }
    else {
        Write-Host "inspector hwnd=$($insp.Hwnd64) title=$($insp.Title)"
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([SeamWin]::P($hwnd64))
        $noticeOpen = $null -ne (Find-Name $root 'Inspector unavailable')
        $p1 = Shot $insp.Hwnd64 '01-open'
        $stats1 = Get-ShotStats $p1
        Write-Host "open stats unique=$($stats1.unique) nonDark=$($stats1.nonDark)"
        if ($noticeOpen) { $script:Findings.Add('the "Inspector unavailable" notice is showing') }
        $renderOk = ($stats1.unique -ge 4) -and ($stats1.nonDark -ge 3)
        if (-not $renderOk) { $script:Findings.Add("inspector did not render: unique=$($stats1.unique) nonDark=$($stats1.nonDark)") }

        # Resize (exercises surface_resize + present on a real WM_SIZE).
        $rc = [SeamWin]::RectOf($insp.Hwnd64)
        $nw = [Math]::Max(640, [int]($rc.W * 0.75))
        $nh = [Math]::Max(480, [int]($rc.Hh * 0.8))
        [void][SeamWin]::MoveWindow([SeamWin]::P($insp.Hwnd64), $rc.L, $rc.T, $nw, $nh, $true)
        Start-Sleep -Seconds 2
        [void](Shot $insp.Hwnd64 '02-after-resize')
        $rc2 = [SeamWin]::RectOf($insp.Hwnd64)
        Write-Host "resize $($rc.W)x$($rc.Hh) -> $($rc2.W)x$($rc2.Hh)"

        # Close via the titlebar (the chord needs main focus the inspector holds).
        $iroot = [System.Windows.Automation.AutomationElement]::FromHandle([SeamWin]::P($insp.Hwnd64))
        $close = Find-Name $iroot 'Close'
        if ($null -ne $close) {
            Invoke-El $close 'Inspector Close'
        }
        else {
            # WinUI's titlebar Close often isn't named in UIA; WM_CLOSE is
            # fine (the old harness's own fallback, kept per #930).
            [void][SeamWin]::PostMessage([SeamWin]::P($insp.Hwnd64), 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
            Write-Host 'inspector closed via WM_CLOSE (no UIA Close)'
        }
        Start-Sleep -Milliseconds 800
        if ($null -ne (Get-InspectorWindow $pid32 $hwnd64)) {
            $script:Findings.Add('the inspector survived its titlebar Close')
        }

        # Re-open.
        Invoke-Chord $session 0x49
        Start-Sleep -Seconds 2
        $insp2 = Get-InspectorWindow $pid32 $hwnd64
        if ($null -eq $insp2) { $script:Findings.Add('re-open failed after the close') }
        else { [void](Shot $insp2.Hwnd64 '03-reopen') }

        # A new tab (config-bound Ctrl+T through the chord) must dismiss it.
        Invoke-Chord $session 0x54 -Plain
        $tabDismissOk = Wait-InspectorGone $pid32 $hwnd64 4
        Write-Host "tabDismiss gone=$tabDismissOk"
        if (-not $tabDismissOk) { $script:Findings.Add('the inspector survived a tab change') }
    }

    $proc.Refresh()
    if ($proc.HasExited) { throw "APP_EXIT: the app exited during the run (code $($proc.ExitCode))" }
}
catch {
    $msg = "$($_.Exception.Message)"
    if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { $script:Findings.Add($msg) }
    else { $harnessError = $msg }
    Write-Host "ERROR: $msg" -ForegroundColor Red
}
finally {
    if ($null -ne $session) { Stop-SeamSession $session }
}

if ((Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)) {
    $script:Findings.Add('crash.log grew during the run')
}

[ordered]@{
    actuation   = 'seam (WINTTY_TEST_SEAM=<session token>, send-text armed); toggle/ctrl+T via chords, resize via MoveWindow, close via UIA'
    findings    = $script:Findings
    harness     = $harnessError
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

if ($script:Findings.Count -gt 0) { exit 2 }
if ($harnessError) { exit 1 }
exit 0
