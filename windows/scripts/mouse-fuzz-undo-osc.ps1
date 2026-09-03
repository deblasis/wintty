#requires -Version 7
<#
    Undo after split, reopen-closed-tab, and the OSC title round trip.

    Seam-actuated (#930). Seed two tabs, split the active one, undo the
    split through chord{0x5A,ctrl,shift} (Ctrl+Shift+Z, the binding), close
    a tab through the seam's close op, reopen it through
    chord{0x54,ctrl,shift} (Ctrl+Shift+T), and set the window title from
    the shell with the seam's send-text op (armed per-harness with
    -AllowInput; the old WM_CHAR posts almost certainly never delivered,
    which is why the OSC leg used to print OSC_UNVERIFIED and exit 0).
    The OSC read is in-process - tab-labels' shellTitle, the OSC result
    itself - because the seeded UserOverrideTitle masks any shell title
    from the window caption.

    The old undo verdict was `$undoOk = $true` - written after invoking the
    palette item, gated on nothing. The real oracle is the state the seam
    already reports: the tab's leaf count going 1 -> 2 across the split and
    back to 1 across the undo. The OSC title now gates exit 2 on a miss
    (#930's acceptance list); an unverified title is a finding, not a note.

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
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$Config = @'
windows-single-instance = true
window-save-state = never
confirm-close-surface = false
profile.pwsh.name = PowerShell
profile.pwsh.command = pwsh.exe
default-profile = pwsh
'@

$titles = @('undo-a', 'undo-b')
$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$script:Findings = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null

function Shot($Session, [string]$Name) {
    $rc = [SeamWin]::RectOf($Session.Hwnd64)
    if ($null -eq $rc) { return }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir "shots\$Name.png"))
    $g.Dispose(); $bmp.Dispose()
}

# The active tab's leaf count, straight out of the seam's state block.
function LeafCount($Session) {
    $st = Invoke-SeamCommand $Session @{ op = 'get-state' }
    # state.active is the INDEX of the active tab; leaves rides its entry.
    $i = [int]$st.state.active
    $tab = @($st.state.tabs)[$i]
    if ($null -eq $tab) { throw "HARVEST_MISS: state.active is $i but the tab list is shorter" }
    return [int]$tab.leaves
}

function TabCount($Session) {
    # Response assigned first: a member chain on a hashtable LITERAL in
    # argument mode parses as separate arguments and binds $Command null.
    $r = Invoke-SeamCommand $Session @{ op = 'get-state' }
    return @($r.state.tabs).Count
}

function Invoke-Chord($Session, [int]$Key) {
    [void](Invoke-SeamCommand $Session @{ op = 'focus'; target = 'frame' })
    $r = Invoke-SeamCommand $Session @{ op = 'chord'; key = $Key; ctrl = $true; shift = $true }
    if (-not $r.dispatched) {
        throw ("HARVEST_MISS: chord 0x{0:X2} was not dispatched (focus was '{1}')" -f $Key, $r.focus)
    }
}

try {
    Assert-NoWintty -Context 'The undo-osc harness'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config -AllowInput
    $hwnd64 = [int64]$session.Hwnd64
    Write-Host "hwnd=$hwnd64 pid=$($session.Proc.Id)"
    Shot $session '00-launch'

    [void](Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = 2; titles = $titles })
    [void](Invoke-SeamCommand $session @{ op = 'select'; index = 1 })

    $before = LeafCount $session
    [void](Invoke-SeamCommand $session @{ op = 'split' })
    $afterSplit = LeafCount $session
    Write-Host "leaves $before -> $afterSplit after split"
    Shot $session '01-split'
    if ($afterSplit -ne ($before + 1)) {
        $script:Findings.Add("split took the active tab from $before to $afterSplit leaves, wanted +1")
    }

    Invoke-Chord $session 0x5A
    # The undo runs asynchronously behind the chord ack; poll for the
    # return to the pre-split count rather than read once.
    $afterUndo = LeafCount $session
    $deadline = (Get-Date).AddSeconds(5)
    while ($afterUndo -gt $before -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $afterUndo = LeafCount $session
    }
    Write-Host "leaves $afterUndo after undo"
    Shot $session '02-undo'
    if ($afterUndo -ne $before) {
        $script:Findings.Add("undo left the tab at $afterUndo leaves, wanted back to $before")
    }

    # Close the single-pane tab, then reopen it through the binding.
    $tabsBefore = TabCount $session
    [void](Invoke-SeamCommand $session @{ op = 'close'; index = 0 })
    $tabsAfterClose = TabCount $session
    Write-Host "tabs $tabsBefore -> $tabsAfterClose after close"
    Shot $session '03-closed'
    if ($tabsAfterClose -ne ($tabsBefore - 1)) {
        $script:Findings.Add("close left $tabsAfterClose tabs of $tabsBefore")
    }

    Invoke-Chord $session 0x54
    $tabsAfterReopen = TabCount $session
    $deadline = (Get-Date).AddSeconds(5)
    while ($tabsAfterReopen -le $tabsAfterClose -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $tabsAfterReopen = TabCount $session
    }
    Write-Host "tabs $tabsAfterReopen after reopen"
    Shot $session '04-reopen'
    if ($tabsAfterReopen -le $tabsAfterClose) {
        $script:Findings.Add("Reopen Closed Tab left $tabsAfterReopen tabs, wanted more than $tabsAfterClose")
    }

    # OSC title round trip, read in-process: the seeded UserOverrideTitle
    # beats any shell title in the caption, so the WINDOW caption can never
    # show OSC-FUZZ here and gating on it would be structurally blind. The
    # shell-reported title is the OSC result itself.
    [void](Invoke-SeamCommand $session @{ op = 'send-text'; text = 'Write-Host "`e]0;OSC-FUZZ`a"' + "`r" })
    # The reopened tab's shell is freshly started and needs a moment before
    # it executes the line, so poll for the title rather than read once -
    # a fixed 1.2s read a startup title that was about to change.
    $oscTitle = ''
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        $labels = Invoke-SeamCommand $session @{ op = 'tab-labels' }
        $active = @($labels.labels)[[int](Invoke-SeamCommand $session @{ op = 'get-state' }).state.active]
        $oscTitle = "$($active.shellTitle)"
        if ($oscTitle -match 'OSC-FUZZ') { break }
        Start-Sleep -Milliseconds 400
    }
    Write-Host "shellTitle=$oscTitle caption=$([SeamWin]::TitleOf([SeamWin]::P($hwnd64)))"
    Shot $session '05-osc'
    if ($oscTitle -notmatch 'OSC-FUZZ') {
        $script:Findings.Add("the OSC title never reached the tab's shell-reported title: '$oscTitle'")
    }

    if ($session.Proc.HasExited) {
        throw "APP_EXIT: the app exited during the run (code $($session.Proc.ExitCode))"
    }
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
    actuation       = 'seam (WINTTY_TEST_SEAM=<session token>, send-text armed); undo/reopen via chords'
    leavesBefore    = $before
    leavesAfterSplit = $afterSplit
    leavesAfterUndo = $afterUndo
    tabsAfterClose  = $tabsAfterClose
    tabsAfterReopen = $tabsAfterReopen
    oscTitle        = $oscTitle
    findings        = $script:Findings
    harness         = $harnessError
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

if ($script:Findings.Count -gt 0) { exit 2 }
if ($harnessError) { exit 1 }
exit 0
