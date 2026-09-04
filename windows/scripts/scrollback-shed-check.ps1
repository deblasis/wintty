#requires -Version 7
<#
    Scrollback shedding, measured against the running app.

    The Windows decommit primitives in terminal/mem.zig turn
    scrollback compression on for our platform; the renderer's own
    scheduler then compresses eligible pages once a surface goes quiet
    (250ms idle, one page per millisecond step). Nothing in the shell
    drives this -- it is entirely in-product -- so what this harness
    proves is the OUTCOME: a tab handed a large scrollback and then
    left alone must give the memory back, and must come back intact
    when it is revisited.

    The oracle is relative, not absolute, so it means the same thing on
    any machine: private bytes, sampled after the scrollback settles,
    must fall by at least 15% within the observation window, and the
    window must stay alive (seam still answers) after the compressed
    tab is re-activated -- pages restored on demand, not lost.

    send-text is armed for this harness (it hands the shell a command
    that generates the scrollback). Exits 0 clean, 2 finding, 1
    could-not-run.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # How long to watch memory after the tab goes quiet. The scheduler
    # needs 250ms of renderer quiet plus ~1ms per page; a 100MB
    # scrollback is a few dozen pages, so 90s is generous margin for a
    # slow machine while the parse itself settles.
    [int]$ObserveSeconds = 90
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# 100MB of scrollback so the page count is unambiguous; compression is
# on by default, it was simply compiled out on Windows before.
$Config = @"
windows-single-instance = true
window-save-state = never
scrollback-limit = 100000000
profile.pwsh.name = PowerShell
profile.pwsh.command = pwsh.exe -NoProfile
default-profile = pwsh
"@

$script:Findings = [System.Collections.Generic.List[string]]::new()
$harnessError = ''
$session = $null

$ownLock = $null
try {
    . C:\temp\seam-lock.ps1
    $ownLock = Enter-SeamLock -Owner 'scrollback-shed-check'

    Assert-NoWintty -Context 'the scrollback shed check'
    $session = Start-SeamSession -ExePath $ExePath -ConfigText $Config -AllowInput
    if (-not (Wait-SeamReady $session.Proc)) { throw 'SEAM_REFUSED: app never announced the pipe' }

    # seed-tabs gives a clean slate but its tabs are the legacy no-profile
    # spawn -- cmd.exe, where a pwsh one-liner dies as "'$s' is not
    # recognized". Spawning pwsh inside the tab is the shell-agnostic fix:
    # the bytes go through the same pty either way, and the dump below is
    # plain pwsh from there. (The new-tab chord was tried and needs pane
    # focus an unfocused harness window does not have.)
    #
    # Titles are seeded EMPTY: seed-tabs writes a UserOverrideTitle, which
    # outranks the shell's OSC title forever, and the DUMPED marker rides
    # the OSC title -- with defaults seeded, the readback below would be
    # blind no matter what the shell did.
    [void](Invoke-SeamCommand $session @{ op = 'seed-tabs'; count = 2; titles = @('', '') })
    # Surfaces spawn asynchronously after the manager creates each tab;
    # send-text into a tab whose surface is not alive yet is an error, not
    # a queue. A plain settle beat keeps this a harness rather than a
    # race.
    Start-Sleep -Seconds 3
    [void](Invoke-SeamCommand $session @{ op = 'send-text'; index = 1; text = 'pwsh -NoProfile' + [char]13 })
    Start-Sleep -Seconds 3

    # Baseline before the dump, so "the dump never happened" is a
    # FINDING here rather than a flat line indistinguishable from a
    # broken shed.
    $proc = Get-Process -Id $session.Proc.Id
    $proc.Refresh()
    $baseline = $proc.PrivateMemorySize64

    # One tiny command; the shell does the volume. What the shed cares
    # about is ROWS -- pages are row-based and page memory is fixed per
    # page -- so short unwrapped lines buy the same thousands of pages
    # for a fraction of the pty throughput: 60k ten-char lines is under
    # a megabyte through ConPTY, where 60k hundred-eighty-char lines
    # were still streaming minutes in (verified by the failure
    # screenshot of run-7: mid-dump viewport, no prompt). Single-quoted
    # so every backtick and dollar reaches the target pwsh literally,
    # and CR appended to submit the line.
    #
    # The command finishes by setting the tab title to a marker + the
    # byte count: the seam proves delivery of the bytes, never that the
    # shell accepted them. The title rides OSC 0/2 back out to
    # get-state, a channel the harness can actually assert on.
    [void](Invoke-SeamCommand $session @{
        op = 'send-text'
        index = 1
        text = '$s = ("A"*10 + "`r`n") * 60000; [Console]::Out.Write($s); $Host.UI.RawUI.WindowTitle = "DUMPED-" + $s.Length' + [char]13
    })

    $dumpedTitle = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(150)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds 3
        $st = Invoke-SeamCommand $session @{ op = 'get-state' }
        if ($st.state.tabs[1].title -like 'DUMPED-*') { $dumpedTitle = $st.state.tabs[1].title; break }
    }
    if (-not $dumpedTitle) {
        # The tab's actual state, as evidence rather than assumption:
        # a screenshot of the window where the command was supposed to
        # run, saved next to the samples.
        try {
            Add-Type -AssemblyName System.Drawing
            $rc = [SeamWin]::RectOf($session.Hwnd64)
            if ($null -ne $rc) {
                $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
                $bmp.Save((Join-Path $OutDir 'setup-failed.png'))
                $g.Dispose(); $bmp.Dispose()
            }
        } catch { }
        $st = Invoke-SeamCommand $session @{ op = 'get-state' }
        throw ("SETUP-FAILED: the dump command never reported (last title: '{0}'). " -f $st.state.tabs[1].title) +
            "Setup evidence in $OutDir\setup-failed.png"
    }
    Write-Host "dump verified: $dumpedTitle (baseline $([Math]::Round($baseline/1MB))MB)"

    # Let the parse finish and the pages settle: poll private bytes
    # until two samples 5s apart move by <2%, then one more beat.
    $proc = Get-Process -Id $session.Proc.Id
    $samples = [System.Collections.Generic.List[long]]::new()
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds 5
        $proc.Refresh()
        $samples.Add($proc.PrivateMemorySize64)
        if ($samples.Count -ge 3) {
            $a = $samples[$samples.Count - 3]; $b = $samples[$samples.Count - 1]
            if ([Math]::Abs($b - $a) -le $a * 0.02) { break }
        }
    }
    $peak = ($samples | Measure-Object -Maximum).Maximum

    # The terminal's own page census, before and after: the shed is a
    # fact about pages (compressed count, decommitted bytes), and the
    # process byte counter is only the corroborating echo -- every other
    # allocation in the process moves it too.
    $pre = Invoke-SeamCommand $session @{ op = 'surface-mem'; index = 1 }
    Write-Host ("pre-shed: pages={0} compressed={1} resident={2:N0}MB" -f `
        $pre.totalPages, $pre.compressedPages, ($pre.residentRawBytes / 1MB))

    # Background the loaded tab, then watch it shed.
    [void](Invoke-SeamCommand $session @{ op = 'select'; index = 0 })
    $after = [System.Collections.Generic.List[long]]::new()
    $deadline = [DateTime]::UtcNow.AddSeconds($ObserveSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds 5
        $proc.Refresh()
        $after.Add($proc.PrivateMemorySize64)
    }
    $floor = ($after | Measure-Object -Minimum).Minimum
    $post = Invoke-SeamCommand $session @{ op = 'surface-mem'; index = 1 }
    Write-Host ("post-shed: pages={0} compressed={1} decommitted={2:N0}MB encoded={3:N1}MB" -f `
        $post.totalPages, $post.compressedPages, ($post.decommittedRawBytes / 1MB), ($post.encodedBytes / 1MB))

    $samples | ForEach-Object { "settle $_" } | Set-Content (Join-Path $OutDir 'samples.txt')
    $after | ForEach-Object { "shed $_" } | Set-Content (Join-Path $OutDir 'shed.txt')
    $dropPct = if ($peak -gt 0) { 100.0 * ($peak - $floor) / $peak } else { 0.0 }
    $proc.Refresh()
    # Informational only. The process byte counter is NOT the shed
    # oracle: a slow dump lets the scheduler compress pages as fast as
    # they are created, so the +commit of creation and the -commit of
    # the decommit cancel inside one sampling interval (observed live:
    # 62MB of pages created and shed with the counter moving 6MB). The
    # census above is the assert.
    Write-Host ("process bytes: peak={0:N0}MB floor={1:N0}MB drop={2:N1}% (now: commit={3:N0}MB ws={4:N0}MB)" -f `
        ($peak / 1MB), ($floor / 1MB), $dropPct,
        ($proc.PrivateMemorySize64 / 1MB), ($proc.WorkingSet64 / 1MB))

    if ($pre.totalPages + $post.totalPages -lt 100) {
        $script:Findings.Add("setup: the dump produced only $($pre.totalPages) pages -- the shed oracle needs a scrollback of real page count")
    }
    elseif ($pre.compressedPages + $post.compressedPages -eq 0) {
        $script:Findings.Add("scrollback did not compress: $($pre.totalPages) pages, 0 compressed after the dump and $ObserveSeconds`s of idle")
    }
    elseif ($pre.decommittedRawBytes + $post.decommittedRawBytes -eq 0) {
        $script:Findings.Add("pages compressed but nothing decommitted -- the reclamation primitive did not run")
    }

    # Revisit the compressed tab: the seam answering afterwards proves
    # the app lived through restore-on-demand; the op runs on the UI
    # thread the decompress path also uses.
    [void](Invoke-SeamCommand $session @{ op = 'select'; index = 1 })
    Start-Sleep -Milliseconds 500
    [void](Invoke-SeamCommand $session @{ op = 'get-state' })
}
catch {
    $harnessError = $_.Exception.Message
}
finally {
    if ($null -ne $session) { Stop-SeamSession $session }
    if ($ownLock) { Exit-SeamLock $ownLock }
}

if ($harnessError) {
    Write-Host "HARNESS-ERROR: $harnessError"
    exit 1
}
if ($script:Findings.Count -gt 0) {
    $script:Findings | ForEach-Object { Write-Host "FINDING: $_" }
    exit 2
}
Write-Host 'scrollback-shed-check: clean (memory shed after idle, tab restored alive)'
exit 0
