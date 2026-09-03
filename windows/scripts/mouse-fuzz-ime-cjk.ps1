#requires -Version 7
<#
    CJK + supplementary-plane content through the real paste path,
    seam-actuated (#930).

    The old header said "IME composition not implemented" while the code
    below it set $imeCompositionImplemented = $true - both halves wrong,
    and neither was what ran. What actually runs, here as before: CJK and
    emoji bytes (BMP + supplementary plane) travel clipboard -> the paste
    action -> ConPTY UTF-8 -> the shell. TSF composition wiring EXISTS in
    the product and this harness has never exercised it; that stays true
    and is no longer claimed either way.

    The paste is chord{0x56,ctrl,shift} - Ctrl+Shift+V through the
    window's real routing into the same paste_from_clipboard the palette
    item dispatches. The verdict was two assigned-trues; the oracle now is
    the OSC title round trip: the pasted command sets the shell-reported
    title to a CJK marker, read back through tab-labels' shellTitle. For
    that to fail, something on the whole path - clipboard read, paste
    dispatch, UTF-8 encoding, the shell parsing the escape, the OSC
    report - had to break.

    What this does NOT gate: the grid's rendering of these bytes - cell
    widths, shaping, tofu - remains eyeball-only in the shots; the title
    channel is the cheapest sound read-back the seam offers.

    The clipboard is the owner's real one (paste has to come from
    somewhere and there is no seam op for it): the previous TEXT is
    snapshotted with backoff and restored with a verified read-back, and a
    restore that cannot be confirmed is a recorded harness error, not a
    silent clean pass. Non-text formats are lost, and a snapshot read that
    stays empty leaves the clipboard untouched rather than wiping it -
    both stated here rather than hidden. The shell runs with -NoProfile so
    the owner's prompt (which may set titles of its own) cannot race the
    marker.

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
clipboard-paste-protection = false
profile.pwsh.name = PowerShell
profile.pwsh.command = pwsh.exe -NoProfile
default-profile = pwsh
'@

# The marker carries BMP CJK and a supplementary-plane emoji: the two
# halves of the UTF-16 encoding path.
$marker = 'CJK-FUZZ-日中文🚀'
$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$script:Findings = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null
$ownerText = $null
$hadOwnerText = $false

function Shot($Session, [string]$Name) {
    $rc = [SeamWin]::RectOf($Session.Hwnd64)
    if ($null -eq $rc) { return }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir "shots\$Name.png"))
    $g.Dispose(); $bmp.Dispose()
}

try {
    Assert-NoWintty -Context 'The ime-cjk harness'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config
    $hwnd64 = [int64]$session.Hwnd64
    Write-Host "hwnd=$hwnd64 pid=$($session.Proc.Id)"
    Shot $session '00-launch'

    # The paste needs real clipboard content; snapshot the owner's TEXT and
    # restore it in the finally. Non-text formats are lost - stated in the
    # header, not hidden. Get-Clipboard returns EMPTY both for no text and
    # for a read that lost a contention race (clipboard managers re-open on
    # change), so the read is retried with real backoff before being
    # believed; and the finally never writes when unsure - an empty
    # clipboard is left alone rather than confirmed with a wipe.
    $ownerText = $null
    $hadOwnerText = $false
    foreach ($attempt in 1..5) {
        $probe = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($null -ne $probe -and "$probe" -ne '') { $ownerText = $probe; $hadOwnerText = $true; break }
        Start-Sleep -Milliseconds (150 * $attempt)
    }
    if (-not $hadOwnerText) {
        Write-Host 'HARNESS: no owner clipboard text read (empty, non-text, or contended); leaving it untouched'
    }

    # The command sets the shell-reported title to the CJK marker. The
    # backtick escapes are the SHELL's (single-quoted here so they survive
    # to pwsh); the trailing CR submits the pasted line - which only works
    # because the paste arrives UNBRACKETED: core wraps pastes in
    # ESC[200~..201~ when the surface has mode 2004 set, and PSReadLine
    # does not execute a trailing newline inside a bracketed paste. That
    # mode is evidently not reaching core through ConPTY today; if
    # passthrough ever changes that, this harness goes red and this
    # comment is why.
    # The settle delay is deliberate: clipboard listeners react to the
    # write milliseconds after it lands, and the product's paste read
    # treats a locked clipboard as "no text" - firing the chord into that
    # window would misread contention as a CJK failure.
    Set-Clipboard -Value ('Write-Host "`e]0;' + $marker + '`a"' + "`r")
    Write-Host 'clipboard set (CJK OSC title command + CR)'
    Start-Sleep -Milliseconds 400

    [void](Invoke-SeamCommand $session @{ op = 'focus'; target = 'frame' })
    $r = Invoke-SeamCommand $session @{ op = 'chord'; key = 0x56; ctrl = $true; shift = $true }
    if (-not $r.dispatched) {
        throw ("HARVEST_MISS: the paste chord was not dispatched (focus was '{0}')" -f $r.focus)
    }
    Shot $session '02-pasted'

    # Poll the shell-reported title: the OSC only lands once the shell has
    # parsed and run the pasted line.
    # 15s: the budget starts at the chord and must absorb a cold pwsh
    # start, profile load, and the queued paste - not just the OSC itself.
    $shellTitle = ''
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $labels = Invoke-SeamCommand $session @{ op = 'tab-labels' }
        $active = @($labels.labels)[0]
        $shellTitle = "$($active.shellTitle)"
        if ($shellTitle -match 'CJK-FUZZ') { break }
        Start-Sleep -Milliseconds 400
    }
    Write-Host "shellTitle=$shellTitle"
    Shot $session '03-cjk-title'
    if ($shellTitle -notmatch '日中文' -or $shellTitle -notmatch '🚀') {
        $script:Findings.Add(("the CJK marker did not arrive in the shell-reported title: '$shellTitle' " +
            '(clipboard read, paste dispatch, shell start, or title plumbing)'))
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
    # clean pass with the marker left in the owner's clipboard. When no
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
            $harnessError = "the owner's clipboard text could not be restored (contended); the marker may be left in it"
            Write-Host "HARNESS: $harnessError" -ForegroundColor Red
        }
    }
    if ($null -ne $session) { Stop-SeamSession $session }
}

if ((Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)) {
    $script:Findings.Add('crash.log grew during the run')
}

[ordered]@{
    actuation  = 'seam (WINTTY_TEST_SEAM=<session token>); paste via chord, clipboard snapshot/restore around it'
    marker     = $marker
    shellTitle = $shellTitle
    findings   = $script:Findings
    harness    = $harnessError
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

if ($script:Findings.Count -gt 0) { exit 2 }
if ($harnessError) { exit 1 }
exit 0
