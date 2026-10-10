#requires -Version 7
<#
    Session-resumption rows for birth-coverage (wintty pro, founder request).

    Cold: no live daemon holds anything; the client boots the file session end
    to end and displays it. Hot: winttyd already holds the running session; a
    client only has to display (attach to) it.

    Why a sibling of seam-initial-size.ps1 and not rows in it: that harness's
    Invoke-Run hardcodes window-save-state=never and a fresh state tree per
    launch, and every row it has depends on both. A resume needs the opposite
    policy -- window-save-state=always -- plus a FRESH owned tree per pair:
    the save and its restore share one tree (save, kill, restore), but no
    pair boots on another's session.json -- so threading it through
    Invoke-Run would tangle every existing row. This file reuses its row
    vocabulary instead (the `& $Body` scenario pattern, settle budgets,
    results.json rows, PRODUCT_FAIL semantics, exit 0/2/1) and mirrors its
    oracles (copied, not shared: a shared helper would couple the two
    harnesses' edits).

    What each row proves, honestly:
      resume-cold[+@daemon]  session 1 builds two tabs and the debounced persist
        writes session.json under the pair's own owned tree; session 1 is
        killed (no clean shutdown -- the kill is why the config is `always`:
        `default` would not restore a dirty file); session 2 relaunches on
        the SAME pair tree and must show BOTH tabs before opening anything (a
        fresh launch would show one), each pane live with its marker on
        screen inside the cmd budget and judged by the birth oracles (floor
        born+settled, equality, wrap). The tree dies with the pair, so no
        later pair boots on its session.json.
      resume-cold-fallback[+@daemon]  same, but session 2's config withdraws
        the second profile: the registry must no longer resolve it (asserted),
        and the tab must still come back through the saved fallback command
        (SessionProfileResolver: a plain command replays verbatim; only a
        withdrawn built-in or retired template drops). Count stays two: a drop
        is a PRODUCT_FAIL with the resolver's name on it.
      resume-hot@daemon  the daemon already held the session. On this tree the
        probe (`pane-sessions`) is unserved and Start-SeamSession mints an
        isolated daemon world per launch while the guard refuses a second live
        client, so the row records a HARNESS gap (exit 1, never a product red
        and never a silent green) naming both missing pieces. Once served, its
        live asserts are: the pre-attach session list is non-empty (the daemon
        held it, the client did not boot it), and the attaching client's screen
        carries the first client's marker without opening any profile.

    Nothing is typed and no OS input is synthesized, same as the birth harness.
    Exits 0 on pass, 2 on a product finding, 1 when the harness could not run.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # Which rows to run: cold (both cold rows, both modes), hot (the hot row),
    # or all. Lets the coordinator re-run one path without the other.
    [ValidateSet('all', 'cold', 'hot')][string]$Scenario = 'all',
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

# The founder's floor (invariants.md I-1), mirrored from seam-initial-size.ps1:
# one edit there re-floors birth; this file follows it by hand and says so.
$MinCols = 2
$MinRows = 2
# I-3's signature length, mirrored: this many consecutive one-character
# viewport lines in a pane wider than one column is the #1262 fingerprint.
$WrapRunLimit = 10

# Birth markers, this harness's own: long enough that a one-column restore
# wraps them into far more one-char lines than the signature needs. The
# fallback tab replays its saved command on restore, so its marker must be
# the command's own bytes, stable across the save/restore boundary.
$MarkerCold = 'PANE-BIRTH-RESUME-COLD-MARKER-1234567890'
$MarkerVanish = 'PANE-BIRTH-RESUME-VANISH-MARKER-1234567890'

# Staged cmd budgets, mirrored from the birth harness's cmd kind: restored
# shells cold-start, so every first render here is a cold one.
$ColdReadySec = 10
$SettleSec = 15

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

# A leaf's own viewport. "no live surface" is a documented transient (the
# surface spawns asynchronously), so it retries within a short budget rather
# than failing the scenario outright; the surface-state readback names the
# stuck state when the budget runs out.
function Read-Screen($s, [int]$Index, [int]$Leaf) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ($true) {
        try {
            return Invoke-SeamCommand $s @{ op = 'screen-text'; index = $Index; leaf = $Leaf }
        }
        catch {
            if ($_.Exception.Message -notmatch 'no live surface' -or
                [DateTime]::UtcNow -ge $deadline) {
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
# layout pass still in flight is not taken for the answer.
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
# than one column. Cols <= 1 is the floor's finding, not this one's.
function Test-OneCharWrap([string]$What, [string]$Text, [int]$Cols) {
    if ($Cols -le 1) { return $null }
    $worst = Count-OneCharRun $Text
    if ($worst -ge $WrapRunLimit) {
        return "${What}: ${worst} consecutive one-character lines at cols ${Cols}: the one-char-per-line wrap signature (I-3)"
    }
    return $null
}

# The absolute-floor oracle on one restored pane: settled AND creation size
# meet the floor, the pty started at the settled size (every restored pane is
# read at its own rebirth -- a restore re-spawns every shell), and the screen
# carries no wrap signature.
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

# Wait until the pane's own program output is on screen, in either shape a
# grid can hold it: the marker as a line (sane grid) or its vertical wrap
# (the #1262 shape -- at least 8 one-char lines means output landed and
# wrapped, mirrored from seam-initial-size.ps1:560). 8 is deliberately below
# the $WrapRunLimit=10 signature Test-Newborn flags: arrival versus defect,
# so a marker-less 8-9 run returns here and the oracle still judges it
# non-signature. $null when nothing arrived inside the budget.
function Wait-PaneOutput($s, [int]$Index, [int]$Leaf, [string]$Marker, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $screen = Read-Screen $s $Index $Leaf
        if ((("$($screen.text)" -replace '\s', '') -like "*$Marker*")) { return $screen }
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

# The session under test right now: Invoke-Scenario's app-exited guard reads
# it, so every phase sets it before running scenarios against that session.
$script:Current = $null

# One scenario body to a row: a throw inside is classified, never silent,
# and never eats the rest of the run.
function Invoke-Scenario([string]$Name, [scriptblock]$Body) {
    if ($script:Current -and $script:Current.Proc.HasExited) {
        Add-Result $Name $false 'product' "APP_EXIT: the app exited (code $($script:Current.Proc.ExitCode)) before $Name"
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
        Add-Result $Name $false $class $msg
    }
}

# The session file the debounced persist writes under an owned tree. Poll,
# don't sleep once: the debounce is 750ms but a loaded machine owes nothing.
function Wait-SessionFile([string]$OwnedPath, [int]$WantTabs, [int]$TimeoutSec = 30) {
    $file = Join-Path $OwnedPath 'Wintty/session.json'
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $file) {
            try {
                $parsed = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
                $tabs = @(foreach ($w in @($parsed.Windows)) { foreach ($t in @($w.Tabs)) { $t } }).Count
                if ($tabs -ge $WantTabs) { return [pscustomobject]@{ Path = $file; Parsed = $parsed; Tabs = $tabs } }
            }
            catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

# One save phase: a session on the owned tree that builds the cold shape (the
# launch tab plus the vanishing tab), waits until the persist wrote both tabs
# to session.json, and records what the restore must show. The caller kills
# the session by stopping it: Stop-SeamSession kills the process, so no clean
# shutdown is ever written -- which is exactly why the config below is
# `always` and never `default`.
function Invoke-SavePhase([string]$Mode, [string]$OwnedPath, [string]$OwnedToken) {
    $daemon = ($Mode -eq 'daemon')
    $suffix = if ($daemon) { '@daemon' } else { '' }
    # The withdrawn-profile variant needs a second staged profile whose
    # command replays verbatim on restore (a plain cmd echo: re-spawnable, so
    # the resolver falls back instead of dropping -- see SessionRestorer).
    $config = @"
windows-single-instance = false
window-save-state = always
mux-attach = $($daemon ? 'true' : 'false')
default-profile = idle
profile.idle.name = Idle
profile.idle.command = cmd.exe /d /c echo $MarkerCold & ping -n 120 127.0.0.1 > nul
profile.vanishing.name = Vanishing
profile.vanishing.command = cmd.exe /d /c echo $MarkerVanish & ping -n 120 127.0.0.1 > nul
"@
    $s = $null
    try {
        Assert-NoWinttyFrom -ExePath $ExePath -Context 'seam-session-resume save'
        # The owned tree is adopted only when path AND token agree (the token
        # never reaches the app: Start strips it so a pane's shell cannot hand
        # this tree to a child harness). Re-exported before every launch: the
        # Stop below restores whatever the caller had, which after the first
        # phase is this same pair, but saying it again keeps the phases
        # independent of that ordering.
        $env:WINTTY_STATE_BASE = $OwnedPath
        $env:WINTTY_STATE_BASE_TOKEN = $OwnedToken
        # Forwarded here, never to the Stop-SeamSession in the finally below:
        # Stop appends, so the hook would run twice for one launch. No
        # -PrivateStateBase: it mints a fresh tree even over an owned one
        # (lib/seam-client.ps1), so the app would never write the owned
        # session.json Wait-SessionFile polls below.
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -BeforeTeardown $BeforeTeardown
        if ($s.StateBase -ne $OwnedPath) { throw "HARNESS: Start-SeamSession did not adopt the owned tree (saw '$($s.StateBase)', wanted '$OwnedPath')" }
        $script:Current = $s
        $opened = Invoke-SeamCommand $s @{ op = 'open-profile'; id = 'vanishing' }
        if (@($opened.state.tabs).Count -ne 2) { throw "HARNESS: open-profile left $(@($opened.state.tabs).Count) tabs, wanted 2" }
        $active = [int]$opened.state.active
        $screen0 = Wait-PaneOutput $s 0 -1 $MarkerCold $ColdReadySec
        if ($null -eq $screen0) { throw "PRODUCT_FAIL: the launch tab never showed its marker; screen: $(Format-Screen (Read-Screen $s 0 -1).text)" }
        $screen1 = Wait-PaneOutput $s $active -1 $MarkerVanish $ColdReadySec
        if ($null -eq $screen1) { throw "PRODUCT_FAIL: the vanishing tab never showed its marker; screen: $(Format-Screen (Read-Screen $s $active -1).text)" }
        # Tested is tested: both markers rendered AND the persist wrote both
        # tabs before the kill, or the restore below measures nothing.
        $saved = Wait-SessionFile $OwnedPath 2
        if ($null -eq $saved) { throw 'HARNESS: session.json never held both tabs inside 30s; the debounced persist did not fire' }
        return [pscustomobject]@{
            Suffix = $suffix; Mode = $Mode; Active = $active
            Clean = [bool]($saved.Parsed.CleanShutdown)
            Text0 = "$($screen0.text)"; Text1 = "$($screen1.text)"
        }
    }
    finally {
        $script:Current = $null
        if ($null -ne $s) {
            # A throwing hook becomes a recorded finding, never an abort of
            # the teardown: the kill below must still happen, or the restore
            # launch refuses beside this very exe.
            try {
                Stop-SeamSession $s
            }
            catch {
                Add-Result "resume-save-teardown$suffix" $false 'harness' "$($_.Exception.Message)"
            }
            if ($s.Proc) { [void]$s.Proc.WaitForExit(20000) }
        }
    }
}

# One restore phase: relaunch on the SAME owned tree (the file session.json is
# still there -- nothing deleted it) with $RestoreConfig, and judge what came
# back. $ExpectVanishing tells whether the config still stages the second
# profile ($true: plain cold, it must resolve; $false: fallback, it must NOT
# resolve and the tab must still come back through its saved command).
function Invoke-RestorePhase([string]$Row, [string]$Mode, [string]$OwnedPath, [string]$OwnedToken, [string]$RestoreConfig, [bool]$ExpectVanishing, [int]$SavedActive) {
    $s = $null
    try {
        Assert-NoWinttyFrom -ExePath $ExePath -Context 'seam-session-resume restore'
        $env:WINTTY_STATE_BASE = $OwnedPath
        $env:WINTTY_STATE_BASE_TOKEN = $OwnedToken
        # Forwarded here, never to the Stop-SeamSession in the finally below. No
        # -PrivateStateBase, same reason as the save phase: the restore must
        # adopt the save's owned tree, or it boots fresh and proves nothing.
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $RestoreConfig -BeforeTeardown $BeforeTeardown
        if ($s.StateBase -ne $OwnedPath) { throw "HARNESS: Start-SeamSession did not adopt the owned tree (saw '$($s.StateBase)', wanted '$OwnedPath')" }
        $script:Current = $s
        Invoke-Scenario $Row {
            # The withdrawal is only honest if asserted: with the profile gone
            # from the registry a two-tab restore proves the fallback path.
            $list = Invoke-SeamCommand $s @{ op = 'profiles' }
            $ids = @($list.profiles | ForEach-Object { "$($_.id)" })
            if ($ExpectVanishing -and $ids -notcontains 'vanishing') {
                throw "HARNESS: the restore config stages no 'vanishing' profile, so the plain cold row measures the fallback path by accident"
            }
            if (-not $ExpectVanishing -and $ids -contains 'vanishing') {
                throw "HARNESS: the restore config still resolves 'vanishing', so the fallback row measures the plain path by accident"
            }
            $st = Invoke-SeamCommand $s @{ op = 'get-state' }
            $count = @($st.state.tabs).Count
            # THE cold assertion: both tabs are already here before this
            # session opened anything. A fresh launch would show exactly one.
            if ($count -ne 2) {
                throw ("PRODUCT_FAIL: the restore showed $count tab(s), wanted 2 (saved 2)" +
                    $(if (-not $ExpectVanishing) { '; the withdrawn tab did not fall back to its saved command' } else { '' }))
            }
            if ([int]$st.state.active -ne $SavedActive) {
                throw "PRODUCT_FAIL: the restore activated tab $([int]$st.state.active), saved active was $SavedActive"
            }
            $fails = @()
            $details = @()
            # Restored background tabs do not paint until first shown (only
            # the active one settles at restore), so each tab is selected
            # before it is read -- the same shape the hidden-floor row drives.
            $markers = @($MarkerCold, $MarkerVanish)
            for ($i = 0; $i -lt 2; $i++) {
                [void](Invoke-SeamCommand $s @{ op = 'select'; index = $i })
                $size = Get-Settled $s $i $SettleSec
                $screen = Wait-PaneOutput $s $i -1 $markers[$i] $ColdReadySec
                if ($null -eq $screen) {
                    $fails += "restored tab ${i}: its program output never reached the screen inside ${ColdReadySec}s (I-2); screen: $(Format-Screen (Read-Screen $s $i -1).text)"
                    continue
                }
                $fails += @(Test-Newborn "restored tab $i" $size $screen.text)
                $details += "tab ${i}: born+settled $($size.cols)x$($size.rows) in $($size.widthPx)x$($size.heightPx) px, wraprun $(Count-OneCharRun $screen.text)"
            }
            if ($fails.Count -gt 0) { throw ('PRODUCT_FAIL: ' + ($fails -join '; ')) }
            "restored 2/2 tabs (saved active $SavedActive); $($details -join '; ')"
        }
    }
    finally {
        $script:Current = $null
        if ($null -ne $s) {
            try {
                Stop-SeamSession $s
            }
            catch {
                Add-Result "$Row-teardown" $false 'harness' "$($_.Exception.Message)"
            }
            if ($s.Proc) { [void]$s.Proc.WaitForExit(20000) }
        }
    }
}

$savedEnv = @{}
foreach ($n in 'WINTTY_STATE_BASE', 'WINTTY_STATE_BASE_TOKEN') {
    $savedEnv[$n] = if (Test-Path "Env:$n") { (Get-Item "Env:$n").Value } else { $null }
}
# A FRESH owned tree per pair, not one for the run: a save and its restore
# share one tree, but no pair sees another's session.json. With
# window-save-state=always ANY boot on a tree holding session.json restores
# (SessionGate Always) and the restore's own persist re-saves at once
# (SessionManager debounce), so a shared tree hands the next save a restore
# (its open-profile then lands a third tab). Fresh trees keep each pair's
# cold proof honest. Under temp, so the guard's state rule holds; the token
# keeps a pane's shell from adopting it.
function Remove-ResumeOwned($owned, [string]$Tag, [string[]]$Rows) {
    # A failed pair keeps its session file; it is the only record of what
    # the save wrote and what the restore read.
    $failed = @($script:Results | Where-Object {
            $r = $_
            (-not $r.ok) -and (@($Rows | Where-Object { $r.name -like "$_*" }).Count -gt 0)
        })
    if ($failed.Count -gt 0) {
        Copy-Item -LiteralPath (Join-Path $owned.Path 'Wintty') -Destination (Join-Path $OutDir "resume-state-$Tag") -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $owned.Path -Recurse -Force -ErrorAction SilentlyContinue
}
try {
    if ($Scenario -in @('all', 'cold')) {
        foreach ($mode in @('local', 'daemon')) {
            $daemon = ($mode -eq 'daemon')
            $suffix = if ($daemon) { '@daemon' } else { '' }
            # The cold pair on its own tree: the save and its restore share
            # it, and nothing else ever boots on it.
            $ownedCold = New-WinttyOwnedStateBase
            try {
                $save = $null
                try {
                    $save = Invoke-SavePhase $mode $ownedCold.Path $ownedCold.Token
                }
                catch {
                    $msg = "$($_.Exception.Message)"
                    $class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
                    Add-Result "resume-cold$suffix" $false $class "save phase: $msg"
                    Add-Result "resume-cold-fallback$suffix" $false $class "save phase failed, so the fallback restore never ran: $msg"
                    continue
                }
                $base = @"
windows-single-instance = false
window-save-state = always
mux-attach = $($daemon ? 'true' : 'false')
default-profile = idle
profile.idle.name = Idle
profile.idle.command = cmd.exe /d /c echo $MarkerCold & ping -n 120 127.0.0.1 > nul
profile.vanishing.name = Vanishing
profile.vanishing.command = cmd.exe /d /c echo $MarkerVanish & ping -n 120 127.0.0.1 > nul
"@
                # The plain cold restore: the same config, so both profiles
                # resolve and both tabs come back by id. A launch throw (guard
                # refusal, pipe timeout) is ledgered here in the save's
                # catch-shape, so later rows still run: Invoke-RestorePhase
                # only finally-teardowns, and seam-initial-size.ps1 contains
                # each row the same way (Invoke-Scenario classifies+records).
                try {
                    Invoke-RestorePhase "resume-cold$($save.Suffix)" $mode $ownedCold.Path $ownedCold.Token $base $true $save.Active
                }
                catch {
                    $msg = "$($_.Exception.Message)"
                    $class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
                    Add-Result "resume-cold$($save.Suffix)" $false $class "restore phase: $msg"
                }
            }
            finally {
                Remove-ResumeOwned $ownedCold "cold$suffix" @("resume-cold$suffix", "resume-save-teardown$suffix")
            }
            # The fallback pair on its own tree: a fresh save, then a restore
            # whose config no longer stages `vanishing`, so the tab can only
            # come back through its saved fallback command.
            $ownedFallback = New-WinttyOwnedStateBase
            try {
                $withdrawn = @"
windows-single-instance = false
window-save-state = always
mux-attach = $($daemon ? 'true' : 'false')
default-profile = idle
profile.idle.name = Idle
profile.idle.command = cmd.exe /d /c echo $MarkerCold & ping -n 120 127.0.0.1 > nul
"@
                try {
                    $save2 = Invoke-SavePhase $mode $ownedFallback.Path $ownedFallback.Token
                }
                catch {
                    $msg = "$($_.Exception.Message)"
                    $class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
                    Add-Result "resume-cold-fallback$suffix" $false $class "save phase: $msg"
                    continue
                }
                # The fallback restore on the fallback pair's own save: a launch
                # throw is ledgered in the same catch-shape, so the next mode
                # still runs.
                try {
                    Invoke-RestorePhase "resume-cold-fallback$suffix" $mode $ownedFallback.Path $ownedFallback.Token $withdrawn $false $save2.Active
                }
                catch {
                    $msg = "$($_.Exception.Message)"
                    $class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
                    Add-Result "resume-cold-fallback$suffix" $false $class "restore phase: $msg"
                }
            }
            finally {
                Remove-ResumeOwned $ownedFallback "fallback$suffix" @("resume-cold-fallback$suffix", "resume-save-teardown$suffix")
            }
        }
    }

    if ($Scenario -in @('all', 'hot')) {
        # HOT: the daemon already holds the session; the client only displays
        # it. Staged only in daemon mode (mux-attach=true): a local-only hot
        # row would be a contradiction in terms. Its own tree, like every
        # pair above: no scenario boots on another's session.json.
        $ownedHot = New-WinttyOwnedStateBase
        $s = $null
        try {
            Assert-NoWinttyFrom -ExePath $ExePath -Context 'seam-session-resume hot'
            $env:WINTTY_STATE_BASE = $ownedHot.Path
            $env:WINTTY_STATE_BASE_TOKEN = $ownedHot.Token
            $config = @"
windows-single-instance = false
window-save-state = always
mux-attach = true
default-profile = idle
profile.idle.name = Idle
profile.idle.command = cmd.exe /d /c echo $MarkerCold & ping -n 120 127.0.0.1 > nul
"@
            # Forwarded here, never to the Stop-SeamSession in the finally below. No
            # -PrivateStateBase: the hot launch adopts the owned tree too, or
            # its probe runs on a minted tree and proves nothing about it.
            $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -BeforeTeardown $BeforeTeardown
            if ($s.StateBase -ne $ownedHot.Path) { throw "HARNESS: Start-SeamSession did not adopt the owned tree (saw '$($s.StateBase)', wanted '$($ownedHot.Path)')" }
            $script:Current = $s
            Invoke-Scenario 'resume-hot@daemon' {
                # The proof this row owes: the daemon held the session BEFORE
                # any client displayed it. Without the tier's session op there
                # is no handle on "held", so no attach can be staged or proved
                # -- and a green here would claim one was.
                $sessions = $null
                try { $sessions = Invoke-SeamCommand $s @{ op = 'pane-sessions' } }
                catch { }
                if ($null -eq $sessions) {
                    throw 'HARNESS: resume-hot needs the daemon pane-sessions op and a daemon world shared across two client launches; this tree serves no sessions ops (the daemon dimension is inert -- mux-attach names nothing here), Start-SeamSession mints an isolated daemon world per launch, and the coexistence guard refuses a second live client on the same exe. No hot attach can be staged or proved through the seam today -- see REPORT for the seam gaps.'
                }
                # Served. An answer is still not the proof: the daemon world is
                # minted per launch, so the list here is this launch's own
                # just-booted session, and the old count passed the row
                # vacuously with a lying detail (@(...) over an absent
                # sessions property even counted $null as 1). Finish the row
                # instead of going green: it needs a second client on a shared
                # daemon world and the attach probe.
                throw 'HARNESS: pane-sessions is served, but this row is not finished: the daemon world is minted per launch, so the answer only lists this launch''s own just-booted session, and the guard refuses a second live client on the same exe. Finish the row when the tier shares a daemon world: probe the held list from a second client, attach, and assert the first client''s marker without opening any profile.'
            }
        }
        catch {
            # A hot launch throw (guard refusal, pipe timeout) is ledgered in
            # the same catch-shape, so the ledger names the row instead of
            # skipping it silently: seam-initial-size.ps1's detection-fresh
            # block records its launch the same way.
            $msg = "$($_.Exception.Message)"
            $class = if ($msg -like 'PRODUCT_*' -or $msg -like 'APP_EXIT*') { 'product' } else { 'harness' }
            Add-Result 'resume-hot@daemon' $false $class "launch: $msg"
        }
        finally {
            $script:Current = $null
            if ($null -ne $s) {
                try {
                    Stop-SeamSession $s
                }
                catch {
                    Add-Result 'resume-hot@daemon-teardown' $false 'harness' "$($_.Exception.Message)"
                }
                if ($s.Proc) { [void]$s.Proc.WaitForExit(20000) }
            }
            Remove-ResumeOwned $ownedHot 'hot-daemon' @('resume-hot@daemon')
        }
    }
}
finally {
    foreach ($n in @($savedEnv.Keys)) {
        if ($null -ne $savedEnv[$n]) { Set-Item "Env:$n" $savedEnv[$n] }
        else { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
    }
}

# The scenario ledger: one row per scenario per mode, the shape the
# release-side gate parses ({name, ok, class, detail}).
$script:Results | ConvertTo-Json | Set-Content (Join-Path $OutDir 'results.json') -Encoding utf8

Write-Host ''
$failed = @($script:Results | Where-Object { -not $_.ok })
$failed | ForEach-Object { Write-Host ("FAILED {0} [{1}] {2}" -f $_.name, $_.class, $_.detail) }
Write-Host ("SUMMARY: {0} scenarios ran: {1} passed, {2} failed" -f
    $script:Results.Count, ($script:Results.Count - $failed.Count), $failed.Count)
if (@($failed | Where-Object { $_.class -eq 'product' }).Count -gt 0) { exit 2 }
if ($failed.Count -gt 0) { exit 1 }
exit 0
