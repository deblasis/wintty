#requires -Version 7
<#
    Two paste payloads through the real clipboard path, seam-actuated
    (#930). This is mouse-fuzz-kitty.ps1 and mouse-fuzz-osc-paste.ps1
    merged: they were two ~500-line files whose only differences were
    the clipboard payload and the oracle - everything else, including
    ~250 lines of palette plumbing and a foreground-gated Enter, was a
    byte-level duplicate.

    What runs now: one session, one tab, two pastes. Each payload is a
    complete pwsh line ending in CR, so the paste itself submits it -
    which only works because the paste arrives UNBRACKETED (core wraps
    pastes in ESC[200~..201~ under mode 2004, and PSReadLine does not
    execute a trailing newline inside a bracketed paste; that mode is
    evidently not reaching core through ConPTY today). If passthrough
    ever changes that, both scenarios go red and this comment is why.
    The paste is chord{0x56,ctrl,shift} - Ctrl+Shift+V through the
    window's real routing into the same paste_from_clipboard the old
    palette path dispatched.

    Scenario 1 (OSC): the pasted command sets the shell-reported title
    to a marker, read back through tab-labels' shellTitle - in-process,
    where the old harness read the Win32 caption. A missed title is a
    FINDING (exit 2): the old osc-paste printed OSC_UNVERIFIED and
    exited 0, so a broken OSC path was green for years.

    Scenario 2 (kitty): the pasted command transmits and places a
    32x16 #FF00CC RGBA image at 10x5 cells; the oracle counts pixels
    matching that colour in a screen capture. CopyFromScreen borrows
    the desktop, so the oracle refuses instead of guessing: the window
    must sit fully inside the virtual screen, it is raised topmost
    without activation before capturing (the composited screen shows
    whatever the desktop parked over the rect - focus is XAML-logical
    and raises nothing; only another TOPMOST window can still cover
    it, which this oracle cannot see), and a baseline shot taken
    BEFORE the paste must contain zero matching pixels - any baseline
    hit means this machine's content already matches the filter and
    the count proves nothing. All three refusals are exit 1. The
    bundled conpty.dll is what forwards the kitty sequence; without it
    the scenario is skipped and the absence is a finding, not a red
    paste.

    What this does NOT gate: the grid's rendering quality of the image
    (scaling, transparency, placement precision) beyond its presence.

    The clipboard is the owner's real one (paste has to come from
    somewhere and there is no seam op for it): the previous TEXT is
    snapshotted with backoff and restored with a verified read-back,
    and a restore that cannot be confirmed is a recorded harness
    error, not a silent clean pass. Non-text formats are lost, and a
    snapshot read that stays empty leaves the clipboard untouched
    rather than wiping it - both stated here rather than hidden. The
    shell runs with -NoProfile so the owner's prompt (which may set
    titles of its own) cannot race the marker.

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
Add-Type -AssemblyName System.Windows.Forms
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$Config = @'
windows-single-instance = true
window-save-state = never
clipboard-paste-protection = false
profile.pwsh.name = PowerShell
profile.pwsh.command = pwsh.exe -NoProfile
default-profile = pwsh
'@

$oscMarker = 'PASTE-OSC-FUZZ'
$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$script:Findings = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null
$ownerText = $null
$hadOwnerText = $false
$oscTitle = ''
$kittyHits = 0
$baselineHits = -1
$conptyPresent = $false

function Shot($Session, [string]$Name) {
    $rc = [SeamWin]::RectOf($Session.Hwnd64)
    if ($null -eq $rc) { throw "HARVEST_MISS: degenerate rect for $Name" }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $p = Join-Path $OutDir "shots\$Name.png"
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose()
    return $p
}

function New-KittyCommand {
    # 32x16 solid #FF00CC RGBA, placed in 10x5 cells. Distinct vs dark theme.
    $w = 32; $h = 16
    $px = [byte[]]::new($w * $h * 4)
    for ($i = 0; $i -lt $px.Length; $i += 4) {
        $px[$i] = 255; $px[$i+1] = 0; $px[$i+2] = 204; $px[$i+3] = 255
    }
    $b64 = [Convert]::ToBase64String($px)
    return "[Console]::Out.Write([char]27 + '_Ga=T,f=32,s=$w,v=$h,i=7,q=2,c=10,r=5;' + '$b64' + [char]27 + '\')"
}

function Count-KittyPixels([string]$pngPath) {
    if (-not (Test-Path $pngPath)) { return 0 }
    $bmp = [System.Drawing.Bitmap]::FromFile($pngPath)
    try {
        $hits = 0
        # Skip chrome: title/tab ~80px, left gutter ~8px.
        $x0 = 12; $y0 = 90
        $x1 = [Math]::Max($x0 + 1, $bmp.Width - 20)
        $y1 = [Math]::Max($y0 + 1, $bmp.Height - 20)
        for ($y = $y0; $y -lt $y1; $y += 2) {
            for ($x = $x0; $x -lt $x1; $x += 2) {
                $c = $bmp.GetPixel($x, $y)
                if ($c.R -ge 200 -and $c.G -le 40 -and $c.B -ge 160) { $hits++ }
            }
        }
        return $hits
    } finally { $bmp.Dispose() }
}

# Set the clipboard, wait out the clipboard-manager contention window,
# then drive the paste chord. The settle delay is deliberate: clipboard
# listeners react to the write milliseconds after it lands, and the
# product's paste read treats a locked clipboard as "no text" - firing
# the chord into that window would misread contention as a payload
# failure.
function Invoke-PastePayload($Session, [string]$Payload, [string]$What) {
    Set-Clipboard -Value $Payload
    Write-Host "clipboard set ($What)"
    Start-Sleep -Milliseconds 400
    [void](Invoke-SeamCommand $Session @{ op = 'focus'; target = 'frame' })
    $r = Invoke-SeamCommand $Session @{ op = 'chord'; key = 0x56; ctrl = $true; shift = $true }
    if (-not $r.dispatched) {
        throw ("HARVEST_MISS: the paste chord was not dispatched (focus was '{0}')" -f $r.focus)
    }
}

try {
    Assert-NoWintty -Context 'The paste-payloads harness'
    $conptyPresent = Test-Path (Join-Path (Split-Path $ExePath) 'conpty.dll')
    Write-Host "conptyPresent=$conptyPresent"
    if (-not $conptyPresent) {
        $script:Findings.Add('conpty.dll missing next to Wintty.exe - the kitty forward cannot run')
    }

    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config
    $hwnd64 = [int64]$session.Hwnd64
    Write-Host "hwnd=$hwnd64 pid=$($session.Proc.Id)"

    # Known geometry for the run; the pixel leg re-checks it against the
    # virtual screen before capturing, so a machine too small for that
    # oracle still gets the OSC verdict.
    [void][SeamWin]::MoveWindow([SeamWin]::P($hwnd64), 60, 60, 1280, 820, $true)
    Start-Sleep -Milliseconds 600
    [void](Shot $session '00-launch')

    # The paste needs real clipboard content; snapshot the owner's TEXT and
    # restore it in the finally. Get-Clipboard returns EMPTY both for no text
    # and for a read that lost a contention race (clipboard managers re-open
    # on change), so the read is retried with real backoff before being
    # believed; and the finally never writes when unsure - an empty clipboard
    # is left alone rather than confirmed with a wipe.
    foreach ($attempt in 1..5) {
        $probe = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($null -ne $probe -and "$probe" -ne '') { $ownerText = $probe; $hadOwnerText = $true; break }
        Start-Sleep -Milliseconds (150 * $attempt)
    }
    if (-not $hadOwnerText) {
        Write-Host 'HARNESS: no owner clipboard text read (empty, non-text, or contended); leaving it untouched'
    }

    # Scenario 1: OSC title round trip. The backtick escapes are the
    # SHELL's (single-quoted here so they survive to pwsh).
    Invoke-PastePayload $session ('Write-Host "`e]0;' + $oscMarker + '`a"' + "`r") 'OSC title command + CR'
    [void](Shot $session '02-osc-pasted')

    # 15s: the budget starts at the chord and must absorb a cold pwsh
    # start, profile load, and the queued paste - not just the OSC.
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $labels = Invoke-SeamCommand $session @{ op = 'tab-labels' }
        $active = @($labels.labels)[0]
        $oscTitle = "$($active.shellTitle)"
        if ($oscTitle -match $oscMarker) { break }
        Start-Sleep -Milliseconds 400
    }
    Write-Host "shellTitle=$oscTitle"
    [void](Shot $session '03-osc-title')
    if ($oscTitle -notmatch $oscMarker) {
        $script:Findings.Add(("the OSC marker did not arrive in the shell-reported title: '$oscTitle' " +
            '(clipboard read, paste dispatch, shell start, or title plumbing)'))
    }

    # Scenario 2: kitty image presence. Calibrate before pasting - a
    # baseline that already matches the filter makes the count prove
    # nothing, and the only honest verdict there is no verdict. The
    # capture is CopyFromScreen, so the leg earns its pixels twice: the
    # whole window must sit inside the virtual screen, and it is raised
    # topmost without activation - focus is XAML-logical only, and the
    # desktop parked over the rect would otherwise be counted instead of
    # the app. Another TOPMOST window can still cover it; that is the one
    # overlap this oracle cannot see and the run does not pretend to.
    $rc = [SeamWin]::RectOf($hwnd64)
    if ($null -eq $rc) { throw 'HARVEST_MISS: no rect for the kitty leg' }
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    if ($rc.L -lt $vs.X -or $rc.T -lt $vs.Y -or
        $rc.R -gt ($vs.X + $vs.Width) -or $rc.B -gt ($vs.Y + $vs.Height)) {
        throw (("HARVEST_MISS: window {0},{1}-{2},{3} is not fully on the virtual screen " +
            "{4},{5}-{6},{7}; CopyFromScreen would read over it") -f
            $rc.L, $rc.T, $rc.R, $rc.B, $vs.X, $vs.Y, ($vs.X + $vs.Width), ($vs.Y + $vs.Height))
    }
    [void][SeamWin]::PlaceOnTop($hwnd64)
    Start-Sleep -Milliseconds 300
    $baselineHits = Count-KittyPixels (Shot $session '04-kitty-baseline')
    Write-Host "baselineHits=$baselineHits"
    if ($baselineHits -gt 0) {
        throw (("HARVEST_MISS: the baseline capture already contains {0} pixels matching the kitty filter; " +
            'this machine''s content cannot host the pixel oracle (shot: 04-kitty-baseline.png)') -f $baselineHits)
    }
    if ($conptyPresent) {
        Invoke-PastePayload $session ((New-KittyCommand) + "`r") 'kitty RGBA transmit+place + CR'
        Start-Sleep -Milliseconds 1800
        $kittyHits = Count-KittyPixels (Shot $session '05-kitty')
        Write-Host "kittyHits=$kittyHits"
        if ($kittyHits -lt 8) {
            $script:Findings.Add("the kitty sequence drew nothing the pixel oracle can find (hits=$kittyHits, want >=8)")
        }
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
    # Restore the owner's text clipboard. Set-Clipboard does NOT throw when
    # it loses a contention race - it reports success with nothing written -
    # so the restore is verified by reading back, with retries, and a
    # persistent failure is a recorded HARNESS error (exit 1), not a silent
    # clean pass with a payload left in the owner's clipboard. When no
    # owner text was read, nothing is written at all: an uncertain state is
    # left untouched rather than confirmed with a wipe.
    if ($hadOwnerText -and $null -ne $ownerText) {
        $restored = $false
        foreach ($attempt in 1..5) {
            try { Set-Clipboard -Value $ownerText } catch { }
            Start-Sleep -Milliseconds (150 * $attempt)
            $check = Get-Clipboard -Raw -ErrorAction SilentlyContinue
            if ($null -ne $check -and "$check" -eq "$ownerText") { $restored = $true; break }
        }
        if (-not $restored) {
            # Append, not replace: a catch above may already have named the
            # run's real failure, and overwriting it here would leave
            # result.json blaming the clipboard for something else.
            $restoreMsg = "the owner's clipboard text could not be restored (contended); a payload may be left in it"
            $harnessError = if ($harnessError) { "$harnessError; $restoreMsg" } else { $restoreMsg }
            Write-Host "HARNESS: $restoreMsg" -ForegroundColor Red
        }
    }
    if ($null -ne $session) { Stop-SeamSession $session }
}

if ((Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)) {
    $script:Findings.Add('crash.log grew during the run')
}

[ordered]@{
    actuation     = 'seam (WINTTY_TEST_SEAM=<session token>); paste via chord, clipboard snapshot/restore around it'
    oscMarker     = $oscMarker
    shellTitle    = $oscTitle
    baselineHits  = $baselineHits
    kittyHits     = $kittyHits
    conptyPresent = $conptyPresent
    findings      = $script:Findings
    harness       = $harnessError
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

if ($script:Findings.Count -gt 0) { exit 2 }
if ($harnessError) { exit 1 }
exit 0
