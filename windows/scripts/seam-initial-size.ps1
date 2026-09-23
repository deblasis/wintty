#requires -Version 7
<#
    A local pane's pty starts at the pane's own size.

    The shell's first reported size is what it formats startup output at: a
    profile error printed at the wrong width is hard-wrapped with real CRLFs
    and no later resize can reflow it. Windows Terminal measures the pane
    first and creates the pseudoconsole at that size. This harness checks
    that Wintty does the same for a local (non-daemon) pane.

    Two panes per launch:

      launch  the window's first tab, created while the window lays out.
      newtab  a tab opened through the seam (open-profile) once the window
              has settled, the way Ctrl+T or the + button opens one.

    For each, the grid the surface was created at (the size its pty is
    created with: surface-size's spawnCols/spawnRows) must equal the grid the
    pane settles on (cols/rows). That check is deterministic.

    The newtab pane also runs a shell that reports its own size: the
    profile's command is `mode con` redirected to a file, run the moment the
    shell starts, then a `ping` to keep the pane alive. Its first report must
    equal the pane's grid too. That is what a user sees, and it depends on
    whether the shell asks before the first resize lands, so on a build that
    creates the pty at the wrong size it fails only some of the time.

    Only the newtab pane runs the reporting shell. The window's first tab
    and the hidden quick-terminal window both run the default profile, and
    two shells writing reports would leave no way to tell whose is whose.

    Nothing is typed and no OS input is synthesized: the probe runs as a
    profile's command, and the seam only reads and opens a tab. The launch
    is isolated (private config, private state, single-instance off) so it
    can run beside a Wintty somebody else is using.

    Exits 0 on pass, 2 on a product finding, 1 when the harness could not
    run and nothing is known about the product.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # Launches to run. The shell's report is timing-shaped, so one launch is
    # a sample and several are a measurement.
    [int]$Runs = 3
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

# The pane's settled grid: read until two reads a second apart agree, so a
# layout pass still in flight is not taken for the answer.
function Get-SettledGrid($s, [int]$Index) {
    $prev = $null
    $grid = $null
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $grid = Invoke-SeamCommand $s @{ op = 'surface-size'; index = $Index }
        if ($prev -and $prev.cols -eq $grid.cols -and $prev.rows -eq $grid.rows) { break }
        $prev = $grid
        Start-Sleep -Milliseconds 1000
    }
    foreach ($field in 'spawnCols', 'spawnRows', 'cols', 'rows') {
        if ($null -eq $grid.PSObject.Properties[$field]) {
            throw "HARNESS: the seam no longer reports '$field'"
        }
    }
    if ($grid.spawnCols -eq 0 -or $grid.cols -eq 0) { throw "HARNESS: the seam reported an empty grid for tab $Index" }
    return $grid
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
        run = $N; ok = $false; class = ''; error = ''
        launchPty = ''; launchPane = ''; newtabPty = ''; newtabShell = ''; newtabPane = ''
    }
    $s = $null
    try {
        Assert-NoWinttyFrom -ExePath $ExePath -Context 'seam-initial-size'
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -PrivateStateBase
        $fails = @()

        $launch = Get-SettledGrid $s 0
        $entry.launchPty = "$($launch.spawnCols)x$($launch.spawnRows)"
        $entry.launchPane = "$($launch.cols)x$($launch.rows)"
        if ($launch.spawnCols -ne $launch.cols -or $launch.spawnRows -ne $launch.rows) {
            $fails += "launch tab: the pty was created at $($entry.launchPty), the pane's grid is $($entry.launchPane)"
        }

        $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'sizeprobe' }
        $tabs = @($opened.state.tabs)
        if ($tabs.Count -ne 2) { throw "HARNESS: open-profile left $($tabs.Count) tabs, wanted 2" }
        $index = [int]$opened.state.active
        if ($index -eq 0) { throw 'HARNESS: the opened tab is not the active one' }

        $deadline = (Get-Date).AddSeconds(30)
        $first = $null
        while ((Get-Date) -lt $deadline -and -not ($first = Read-ModeCon $probe)) {
            Start-Sleep -Milliseconds 200
        }
        if (-not $first) { throw "HARNESS: the probe shell never wrote its size to $probe" }

        $grid = Get-SettledGrid $s $index
        $entry.newtabPty = "$($grid.spawnCols)x$($grid.spawnRows)"
        $entry.newtabShell = "$($first.Cols)x$($first.Rows)"
        $entry.newtabPane = "$($grid.cols)x$($grid.rows)"
        if ($grid.spawnCols -ne $grid.cols -or $grid.spawnRows -ne $grid.rows) {
            $fails += "new tab: the pty was created at $($entry.newtabPty), the pane's grid is $($entry.newtabPane)"
        }
        if ($first.Cols -ne $grid.cols -or $first.Rows -ne $grid.rows) {
            $fails += "new tab: the shell's first size was $($entry.newtabShell), the pane's grid is $($entry.newtabPane)"
        }

        if ($s.Proc.HasExited) { throw "APP_EXIT: the app exited during run $N (code $($s.Proc.ExitCode))" }
        if ($fails.Count -gt 0) {
            throw ("PRODUCT_FAIL: " + ($fails -join '; ') +
                " (cell $($grid.cellWidthPx)x$($grid.cellHeightPx) px)")
        }
        $entry.ok = $true
        Write-Host ("PASS run {0}: launch pty {1} pane {2}; new tab pty {3} shell {4} pane {5}" -f $N,
            $entry.launchPty, $entry.launchPane, $entry.newtabPty, $entry.newtabShell, $entry.newtabPane) -ForegroundColor Green
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
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

Write-Host ''
foreach ($r in $results) {
    Write-Host ("run {0}: launch pty {1,-8} pane {2,-8} | new tab pty {3,-8} shell {4,-8} pane {5,-8} {6}" -f $r.run,
        $r.launchPty, $r.launchPane, $r.newtabPty, $r.newtabShell, $r.newtabPane,
        $(if ($r.ok) { 'PASS' } else { "FAIL ($($r.class))" }))
}
if (@($results | Where-Object { -not $_.ok -and $_.class -eq 'product' }).Count -gt 0) { exit 2 }
if (@($results | Where-Object { -not $_.ok }).Count -gt 0) { exit 1 }
exit 0
