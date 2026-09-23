#requires -Version 7
<#
    A local pane's pty starts at the pane's own size.

    The shell's first reported size is what it formats startup output at: a
    profile error printed at the wrong width is hard-wrapped with real CRLFs
    and no later resize can reflow it. Windows Terminal does not start a
    terminal's connection until the control has a measured, non-zero size,
    and then creates the pseudoconsole at that size. This harness checks that
    Wintty does the same for local (non-daemon) panes.

    Three kinds of pane per launch:

      launch  the window's first tab, created while the window lays out.
      newtab  a tab opened through the seam (open-profile) once the window
              has settled, the way Ctrl+T or the + button opens one.
      hidden  tabs created in one dispatcher turn behind the active one
              (seed-tabs), the shape a session restore has. They are laid
              out only when first shown.

    The oracle, for every pane: the size the surface was created at, which
    is the size its pty starts with (surface-size: spawnWidthPx/HeightPx and
    spawnCols/Rows), equals the size the pane settles on (widthPx/heightPx,
    cols/rows). Pixels are compared as well as cells, so a creation size a
    few pixels off the laid-out one fails even when both round to the same
    grid: which grid a few pixels land in depends on the window height, and
    the verdict should not. A hidden pane may also have no surface yet
    (live=false): no pty has started, which is correct. Once shown it must
    be live and pass the same check.

    What is recorded but not judged:

      shell   the newtab pane's shell reports its own size (`mode con` to a
              file, run as the profile's command). Whether that report
              comes before or after the first resize is a race the harness
              cannot control, so on a build that starts the pty at the wrong
              size it is red only sometimes. It is data, not the verdict.
      scale   the panel's composition scale. At 1.0 the display-scale half
              of the size calculation is not exercised, and the summary
              says so; the unit tests cover it at 1.5 and 2.0.

    The window's first tab and the hidden quick-terminal window run a silent
    default profile; only the seam-opened tab runs the reporting shell, so
    no report can be mistaken for another shell's.

    Nothing is typed and no OS input is synthesized: the probe runs as a
    profile's command, and the seam only reads, opens, seeds and selects
    tabs. The launch is isolated (private config, private state,
    single-instance off) so it can run beside a Wintty somebody else is
    using.

    Exits 0 on pass, 2 on a product finding, 1 when the harness could not
    run and nothing is known about the product.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$Runs = 3,
    # Tabs seed-tabs adds per launch. The last becomes the active one, so
    # all but one of them are created hidden.
    [int]$SeededTabs = 3
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

# `mode con` labels are localized; the numbers are what matter, and the
# first two are Lines then Columns.
function Read-ModeCon([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $nums = @(Get-Content -LiteralPath $Path | ForEach-Object {
            if ($_ -match ':\s+(\d+)\s*$') { [int]$Matches[1] } })
    if ($nums.Count -lt 2) { return $null }
    return [pscustomobject]@{ Rows = $nums[0]; Cols = $nums[1] }
}

function Read-Size($s, [int]$Index) {
    $r = Invoke-SeamCommand $s @{ op = 'surface-size'; index = $Index }
    foreach ($field in 'live', 'scale') {
        if ($null -eq $r.PSObject.Properties[$field]) { throw "HARNESS: the seam no longer reports '$field'" }
    }
    if ($r.live) {
        foreach ($field in 'spawnCols', 'spawnRows', 'spawnWidthPx', 'spawnHeightPx', 'cols', 'rows', 'widthPx', 'heightPx') {
            if ($null -eq $r.PSObject.Properties[$field]) { throw "HARNESS: the seam no longer reports '$field'" }
        }
        if ($r.spawnCols -eq 0 -or $r.cols -eq 0) { throw "HARNESS: the seam reported an empty grid for tab $Index" }
    }
    return $r
}

# The pane's settled size: read until two reads a second apart agree, so a
# layout pass still in flight is not taken for the answer.
function Get-Settled($s, [int]$Index) {
    $prev = $null
    $r = $null
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $r = Read-Size $s $Index
        if ($r.live -and $prev -and $prev.live -and $prev.widthPx -eq $r.widthPx -and $prev.heightPx -eq $r.heightPx) { break }
        $prev = $r
        Start-Sleep -Milliseconds 1000
    }
    if (-not $r.live) { throw "HARNESS: tab $Index never got a live surface" }
    return $r
}

function Format-Pane($r) {
    return "pty $($r.spawnCols)x$($r.spawnRows) ($($r.spawnWidthPx)x$($r.spawnHeightPx) px), pane $($r.cols)x$($r.rows) ($($r.widthPx)x$($r.heightPx) px)"
}

function Test-Pane([string]$What, $r) {
    if ($r.spawnWidthPx -ne $r.widthPx -or $r.spawnHeightPx -ne $r.heightPx -or
        $r.spawnCols -ne $r.cols -or $r.spawnRows -ne $r.rows) {
        return "${What}: $(Format-Pane $r)"
    }
    return $null
}

function Invoke-Run([int]$N) {
    $probe = Join-Path $OutDir "first-size-$N.txt"
    Remove-Item -LiteralPath $probe -ErrorAction SilentlyContinue
    if ($probe -match '\s') { throw "HARNESS: probe path '$probe' holds a space; cmd would split it" }

    $config = @"
windows-single-instance = false
window-save-state = never
default-profile = idle
profile.idle.name = Idle
profile.idle.command = cmd.exe /d /c ping -n 120 127.0.0.1 > nul
profile.sizeprobe.name = SizeProbe
profile.sizeprobe.command = cmd.exe /d /c mode con > $probe & ping -n 120 127.0.0.1 > nul
"@
    $entry = [ordered]@{
        run = $N; ok = $false; class = ''; error = ''; scale = $null
        launch = ''; newtab = ''; shell = ''; hidden = @()
    }
    $s = $null
    try {
        Assert-NoWinttyFrom -ExePath $ExePath -Context 'seam-initial-size'
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -PrivateStateBase
        $fails = @()

        # launch
        $launch = Get-Settled $s 0
        $entry.scale = [double]$launch.scale
        $entry.launch = Format-Pane $launch
        if ($f = Test-Pane 'launch tab' $launch) { $fails += $f }

        # newtab
        $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'sizeprobe' }
        if (@($opened.state.tabs).Count -ne 2) { throw "HARNESS: open-profile left $(@($opened.state.tabs).Count) tabs, wanted 2" }
        $index = [int]$opened.state.active
        if ($index -eq 0) { throw 'HARNESS: the opened tab is not the active one' }
        $newtab = Get-Settled $s $index
        $entry.newtab = Format-Pane $newtab
        if ($f = Test-Pane 'new tab' $newtab) { $fails += $f }

        # The shell's own report, as data.
        $deadline = (Get-Date).AddSeconds(30)
        $first = $null
        while ((Get-Date) -lt $deadline -and -not ($first = Read-ModeCon $probe)) { Start-Sleep -Milliseconds 200 }
        $entry.shell = if ($first) { "$($first.Cols)x$($first.Rows)" } else { 'no report' }

        # hidden: seed-tabs closes down to one tab, then adds the rest in one
        # turn; the last one added is the active one and the others are
        # created behind it.
        $seeded = Invoke-SeamCommand $s @{ op = 'seed-tabs'; count = $SeededTabs + 1 }
        $tabs = @($seeded.state.tabs)
        if ($tabs.Count -ne $SeededTabs + 1) { throw "HARNESS: seed-tabs left $($tabs.Count) tabs, wanted $($SeededTabs + 1)" }
        $active = [int]$seeded.state.active
        $hiddenIdx = @(1..$SeededTabs | Where-Object { $_ -ne $active })
        if ($active -eq 0) { $hiddenIdx = @(1..$SeededTabs) }
        if ($hiddenIdx.Count -eq 0) { throw 'HARNESS: seed-tabs left no tab behind the active one' }
        # Let the seeded tabs load before reading what they started with.
        Start-Sleep -Seconds 2
        $before = @{}
        foreach ($i in $hiddenIdx) { $before[$i] = Read-Size $s $i }
        foreach ($i in $hiddenIdx) {
            [void](Invoke-SeamCommand $s @{ op = 'select'; index = $i })
            $shown = Get-Settled $s $i
            $pre = $before[$i]
            $preText = if ($pre.live) { "live before shown, pty $($pre.spawnCols)x$($pre.spawnRows)" } else { 'no pty before shown' }
            $entry.hidden += "tab ${i}: $preText; shown: $(Format-Pane $shown)"
            if ($f = Test-Pane "hidden tab $i once shown" $shown) { $fails += $f }
        }

        if ($s.Proc.HasExited) { throw "APP_EXIT: the app exited during run $N (code $($s.Proc.ExitCode))" }
        if ($fails.Count -gt 0) { throw ("PRODUCT_FAIL: " + ($fails -join '; ')) }
        $entry.ok = $true
        Write-Host ("PASS run {0}: launch {1}; new tab {2}; shell {3}; {4}" -f $N, $entry.launch, $entry.newtab,
            $entry.shell, ($entry.hidden -join '; ')) -ForegroundColor Green
    } catch {
        $msg = "$($_.Exception.Message)"
        $entry.error = $msg
        $entry.class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
        Write-Host "FAIL run $N [$($entry.class)]: $msg" -ForegroundColor Red
    } finally {
        if ($null -ne $s) {
            $crash = if ($s.StateBase) { Get-ChildItem -LiteralPath $s.StateBase -Recurse -Filter crash.log -ErrorAction SilentlyContinue } else { @() }
            if (@($crash).Count -gt 0) {
                $entry.ok = $false
                $entry.class = 'product'
                $entry.error += ' | crash.log written during the run'
            }
            # A failed run keeps the app's logs; they are the only record of
            # what it was doing.
            if (-not $entry.ok -and $s.StateBase) {
                Copy-Item -LiteralPath $s.StateBase -Destination (Join-Path $OutDir "state-$N") -Recurse -Force -ErrorAction SilentlyContinue
            }
            Stop-SeamSession $s
            # The next run refuses to start beside this exe, so wait out a
            # slow exit rather than failing the next run on it.
            if ($s.Proc) { [void]$s.Proc.WaitForExit(20000) }
        }
    }
    return [pscustomobject]$entry
}

$results = @(1..$Runs | ForEach-Object { Invoke-Run $_ })
$scales = @($results | Where-Object { $null -ne $_.scale } | ForEach-Object { $_.scale } | Select-Object -Unique)
$scaleCovered = @($scales | Where-Object { $_ -ne 1.0 }).Count -gt 0
[ordered]@{
    runs = $results
    scales = $scales
    scaleCovered = $scaleCovered
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

Write-Host ''
foreach ($r in $results) {
    Write-Host ("run {0}: {1}  launch [{2}]  new tab [{3}]  shell {4}  hidden [{5}]" -f $r.run,
        $(if ($r.ok) { 'PASS' } else { "FAIL ($($r.class))" }), $r.launch, $r.newtab, $r.shell, ($r.hidden -join '; '))
}
if ($scaleCovered) {
    Write-Host ("display scale: {0}" -f ($scales -join ', '))
} else {
    Write-Host ("display scale: {0}; the display-scale half of the size calculation was not exercised here" -f
        $(if ($scales.Count) { $scales -join ', ' } else { 'unknown' }))
}
if (@($results | Where-Object { -not $_.ok -and $_.class -eq 'product' }).Count -gt 0) { exit 2 }
if (@($results | Where-Object { -not $_.ok }).Count -gt 0) { exit 1 }
exit 0
