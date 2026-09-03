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

    The clipboard is the owner's real one (paste has to come from
    somewhere and there is no seam op for it): the previous TEXT is
    snapshotted and restored after; non-text formats are lost, which is
    stated here rather than hidden.

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
profile.pwsh.command = pwsh.exe
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
    # header, not hidden.
    $hadOwnerText = $null -ne (Get-Clipboard)
    if ($hadOwnerText) { $ownerText = Get-Clipboard -Raw }

    # The command sets the shell-reported title to the CJK marker. The
    # backtick escapes are the SHELL's (single-quoted here so they survive
    # to pwsh); the trailing CR submits the pasted line.
    Set-Clipboard -Value ('Write-Host "`e]0;' + $marker + '`a"' + "`r")
    Write-Host 'clipboard set (CJK OSC title command + CR)'

    [void](Invoke-SeamCommand $session @{ op = 'focus'; target = 'frame' })
    $r = Invoke-SeamCommand $session @{ op = 'chord'; key = 0x56; ctrl = $true; shift = $true }
    if (-not $r.dispatched) {
        throw ("HARVEST_MISS: the paste chord was not dispatched (focus was '{0}')" -f $r.focus)
    }
    Shot $session '02-pasted'

    # Poll the shell-reported title: the OSC only lands once the shell has
    # parsed and run the pasted line.
    $shellTitle = ''
    $deadline = (Get-Date).AddSeconds(10)
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
        $script:Findings.Add(("the CJK marker did not survive the paste round trip: shellTitle='$shellTitle'"))
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
    # Restore the owner's text clipboard AFTER the run; non-text formats
    # are gone either way.
    try {
        if ($hadOwnerText -and $null -ne $ownerText) { Set-Clipboard -Value $ownerText }
        elseif (-not $hadOwnerText) { Set-Clipboard -Value '' }
    } catch { Write-Host "HARNESS: clipboard restore failed: $($_.Exception.Message)" }
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
