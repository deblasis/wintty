#requires -Version 7
<#
    A pane is born with a sane grid, and its pty starts at that grid.

    Two oracles, two issues. #1159: the size a surface was created at,
    which is the size its pty starts with (surface-size:
    spawnWidthPx/HeightPx and spawnCols/Rows), must equal the size the
    pane settles on (widthPx/heightPx, cols/rows). Pixels are compared as
    well as cells, so a creation size a few pixels off the laid-out one
    fails even when both round to the same grid. #1262: equality is not
    sanity. A pane born AT one column and STAYING there satisfies every
    equality (spawn == settled == degenerate), so every newborn is also
    held to a floor and to the one-char wrap signature: the grid the pane
    was born with must be at least MinCols x MinRows (the founder's
    proposed floor, invariants.md I-1), and no pane may show the
    one-character-per-line wrap fingerprint while its grid is wider than
    one column (I-3) - the exact shape a degenerate birth leaves on
    screen after any later resize, because hard-wrapped startup output
    cannot reflow.

    Birth kinds, each born through the real funnel and each judged by
    both oracles:

      launch  the window's first tab, created while the window lays out.
      newtab  a tab opened through the seam (open-profile) once the
              window has settled, the way Ctrl+T or the + button opens
              one. The shell's own first size report (`mode con` to a
              file, run as the profile's command) is a second oracle
              here: on a build that starts the pty at the pane's size the
              report always agrees with surface-size.
      split   panes born from SplitVertical and SplitHorizontal through
              the chord's own dispatch (seam split). The newborn leaf
              becomes the active one, so surface-size reads it directly;
              screen-text reads its own grid, not the sibling's.
      hidden  tabs created in one dispatcher turn behind the active one
              (seed-tabs), the shape a session restore has. They are laid
              out only when first shown. A hidden pane may also have no
              surface yet (live=false): no pty has started, which is
              correct. Once shown it must be live and pass both oracles.
      profile every profile the registry resolved on this machine (seam
              profiles op), opened as a new tab in run 1: the staged
              synthetic ones plus everything discovery found. The
              launch/newtab/split kinds above cover the staged set every
              run; the sweep covers the machine's real shells, which is
              the "every profile" question a hardcoded id list cannot
              answer.

    Every pane program prints a birth marker first (PANE-BIRTH-...), so
    the first-render wait is honest on both shapes of grid: in a sane one
    the marker arrives as a line, in a degenerate one as its vertical
    wrap, and a pane whose program output never reaches the screen at all
    is itself a finding, not a skip.

    What is recorded but not judged:

      shell   the split newborn's own `mode con` report (the probe file
              is rewritten by the inherited command). Whether it lands
              before the harness reads is a race, so it is data.
      scale   the panel's composition scale. At 1.0 the display-scale half
              of the size calculation is not exercised, and the summary
              says so; the unit tests cover it at 1.5 and 2.0.

    Per-scenario rows land in results.json ({name, ok, class, detail}),
    one row per scenario per run, and the summary counts them: a run
    where a scenario never applied is visible as a missing row, and a
    sweep that opened zero profiles is a refused verdict upstream, not a
    green here.

    Nothing is typed and no OS input is synthesized: the probe runs as a
    profile's command, and the seam only reads, opens, seeds, selects and
    splits. The launch is isolated (private config, private state,
    single-instance off) so it can run beside a Wintty somebody else is
    using.

    Exits 0 on pass, 2 on a product finding, 1 when the harness could
    not run and nothing is known about the product.
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

# The founder's floor (invariants.md I-1, proposed MIN_COLS = MIN_ROWS =
# 2; #1262 shipped cols == 1). One edit here re-floors every birth
# assertion in this harness.
$MinCols = 2
$MinRows = 2
# I-3's signature length: this many consecutive one-character viewport
# lines in a pane wider than one column is the #1262 fingerprint.
$WrapRunLimit = 10

# Birth markers: long enough that a one-column birth wraps them into far
# more one-char lines than the signature needs.
$MarkerLaunch = 'PANE-BIRTH-LAUNCH-MARKER-1234567890'
$MarkerNewTab = 'PANE-BIRTH-NEWTAB-MARKER-1234567890'
$MarkerSplit = 'PANE-BIRTH-SPLIT-MARKER-1234567890'

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
    }
    return $r
}

# A leaf's own viewport, off the cell grid. leaf=-1 reads the active
# leaf, which after a split is the newborn.
function Read-Screen($s, [int]$Index, [int]$Leaf) {
    return Invoke-SeamCommand $s @{ op = 'screen-text'; index = $Index; leaf = $Leaf }
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

function Format-Screen([string]$Text) {
    return ((($Text -split "`n") | Where-Object { $_.TrimEnd() -ne '' } | Select-Object -First 6) -join ' | ')
}

# The #1159 equality oracle: the pty started at the pane's size.
function Test-Pane([string]$What, $r) {
    if ($r.spawnWidthPx -ne $r.widthPx -or $r.spawnHeightPx -ne $r.heightPx -or
        $r.spawnCols -ne $r.cols -or $r.spawnRows -ne $r.rows) {
        return "${What}: $(Format-Pane $r)"
    }
    return $null
}

# The longest run of consecutive one-character lines in a screen.
function Count-OneCharRun([string]$Text) {
    $run = 0
    $worst = 0
    foreach ($line in ($Text -split "`n")) {
        if ($line.TrimEnd().Length -eq 1) { $run++; if ($run -gt $worst) { $worst = $run } }
        else { $run = 0 }
    }
    return $worst
}

# The I-3 signature: one-character-per-line wrap while the grid is wider
# than one column. This is a signature detector, not a wrapping
# assertion; cols <= 1 is the floor's finding, not this one.
function Test-OneCharWrap([string]$What, [string]$Text, [int]$Cols) {
    if ($Cols -le 1) { return $null }
    $worst = Count-OneCharRun $Text
    if ($worst -ge $WrapRunLimit) {
        return "${What}: ${worst} consecutive one-character lines at cols ${Cols}: the one-char-per-line wrap signature (I-3)"
    }
    return $null
}

# Both oracles on one newborn: the floor (settled AND creation size), the
# equality, and the wrap signature off its own screen.
function Test-Newborn([string]$What, $r, [string]$ScreenText) {
    $fails = @()
    if ([int]$r.cols -lt $MinCols -or [int]$r.rows -lt $MinRows) {
        $fails += "${What}: settled at $($r.cols)x$($r.rows), floor is ${MinCols}x${MinRows} (I-1)"
    }
    if ([int]$r.spawnCols -lt $MinCols -or [int]$r.spawnRows -lt $MinRows) {
        $fails += "${What}: born at $($r.spawnCols)x$($r.spawnRows), floor is ${MinCols}x${MinRows} (I-1, creation size)"
    }
    if ($f = Test-Pane $What $r) { $fails += $f }
    if ($f = Test-OneCharWrap $What $ScreenText ([int]$r.cols)) { $fails += $f }
    return $fails
}

# Wait until the pane's own program output is on screen, in either shape
# a grid can hold it: the marker as a line (sane grid) or its vertical
# wrap (the #1262 shape - at least 8 one-char lines means output landed
# and wrapped). $null when nothing arrived inside the budget.
function Wait-PaneOutput($s, [int]$Index, [int]$Leaf, [string]$Marker, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $screen = Read-Screen $s $Index $Leaf
        if ($screen.text -like "*$Marker*") { return $screen }
        if ((Count-OneCharRun $screen.text) -ge 8) { return $screen }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

$script:Results = [System.Collections.Generic.List[object]]::new()

function Add-Result([string]$Name, [bool]$Ok, [string]$Class, [string]$Detail) {
    $script:Results.Add([pscustomobject]@{ name = $Name; ok = $Ok; class = $Class; detail = $Detail })
    if ($Ok) { Write-Host "PASS $Name $Detail" -ForegroundColor Green }
    else { Write-Host "FAIL $Name [$Class] $Detail" -ForegroundColor Red }
}

# One scenario body to a row: a throw inside is classified, never silent,
# and never eats the rest of the run.
function Invoke-Scenario([string]$Name, [scriptblock]$Body) {
    if ($s -and $s.Proc.HasExited) {
        Add-Result $Name $false 'product' "APP_EXIT: the app exited (code $($s.Proc.ExitCode)) before $Name"
        return
    }
    try {
        $detail = & $Body
        Add-Result $Name $true 'product' "$detail"
    }
    catch {
        $msg = "$($_.Exception.Message)"
        $class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
        Add-Result $Name $false $class $msg
    }
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
profile.idle.command = cmd.exe /d /c echo $MarkerLaunch & ping -n 120 127.0.0.1 > nul
profile.sizeprobe.name = SizeProbe
profile.sizeprobe.command = cmd.exe /d /c mode con > $probe & echo $MarkerNewTab & ping -n 120 127.0.0.1 > nul
profile.splitprobe.name = SplitProbe
profile.splitprobe.command = cmd.exe /d /c echo $MarkerSplit & ping -n 120 127.0.0.1 > nul
"@
    $entry = [ordered]@{
        run = $N; ok = $false; class = ''; error = ''; scale = $null
        launch = ''; newtab = ''; shell = ''; splitVertical = ''; splitHorizontal = ''; hidden = @(); sweep = @()
    }
    $s = $null
    try {
        Assert-NoWinttyFrom -ExePath $ExePath -Context 'seam-initial-size'
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -PrivateStateBase

        # launch: the window's first tab.
        Invoke-Scenario 'launch-floor' {
            $launch = Get-Settled $s 0
            $entry.scale = [double]$launch.scale
            $entry.launch = Format-Pane $launch
            $screen = Wait-PaneOutput $s 0 -1 $MarkerLaunch 20
            if ($null -eq $screen) {
                throw "PRODUCT_FAIL: the launch pane never showed its program output; screen: $(Format-Screen (Read-Screen $s 0 -1).text)"
            }
            $fails = @(Test-Newborn 'launch tab' $launch $screen.text)
            if ($fails.Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($fails -join '; ')) }
            "$(Format-Pane $launch); screen cols $($screen.cols) wraprun $(Count-OneCharRun $screen.text)"
        }

        # newtab: through the profile funnel, plus the shell's own report
        # as a second oracle (on a sane build it always agrees).
        Invoke-Scenario 'newtab-floor' {
            $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'sizeprobe' }
            if (@($opened.state.tabs).Count -ne 2) { throw "HARNESS: open-profile left $(@($opened.state.tabs).Count) tabs, wanted 2" }
            $index = [int]$opened.state.active
            if ($index -eq 0) { throw 'HARNESS: the opened tab is not the active one' }
            $newtab = Get-Settled $s $index
            $entry.newtab = Format-Pane $newtab
            $screen = Wait-PaneOutput $s $index -1 $MarkerNewTab 20
            if ($null -eq $screen) {
                throw "PRODUCT_FAIL: the new tab never showed its program output; screen: $(Format-Screen (Read-Screen $s $index -1).text)"
            }
            $deadline = (Get-Date).AddSeconds(30)
            $first = $null
            while ((Get-Date) -lt $deadline -and -not ($first = Read-ModeCon $probe)) { Start-Sleep -Milliseconds 200 }
            $entry.shell = if ($first) { "$($first.Cols)x$($first.Rows)" } else { 'no report' }
            $fails = @(Test-Newborn 'new tab' $newtab $screen.text)
            if ($first) {
                if ($first.Cols -ne [int]$newtab.cols -or $first.Rows -ne [int]$newtab.rows) {
                    $fails += "new tab: the shell itself reports $($first.Cols)x$($first.Rows), surface-size says $($newtab.cols)x$($newtab.rows)"
                }
            }
            else {
                $fails += 'new tab: the shell never wrote its own size report'
            }
            if ($fails.Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($fails -join '; ')) }
            "$(Format-Pane $newtab); shell reports the same; screen cols $($screen.cols) wraprun $(Count-OneCharRun $screen.text)"
        }

        # splits: both orientations, on a tab of their own, through the
        # chord's own dispatch. The newborn leaf becomes the active one.
        Invoke-Scenario 'split-vertical-floor' {
            $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'splitprobe' }
            $index = [int]$opened.state.active
            [void](Wait-PaneOutput $s $index -1 $MarkerSplit 20)
            $split = Invoke-SeamCommand $s @{ op = 'split'; orientation = 'vertical' }
            $tab = @($split.state.tabs)[$index]
            if ([int]$tab.leaves -lt 2) { throw "HARNESS: the vertical split left the tab at $($tab.leaves) leaf/leaves, wanted >= 2" }
            $newborn = Get-Settled $s $index
            $screen = Wait-PaneOutput $s $index -1 $MarkerSplit 20
            if ($null -eq $screen) {
                throw "PRODUCT_FAIL: the split newborn never showed its program output; screen: $(Format-Screen (Read-Screen $s $index -1).text)"
            }
            $what = "vertical split newborn (leaf $($tab.activeLeaf))"
            $fails = @(Test-Newborn $what $newborn $screen.text)
            $entry.splitVertical = Format-Pane $newborn
            if ($fails.Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($fails -join '; ')) }
            "leaf $($tab.activeLeaf) of $($tab.leaves): $(Format-Pane $newborn); screen cols $($screen.cols) wraprun $(Count-OneCharRun $screen.text)"
        }

        Invoke-Scenario 'split-horizontal-floor' {
            $state = Invoke-SeamCommand $s @{ op = 'get-state' }
            $index = [int]$state.state.active
            $split = Invoke-SeamCommand $s @{ op = 'split'; orientation = 'horizontal' }
            $tab = @($split.state.tabs)[$index]
            if ([int]$tab.leaves -lt 3) { throw "HARNESS: the horizontal split left the tab at $($tab.leaves) leaf/leaves, wanted >= 3" }
            $newborn = Get-Settled $s $index
            $screen = Wait-PaneOutput $s $index -1 $MarkerSplit 20
            if ($null -eq $screen) {
                throw "PRODUCT_FAIL: the horizontal split newborn never showed its program output; screen: $(Format-Screen (Read-Screen $s $index -1).text)"
            }
            $what = "horizontal split newborn (leaf $($tab.activeLeaf))"
            $fails = @(Test-Newborn $what $newborn $screen.text)
            $entry.splitHorizontal = Format-Pane $newborn
            if ($fails.Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($fails -join '; ')) }
            "leaf $($tab.activeLeaf) of $($tab.leaves): $(Format-Pane $newborn); screen cols $($screen.cols) wraprun $(Count-OneCharRun $screen.text)"
        }

        # hidden: seed-tabs closes down to one tab, then adds the rest in one
        # turn; the last one added is the active one and the others are
        # created behind it.
        Invoke-Scenario 'hidden-floor' {
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
            $fails = @()
            foreach ($i in $hiddenIdx) {
                [void](Invoke-SeamCommand $s @{ op = 'select'; index = $i })
                $shown = Get-Settled $s $i
                $pre = $before[$i]
                $preText = if ($pre.live) { "live before shown, pty $($pre.spawnCols)x$($pre.spawnRows)" } else { 'no pty before shown' }
                $entry.hidden += "tab ${i}: $preText; shown: $(Format-Pane $shown)"
                # The marker wait is bounded but not a finding of its own
                # here: a hidden tab's shell may legitimately be the
                # silent legacy spawn. The floor and the equality are.
                $screen = Wait-PaneOutput $s $i -1 $MarkerLaunch 5
                $text = if ($screen) { $screen.text } else { '' }
                $fails += @(Test-Newborn "hidden tab $i once shown" $shown $text)
            }
            if ($fails.Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($fails -join '; ')) }
            "$($entry.hidden -join '; ')"
        }

        # The machine's real profile set, once per invocation (run 1): a
        # sweep is a sweep wherever it runs.
        if ($N -eq 1) {
            Invoke-Scenario 'profiles-list' {
                # Discovery composes async at startup; wait until two
                # answers a second apart agree so the sweep sees the whole
                # set rather than a partial one.
                $prev = $null
                $list = $null
                $deadline = (Get-Date).AddSeconds(20)
                while ((Get-Date) -lt $deadline) {
                    $list = Invoke-SeamCommand $s @{ op = 'profiles' }
                    if ($prev -and $prev.count -eq $list.count) { break }
                    $prev = $list
                    Start-Sleep -Milliseconds 1000
                }
                $ids = @($list.profiles | ForEach-Object { $_.id })
                if ($ids.Count -lt 1) { throw 'PRODUCT_FAIL: the registry resolved no profiles at all' }
                foreach ($staged in 'idle', 'sizeprobe', 'splitprobe') {
                    if ($ids -notcontains $staged) {
                        throw "HARNESS: the registry's visible set does not name the staged profile '$staged'; the config source broke"
                    }
                }
                "$($ids.Count) profiles: $($ids -join ', ')"
            }

            $stagedIds = @('idle', 'sizeprobe', 'splitprobe')
            $listed = Invoke-SeamCommand $s @{ op = 'profiles' }
            foreach ($p in @($listed.profiles)) {
                if ($stagedIds -contains $p.id) { continue } # covered above, every run
                $profileId = $p.id
                Invoke-Scenario "profile-$profileId-newtab" {
                    $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = $profileId }
                    $index = [int]$opened.state.active
                    $settled = Get-Settled $s $index
                    $screen = Read-Screen $s $index -1
                    $entry.sweep += "$profileId -> $(Format-Pane $settled)"
                    $fails = @(Test-Newborn "profile $profileId new tab" $settled $screen.text)
                    # A profile whose program prints nothing yet is not a
                    # wrap finding; record it so the row says so.
                    $hasText = (($screen.text -replace '\s', '').Length -gt 0)
                    if (-not $hasText) { $fails = @($fails | Where-Object { $_ -notlike '*wrap signature*' }) }
                    if ($fails.Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($fails -join '; ')) }
                    "$(Format-Pane $settled); screen $(if ($hasText) { "has text, wraprun $(Count-OneCharRun $screen.text)" } else { 'no text yet (wrap signature not applicable)' })"
                }
            }
        }

        if ($s.Proc.HasExited) { throw "APP_EXIT: the app exited during run $N (code $($s.Proc.ExitCode))" }
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
    runs       = $results
    scales     = $scales
    scaleCovered = $scaleCovered
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

# The scenario ledger: one row per scenario per run, the shape the
# release-side gate parses (it refuses a green whose core rows are
# missing, so a narrowed run cannot pass there either).
$script:Results | ConvertTo-Json | Set-Content (Join-Path $OutDir 'results.json') -Encoding utf8

Write-Host ''
foreach ($r in $results) {
    Write-Host ("run {0}: {1}  launch [{2}]  new tab [{3}]  shell {4}  hidden [{5}]" -f $r.run,
        $(if ($r.ok) { 'PASS' } else { "FAIL ($($r.class))" }), $r.launch, $r.newtab, $r.shell, ($r.hidden -join '; '))
}
if ($scaleCovered) {
    Write-Host ("display scale: {0}" -f ($scales -join ', '))
}
else {
    Write-Host ("display scale: {0}; the display-scale half of the size calculation was not exercised here" -f
        $(if ($scales.Count) { $scales -join ', ' } else { 'unknown' }))
}

$failed = @($script:Results | Where-Object { -not $_.ok })
$failed | ForEach-Object { Write-Host ("FAILED {0} [{1}] {2}" -f $_.name, $_.class, $_.detail) }
Write-Host ("SUMMARY: {0} scenarios ran: {1} passed, {2} failed" -f
    $script:Results.Count, ($script:Results.Count - $failed.Count), $failed.Count)
if (@($failed | Where-Object { $_.class -eq 'product' }).Count -gt 0) { exit 2 }
if ($failed.Count -gt 0) { exit 1 }
exit 0
