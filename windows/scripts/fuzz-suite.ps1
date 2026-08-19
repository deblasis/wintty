#requires -Version 7
<#
    The GUI fuzz suite: one entry point over the harnesses in this directory.

    Each harness already knows how to drive Wintty and judge what it sees.
    What was missing was something that runs them in a known order, keeps
    their verdicts apart, and cannot quietly turn a failure into a pass.

    That last part is the reason this exists. The suite's own most likely
    defect is the one that looks like success, and it is not hypothetical:
    vtabs-visual-qa.ps1 marked a step OK whenever the sub-script did not
    throw, and a sub-script that exits 2 does not throw - so a run that
    found real defects printed all green. aot-fuzz.ps1 retried every
    non-zero exit, product findings included, and reported only the last
    attempt. -SelfTest runs the runner against fixtures that exit each way
    on purpose, so those two failure modes are checked rather than assumed.
    It needs no build and no desktop.

    Verdicts, from each harness's exit code:

      0  pass
      2  product findings, in the build under test
      1  the harness could not run, so nothing is known about the product

    A 1 is retried, because a run that never started tells you nothing and
    the causes are usually transient - the window never appeared, something
    stole the foreground. A 2 is never retried: re-running a real defect
    until it passes is how a regression gets buried.

    That split only works because each harness leaves with the right code.
    They signal defects by throwing, and an unhandled throw makes pwsh return
    1, so every one of them opens with a trap that maps a PRODUCT_FAIL throw
    to 2 and lets anything else through as 1. HARVEST_MISS and
    FOREGROUND_MISS stay 1 on purpose: a refused click is usually another app
    taking the foreground, not a defect.

    Each harness also gets a wall-clock budget - four times its manifest
    estimate, floor three minutes - after which it is killed and recorded as
    1. A harness that wedges holding the foreground would otherwise stop the
    run dead and leave the desktop unusable.

    The suite's own exit code follows the same numbering. Findings win over
    harness failures, because a finding is the actionable result; harnesses
    that could not run are reported separately as incomplete coverage rather
    than folded into either.

    What this does NOT do: judge anything itself. It reports what the
    harnesses report, and several check far less than their names suggest -
    tab-colors reads no pixel, loop saves screenshots and reads none of them
    back, mica-dpi never changes the DPI. The `oracle` field in the manifest
    below says what each one actually rules out, so a green suite is not read
    as more than it is. Keep those strings honest: they were wrong here once,
    in the direction of promising checks the code did not contain.

    Three integrity checks run on every invocation, including -List, and all
    are free: the manifest cannot name a script that is gone, a script cannot
    sit in this directory unclassified, and a harness cannot stop declaring a
    parameter the manifest passes it. That last one matters because
    `pwsh -File` ignores an argument the script does not declare, so a
    renamed -ExePath would leave every harness quietly testing its own
    default build.

    Usage:

        just fuzz                     # everything, against the Debug build
        just fuzz "-Tag smoke"        # the fast, high-signal subset
        just fuzz "-Only search,loop"
        just fuzz-list                # the manifest, no desktop needed
        just fuzz-selftest            # prove the runner classifies correctly
#>
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '../Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe'),
    [string]$OutRoot = (Join-Path $PSScriptRoot ('fuzz-out/suite-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string[]]$Tag,
    [string[]]$Only,
    [string[]]$Skip,
    [int]$Seed = 1337,
    # Retries apply to exit 1 only. 0 disables them. Range-checked because a
    # negative value would skip the run loop entirely, leaving a null exit
    # code that reads as a pass.
    [ValidateRange(0, 10)][int]$Retries = 1,
    [switch]$List,
    [switch]$SelfTest,
    # Used by -SelfTest, not meant to be called directly: runs the same
    # fixtures through the ORDINARY report path so the child's real process
    # exit code can be asserted. Without it the self-test would exercise
    # everything except the two lines that actually end the run.
    [switch]$SelfTestInner,
    # Stop at the first harness that reports findings, leaving its artifacts
    # as the newest thing on disk. Off by default: one broken area should
    # not hide the state of the rest.
    [switch]$StopOnFindings
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
$ErrorActionPreference = 'Stop'

# A harness exiting non-zero is this runner's input, not an error. When
# $PSNativeCommandUseErrorActionPreference is on - it is the default on some
# hosts and a profile can set it - $ErrorActionPreference = 'Stop' turns the
# first harness that reports findings into a terminating error, and the run
# ends with no summary and no verdict. Assign it only where it exists.
if (Test-Path Variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

# name      short id, what -Only and -Skip match
# script    relative to this directory
# tags      -Tag matches any
# outDir    does it take -OutDir
# seed      does it take -Seed
# minutes   rough wall clock, so a full run can be planned around. Only the
#           smoke four are measured; the rest are upper-bound guesses
# oracle    what a pass from this harness actually rules out
$Harnesses = @(
    [ordered]@{ name = 'search';         script = 'search-fuzz.ps1';               tags = @('smoke','search'); outDir = $true;  seed = $true;  minutes = 2
                oracle = 'counts matches in the terminal UIA document itself; printable non-space needles are checked against that count, the rest only for a well-formed counter' }
    [ordered]@{ name = 'probe';          script = 'mouse-fuzz-probe.ps1';          tags = @('smoke','core');   outDir = $true;  seed = $false; minutes = 1
                oracle = 'liveness: the app survived the click stream and crash.log did not grow' }
    [ordered]@{ name = 'loop';           script = 'mouse-fuzz-loop.ps1';           tags = @('smoke','core','chrome'); outDir = $true; seed = $false; minutes = 1
                oracle = 'liveness across a click stream over the chrome; it saves screenshots but reads no pixel back, and skips any affordance it cannot find' }
    [ordered]@{ name = 'vertical-tabs';  script = 'mouse-fuzz-vertical-tabs.ps1';  tags = @('smoke','tabs');   outDir = $true;  seed = $false; minutes = 1
                oracle = 'measures the pane width through collapse and expand, so a dead toggle fails' }
    [ordered]@{ name = 'tab-colors';     script = 'mouse-fuzz-tab-colors.ps1';     tags = @('tabs');           outDir = $true;  seed = $false; minutes = 3
                oracle = 'drives every preset plus None, recolor and a layout round-trip, and asserts the swatches were findable and the layout switched; it compares no pixel, so a build that paints them all alike passes' }
    [ordered]@{ name = 'morph';          script = 'vtabs-morph-fuzz.ps1';          tags = @('tabs');           outDir = $false; seed = $true;  minutes = 3
                oracle = 'randomized layout switching against a full strip, checked against a trace the product emits; a seed replays the sequence' }
    [ordered]@{ name = 'inspector';      script = 'mouse-fuzz-inspector.ps1';      tags = @('inspector');      outDir = $true;  seed = $false; minutes = 3
                oracle = 'the inspector opens, renders something other than a flat surface, and closes; the tab-switch dismissal is gated but a missing second tab only warns' }
    [ordered]@{ name = 'dialogs';        script = 'mouse-fuzz-dialogs.ps1';        tags = @('dialogs');        outDir = $true;  seed = $false; minutes = 2
                oracle = 'each of About, Keyboard Shortcuts and the inspector toggle opened a window, plus liveness' }
    [ordered]@{ name = 'settings';       script = 'mouse-fuzz-settings.ps1';       tags = @('dialogs');        outDir = $true;  seed = $false; minutes = 2
                oracle = 'the settings window opens with three named vertical-tab cards and the Keybindings page does not take the app down' }
    [ordered]@{ name = 'confirm-always'; script = 'mouse-fuzz-confirm-always.ps1'; tags = @('dialogs');        outDir = $true;  seed = $false; minutes = 2
                oracle = 'the close confirmation appears for a single-pane tab under confirm-close-surface=always' }
    [ordered]@{ name = 'ime-cjk';        script = 'mouse-fuzz-ime-cjk.ps1';        tags = @('input');          outDir = $true;  seed = $false; minutes = 2
                oracle = 'liveness pasting CJK and supplementary-plane text; the paste itself is not read back' }
    [ordered]@{ name = 'kitty';          script = 'mouse-fuzz-kitty.ps1';          tags = @('vt');             outDir = $true;  seed = $false; minutes = 2
                oracle = 'scans the surface for the image the kitty sequence should have drawn, so a dropped image fails' }
    [ordered]@{ name = 'osc-paste';      script = 'mouse-fuzz-osc-paste.ps1';      tags = @('vt');             outDir = $true;  seed = $false; minutes = 2
                oracle = 'the window title changes to what the OSC sequence set, plus liveness' }
    [ordered]@{ name = 'undo-osc';       script = 'mouse-fuzz-undo-osc.ps1';       tags = @('vt');             outDir = $true;  seed = $false; minutes = 2
                oracle = 'liveness across undo after split, reopen-closed-tab, and console title' }
    [ordered]@{ name = 'mica-dpi';       script = 'mouse-fuzz-mica-dpi.ps1';       tags = @('chrome');         outDir = $true;  seed = $false; minutes = 3
                oracle = 'two backdrop presets and one palette backdrop round-trip into the config file; it reads DPI once and never changes it, and checks PerMonitorV2 by grepping the manifest source' }
    [ordered]@{ name = 'remain';         script = 'mouse-fuzz-remain.ps1';         tags = @('chrome');         outDir = $true;  seed = $false; minutes = 3
                oracle = 'liveness plus the tab overview opening; rename, color, snap, zoom, paste and quake are driven and logged but not gated, so a build missing all six still passes' }
    [ordered]@{ name = 'remain-title';   script = 'mouse-fuzz-remain-title.ps1';   tags = @('session');        outDir = $true;  seed = $false; minutes = 2
                oracle = 'the surviving tab keeps the default profile title after a tab closes; its config defines one profile, so the cross-shell case its header describes is not staged' }
    [ordered]@{ name = 'jumplist';       script = 'mouse-fuzz-jumplist.ps1';       tags = @('shell');          outDir = $true;  seed = $false; minutes = 3
                oracle = 'jump-list CLI arguments reach the running primary and open what they name, across five checks' }
    [ordered]@{ name = 'splash-race';    script = 'splash-single-instance-race.ps1'; tags = @('startup');      outDir = $false; seed = $false; minutes = 2
                oracle = 'samples the window list for a splash owned by a secondary; demonstrates the race, does not certify its absence' }
)

# Deliberately not in the manifest. This is a list rather than a comment
# because the integrity check below reads it: a new harness dropped into this
# directory has to be classified, and prose does not fail a run.
$NotInSuite = [ordered]@{
    'fuzz-suite.ps1'                = 'this runner'
    'aot-fuzz.ps1'                  = 'a runner; targets the NativeAOT publish, which this suite can also do with -ExePath'
    'vtabs-visual-qa.ps1'           = 'a runner'
    'release-smoke.ps1'             = 'a runner'
    'verified-input-probe.ps1'      = 'leaves its window up for inspection by design, which would make the next harness refuse, and its PASS_PENDING_SCREENSHOT is not self-checked'
    'mouse-smoke-run.ps1'           = 'the operator drives the checklist by hand'
    'vtabs-layout-switch-capture.ps1' = 'produces frames for a human to look at; no verdict to aggregate'
    'vtabs-switcher-capture.ps1'    = 'produces frames for a human to look at; no verdict to aggregate'
    'vtabs-morph-filmstrip.ps1'     = 'produces frames for a human to look at; no verdict to aggregate'
    'gen-bell.ps1'                  = 'generates a test asset'
}

$SelfTestHarnesses = @(
    [ordered]@{ name = 'st-pass';       script = 'lib/fuzz-selftest/pass.ps1';       tags = @('selftest'); outDir = $true;  seed = $false; minutes = 0; oracle = 'fixture' }
    [ordered]@{ name = 'st-findings';   script = 'lib/fuzz-selftest/findings.ps1';   tags = @('selftest'); outDir = $true;  seed = $false; minutes = 0; oracle = 'fixture' }
    [ordered]@{ name = 'st-cannot-run'; script = 'lib/fuzz-selftest/cannot-run.ps1'; tags = @('selftest'); outDir = $true;  seed = $false; minutes = 0; oracle = 'fixture' }
    [ordered]@{ name = 'st-throws';     script = 'lib/fuzz-selftest/throws.ps1';     tags = @('selftest'); outDir = $true;  seed = $false; minutes = 0; oracle = 'fixture' }
    [ordered]@{ name = 'st-flaky';      script = 'lib/fuzz-selftest/flaky.ps1';      tags = @('selftest'); outDir = $true;  seed = $false; minutes = 0; oracle = 'fixture' }
    [ordered]@{ name = 'st-no-outdir';  script = 'lib/fuzz-selftest/no-outdir.ps1';  tags = @('selftest'); outDir = $false; seed = $true;  minutes = 0; oracle = 'fixture' }
    [ordered]@{ name = 'st-product-throw'; script = 'lib/fuzz-selftest/product-throw.ps1'; tags = @('selftest'); outDir = $true; seed = $false; minutes = 0; oracle = 'fixture' }
    [ordered]@{ name = 'st-unknown-code';  script = 'lib/fuzz-selftest/unknown-code.ps1';  tags = @('selftest'); outDir = $true; seed = $false; minutes = 0; oracle = 'fixture' }
    # Deliberately short budget: the point is the runaway guard, not the wait.
    [ordered]@{ name = 'st-hangs';         script = 'lib/fuzz-selftest/hangs.ps1';         tags = @('selftest'); outDir = $true; seed = $false; minutes = 0; timeoutSeconds = 2; oracle = 'fixture' }
)

# `pwsh -File` hands every argument over as a string, so `-Only search,loop`
# arrives as one element "search,loop" and matches nothing. Splitting here
# means the documented form works, and the -Only typo check below still
# catches a genuinely wrong name rather than blaming the whole list.
function Split-List {
    param([string[]]$Value)
    if (-not $Value) { return $Value }
    return @($Value | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}
$Tag  = Split-List $Tag
$Only = Split-List $Only
$Skip = Split-List $Skip

# $null is handled explicitly rather than left to [int] coercion, which would
# turn "no exit code was ever collected" into 0, into 'pass'. Only -Retries
# validation currently keeps that unreachable, and a guard three hundred lines
# away is not a guard.
function Get-Verdict {
    param($Code)
    if ($null -eq $Code) { return 'error' }
    switch ([int]$Code) {
        0       { 'pass' }
        2       { 'findings' }
        1       { 'harness' }
        default { 'error' }
    }
}

# The single place the run's outcome is decided. The self-test asserts against
# THIS function rather than re-deriving the same rules, because a self-test
# that checks a copy of the logic passes happily while the shipped roll-up is
# broken - which is the whole failure this suite exists to catch.
function Get-SuiteOutcome {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Rows,
          [Parameter(Mandatory)][int]$SelectedCount)

    $findings = @($Rows | Where-Object { $_.verdict -eq 'findings' })
    $broken   = @($Rows | Where-Object { $_.verdict -eq 'harness' -or $_.verdict -eq 'error' })
    $notRun   = $SelectedCount - @($Rows).Count

    # Findings outrank a harness that could not run: a finding is actionable,
    # and incomplete coverage is reported alongside rather than folded in.
    $code = if ($findings.Count -gt 0) { 2 }
            elseif ($broken.Count -gt 0 -or $notRun -gt 0) { 1 }
            else { 0 }

    [ordered]@{ findings = $findings; broken = $broken; notRun = $notRun; exit = $code }
}

# Start-Process -ArgumentList does NOT quote array elements: hand it a path
# containing a space and pwsh receives two arguments and prints its usage.
# Every path here is caller-supplied, so quote before handing them over.
function ConvertTo-ProcessArgs {
    param([Parameter(Mandatory)][string[]]$Argv)
    return @($Argv | ForEach-Object {
        if ($_ -match '\s' -and $_ -notmatch '^".*"$') { '"' + $_ + '"' } else { $_ }
    })
}

# Waits for a child, echoing its log as it grows, and kills it if it outstays
# its budget. Returns the child's exit code, or 1 on a timeout: a harness that
# had to be killed judged nothing, which is exactly what 1 means.
function Wait-ChildWithTail {
    param(
        [Parameter(Mandatory)]$Child,
        [Parameter(Mandatory)][string]$LogPath,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [Parameter(Mandatory)][string]$Label
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $offset = 0
    while (-not $Child.HasExited) {
        if ((Get-Date) -gt $deadline) {
            Write-Host ("  {0} exceeded its {1}s budget; killing it" -f $Label, $TimeoutSeconds) -ForegroundColor Yellow
            try { $Child.Kill($true); [void]$Child.WaitForExit(5000) } catch { }
            $offset = Write-NewLines -Path $LogPath -Offset $offset
            return 1
        }
        $offset = Write-NewLines -Path $LogPath -Offset $offset
        Start-Sleep -Milliseconds 250
    }
    [void]$Child.WaitForExit(5000)
    # Once more after exit: the last writes land between the final poll and
    # the process going away.
    [void](Write-NewLines -Path $LogPath -Offset $offset)
    return $Child.ExitCode
}

# Echoes whatever has been appended to a file since $Offset, and returns the
# new offset. Opened share-write because the child still holds it open.
function Write-NewLines {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][int]$Offset)
    if (-not (Test-Path -LiteralPath $Path)) { return $Offset }
    try {
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
                                     [System.IO.FileAccess]::Read,
                                     [System.IO.FileShare]::ReadWrite)
        try {
            if ($fs.Length -le $Offset) { return $Offset }
            [void]$fs.Seek($Offset, [System.IO.SeekOrigin]::Begin)
            $sr = New-Object System.IO.StreamReader($fs)
            $text = $sr.ReadToEnd()
            if ($text) { Write-Host $text.TrimEnd() }
            return [int]$fs.Length
        } finally { $fs.Dispose() }
    } catch { return $Offset }
}

# Runs one harness to a verdict, retrying only what is worth retrying.
# Returns the row that goes into summary.json.
function Invoke-Harness {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Harness,
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Exe,
        [int]$SeedValue,
        [int]$RetryCount
    )

    $scriptPath = Join-Path $PSScriptRoot $Harness.script
    $out = Join-Path $Root $Harness.name
    New-Item -ItemType Directory -Force -Path $out | Out-Null

    # Four times the manifest estimate, floor three minutes. Generous, because
    # this is a runaway guard and not a performance assertion.
    $timeoutSeconds = if ($Harness.Contains('timeoutSeconds')) { [int]$Harness.timeoutSeconds }
                      else { [int][math]::Max(180, $Harness.minutes * 60 * 4) }

    $code = $null
    $attempts = 0
    $started = Get-Date
    for ($try = 0; $try -le $RetryCount; $try++) {
        if ($try -gt 0) {
            Write-Host ("  retry {0}/{1} ({2} could not run)" -f $try, $RetryCount, $Harness.name) -ForegroundColor Yellow
            # Keep the failed attempt. The harness writes result.json and
            # shots/ to a fixed path, so a retry would overwrite the evidence
            # of why the first attempt could not run - which is the only
            # thing that explains an eventual pass on a flaky harness.
            $kept = "$out.attempt$try"
            Remove-Item -Recurse -Force $kept -ErrorAction SilentlyContinue
            try { Move-Item -LiteralPath $out -Destination $kept -ErrorAction Stop } catch { }
            New-Item -ItemType Directory -Force -Path $out | Out-Null
            Start-Sleep -Seconds 2
        }
        $argv = @('-NoProfile', '-File', $scriptPath, '-ExePath', $Exe)
        if ($Harness.outDir) { $argv += @('-OutDir', $out) }
        if ($Harness.seed)   { $argv += @('-Seed', $SeedValue) }

        $attempts++
        # Start-Process rather than `& pwsh`, because a call operator cannot
        # be interrupted: a harness that wedges with the foreground grabbed
        # would otherwise hang the whole run with no way out but Ctrl-C.
        # Output is redirected to files and tailed back to the host, which
        # keeps the live progress a long run needs.
        $log = Join-Path $out 'console.log'
        $errLog = Join-Path $out 'console.err.log'
        $child = Start-Process -FilePath 'pwsh' -ArgumentList (ConvertTo-ProcessArgs $argv) `
                               -PassThru -NoNewWindow `
                               -RedirectStandardOutput $log -RedirectStandardError $errLog
        $code = Wait-ChildWithTail -Child $child -LogPath $log -TimeoutSeconds $timeoutSeconds -Label $Harness.name

        # Only "could not run" is worth another go. A finding is a result.
        if ($code -ne 1) { break }
    }

    [ordered]@{
        name     = $Harness.name
        script   = $Harness.script
        exit     = $code
        verdict  = Get-Verdict $code
        attempts = $attempts
        seconds  = [int]((Get-Date) - $started).TotalSeconds
        outDir   = $out
        oracle   = $Harness.oracle
    }
}

# ---- manifest integrity ---------------------------------------------------
# All of this is cheap and needs no desktop, and it runs on every invocation
# including -List. Between them these three checks cover the ways the manifest
# rots, in both directions.
$problems = @()

# 1. The manifest names something that is not there any more.
foreach ($h in @($Harnesses) + @($SelfTestHarnesses)) {
    if (-not (Test-Path (Join-Path $PSScriptRoot $h.script))) {
        $problems += "manifest names a script that does not exist: $($h.name) -> $($h.script)"
    }
}

# 2. A harness exists that the manifest does not name. Without this the
#    manifest silently shrinks: delete an entry and the suite reports PASS
#    for an area it never touched.
$claimed = @(@($Harnesses) | ForEach-Object { Split-Path -Leaf $_.script })
foreach ($f in Get-ChildItem -Path $PSScriptRoot -Filter '*.ps1' -File) {
    if ($claimed -contains $f.Name) { continue }
    if ($NotInSuite.Contains($f.Name)) { continue }
    $problems += "$($f.Name) is in this directory but neither in the manifest nor in `$NotInSuite; classify it"
}

# 3. The manifest's calling convention still matches what each script accepts.
#    `pwsh -File` silently ignores a parameter the script does not declare, so
#    a renamed -ExePath would leave every harness quietly testing its own
#    default build while the suite reported on the exe you asked for.
foreach ($h in @($Harnesses) + @($SelfTestHarnesses)) {
    $path = Join-Path $PSScriptRoot $h.script
    if (-not (Test-Path $path)) { continue }
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
    $declared = @()
    if ($ast.ParamBlock) { $declared = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath }) }
    foreach ($need in @('ExePath') + $(if ($h.outDir) { 'OutDir' }) + $(if ($h.seed) { 'Seed' })) {
        if ($declared -notcontains $need) {
            $problems += "$($h.name) is called with -$need but $($h.script) does not declare it"
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Host 'manifest integrity:' -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

if ($List) {
    $Harnesses | ForEach-Object {
        [pscustomobject]@{ name = $_.name; tags = ($_.tags -join ','); minutes = $_.minutes; oracle = $_.oracle }
    } | Format-Table -AutoSize -Wrap
    $total = ($Harnesses | ForEach-Object { $_.minutes } | Measure-Object -Sum).Sum
    Write-Host ("{0} harnesses, about {1} minutes for a full run" -f $Harnesses.Count, $total)
    exit 0
}

# ---- selection ------------------------------------------------------------
# Both switches run the fixtures; only -SelfTest asserts. -SelfTestInner
# falls through to the real report block and exits on its verdict.
$useFixtures = $SelfTest -or $SelfTestInner
$all = if ($useFixtures) { @($SelfTestHarnesses) } else { @($Harnesses) }

# The assertions are written against the whole fixture set at exactly one
# retry, so anything that would change either is refused rather than
# silently ignored.
if ($SelfTest -and ($Tag -or $Only -or $Skip -or
                    $PSBoundParameters.ContainsKey('Retries') -or
                    $PSBoundParameters.ContainsKey('Seed') -or
                    $StopOnFindings)) {
    Write-Host '-SelfTest asserts against the whole fixture set at one retry; it takes no filters, -Retries, -Seed or -StopOnFindings' -ForegroundColor Red
    exit 1
}

# An unknown name in -Only is a typo, and silently running a smaller suite
# than was asked for is the failure mode this whole script exists to avoid.
if ($Only) {
    $known = @($all | ForEach-Object { $_.name })
    $unknown = @($Only | Where-Object { $known -notcontains $_ })
    if ($unknown.Count -gt 0) {
        Write-Host ("-Only names no such harness: {0}" -f ($unknown -join ', ')) -ForegroundColor Red
        exit 1
    }
}

$selected = $all
if ($Tag)  { $selected = @($selected | Where-Object { @($_.tags | Where-Object { $Tag -contains $_ }).Count -gt 0 }) }
if ($Only) { $selected = @($selected | Where-Object { $Only -contains $_.name }) }
if ($Skip) { $selected = @($selected | Where-Object { $Skip -notcontains $_.name }) }
$selected = @($selected)

if ($selected.Count -eq 0) {
    Write-Host 'no harness matched the filters; run with -List to see the manifest' -ForegroundColor Red
    exit 1
}

New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null

# Before the exe check and the gate, so a filter typo or a missing build is
# diagnosed against a visible selection rather than in the dark.
Write-Host ("run:  {0} harness(es) - {1}" -f $selected.Count, (($selected | ForEach-Object { $_.name }) -join ', '))
Write-Host "out:  $OutRoot"

if ($useFixtures) {
    # The fixtures ignore it, and pointing at a real exe would suggest they
    # do not.
    $ExePath = 'selftest-no-exe'
    # The assertions below cover the whole fixture set and each fixture's
    # expected attempt count, so both are fixed here rather than taken from
    # the caller. -Seed is what st-no-outdir checks was passed through.
    $Retries = 1
    $Seed = 4242
} else {
    if (-not (Test-Path $ExePath)) { throw "missing exe: $ExePath (build it first: just build-dll build-win)" }
    $ExePath = (Resolve-Path -LiteralPath $ExePath).Path
    # Once, up front. Each harness gates itself too, but paying for that at
    # the start of a 40-minute run rather than 30 minutes in is the point.
    Assert-NoWintty -Context 'The fuzz suite'
    Write-Host "exe:  $ExePath"
}

# ---- run ------------------------------------------------------------------
$rows = @()
# One stamp per harness, taken immediately before it launches, not one for the
# whole run. A single stamp at minute zero means every sweep for the next 40
# minutes matches anything from this exe started at any point in them - and
# the default exe is the one `just run-win` opens, so a window the developer
# starts by hand mid-run gets tree-killed with its shell.
$script:CurrentStamp = $null
try {
    foreach ($h in $selected) {
        Write-Host ("`n===== {0} =====" -f $h.name) -ForegroundColor Cyan
        if (-not $useFixtures) { $script:CurrentStamp = Get-WinttyLaunchStamp }
        $row = Invoke-Harness -Harness $h -Root $OutRoot -Exe $ExePath -SeedValue $Seed -RetryCount $Retries
        $rows += $row

        $colour = switch ($row.verdict) { 'pass' { 'Green' } 'findings' { 'Red' } default { 'Yellow' } }
        Write-Host ("{0}: {1} (exit {2}, {3}s)" -f $h.name, $row.verdict, $row.exit, $row.seconds) -ForegroundColor $colour

        if (-not $useFixtures) {
            # Reap anything the harness left behind, before the next one's gate
            # refuses over it. A harness that tore down cleanly makes this a
            # no-op.
            Stop-WinttyStartedAfter -Since $script:CurrentStamp -ExePath $ExePath
            $script:CurrentStamp = $null
            Start-Sleep -Milliseconds 800
        }
        if ($StopOnFindings -and $row.verdict -eq 'findings') {
            Write-Host 'stopping at the first findings (-StopOnFindings)' -ForegroundColor Yellow
            break
        }
    }
}
finally {
    # Ctrl-C on a 40-minute run that holds the foreground is not an edge case.
    # Without this the harness that was in flight keeps its window, and every
    # later run refuses over it. Only reachable with a stamp set, so it can
    # never bind $null to the mandatory -Since.
    if ($script:CurrentStamp) {
        Stop-WinttyStartedAfter -Since $script:CurrentStamp -ExePath $ExePath
    }
}

# ---- self-test assertions -------------------------------------------------
# The fixtures exit each way on purpose; what is under test here is the
# runner. attempts is half the point: a product finding that gets retried is
# a regression this suite would hide, and a retry that does not actually
# re-run the harness is a retry that proves nothing.
if ($SelfTest) {
    $expect = @(
        @{ name = 'st-pass';       verdict = 'pass';     attempts = 1; why = 'a clean harness runs once' }
        @{ name = 'st-findings';   verdict = 'findings'; attempts = 1; why = 'findings are a result, never retried' }
        @{ name = 'st-cannot-run'; verdict = 'harness';  attempts = 2; why = 'a harness that could not run is retried' }
        @{ name = 'st-throws';     verdict = 'harness';  attempts = 2; why = 'an unhandled throw is a harness failure, not a pass' }
        @{ name = 'st-flaky';      verdict = 'pass';     attempts = 2; why = 'the retry re-runs rather than replaying the first verdict' }
        @{ name = 'st-no-outdir';  verdict = 'pass';     attempts = 1; why = '-OutDir is omitted and -Seed is passed through' }
        @{ name = 'st-product-throw'; verdict = 'findings'; attempts = 1; why = 'a thrown PRODUCT_FAIL leaves with 2, not the retryable 1' }
        @{ name = 'st-unknown-code'; verdict = 'error';    attempts = 1; why = 'an exit code outside the convention is not a pass' }
        @{ name = 'st-hangs';      verdict = 'harness';  attempts = 2; why = 'a wedged harness is killed at its budget and treated as retryable' }
    )
    $bad = @()
    # One row per harness and nothing else. A harness's console output
    # leaking into the result set is not cosmetic: it inflates the pass
    # count that gets reported and fills summary.json with printed lines.
    if ($rows.Count -ne $expect.Count) {
        $bad += "collected $($rows.Count) result(s) for $($expect.Count) harnesses; something other than verdicts is in the result set"
    }
    foreach ($e in $expect) {
        $got = $rows | Where-Object { $_.name -eq $e.name } | Select-Object -First 1
        if (-not $got) { $bad += "$($e.name): did not run at all"; continue }
        if ($got.verdict -ne $e.verdict) {
            $bad += "$($e.name): verdict $($got.verdict), expected $($e.verdict) - $($e.why)"
        }
        if ($got.attempts -ne $e.attempts) {
            $bad += "$($e.name): $($got.attempts) attempt(s), expected $($e.attempts) - $($e.why)"
        }
    }
    # The aggregate matters as much as the rows, and it is checked by calling
    # the same Get-SuiteOutcome the real run exits on - not by re-deriving it
    # here. This fixture set holds two findings, four that could not run (an
    # exit code outside the convention, and one that had to be killed) and
    # three passes, so every branch of the roll-up has a witness.
    $outcome = Get-SuiteOutcome -Rows $rows -SelectedCount $selected.Count
    if ($outcome.exit -ne 2) {
        $bad += "aggregate exit is $($outcome.exit), expected 2 (findings outrank harness failures)"
    }
    if ($outcome.findings.Count -ne 2) {
        $bad += "roll-up counted $($outcome.findings.Count) findings, expected 2"
    }

    # The trap that maps PRODUCT_FAIL to 2 must not cost the cleanup: that is
    # where XDG_CONFIG_HOME is restored and where the process sweep lives.
    $ptDir = Join-Path $OutRoot 'st-product-throw'
    if (-not (Test-Path (Join-Path $ptDir 'finally-ran.txt'))) {
        $bad += 'st-product-throw exited 2 without running its finally, so a real harness would leak its config dir and its window'
    }
    if ($outcome.broken.Count -ne 4) {
        $bad += "roll-up counted $($outcome.broken.Count) harness failures, expected 4"
    }
    if ($outcome.notRun -ne 0) {
        $bad += "roll-up counted $($outcome.notRun) not reached, expected 0"
    }

    # Everything above inspects objects in this process. The two lines that
    # actually end a real run - the report block and `exit $outcome.exit` -
    # are only reached by a run that does not stop to assert, so run one and
    # read its process exit code. The fixtures hold a findings, so 2.
    # Not $Args: that is an automatic variable, and shadowing it in a function
    # makes the splat below behave in ways that are not worth debugging.
    function Invoke-Inner {
        param([string]$Name, [string[]]$Extra)
        # The space is deliberate. Start-Process does not quote its argument
        # array, so a path containing one silently turns into two arguments
        # and pwsh prints its usage instead of running the harness. Nothing
        # would notice until someone checked the repo out under a path with a
        # space in it.
        $root = Join-Path $OutRoot "inner $Name"
        $argv = @('-NoProfile', '-File', $PSCommandPath, '-SelfTestInner', '-OutRoot', $root) + $Extra
        & pwsh @argv | Out-Null
        return @{ exit = $LASTEXITCODE; root = $root }
    }

    $full = Invoke-Inner -Name 'full' -Extra @()
    if ($full.exit -ne 2) {
        $bad += "a real run over the same fixtures exited $($full.exit), expected 2"
    }
    if (-not (Test-Path (Join-Path $full.root 'summary.json'))) {
        $bad += 'a real run over the same fixtures wrote no summary.json'
    }

    # The filters are refused under -SelfTest, so without these child runs
    # nothing ever exercises Split-List, the -Only typo check, or the three
    # Where-Object filters. An inverted -Skip is silent in the safe-looking
    # direction: it runs more than you asked for and still reports.
    $onlyClean = Invoke-Inner -Name 'only-clean' -Extra @('-Only', 'st-pass,st-no-outdir')
    if ($onlyClean.exit -ne 0) {
        $bad += "-Only over two clean fixtures exited $($onlyClean.exit), expected 0 - the filter ran more than it was asked for"
    }
    $onlyFindings = Invoke-Inner -Name 'only-findings' -Extra @('-Only', 'st-findings')
    if ($onlyFindings.exit -ne 2) {
        $bad += "-Only st-findings exited $($onlyFindings.exit), expected 2"
    }
    $skipped = Invoke-Inner -Name 'skip' -Extra @('-Only', 'st-pass,st-findings', '-Skip', 'st-findings')
    if ($skipped.exit -ne 0) {
        $bad += "-Skip st-findings exited $($skipped.exit), expected 0 - the skip did not take"
    }
    $typo = Invoke-Inner -Name 'typo' -Extra @('-Only', 'st-pass,st-nope')
    if ($typo.exit -ne 1) {
        $bad += "-Only with an unknown name exited $($typo.exit), expected 1 - a typo must not silently shrink the run"
    }

    Write-Host ''
    if ($bad.Count -gt 0) {
        Write-Host 'SELFTEST FAILED' -ForegroundColor Red
        $bad | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        exit 1
    }
    Write-Host ("SELFTEST OK  {0} exit paths classified correctly, and a real run over them exits 2" -f $expect.Count) -ForegroundColor Green
    exit 0
}

# ---- report ---------------------------------------------------------------
$outcome  = Get-SuiteOutcome -Rows $rows -SelectedCount $selected.Count
$findings = $outcome.findings
$broken   = $outcome.broken
$notRun   = $outcome.notRun

$summary = [ordered]@{
    exe              = $ExePath
    seed             = $Seed
    selected         = @($selected | ForEach-Object { $_.name })
    findings         = @($findings | ForEach-Object { $_.name })
    couldNotRun      = @($broken | ForEach-Object { $_.name })
    skippedAfterStop = $notRun
    results          = $rows
}
$summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutRoot 'summary.json') -Encoding utf8

Write-Host ''
$rows | ForEach-Object { [pscustomobject]$_ } | Format-Table name, verdict, exit, attempts, seconds -AutoSize
Write-Host "summary -> $(Join-Path $OutRoot 'summary.json')"

if ($broken.Count -gt 0) {
    Write-Host ("{0} harness(es) could not run, so nothing is known about the area they cover: {1}" -f
        $broken.Count, (($broken | ForEach-Object { $_.name }) -join ', ')) -ForegroundColor Yellow
}
if ($notRun -gt 0) {
    Write-Host ("{0} harness(es) were not reached after -StopOnFindings" -f $notRun) -ForegroundColor Yellow
}

if ($findings.Count -gt 0) {
    Write-Host ("FINDINGS in {0} harness(es): {1}" -f $findings.Count, (($findings | ForEach-Object { $_.name }) -join ', ')) -ForegroundColor Red
} elseif ($outcome.exit -eq 0) {
    Write-Host ("PASS  {0} harness(es), 0 findings" -f @($rows).Count) -ForegroundColor Green
}
exit $outcome.exit
