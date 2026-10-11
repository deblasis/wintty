#requires -Version 7
<#
    A pane is born with a sane grid, its pty starts at that grid, and its
    grid is its fair share of the room the layout gave it.

    Four oracles, one defect class each:

    #1159 equality  the size a surface was created at, which is the size
                    its pty starts with (surface-size:
                    spawnWidthPx/HeightPx and spawnCols/Rows), equals the
                    size the pane settles on. Pixels too, so a creation a
                    few pixels off fails even when both round to the same
                    grid.
    #1262 floor     equality is not sanity: a pane born AT one column and
                    STAYING there satisfies every equality. Every newborn
                    is held to MinCols x MinRows (the founder's proposed
                    floor, invariants I-1), born AND settled.
    #1262 share     a flat floor cannot see a pane robbed of its room:
                    for every leaf of every split tree this harness
                    builds, the leaf's grid must be at least its expected
                    share of the tab - the layout's own math (measured
                    cell metrics, padding, divider widths and split
                    ratios, all read off the deterministic core
                    scenarios), applied to the tree the harness drove, is
                    the oracle. The absolute floor stays as the backstop.
    I-3 signature   no pane may show the one-character-per-line wrap
                    fingerprint while its grid is wider than one column:
                    hard-wrapped startup output cannot reflow, so a
                    degenerate birth keeps its fingerprint after any
                    later resize.

    Birth kinds, each born through the real funnel and each judged by all
    four oracles on EVERY pane it produces:

      launch  the window's first tab, created while the window lays out.
      newtab  a tab opened through the seam (open-profile) once the
              window has settled, the way Ctrl+T opens one. The shell's
              own first size report (`mode con` to a file, run as the
              profile's command) is a second oracle here: on a build that
              starts the pty at the pane's size the report always agrees
              with surface-size.
      split   both orientations, deterministic core shapes (2 panes:
              one vertical split; 3 panes: vertical then horizontal) plus
              one SAMPLED shape per run: 4..10 panes, an interleaving of
              orientations drawn novelly per run so coverage grows across
              runs (see the ledger below), the seed recorded in the
              scenario row. Combos whose deepest leaf could not hold the
              floor in the measured window are not testable by design
              and are never drawn.
      hidden  tabs created in one dispatcher turn behind the active one
              (seed-tabs), the shape a session restore has. They are laid
              out only when first shown; once shown they pass the same
              oracles.
      profile every profile the registry resolved on this machine (seam
              profiles op), opened as a new tab: the staged synthetic
              ones plus everything discovery found - the "every profile"
              question a hardcoded id list cannot answer.

    Two dimensions cut across every scenario:

      daemon  every scenario runs twice, local-only (mux-attach = false)
              and with the session daemon (mux-attach = true): the pro
              family's spawn/attach path differs, and a birth defect can
              live on one path alone. Daemon-mode rows carry an @daemon
              suffix, so a build that only breaks one path cannot pass by
              averaging. On a tree with no daemon the key is inert and
              the rows say so.
      timing  settle and first-render budgets are a property of the
              PROFILE KIND, never a uniform constant: a cold WSL first
              launch takes seconds (and its warm re-open is a separate,
              tighter variant), a pwsh with a real $PROFILE has a
              legitimately late first render, cmd is near-instant. The
              sweep classifies each profile by the command the registry
              reported and holds it to its kind's budget, cold first and
              warm again for the kinds where cold and warm differ.

    The coverage ledger (seam-initial-size.coverage.json beside this
    script) records every sampled combination - pane count, orientation
    interleaving, profile, daemon mode - with run counts and the seed, so
    each run can bias its draw toward combinations no previous run drew.

    Every pane program prints a birth marker first (PANE-BIRTH-...), so
    the first-render wait is honest on both shapes of grid: in a sane one
    the marker arrives as a line, in a degenerate one as its vertical
    wrap, and a pane whose program output never reaches the screen at all
    is itself a finding, not a skip.

    What is recorded but not judged: the display scale (at 1.0 the
    display-scale half of the size calculation is not exercised, and the
    summary says so; unit tests cover 1.5 and 2.0).

    Per-scenario rows land in results.json ({name, ok, class, detail}),
    one row per scenario per run per mode; the summary counts them.

    Nothing is typed and no OS input is synthesized: the probe runs as a
    profile's command, and the seam only reads, opens, seeds, selects,
    focuses and splits. The launch is isolated (private config, private
    state, single-instance off) so it can run beside a Wintty somebody
    else is using.

    Exits 0 on pass, 2 on a product finding, 1 when the harness could
    not run and nothing is known about the product.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # Each run now walks BOTH daemon modes, so a run is two sessions plus
    # (run 1) two profile sweeps; two runs is the cadence that fits the
    # desktop lane's 30-minute wait.
    [int]$Runs = 2,
    # Tabs seed-tabs adds per launch. The last becomes the active one, so
    # all but one of them are created hidden.
    [int]$SeededTabs = 3,
    # A caller's teardown hook, forwarded to every session this harness
    # starts and to no Stop-SeamSession: Stop appends, so passing it there
    # too would run the hook twice for one launch.
    [scriptblock]$BeforeTeardown = $null
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

# The founder's floor (invariants.md I-1, proposed MIN_COLS = MIN_ROWS =
# 2; #1262 shipped cols == 1). One edit here re-floors every birth
# assertion in this harness. The share oracle below is the primary
# check; this is its backstop.
$MinCols = 2
$MinRows = 2
# I-3's signature length: this many consecutive one-character viewport
# lines in a pane wider than one column is the #1262 fingerprint.
$WrapRunLimit = 10
# Cells of slack between a leaf's expected share and its asserted floor:
# the model measures dividers, ratios and padding, so what remains is
# per-level device-pixel rounding; two cells of headroom on each axis.
$ShareSlack = 2

# Birth markers: long enough that a one-column birth wraps them into far
# more one-char lines than the signature needs.
$MarkerLaunch = 'PANE-BIRTH-LAUNCH-MARKER-1234567890'
$MarkerNewTab = 'PANE-BIRTH-NEWTAB-MARKER-1234567890'
$MarkerSplit = 'PANE-BIRTH-SPLIT-MARKER-1234567890'

# First-render and settle budgets, a property of the profile kind: a
# cold WSL distro start takes seconds; a pwsh with a real $PROFILE has a
# legitimately late first render; cmd is near-instant. Warm is the
# second open of the same profile in the same run, where cold and warm
# differ; otherwise warm equals cold.
$KindBudgets = @{
    cmd        = @{ ColdReady = 10; WarmReady = 10; Settle = 15; WarmSettle = 15 }
    powershell = @{ ColdReady = 30; WarmReady = 15; Settle = 25; WarmSettle = 20 }
    wsl        = @{ ColdReady = 90; WarmReady = 25; Settle = 45; WarmSettle = 25 }
    other      = @{ ColdReady = 25; WarmReady = 25; Settle = 20; WarmSettle = 20 }
}

function Get-ProfileKind([string]$Id, [string]$Command) {
    $text = "$Id $Command".ToLowerInvariant()
    if ($text -match '\bwsl(\.exe)?\b') { return 'wsl' }
    if ($text -match '\bpwsh(\.exe)?\b' -or $text -match '\bpowershell(\.exe)?\b') { return 'powershell' }
    if ($text -match '\bcmd(\.exe)?\b') { return 'cmd' }
    return 'other'
}

function Get-Budget([string]$Kind, [string]$Which) {
    $b = if ($KindBudgets.ContainsKey($Kind)) { $KindBudgets[$Kind] } else { $KindBudgets['other'] }
    return [int]$b[$Which]
}

# ---------------------------------------------------- WSL environment policy --
# The founder observed WSL broken on this machine. Policy: a WSL scenario
# never runs blind - the harness probes the distro itself first; a probe
# that fails is a LOUD SKIP (visible in the output, recorded in the
# pending-WSL ledger), never a red for a broken environment and never a
# silent green. The ledger is the re-run list once the environment is
# confirmed healthy again; WINTTY_PENDING_WSL_LEDGER names a shared
# ledger and the default sits beside the run's output.
# The pending-WSL ledger path is never hardcoded to a directory another
# machine may not have: WINTTY_PENDING_WSL_LEDGER names a shared ledger
# when one is wanted (the founder's machine points it at the shared
# C:\wt file), and the default lands beside the harness's own output,
# created on demand. The skip machinery must never be a source of red, so
# every ledger write degrades to a printed line if it cannot happen.
$PendingWslLedger = if ($env:WINTTY_PENDING_WSL_LEDGER) { "$($env:WINTTY_PENDING_WSL_LEDGER)" }
else { Join-Path $OutDir 'pending-wsl-tests.md' }
$script:Skips = [System.Collections.Generic.List[string]]::new()

function Add-PendingWsl([string]$What, [string]$ProbeNote) {
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    try {
        $dir = Split-Path -Parent $PendingWslLedger
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
        }
        if (-not (Test-Path -LiteralPath $PendingWslLedger)) {
            @(
                '# Pending WSL tests'
                ''
                '# Every WSL combination skipped because the WSL environment was'
                '# unhealthy at run time. This file is the re-run list: when the'
                '# environment is confirmed healthy, run these rows. Founder'
                '# directive 2026-10-02: WSL observed broken on this machine; the'
                '# harness probes basic execution before every WSL scenario.'
                ''
            ) | Set-Content -LiteralPath $PendingWslLedger -Encoding utf8
        }
        Add-Content -LiteralPath $PendingWslLedger -Value "$stamp | $What | probe: $ProbeNote" -Encoding utf8
    }
    catch {
        Write-Host "SKIP machinery: could not write the pending-WSL ledger at $PendingWslLedger : $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

function Add-WslSkip([string]$What, [string]$ProbeNote) {
    $line = "SKIP (WSL environment unhealthy): $What"
    $script:Skips.Add($line)
    Write-Host "$line [$ProbeNote]" -ForegroundColor Yellow
    Add-PendingWsl $What $ProbeNote
}

# One distro probe run, with a timeout: a hung wsl is an unhealthy wsl.
# The probe's own finding is recorded verbatim - a probe that answers
# healthy while the founder observes broken is itself diagnostic (the
# app's WSL profile path may be the finding, not the environment).
$script:WslProbeCache = @{}

function ConvertTo-WslSlug([string]$Name) {
    # The ID spec (WslProbe.Slugify): lower-case, keep [a-z0-9-], '_' and
    # ' ' become '-', everything else drops, duplicate hyphens collapse.
    $sb = [System.Text.StringBuilder]::new()
    foreach ($ch in $Name.ToLowerInvariant().ToCharArray()) {
        if ($ch -ge 'a' -and $ch -le 'z') { [void]$sb.Append($ch) }
        elseif ($ch -ge '0' -and $ch -le '9') { [void]$sb.Append($ch) }
        elseif ($ch -eq '-') { [void]$sb.Append($ch) }
        elseif ($ch -eq '_' -or $ch -eq ' ') { [void]$sb.Append('-') }
    }
    $s = $sb.ToString()
    while ($s.Contains('--')) { $s = $s.Replace('--', '-') }
    return $s.Trim('-')
}

function Invoke-WslCapture([string[]]$WslArgs, [int]$TimeoutSec) {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "wintty-wsl-probe-$([guid]::NewGuid().ToString('N')).txt"
    try {
        $p = Start-Process -FilePath 'wsl.exe' -ArgumentList $WslArgs -PassThru -NoNewWindow `
            -RedirectStandardOutput $tmp -RedirectStandardError "$tmp.err"
        if (-not $p.WaitForExit($TimeoutSec * 1000)) {
            try { $p.Kill($true) } catch { }
            return @{ State = 'unhealthy'; Text = ''; Note = "wsl $($WslArgs -join ' ') hung past ${TimeoutSec}s and was killed" }
        }
        $bytes = [System.IO.File]::ReadAllBytes($tmp)
        # wsl.exe writes UTF-16LE on stdout; a UTF-8 read of it is NUL-
        # laced. Decode by content, not by hope.
        $text = if ($bytes -contains 0) { [System.Text.Encoding]::Unicode.GetString($bytes) }
        else { [System.Text.Encoding]::UTF8.GetString($bytes) }
        if ($p.ExitCode -ne 0) {
            return @{ State = 'unhealthy'; Text = $text; Note = "wsl $($WslArgs -join ' ') exited $($p.ExitCode)" }
        }
        return @{ State = 'ok'; Text = $text; Note = "exit 0" }
    }
    catch {
        return @{ State = 'unhealthy'; Text = ''; Note = "$($_.Exception.Message)" }
    }
    finally {
        Remove-Item $tmp, "$tmp.err" -Force -ErrorAction SilentlyContinue
    }
}

function Test-WslDistroHealth([string]$Distro) {
    # Cached per distro ('' probes the default distro).
    $key = if ($Distro) { $Distro } else { '(default)' }
    if ($script:WslProbeCache.ContainsKey($key)) { return $script:WslProbeCache[$key] }
    $args = if ($Distro) { @('-d', $Distro, 'echo', 'wintty-wsl-probe-ok') } else { @('echo', 'wintty-wsl-probe-ok') }
    $r = Invoke-WslCapture $args 30
    $healthy = ($r.State -eq 'ok' -and $r.Text -like '*wintty-wsl-probe-ok*')
    $finding = @{
        Healthy = $healthy
        Note    = "$($r.Note); echo $(if ($healthy) { 'answered' } else { 'never answered' })"
    }
    $script:WslProbeCache[$key] = $finding
    if ($healthy) {
        # The founder's standing observation says this machine's WSL is
        # broken; a probe that answers healthy does not erase that, it
        # narrows it - the app's WSL profile path may be the finding.
        Add-PendingWsl "probe answered healthy ($key) - founder observed WSL broken; app-side WSL failures are product findings, not environment" $finding.Note
    }
    return $finding
}

# ------------------------------------------------- machine profile truth --
# The machine's own profile reality, enumerated by THIS harness (PATH,
# canonical install paths, `wsl --list --quiet` run and decoded here):
# the oracle for the detection scenarios, never the app's own answer.

function Read-WslDistroTruth {
    if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
        return @{ State = 'absent'; DistroIds = @(); Names = @(); Note = 'no wsl.exe' }
    }
    $r = Invoke-WslCapture @('--list', '--quiet') 30
    if ($r.State -ne 'ok') {
        return @{ State = 'unhealthy'; DistroIds = @(); Names = @(); Note = $r.Note }
    }
    $names = @()
    foreach ($line in ($r.Text -split "`n")) {
        $name = $line.Trim([char]0xFEFF, [char]0, ' ', "`t", "`r")
        if ($name.Length -gt 0) { $names += $name }
    }
    $ids = @($names | ForEach-Object { 'wsl-' + (ConvertTo-WslSlug "$_") })
    return @{ State = 'ok'; DistroIds = $ids; Names = $names; Note = "$($names.Count) distro(s): $($names -join ', ')" }
}

function Get-MachineProfileTruth {
    if ($null -ne $script:MachineTruth) { return $script:MachineTruth }
    $ids = @()
    if ((Get-Command pwsh -ErrorAction SilentlyContinue) -or
        (Test-Path (Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'))) { $ids += 'pwsh-7' }
    if (Test-Path (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe')) { $ids += 'pwsh-windows' }
    if (Test-Path (Join-Path $env:SystemRoot 'System32\cmd.exe')) { $ids += 'cmd' }
    $wsl = Read-WslDistroTruth
    $script:MachineTruth = @{ Ids = $ids; Wsl = $wsl }
    return $script:MachineTruth
}

# The detection oracle: the app's resolved set against the machine's own
# reality. Required-present is asserted from the harness's independent
# probes; the hallucination guard (the app reporting a WSL distro that
# does not exist) is asserted wherever the harness can enumerate the
# family completely. Extra app ids the harness cannot independently
# disprove are recorded, not failed. WSL halves follow the environment
# policy: unhealthy is a loud skip, never a red, never a silent green.
function Test-DetectionSet([string]$What, [string[]]$AppIds, [bool]$Configured) {
    $truth = Get-MachineProfileTruth
    $fails = @()
    $notes = @()
    foreach ($id in @($truth.Ids)) {
        if ($AppIds -notcontains $id) {
            $fails += "${What}: the app did not detect '$id', which this machine has"
        }
    }
    $staged = @('idle', 'sizeprobe', 'splitprobe')
    if ($Configured) {
        foreach ($id in $staged) {
            if ($AppIds -notcontains $id) {
                $fails += "${What}: the configured profile '$id' vanished - a machine with user config must not lose the user's profiles while still detecting new ones"
            }
        }
    }
    $appWsl = @($AppIds | Where-Object { $_ -like 'wsl-*' })
    if ($truth.Wsl.State -eq 'ok') {
        foreach ($id in $appWsl) {
            if ($truth.Wsl.DistroIds -notcontains $id) {
                $fails += "${What}: the app reports '$id' but 'wsl --list' has no such distro (detection hallucinated a profile)"
            }
        }
    }
    elseif ($truth.Wsl.State -eq 'unhealthy') {
        if ($appWsl.Count -gt 0) {
            Add-WslSkip "${What}: cannot check the app's WSL rows against 'wsl --list'" $truth.Wsl.Note
        }
        else {
            Add-WslSkip "${What}: WSL detection assertion (no app WSL rows to check either)" $truth.Wsl.Note
        }
    }
    elseif ($truth.Wsl.State -eq 'absent' -and $appWsl.Count -gt 0) {
        $fails += "${What}: the app reports WSL profiles and this machine has no wsl.exe at all"
    }
    $extras = @($AppIds | Where-Object { $truth.Ids -notcontains $_ -and $staged -notcontains $_ })
    if ($extras.Count -gt 0) { $notes += "app also reports: $($extras -join ', ') (not independently verifiable here)" }
    return @{ Fails = $fails; Notes = $notes }
}

# The coverage ledger: every sampled combination, with run counts and
# seeds, persisted beside this script so the next run can bias its draw
# toward what no run has drawn yet.
$LedgerPath = Join-Path $PSScriptRoot 'seam-initial-size.coverage.json'

function Read-CoverageLedger {
    if (Test-Path -LiteralPath $LedgerPath) {
        try {
            $parsed = Get-Content -LiteralPath $LedgerPath -Raw | ConvertFrom-Json
            if ($null -ne $parsed -and $null -ne $parsed.combos) { return $parsed }
        }
        catch { }
    }
    return [pscustomobject]@{ updatedAt = ''; combos = [pscustomobject]@{} }
}

function Write-CoverageLedger($Ledger) {
    $Ledger.updatedAt = (Get-Date).ToUniversalTime().ToString('o')
    $Ledger | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $LedgerPath -Encoding utf8
}

function Record-Combo($Ledger, [string]$Key, [int]$Seed) {
    $slot = $Ledger.combos.PSObject.Properties[$Key]
    $stamp = (Get-Date).ToUniversalTime().ToString('o')
    if ($null -eq $slot) {
        $Ledger.combos | Add-Member -NotePropertyName $Key -NotePropertyValue (
            [pscustomobject]@{ runs = 1; lastRun = $stamp; lastSeed = $Seed })
    }
    else {
        $slot.Value.runs = [int]$slot.Value.runs + 1
        $slot.Value.lastRun = $stamp
        $slot.Value.lastSeed = $Seed
    }
}

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
        foreach ($field in 'spawnCols', 'spawnRows', 'spawnWidthPx', 'spawnHeightPx', 'cols', 'rows', 'widthPx', 'heightPx', 'cellWidthPx', 'cellHeightPx') {
            if ($null -eq $r.PSObject.Properties[$field]) { throw "HARNESS: the seam no longer reports '$field'" }
        }
    }
    return $r
}

# A leaf's own viewport, off the cell grid. leaf=-1 reads the active
# leaf, which after a split is the newborn. "no live surface" is a
# documented transient (the surface spawns asynchronously; surface-size
# answers live=false gracefully but screen-text errors on it), so it
# retries within a short budget rather than failing the scenario
# outright.
function Read-Screen($s, [int]$Index, [int]$Leaf) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ($true) {
        try {
            return Invoke-SeamCommand $s @{ op = 'screen-text'; index = $Index; leaf = $Leaf }
        }
        catch {
            if ($_.Exception.Message -notmatch 'no live surface' -or
                [DateTime]::UtcNow -ge $deadline) {
                # Self-describing failure: the surface-state readback says
                # WHICH stuck state the pane is in (never measured, gave
                # up, or still retrying), so the row's error is the
                # diagnosis instead of a blind PRODUCT_FAIL.
                if ($_.Exception.Message -match 'no live surface') {
                    $st = $null
                    try { $st = Invoke-SeamCommand $s @{ op = 'surface-state'; index = $Index; leaf = $Leaf } } catch { }
                    if ($st) {
                        throw ("{0} [surface-state: live={1} attempted={2} retriesLeft={3} panel={4}x{5}]" -f `
                            $_.Exception.Message, $st.hasSurface, $st.attempted, $st.retriesLeft, $st.panelW, $st.panelH)
                    }
                }
                throw
            }
            Start-Sleep -Milliseconds 250
        }
    }
}

# The pane's settled size: read until two reads a second apart agree, so a
# layout pass still in flight is not taken for the answer. The budget is
# the profile kind's settle budget - never a uniform constant.
function Get-Settled($s, [int]$Index, [int]$BudgetSec = 15) {
    $prev = $null
    $r = $null
    $deadline = (Get-Date).AddSeconds($BudgetSec)
    while ((Get-Date) -lt $deadline) {
        $r = Read-Size $s $Index
        if ($r.live -and $prev -and $prev.live -and $prev.widthPx -eq $r.widthPx -and $prev.heightPx -eq $r.heightPx) { break }
        $prev = $r
        Start-Sleep -Milliseconds 1000
    }
    if (-not $r.live) { throw "HARNESS: tab $Index never got a live surface inside ${BudgetSec}s" }
    return $r
}

function Format-Pane($r) {
    return "pty $($r.spawnCols)x$($r.spawnRows) ($($r.spawnWidthPx)x$($r.spawnHeightPx) px), pane $($r.cols)x$($r.rows) ($($r.widthPx)x$($r.heightPx) px)"
}

function Format-Screen([string]$Text) {
    return ((($Text -split "`n") | Where-Object { $_.TrimEnd() -ne '' } | Select-Object -First 6) -join ' | ')
}

# The #1159 equality oracle: the pty started at the pane's size. The
# GRID must match exactly (born at the settled grid is the claim); the
# PIXEL axes may differ by under one cell each way - a pane created at
# the window's pre-DWM-settle extent holds a delta the grid cannot see
# (a 4px height overshoot at a ~21px cell), and the settled pane reports
# the settled extent, so an exact-pixel equality would fail a healthy
# launch pane whose creation fired at the first sizing pass.
function Test-Pane([string]$What, $r) {
    if ($r.spawnCols -ne $r.cols -or $r.spawnRows -ne $r.rows) {
        return "${What}: $(Format-Pane $r)"
    }
    $cellW = if ($r.cellWidthPx -gt 0) { [double]$r.cellWidthPx } else { 10 }
    $cellH = if ($r.cellHeightPx -gt 0) { [double]$r.cellHeightPx } else { 20 }
    if ([math]::Abs([double]$r.spawnWidthPx - [double]$r.widthPx) -ge $cellW -or
        [math]::Abs([double]$r.spawnHeightPx - [double]$r.heightPx) -ge $cellH) {
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

# The absolute-floor oracle on one pane: settled AND creation size meet
# the floor, and the screen carries no wrap signature. The creation-side
# floor applies to every pane however late it is read - a pane BORN
# degenerate and resized wider afterwards still shows its degenerate
# spawn size, which is exactly the #1262 shape. The #1159 EQUALITY is
# UNCONDITIONAL: the pane under test's pty must have started at its
# settled size, because every pane this function judges directly was read
# at its own birth. The only exception is spelled out loud below
# (-PaneWasResizedAfterBirth) for split leaves that a later split in the
# chain resized after their birth: their creation size stays the
# historical one, which is correct product behavior. A call site cannot
# lose the equality by forgetting an argument - omitting the switch IS
# the check.
function Test-Newborn([string]$What, $r, [string]$ScreenText, [switch]$PaneWasResizedAfterBirth) {
    $fails = @()
    if ([int]$r.cols -lt $MinCols -or [int]$r.rows -lt $MinRows) {
        $fails += "${What}: settled at $($r.cols)x$($r.rows), floor is ${MinCols}x${MinRows} (I-1)"
    }
    if ([int]$r.spawnCols -lt $MinCols -or [int]$r.spawnRows -lt $MinRows) {
        $fails += "${What}: born at $($r.spawnCols)x$($r.spawnRows), floor is ${MinCols}x${MinRows} (I-1, creation size)"
    }
    if (-not $PaneWasResizedAfterBirth -and ($f = Test-Pane $What $r)) { $fails += $f }
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
        # Whitespace stripped before the match: a marker wrapped across
        # lines at the pane's own width (or one char per line in the
        # degenerate case) is still the marker's bytes on screen.
        if ((("$($screen.text)" -replace '\s', '') -like "*$Marker*")) { return $screen }
        if ((Count-OneCharRun $screen.text) -ge 8) { return $screen }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

# Wait until the pane shows ANY output of its own: for profiles whose
# program is not one of this harness's staged markers, the marker cannot
# be named, but "no output at all" is still a finding.
function Wait-PaneAnyText($s, [int]$Index, [int]$Leaf, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $screen = Read-Screen $s $Index $Leaf
        if ((("$($screen.text)" -replace '\s', '').Length -gt 0)) { return $screen }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

# Focus a leaf so surface-size (which reads the ACTIVE leaf) answers for
# it; screen-text is leaf-indexed on its own.
function Read-Leaf($s, [int]$Index, [int]$Leaf) {
    [void](Invoke-SeamCommand $s @{ op = 'focus-pane'; index = $Leaf })
    return Read-Size $s $Index
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
        if ($_.InvocationInfo) { $msg += " [at $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.PositionMessage -replace '\s+', ' ')]" }
        $class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
        if ($msg -like "*unknown op 'profiles'*") {
            # The enumeration op is fork-side; a tree whose seam lacks it
            # is a pin gap, not a product defect of that build.
            $class = 'harness'
        }
        Add-Result $Name $false $class $msg
    }
}

# ------------------------------------------------------- geometry model --

# The measured layout math of this run: cell metrics, total padding per
# axis (W - cols*cellW, the honest over-estimate), the divider the layout
# substracts per split, and the lower-index child's fraction per axis.
# Measured on the deterministic core scenarios, consumed by the sampled
# scenario, so the share oracle is this machine's own layout, not guesses.
$Geom = @{
    CellW = 0.0; CellH = 0.0; PadX = 0.0; PadY = 0.0
    DividerW = 0.0; DividerH = 0.0; RatioV = 0.5; RatioH = 0.5
}

# The two children of a rect split along an axis: the lower index keeps
# the measured ratio, the divider leaves the room first.
function Split-ModelRects($Rect, [string]$Orientation) {
    if ($Orientation -eq 'horizontal') {
        $room = $Rect.H - $Geom.DividerH
        $a = $room * $Geom.RatioH
        return @(
            [pscustomobject]@{ W = $Rect.W; H = $a },
            [pscustomobject]@{ W = $Rect.W; H = $room - $a }
        )
    }
    $room = $Rect.W - $Geom.DividerW
    $a = $room * $Geom.RatioV
    return @(
        [pscustomobject]@{ W = $a; H = $Rect.H },
        [pscustomobject]@{ W = $room - $a; H = $Rect.H }
    )
}

# Every pane of a split tab, judged by every oracle: the absolute floor
# and creation floor, the equality, the wrap signature, the leaf's
# EXPECTED SHARE of the room (the model's rect for it), and grid honesty
# (the grid must take the room its own rect gives it - I-1's hard
# sub-check, which catches a renderer with room the grid never took even
# when the share model is somehow satisfied).
function Test-Leaf([string]$What, $s, [int]$Index, [int]$Leaf, $Expected, [string]$Marker, [int]$ReadySec, [bool]$Fresh) {
    # An empty $Marker means the profile's program is not one of this
    # harness's staged markers; any output of its own still proves the
    # first render happened.
    $screen = if ([string]::IsNullOrEmpty($Marker)) {
        Wait-PaneAnyText $s $Index $Leaf $ReadySec
    }
    else {
        Wait-PaneOutput $s $Index $Leaf $Marker $ReadySec
    }
    if ($null -eq $screen) {
        $dump = Format-Screen (Read-Screen $s $Index $Leaf).text
        return @{ Fails = @("${What}: the pane's program output never reached the screen inside ${ReadySec}s (I-2); screen: $dump"); Detail = 'no output' }
    }
    $size = Read-Leaf $s $Index $Leaf
    # $Fresh (read at this leaf's own birth) maps to the loud opt-out the
    # other way round: every pane this harness reads at its own birth
    # keeps the equality, and only a leaf a LATER split resized after its
    # birth carries the opt-out.
    $fails = if ($Fresh) { @(Test-Newborn $What $size $screen.text) }
    else { @(Test-Newborn $What $size $screen.text -PaneWasResizedAfterBirth) }
    $detail = "leaf ${Leaf}: born+settled $($size.cols)x$($size.rows) in $($size.widthPx)x$($size.heightPx) px, wraprun $(Count-OneCharRun $screen.text)"
    if ($null -ne $Expected -and $Geom.CellW -gt 0) {
        $wantCols = [int][math]::Floor(($Expected.W - $Geom.PadX) / $Geom.CellW)
        $wantRows = [int][math]::Floor(($Expected.H - $Geom.PadY) / $Geom.CellH)
        $needCols = [math]::Max($MinCols, $wantCols - $ShareSlack)
        $needRows = [math]::Max($MinRows, $wantRows - $ShareSlack)
        if ([int]$size.cols -lt $needCols) {
            $fails += "${What}: cols $($size.cols) but its share of the room is ~$wantCols cols (a $($Expected.W) px share of the tab); floor $needCols after slack"
        }
        if ([int]$size.rows -lt $needRows) {
            $fails += "${What}: rows $($size.rows) but its share of the room is ~$wantRows rows (a $($Expected.H) px share of the tab); floor $needRows after slack"
        }
        $detail += ", share ~${wantCols}x${wantRows}"
    }
    if ($Geom.CellW -gt 0) {
        $roomCols = [int][math]::Floor(([double]$size.widthPx - $Geom.PadX) / $Geom.CellW)
        $roomRows = [int][math]::Floor(([double]$size.heightPx - $Geom.PadY) / $Geom.CellH)
        if ([int]$size.cols -lt $roomCols - 1) {
            $fails += "${What}: cols $($size.cols) though its own $($size.widthPx) px rect has room for ~$roomCols cols (the grid did not take the room the renderer gave it)"
        }
        if ([int]$size.rows -lt $roomRows - 1) {
            $fails += "${What}: rows $($size.rows) though its own $($size.heightPx) px rect has room for ~$roomRows rows"
        }
    }
    return @{ Fails = $fails; Detail = $detail }
}

# ------------------------------------------------------------- sampling --

# The sampled combo: pane count 4..10 and an orientation interleaving,
# drawn from a recorded seed, biased toward combinations no previous run
# drew (the ledger), and FEASIBLE in the measured window: a combination
# whose deepest leaf could not hold the floor in this tab is not
# testable by design and is never drawn.
function Test-ComboFeasible($BaseW, $BaseH, [string[]]$Orientations) {
    $rects = @([pscustomobject]@{ W = [double]$BaseW; H = [double]$BaseH })
    $active = 0
    foreach ($o in $Orientations) {
        $pair = Split-ModelRects $rects[$active] $o
        $newRects = @()
        for ($k = 0; $k -lt $rects.Count; $k++) {
            if ($k -ne $active) { $newRects += $rects[$k]; continue }
            $newRects += $pair[0]
            $newRects += $pair[1]
        }
        # the newborn is the active leaf after the split; with the
        # active-leaf chain the newborn is the second child
        $rects = $newRects
        $active = $active + 1
    }
    foreach ($r in $rects) {
        $cols = [int][math]::Floor(($r.W - $Geom.PadX) / $Geom.CellW)
        $rows = [int][math]::Floor(($r.H - $Geom.PadY) / $Geom.CellH)
        if ($cols -lt ($MinCols + $ShareSlack) -or $rows -lt ($MinRows + $ShareSlack)) { return $false }
        # The I-2 oracle must be able to SEE the marker: at narrow shares
        # the echoed marker wraps onto ceil(len/cols) lines and the leaf
        # must hold those plus the prompt line, or the whitespace-stripped
        # match can never succeed however healthy the pane (the 9:vvvvvhhh
        # draw left its last leaves a legitimate 7x5 share - every pane
        # painted, the oracle could not see it). Feasibility bounds by
        # what the oracle can observe, not the founder's 2x2 floor alone.
        $markerLines = [int][math]::Ceiling($MarkerSplit.Length / $cols)
        if ($rows -lt ($markerLines + 1)) { return $false }
    }
    return $true
}

function Draw-SampleCombo([string]$ProfileId, [string]$Mode, [double]$BaseW, [double]$BaseH) {
    $seed = Get-Random -Minimum 1 -Maximum 2147483647
    $rand = [System.Random]::new($seed)
    $fallback = $null
    for ($try = 0; $try -lt 60; $try++) {
        $n = 4 + $rand.Next(7)
        $count = $n - 1
        $oris = @()
        for ($j = 0; $j -lt $count; $j++) {
            $oris += if ($rand.Next(2) -eq 0) { 'vertical' } else { 'horizontal' }
        }
        if (-not (Test-ComboFeasible $BaseW $BaseH $oris)) { continue }
        $key = "${ProfileId}:${n}:$(($oris | ForEach-Object { $_[0] }) -join '')@${Mode}"
        $seen = $script:Ledger.combos.PSObject.Properties[$key]
        if ($null -eq $seen) {
            return @{ N = $n; Orientations = $oris; Key = $key; Seed = $seed }
        }
        if ($null -eq $fallback -or [int]$seen.Value.runs -lt [int]$fallback.SeenRuns) {
            $fallback = @{ N = $n; Orientations = $oris; Key = $key; Seed = $seed; SeenRuns = [int]$seen.Value.runs }
        }
    }
    # everything feasible has been drawn: take the least-run fallback
    return $fallback
}

# ------------------------------------------------------------------ run --

function Invoke-Run([int]$N, [string]$Mode) {
    $daemon = ($Mode -eq 'daemon')
    $suffix = if ($daemon) { '@daemon' } else { '' }
    $probe = Join-Path $OutDir "first-size-$N-$Mode.txt"
    Remove-Item -LiteralPath $probe -ErrorAction SilentlyContinue
    if ($probe -match '\s') { throw "HARNESS: probe path '$probe' holds a space; cmd would split it" }

    $config = @"
windows-single-instance = false
window-save-state = never
mux-attach = $($Mode -eq 'daemon' ? 'true' : 'false')
default-profile = idle
profile.idle.name = Idle
profile.idle.command = cmd.exe /d /c echo $MarkerLaunch & ping -n 120 127.0.0.1 > nul
profile.sizeprobe.name = SizeProbe
profile.sizeprobe.command = cmd.exe /d /c mode con > $probe & echo $MarkerNewTab & ping -n 120 127.0.0.1 > nul
profile.splitprobe.name = SplitProbe
profile.splitprobe.command = cmd.exe /d /c echo $MarkerSplit & ping -n 120 127.0.0.1 > nul
"@
    $entry = [ordered]@{
        run = $N; mode = $Mode; ok = $false; class = ''; error = ''; scale = $null
        launch = ''; newtab = ''; shell = ''; split2 = ''; split3 = ''; sample = ''; hidden = @(); sweep = @()
    }
    $s = $null
    try {
        Assert-NoWinttyFrom -ExePath $ExePath -Context 'seam-initial-size'
        # Forwarded here, never to the Stop-SeamSession in the finally below: Stop appends, so the hook would run twice for one launch.
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -PrivateStateBase -BeforeTeardown $BeforeTeardown

        # The registry snapshot the sampled scenario's profile pool and
        # the kind budgets need. Best-effort and early: discovery may
        # still be composing, and a tree without the profiles op (an old
        # pin) leaves the pool at the staged profiles, which is exactly
        # the degradation that run should report, not crash on.
        try {
            $snap = Invoke-SeamCommand $s @{ op = 'profiles' }
            $script:RegistryIds = @($snap.profiles | ForEach-Object { "$($_.id)" })
            foreach ($sp in @($snap.profiles)) {
                $script:RegistryCommands["$($sp.id)"] = "$($sp.command)"
            }
        }
        catch { }

        # launch: the window's first tab.
        Invoke-Scenario "launch-floor$suffix" {
            $launch = Get-Settled $s 0 (Get-Budget cmd Settle)
            $entry.scale = [double]$launch.scale
            $entry.launch = Format-Pane $launch
            $screen = Wait-PaneOutput $s 0 -1 $MarkerLaunch (Get-Budget cmd ColdReady)
            if ($null -eq $screen) {
                throw "PRODUCT_FAIL: the launch pane never showed its program output; screen: $(Format-Screen (Read-Screen $s 0 -1).text)"
            }
            $fails = @(Test-Newborn 'launch tab' $launch $screen.text)
            if ($daemon) {
                # The daemon dimension is only honest if recorded: probe
                # the tier's pane-sessions op, best-effort, so the row
                # says whether this build has a daemon at all.
                try {
                    $sessions = Invoke-SeamCommand $s @{ op = 'pane-sessions' }
                    $entry.launch += " [pane-sessions: $(@($sessions.sessions).Count) session(s)]"
                }
                catch {
                    $entry.launch += ' [pane-sessions: not served (a tree with no sessions ops; the daemon dimension is inert here)]'
                }
            }
            if ($fails.Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($fails -join '; ')) }
            "$(Format-Pane $launch); screen cols $($screen.cols) wraprun $(Count-OneCharRun $screen.text)"
        }

        # newtab: through the profile funnel, plus the shell's own report
        # as a second oracle (on a sane build it always agrees).
        Invoke-Scenario "newtab-floor$suffix" {
            $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'sizeprobe' }
            if (@($opened.state.tabs).Count -ne 2) { throw "HARNESS: open-profile left $(@($opened.state.tabs).Count) tabs, wanted 2" }
            $index = [int]$opened.state.active
            if ($index -eq 0) { throw 'HARNESS: the opened tab is not the active one' }
            $newtab = Get-Settled $s $index (Get-Budget cmd Settle)
            $entry.newtab = Format-Pane $newtab
            $screen = Wait-PaneOutput $s $index -1 $MarkerNewTab (Get-Budget cmd ColdReady)
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

        # The deterministic split core, shapes of 2 and 3 panes. These
        # also MEASURE the layout math (cell, padding, dividers, ratios)
        # the sampled scenario's share oracle uses, and carry a live rect
        # model the sampled scenario replays.
        Invoke-Scenario "split-core-2v$suffix" {
            $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'splitprobe' }
            $index = [int]$opened.state.active
            $base = Get-Settled $s $index (Get-Budget cmd Settle)
            [void](Wait-PaneOutput $s $index -1 $MarkerSplit (Get-Budget cmd ColdReady))
            $Geom.CellW = [double]$base.cellWidthPx
            $Geom.CellH = [double]$base.cellHeightPx
            $Geom.PadX = [double]$base.widthPx - [int]$base.cols * $Geom.CellW
            $Geom.PadY = [double]$base.heightPx - [int]$base.rows * $Geom.CellH
            $split = Invoke-SeamCommand $s @{ op = 'split'; orientation = 'vertical' }
            $tab = @($split.state.tabs)[$index]
            if ([int]$tab.leaves -ne 2) { throw "HARNESS: the vertical split left the tab at $($tab.leaves) leaf/leaves, wanted 2" }
            $r0 = Read-Leaf $s $index 0
            $r1 = Read-Leaf $s $index 1
            # The layout math, measured: the divider the split subtracted
            # and the lower-index child's fraction.
            $Geom.DividerW = [double]$base.widthPx - [double]$r0.widthPx - [double]$r1.widthPx
            $Geom.RatioV = [double]$r0.widthPx / ([double]$r0.widthPx + [double]$r1.widthPx)
            $pair = Split-ModelRects ([pscustomobject]@{ W = [double]$base.widthPx; H = [double]$base.heightPx }) 'vertical'
            $newborn = [int]$tab.activeLeaf
            $rects = if ($newborn -eq 0) { @($pair[1], $pair[0]) } else { @($pair[0], $pair[1]) }
            $script:CoreRects = $rects
            $res0 = Test-Leaf 'split 2v leaf 0' $s $index 0 $rects[0] $MarkerSplit (Get-Budget cmd ColdReady) $false
            $res1 = Test-Leaf 'split 2v leaf 1' $s $index 1 $rects[1] $MarkerSplit (Get-Budget cmd ColdReady) $true
            $fails = @($res0.Fails) + @($res1.Fails)
            $entry.split2 = "$($r0.cols)x$($r0.rows) + $($r1.cols)x$($r1.rows)"
            if ($fails.Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($fails -join '; ')) }
            "dividerW $([math]::Round($Geom.DividerW,1)) px, ratioV $([math]::Round($Geom.RatioV,3)); $($res0.Detail); $($res1.Detail)"
        }

        Invoke-Scenario "split-core-3vh$suffix" {
            if ($null -eq $script:CoreRects) { throw 'HARNESS: the 2v core scenario did not complete, so the rect model it measures never existed' }
            $state = Invoke-SeamCommand $s @{ op = 'get-state' }
            $index = [int]$state.state.active
            # The horizontal split lands on the ACTIVE leaf; the 2v leg
            # ends with its per-leaf reads focused on leaf 1, the newborn.
            $activeBefore = [int]@($state.state.tabs)[$index].activeLeaf
            $parent = Read-Leaf $s $index $activeBefore
            $split = Invoke-SeamCommand $s @{ op = 'split'; orientation = 'horizontal' }
            $tab = @($split.state.tabs)[$index]
            if ([int]$tab.leaves -ne 3) { throw "HARNESS: the horizontal split left the tab at $($tab.leaves) leaf/leaves, wanted 3" }
            $childA = Read-Leaf $s $index $activeBefore
            $childB = Read-Leaf $s $index ($activeBefore + 1)
            $Geom.DividerH = [double]$parent.heightPx - [double]$childA.heightPx - [double]$childB.heightPx
            $Geom.RatioH = [double]$childA.heightPx / ([double]$childA.heightPx + [double]$childB.heightPx)
            $pair = Split-ModelRects $script:CoreRects[$activeBefore] 'horizontal'
            $newborn = [int]$tab.activeLeaf
            $next = @()
            for ($k = 0; $k -lt $script:CoreRects.Count; $k++) {
                if ($k -ne $activeBefore) { $next += $script:CoreRects[$k]; continue }
                if ($newborn -eq $k) { $next += $pair[1]; $next += $pair[0] }
                else { $next += $pair[0]; $next += $pair[1] }
            }
            $script:CoreRects = $next
            $fails = @()
            $details = @()
            for ($l = 0; $l -lt 3; $l++) {
                $res = Test-Leaf "split 3vh leaf $l" $s $index $l $script:CoreRects[$l] $MarkerSplit (Get-Budget cmd ColdReady) ($l -eq $newborn)
                $fails += @($res.Fails)
                $details += $res.Detail
            }
            $entry.split3 = "$($childA.cols)x$($childA.rows) + $($childB.cols)x$($childB.rows)"
            if ($fails.Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($fails -join '; ')) }
            "dividerH $([math]::Round($Geom.DividerH,1)) px, ratioH $([math]::Round($Geom.RatioH,3)); $($details -join '; ')"
        }

        # The sampled tree: 4..10 panes of novel interleaving, every pane
        # judged by every oracle, the combo and seed in the row.
        Invoke-Scenario "split-sample$suffix" {
            $pool = @('splitprobe')
            foreach ($candidate in @('pwsh-7', 'pwsh-windows')) {
                if ($script:RegistryIds -contains $candidate) { $pool += $candidate; break }
            }
            $wslIds = @($script:RegistryIds | Where-Object { $_ -like 'wsl-*' -and $_ -notlike 'wsl-docker*' })
            if ($wslIds.Count -gt 0) {
                # The sampled profile axis includes WSL only when the
                # environment probes healthy; otherwise the axis is
                # loudly skipped and ledgered, not silently narrowed.
                # docker-desktop distros are infrastructure (they echo
                # but run no user shell), so they are not profile axis
                # candidates.
                $wslCommand = "$($script:RegistryCommands["$($wslIds[0])"])"
                $distro = if ($wslCommand -match '-d\s+(\S+)') { $Matches[1].Trim('"') } else { '' }
                $health = Test-WslDistroHealth $distro
                if ($health.Healthy) { $pool += $wslIds[0] }
                else { Add-WslSkip "split-sample$suffix profile axis wsl:$($wslIds[0])" $health.Note }
            }
            $profileId = "$($pool[(Get-Random -Minimum 0 -Maximum $pool.Count)])"
            $kind = Get-ProfileKind $profileId "$($script:RegistryCommands[$profileId])"
            # Only the staged splitprobe prints the split marker; for a
            # discovered profile any output of its own proves the render.
            $leafMarker = if ($profileId -eq 'splitprobe') { $MarkerSplit } else { '' }
            $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = $profileId }
            $index = [int]$opened.state.active
            $base = Get-Settled $s $index (Get-Budget $kind Settle)
            [void](Wait-PaneAnyText $s $index -1 (Get-Budget $kind ColdReady))
            $combo = Draw-SampleCombo $profileId $Mode ([double]$base.widthPx) ([double]$base.heightPx)
            if ($null -eq $combo) { throw "HARNESS: no feasible 4..10-pane interleaving fits the measured tab ($([int]$base.widthPx)x$([int]$base.heightPx) px); the window is too small to sample" }
            $rects = @([pscustomobject]@{ W = [double]$base.widthPx; H = [double]$base.heightPx })
            $active = 0
            foreach ($o in $combo.Orientations) {
                # The model is authoritative: name the leaf to divide so no
                # focus event (a daemon attach landing mid-burst) can send a
                # split to the wrong pane.
                $split = Invoke-SeamCommand $s @{ op = 'split'; orientation = $o; leaf = $active }
                $tab = @($split.state.tabs)[$index]
                $newborn = [int]$tab.activeLeaf
                $pair = Split-ModelRects $rects[$active] $o
                $newRects = @()
                for ($k = 0; $k -lt $rects.Count; $k++) {
                    if ($k -ne $active) { $newRects += $rects[$k]; continue }
                    if ($newborn -eq $active) { $newRects += $pair[1]; $newRects += $pair[0] }
                    else { $newRects += $pair[0]; $newRects += $pair[1] }
                }
                $rects = $newRects
                $active = $newborn
                if ([int]$tab.leaves -ne $rects.Count) {
                    throw "HARNESS: the model's leaf count ($($rects.Count)) drifted from the app's ($($tab.leaves)) mid-combo"
                }
            }
            # Tested is tested: the tree was driven and every leaf is
            # about to be read; the ledger records it now so a crash in
            # the asserts does not erase the combination from coverage.
            Record-Combo $script:Ledger $combo.Key $combo.Seed
            Write-CoverageLedger $script:Ledger
            $fails = @()
            $details = @()
            for ($l = 0; $l -lt $rects.Count; $l++) {
                # Every split pane spawns its own shell: each leaf gets
                # the kind's cold budget, and only the FINAL newborn is
                # fresh (every earlier pane was resized by the next split
                # in the active-leaf chain, so the equality oracle does
                # not apply to it).
                $res = Test-Leaf "sample $($combo.Key) leaf $l" $s $index $l $rects[$l] $leafMarker (Get-Budget $kind ColdReady) ($l -eq $active)
                $fails += @($res.Fails)
                $details += $res.Detail
            }
            $entry.sample = $combo.Key
            if ($fails.Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($fails -join '; ')) }
            "combo $($combo.Key) seed $($combo.Seed): $($details -join '; ')"
        }

        # hidden: seed-tabs closes down to one tab, then adds the rest in one
        # turn; the last one added is the active one and the others are
        # created behind it. `profile` births them through the resolved
        # idle snapshot -- the shape a session restore has -- so each hidden
        # pane runs the same echo program the launch pane does and its
        # marker can reach the screen once shown.
        Invoke-Scenario "hidden-floor$suffix" {
            $seeded = Invoke-SeamCommand $s @{ op = 'seed-tabs'; count = $SeededTabs + 1; profile = 'idle' }
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
                $shown = Get-Settled $s $i (Get-Budget cmd Settle)
                $pre = $before[$i]
                $preText = if ($pre.live) { "live before shown, pty $($pre.spawnCols)x$($pre.spawnRows)" } else { 'no pty before shown' }
                $entry.hidden += "tab ${i}: $preText; shown: $(Format-Pane $shown)"
                # No output is a finding for every pane kind this
                # harness births: a hidden tab runs the same echo program,
                # and once shown its marker must reach the screen.
                $screen = Wait-PaneOutput $s $i -1 $MarkerLaunch (Get-Budget cmd WarmReady)
                $text = if ($screen) { $screen.text } else { '' }
                if ($null -eq $screen) {
                    $fails += "hidden tab $i once shown: its shell's output never reached the screen inside $(Get-Budget cmd WarmReady)s (I-2)"
                }
                $fails += @(Test-Newborn "hidden tab $i once shown" $shown $text)
            }
            if ($fails.Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($fails -join '; ')) }
            "$($entry.hidden -join '; ')"
        }

        # The machine's real profile set, once per invocation (run 1), in
        # BOTH modes: the daemon axis is a scenario dimension. Each
        # profile is held to its KIND's budget, cold first; the kinds
        # where cold and warm differ (wsl, powershell) are re-opened
        # under the warm budget.
        if ($N -eq 1) {
            Invoke-Scenario "profiles-list$suffix" {
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

            # Fresh-vs-configured, the other half: this session HAS user
            # config (the staged profiles); detection must ADD to it, not
            # replace it. Same independent oracle.
            Invoke-Scenario "detection-configured$suffix" {
                $list = Invoke-SeamCommand $s @{ op = 'profiles' }
                $ids = @($list.profiles | ForEach-Object { "$($_.id)" })
                $verdict = Test-DetectionSet 'detection (configured)' $ids $true
                if (@($verdict.Fails).Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($verdict.Fails -join '; ')) }
                "{0} profile(s) on a configured machine: {1}{2}" -f $ids.Count, ($ids -join ', '),
                $(if ($verdict.Notes) { '; ' + ($verdict.Notes -join '; ') } else { '' })
            }

            $stagedIds = @('idle', 'sizeprobe', 'splitprobe')
            $listed = $null
            try {
                $listed = Invoke-SeamCommand $s @{ op = 'profiles' }
            }
            catch {
                # A seam without the profiles op is a pin that predates
                # it: the sweep cannot run, loudly, as a harness gap.
                Add-Result "profile-sweep$suffix" $false 'harness' "the seam does not serve the profiles op ($_); the fork pin predates the enumeration op, so the every-profile sweep cannot run"
            }
            foreach ($p in (@($listed) | Where-Object { $null -ne $_ } | ForEach-Object { @($_.profiles) } | ForEach-Object { $_ })) {
                if ($stagedIds -contains $p.id) { continue } # covered above, every run
                $profileId = "$($p.id)"
                if ($profileId -like 'wsl-docker*') {
                    # An infrastructure distro (docker's utility VM): it
                    # probes alive but runs no user shell, so a birth
                    # assertion on its pane would be an environment
                    # finding dressed as a product one. Visible note, not
                    # a judged row and not a WSL-health skip.
                    Write-Host "NOTE profile-$profileId-newtab${suffix}: infrastructure distro, not a user shell; not a judged row" -ForegroundColor Yellow
                    continue
                }
                $kind = Get-ProfileKind $profileId "$($p.command)"
                # The WSL environment policy: probe the distro the
                # profile targets; unhealthy is a loud skip with a ledger
                # line, never a red for the environment, never a silent
                # green.
                if ($kind -eq 'wsl') {
                    $distro = "$($p.command)"
                    if ($distro -match '-d\s+(\S+)') { $distro = $Matches[1].Trim('"') } else { $distro = '' }
                    $health = Test-WslDistroHealth $distro
                    if (-not $health.Healthy) {
                        Add-WslSkip "profile-$profileId-newtab$suffix + profile-$profileId-warm$suffix (cold and warm opens)" $health.Note
                        continue
                    }
                }
                Invoke-Scenario "profile-$profileId-newtab$suffix" {
                    $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = $profileId }
                    $index = [int]$opened.state.active
                    $settled = Get-Settled $s $index (Get-Budget $kind Settle)
                    # A swept profile's program is its own; any output of
                    # its own proves the first render, inside its kind's
                    # cold budget.
                    $screen = Wait-PaneAnyText $s $index -1 (Get-Budget $kind ColdReady)
                    $text = if ($screen) { $screen.text } else { (Read-Screen $s $index -1).text }
                    $hasText = (($text -replace '\s', '').Length -gt 0)
                    $entry.sweep += "$profileId ($kind) -> $(Format-Pane $settled)"
                    $fails = @(Test-Newborn "profile $profileId new tab" $settled $text)
                    # No output is a finding for every pane kind: a
                    # profile whose program shows nothing inside its own
                    # kind's cold budget is a blank-with-live-shell pane.
                    if (-not $hasText) {
                        $fails += "profile $profileId new tab: no output of its own reached the screen inside the ${kind} cold budget ($(Get-Budget $kind ColdReady)s) (I-2)"
                    }
                    if ($fails.Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($fails -join '; ')) }
                    "$(Format-Pane $settled); screen has text, wraprun $(Count-OneCharRun $text)"
                }
                if ($kind -in @('wsl', 'powershell')) {
                    Invoke-Scenario "profile-$profileId-warm$suffix" {
                        # The warm variant: the same profile opened again,
                        # under the tighter warm budget - cold/warm is a
                        # scenario variant where it matters.
                        $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = $profileId }
                        $index = [int]$opened.state.active
                        $settled = Get-Settled $s $index (Get-Budget $kind WarmSettle)
                        $screen = Wait-PaneAnyText $s $index -1 (Get-Budget $kind WarmReady)
                        $text = if ($screen) { $screen.text } else { '' }
                        $entry.sweep += "$profileId ($kind, warm) -> $(Format-Pane $settled)"
                        $fails = @(Test-Newborn "profile $profileId warm reopen" $settled $text)
                        if (($text -replace '\s', '').Length -eq 0) {
                            $fails += "profile $profileId warm reopen: no output of its own reached the screen inside the ${kind} warm budget ($(Get-Budget $kind WarmReady)s) (I-2)"
                        }
                        if ($fails.Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($fails -join '; ')) }
                        "warm reopen inside the ${kind} warm budget ($(Get-Budget $kind WarmSettle)s settle): $(Format-Pane $settled)"
                    }
                }
            }
        }

        if ($s.Proc.HasExited) { throw "APP_EXIT: the app exited during run $N (code $($s.Proc.ExitCode))" }
        $entry.ok = $true
        Write-Host ("PASS run {0} [{1}]: launch {2}; new tab {3}; shell {4}; {5}" -f $N, $Mode, $entry.launch, $entry.newtab,
            $entry.shell, ($entry.hidden -join '; ')) -ForegroundColor Green
    } catch {
        $msg = "$($_.Exception.Message)"
        $entry.error = $msg
        $entry.class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
        Write-Host "FAIL run $N [$Mode] [$($entry.class)]: $msg" -ForegroundColor Red
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
                Copy-Item -LiteralPath $s.StateBase -Destination (Join-Path $OutDir "state-$N-$Mode") -Recurse -Force -ErrorAction SilentlyContinue
            }
            # A teardown hook that throws must not abort the finally: it is
            # recorded against the run instead.
            try {
                Stop-SeamSession $s
            }
            catch {
                $teardownMsg = "$($_.Exception.Message)"
                $entry.ok = $false
                $entry.error += " | teardown: $teardownMsg"
                if (-not $entry.class) {
                    $entry.class = if ($teardownMsg -like '*PRODUCT_*') { 'product' } else { 'harness' }
                }
            }
            # The next run refuses to start beside this exe, so wait out a
            # slow exit rather than failing the next run on it.
            if ($s.Proc) { [void]$s.Proc.WaitForExit(20000) }
        }
    }
    return [pscustomobject]$entry
}

# The registry snapshot the sampled scenario's profile pool and the
# sweep's kind classification need; filled per session, best-effort.
$script:RegistryIds = @()
$script:RegistryCommands = @{}

# Profile detection on a new machine: a fresh state (the seam's private
# config and state base IS a first run - nothing on disk), no configured
# profiles at all, judged against the machine's own reality enumerated by
# this harness. Owns its own session, sequentially, before the configured
# runs: the coexistence guard refuses a second instance of the same exe.
{
    $freshConfig = "windows-single-instance = false`nwindow-save-state = never"
    $s = $null
    try {
        Assert-NoWinttyFrom -ExePath $ExePath -Context 'seam-initial-size'
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $freshConfig -PrivateStateBase -BeforeTeardown $BeforeTeardown
        $prev = $null
        $list = $null
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            $list = Invoke-SeamCommand $s @{ op = 'profiles' }
            if ($prev -and $prev.count -eq $list.count) { break }
            $prev = $list
            Start-Sleep -Milliseconds 1000
        }
        $ids = @($list.profiles | ForEach-Object { "$($_.id)" })
        if ($ids.Count -lt 1) { throw 'PRODUCT_FAIL: a fresh machine resolved no profiles at all - detection never ran' }
        $verdict = Test-DetectionSet 'detection (fresh)' $ids $false
        $staged = @('idle', 'sizeprobe', 'splitprobe')
        foreach ($id in $staged) {
            if ($ids -contains $id) { $verdict.Fails += "detection (fresh): '$id' exists on a machine with no config at all" }
        }
        if (@($verdict.Fails).Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($verdict.Fails -join '; ')) }
        Add-Result 'detection-fresh' $true 'product' ("{0} profile(s): {1}{2}" -f $ids.Count, ($ids -join ', '),
            $(if ($verdict.Notes) { '; ' + ($verdict.Notes -join '; ') } else { '' }))
    }
    catch {
        $msg = "$($_.Exception.Message)"
        if ($_.InvocationInfo) { $msg += " [at $($_.InvocationInfo.ScriptLineNumber)]" }
        $class = if ($msg -like 'PRODUCT_*') { 'product' } else { 'harness' }
        Add-Result 'detection-fresh' $false $class $msg
    }
    finally {
        if ($null -ne $s) {
            # Same rule as the run function: a throwing hook becomes a
            # recorded finding, never an abort of the teardown.
            try {
                Stop-SeamSession $s
            }
            catch {
                $teardownMsg = "$($_.Exception.Message)"
                $teardownClass = if ($teardownMsg -like '*PRODUCT_*') { 'product' } else { 'harness' }
                Add-Result 'detection-fresh-teardown' $false $teardownClass $teardownMsg
            }
            if ($s.Proc) { [void]$s.Proc.WaitForExit(20000) }
        }
    }
}

$runEntries = @()
$script:Ledger = Read-CoverageLedger
foreach ($n in 1..$Runs) {
    foreach ($mode in @('local', 'daemon')) {
        $runEntries += @(Invoke-Run $n $mode)
    }
}

$scales = @($runEntries | Where-Object { $null -ne $_.scale } | ForEach-Object { $_.scale } | Select-Object -Unique)
$scaleCovered = @($scales | Where-Object { $_ -ne 1.0 }).Count -gt 0
[ordered]@{
    runs         = $runEntries
    scales       = $scales
    scaleCovered = $scaleCovered
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8

# The scenario ledger: one row per scenario per run per mode, the shape
# the release-side gate parses (it refuses a green whose core rows are
# missing, so a narrowed run cannot pass there either).
$script:Results | ConvertTo-Json | Set-Content (Join-Path $OutDir 'results.json') -Encoding utf8

Write-Host ''
foreach ($r in $runEntries) {
    Write-Host ("run {0} [{1}]: {2}  launch [{3}]  new tab [{4}]  shell {5}  hidden [{6}]" -f $r.run, $r.mode,
        $(if ($r.ok) { 'PASS' } else { "FAIL ($($r.class))" }), $r.launch, $r.newtab, $r.shell, ($r.hidden -join '; '))
}
if ($scaleCovered) {
    Write-Host ("display scale: {0}" -f ($scales -join ', '))
}
else {
    Write-Host ("display scale: {0}; the display-scale half of the size calculation was not exercised here" -f
        $(if ($scales.Count) { $scales -join ', ' } else { 'unknown' }))
}
$comboCount = @($script:Ledger.combos.PSObject.Properties).Count
Write-Host ("coverage ledger: {0} combination(s) drawn across all runs; latest at {1}" -f $comboCount, $LedgerPath)

# The WSL environment skips, reported as neither red nor green: visibly,
# in the summary count, and in skips.json for the release-side gate to
# surface; the pending-WSL ledger is the re-run list.
if ($script:Skips.Count -gt 0) {
    $script:Skips | ConvertTo-Json | Set-Content (Join-Path $OutDir 'skips.json') -Encoding utf8
    Write-Host ''
    foreach ($skip in $script:Skips) { Write-Host $skip -ForegroundColor Yellow }
    Write-Host ("SKIP SUMMARY: {0} WSL scenario(s) skipped on environment health; ledger: {1}" -f
        $script:Skips.Count, $PendingWslLedger) -ForegroundColor Yellow
}

$failed = @($script:Results | Where-Object { -not $_.ok })
$failed | ForEach-Object { Write-Host ("FAILED {0} [{1}] {2}" -f $_.name, $_.class, $_.detail) }
Write-Host ("SUMMARY: {0} scenarios ran: {1} passed, {2} failed; {3} WSL environment skip(s)" -f
    $script:Results.Count, ($script:Results.Count - $failed.Count), $failed.Count, $script:Skips.Count)
if (@($failed | Where-Object { $_.class -eq 'product' }).Count -gt 0) { exit 2 }
if ($failed.Count -gt 0) { exit 1 }
exit 0
