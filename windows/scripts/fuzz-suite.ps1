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
    Most signal defects by throwing, and an unhandled throw makes pwsh return
    1, so they open with a trap that maps a PRODUCT_FAIL throw to 2 and lets
    anything else through as 1. HARVEST_MISS and FOREGROUND_MISS stay 1 on
    purpose: a refused click is usually another app taking the foreground,
    not a defect.

    search-fuzz.ps1 reaches the same split the other way, and the difference
    matters when reading its exits: it records findings with a kind, and its
    tail exits 2 when any finding is not a harness one, 1 when they all are.
    A harness that cannot establish the corpus its oracle measures against
    leaves with 1 through that path, not 2.

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

    A tier build adds its own harnesses by dropping fuzz-tier-harnesses.ps1
    beside this runner; they append to the base set, so a tier runs what it was
    built on plus what it adds. Absent means base-only. -RequireLayer makes that
    absence an error for a build that should have one. -SelfTest covers that
    merge from a copy of this directory rather than from this one, because a
    fixture manifest placed here would be found by every other run from it.

    Four integrity checks run on every invocation, including -List, and all
    are free: the manifest cannot name a script that is gone, a script cannot
    sit in this directory unclassified, a harness cannot stop declaring a
    parameter the manifest passes it, and no name or tag may hold the comma
    the filters split on. The third matters because `pwsh -File` ignores an
    argument the script does not declare, so a renamed -ExePath would leave
    every harness quietly testing its own default build.

    A filter typo is refused on every invocation too, -List included, so the
    one flag that needs no desktop also answers whether a name is real.

    Usage:

        just fuzz                     # everything, against the Debug build
        just fuzz "-Tag smoke"        # the fast, high-signal subset
        just fuzz "-Only search,loop"
        just fuzz-list                # the manifest, no desktop needed
        just fuzz-selftest            # prove the runner classifies correctly
        ... -RequireLayer pro         # refuse to run the base set alone
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
    # Also used by -SelfTest only: marks the child it runs to prove that -SelfTest
    # refuses filters, so that child does not run one of its own if the refusal
    # ever stops refusing. A parameter rather than an environment variable,
    # because an ambient one is set by anything in the process tree and turns the
    # guard off silently - the self-test then reports the same case count with
    # the check gone, which is the shape of defect this file exists to refuse.
    [switch]$SelfTestRefusalChild,
    # Stop at the first harness that reports findings, leaving its artifacts
    # as the newest thing on disk. Off by default: one broken area should
    # not hide the state of the rest.
    [switch]$StopOnFindings,
    # Refuse to run unless the tier layer of this name is present. A tier's own
    # recipe passes it, so an overlay that failed to place the manifest is exit 1
    # rather than a green run over the base set - the two are the same event from
    # here, and without this only the malformed case was loud. The summary is
    # shape-identical to an oss run, so nothing downstream could tell either.
    [string]$RequireLayer
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

# Said before anything can refuse this run, because the run it belongs to is
# meant to be refused. The self-test below starts a child that must leave on the
# filter refusal, and hands it this flag so that a refusal which stopped
# refusing would not have the child start a self-test of its own, and that one
# another. Without a marker of its own in the child's output, a parent that
# stopped passing the flag looks exactly the same from outside - refused for its
# filter, with the recursion bound handed to nobody - so the parent requires
# this line rather than the flag's absence being invisible.
if ($SelfTestRefusalChild) {
    Write-Host 'selftest: refusal child; it will not start a self-test of its own'
}

# name      short id, what -Only and -Skip match
# script    relative to this directory
# tags      -Tag matches any
# outDir    does it take -OutDir
# seed      does it take -Seed
# minutes   rough wall clock, so a full run can be planned around. Only the
#           smoke four are measured; the rest are upper-bound guesses
# oracle    what a pass from this harness actually rules out
$Harnesses = [System.Collections.Generic.List[object]]@(
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
    'fuzz-tier-harnesses.ps1'       = 'the tier layer manifest, not a harness; read below'
}

# The one form every check compares a script path in: the path relative to this
# directory, resolved. A tier's paths go through here ONCE, at the merge below,
# and what comes back is STORED on the entry - so the inventory, the tier's
# claimed scripts, both halves of integrity check 2 and the run loop all read
# the same string rather than each reducing a manifest's spelling for itself.
# The base manifest and $NotInSuite are written in that form already, which is
# why they are read here as literals rather than through this.
#
# Reducing by prefix was not enough, and was the last shape of the defect this
# file keeps growing: a value normalised for one check and compared raw by the
# next. It took exactly one leading '.\', so 'lib\..\x.ps1', '.\.\x.ps1' and
# '..\<this directory>\x.ps1' each named a file sitting beside the runner and
# none of them compared equal to 'x.ps1' - each one reopening the wrong-blame
# failure at check 2, and each one able to excuse a harness through notInSuite.
# Resolving the path closes all of them together.
#
# $null means the path does not name something inside this directory: it climbed
# out, it was absolute, or it could not be resolved at all. That is also the
# answer the escape guard below needs, which is the other reason the resolution
# belongs here rather than beside it - the guard resolved the path, refused on
# it, and then threw the resolved form away.
function ConvertTo-ScriptKey {
    param([string]$Path)
    $text = "$Path".Trim()
    if (-not $text) { return $null }
    $rootFull = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    # The two-argument overload rather than Join-Path, which is what left the
    # guard dead for an absolute path: joining one onto the root produced
    # 'C:\<root>\C:\...\x.ps1', which is inside the root, so the guard passed it
    # and check 1 blamed a script that does not exist instead.
    try { $full = [System.IO.Path]::GetFullPath($text, $rootFull) } catch { return $null }
    $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $null }
    return $full.Substring($prefix.Length)
}

# Everything this file ships beside itself, as paths relative to this directory:
# the harnesses above plus everything deliberately outside them. Integrity check
# 2 is what makes that an inventory rather than a claim - a script sitting here
# in neither collection fails every invocation, -List included. Taken while both
# are still literals, because the tier merge below appends a tier's own scripts
# to both and those are not what this file ships.
$BaseScripts = @(
    @($Harnesses  | ForEach-Object { $_.script }) +
    @($NotInSuite.Keys | ForEach-Object { [string]$_ })
)

# ---- tier layer -----------------------------------------------------------
# A tier build ships the harnesses above PLUS its own, because a tier is the
# thing it was built on plus what it adds. The base set is not optional for a
# tier: a Pro build still has to pass everything oss passes, and running only
# the Pro harnesses against it would report on the smaller half.
#
# The layer arrives as a file the tier overlay drops next to this one, so oss
# needs no knowledge of which tiers exist and a tier needs no patch against
# this runner. Absent means base-only, which is what a public build gets.
#
# It is read strictly rather than leniently. A tier manifest that is malformed,
# names a missing script, or collides with a base name would otherwise produce
# a SMALLER suite that still reports PASS - the same silent-shrink failure
# integrity check 2 exists to stop, arriving by a different door.
$TierManifestPath = Join-Path $PSScriptRoot 'fuzz-tier-harnesses.ps1'
$TierLayerName = $null

foreach ($h in $Harnesses) { $h.layer = 'base' }

# The caller's half of the layer comparison, put into its final form once, in
# the same place and for the same reason the manifest's half is: Split-List
# trims -Tag, -Only and -Skip, and this was the one caller-supplied string left
# reading raw. It is compared against a name that IS trimmed, so -RequireLayer
# ' pro ' refused a build declaring 'pro' - a false refusal on the one flag
# whose whole job is to prove the overlay landed, and the one flag a build
# recipe passes from a variable, where trailing whitespace is likeliest.
$RequireLayer = "$RequireLayer".Trim()

# Passed and empty is not the same as not passed, and the trim above is exactly
# what makes them look alike: every test below reads this value for truth, so a
# recipe whose layer variable came out empty would skip the check silently and
# get the base-only run this flag exists to refuse. Read off the binding rather
# than off the value, because the value can no longer tell the two apart.
if ($PSBoundParameters.ContainsKey('RequireLayer') -and -not $RequireLayer) {
    Write-Host ('-RequireLayer was given nothing to require. It is a build asserting that its own overlay ' +
                'landed, so an empty one asserts nothing and would pass on the base set.') -ForegroundColor Red
    exit 1
}

if ($RequireLayer -and -not (Test-Path -LiteralPath $TierManifestPath)) {
    Write-Host ("-RequireLayer '$RequireLayer' but no tier manifest sits beside this runner. " +
                "Either the overlay did not place fuzz-tier-harnesses.ps1, or this is not the " +
                "tier you think it is.") -ForegroundColor Red
    exit 1
}

if (Test-Path -LiteralPath $TierManifestPath) {
    $declared = & $TierManifestPath
    if ($null -eq $declared) {
        Write-Host "tier layer: $TierManifestPath returned nothing" -ForegroundColor Red
        exit 1
    }

    # More than one object out of one file is what an overlay that appended
    # rather than replaced leaves behind, and what keeping both sides of a merge
    # conflict produces. Member enumeration hides it rather than failing: across
    # an array .layer stringifies to the names joined and .harnesses to both
    # sets, so everything merges under a layer named after neither of them.
    # Phrased as a collection rather than a count, because the count can be one:
    # a manifest ending `,@( @{...} )` emits a single-element array, and telling
    # its author it emitted one object when it must emit exactly one is not a
    # diagnosis. What is wrong is the wrapper, at any length.
    #
    # [array] is the right test HERE and the wrong one for notInSuite below,
    # which is why the two are not written alike. This value came off a
    # pipeline, and the pipeline unrolls any enumerable and collects it again
    # as object[] - so a manifest emitting a List arrives here as an array
    # whatever it wrote. notInSuite is read as a property instead, so it keeps
    # the type the manifest gave it and an [array] test there misses every
    # other shape.
    if ($declared -is [array]) {
        Write-Host ("tier layer: $TierManifestPath emitted a collection of $($declared.Count); it must emit exactly one object") -ForegroundColor Red
        exit 1
    }

    # One object with a layer name and its harnesses, so the layer can be
    # named in output without inferring it from the harnesses. Membership is
    # tested by value rather than through PSObject.Properties, which does not
    # index a Hashtable the way it indexes a custom object - the manifest is
    # written as a hashtable literal, so that distinction is the difference
    # between reading it and rejecting every valid one.
    if (-not $declared.layer -or -not $declared.harnesses) {
        Write-Host "tier layer: expected an object with 'layer' and 'harnesses'" -ForegroundColor Red
        exit 1
    }

    # Trimmed before it is tested, compared or stored, like every other field
    # the merge reads. The name is matched against 'base' and against
    # -RequireLayer and printed by -List, so a padded one took the name this
    # runner reserves for its own harnesses and -List reported
    # 'layers: base (19) +  base  (1)'.
    $TierLayerName = ([string]$declared.layer).Trim()
    # A name that is only padding is no name, the same way an empty oracle is no
    # oracle. It is what -List prints and what -RequireLayer matches, so a blank
    # one merges a layer that -List then reports as base-only - this feature's
    # own failure mode, arriving through the field that names it.
    if (-not $TierLayerName) {
        Write-Host "tier layer: the layer name is blank; it is what -List prints and what -RequireLayer matches" -ForegroundColor Red
        exit 1
    }
    if ($RequireLayer -and $TierLayerName -ne $RequireLayer) {
        Write-Host ("-RequireLayer '$RequireLayer' but the manifest declares '$TierLayerName'") -ForegroundColor Red
        exit 1
    }
    if ($TierLayerName -eq 'base') {
        Write-Host "tier layer: 'base' is the name this runner gives its own harnesses" -ForegroundColor Red
        exit 1
    }
    $baseNames = @($Harnesses | ForEach-Object { $_.name })
    $tierNames = @()
    $tierProblems = @()

    foreach ($h in @($declared.harnesses)) {
        # Copied into an ordered hashtable rather than used as it arrives.
        # A manifest may reasonably be written with [pscustomobject]@{} or
        # [ordered]@{}, and the two answer different APIs: .Contains() does not
        # exist on a PSCustomObject, and assigning a property it does not
        # already have fails. Normalising once here means everything after this
        # sees the same shape the base manifest uses.
        $entry = [ordered]@{}
        if ($h -is [System.Collections.IDictionary]) {
            foreach ($k in $h.Keys) { $entry[[string]$k] = $h[$k] }
        } else {
            foreach ($prop in $h.PSObject.Properties) { $entry[$prop.Name] = $prop.Value }
        }

        foreach ($key in @('name', 'script', 'tags', 'outDir', 'seed', 'minutes', 'oracle')) {
            if (-not $entry.Contains($key)) {
                $tierProblems += "tier harness is missing '$key': $($entry.name)"
            }
        }

        # Everything a later check reads is put into its final form HERE, once,
        # and STORED. Round after round of review found the same defect in a
        # different field - a value normalised for one check and then compared
        # raw by the next - so no field is normalised where it is tested any
        # more. What is stored is what -List prints, what -Only, -Skip and -Tag
        # match, what the collision checks compare and what the run loop
        # launches: a value that gets through this block is reachable by every
        # one of them, or by none of them.
        #
        # Only keys that are already present are touched. Assigning into an
        # ordered hashtable CREATES the key, and the presence check above has to
        # see a missing one as missing.
        foreach ($key in @('name', 'script', 'oracle')) {
            if ($entry.Contains($key)) { $entry[$key] = "$($entry[$key])".Trim() }
        }

        # Present but empty, for the three that are read as text. An empty name
        # is a blank row in -List that neither -Only nor -Skip can select, and
        # an OutDir that resolves to the run root. An empty script slips both
        # the path guard below and integrity check 1 - Test-Path on the
        # directory itself succeeds - and surfaces only as check 3 saying
        # nothing declares -ExePath, which reads as a broken harness rather
        # than a broken manifest. An empty oracle is a harness claiming a pass
        # rules something out without saying what, and the header above is
        # explicit that those strings were already wrong here once. Read off
        # what was stored: a name of ' ' is empty and a name of ' search ' is
        # the base harness it collides with, and both were true only of the
        # value the old test trimmed and threw away.
        foreach ($key in @('name', 'script', 'oracle')) {
            if ($entry.Contains($key) -and -not $entry[$key]) {
                $tierProblems += "tier harness has an empty '$key': $($entry.name)"
            }
        }

        # minutes is the dangerous one, so it is coerced here rather than trusted.
        # It reaches [math]::Max(180, $_ * 60 * 4) in the run loop, and a string
        # multiplies by REPEATING - '2' * 60 is a 60-character string - which then
        # throws out of a foreach that has no catch. That kills the report block, so
        # every verdict already collected, findings included, is discarded and the
        # run exits 1 "could not run" instead of 2 "findings". Tier harnesses append
        # last, so on a full run it is the base results that are thrown away.
        if ($entry.Contains('minutes')) {
            $asInt = $entry.minutes -as [int]
            if ($null -eq $asInt) {
                $tierProblems += "tier harness '$($entry.name)' has a non-numeric minutes: $($entry.minutes)"
            } else {
                $entry.minutes = $asInt
            }
        }

        # timeoutSeconds is minutes' twin and was left out of every rule minutes
        # got: not required, so its absence is fine, but read straight by the run
        # loop when it is there. A non-numeric one throws out of the same
        # uncaught foreach with the same result - the report block dies, every
        # verdict already collected is discarded, and the run exits 1 "could not
        # run" where 2 "findings" was owed. Tier harnesses append last, so it is
        # the base results that go.
        #
        # Optional, so absence is not a problem, but a value that is present is
        # coerced and range-checked. It is the override that SKIPS the
        # [math]::Max(180, ...) floor minutes goes through, so nothing else
        # stands between a budget of 0 and every attempt being killed on the
        # spot - which reads as a wedged harness rather than as a bad manifest.
        # A null coerces to 0 rather than to $null, so the floor catches it and
        # the numeric test cannot.
        #
        # What is REFUSED here is what carries the fix, and the self-test pins
        # both refusals. Storing the coerced value has no witness and cannot
        # have one: the run loop hands it to a parameter typed [int], and
        # binding performs the same coercion, so a version that tested and threw
        # the result away would behave identically. It is stored anyway, because
        # the rule this block exists to keep is that a value is put into its
        # final form once and read as stored - and because that is what lets the
        # run loop read it without a cast of its own.
        if ($entry.Contains('timeoutSeconds')) {
            $asInt = $entry.timeoutSeconds -as [int]
            if ($null -eq $asInt) {
                $tierProblems += "tier harness '$($entry.name)' has a non-numeric timeoutSeconds: $($entry.timeoutSeconds)"
            } elseif ($asInt -lt 1) {
                $tierProblems += "tier harness '$($entry.name)' has a timeoutSeconds of $asInt; a budget below one second kills every attempt the moment it starts"
            } else {
                $entry.timeoutSeconds = $asInt
            }
        }

        # A null or empty tags list passes a key-presence check and then quietly
        # excludes the harness from every -Tag run, which is how CI invokes this.
        # Put through the same trim-and-drop rule Split-List applies to the
        # caller's -Tag values, and STORED that way rather than only tested that
        # way: selection compares -Tag against the value as stored, so a tag of
        # ' tier ' listed as a real one while no -Tag argument could reach it -
        # Split-List trims the caller's side down to 'tier' and the two never
        # meet. Testing the normalised list also settles the values that are not
        # strings: they are compared as strings anyway, so a tag of 0 is exactly
        # as selectable as '0' and is kept.
        #
        # The other half of Split-List's rule, a comma, is not checked here: it
        # applies to a harness name as much as to a tag, and to the base
        # manifest as much as to a tier's. It is integrity check 4, over the
        # merged set.
        if ($entry.Contains('tags')) {
            $entry.tags = @($entry.tags | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
        }
        if (-not $entry.tags) {
            $tierProblems += "tier harness '$($entry.name)' declares no tags, so no -Tag run would ever select it"
        }

        # Resolved against this directory and STORED in the one form the checks
        # compare, by the same call that answers the guard. A path that climbs
        # out lands outside the tree the suite is meant to cover, and combined
        # with the relative-path comparison in check 2 below it can also excuse
        # an unrelated script sitting here unclassified. A path that cannot be
        # resolved at all is not inside this directory either, and lands here
        # rather than ending the run on an exception.
        #
        # The message quotes the path as the manifest wrote it, because that is
        # the string its author has to go and find; everything after this reads
        # the resolved one.
        if ($entry.script) {
            $scriptKey = ConvertTo-ScriptKey $entry.script
            if ($null -eq $scriptKey) {
                $tierProblems += "tier harness '$($entry.name)' names a script outside this directory: $($entry.script)"
            } else {
                $entry.script = $scriptKey
            }
        }
        if ($entry.name -and ($baseNames -contains $entry.name)) {
            # -Only and -Skip match on name, so a collision would make one of
            # the two unreachable, and which one would depend on order.
            $tierProblems += "tier harness '$($entry.name)' collides with a base harness of the same name"
        }
        if ($entry.name -and ($tierNames -contains $entry.name)) {
            $tierProblems += "tier harness '$($entry.name)' is declared twice in the tier manifest"
        }
        $tierNames += $entry.name

        $entry.layer = $TierLayerName
        $Harnesses.Add($entry)
    }

    # The base set carries five scripts that are runners or assets rather than
    # harnesses. A tier shipping its own had no way to say so: check 2 refuses
    # the run, and the only escapes were patching this file, which is what the
    # layer exists to avoid, or declaring it a harness, which is worse.
    # Presence, not truthiness. An empty list is falsy, so `notInSuite = @()`
    # skipped this block entirely and reached integrity check 2 instead, where
    # the tier author is told to classify a script rather than told that the
    # classification they wrote is the wrong shape.
    if ($null -ne $declared.notInSuite) {
        # Read the same two ways a harness entry is, and for the same reason: a
        # PSCustomObject has no .Keys at all, so the foreach ran over $null and
        # did nothing, and the run then died at integrity check 2 telling the
        # tier author to classify a file they had classified.
        $rawNotInSuite = [ordered]@{}
        if ($declared.notInSuite -is [System.Collections.IDictionary]) {
            foreach ($k in $declared.notInSuite.Keys) { $rawNotInSuite[[string]$k] = [string]$declared.notInSuite[$k] }
        } elseif ($declared.notInSuite -is [System.Collections.IEnumerable]) {
            # The reason is not decoration: it is the only place a tier says why
            # a script of its own is not a harness. A bare list also reads as an
            # object whose properties are Length and Rank, so left to the branch
            # below it would classify those and nothing else.
            #
            # Asked as "enumerable", not as "is it System.Array". A List[string]
            # or an ArrayList is neither an array nor a dictionary, so it fell
            # through to the branch below and had Count and Capacity read off it
            # as file names - the tier is then told to classify a file it
            # classified, which is the exact wrong-blame the pairs-form refusal
            # exists to close. Reached only after the dictionary branch above,
            # which is the one enumerable that must not land here; a string is
            # one too, and belongs here.
            $tierProblems += 'tier notInSuite must be written as name = reason pairs, not a list of names'
        } else {
            foreach ($prop in $declared.notInSuite.PSObject.Properties) { $rawNotInSuite[[string]$prop.Name] = [string]$prop.Value }
        }

        # One trim over whichever shape it arrived in, rather than one per
        # branch. A symmetric rule written twice is a rule with two coverage
        # requirements, and the halves came apart before. The names are
        # normalised on the way in and not on the way out: check 2 compares
        # against a stored path, so a key held with padding excuses nothing
        # while passing every check below - the same wrong-blame failure by the
        # door this normalisation left open.
        #
        # Both halves of the pair, not just the name. The reason was trimmed to
        # TEST it and then stored as it arrived, which is this file's own
        # recurring defect written down one last time; nothing reads a reason
        # back today, which is exactly the kind of harmlessness that stops being
        # true without anyone noticing.
        $declaredNotInSuite = [ordered]@{}
        foreach ($k in $rawNotInSuite.Keys) { $declaredNotInSuite[$k.Trim()] = $rawNotInSuite[$k].Trim() }

        # This is the one door in the merge that lets a tier tell check 2 to look
        # away, so it is the one place a lenient read would undo the strict one.
        $claimedScripts = @($Harnesses | ForEach-Object { $_.script })
        foreach ($name in $declaredNotInSuite.Keys) {
            # Check 2 compares a stored path against a name, so anything
            # carrying a separator excuses nothing while reading as though it
            # did.
            if (-not $name -or $name -match '[\\/]') {
                $tierProblems += "tier notInSuite names something that is not a plain file name: '$name'"
                continue
            }
            # Excusing a script the manifest also names is the silent shrink
            # again, arriving through the only door a tier can open by itself.
            if ($claimedScripts -contains $name) {
                $tierProblems += "tier notInSuite excuses '$name', which the manifest also names as a harness script"
                continue
            }
            # The reason is the whole point of the pairs form refused above: it
            # is the only place a tier says why a script of its own is not a
            # harness. An empty one is the list form again, spelled differently,
            # and the same argument that rejects an empty oracle applies to it.
            if (-not $declaredNotInSuite[$name]) {
                $tierProblems += "tier notInSuite gives no reason for '$name'; the reason is why the script is not a harness"
                continue
            }
            $NotInSuite[$name] = $declaredNotInSuite[$name]
        }
    }

    if ($tierProblems.Count -gt 0) {
        Write-Host 'tier layer:' -ForegroundColor Red
        $tierProblems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        exit 1
    }
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
    [ordered]@{ name = 'st-seed-unverified'; script = 'lib/fuzz-selftest/seed-unverified.ps1'; tags = @('selftest'); outDir = $true; seed = $false; minutes = 0; oracle = 'fixture' }
    [ordered]@{ name = 'st-seed-readback'; script = 'lib/fuzz-selftest/seed-readback-cases.ps1'; tags = @('selftest'); outDir = $true; seed = $false; minutes = 0; oracle = 'fixture' }
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
    #
    # Both fields are read as stored, with no coercion of their own. The base
    # manifest writes literals and a tier's are coerced at the merge, so a cast
    # here would only be a second normalisation of the same value - which is
    # the one habit every round of this file has had to unpick. The [int] on
    # the Max is for its double return, not for the manifest.
    $timeoutSeconds = if ($Harness.Contains('timeoutSeconds')) { $Harness.timeoutSeconds }
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

            # Reap before retrying, not just between harnesses. A harness that
            # failed halfway often left its window up, and the retry's own gate
            # then refuses over it - so the retry was guaranteed to fail and
            # the whole area got reported as untested. Seen for real: dialogs'
            # retry refused over a pid its first attempt had leaked.
            if ($script:CurrentStamp) {
                Stop-WinttyStartedAfter -Since $script:CurrentStamp -ExePath $Exe
            }
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
# including -List. Between them these four checks cover the ways the manifest
# rots, in both directions.
#
# Every path below is read with -LiteralPath, and the directory is enumerated
# the same way. Test-Path and Get-ChildItem take a WILDCARD by default, so a
# '[' anywhere in a path turns it into a pattern: a real harness named
# 'tier[1].ps1' was refused as missing while sitting on disk, and - worse,
# because it is silent - a checkout under a directory holding one enumerated
# nothing here, so check 2 passed by finding no files to classify rather than
# by finding them all classified.
$problems = @()

# 1. The manifest names something that is not there any more, or names
#    something that is not a script. A directory passes a bare existence test,
#    and then reaches check 3, where the syntax tree of a directory has no param
#    block and the run blames the harness for not declaring -ExePath. That is
#    the wrong blame an EMPTY script already produced - an empty one resolves to
#    this directory - and the merge refuses that one before it arrives here. A
#    directory named outright is the same event with nothing upstream to catch
#    it, so it is answered here, in words that name what is wrong.
foreach ($h in @($Harnesses) + @($SelfTestHarnesses)) {
    $scriptPath = Join-Path $PSScriptRoot $h.script
    if (Test-Path -LiteralPath $scriptPath -PathType Leaf) { continue }
    if (Test-Path -LiteralPath $scriptPath) {
        $problems += "manifest names a directory rather than a script: $($h.name) -> $($h.script)"
    } else {
        $problems += "manifest names a script that does not exist: $($h.name) -> $($h.script)"
    }
}

# 2. A harness exists that the manifest does not name. Without this the
#    manifest silently shrinks: delete an entry and the suite reports PASS
#    for an area it never touched.
# Compared on the relative path, not the leaf. Every base harness script is flat,
# so a leaf comparison was equivalent until a tier could name one in a
# subdirectory; after that, naming lib/pro/x.ps1 excuses an unrelated x.ps1
# sitting here unclassified, which is the check's whole purpose. Both sides are
# read as stored rather than reduced again here: the merge resolved a tier's
# spellings once, and reducing them a second time in each place they are
# compared is how the places came to disagree.
$claimed = @(@($Harnesses) | ForEach-Object { $_.script })
$excused = @($NotInSuite.Keys | ForEach-Object { [string]$_ })
foreach ($f in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File) {
    if ($claimed -contains $f.Name) { continue }
    if ($excused -contains $f.Name) { continue }
    $problems += "$($f.Name) is in this directory but neither in the manifest nor in `$NotInSuite; classify it"
}

# 3. The manifest's calling convention still matches what each script accepts.
#    `pwsh -File` silently ignores a parameter the script does not declare, so
#    a renamed -ExePath would leave every harness quietly testing its own
#    default build while the suite reported on the exe you asked for.
foreach ($h in @($Harnesses) + @($SelfTestHarnesses)) {
    $path = Join-Path $PSScriptRoot $h.script
    # Leaf, not existence. ParseFile handed a directory returns an AST with no
    # param block rather than throwing, so every parameter reads as undeclared
    # and the run blames a harness for what check 1 has already named.
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
    $declared = @()
    if ($ast.ParamBlock) { $declared = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath }) }
    foreach ($need in @('ExePath') + $(if ($h.outDir) { 'OutDir' }) + $(if ($h.seed) { 'Seed' })) {
        if ($declared -notcontains $need) {
            $problems += "$($h.name) is called with -$need but $($h.script) does not declare it"
        }
    }
}

# 4. A name or a tag holding the comma the filters split on. Split-List cuts the
#    caller's -Tag, -Only and -Skip values on commas, so a value declared with
#    one in it can never be matched: -Only 'st,tier' arrives as two names and is
#    refused as a typo, -Tag 'a,b' matches nothing.
#    What that costs differs between the two fields, and the messages say so
#    rather than arguing the stronger case for both. A tag is the whole of it: a
#    comma'd tag is declared, listed, and reachable by nothing. A name is not -
#    it still runs on a full run and is still selectable by -Tag - so what is
#    lost is only that -Only and -Skip cannot name it. Both are refused anyway,
#    because a manifest row that two documented flags cannot address is a
#    manifest defect either way, and the refusal is the only place anyone sees
#    it.
#    Refused rather than split, because splitting means one declaration silently
#    becomes two, and -List joins tags with a comma - so one tag 'a,b' and two
#    tags 'a' and 'b' print identically and nothing anyone can read says which
#    happened.
#    Placed here, after the merge and over the merged set, rather than in the
#    tier block: the rule is Split-List's and applies to a base harness exactly
#    as much as to a tier's. Over the fixtures too, and for the same reason
#    checks 1 and 3 read them - the self-test selects them with -Only, so a
#    fixture name is split exactly like any other. They carry no layer, so the
#    label is computed rather than read off the entry: read off it, the message
#    would open with an empty string and say nothing about which manifest the
#    reader has to go and edit.
foreach ($h in @($Harnesses) + @($SelfTestHarnesses)) {
    $where = if ($h.Contains('layer')) { $h.layer } else { 'selftest fixture' }
    if ("$($h.name)".Contains(',')) {
        $problems += ("$where harness name holds a comma: '$($h.name)'. " +
                      'A comma separates -Only and -Skip values, so neither can name it')
    }
    foreach ($t in $h.tags) {
        if ("$t".Contains(',')) {
            $problems += ("$where harness '$($h.name)' declares a tag holding a comma: '$t'. " +
                          'A comma separates -Tag values, so no -Tag run could select it; declare them as separate tags')
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Host 'manifest integrity:' -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

# ---- selection ------------------------------------------------------------
# Both switches run the fixtures; only -SelfTest asserts. -SelfTestInner
# falls through to the real report block and exits on its verdict.
$useFixtures = $SelfTest -or $SelfTestInner
$all = if ($useFixtures) { @($SelfTestHarnesses) } else { @($Harnesses) }

# The assertions are written against the whole fixture set at exactly one
# retry with one seed, so anything that would change any of the three is
# refused rather than silently ignored: an assertion that still runs against a
# selection it was not written for reports on something nobody asked about.
# -OutRoot is NOT among them. It was, on the grounds that the tier cases below
# run a child out of a copy of this directory placed under the root and two
# self-tests sharing one root rewrite each other's manifest between the copy
# and the child launch - but that argument indicts the DEFAULT root, which is
# stamped per second, and refusing the override removed the only way to give
# two runs distinct roots. The root is made unique below instead.
# Ahead of -List, so that -SelfTest -List -Only x is refused rather than
# quietly listing the base manifest a -SelfTest run does not use.
if ($SelfTest -and ($Tag -or $Only -or $Skip -or
                    $PSBoundParameters.ContainsKey('Retries') -or
                    $PSBoundParameters.ContainsKey('Seed') -or
                    $StopOnFindings)) {
    Write-Host '-SelfTest asserts against the whole fixture set at one retry and one seed; it takes no filters, -Retries, -Seed or -StopOnFindings' -ForegroundColor Red
    exit 1
}

# An unknown name in -Only is a typo, and silently running a smaller suite
# than was asked for is the failure mode this whole script exists to avoid.
# -Skip is read the same way: a name that matches nothing skips nothing, and an
# operator who asked for a harness to be left out has no way to tell it ran.
# Both are compared against the whole manifest rather than against the current
# selection, so `-Tag smoke -Skip inspector` is not a typo.
#
# Ahead of -List rather than after it. The integrity checks all run on -List
# because it is the invocation that needs no desktop, and a filter typo is the
# same class of defect read from the same manifest - so -List was the one place
# a wrong name stayed silent, and the flag anyone would reach for to find out
# whether a name is real answered by printing the whole set.
#
# One consequence is worth writing down rather than discovering: a -Skip list
# cannot be shared between an oss invocation and a tier one. A tier harness
# named in -Skip is a real name on the tier build and a typo on the base build,
# so the base build refuses it, and `just fuzz` passes its arguments straight
# through. That is the trade this check was taken on: the alternative is a
# -Skip that silently skips nothing, which is what it exists to refuse.
foreach ($filter in @(@{ flag = '-Only'; names = $Only }, @{ flag = '-Skip'; names = $Skip })) {
    if (-not $filter.names) { continue }
    $known = @($all | ForEach-Object { $_.name })
    $unknown = @($filter.names | Where-Object { $known -notcontains $_ })
    if ($unknown.Count -gt 0) {
        Write-Host ("{0} names no such harness: {1}" -f $filter.flag, ($unknown -join ', ')) -ForegroundColor Red
        exit 1
    }
}

if ($List) {
    $Harnesses | ForEach-Object {
        [pscustomobject]@{ layer = $_.layer; name = $_.name; tags = ($_.tags -join ','); minutes = $_.minutes; oracle = $_.oracle }
    } | Format-Table -AutoSize -Wrap
    $total = ($Harnesses | ForEach-Object { $_.minutes } | Measure-Object -Sum).Sum
    Write-Host ("{0} harnesses, about {1} minutes for a full run" -f $Harnesses.Count, $total)
    if ($TierLayerName) {
        $baseCount = @($Harnesses | Where-Object { $_.layer -eq 'base' }).Count
        $tierCount = @($Harnesses | Where-Object { $_.layer -eq $TierLayerName }).Count
        Write-Host ("layers: base ({0}) + {1} ({2})" -f $baseCount, $TierLayerName, $tierCount)
    } else {
        Write-Host 'layers: base only (no tier manifest beside this runner)'
    }
    exit 0
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

# A root of its own per process, so two self-tests cannot collide however the
# root was chosen. Every path this run writes hangs off here - the per-fixture
# output directories, the child runs' roots, and the copy of this directory the
# tier cases run out of - and sharing that last one means a run rewriting the
# manifest another is about to read. Applied to the root rather than to the
# copy alone because the collision is the root's, and applied here rather than
# to the default because the default is not the only way to arrive at one.
if ($SelfTest) { $OutRoot = Join-Path $OutRoot "selftest-$PID" }

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
    if (-not (Test-Path -LiteralPath $ExePath)) { throw "missing exe: $ExePath (build it first: just build-dll build-win)" }
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
        @{ name = 'st-seed-unverified'; verdict = 'harness'; attempts = 2; why = 'a harness that could not establish its own corpus leaves with 1, not the never-retried 2' }
        @{ name = 'st-seed-readback'; verdict = 'pass'; attempts = 1; why = 'the seed read-back rules decide whether a run has a real corpus, so they are exercised rather than assumed' }
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
    # here. This fixture set holds two findings, five that could not run (an
    # exit code outside the convention, one that had to be killed, and one
    # that classified its own failure as retryable) and four passes, so
    # every branch of the roll-up has a witness.
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
    if (-not (Test-Path -LiteralPath (Join-Path $ptDir 'finally-ran.txt'))) {
        $bad += 'st-product-throw exited 2 without running its finally, so a real harness would leak its config dir and its window'
    }
    if ($outcome.broken.Count -ne 5) {
        $bad += "roll-up counted $($outcome.broken.Count) harness failures, expected 5"
    }

    # Same reason as st-product-throw: the verdict must not cost the cleanup.
    # A seeding failure aborts a real run from the middle of its op loop, and
    # that is exactly when leaving the app up would make the next harness
    # refuse to start.
    $suDir = Join-Path $OutRoot 'st-seed-unverified'
    if (-not (Test-Path -LiteralPath (Join-Path $suDir 'finally-ran.txt'))) {
        $bad += 'st-seed-unverified exited without running its finally, so a real harness would leave its window up'
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
        # Captured rather than discarded, so a child that was NOT told what it
        # is can be read for what it must not say. Nothing else here is a child
        # without the refusal flag.
        $text = (& pwsh @argv | Out-String)
        return @{ exit = $LASTEXITCODE; root = $root; text = [string]$text }
    }

    $full = Invoke-Inner -Name 'full' -Extra @()
    if ($full.exit -ne 2) {
        $bad += "a real run over the same fixtures exited $($full.exit), expected 2"
    }
    # The other half of the refusal marker, and the half nothing pinned: the
    # marker's whole job is to say that the flag ARRIVED, so a marker printed
    # unconditionally says nothing at all. Widening it that way left every
    # assertion below still passing. This child was passed no flag, so it must
    # be silent.
    if ($full.text.Contains('selftest: refusal child')) {
        $bad += 'a child that was passed no refusal flag printed the marker anyway, so the marker no longer says whether the flag arrived and the recursion bound is carried by nobody'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $full.root 'summary.json'))) {
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
    # The same typo on the other filter, which fails the quieter way: it runs
    # what it was asked to leave out, and says nothing either way.
    $skipTypo = Invoke-Inner -Name 'skip-typo' -Extra @('-Only', 'st-pass', '-Skip', 'st-nope')
    if ($skipTypo.exit -ne 1) {
        $bad += "-Skip with an unknown name exited $($skipTypo.exit), expected 1 - a skip that matches nothing skips nothing"
    }

    # The refusal that keeps everything above meaning what it says: the
    # expectations are written for the whole fixture set, so a filter that got
    # through would leave them asserting against a selection nobody chose. It
    # cannot be reached from this process - this one is already past it - so a
    # child is run for real. The message is checked as well as the exit code,
    # because a run whose filter took would leave with 1 as well, by failing
    # these very assertions.
    #
    # The child is told what it is, on the command line, and skips this block.
    # Its whole job is to be refused before it starts, so if the refusal ever
    # stopped refusing the child would reach this line and start a -SelfTest of
    # its own, and that one another, without end. A self-test that takes the
    # machine down when a guard breaks is worse than one that misses. Told with
    # a parameter rather than an environment variable because the variable is
    # also readable from outside: exported into the shell that runs this, it
    # skips the block with no message and no change to the case count.
    #
    # A flag cannot be set ambiently the way that variable could, but it can
    # still be typed - and typed, it skipped the block just as quietly. So the
    # case is counted and the count is printed with the others: a run that did
    # not make this check says 0 where every other run says 1, which is the
    # thing the variable never had.
    $script:RefusalCases = 0
    if (-not $SelfTestRefusalChild) {
        $refused = (& pwsh -NoProfile -File $PSCommandPath -SelfTest -Only st-pass -SelfTestRefusalChild | Out-String)
        $refusedExit = $LASTEXITCODE
        if ($refusedExit -ne 1 -or -not $refused.Contains('it takes no filters')) {
            $bad += "-SelfTest with -Only exited $refusedExit without refusing the filter; every expectation above would have run against one fixture"
        }
        # The child's own answer that it was told what it is. A parent that
        # stopped passing the flag would be refused for its filter exactly like
        # this one and nothing else would differ, so the bound that keeps a
        # broken refusal from starting self-tests without end would be carried
        # by nobody.
        if (-not $refused.Contains('selftest: refusal child')) {
            $bad += 'the refusal child was not told what it is, so a refusal that stopped refusing would start a self-test of its own, and that one another'
        }
        $script:RefusalCases++
    }

    # ---- tier layer -------------------------------------------------------
    # The merge reads a manifest found by presence beside this runner, and the
    # checks it feeds read $PSScriptRoot rather than anything a caller can pass
    # in. So a fixture manifest cannot be dropped into windows/scripts: it would
    # change every other invocation from that directory, this run's own child
    # runs included. Giving the child a different $PSScriptRoot is the only way
    # to hand those checks a directory of our own, and since everything they
    # read is text and none of it is built, a copy is enough.
    # Copies a suite directory from the inventory by name rather than sweeping
    # it up with a glob. A tier checkout has the tier's own scripts sitting
    # beside the base ones, and a glob takes those while deliberately leaving
    # behind the one file that classifies them - so every case below would fail
    # integrity check 2 on exactly the builds the layer exists for.
    #
    # A function taking a source root rather than four lines reading
    # $PSScriptRoot, because that is the difference between something the
    # self-test can hand a tier checkout and something it can only ever run
    # against this one, where a glob and the inventory name the same files.
    function Copy-SuiteScripts {
        param(
            [Parameter(Mandatory)][string]$From,
            [Parameter(Mandatory)][string]$To,
            [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Inventory
        )
        New-Item -ItemType Directory -Force -Path $To | Out-Null
        foreach ($rel in $Inventory) {
            # The manifest is the one name left behind. A tier checkout has a
            # real one sitting there, and copying it would give every case below
            # a second layer, the case that asserts what an ABSENT manifest does
            # included - on exactly the builds that ship one.
            if ($rel -eq 'fuzz-tier-harnesses.ps1') { continue }
            $src = Join-Path $From $rel
            # $NotInSuite classifies names, and nothing checks that the file
            # behind one is still there: check 2 only looks the other way. A
            # stale entry is its silence, not a reason to fail the self-test.
            if (-not (Test-Path -LiteralPath $src)) { continue }
            $dest = Join-Path $To $rel
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
            Copy-Item -LiteralPath $src -Destination $dest -Force
        }
        # lib/ wholesale, because it carries wintty-process.ps1 - dot-sourced
        # before anything else runs - and every fixture the harnesses name.
        # A source without one is not a suite directory. Left to Copy-Item under
        # $ErrorActionPreference = 'Stop' that ends the whole self-test on an
        # ItemNotFoundException, which names the line it stopped at and not the
        # argument that was wrong.
        $libFrom = Join-Path $From 'lib'
        if (-not (Test-Path -LiteralPath $libFrom)) {
            throw "Copy-SuiteScripts: '$From' has no lib/, so it is not a suite directory to copy from"
        }
        Copy-Item -LiteralPath $libFrom -Destination $To -Recurse -Force
    }

    # Everything the tier cases create lives under one directory, the copy and
    # the files injected beside it alike, so the sweep at the end is one removal
    # rather than a list that has to be kept in step with the cases. The cases
    # name paths relative to this, and two of them turn on where the copy sits
    # inside it: one climbs out of the copy, one names a sibling whose full path
    # opens with the copy's.
    $LayerSandbox = Join-Path $OutRoot 'layer'
    $LayerRoot = Join-Path $LayerSandbox 'layer-scripts'

    # Everything this run writes has to sit under a path of this process's own,
    # and nothing else here would notice if it stopped doing so: two self-tests
    # sharing a path corrupt each other rather than colliding loudly. flaky.ps1
    # keys its retry marker off the run root, so the second run's st-flaky
    # passes on attempt 1 and is reported as '1 attempt(s), expected 2'; and the
    # tier cases rewrite the manifest in the copy below between the copy and
    # each child launch, so one run reads what the other just wrote. Neither
    # reproduces on its own.
    # Asserted on the copy rather than on $OutRoot, because the copy is where
    # the second half of that happens and it is built from $OutRoot: this covers
    # both, where a check on $OutRoot alone still passed with the copy pointed
    # somewhere shared and the collision fully restored.
    if (-not $LayerRoot.Contains("selftest-$PID")) {
        $bad += "the copy the tier cases run out of is not under this process's own root: $LayerRoot. Two self-tests at once would share it and rewrite each other's fixtures"
    }

    Copy-SuiteScripts -From $PSScriptRoot -To $LayerRoot -Inventory $BaseScripts

    # Read off this run rather than written down here, so adding a base harness
    # is not a self-test failure. What is under test is that a layer adds
    # exactly its own and leaves the base half alone; a base manifest that
    # silently shrinks is already integrity check 2's job.
    $baseSet     = @($Harnesses | Where-Object { $_.layer -eq 'base' })
    $baseCount   = $baseSet.Count
    $baseMinutes = ($baseSet | ForEach-Object { $_.minutes } | Measure-Object -Sum).Sum

    # Nothing runs these. Only the param block is read, by integrity check 3.
    $LayerStub = @'
#requires -Version 7
param([string]$ExePath, [Parameter(Mandatory)][string]$OutDir)
exit 0
'@
    $script:LayerInjected = @()
    $script:LayerCases = 0

    # Puts the copy back the way the last case found it. A case injects onto a
    # relative path, and a path it names may be one the copy legitimately
    # carries - the last case below injects over gen-bell.ps1, which it does -
    # so what was there is restored rather than deleted. Deleting it instead
    # mutates the tree under test for every case that follows, which is how a
    # case comes to pass for a reason nobody wrote down.
    function Reset-LayerInjection {
        foreach ($p in $script:LayerInjected) {
            if ($null -eq $p.was) { Remove-Item -LiteralPath $p.path -Force -ErrorAction SilentlyContinue }
            else { [System.IO.File]::WriteAllBytes($p.path, $p.was) }
        }
        $script:LayerInjected = @()
    }

    # One child run over the copy. The manifest and whatever scripts a case
    # needs on disk are placed fresh and undone again on the next call, so no
    # case inherits what another left behind and the order they are written in
    # does not matter. That last part needs the sweep after the final assertion
    # too: undoing on the way in leaves the last injecting case's files sitting
    # there. -List is the whole run: the merge, all four integrity checks and
    # the filter typo check happen before it, and none of them needs a desktop.
    #
    # -Root is the copy to run out of, and it is a parameter for one case: a
    # checkout path holding a wildcard character. Everything the runner reads
    # about itself hangs off its own $PSScriptRoot, so the only way to hand
    # those reads such a path is to put a copy at one.
    function Invoke-Layer {
        param(
            [Parameter(Mandatory)][string]$Case,
            [string]$Manifest,
            [hashtable]$Inject = @{},
            [string[]]$Extra = @('-List'),
            [string]$Root = $LayerRoot
        )
        Reset-LayerInjection
        $script:LayerCases++

        $manifestPath = Join-Path $Root 'fuzz-tier-harnesses.ps1'
        Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
        if ($Manifest) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot "lib/fuzz-selftest/layers/$Manifest") -Destination $manifestPath
        }
        # Rooted at the sandbox rather than at the copy, so a case can put a
        # file somewhere only a script path that climbs out of the copy reaches
        # - and so the sweep at the end takes those directories with it.
        foreach ($rel in $Inject.Keys) {
            $dest = Join-Path $LayerSandbox $rel
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
            # Whatever was there first, so the sweep can put it back rather than
            # delete a file it did not create. $null means there was nothing.
            $was = if (Test-Path -LiteralPath $dest) { [System.IO.File]::ReadAllBytes($dest) } else { $null }
            Set-Content -LiteralPath $dest -Value $Inject[$rel] -Encoding utf8
            $script:LayerInjected += @{ path = $dest; was = $was }
        }

        $argv = @('-NoProfile', '-File', (Join-Path $Root 'fuzz-suite.ps1')) + $Extra
        $text = (& pwsh @argv | Out-String)
        return @{ case = $Case; exit = $LASTEXITCODE; text = [string]$text }
    }

    # Both halves are the assertion. Every refusal in the merge leaves with 1,
    # so a case reading the exit code alone would pass while some other guard
    # did the refusing - and a guard whose body was gutted still has its shape.
    function Assert-Layer {
        param(
            [Parameter(Mandatory)]$Run,
            [Parameter(Mandatory)][int]$Exit,
            [string[]]$Says = @(),
            [string[]]$Silent = @()
        )
        if ($Run.exit -ne $Exit) {
            $script:bad += "layer/$($Run.case): exited $($Run.exit), expected $Exit"
        }
        foreach ($s in $Says) {
            if (-not $Run.text.Contains($s)) { $script:bad += "layer/$($Run.case): said nothing about: $s" }
        }
        foreach ($s in $Silent) {
            if ($Run.text.Contains($s)) { $script:bad += "layer/$($Run.case): said what it must not: $s" }
        }
    }

    # No manifest, which is what a public build runs. This is the property most
    # likely to rot without anyone noticing, because nothing downstream can tell
    # a base-only run from the tier run it was supposed to be.
    Assert-Layer -Run (Invoke-Layer -Case 'base-only') -Exit 0 `
        -Says @("$baseCount harnesses, about $baseMinutes minutes for a full run",
                'layers: base only (no tier manifest beside this runner)') `
        -Silent @('layers: base (')

    Assert-Layer -Run (Invoke-Layer -Case 'valid' -Manifest 'valid.ps1') -Exit 0 `
        -Says @("layers: base ($baseCount) + pro (1)",
                "$($baseCount + 1) harnesses, about $($baseMinutes + 1) minutes for a full run")

    Assert-Layer -Run (Invoke-Layer -Case 'pscustomobject' -Manifest 'pscustomobject.ps1') -Exit 0 `
        -Says @("layers: base ($baseCount) + pro (1)")

    # -List's minutes total is the only place a coerced value looks different
    # from the text it came from, so this pins .NET's rounding of '2.6' to 3 for
    # want of any other signal. Nobody chose that rounding and nothing depends
    # on it: if the coercion ever changes, this number follows it rather than
    # the other way round.
    Assert-Layer -Run (Invoke-Layer -Case 'minutes-string' -Manifest 'minutes-string.ps1') -Exit 0 `
        -Says @("layers: base ($baseCount) + pro (2)",
                "$($baseCount + 2) harnesses, about $($baseMinutes + 5) minutes for a full run")

    Assert-Layer -Run (Invoke-Layer -Case 'minutes-bad' -Manifest 'minutes-bad.ps1') -Exit 1 `
        -Says @("tier harness 'st-tier' has a non-numeric minutes: soon")

    # The same field's twin, which had none of its rules. All four ways it goes
    # wrong in one run, because the merge collects them: text, which throws out
    # of the run loop and takes every verdict already collected with it, and the
    # three a number gets wrong. Zero and below are their own case rather than
    # minutes' because this field is the one that skips the floor.
    Assert-Layer -Run (Invoke-Layer -Case 'timeout-bad' -Manifest 'timeout-bad.ps1') -Exit 1 `
        -Says @("tier harness 'st-tier-text' has a non-numeric timeoutSeconds: soon",
                "tier harness 'st-tier-zero' has a timeoutSeconds of 0",
                "tier harness 'st-tier-negative' has a timeoutSeconds of -5",
                "tier harness 'st-tier-null' has a timeoutSeconds of 0")

    # All three in one manifest, because the guard collects them: an empty list,
    # a null one, and one holding only blanks. The last is the only one a bare
    # truthiness test does not see, so without it the filter doing the work is
    # unpinned and could be dropped.
    Assert-Layer -Run (Invoke-Layer -Case 'tags-missing' -Manifest 'tags-missing.ps1') -Exit 1 `
        -Says @("tier harness 'st-tier-empty' declares no tags",
                "tier harness 'st-tier-null' declares no tags",
                "tier harness 'st-tier-blank' declares no tags")

    # The other half of the same rule, and the half a guard that only TESTS the
    # trimmed value leaves open: a padded tag is declared, is listed, and is
    # still unreachable, because selection compares -Tag against what was
    # stored. -List joins the tags with a comma, so 'tier,x' appears only if the
    # padding came off on the way in - unnormalised it reads ' tier ,x'. The
    # numeric tag rides along to show a value that is not a string survives:
    # tags are compared as text either way.
    Assert-Layer -Run (Invoke-Layer -Case 'tags-padded' -Manifest 'tags-padded.ps1') -Exit 0 `
        -Says @("layers: base ($baseCount) + pro (2)", 'tier,x')

    # The last character the two sides disagreed on, and the one -List cannot
    # show either way: it joins tags with a comma, so a single tag of 'a,b' and
    # two tags a and b print identically. The refusal is the only place the
    # difference can be seen, which is the argument for refusing rather than
    # splitting. All three entries assert it: the second reaches the same value
    # by trimming rather than by typing, so a guard placed before the trim would
    # miss it, and the third declares a good tag before the bad one, so a guard
    # that reads a harness's first tag and stops has something to miss too. The
    # message names the layer because the rule is not the tier's - it is
    # Split-List's, read off the merged set.
    Assert-Layer -Run (Invoke-Layer -Case 'tags-comma' -Manifest 'tags-comma.ps1') -Exit 1 `
        -Says @("pro harness 'st-tier-comma' declares a tag holding a comma: 'a,b'",
                "pro harness 'st-tier-padded' declares a tag holding a comma: 'c,d'",
                "pro harness 'st-tier-second' declares a tag holding a comma: 'e,f'")

    # The same rule on the field -Only and -Skip match. A name holding a comma
    # is listed as a real harness that neither filter can name.
    Assert-Layer -Run (Invoke-Layer -Case 'name-comma' -Manifest 'name-comma.ps1') -Exit 1 `
        -Says @("pro harness name holds a comma: 'st,tier'")

    # The other two thirds of that check, which had no witness at all. Moving
    # the rule out of the tier block and onto the merged set is the whole claim
    # made for it, and nothing tested the claim: no base name and no fixture
    # name holds a comma, so planting `if ($h.layer -eq 'base') { continue }` at
    # the top of that loop changed no case and put the rule back where it was.
    # No manifest can reach a base or fixture entry - the merge stamps every
    # tier entry with the tier's own layer - so this is the one case that edits
    # the RUNNER in the copy rather than what the runner reads. The edits are
    # checked for having applied, because a rename upstream would otherwise
    # turn this into a case that plants nothing and passes.
    $commaRunner = Get-Content -Raw -LiteralPath $PSCommandPath
    foreach ($edit in @(
            @{ from = "name = 'search';";            to = "name = 'sea,rch';" }
            @{ from = "tags = @('smoke','search');"; to = "tags = @('smo,ke','search');" }
            @{ from = "name = 'st-pass';";           to = "name = 'st,pass';" })) {
        $after = $commaRunner.Replace($edit.from, $edit.to)
        if ($after -eq $commaRunner) {
            $bad += "layer/base-comma: this file no longer spells $($edit.from), so the case plants nothing and proves nothing; re-point it at an entry that is really there"
        }
        $commaRunner = $after
    }
    Assert-Layer -Run (Invoke-Layer -Case 'base-comma' `
                                    -Inject @{ 'layer-scripts/fuzz-suite.ps1' = $commaRunner }) -Exit 1 `
        -Says @("base harness name holds a comma: 'sea,rch'",
                "base harness 'sea,rch' declares a tag holding a comma: 'smo,ke'",
                "selftest fixture harness name holds a comma: 'st,pass'")

    # Names that were trimmed to test for emptiness and then stored as they
    # arrived. Both collision checks read the stored value, so both halves are
    # here: ' search ' against the base set, and ' dupe ' against the layer's
    # own names. Accepted, each was a row in -List that no -Only or -Skip could
    # ever select, and -Skip search did not skip it.
    Assert-Layer -Run (Invoke-Layer -Case 'name-padded' -Manifest 'name-padded.ps1') -Exit 1 `
        -Says @("tier harness 'search' collides with a base harness of the same name",
                "tier harness 'dupe' is declared twice in the tier manifest")

    # One entry per required key, each short a different one. The loop collects
    # every problem before it exits, so seven assertions cost one child run - and
    # a key quietly dropped from the list is otherwise invisible.
    Assert-Layer -Run (Invoke-Layer -Case 'missing-key' -Manifest 'missing-key.ps1') -Exit 1 `
        -Says @("tier harness is missing 'name':",
                "tier harness is missing 'script': st-tier-no-script",
                "tier harness is missing 'tags': st-tier-no-tags",
                "tier harness is missing 'outDir': st-tier-no-outdir",
                "tier harness is missing 'seed': st-tier-no-seed",
                "tier harness is missing 'minutes': st-tier-no-minutes",
                "tier harness is missing 'oracle': st-tier-no-oracle")

    # The target is made to exist and to declare what the manifest passes it, so
    # checks 1 and 3 have nothing to say and only the path guard can refuse it.
    # The second name is the absolute path to the same file, spelled out here so
    # the assertion pins what the run printed rather than that it printed
    # something: the guard was dead for that input class, and an absolute path
    # surfaced as check 1 saying the script does not exist.
    $escapeAbsolute = [System.IO.Path]::GetFullPath((Join-Path $LayerSandbox 'layer-escape/escape.ps1'))
    Assert-Layer -Run (Invoke-Layer -Case 'script-escapes' -Manifest 'script-escapes.ps1' `
                                    -Inject @{ 'layer-escape/escape.ps1' = $LayerStub }) -Exit 1 `
        -Says @("tier harness 'st-tier' names a script outside this directory: ../layer-escape/escape.ps1",
                "tier harness 'st-tier-absolute' names a script outside this directory: $escapeAbsolute") `
        -Silent @('names a script that does not exist')

    # A sibling directory whose full path opens with this one's. A prefix test
    # that stops short of the separator reads it as inside.
    Assert-Layer -Run (Invoke-Layer -Case 'script-sibling' -Manifest 'script-sibling.ps1' `
                                    -Inject @{ 'layer-scriptsX/escape.ps1' = $LayerStub }) -Exit 1 `
        -Says @("tier harness 'st-tier' names a script outside this directory: ../layer-scriptsX/escape.ps1")

    # The other thing that reduction does, and the half a subdirectory case
    # cannot reach: a leading './' has to come off before a leaf comparison can
    # match, or a tier naming its own harness the way a path beside the runner
    # is usually written is told to classify the script it just declared.
    Assert-Layer -Run (Invoke-Layer -Case 'script-relative' -Manifest 'script-relative.ps1' `
                                    -Inject @{ 'layer-scripts/tier-relative.ps1' = $LayerStub }) -Exit 0 `
        -Says @("layers: base ($baseCount) + pro (1)") `
        -Silent @('tier-relative.ps1 is in this directory')

    # The rest of the ways the same path beside the runner gets written. Reducing
    # by prefix took exactly one leading './', so each of these named a file the
    # tier ships and compared equal to nothing - and the run died at check 2
    # naming the very file the manifest declared. The padded one is the same
    # reduction over a value the emptiness test trimmed and threw away. One
    # child run, because the failure is one message per spelling and the
    # assertion is that none of them appears.
    #
    # The last name in that manifest is not a spelling at all: it is a plain
    # file name holding a wildcard character. Every path this runner reads about
    # itself went through an existence test that takes a PATTERN by default, so
    # a real harness called tier-d[1].ps1 was refused as missing while sitting
    # on disk beside the runner, and the parameter check then skipped past it.
    Assert-Layer -Run (Invoke-Layer -Case 'script-spellings' -Manifest 'script-spellings.ps1' `
                                    -Inject @{ 'layer-scripts/tier-a.ps1' = $LayerStub
                                               'layer-scripts/tier-b.ps1' = $LayerStub
                                               'layer-scripts/tier-c.ps1' = $LayerStub
                                               'layer-scripts/tier-d[1].ps1' = $LayerStub }) -Exit 0 `
        -Says @("layers: base ($baseCount) + pro (5)") `
        -Silent @('tier-a.ps1 is in this directory',
                  'tier-b.ps1 is in this directory',
                  'tier-c.ps1 is in this directory',
                  'tier-d[1].ps1 is in this directory',
                  'names a script that does not exist',
                  'does not declare it')

    # The manifest names lib/fuzz-selftest/pass.ps1 and an unrelated pass.ps1
    # sits at the top level. Comparing leaves rather than relative paths reads
    # the first as classifying the second.
    Assert-Layer -Run (Invoke-Layer -Case 'leaf-collision' -Manifest 'leaf-collision.ps1' `
                                    -Inject @{ 'layer-scripts/pass.ps1' = $LayerStub }) -Exit 1 `
        -Says @('pass.ps1 is in this directory but neither in the manifest nor in $NotInSuite')

    Assert-Layer -Run (Invoke-Layer -Case 'duplicate-name' -Manifest 'duplicate-name.ps1') -Exit 1 `
        -Says @("tier harness 'st-tier' is declared twice in the tier manifest")

    Assert-Layer -Run (Invoke-Layer -Case 'base-collision' -Manifest 'base-collision.ps1') -Exit 1 `
        -Says @("tier harness 'search' collides with a base harness of the same name")

    Assert-Layer -Run (Invoke-Layer -Case 'reserved-layer' -Manifest 'reserved-layer.ps1') -Exit 1 `
        -Says @("tier layer: 'base' is the name this runner gives its own harnesses")

    # The same name with padding, which the refusal compared and missed. -List
    # then printed two layers whose names differ by characters nobody can see.
    Assert-Layer -Run (Invoke-Layer -Case 'reserved-layer-padded' -Manifest 'reserved-layer-padded.ps1') -Exit 1 `
        -Says @("tier layer: 'base' is the name this runner gives its own harnesses")

    # A layer name that is only padding: present, so the shape check takes it,
    # and empty once normalised. -List reported the merged suite as base-only.
    Assert-Layer -Run (Invoke-Layer -Case 'layer-blank' -Manifest 'layer-blank.ps1') -Exit 1 `
        -Says @('tier layer: the layer name is blank') `
        -Silent @('layers: base only')

    Assert-Layer -Run (Invoke-Layer -Case 'no-harnesses' -Manifest 'no-harnesses.ps1') -Exit 1 `
        -Says @("tier layer: expected an object with 'layer' and 'harnesses'")

    # The other arm of that same check, which nothing else reaches: a manifest
    # with harnesses and no layer name merges them and then has no name to print,
    # so -List reports a suite bigger than the base set as base-only. That is
    # this whole feature's failure mode wearing its own clothes, so the case
    # pins the silence as well as the exit.
    Assert-Layer -Run (Invoke-Layer -Case 'no-layer' -Manifest 'no-layer.ps1') -Exit 1 `
        -Says @("tier layer: expected an object with 'layer' and 'harnesses'") `
        -Silent @('layers: base only')

    Assert-Layer -Run (Invoke-Layer -Case 'returns-nothing' -Manifest 'returns-nothing.ps1') -Exit 1 `
        -Says @('fuzz-tier-harnesses.ps1 returned nothing')

    # The opposite of returns-nothing, and the one the pipeline hides: two
    # objects out of one file read as a single manifest whose layer name is both
    # names joined. -RequireLayer refuses that; a run without it did not.
    Assert-Layer -Run (Invoke-Layer -Case 'two-objects' -Manifest 'two-objects.ps1') -Exit 1 `
        -Says @('emitted a collection of 2; it must emit exactly one object')

    # The same wrapper holding one object, which is what a message that counts
    # them cannot describe: it must be refused, and told apart from the manifest
    # it is wrapping.
    Assert-Layer -Run (Invoke-Layer -Case 'one-object-collection' -Manifest 'one-object-collection.ps1') -Exit 1 `
        -Says @('emitted a collection of 1; it must emit exactly one object')

    # A tier ships runners and assets of its own, and until the layer could
    # classify them the choices were patching this file or calling one a harness.
    Assert-Layer -Run (Invoke-Layer -Case 'not-in-suite' -Manifest 'not-in-suite.ps1' `
                                    -Inject @{ 'layer-scripts/tier-runner.ps1' = $LayerStub }) -Exit 0 `
        -Says @("layers: base ($baseCount) + pro (1)")

    # The same classification written as a PSCustomObject. Unnormalised it is a
    # no-op with no message of its own: the run dies at check 2 naming the very
    # file the manifest classified, which sends the tier author looking at the
    # half that was right.
    Assert-Layer -Run (Invoke-Layer -Case 'not-in-suite-object' -Manifest 'not-in-suite-object.ps1' `
                                    -Inject @{ 'layer-scripts/tier-runner.ps1' = $LayerStub }) -Exit 0 `
        -Says @("layers: base ($baseCount) + pro (1)") `
        -Silent @('tier-runner.ps1 is in this directory')

    # The same classification with padding on the name. Stored as it arrives it
    # excuses nothing, and the run dies at check 2 naming the file the manifest
    # classified - so the assertion is the silence as much as the exit.
    Assert-Layer -Run (Invoke-Layer -Case 'not-in-suite-padded' -Manifest 'not-in-suite-padded.ps1' `
                                    -Inject @{ 'layer-scripts/tier-runner.ps1' = $LayerStub }) -Exit 0 `
        -Says @("layers: base ($baseCount) + pro (1)") `
        -Silent @('tier-runner.ps1 is in this directory')

    # A classified name that opens with a dot. Reducing a path by trimming the
    # characters '.' and '\' rather than the prefix takes this one down to
    # 'helper.ps1' and the run dies at check 2 naming the file the manifest
    # classified - the padded-name failure again, from the other side of the
    # same reduction. Nothing else in the set reaches that difference: every
    # other name here opens with a letter.
    Assert-Layer -Run (Invoke-Layer -Case 'not-in-suite-dotname' -Manifest 'not-in-suite-dotname.ps1' `
                                    -Inject @{ 'layer-scripts/.helper.ps1' = $LayerStub }) -Exit 0 `
        -Says @("layers: base ($baseCount) + pro (1)") `
        -Silent @('.helper.ps1 is in this directory')

    Assert-Layer -Run (Invoke-Layer -Case 'not-in-suite-list' -Manifest 'not-in-suite-list.ps1') -Exit 1 `
        -Says @('tier notInSuite must be written as name = reason pairs, not a list of names')

    # The same list, typed. A shape test asking for System.Array answers no to a
    # List and to an ArrayList, and neither is a dictionary either, so both fell
    # through to the object read and had Count and Capacity classified as file
    # names - the run then dies telling the tier author to classify the script
    # they classified. The array literal above is the only shape the old test
    # saw, which is why it is not the only one asserted.
    Assert-Layer -Run (Invoke-Layer -Case 'not-in-suite-typed-list' -Manifest 'not-in-suite-typed-list.ps1') -Exit 1 `
        -Says @('tier notInSuite must be written as name = reason pairs, not a list of names') `
        -Silent @('tier-runner.ps1 is in this directory')

    # The list form with nothing in it. Read for truthiness rather than for
    # presence it is not the list form at all: it is skipped, and the run dies
    # at check 2 about a script instead of here about the shape.
    Assert-Layer -Run (Invoke-Layer -Case 'not-in-suite-empty' -Manifest 'not-in-suite-empty.ps1') -Exit 1 `
        -Says @('tier notInSuite must be written as name = reason pairs, not a list of names')

    # Names check 2 could never act on: one carrying a separator, which it
    # compares leaves against, and one that is blank. Nothing else in the set
    # gives the plain-file-name guard anything to refuse.
    Assert-Layer -Run (Invoke-Layer -Case 'not-in-suite-path' -Manifest 'not-in-suite-path.ps1') -Exit 1 `
        -Says @("tier notInSuite names something that is not a plain file name: 'lib/tier-runner.ps1'",
                "tier notInSuite names something that is not a plain file name: ''")

    # The pairs form with an empty reason, which is the list form spelled the
    # long way round. Both ways a reason can say nothing are here: absent, and
    # present as whitespace. The second is the one an emptiness test that does
    # not trim reads as given.
    Assert-Layer -Run (Invoke-Layer -Case 'not-in-suite-empty-reason' -Manifest 'not-in-suite-empty-reason.ps1') -Exit 1 `
        -Says @("tier notInSuite gives no reason for 'tier-runner.ps1'",
                "tier notInSuite gives no reason for 'tier-asset.ps1'")

    # notInSuite is the only thing in the merge that tells check 2 to look away,
    # so what it may excuse is the merge's own business.
    Assert-Layer -Run (Invoke-Layer -Case 'not-in-suite-harness' -Manifest 'not-in-suite-harness.ps1') -Exit 1 `
        -Says @("tier notInSuite excuses 'search-fuzz.ps1', which the manifest also names as a harness script")

    # The same door, with the harness named as a path rather than as a leaf.
    # notInSuite names leaves, so the manifest's own scripts have to be reduced
    # to leaves before the two can be compared - and unreduced they never match,
    # so the tier excuses a script it declared and the suite shrinks by one with
    # nothing said. The stub is injected because that shrink is a PASS: without
    # a file behind the name the run would be refused by check 1 instead.
    Assert-Layer -Run (Invoke-Layer -Case 'not-in-suite-harness-relative' -Manifest 'not-in-suite-harness-relative.ps1' `
                                    -Inject @{ 'layer-scripts/tier-runner.ps1' = $LayerStub
                                               'layer-scripts/tier-asset.ps1'  = $LayerStub }) -Exit 1 `
        -Says @("tier notInSuite excuses 'tier-runner.ps1', which the manifest also names as a harness script",
                "tier notInSuite excuses 'tier-asset.ps1', which the manifest also names as a harness script")

    # Present and empty, which a key-presence check takes for declared. The last
    # is empty only after trimming, which is how a manifest is likelier to write
    # it and the only one that pins the trim.
    Assert-Layer -Run (Invoke-Layer -Case 'empty-value' -Manifest 'empty-value.ps1') -Exit 1 `
        -Says @("tier harness has an empty 'name':",
                "tier harness has an empty 'script': st-tier-empty-script",
                "tier harness has an empty 'oracle': st-tier-empty-oracle",
                "tier harness has an empty 'oracle': st-tier-blank-oracle")

    # Refused by integrity check 1 rather than by the merge, which is why it is
    # asserted end to end: the merge reads the manifest strictly so that a tier
    # cannot shrink the suite quietly, and a script the tier declared but never
    # shipped is that same event whichever check happens to catch it.
    # Named with the separators the merge stored, not the ones the manifest was
    # written with. Every check downstream reads the resolved path, and this
    # message is the one place a tier author sees it, so what it prints is worth
    # pinning: a message quoting a spelling nothing else uses is how a reader
    # goes looking for the wrong string.
    #
    # The second entry is the other answer that check gives, and the one a bare
    # existence test cannot tell from a script: a directory. Left through, it
    # reaches the parameter check, whose syntax tree for a directory has no
    # param block - so the run says the harness declares nothing, about a
    # harness nobody wrote. The silence is the assertion as much as the message.
    Assert-Layer -Run (Invoke-Layer -Case 'script-missing' -Manifest 'script-missing.ps1') -Exit 1 `
        -Says @('manifest names a script that does not exist: st-tier -> lib\fuzz-selftest\never-shipped.ps1',
                'manifest names a directory rather than a script: st-tier-directory -> lib') `
        -Silent @('st-tier-directory is called with -ExePath')

    # -RequireLayer is a tier's assertion that its overlay landed. An absent
    # manifest and a wrong one are the same event from here, and both leave a
    # summary shape-identical to the oss run nobody asked for.
    Assert-Layer -Run (Invoke-Layer -Case 'require-missing' -Extra @('-List', '-RequireLayer', 'pro')) -Exit 1 `
        -Says @("-RequireLayer 'pro' but no tier manifest sits beside this runner")

    Assert-Layer -Run (Invoke-Layer -Case 'require-mismatch' -Manifest 'valid.ps1' `
                                    -Extra @('-List', '-RequireLayer', 'enterprise')) -Exit 1 `
        -Says @("-RequireLayer 'enterprise' but the manifest declares 'pro'")

    # The matching case, because a -RequireLayer that refused everything would
    # satisfy both of the two above.
    Assert-Layer -Run (Invoke-Layer -Case 'require-match' -Manifest 'valid.ps1' `
                                    -Extra @('-List', '-RequireLayer', 'pro')) -Exit 0 `
        -Says @("layers: base ($baseCount) + pro (1)")

    # The same name with padding, which is the caller's half of a comparison
    # whose other half is trimmed. Every other caller-supplied string goes
    # through Split-List; this one did not, so a build recipe passing the layer
    # from a variable was told its manifest declares a different layer than it
    # does - a false refusal on the one flag whose job is to prove the overlay
    # landed. Read as a refusal of a correct build, it is worse than the silence
    # it was added to replace.
    Assert-Layer -Run (Invoke-Layer -Case 'require-padded' -Manifest 'valid.ps1' `
                                    -Extra @('-List', '-RequireLayer', ' pro ')) -Exit 0 `
        -Says @("layers: base ($baseCount) + pro (1)") `
        -Silent @('but the manifest declares')

    # The other end of that trim, and the direction that matters more: passed
    # and empty. Trimmed, ' ' is falsy, and every test of this flag reads it for
    # truth - so a recipe whose layer variable came out empty would skip the
    # assertion entirely and report a green base-only run, which is the silence
    # the flag was added to break. Untrimmed it was refused for the wrong
    # reason; the trim has to keep refusing it for the right one.
    Assert-Layer -Run (Invoke-Layer -Case 'require-empty' -Manifest 'valid.ps1' `
                                    -Extra @('-List', '-RequireLayer', '   ')) -Exit 1 `
        -Says @('-RequireLayer was given nothing to require') `
        -Silent @("layers: base ($baseCount) + pro (1)")

    # A filter typo under -List. The integrity checks all run on -List because
    # it is the invocation that needs no build and no desktop, and the header
    # says so - but the typo check sat after the -List block and exited before
    # it, so the one flag anyone would use to ask whether a name is real
    # answered a wrong name by printing the whole manifest and leaving 0.
    Assert-Layer -Run (Invoke-Layer -Case 'list-typo' -Extra @('-List', '-Only', 'totally-bogus')) -Exit 1 `
        -Says @('-Only names no such harness: totally-bogus') `
        -Silent @('harnesses, about')

    # A CHECKOUT path holding a wildcard character, which is not a manifest
    # defect at all: everything this runner reads about itself is rooted at its
    # own directory, so one '[' anywhere above it turned every existence test
    # into a pattern that matches nothing and the directory listing into an
    # empty one. Check 1 refused every base harness on a complete tree,
    # -RequireLayer reported no tier manifest beside a runner that had one, and
    # check 2 - the silent one, and the worse one - passed by finding no files
    # to classify rather than by finding them all classified.
    #
    # All of it off one run: the manifest is placed, so a word about an absent
    # one is a failure; every script is there, so a word about a missing one is
    # a failure; and one unclassified file is dropped in, so the listing HAS to
    # speak. Silence there is the only shape a directory that read as empty
    # could take.
    $bracketRoot = Join-Path $LayerSandbox 'checkout[1]'
    Copy-SuiteScripts -From $PSScriptRoot -To $bracketRoot -Inventory $BaseScripts
    Assert-Layer -Run (Invoke-Layer -Case 'bracket-checkout' -Manifest 'valid.ps1' -Root $bracketRoot `
                                    -Inject @{ 'checkout[1]/stray.ps1' = $LayerStub } `
                                    -Extra @('-List', '-RequireLayer', 'pro')) -Exit 1 `
        -Says @('stray.ps1 is in this directory but neither in the manifest nor in $NotInSuite') `
        -Silent @('no tier manifest sits beside this runner',
                  'names a script that does not exist',
                  'does not declare it')

    # What the copy does that a glob does not, which is the whole reason it is
    # written by name. The difference only shows against a tier checkout - a
    # directory holding the tier's own scripts and the manifest that classifies
    # them - and a copy of THIS directory can never be one, so the source tree
    # is staged rather than found. A glob over it takes two files the inventory
    # does not name: the tier's own harness, which then fails check 2 in the
    # copy, and the tier manifest, which gives every case above a second layer.
    # The inventory also names one file that is not there, because $NotInSuite
    # classifies names and nothing prunes an entry whose file has gone.
    $stagedTier = Join-Path $LayerSandbox 'tier-checkout'
    $stagedCopy = Join-Path $LayerSandbox 'tier-checkout-copy'
    New-Item -ItemType Directory -Force -Path (Join-Path $stagedTier 'lib') | Out-Null
    foreach ($f in @('fuzz-suite.ps1', 'gen-bell.ps1', 'fuzz-tier-harnesses.ps1', 'tier-only.ps1')) {
        Set-Content -LiteralPath (Join-Path $stagedTier $f) -Value "# $f" -Encoding utf8
    }
    Set-Content -LiteralPath (Join-Path $stagedTier 'lib/wintty-process.ps1') -Value '# dot-sourced' -Encoding utf8
    Copy-SuiteScripts -From $stagedTier -To $stagedCopy `
                      -Inventory @('fuzz-suite.ps1', 'gen-bell.ps1', 'fuzz-tier-harnesses.ps1', 'deleted-since.ps1')
    $script:LayerCases++
    foreach ($want in @('fuzz-suite.ps1', 'gen-bell.ps1', 'lib\wintty-process.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagedCopy $want))) {
            $bad += "layer/tier-checkout: the copy left behind $want, which the inventory names"
        }
    }
    foreach ($unwanted in @('tier-only.ps1', 'fuzz-tier-harnesses.ps1')) {
        if (Test-Path -LiteralPath (Join-Path $stagedCopy $unwanted)) {
            $bad += "layer/tier-checkout: the copy carried $unwanted, which only a glob over a tier checkout takes"
        }
    }

    # The other thing taking a source root rather than $PSScriptRoot admits: a
    # source that is not a suite directory at all. lib/ is copied wholesale and
    # not through the inventory, so a missing one is the one argument error this
    # function cannot skip past - and under $ErrorActionPreference = 'Stop' it
    # ended the whole self-test on Copy-Item's own exception, which names a line
    # in here and not the directory it was handed.
    $noLib = Join-Path $LayerSandbox 'no-lib'
    New-Item -ItemType Directory -Force -Path $noLib | Out-Null
    Set-Content -LiteralPath (Join-Path $noLib 'fuzz-suite.ps1') -Value '# fuzz-suite.ps1' -Encoding utf8
    $script:LayerCases++
    $noLibSaid = try {
        Copy-SuiteScripts -From $noLib -To (Join-Path $LayerSandbox 'no-lib-copy') -Inventory @('fuzz-suite.ps1')
        '(it did not refuse)'
    } catch { "$_" }
    if ($noLibSaid -notlike '*has no lib/*') {
        $bad += "layer/no-lib: copying from a directory without lib/ said '$noLibSaid', which does not say what was wrong with the source it was given"
    } elseif (-not $noLibSaid.Contains($noLib)) {
        # Naming the source is the whole reason this is a throw of its own
        # rather than Copy-Item's exception, which names a line in here and not
        # the argument that was wrong. A message that stops at the diagnosis is
        # the same dead end wearing better words.
        $bad += "layer/no-lib: the refusal said '$noLibSaid' without naming the directory it was handed, $noLib"
    }

    # Injected over a file the copy legitimately carries, which is what the
    # sweep's restore branch exists for and what nothing else reaches: every
    # other case names a path the copy does not already hold, so deleting and
    # restoring are indistinguishable. Last on purpose - nothing resets after
    # it, so the sweep below is the only thing that can put the file back.
    Assert-Layer -Run (Invoke-Layer -Case 'injection-over-a-copied-file' -Manifest 'valid.ps1' `
                                    -Inject @{ 'layer-scripts/gen-bell.ps1' = $LayerStub }) -Exit 0 `
        -Says @("layers: base ($baseCount) + pro (1)")

    # Read through Test-Path rather than straight: a sweep that deleted the file
    # is one of the two things under test here, and a read that throws over it
    # ends the self-test with no verdict instead of with this one.
    function Get-LayerFileText {
        param([Parameter(Mandatory)][string]$Path)
        if (-not (Test-Path -LiteralPath $Path)) { return '' }
        return (Get-Content -Raw -LiteralPath $Path).Trim()
    }
    $genBellCopy = Join-Path $LayerRoot 'gen-bell.ps1'
    $genBellSource = Get-LayerFileText (Join-Path $PSScriptRoot 'gen-bell.ps1')
    if (-not $genBellSource) {
        $bad += 'layer/injection-over-a-copied-file: gen-bell.ps1 is not in this directory any more, so the case injects over nothing and asserts nothing; point it at another script the inventory names'
    }
    if ((Get-LayerFileText $genBellCopy) -ne $LayerStub.Trim()) {
        $bad += 'layer/injection-over-a-copied-file: the injection never landed, so the restore below proves nothing'
    }

    # What makes the order-independence above a property rather than an accident
    # of which cases happen to come last, asserted rather than asserted about:
    # the case above is the only one whose leavings a later case would inherit,
    # and this is the only thing that undoes them. The sandbox goes with it - it
    # is most of a megabyte, and nothing under fuzz-out/ is ever pruned.
    Reset-LayerInjection
    if ((Get-LayerFileText $genBellCopy) -ne $genBellSource) {
        $bad += 'the final sweep left gen-bell.ps1 injected or deleted rather than restored, so a case after it would run against a copy missing a script'
    }
    Remove-Item -Recurse -Force -LiteralPath $LayerSandbox -ErrorAction SilentlyContinue

    Write-Host ''
    if ($bad.Count -gt 0) {
        Write-Host 'SELFTEST FAILED' -ForegroundColor Red
        $bad | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        exit 1
    }
    Write-Host ("SELFTEST OK  {0} exit paths classified correctly, {1} tier layer cases, {2} filter refusal case(s), and a real run over them exits 2" -f $expect.Count, $script:LayerCases, $script:RefusalCases) -ForegroundColor Green
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
