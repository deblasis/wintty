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
    tab-colors reads no pixel and mica-dpi never changes the DPI. The `oracle`
    field in the manifest
    below says what each one actually rules out, so a green suite is not read
    as more than it is. Keep those strings honest: they were wrong here once,
    in the direction of promising checks the code did not contain.

    A tier build adds its own harnesses by dropping fuzz-tier-harnesses.ps1
    beside this runner; they append to the base set, so a tier runs what it was
    built on plus what it adds. Absent means base-only. -RequireLayer makes that
    absence an error for a build that should have one. -SelfTest covers that
    merge from a copy of this directory rather than from this one, because a
    fixture manifest placed here would be found by every other run from it.

    Six integrity checks run on every invocation, including -List: the
    manifest cannot name a script that is gone, a script cannot sit in this
    directory unclassified, a harness cannot stop declaring a parameter the
    manifest passes it, no name or tag may hold the comma the filters split
    on, the two numbers the run loop does arithmetic on have to be numbers it
    can do arithmetic with, and no script may define a function name twice or
    define one a script it shares a scope with already defines.

    The first five are free -- hashtable and string work over the
    manifest. The sixth is NOT: it parses every .ps1 under this directory,
    which measures at about four and a half seconds and roughly quadruples
    what -List costs. It runs anyway, and on -List too, for the reason the
    other five do: the suite's own most likely defect is the one that looks
    like success, and #938 is what that looks like -- a harness silently
    asking the wrong function about the wrong rects, reported as a table of
    NOT MEASURED and read as the product being unreachable. Four seconds is
    cheaper than that, but it is not free and the contract should not say it
    is. The third matters because
    `pwsh -File` ignores an argument the script does not declare, so a renamed
    -ExePath would leave every harness quietly testing its own default build.

    A filter typo is refused on every invocation too, -List included, so the
    one flag that needs no desktop also answers whether a name is real.

    Usage:

        just fuzz                     # everything, against the Debug build
        just fuzz "-Tag smoke"        # the fast, high-signal subset
        just fuzz "-Only search,probe"
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
#           smoke set is measured; the rest are upper-bound guesses
# oracle    what a pass from this harness actually rules out
$Harnesses = [System.Collections.Generic.List[object]]@(
    [ordered]@{ name = 'search';         script = 'search-fuzz.ps1';               tags = @('smoke','search'); outDir = $true;  seed = $true;  minutes = 2
                oracle = 'counts matches in the terminal UIA document itself; printable non-space needles are checked against that count, the rest only for a well-formed counter' }
    [ordered]@{ name = 'probe';          script = 'mouse-fuzz-probe.ps1';          tags = @('smoke','core');   outDir = $true;  seed = $false; minutes = 1
                oracle = 'liveness: the app survived the click stream and crash.log did not grow' }
    [ordered]@{ name = 'tab-colors';     script = 'mouse-fuzz-tab-colors.ps1';     tags = @('tabs');           outDir = $true;  seed = $false; minutes = 3
                oracle = 'drives every preset plus None, recolor and a layout round-trip, and asserts the swatches were findable and the layout switched; it compares no pixel, so a build that paints them all alike passes' }
    [ordered]@{ name = 'tab-close-selection'; script = 'mouse-fuzz-tab-close-selection.ps1'; tags = @('tabs'); outDir = $true; seed = $true; minutes = 4
                oracle = 'seam-actuated: it seeds, selects and closes through the in-process test seam - the manager ops a click and a chord funnel into - with zero synthesized OS input, so nothing is typed, no pointer is moved and no foreground is taken while it runs. It closes a randomly chosen OTHER tab in both strips and checks two things about the tab that was active, once the STRIP has stopped changing (a separate clock from the model, which the seam''s close op already settles): UIA still reports it selected, matched on the tab title, which the seam seeds distinct per tab because every tab here runs the same shell and would otherwise be named alike; and the fill painted under every row, measured against the terminal''s own background sampled out of the same capture. The paint claim is directional, and it is what changed: the selected row''s fill must MATCH the terminal background within 3 on the widest channel and every unselected row''s must be at least 6 off it, because the active tab is now painted the terminal ground so that tab and pane read as one surface. The old relative test - the selected row differs from every other by 20 while the others all agree - is gone rather than retuned: the design separates the field from the strip by five percent of the palette''s contrast, nine points on Catppuccin Mocha, so it cannot pass on any theme, and no threshold small enough to admit nine also excludes hover, a colour tag or a repaint in flight. A fill left on a vacated slot is still what it catches, now as the WRONG row reading the terminal ground; a build that painted EVERY row the terminal ground fails on the unselected ones, and a capture too flat to carry a picture is refused as a harness miss rather than reported as that bug. A measurement between the two boundaries is exit 1 with the number in it, not a verdict. It samples one pixel per row, so a fill misplaced by less than a row height passes; it settles before it measures, so a selection briefly wrong and right at rest is recorded as transientOff rather than failed; it never closes the ACTIVE tab, so the successor-selection rule is not exercised; and every read is at rest, so nothing is claimed about the transition. A strip that never stops changing is exit 1. Container RuntimeIds are measured over the same closes and reported as idDrift rather than asserted on: they name a slot, not a tab. Dropped with the SendInput era: the close BUTTON is never pressed, so the horizontal strip''s hover-to-reveal close affordance goes untested and a multi-pane tab''s confirmation dialog is out of reach entirely (the seam''s close op refuses a multi-pane tab); and the pointer can no longer be parked off the strip, so a pointer resting on a row is exit 1 instead of a nuisance. One seed per process, with the tab count falling from eight to two across five rounds per layout, because repeated seed churn in one process trips the filed coreclr access violation' }
    [ordered]@{ name = 'tab-drag';       script = 'mouse-fuzz-tab-drag.ps1';       tags = @('tabs','motion');  outDir = $true;  seed = $false; minutes = 4
                oracle = 'seam-actuated: drives the real drag engine and manager ops over the in-process test seam (zero synthesized OS input; the machine stays usable) and relaunches the app per scenario to stay clear of the filed seed-churn access violation. The oracles are unchanged in kind: final orders and pinned/collapsed statuses through UIA names seeded distinct per tab, visible-row count gates, crash.log growth, and the product''s own drag trace read back per scenario - every session must pair a begin with an end, report zero leaked motions at and after the end, and answer its release the way the motion gate says: a settle with animations on and NO settle on the leg run with animations off, which the harness sets through lib/env-guard (snapshot, set, read-back, restore) and proves the app saw via the trace''s motion=off begin line; that leg''s final order must equal the motion-on scenario''s - the identity pair. The pin boundary is crossed and released outside (stays unpinned, order intact) and inside (pins at the crossing''s slot); a routed collapse folds the group and hides the member; a same-polarity collapse must be swallowed with the active tab unmoved; a drag released on the folded header must join and auto-expand; a tab held over its neighbour until the join ring fills is released into a group with nothing else swept in, while the identical gesture released before the ring fills groups nothing and leaves the ordinary sort working in the same process - the hold is driven through the seam''s own dwell clock rather than a sleep, so what that pair measures is the ring and not the scheduler, and neither leg sees the RING at all: no pixel is compared, so a build that joins correctly while drawing nothing passes, and the horizontal strip''s copy of the gesture is not staged here because the seam has no horizontal drag op; and the layout toggles there and back over two pins, a group and a collapsed chip with both strips rendering. Scenarios are independent processes, so one verdict does not stop the rest; exit 2 on any product finding, 1 when only harness misses remain. Dropped from the SendInput era and tracked in issue #866: the horizontal TabView reorder (previously the horizontal engine''s only automated gesture coverage; the seam has no horizontal drag op yet), the run-label drag-refusal probe with its anti-vacuity hover guard, and the horizontal chip menu round-trip. The pin-boundary-out oracle temporarily accepts both adjacent landing slots and prints the landed order as a FINDING - the one-slot nondeterminism is issue #865' }
    [ordered]@{ name = 'drag-filmstrip'; script = 'vtabs-drag-filmstrip.ps1';      tags = @('tabs','motion');  outDir = $true;  seed = $false; minutes = 2
                oracle = 'films one seam-paced vertical drag frame by frame - CopyFromScreen and the seam''s drag-paced walker on one clock, zero synthesized OS input - and measures the pixels: the selected row''s fill (row 3 is selected through the seam and row 2 drags past it, because the dragged row''s own chrome dims mid-gesture and unselected rows cannot be told apart in pixels) must rise at least 60% of a row height, must have risen 5px within 2 frames of the commit (the gap opening), and must sit within 2px of its final position for 6 consecutive frames within 500ms of it (offsets converging), with the swapped final order read back through UIA. The commit time is the seam response''s own commit offset added to the send stamp, which can only be early - a late-looking gap is real, a flattering one is impossible. Calibration comes from frame 0 and the harness refuses (exit 1) rather than track a guess; it also refuses on a machine whose client-area animations are off, since with them the product cuts the glide this oracle measures. The window must stay visible while the film runs. Frame PNGs are saved next to result.json' }
    [ordered]@{ name = 'seam-acceptance'; script = 'seam-acceptance.ps1';          tags = @('smoke','tabs');   outDir = $true;  seed = $false; minutes = 3
                oracle = 'the crash investigation''s repro made deterministic, driven entirely over the in-process seam (zero synthesized OS input, zero focus steals, so the machine stays usable). Per iteration: seed 5 tabs, pin one, group two, collapse one, toggle the layout twice - so every iteration contains one switch INTO the vertical layout with pins, groups and collapsed chips already in the strip, which is the compound that died on 2026-08-31 (COMException 0x800F1000, a NavigationViewItem style applied onto a ContentControl through ApplyPaneLayout -> set_PaneDisplayMode -> MeasureOverride). It asserts manager truth after every step - order, pin flag, group membership, collapse bit - and that the process is still alive; a final leg drives the drag engine''s real press/threshold/crossing/release and asserts the landed order. It reads the MANAGER, never a pixel and never UIA, so a strip that models the right state and renders it wrongly passes. It runs at the tight command train by default: -GapMs 400 is the pacing that died 6/6 before the realization guard, and the suite does not stage it, so the timing-dependent half of that crash is not covered here' }
    [ordered]@{ name = 'pane-memory';    script = 'pane-focus-tab-switch.ps1';     tags = @('smoke','panes');  outDir = $true;  seed = $false; minutes = 2
                oracle = 'the per-tab active pane, end to end (#869): split a tab, park focus on its RIGHT leaf, leave for another tab, come back, and demand the right leaf is both where typing lands AND where the active-pane chrome is drawn. Two oracles deliberately different in kind. The focus report is read out of FocusManager rather than out of PaneHost.ActiveLeaf, because "the tab remembers its last pane" and "you can type into it" are separate claims - the memory was always there, and asserting it would have passed straight over the bug. The other is pixels from a screenshot of the real window: the inactive-pane dim film must lie over the LEFT leaf, and the cursor block inside the right leaf must be the filled one a focused surface draws rather than the hollow outline an unfocused one leaves. The dim-film assert is a regression guard on the drawing path and is green with or without the focus restore; the focus report and the cursor-block count are the two that go red without it. Seam-actuated, so zero OS input is synthesized and the only thing done to the desktop is reading pixels off it. One split, one shape, two tabs: a deeper tree or a vertical split is not staged' }
    [ordered]@{ name = 'switcher-wrap';  script = 'vtabs-switcher-capture.ps1';   tags = @('tabs','chrome');  outDir = $true;  seed = $false; minutes = 3
                oracle = 'seam-actuated: fourteen real ctrl+t tabs, six tab-colour ops, then cycle{forward} raises the switcher through the chord''s own dispatch. The wrap claim is GATED for the first time: every tile rect read over UIA inside the popup''s 1.2s life must sit within the window''s right edge, and that many tiles must occupy more than one row - the file described this for its whole life and asserted it nowhere. The colours are read back off the state block, and the overview chord lands last for the picture' }
    [ordered]@{ name = 'vtab-geometry';  script = 'vtab-strip-geometry.ps1';       tags = @('tabs','chrome');  outDir = $true;  seed = $false; minutes = 2
                oracle = 'reads the vertical strip''s ARRANGED LAYOUT back over the seam''s element-rects op rather than sampling pixels, because the strip wears Mica and a grab would answer for the desktop behind the window. One process, one seeded state (two pins, one group, two loose tabs), measured at both pane widths - the 48px compact rail and the expanded sidebar. Eight independent checks, findings collected rather than thrown one at a time: every pinned tab is arranged as the same 40px square and every square is inside the band''s own box; two pins share a band row in the expanded pane and stack in the compact rail, which is the wrapping band''s whole claim; the retired pin-boundary stroke is not arranged at either width, so a rule redrawn beside the structural division would be a finding; the close glyph''s right edge sits one named inset in from the pane edge when expanded, and the compact rail carries none at all, since MUXC''s item template lays row content out past 48px and a close button there would be arranged outside the pane; and a group header''s painted span, swatch through chevron, stays inside the pane at both widths. The sidebar also makes a full round trip - expanded, collapsed to the 48 rail, reopened - with every width a DIP off the seam''s paneWidth so the thresholds hold at any monitor scale, and the pane toggle''s UIA name must track the pane (Collapse/Expand sidebar; the XAML default Toggle sidebar is a finding), folded in from the retired mouse-fuzz-vertical-tabs (#930). Because it measures layout it says nothing about paint: a square arranged correctly and drawn in the wrong colour, or not drawn at all, passes. It says nothing about MOTION either - the band''s reflow glide is a composition animation no arranged rect records. It measures one seeded state at two pins, so a band that breaks only at a column boundary (five pins in the expanded pane) is not covered, and only the VERTICAL strip is staged: the horizontal strip''s pinned squares have no element-rects op' }
    [ordered]@{ name = 'field-seam';     script = 'tab-field-seam.ps1';            tags = @('tabs','chrome');  outDir = $true;  seed = $false; minutes = 4
                oracle = 'the join the active tab makes with the terminal, in BOTH layouts. The active tab alone is painted the terminal''s ground and merges into the pane with no line between; the line is removed by a seam cover drawn over the strip of pane border the tab meets, and this measures whether that cover lands on the tab''s own span. It reads ARRANGED GEOMETRY over the seam''s layout-frame op and converts to device pixels with the window''s own scale, because the cover and the tab are the same colour on purpose: a capture shows one continuous surface whether they line up or not, and what a misalignment leaves is one pixel of the pane''s stroke at a corner, which under Mica is indistinguishable from noise. Swept over six window widths and, vertically, three widths x both pane widths, selecting the first, middle and last tab at each - because an equal-width strip divides the window by the tab count, so almost every width puts the tab edges on fractions of a DIP and whether the cover and the tab round onto the same pixel is the whole question. Each pair of edges must round onto the SAME device pixel -- not "within one", because the defect IS one pixel and a budget of one admits it; the sub-pixel drift is recorded but not judged on. It says nothing about COLOUR: a cover that lands perfectly and is filled with the wrong brush passes here, and contrast-oracle.ps1 is what scores the fills. It also says nothing about the transition - every frame is taken at rest, past the 167ms field settle - so a tab that snaps into the field instead of easing into it passes. Two things to read the headline number with. The comparison count is not a count of independent invariants: the vertical cover''s width is a compile-time constant and its left edge is derived from the same row edge the reach check re-derives, so eighteen of the comparisons restate the one before them, and the horizontal cover''s depth is fixed at construction. And the seam rounds every rect to one decimal before the harness scales it, so drift cannot resolve below 0.1 DIP - harmless at scale 1, but at 1.5 or 1.75 that is a sixth of a pixel, enough to hide a real drift or manufacture one, which is the fractional-scale caveat this harness cannot answer. The sweep itself is now checked rather than assumed: MoveWindow''s result is read, the achieved width is read back off the window, and a run that failed to actually resize is a harness miss instead of a set of agreeing measurements of one geometry' }
    [ordered]@{ name = 'frame-chords';   script = 'frame-keybind-check.ps1';       tags = @('smoke','input');  outDir = $true;  seed = $false; minutes = 3
                oracle = 'a chord pressed with focus on the FRAME (title bar, tab strip, chrome) must reach the action it is bound to, and a key belonging to whatever holds focus must never be taken from it. Seam-actuated, zero synthesized OS input: the focus op moves real XAML focus and the chord op calls the window''s own router - focus gate, residual table, libghostty match, dispatch - with the modifier state passed in, since no key is actually held. Four scenarios against fresh processes cover both matching arms (the apprt residual table via Ctrl+Shift+, and libghostty via Ctrl+T), a USER keybind for pin_tab which has no default chord anywhere and so proves the match reads the live set, and the refusal case: with a pane focused the router stands down for a bare letter, a bound chord and Ctrl+T alike. What it cannot see is the framework hop ABOVE the router - that WinUI raises KeyDown on the window content at all - because the harness hands the router its own call; frame-keybind-live-key.ps1 is what observes that, and it is not seam-only' }
    [ordered]@{ name = 'frame-live-key'; script = 'frame-keybind-live-key.ps1';    tags = @('input');          outDir = $true;  seed = $false; minutes = 3
                oracle = 'the framework hop frame-chords cannot reach: that a key the harness did NOT hand to the router is delivered by WinUI as a KeyDown on Window.Content. Delivered by PostMessage to the app''s own InputSiteWindowClass HWND - window-targeted, steals no focus, touches no other application - since the top-level WinUIDesktopWin32WindowClass receives no keyboard input in WinUI 3. A posted message carries no modifier state (GetKeyState answers for input the system queued itself, not for another process''s posts), so the chord under test is a bare f9 bound to new_tab, which the frame''s shape rule admits precisely because nothing on the frame can claim a function key. Two oracles, both from the product: a routedKeyDowns counter the window bumps on every KeyDown reaching its content, so the hop is visible even when nothing acts on the key, and the manager state, so the action is visible too. It DOES synthesize one left click per leg, on this app''s own window after raising it, because only a real click reproduces "the user clicked there" - so it takes the foreground, like the mouse-fuzz harnesses. -WithSyntheticChord adds two legs that press a real Ctrl+Shift+, through SendInput; that is six key events into whatever is foreground, it is OFF by default, and the suite never passes it' }

    [ordered]@{ name = 'cwd-tab-label'; script = 'seam-cwd-tab-label.ps1';        tags = @('tabs','shell');   outDir = $true;  seed = $false; minutes = 5
                oracle = 'the tab label at a shell prompt (#873), one scenario per native shell because the two report their directory in nothing alike: cmd has only `PROMPT $p`, a RAW Windows path on OSC 9;9, while a native PowerShell session sends a `file://HOST/c:/dir` URL on OSC 7. Each asks two independent questions - did the report reach the app at all (the seam reads the pane''s own LastCwd, which stays null for a native shell whenever either arm is dead) and does the strip actually SAY that folder, read twice, from the row''s own TextBlock through the seam and from the rendered row''s UIA Name, neither of which is the model property under test. Each also asserts a decoded interpreter icon, and a final check says pwsh''s is not cmd''s. Each reads the row''s tooltip too, off the TextBlock''s own ToolTipService value: the whole directory, and for the pwsh leg, whose probe sits under the profile, the ~ form while the reported directory stays absolute. A declared-profile leg (name "Probe", command pwsh.exe) is the one that can tell the icon''s tooltip naming the shell from it repeating the profile, and requires the shell on its own line above the name. Every shell leg then flips to the horizontal layout through the seam''s own toggle and reads the TabViewItem''s header and tooltip, plus the TabItem names off UIA, so both strips answer for the same tab. A further scenario asks the security question the first two cannot: a reported cwd becomes a spawn directory, so a UNC one names a server Windows authenticates to. It injects the raw OSC 9;9 form the integration itself emits - a local path then a remote one, in one command, after replacing the prompt function so the shell cannot overwrite the result - and requires the local report to be the one still standing; two reports rather than one is what keeps it honest, since a dead raw arm then fails as a miss instead of passing on silence. The remote host is `.invalid`, which never resolves, so the harness cannot provoke a lookup against anything real. Zero OS input is synthesized: what the shells run arrives as one ghostty_surface_text on the focused pane. It stages the repo''s own src/shell-integration under a GHOSTTY_RESOURCES_DIR, because the Debug layout ships no resources tree - a harness compensation for the build layout, not part of what is under test. Only cmd and pwsh are staged: WSL and MSYS2 take different arms of the same translation and are not covered here' }

    [ordered]@{ name = 'tab-tag-ink';    script = 'tab-tag-ink.ps1';               tags = @('tabs','chrome');  outDir = $true;  seed = $false; minutes = 2
                oracle = 'does a colour-tagged tab''s PROFILE ICON get the tag''s ink? It reads PIXELS, and it has to: the brush was still computed and the call still written when the row lookup went through the header panel''s first child and the group rail took that slot (#833), so a test over resolved brushes would have measured the correct value of a colour nothing painted with (#883, #882). It drives real state through the seam, asks the seam where the icon landed, and samples the ink out of a screen capture. The canary used to be the pushpin; pinned tabs are icon squares now and carry none, so the icon is the glyph that remains, and it is the same loop over the header row''s children that stopped running. The claim is deliberately RELATIVE - the tagged tab''s icon ink IS the tag foreground and is NOT the untagged tab''s icon ink, with both tabs pinned and neither active so the tag is the only difference - because the strip renders light in every leg on this machine (Mica shows a light desktop through it even under a dark theme) and an absolute claim would be measuring the desktop. Like contrast-oracle.ps1 it does NOT call Assert-NoWintty: it launches its own instance with single-instance off against an isolated XDG_CONFIG_HOME, moves only its own window and stops only what it started, so a developer''s Wintty can be running beside it. Only the HORIZONTAL strip is staged, and only the profile icon of the row''s ink pass - the title and the bell share the brush but are not sampled. It also assumes the seeded tab''s icon is a GLYPH: a profile whose icon resolves to a bitmap takes no foreground, and the harness would read that as the tag failing to land' }

    [ordered]@{ name = 'idle-badge';     script = 'idle-badge-check.ps1';          tags = @('tabs','chrome');  outDir = $true;  seed = $false; minutes = 2
                oracle = 'the idle moon and the dimmed row (#989), driven through the tab-idle seam op so the exact INPC chain the product''s idle sweep drives (a one-minute threshold, checked every 30s) is exercised without waiting a minute per leg. Three oracles layered over the HORIZONTAL strip, because no one signal survives every theme: the op must report tabs[n].idle back; header-rect part "idle" must return the moon''s rect only while the tab is idle, so no rect while awake and no rect after the clear are both gated; and PIXELS out of a shot of the real window - the moon''s rect must hold glyph ink, and the title rect''s luminance SPREAD must compress under the 0.45 dim, spread rather than mean because the mean''s direction flips with the theme. The VERTICAL strip and a pinned idle square get the seam readback only: there is no header-rect op for the moon there, so their shots are saved for a human and nothing about their paint is gated - a vertical strip that models idle and draws nothing passes. A tab the real sweep re-idles during a slow run is noted rather than counted against the clear. Seam-driven, zero synthesized input; it takes the machine-wide seam lock itself if none is held, and the window must stay visible for the shots. It refuses to start with a Wintty already open. No leg rings a bell, so the moon''s suppression while a bell is up is not covered' }

    [ordered]@{ name = 'layout-switch';  script = 'layout-switch-filmstrip.ps1';   tags = @('tabs','motion');  outDir = $true;  seed = $false; minutes = 4
                oracle = 'the layout switch, filmed and judged on two clocks that do not share a thread. A before/after pair cannot tell a transition that CARRIES the collapsed state across from one that flashes the run expanded for three frames on the way, because the manager agrees with itself either way - the collapse bit never moves - so the evidence has to be what the strips were HOLDING mid-flight. The STATE track is the layout-frame seam op: both hosts'' rendered inventories with each row''s effective alpha and rect, a few dozen reads per flight, and it is the oracle. The PICTURE track is a separate capture process taking frames from the compositor at the window''s full present rate; it replaced a CopyFromScreen loop that cost ~175ms per grab whatever the region and got three pictures out of an entire flight, the first a third of a second in - a filmstrip that could not see the motion it exists to judge. One pixel cross-check per settled frame says the selected row''s chrome is really on screen where the seam says it is, because a model that agrees with itself while the compositor draws something else is the one failure a model-only oracle cannot see. Seam-driven throughout: no synthesized keystrokes, no focus theft. It takes the machine-wide seam lock itself if none is held, and the window must stay visible for the film. It judges SEQUENCE properties - no frame flashing a collapsed run, no lost selection, no left lane, both legs inside budget - not how the motion FEELS: curve character is not observable at the frame rates involved and is left to a by-eye pass' }
    [ordered]@{ name = 'morph';          script = 'vtabs-morph-fuzz.ps1';          tags = @('tabs');           outDir = $false; seed = $true;  minutes = 3
                oracle = 'seam-actuated randomized layout switching against a full strip, checked against the morph trace the product emits: no switch ends with a ghost on the morph layer, every begin is answered by an end or cancel, and something staged a morph immediately. The toggle is the toggle-layout op - the router event the chord raises - and the vacuity gate is rewritten for it: counted toggles wait out the coordinator first (a mid-flight request no-ops by design), so one begin per counted toggle is the floor and a shortfall leaves with 1, not 2; the uncounted interrupt leg is the one place a dropped request is the product behaving. A probe toggle proves both the router and the trace file are alive before the fuzz spends its minutes. What the migration retired: foreground ownership, thread attachment, the XAML-island arming click, and the chord-miss caps that existed to forgive the desktop refusing them. A seed replays the sequence' }
    [ordered]@{ name = 'inspector';      script = 'mouse-fuzz-inspector.ps1';      tags = @('inspector');      outDir = $true;  seed = $false; minutes = 3
                oracle = 'seam-actuated, send-text armed: the shell is seeded through the seam, the toggle and the tab-change dismissal are chords (Ctrl+Shift+I; a config-bound ctrl+t=new_tab, which the windows default already carries), the resize is a real MoveWindow and the close is the titlebar button through UIA. The inspector must render more than a flat surface, close on demand, and be dismissed by a tab change - all gated. The wheel and ctrl+wheel zoom legs are dropped: neither was read by an exit condition, and the zoom gate reads the live keyboard (#866)' }
    [ordered]@{ name = 'dialogs';        script = 'mouse-fuzz-dialogs.ps1';        tags = @('dialogs');        outDir = $true;  seed = $false; minutes = 2
                oracle = 'each of About, Keyboard Shortcuts and the inspector toggle opened a window, plus liveness' }
    [ordered]@{ name = 'settings';       script = 'mouse-fuzz-settings.ps1';       tags = @('dialogs');        outDir = $true;  seed = $false; minutes = 2
                oracle = 'seam-launched, zero OS input: the palette opens through focus+chord and every click is UIA. The settings window opens with three named vertical-tab cards and the Keybindings page does not take the app down' }
    [ordered]@{ name = 'confirm-always'; script = 'mouse-fuzz-confirm-always.ps1'; tags = @('dialogs');        outDir = $true;  seed = $false; minutes = 2
                oracle = 'seam-actuated: seed two tabs and raise the close through the Ctrl+Shift+W chord (the same TabCloseConfirmation every close path shares). Under confirm-close-surface=always the dialog must appear, Cancel must keep both tabs, and the confirmed Close must drop one - all three gate exit 2, tab counts from the manager' }
    [ordered]@{ name = 'ime-cjk';        script = 'mouse-fuzz-ime-cjk.ps1';        tags = @('input');          outDir = $true;  seed = $false; minutes = 2
                oracle = 'seam-actuated: the paste is the Ctrl+Shift+V chord into the same paste_from_clipboard the palette dispatches, and the read-back is the OSC title round trip - the pasted command sets the shell-reported title to a marker carrying BMP CJK and a supplementary-plane emoji, gated on both halves arriving in tab-labels'' shellTitle. The old verdicts were two assigned trues. The owner''s text clipboard is snapshotted with backoff and restored with a verified read-back (a restore that cannot be confirmed is a harness error, not a clean pass); non-text formats are lost. TSF composition remains unexercised, which the old header both denied and claimed' }
    [ordered]@{ name = 'paste-payloads'; script = 'mouse-fuzz-paste-payloads.ps1'; tags = @('vt');           outDir = $true;  seed = $false; minutes = 3
                oracle = 'seam-actuated, one session two pastes: the OSC payload must land its marker in the shell-reported title (the old osc-paste printed OSC_UNVERIFIED and exited 0 here), and the kitty payload must draw a #FF00CC block the pixel count finds (>=8 sampled hits) - the old kitty half. The pixel oracle refuses rather than guesses: the window must sit fully on the virtual screen, is raised topmost without activation before capturing (CopyFromScreen reads the composited screen), and a pre-paste baseline shot must contain zero matching pixels, or exit 1. Missing conpty.dll is a finding, not a red paste. What the merge retired: the palette path, the foreground-gated Enter, and ~250 duplicated shim lines' }
    [ordered]@{ name = 'undo-osc';       script = 'mouse-fuzz-undo-osc.ps1';       tags = @('vt');             outDir = $true;  seed = $false; minutes = 2
                oracle = 'seam-actuated, send-text armed: the active tab''s leaf count must go 1 -> 2 across a split and back to 1 across the undo chord (Ctrl+Shift+Z) - the old undoOk was assigned true and gated nothing; a closed tab must stay closed until the reopen chord (Ctrl+Shift+T) restores it; and the OSC title the shell writes must land in the tab''s shell-reported title (the caption is masked by the seeded override), which used to print OSC_UNVERIFIED and exit 0. All gate exit 2' }
    [ordered]@{ name = 'contrast';       script = 'contrast-oracle.ps1';           tags = @('chrome','tabs');  outDir = $true;  seed = $false; minutes = 6
                oracle = 'measures RENDERED pixels: it locates each chrome surface over UIA and reads the ink and the ground it actually sits on out of a screen capture, then scores them WCAG AA 4.5 for text, 3.0 for glyphs and >1.2 for fills, plus one class judged from ABOVE: the active row is the field, so its fill must MATCH the terminal (<=1.05) rather than separate from anything. It covers the vertical strip (active and inactive titles, close glyph, the pinned icon square, group title/count/chevron, the selection field), the horizontal strip (titles, close glyph, the pinned icon square, chip title/count/chevron), the switcher tile text -- located from the seam''s own card and preview rects, not as the leftmost run under the popup -- and the terminal foreground, across --no-config plus both built-in halves plus a themed config, in both layouts and both sidebar states. A pinned row carries no title and there is no pin boundary stroke, so neither is measured. It does NOT cover the floating group run label, which carries no automation properties, and a surface it cannot locate is reported as unmeasured and exits 1 rather than passing' }
    [ordered]@{ name = 'preview-theme';  script = 'switcher-preview-theme.ps1';    tags = @('chrome','tabs');  outDir = $true;  seed = $false; minutes = 3
                oracle = 'the Ctrl+Tab switcher pane preview must be FILLED with the terminal background of the theme in force. Rendered pixels again, and for the same reason contrast-oracle reads them: two real themes with far-apart backgrounds (Catppuccin Latte and Mocha, named by absolute path because a seam session''s isolated XDG_CONFIG_HOME puts %APPDATA%\ghostty\themes out of the resolver''s search), each sampled inside the rect the switcher-cells seam op reports for the ACTIVE tile -- the one card the switcher paints at full opacity, every other one being dimmed -- each held to 6 per channel. The third assert is the load-bearing one: the two legs must also differ from each other, so a config that silently resolved to nothing cannot pass twice. It covers the fill only -- the tile text is contrast-oracle''s switcher-tile-text, and the overview grid, which shares PanePreviewRenderer, is not driven here' }
    [ordered]@{ name = 'switcher-groups'; script = 'switcher-groups.ps1';          tags = @('chrome','tabs');  outDir = $true;  seed = $false; minutes = 3
                oracle = 'the Ctrl+Tab switcher must say which tiles belong to a group and which tile the cycle is on. Five legs over one seeded session with a three-tab run in the middle of five tabs: the switcher-cells seam op''s reading of the card (one group over exactly three cells, exactly one head and one tail, two ungrouped, every slot reserving a header band), and then RENDERED pixels for what that reading paints -- the head cell''s header band must differ from an ungrouped slot''s by more than 10 per channel (else the wash composites to the card ground and the field is invisible), exactly one cell reports active and its title matches the manager''s, its tile must out-paint an idle tile in the same field by more than 5 (the dim is 30% of a small distance, so it gets a floor of its own), one more cycle step must move the brightness to a different NAMED tile, which is the leg that stops an always-tile-0 oracle, and a THIRD step must leave the first tile still dim -- a stopped Storyboard reverts to its base, and the base of the tile lit at build time is lit, so two steps cannot see the card showing two selections. It does NOT see the end bar, the field''s rounding, the header''s text or its contrast (that is contrast-oracle''s job), the highlight''s easing or duration, reduce-motion or High Contrast (it runs one session on whatever the desktop is set to), the light theme, a run that wraps across two grid rows, or anything about either STRIP -- it only ever opens the popup' }
    [ordered]@{ name = 'mica-dpi';       script = 'mouse-fuzz-mica-dpi.ps1';       tags = @('chrome');         outDir = $true;  seed = $false; minutes = 3
                oracle = 'seam-launched, zero OS input: the palette opens through focus+chord, the combos through ExpandCollapsePattern. Two backdrop presets and one palette backdrop round-trip into the config file; it reads DPI once and never changes it, and checks PerMonitorV2 by grepping the manifest source' }
    [ordered]@{ name = 'remain';         script = 'mouse-fuzz-remain.ps1';         tags = @('chrome');         outDir = $true;  seed = $false; minutes = 3
                oracle = 'liveness plus the tab overview opening; rename, color, snap, zoom, paste and quake are driven and logged but not gated, so a build missing all six still passes' }
    [ordered]@{ name = 'remain-title';   script = 'mouse-fuzz-remain-title.ps1';   tags = @('session');        outDir = $true;  seed = $false; minutes = 2
                oracle = 'seam-actuated: seed two tabs, split the active one, close the other, and the survivor keeps its title - two halves: the caption (the active tab''s EffectiveTitle) must equal the survivor''s override, which catches the caption left pointing at the closed tab, and the tab-labels op must show the survivor''s shell-reported title unchanged across the close - the leak oracle, refused as a harness miss when no shell title arrives - and the tab-labels op must show its shell-reported title unchanged across the close. The old pwsh-regex oracle could never match that name and was red before the migration (#964); the cross-shell case its header described was never stageable with one profile' }
    [ordered]@{ name = 'jumplist';       script = 'mouse-fuzz-jumplist.ps1';       tags = @('shell');          outDir = $true;  seed = $false; minutes = 3
                oracle = 'jump-list CLI arguments reach the running primary and open what they name, across five checks' }
    [ordered]@{ name = 'splash-race';    script = 'splash-single-instance-race.ps1'; tags = @('startup');      outDir = $false; seed = $false; minutes = 2
                oracle = 'samples the window list for a splash owned by a secondary; demonstrates the race, does not certify its absence' }
    [ordered]@{ name = 'shader-notice';  script = 'shader-notice-fuzz.ps1';        tags = @('smoke','render','startup'); outDir = $true; seed = $true; minutes = 2
                oracle = 'the custom-shader banner in both directions against a staged config: absent or empty raises no banner at all, an unreadable or untranslatable shader raises one reporting a load failure. A translatable shader is only checked for NOT reporting a load failure - a missing compiler or a refused pipeline are the machine as much as the build' }
    [ordered]@{ name = 'frame-style';    script = 'frame-style-fuzz.ps1';         tags = @('chrome','render'); outDir = $true; seed = $true; minutes = 11
                oracle = 'launches once per config over a fixed spanning set run once per half of the built-in theme pair - both window-theme values against all three frame-style values, frame-style unset, and a translucent frame over a solid backdrop - and judges two things. Contrast: the title row''s text against the title row''s own fill, and the tab strip''s text against its own, at WCAG 4.5:1 from a verbatim port of ThemeResolution.RelativeLuminance/ContrastRatio. Material: solid must paint different chrome from frosted for two configs differing only in frame-style, by more than the same channel delta the tab-close harness uses, and a translucent frame over a solid backdrop must paint the SAME chrome as a solid one. frosted against crystal is measured and reported, never asserted - they are one frame material by design, and so is the High Contrast pin, under which the material layer stands down and reports rather than asserting. Five controls run first and each can fail: this harness''s copy of BackdropGround.Estimate agrees with the one in the build under test, two captures of one unchanged window differ in no chrome pixel, a selected tab row measures differently from an unselected one, the title text yields an ink/fill gap far larger than an ink-free strip of the same chrome, and that ink-free strip scores under the floor. It reads the desktop polarity and High Contrast and sets neither. A translucent frame paints a composite, not a colour, so a failed material comparison is re-read against what the product means that composite to be - the palette the case actually loaded, tinted over the system base for the active desktop polarity, from a mirror of BackdropGround.Estimate that a control holds against the shipped Ghostty.Core.dll. A composite that lands within the same channel delta of the opaque frame''s own shade is reported as indistinguishable and exits 1 rather than being filed as a defect; the raw screen behind the window is recorded and decides nothing. It samples chrome only, so a wrong terminal background passes; it compares region MEANS, so a fill that is right on average and wrong in places passes; it scores contrast off the screen rather than off the theme, so a row that is readable against a wallpaper that happens to sit behind it passes; only the VERTICAL layout is staged, so the horizontal strip is not sampled at all; the -Random cases get the contrast layer only, because the material layer needs a matched pair of configs; and a genuine frame-style defect goes UNJUDGED rather than reported whenever the palette sits within the channel delta of the system base after tinting, which is the price of never filing a comparison the estimate says nothing was meant to move in; the theme axis is staged rather than inherited: wintty-light and wintty-dark are written as theme files under the same root the staged config lives in, so libghostty and the C# chrome resolve the name against the same directory, the catalogue is enumerated under that staging and refuses a run whose own pair is missing from it, the launches pass --config-file so libghostty gets the config by name while the XDG override still steers the shell half, and a third layer asserts the two halves of the pair paint different chrome under window-theme=wintty with a solid frame - painted from the theme, not composited, so the composite excuse does not apply - standing down under High Contrast exactly like the material layer' }
)

# Deliberately not in the manifest. This is a list rather than a comment
# because the integrity check below reads it: a new harness dropped into this
# directory has to be classified, and prose does not fail a run.
$NotInSuite = [ordered]@{
    'ShellIntegrationPs1.Tests.ps1' = 'plain-assert tests with their own 0/1 exit (see its header), run by its own command line; deliberately unautomated - no recipe, no CI, no suite entry'
    'fuzz-suite.ps1'                = 'this runner'
    'aot-fuzz.ps1'                  = 'a runner; targets the NativeAOT publish, which this suite can also do with -ExePath'
    'vtabs-visual-qa.ps1'           = 'a runner'
    'release-smoke.ps1'             = 'a runner'
    'mouse-smoke-run.ps1'           = 'the operator drives the checklist by hand'
    'dump-uia-state.ps1'            = 'a UIA-loss discrimination battery an operator points at a live window at a HARVEST_MISS moment; emits an evidence block, no verdict to aggregate'
    'gen-bell.ps1'                  = 'generates a test asset'
    'fuzz-tier-harnesses.ps1'       = 'the tier layer manifest, not a harness; read below'
    'clipboard-fuzz.ps1'            = 'no desktop and no app; it drives the marshalling round-trip oracles in Ghostty.Tests, which the build ladder already runs at a cheaper iteration count'
    'kitty-clipboard-roundtrip.ps1' = 'runs INSIDE Wintty rather than launching it, and its attended mode waits on a human at the permission prompt; -Unattended needs clipboard-read and clipboard-write set to allow, which is a config this suite does not stage'
    # Both crash harnesses are written against a published NativeAOT build and
    # are pointed at one by their own usage text: the coverage map crash-matrix
    # asserts is the NativeAOT answer, and under the CoreCLR binary this suite
    # hands out, Main's catch-all swallows native-seh and captures
    # managed-unhandled, so the rows would fail on the build and not the
    # product. Neither leaves a 2, so a coverage failure could not be told from
    # a harness that could not run even if the build were right.
    'crash-matrix.ps1'              = 'gates a published NativeAOT build against the crash coverage map; pointed at the Debug build this suite stages, CoreCLR answers differently and every row fails for the build rather than the product. Its exits are 0 for the map holding, 1 for it not holding or for a launch that could not happen, and nothing for findings'
    'crash-canary.ps1'              = 'a measurement, not a gate: it exits 0 whether canaries are found or not, so a pass rules nothing out and would read as more than it is here. It also needs a crash the Debug build this suite stages does not produce, since CoreCLR swallows native-seh; no envelope means exit 1, and there is no findings exit'
    # The seam''s investigation tools. seam-acceptance.ps1 is the one that
    # aggregates and is in the manifest above; these four are pointed at a
    # live process by hand, and none of them answers the question the suite
    # asks.
    'seam-probe.ps1'                = 'a connectivity probe: it launches, waits for the pipe and reports what it saw. No verdict to aggregate'
    'seam-bisect.ps1'               = 'an operator drives it: mandatory -Scenario and -Ops name the compound to narrow, and it prints SURVIVED or CRASHED(op) so a minimal repro can be found. It answers a question about one hypothesis, not about the build'
    'seam-cdb.ps1'                  = 'needs cdb on PATH and takes a full dump at a fail-fast. Exits 0 with a dump path, 1 when the dump never appeared - so a crash that IS reproduced and a debugger that never fired look alike to an aggregator'
    'seam-crash-dump.ps1'           = 'writes WER LocalDumps registry state (snapshot, set, restore) and needs to be the only thing holding the process, since WER does not run under a debugger. Its exits are inverted for this suite''s purpose: 2 means the app SURVIVED and there was no crash to dump, which the runner would file as a product finding'
    'layout-motion-profile.ps1'     = 'an analysis tool, not a harness: it reads a layout-switch-filmstrip run that has already happened (-RunDir and -Tag, no -ExePath) and prints what moved, when and where. It launches nothing and returns no verdict; the change-box column is for a human reading a filmstrip that already failed'
    'backdrop-stage-selftest.ps1'   = 'measures an instrument, not the product: it proves lib/BackdropStage paints the scene it was told to and never takes the foreground. Launches no Wintty, takes no -ExePath, and has no findings exit'
    # The theme matrix is exploratory and long: hours for the curated set, a
    # day and more for the catalogue, and it flips the desktop theme and the
    # wallpaper while it runs, which no other harness here does. Its red run
    # is the expected outcome and the matrix.md it leaves is the deliverable
    # (#937), so aggregating its exit into a suite verdict would say nothing.
    'theme-matrix.ps1'              = 'hours long by design and it sets the desktop theme and wallpaper; run it on its own through `just theme-matrix`, which takes the build and desktop lanes itself, and read its matrix.md (#937)'
    'theme-matrix-report.ps1'       = 'an analysis tool: it reads a theme-matrix run that has already happened (-RunDir, no -ExePath) and writes matrix.md. Launches nothing and returns no verdict'
}

# Deliberate redefinitions integrity check 6 must not refuse, value = why.
# Two key shapes, because they answer different questions and one must not
# silence the other: "<file>::<Function>" excuses a repeat WITHIN that file,
# and "<file>::<lib>::<Function>" excuses that file deliberately overriding a
# name from that library. A single namespace would let an entry written to
# allow a library override wave through a genuine in-file duplicate of the
# same name -- which is exactly the #938 shape.
#
# A reason is required, for the same purpose $NotInSuite's entries carry one:
# an unexplained exception is how the next silent shadowing gets waved
# through. Empty is the expected state.
$FunctionOverrides = [ordered]@{
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

        # minutes and timeoutSeconds are deliberately NOT here, and the argument
        # that moved the comma rule out onto the merged set moved them too: what
        # makes those two dangerous is the run loop's budget arithmetic, and that
        # arithmetic runs over a base harness exactly as much as over a tier's.
        # Read here they were tier-only, so a base entry of minutes = 'soon' got
        # neither the coercion nor the range test. They are integrity check 5,
        # below, and they NORMALISE there rather than only test there - the rule
        # this block keeps is the rule that one keeps too.

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
            #
            # What the list HOLDS decides which of the two things went wrong,
            # and telling an author who wrote pairs that they wrote a list of
            # names is the same wrong-blame from the other side: `harnesses =
            # @( ... )` sits two lines above in every manifest, so wrapping the
            # pairs the same way is the natural thing to type and the wrapper is
            # the whole of the mistake. The top-level check goes out of its way
            # to say this about the manifest itself; this is the same sentence
            # about the same shape one level down.
            $wrappedPairs = @($declared.notInSuite | Where-Object {
                $_ -is [System.Collections.IDictionary] -or $_ -is [System.Management.Automation.PSCustomObject]
            })
            if ($wrappedPairs.Count -gt 0) {
                $tierProblems += 'tier notInSuite holds the name = reason pairs inside a list; write them as one object, with no @( ) around it'
            } else {
                $tierProblems += 'tier notInSuite must be written as name = reason pairs, not a list of names'
            }
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

    # Here, and not folded in with the five checks below, which would report
    # both sets at once. It reads like a lost opportunity and it is the
    # opposite: everything below reads a merged entry as though its fields were
    # there, so a manifest whose fields are NOT there gets answered about the
    # wrong one. Reproduced by deferring this exit - an entry with no 'script'
    # and a minutes of 'soon' printed "manifest names a directory rather than a
    # script: st-tier -> ", because an absent script resolves to this directory,
    # and blamed the tier author for a field they simply left out. That is the
    # wrong blame this file spends most of its comments closing, so the shape
    # of an entry is settled before anything reads its contents.
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
    # A budget of its own for the same reason, from the other direction: it
    # passes in under a second when both pipes are drained, and a runner that
    # drains one leaves it blocked until something kills it. Short, so the
    # failure costs ten seconds and a retry rather than the three-minute floor.
    [ordered]@{ name = 'st-stderr-flood';  script = 'lib/fuzz-selftest/stderr-flood.ps1';  tags = @('selftest'); outDir = $true; seed = $false; minutes = 0; timeoutSeconds = 10; oracle = 'fixture' }
)

# `pwsh -File` hands every argument over as a string, so `-Only search,probe`
# arrives as one element "search,loop" and matches nothing. Splitting here
# means the documented form works, and the -Only typo check below still
# catches a genuinely wrong name rather than blaming the whole list.
function Split-List {
    param([string[]]$Value)
    if (-not $Value) { return $Value }
    return @($Value | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

# The one place a manifest number is read for the run loop's arithmetic, and
# the one place it is said what is wrong with one that cannot be. `-as [int]`
# answers $null to all three ways it goes wrong alike, and a message built from
# the value alone then prints a number and calls it non-numeric: 2147483648 and
# @(30) both did. Out of Int32 is a range fact and belongs with the other range
# fact; a list is not a number at all, it is a number someone wrapped, and the
# value it prints is the one inside the wrapper - which reads as the manifest
# being told a correct number is wrong.
#
# Not the coercion `[int]` parameter binding performs, and the difference is
# worth naming rather than assumed away: binding unrolls a single-element array
# and this refuses it. The two agree over every value this returns a number
# for, which is all the run loop ever receives, so the run loop can go on
# reading what was stored without a cast of its own.
#
# Returns either a value or a problem, never both: a caller that got a number
# has nothing left to check.
function Read-ManifestInt {
    param($Value)
    # A table before a list, because a table is one: @{ } answers IEnumerable,
    # so told apart from a list it was told to drop a @( ) it never wrote -
    # this function's own wrong-blame, one shape further along.
    if ($Value -is [System.Collections.IDictionary]) {
        return @{ problem = 'is written as @{ } rather than as a number' }
    }
    if ($null -ne $Value -and $Value -isnot [string] -and $Value -is [System.Collections.IEnumerable]) {
        return @{ problem = 'is a list rather than a number; drop the @( ) from around it' }
    }
    # The two shapes `-as [int]` answers with a number rather than with $null,
    # which is how they reached the run loop as budgets nobody wrote. $true is
    # 1 and $false is 0, and an empty or blank string is 0 - a harness the run
    # loop then treats as costing nothing. $null is deliberately NOT here: it
    # coerces to 0 too, and the callers' floors are what speak about it, which
    # keeps "the key is there and says nothing" one event rather than two.
    if ($Value -is [bool]) {
        return @{ problem = 'is a true/false rather than a number' }
    }
    if ($Value -is [string] -and -not $Value.Trim()) {
        return @{ problem = 'is empty rather than a number, and an empty one coerces to 0' }
    }
    $asInt = $Value -as [int]
    if ($null -ne $asInt) { return @{ value = $asInt } }
    # Numeric by every measure except the one that matters. A double reaches
    # here for a value too large for Int32 and for 1e10, which is how a manifest
    # writes a big number without meaning to. NaN and the infinities reach it
    # too and are none of that: a range fact said about a value that has no
    # place on the range is the same wrong blame the range branch was split out
    # to end, so they fall through to the sentence that fits them.
    $asDouble = $Value -as [double]
    if ($null -ne $asDouble -and [double]::IsFinite($asDouble)) {
        return @{ problem = "is outside Int32, which is the range the run loop's budget arithmetic works in: $Value" }
    }
    return @{ problem = "is not a number: $Value" }
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

# Launches one harness and hands back everything that has to be closed again.
#
# Started through ProcessStartInfo rather than through Start-Process, for the
# two log paths. Start-Process resolves -RedirectStandardOutput and
# -RedirectStandardError as WILDCARDS and offers no literal form, so a '['
# anywhere above the run root fails the launch outright with "the wildcard path
# ... did not resolve to a file" - and the default root sits under this
# directory, which makes a bracketed CHECKOUT enough. That is a terminating
# error from inside the run loop: no summary.json, no verdict table, exit 1
# over a tree with nothing wrong with it. The streams are opened here instead,
# by .NET, which has no notion of a pattern in a file name.
#
# ArgumentList replaces the hand-quoting that had to go with Start-Process,
# which does not quote its argument array at all: a path holding a space
# silently became two arguments and pwsh printed its usage. .NET builds the
# command line from the list, so the escaping rule is the runtime's rather than
# one written out here.
#
# BOTH pipes are drained, not the one that is read back: a child that fills the
# pipe nobody is reading blocks there forever, and a harness driving a GUI for
# minutes has plenty to say on stderr. CopyToAsync does the draining, into
# streams opened unbuffered - the tail below reads the same files through a
# second handle, and cannot see a write still sitting in a buffer.
#
# One difference from Start-Process is left standing rather than closed:
# .WorkingDirectory is unset, so the child inherits this process's
# [Environment]::CurrentDirectory, where -NoNewWindow gave it PowerShell's
# Get-Location. The two only part company when something Set-Location's without
# telling the .NET side, which nothing here does, and every path a harness is
# handed is absolute - so it costs nothing today. It is named because a harness
# that starts reading a relative path is where it stops being free.

# Minimizes every other window, and reports what would not go.
#
# The harnesses click at screen coordinates and refuse the click when
# WindowFromPoint says the pixel belongs to somebody else. That guard is
# right -- clicking blind into another app is worse than failing -- but with
# nothing clearing the desktop first, whatever the developer left on screen
# decides how much of the suite runs. One round lost 10 of 21 harnesses that
# way, and the reason was a HARVEST_MISS buried in a single harness's stderr,
# which reads as a product problem rather than a desktop one.
#
# Shell.Application's MinimizeAll was the obvious tool and does not work here:
# it returned without error and left all ten windows exactly where they were.
# ShowWindow per window does work, so this drives each one directly and then
# CHECKS, because the first version of this reported the before-count as its
# achievement and claimed ten successes having minimized nothing.
#
# The console this run prints into goes down with everything else: it belongs
# to the terminal host, not to this process, and a terminal parked over the
# app under test refuses a harness click like any other window. skipPid only
# spares a window this process owns, and at the one call site there are none.
function Clear-Desktop {
    if (-not ('DesktopClear' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class DesktopClear {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);

    public const int SW_MINIMIZE = 6;
    const int DWMWA_CLOAKED = 14;

    // A cloaked window answers IsWindowVisible with true while being nowhere
    // on screen: a window on another virtual desktop, or a suspended UWP
    // shell. It can neither steal a click nor be minimized, so counting it
    // would put a permanent false entry under WOULD NOT MINIMIZE.
    static bool OnScreen(IntPtr h) {
        int cloaked;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0) {
            return false;
        }
        return true;
    }

    public static IntPtr[] TopLevel(uint skipPid) {
        var found = new System.Collections.Generic.List<IntPtr>();
        IntPtr shell = GetShellWindow();
        EnumProc cb = (h, lp) => {
            if (h == shell || !IsWindowVisible(h) || IsIconic(h) || !OnScreen(h)) return true;
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == skipPid) return true;
            var sb = new StringBuilder(256);
            if (GetWindowText(h, sb, 256) <= 0) return true;
            found.Add(h);
            return true;
        };
        EnumWindows(cb, IntPtr.Zero);
        return found.ToArray();
    }

    // Asked of the handles that were actually driven, which a second
    // enumeration cannot answer: it scores a window that closed itself as a
    // success, counts one that opened during the wait as a refusal, and can
    // come out negative.
    public static bool StillUp(IntPtr h) {
        return IsWindow(h) && IsWindowVisible(h) && !IsIconic(h) && OnScreen(h);
    }

    public static string TitleOf(IntPtr h) {
        var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString();
    }
}
'@
    }

    $before = @([DesktopClear]::TopLevel([uint32]$PID))
    foreach ($h in $before) { [void][DesktopClear]::ShowWindow($h, [DesktopClear]::SW_MINIMIZE) }
    Start-Sleep -Milliseconds 700

    $stuck = @($before | Where-Object { [DesktopClear]::StillUp($_) })
    Write-Host ("desktop: minimized {0} of {1} window(s)" -f ($before.Count - $stuck.Count), $before.Count)

    if ($stuck.Count -gt 0) {
        # Whatever refuses to minimize is the most likely thief of a later
        # click: always-on-top overlays, and anything that re-raises itself.
        # Named now rather than inferred from a harness failure much later.
        $names = @($stuck | ForEach-Object { [DesktopClear]::TitleOf($_) } | Where-Object { $_ })
        Write-Host ("  WOULD NOT MINIMIZE: {0}" -f ($names -join ', ')) -ForegroundColor Yellow
        Write-Host '  a harness that clicks under one of these will be refused'
    }
}

function Start-HarnessProcess {
    param(
        [Parameter(Mandatory)][string[]]$Argv,
        [Parameter(Mandatory)][string]$LogPath,
        [Parameter(Mandatory)][string]$ErrLogPath
    )
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = 'pwsh'
    foreach ($a in $Argv) { [void]$psi.ArgumentList.Add($a) }
    # No new window, which is what -NoNewWindow bought: the child inherits this
    # console. Redirecting is what requires it to be false at all.
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    # Nothing that can fail is left between a started child and the return that
    # hands it back. The caller closes what this returns in a finally, so a
    # throw AFTER the start returns nothing to close: the child - a GUI process
    # holding the foreground, on a real run - keeps running with no reference
    # anywhere able to kill it, the first stream leaks its handle onto the log
    # the retry then tries to move, and the terminating error ends the run from
    # inside the loop, with no summary and no verdict table. Opening both
    # streams first turns that into a failure with nothing started yet; the
    # catch covers what is left, which is a start that fails with the streams
    # already open.
    $outStream = $null
    $errStream = $null
    $child = $null
    try {
        $outStream = [System.IO.FileStream]::new($LogPath, [System.IO.FileMode]::Create,
                                                 [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite, 1)
        $errStream = [System.IO.FileStream]::new($ErrLogPath, [System.IO.FileMode]::Create,
                                                 [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite, 1)
        $child = [System.Diagnostics.Process]::Start($psi)
        return @{
            child   = $child
            streams = @($outStream, $errStream)
            pumps   = @($child.StandardOutput.BaseStream.CopyToAsync($outStream),
                        $child.StandardError.BaseStream.CopyToAsync($errStream))
        }
    } catch {
        # The tree, like the budget's kill: a harness that got as far as
        # starting has usually started the app already.
        if ($null -ne $child) {
            try { $child.Kill($true) } catch { }
            try { $child.Dispose() } catch { }
        }
        foreach ($s in @($outStream, $errStream)) {
            if ($null -ne $s) { try { $s.Dispose() } catch { } }
        }
        throw
    }
}

# The other half, in a finally: a harness that could not run is retried, so a
# stream left open here is a stream still holding console.log when the retry
# tries to move the directory out from under it.
function Close-HarnessProcess {
    param([Parameter(Mandatory)]$Launched)
    # The pumps end when the child's pipes close, which a kill does as surely
    # as an exit. Bounded anyway: an unbounded wait here would give back the
    # runaway the budget above just took away.
    try { [void][System.Threading.Tasks.Task]::WaitAll($Launched.pumps, 5000) } catch { }
    foreach ($s in $Launched.streams) { try { $s.Dispose() } catch { } }
    try { $Launched.child.Dispose() } catch { }
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
    # Both fields are read as stored, with no coercion of their own. Integrity
    # check 5 coerced and range-checked them over the merged set before this
    # ran, so a cast here would only be a second normalisation of the same
    # value - which is the one habit every round of this file has had to
    # unpick. The [int] on the Max is for its double return, not for the
    # manifest; the check is what keeps the multiply inside Int32.
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
            #
            # -LiteralPath on the removal as much as on the move. A run root
            # holding a '[' - the default one sits under this directory, so a
            # bracketed CHECKOUT is enough - turns the path into a pattern that
            # matches nothing, and Remove-Item then removes nothing and says
            # nothing. The move onto it fails next, silently, and the retry
            # overwrites the evidence this block exists to keep.
            $kept = "$out.attempt$try"
            Remove-Item -Recurse -Force -LiteralPath $kept -ErrorAction SilentlyContinue
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
        # A separate process rather than `& pwsh`, because a call operator
        # cannot be interrupted: a harness that wedges with the foreground
        # grabbed would otherwise hang the whole run with no way out but
        # Ctrl-C. Output goes to files and is tailed back to the host, which
        # keeps the live progress a long run needs.
        $log = Join-Path $out 'console.log'
        $errLog = Join-Path $out 'console.err.log'
        $launched = Start-HarnessProcess -Argv $argv -LogPath $log -ErrLogPath $errLog
        try {
            $code = Wait-ChildWithTail -Child $launched.child -LogPath $log -TimeoutSeconds $timeoutSeconds -Label $Harness.name
        } finally { Close-HarnessProcess -Launched $launched }

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
# including -List. Between them these six checks cover the ways the manifest
# rots, in both directions, and the way a harness rots itself.
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
    # Parse errors were discarded here. A harness that does not parse then
    # read as one declaring no parameters, so the run reported a plausible
    # wrong thing -- "called with -ExePath but does not declare it" -- for a
    # script whose real problem is a missing brace. Say what actually broke.
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $path, [ref]$null, [ref]$parseErrors)
    if ($parseErrors -and $parseErrors.Count -gt 0) {
        $first = $parseErrors[0]
        $problems += "$($h.script) does not parse: $($first.Message) (line $($first.Extent.StartLineNumber))"
        continue
    }
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

# 5. The two fields the run loop does arithmetic on. minutes reaches
#    [math]::Max(180, minutes * 60 * 4) for the runaway budget and
#    timeoutSeconds is handed to that budget directly, both from inside a
#    foreach with no catch - so a value that throws there kills the report
#    block, and every verdict already collected, findings included, is
#    discarded: the run leaves with 1 "could not run" where 2 "findings" was
#    owed. Base harnesses append first, so on a full run it is their results
#    that go.
#    NORMALISED here rather than only tested here, for the reason every other
#    field in this file is: -List prints minutes and the run loop multiplies
#    it, and a value put into shape for one and read raw by the other is the
#    defect this file keeps growing back. The coercion is not a tidiness - a
#    string multiplies by REPEATING, so '2' * 60 is a 60-character string.
#    Placed here, over the merged set and over the fixtures, rather than in the
#    tier block where both used to sit: the rule is the run loop's and applies
#    to a base harness exactly as much as to a tier's, which is the same
#    argument check 4 is placed on. Read there, a base entry of minutes =
#    'soon' got neither the coercion nor the range test.
#    The floor differs between them because their defaults do. minutes goes
#    through [math]::Max(180, ...), so a 0 is a harness that costs nothing to
#    run and the fixtures below say so honestly; a NEGATIVE one is still
#    subtracted from the total -List prints for a full run. timeoutSeconds is
#    the override that SKIPS that floor, so nothing at all stands between a
#    budget of 0 and every attempt being killed the moment it starts - which
#    reads as a wedged harness rather than as a bad manifest. A null coerces to
#    0 rather than to nothing, so only the floor can see one.
#
# Derived from the same 60 * 4 the run loop multiplies by rather than written
# out: a bound copied by hand is a bound that goes stale the first time the
# multiplier moves. PowerShell WIDENS an Int32 overflow rather than wrapping it,
# and [math]::Max's Int32 overload then refuses the widened argument - which is
# how a manifest of 9000000 got as far as -List printing a total and died in the
# run loop with every earlier verdict.
$MaxHarnessMinutes = [int][math]::Floor([int]::MaxValue / (60 * 4))

foreach ($h in @($Harnesses) + @($SelfTestHarnesses)) {
    $where = if ($h.Contains('layer')) { $h.layer } else { 'selftest fixture' }

    if ($h.Contains('minutes')) {
        $read = Read-ManifestInt $h.minutes
        if ($read.problem) {
            $problems += "$where harness '$($h.name)' has a minutes that $($read.problem)"
        } elseif ($read.value -lt 0) {
            $problems += ("$where harness '$($h.name)' has a minutes of $($read.value); " +
                          '-List subtracts it from the total it prints for a full run')
        } elseif ($read.value -gt $MaxHarnessMinutes) {
            $problems += ("$where harness '$($h.name)' has a minutes of $($read.value); " +
                          "the run loop multiplies it by 60 * 4 for the runaway budget, and anything over " +
                          "$MaxHarnessMinutes leaves Int32 there and throws out of the loop that collects the verdicts")
        } else {
            $h.minutes = $read.value
        }
    }

    if ($h.Contains('timeoutSeconds')) {
        $read = Read-ManifestInt $h.timeoutSeconds
        if ($read.problem) {
            $problems += "$where harness '$($h.name)' has a timeoutSeconds that $($read.problem)"
        } elseif ($read.value -lt 1) {
            $problems += ("$where harness '$($h.name)' has a timeoutSeconds of $($read.value); " +
                          'a budget below one second kills every attempt the moment it starts')
        } else {
            $h.timeoutSeconds = $read.value
        }
    }
}

# 6. A script defining the same function name twice, or defining one a script
#    it shares a scope with through a dot-source already defines. PowerShell
#    defines functions as it reads them, so the later definition silently
#    replaces the earlier for the whole scope -- including for calls written
#    ABOVE it. There is no shadowing diagnostic, no default analyzer rule, and
#    nothing fails at parse time, so the only symptom is the wrong body running.
#
#    #938 is the worked example: contrast-oracle.ps1 grew a second
#    Test-RectInside for the seam's rects while the first served UIA's, and
#    Test-Samplable -- the gate on every ink sample in both strips -- began
#    asking the seam version about UIA rects. Case-insensitive property lookup
#    let .x find .X so the comparison looked plausible; .w found nothing and
#    returned $null. The run came back a full table of NOT MEASURED and exit 1,
#    which reads as "the harness could not reach the product" rather than "the
#    harness broke itself" -- and it survived a review, a signoff and a merge.
$dupRoot = $PSScriptRoot
# RECURSIVE. Enumerating this directory flat reached 66 of the 125 .ps1 files
# under it and missed lib/fuzz-selftest/ entirely -- which is where the guards
# for the other five checks live, and which dot-sources back into a scanned
# lib, so the blind spot ran in both directions.
$dupScanFiles = @(Get-ChildItem -LiteralPath $dupRoot -Filter '*.ps1' -File -Recurse)

# Keyed by path relative to this directory, because recursion makes a leaf
# ambiguous: lib/pro/x.ps1 and x.ps1 are two files with one leaf.
$dupDefs = @{}
$dupSources = @{}
foreach ($f in $dupScanFiles) {
    $rel = $f.FullName.Substring($dupRoot.Length).TrimStart(
        [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $rel = $rel.Replace([IO.Path]::DirectorySeparatorChar, [char]0x2F)

    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $f.FullName, [ref]$null, [ref]$parseErrors)
    # Reported, not discarded. ParseFile returns an AST for unparseable input
    # and simply stops collecting at the break, so a duplicate after it is
    # invisible -- and check 3 parses only manifest harnesses, so for anything
    # under lib/ this is the only parse there is. The lesson is check 3's, 150
    # lines above: say what actually broke.
    if ($parseErrors -and $parseErrors.Count -gt 0) {
        $problems += ("$rel does not parse, so nothing in it can be checked: " +
                      $parseErrors[0].Message +
                      " (line $($parseErrors[0].Extent.StartLineNumber))")
        continue
    }

    # Top-level only. FindAll recurses, and a function defined inside another
    # function -- or inside a script block that is invoked separately -- is
    # scoped to that call, so counting it would refuse working code.
    $byName = @{}
    foreach ($fn in $ast.FindAll({
            param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
        $nested = $false
        $parent = $fn.Parent
        while ($parent) {
            if ($parent -is [System.Management.Automation.Language.FunctionDefinitionAst] -or
                $parent -is [System.Management.Automation.Language.ScriptBlockExpressionAst]) {
                $nested = $true; break
            }
            $parent = $parent.Parent
        }
        if ($nested) { continue }
        # Function names are case-insensitive in PowerShell, so Get-Foo and
        # get-foo are one function and one shadowing the other is the bug.
        $key = $fn.Name.ToLowerInvariant()
        if (-not $byName.Contains($key)) { $byName[$key] = @() }
        $byName[$key] += @{ Name = $fn.Name; Line = $fn.Extent.StartLineNumber }
    }
    $dupDefs[$rel] = $byName

    # Dot-sourced files, read as TEXT out of the command's extent rather than
    # as a string literal. `. (Join-Path $PSScriptRoot 'lib/x.ps1')` is a
    # constant, but `. "$PSScriptRoot/lib/x.ps1"` is an expandable string and a
    # literal-only reader skips it in silence -- which switches this whole half
    # of the check off for that file while every guard stays green. Same
    # nesting rule as above: a dot-source inside a function is function-scoped.
    $fileText = [IO.File]::ReadAllText($f.FullName)
    $sourced = @()
    foreach ($cmd in $ast.FindAll({
            param($n) $n -is [System.Management.Automation.Language.CommandAst] -and
                      $n.InvocationOperator -eq [System.Management.Automation.Language.TokenKind]::Dot }, $true)) {
        $nested = $false
        $parent = $cmd.Parent
        while ($parent) {
            if ($parent -is [System.Management.Automation.Language.FunctionDefinitionAst] -or
                $parent -is [System.Management.Automation.Language.ScriptBlockExpressionAst]) {
                $nested = $true; break
            }
            $parent = $parent.Parent
        }
        if ($nested) { continue }
        $found = [regex]::Matches($cmd.Extent.Text, '[\w.\-]+\.ps[dm]?1')
        if ($found.Count -lt 1) {
            # A VARIABLE dot-source is resolved through its own file before
            # being excused: ShellIntegrationPs1.Tests.ps1 writes
            # `$script:integration = Join-Path $PSScriptRoot '...ghostty.ps1'`
            # one line up, and the leaf is right there in the assignment.
            # Reading it keeps `. $p` where `$p = 'x.ps1'` visible to this
            # check instead of silently dynamic.
            $varDot = [regex]::Match($cmd.Extent.Text, '\.\s*(\$(?:script:|local:|global:|private:)?[A-Za-z_]\w*)')
            if ($varDot.Success) {
                $esc = [regex]::Escape($varDot.Groups[1].Value)
                $assign = [regex]::Match($fileText, ($esc + '\s*=\s*([^\r\n]+)'))
                if ($assign.Success) {
                    $found = [regex]::Matches($assign.Groups[1].Value, '[\w.\-]+\.ps[dm]?1')
                }
            }
        }
        if ($found.Count -lt 1) {
            if (-not $varDot.Success) {
                # DYNAMIC by design after resolution failed: no path literal
                # in the command and no literal in the variable's own
                # assignment - composed at runtime from non-literals. There
                # is no name anywhere in the file to misread.
                continue
            }
            # The command NAMES a script file but no leaf could be extracted
            # - a spelling the reader misread. REFUSED, not skipped.
            $problems += ("$rel dot-sources something this check cannot resolve to a file name, " +
                          "so it cannot be compared: " + $cmd.Extent.Text.Trim())
            continue
        }
        foreach ($hit in $found) { $sourced += $hit.Value }
    }
    $dupSources[$rel] = @($sourced | Sort-Object -Unique)
}

# Leaf -> the relative paths carrying it, so a dot-source naming x.ps1 resolves
# back to the file that was scanned.
$dupByLeaf = @{}
foreach ($rel in $dupDefs.Keys) {
    $leaf = @($rel -split [char]0x2F)[-1]
    if (-not $dupByLeaf.Contains($leaf)) { $dupByLeaf[$leaf] = @() }
    $dupByLeaf[$leaf] += $rel
}

# The per-file half.
foreach ($rel in ($dupDefs.Keys | Sort-Object)) {
    foreach ($key in ($dupDefs[$rel].Keys | Sort-Object)) {
        $defs = @($dupDefs[$rel][$key])
        if ($defs.Count -lt 2) { continue }
        if ($FunctionOverrides.Contains($rel + '::' + $defs[0].Name)) { continue }
        $problems += ("$rel defines $($defs[0].Name) $($defs.Count) times, at lines " +
                      (($defs | ForEach-Object { $_.Line }) -join ' and ') +
                      '; PowerShell keeps the last, so every call in the file reaches that one')
    }
}

# The cross-file half, over the TRANSITIVE closure and over every pair in it.
# Comparing only a file against its own libraries missed two shapes that shadow
# just as completely: two libraries co-sourced by one harness colliding with
# each other (theme-matrix.ps1 co-sources five, which is ten unchecked pairs),
# and a collision one hop further out (A sources B, B sources C, A and C
# collide).
foreach ($rel in ($dupSources.Keys | Sort-Object)) {
    $closure = New-Object System.Collections.Generic.List[string]
    $pending = New-Object System.Collections.Generic.Queue[string]
    $pending.Enqueue($rel)
    $seen = @{}
    while ($pending.Count -gt 0) {
        $cur = $pending.Dequeue()
        if ($seen.Contains($cur)) { continue }
        $seen[$cur] = $true
        if ($cur -ne $rel) { [void]$closure.Add($cur) }
        foreach ($leaf in @($dupSources[$cur])) {
            # A dot-source can legitimately name a file outside this tree --
            # layout-switch-filmstrip.ps1 and idle-badge-check.ps1 source
            # C:/temp/seam-lock.ps1 -- and
            # an unscanned leaf has no definitions to compare. Skipped rather
            # than refused: it is reachable and intentional, unlike a path this
            # check could not reduce to a name at all.
            if (-not $dupByLeaf.Contains($leaf)) { continue }
            foreach ($target in @($dupByLeaf[$leaf])) {
                if ($null -eq $target) { continue }
                if (-not $seen.Contains($target)) { $pending.Enqueue($target) }
            }
        }
    }
    if ($closure.Count -lt 1) { continue }

    # Every pair in the closure, the file itself included, compared once.
    $members = @(@($rel) + $closure)
    for ($i = 0; $i -lt $members.Count; $i++) {
        for ($j = $i + 1; $j -lt $members.Count; $j++) {
            $a = $members[$i]
            $b = $members[$j]
            foreach ($key in ($dupDefs[$a].Keys | Sort-Object)) {
                if (-not $dupDefs[$b].Contains($key)) { continue }
                $mine = @($dupDefs[$a][$key])[0]
                $theirs = @($dupDefs[$b][$key])[0]
                if ($FunctionOverrides.Contains($a + '::' + $b + '::' + $mine.Name)) { continue }
                if ($FunctionOverrides.Contains($b + '::' + $a + '::' + $mine.Name)) { continue }
                $problems += ("$a defines $($mine.Name) at line $($mine.Line) and shares a scope with " +
                              "$b through a dot-source, which defines it at line $($theirs.Line); " +
                              'whichever is read last wins for the whole scope')
            }
        }
    }
}

# An override that excuses without saying why is the exception nobody can
# review, which is the rule $NotInSuite already carries.
foreach ($overrideKey in $FunctionOverrides.Keys) {
    if (-not ([string]$FunctionOverrides[$overrideKey]).Trim()) {
        $problems += ("a `$FunctionOverrides entry has no reason, so the exception cannot be reviewed: " +
                      $overrideKey)
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
# -List is refused with them, and is the reason this block sits ahead of the
# -List block rather than after it. A -SelfTest run does not read the base
# manifest at all, so -SelfTest -List printed nineteen rows and 'layers: base
# only' about a manifest that had nothing to do with the run being asked for,
# and left 0. Refusing the pair is the only answer that is not a lie: the
# fixture set is not a manifest anyone plans a run around, and the base
# manifest is what plain -List already prints.
if ($SelfTest -and ($Tag -or $Only -or $Skip -or $List -or
                    $PSBoundParameters.ContainsKey('Retries') -or
                    $PSBoundParameters.ContainsKey('Seed') -or
                    $StopOnFindings)) {
    Write-Host '-SelfTest asserts against the whole fixture set at one retry and one seed; it takes no filters, -List, -Retries, -Seed or -StopOnFindings' -ForegroundColor Red
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
    # -List does not narrow, and says so rather than letting the rows imply it.
    # The pair is NOT refused the way -SelfTest -List is: the typo check above
    # runs on -List precisely so that the invocation needing no desktop can
    # answer whether a name is real, and refusing the combination would take
    # that back. But a flag accepted and then quietly ignored is this file's own
    # named failure mode read from the reporting side - the rows and the total
    # here describe a full run, and nothing else on screen says which of the two
    # questions was answered.
    if ($Tag -or $Only -or $Skip) {
        Write-Host '-Tag, -Only and -Skip do not narrow -List: the rows and the total above are the whole manifest. They are still read for a name that does not exist, which is what makes -List the cheap way to ask.'
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
    Clear-Desktop
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
        @{ name = 'st-stderr-flood'; verdict = 'pass'; attempts = 1; why = 'both pipes are drained, so a harness that fills the one nobody reads back still reaches its exit' }
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
    # that classified its own failure as retryable) and five passes, so
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

    # st-stderr-flood's verdict says the child got to its exit; this says where
    # the bytes went. The two fail together when the second pump is dropped
    # outright, and apart when it is pointed at the wrong stream or when the
    # handle is never closed - which is the shape a drain regression is likelier
    # to take than a deletion. A length, not the text: what is under test is
    # that more than a pipe buffer came through.
    $sfErrLog = Join-Path (Join-Path $OutRoot 'st-stderr-flood') 'console.err.log'
    $sfErrBytes = if (Test-Path -LiteralPath $sfErrLog) { (Get-Item -LiteralPath $sfErrLog).Length } else { 0 }
    if ($sfErrBytes -lt 65536) {
        $bad += "st-stderr-flood left $sfErrBytes byte(s) in console.err.log, and it wrote well past a pipe buffer; the drain on the stream nobody reads back is not doing it, so a real harness would block in its own write until the budget killed it"
    }

    # Integrity check 5's ceiling, at the boundary it exists for. No fixture can
    # stand here: a manifest would have to spell the number out, and a number
    # written down is one that goes stale the first time the multiplier moves -
    # which is the whole argument for deriving the ceiling rather than typing
    # it. So the arithmetic itself is the witness, run in this process against
    # the same value the check compares against. Floor for Ceiling survives
    # every other case in this file, and leaves a bound one too high: the check
    # then accepts a minutes whose product widens out of Int32, [math]::Max's
    # Int32 overload refuses the widened argument, and the throw takes the
    # foreach collecting the verdicts with it.
    $overCeiling = try { [void][math]::Max(180, ($MaxHarnessMinutes + 1) * 60 * 4); $false } catch { $true }
    if (-not $overCeiling) {
        $bad += "a minutes of $($MaxHarnessMinutes + 1) survives the run loop's budget arithmetic, so the ceiling check 5 refuses at is lower than the arithmetic needs and turns manifests that would have run into refusals"
    }
    $atCeiling = try { [void][math]::Max(180, $MaxHarnessMinutes * 60 * 4); $true } catch { $false }
    if (-not $atCeiling) {
        $bad += "a minutes of $MaxHarnessMinutes - the largest check 5 accepts - throws out of the run loop's budget arithmetic, so the ceiling lets through the manifest it exists to refuse and every verdict already collected goes with it"
    }

    # Everything above inspects objects in this process. The two lines that
    # actually end a real run - the report block and `exit $outcome.exit` -
    # are only reached by a run that does not stop to assert, so run one and
    # read its process exit code. The fixtures hold a findings, so 2.
    # Not $Args: that is an automatic variable, and shadowing it in a function
    # makes the splat below behave in ways that are not worth debugging.
    function Invoke-Inner {
        param([string]$Name, [string[]]$Extra)
        # The space is deliberate. This root is handed to the child as -OutRoot
        # and comes back out of it as each harness's -OutDir, so one space here
        # is carried through the launch path twice: once by the pwsh argument
        # parsing this run's own splat performs, and once by the runtime's
        # quoting of the ArgumentList the child builds for each harness. A path
        # that arrives as two arguments makes pwsh print its usage, and nothing
        # would notice until someone checked the repo out somewhere with a
        # space in the path.
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
    # The manifest's timeoutSeconds actually being READ, which nothing asserted.
    # Storing the coerced value survives any mutation and always will - the run
    # loop hands it to an [int] parameter, so a version that threw the coerced
    # value away behaves identically - but the field's USE does not: deleting
    # the branch that reads it drops st-hangs onto the [math]::Max(180, ...)
    # floor every other harness takes, and st-hangs is still 'harness' at two
    # attempts, so only the elapsed time changes. Asserted on the number, not
    # just on the line: the floor would print 180 here.
    if (-not $full.text.Contains('st-hangs exceeded its 2s budget')) {
        $bad += 'the run never said st-hangs exceeded its 2s budget, so the budget it was killed at did not come from the manifest; a run that fell back to the three-minute floor is still a harness failure at two attempts and looks the same from every other assertion here'
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
    #
    # Three spellings, because the refusal answers two questions and only one of
    # them was asked. -Only is the filter case. The other two are -List, which
    # is refused for a different reason and is the reason this whole block sits
    # AHEAD of the -List block: a -SelfTest run reads the fixture set and never
    # touches the base manifest, so -SelfTest -List printed nineteen rows and
    # 'layers: base only' about a manifest with nothing to do with the run, and
    # left 0. Moving the refusal back after -List is the mutation that has to
    # fail, and with -Only alone it did not: no case passed the two together,
    # and bare -SelfTest -List was not refused at all. Both are asserted on the
    # silence as well as the exit, because printing the manifest IS the defect
    # and a wrong exit code is only how it is noticed.
    $script:RefusalCases = 0
    if (-not $SelfTestRefusalChild) {
        function Invoke-Refused {
            param([Parameter(Mandatory)][string]$Case, [Parameter(Mandatory)][string[]]$Extra)
            $text = (& pwsh -NoProfile -File $PSCommandPath -SelfTest -SelfTestRefusalChild @Extra | Out-String)
            $script:RefusalCases++
            return @{ case = $Case; exit = $LASTEXITCODE; text = [string]$text }
        }
        $refusals = @(
            @{ run = (Invoke-Refused -Case 'only' -Extra @('-Only', 'st-pass'))
               why = 'every expectation above would have run against one fixture' }
            @{ run = (Invoke-Refused -Case 'list' -Extra @('-List'))
               why = 'a -SelfTest run does not read the base manifest, so listing it answers a question nobody asked' }
            @{ run = (Invoke-Refused -Case 'list-and-only' -Extra @('-List', '-Only', 'st-pass'))
               why = 'the refusal has to come before the -List block, or -List answers first and leaves 0' }
        )
        foreach ($r in $refusals) {
            if ($r.run.exit -ne 1 -or -not $r.run.text.Contains('it takes no filters')) {
                $bad += "-SelfTest/$($r.run.case) exited $($r.run.exit) without refusing: $($r.why)"
            }
            if ($r.run.text.Contains('harnesses, about')) {
                $bad += "-SelfTest/$($r.run.case) printed the base manifest before refusing: $($r.why)"
            }
            # The child's own answer that it was told what it is. A parent that
            # stopped passing the flag would be refused exactly like this one
            # and nothing else would differ, so the bound that keeps a broken
            # refusal from starting self-tests without end would be carried by
            # nobody.
            if (-not $r.run.text.Contains('selftest: refusal child')) {
                $bad += "-SelfTest/$($r.run.case): the refusal child was not told what it is, so a refusal that stopped refusing would start a self-test of its own, and that one another"
            }
        }
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
    # The second and third are what that check has to SPEAK about: one that
    # declares a parameter the manifest does not pass and neither of the two it
    # does, and one with no param block at all.
    $LayerStub = @'
#requires -Version 7
param([string]$ExePath, [Parameter(Mandatory)][string]$OutDir)
exit 0
'@
    $LayerStubPartialParams = @'
#requires -Version 7
param([string]$Unrelated)
exit 0
'@
    $LayerStubNoParams = @'
#requires -Version 7
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
    # there. -List is the whole run: the merge, all five integrity checks and
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

    # The silence is the other half of the -List filter notice below: a line
    # printed whether or not a filter was bound says nothing about which of the
    # two questions the listing answered, and would leave that notice passing
    # while carrying no information.
    Assert-Layer -Run (Invoke-Layer -Case 'valid' -Manifest 'valid.ps1') -Exit 0 `
        -Says @("layers: base ($baseCount) + pro (1)",
                "$($baseCount + 1) harnesses, about $($baseMinutes + 1) minutes for a full run") `
        -Silent @('do not narrow -List')

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

    # Every way the field goes wrong in one run, because integrity check 5
    # collects them. Most of them are classes the refusal used to misname: two
    # printed a number and called it not a number, NaN was called out of a range
    # it has no place on, a table was told to drop a @( ) it never wrote, and
    # the overflow one was not refused at all - it passed the merge, -List
    # printed a total for it, and the run died in the budget arithmetic with
    # every verdict already collected. The last two are refused at all only
    # because a coercion that answers a number for them is worse than one that
    # answers nothing: $true is a budget of 1 and a blank string a budget of 0,
    # and nothing downstream can tell either from a value someone chose. The
    # layer is named because the rule is not the tier's: it is the run loop's,
    # read off the merged set.
    Assert-Layer -Run (Invoke-Layer -Case 'minutes-bad' -Manifest 'minutes-bad.ps1') -Exit 1 `
        -Says @("pro harness 'st-tier' has a minutes that is not a number: soon",
                "pro harness 'st-tier-negative' has a minutes of -3",
                "pro harness 'st-tier-overflow' has a minutes of 9000000",
                "pro harness 'st-tier-huge' has a minutes that is outside Int32",
                "pro harness 'st-tier-list' has a minutes that is a list rather than a number",
                "pro harness 'st-tier-nan' has a minutes that is not a number: NaN",
                "pro harness 'st-tier-table' has a minutes that is written as @{ } rather than as a number",
                "pro harness 'st-tier-bool' has a minutes that is a true/false rather than a number",
                "pro harness 'st-tier-blank' has a minutes that is empty rather than a number") `
        -Silent @('has a minutes that is not a number: 2147483648',
                  'has a minutes that is not a number: 2',
                  "has a minutes that is outside Int32, which is the range the run loop's budget arithmetic works in: NaN")

    # The same field's twin, which had none of its rules. Every way it goes
    # wrong in one run, because check 5 collects them: text, which throws out of
    # the run loop and takes every verdict already collected with it, and the
    # five a number gets wrong. Zero and below are its own case rather than
    # minutes' because this field is the one that skips the floor - there is no
    # [math]::Max standing behind it.
    Assert-Layer -Run (Invoke-Layer -Case 'timeout-bad' -Manifest 'timeout-bad.ps1') -Exit 1 `
        -Says @("pro harness 'st-tier-text' has a timeoutSeconds that is not a number: soon",
                "pro harness 'st-tier-zero' has a timeoutSeconds of 0",
                "pro harness 'st-tier-negative' has a timeoutSeconds of -5",
                "pro harness 'st-tier-null' has a timeoutSeconds of 0",
                "pro harness 'st-tier-huge' has a timeoutSeconds that is outside Int32",
                "pro harness 'st-tier-list' has a timeoutSeconds that is a list rather than a number") `
        -Silent @('has a timeoutSeconds that is not a number: 2147483648',
                  'has a timeoutSeconds that is not a number: 30')

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
    #
    # The FIRST occurrence of each, not every occurrence. String.Replace is
    # global, and each of these literals appears more than once in this file:
    # the manifest entry that is meant, the expectation table further down, and
    # the plant list itself - which a global replace rewrites too, leaving a
    # from that equals its own to in the injected copy. The count a case claims
    # is the count it has to plant, or the comment above it is describing a case
    # that no longer exists. Each first occurrence is the manifest entry,
    # because both manifests are declared before anything that quotes them.
    #
    # The plant count is asserted rather than assumed, because that is exactly
    # what tells three plants from six, and the extras are harmless only while
    # the copy runs nothing but -List.
    function Edit-RunnerCopy {
        param([Parameter(Mandatory)][string]$Case, [Parameter(Mandatory)][hashtable[]]$Edits)
        $text = Get-Content -Raw -LiteralPath $PSCommandPath
        foreach ($edit in $Edits) {
            # Counted before and after, which is the only form of this
            # assertion the plant list cannot fool: the `to` string is written
            # here as well, so counting THAT after the plant always finds two.
            # What is under test is that the plant consumed exactly one of the
            # `from` occurrences - a global replace consumes all of them.
            $was = ([regex]::Matches($text, [regex]::Escape($edit.from))).Count
            $at = $text.IndexOf($edit.from, [System.StringComparison]::Ordinal)
            if ($at -lt 0) {
                $script:bad += "layer/${Case}: this file no longer spells $($edit.from), so the case plants nothing and proves nothing; re-point it at an entry that is really there"
                continue
            }
            $text = $text.Remove($at, $edit.from.Length).Insert($at, $edit.to)
            $now = ([regex]::Matches($text, [regex]::Escape($edit.from))).Count
            if ($now -ne $was - 1) {
                $script:bad += "layer/${Case}: planting $($edit.from) took $($was - $now) of its $was occurrences rather than one, so the case edits more of the copy than the manifest entry it names"
            }
        }
        return $text
    }

    $commaRunner = Edit-RunnerCopy -Case 'base-comma' -Edits @(
        @{ from = "name = 'search';";            to = "name = 'sea,rch';" }
        @{ from = "tags = @('smoke','search');"; to = "tags = @('smo,ke','search');" }
        @{ from = "name = 'st-pass';";           to = "name = 'st,pass';" })
    Assert-Layer -Run (Invoke-Layer -Case 'base-comma' `
                                    -Inject @{ 'layer-scripts/fuzz-suite.ps1' = $commaRunner }) -Exit 1 `
        -Says @("base harness name holds a comma: 'sea,rch'",
                "base harness 'sea,rch' declares a tag holding a comma: 'smo,ke'",
                "selftest fixture harness name holds a comma: 'st,pass'")

    # Integrity check 5 over the halves of the merged set no manifest can reach,
    # for exactly the reason base-comma exists: the whole claim made for moving
    # the numeric rules out of the tier block is that they apply to a base
    # harness and to a fixture as much as to a tier's, and nothing tested the
    # claim, because every number in both manifests is a good one. Planting
    # `if ($where -ne 'base') { continue }` at the top of that loop changed no
    # case and put the rules back where they were.
    #
    # A range and a coercion, one on each side. The base entry's minutes is out
    # of the budget arithmetic's reach, which is the failure that got furthest
    # of all: refused nowhere, printed in -List's total, and thrown in the run
    # loop with every verdict already collected. The fixture's is text, which
    # kills -List's own Measure-Object before any of it.
    #
    # Both halves say WHICH sentence fired, not that one did. The two messages a
    # number can get share their opening - "has a minutes of 9000000" is the
    # first half of the ceiling refusal and of the negative one alike - so the
    # clause that tells them apart is asserted and the other one is asserted
    # absent. The same on the other side: text must not be told it is off the
    # end of a range it has no place on, which is the misnaming this pair of
    # rules was split apart to end. The entry that stood here before named a
    # string this file has never printed, in any version, and could not fire.
    $numbersRunner = Edit-RunnerCopy -Case 'base-numbers' -Edits @(
        @{ from = "seed = `$true;  minutes = 2"; to = "seed = `$true;  minutes = 9000000" }
        @{ from = "minutes = 0; oracle = 'fixture' }"; to = "minutes = 'soon'; oracle = 'fixture' }" })
    Assert-Layer -Run (Invoke-Layer -Case 'base-numbers' `
                                    -Inject @{ 'layer-scripts/fuzz-suite.ps1' = $numbersRunner }) -Exit 1 `
        -Says @("base harness 'search' has a minutes of 9000000",
                'the run loop multiplies it by 60 * 4 for the runaway budget',
                "selftest fixture harness 'st-pass' has a minutes that is not a number: soon") `
        -Silent @('-List subtracts it from the total it prints for a full run',
                  "has a minutes that is outside Int32, which is the range the run loop's budget arithmetic works in: soon")

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

    # The pairs written correctly and then wrapped, which every case above reads
    # as a list of names - a refusal aimed at the half the author got right, and
    # the likeliest half to be wrapped, because `harnesses = @( ... )` sits two
    # lines up in every manifest. Refused either way; what is under test is
    # which of the two it is told.
    Assert-Layer -Run (Invoke-Layer -Case 'not-in-suite-wrapped-pairs' -Manifest 'not-in-suite-wrapped-pairs.ps1' `
                                    -Inject @{ 'layer-scripts/tier-runner.ps1' = $LayerStub }) -Exit 1 `
        -Says @('tier notInSuite holds the name = reason pairs inside a list; write them as one object') `
        -Silent @('not a list of names',
                  'tier-runner.ps1 is in this directory')

    # The other arm of that same test, which had no fixture: the case above
    # wraps a hashtable, and a hashtable answers the dictionary half. A manifest
    # writing [pscustomobject]@{ } inside the list reaches only the custom-object
    # half, so dropping it left every case above green and put the wrong-blame
    # back for the shape a manifest is as likely to write.
    Assert-Layer -Run (Invoke-Layer -Case 'not-in-suite-wrapped-pairs-object' `
                                    -Manifest 'not-in-suite-wrapped-pairs-object.ps1' `
                                    -Inject @{ 'layer-scripts/tier-runner.ps1' = $LayerStub }) -Exit 1 `
        -Says @('tier notInSuite holds the name = reason pairs inside a list; write them as one object') `
        -Silent @('not a list of names',
                  'tier-runner.ps1 is in this directory')

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
    # Integrity check 3 SPEAKING, which nothing asserted: every case that
    # touches it asserts its silence, so `foreach ($need in @())` at the top of
    # its loop left the whole set green. It is also the check this file leans on
    # hardest - `pwsh -File` drops an undeclared argument without a word, so a
    # renamed -ExePath is a whole suite testing the wrong build - and the one
    # the -PathType Leaf skip was added to.
    #
    # Both sources of a needed parameter are here. -ExePath is passed to every
    # harness; -OutDir and -Seed are passed because the manifest row said so, so
    # a check that read only the fixed one would still pass the first entry.
    Assert-Layer -Run (Invoke-Layer -Case 'param-missing' -Manifest 'param-missing.ps1' `
                                    -Inject @{ 'layer-scripts/tier-partial.ps1' = $LayerStubPartialParams
                                               'layer-scripts/tier-bare.ps1'    = $LayerStubNoParams }) -Exit 1 `
        -Says @('st-tier-partial is called with -ExePath but tier-partial.ps1 does not declare it',
                'st-tier-partial is called with -OutDir but tier-partial.ps1 does not declare it',
                'st-tier-partial is called with -Seed but tier-partial.ps1 does not declare it',
                'st-tier-bare is called with -ExePath but tier-bare.ps1 does not declare it') `
        -Silent @('st-tier-bare is called with -OutDir',
                  'st-tier-bare is called with -Seed',
                  'names a script that does not exist')

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

    # The same pair with a name that IS real, which is the case the refusal
    # above cannot speak for. -Only, -Tag and -Skip are read for a typo and then
    # ignored, and the rows and total are the whole manifest either way - so the
    # totals are asserted as the FULL ones next to the notice that says so.
    # Accepting a flag and silently doing nothing with it is the failure this
    # file refuses everywhere else; here the answer is to say it rather than to
    # refuse the pair, because -List is the one invocation that needs no
    # desktop and is therefore how anyone asks whether a name exists.
    Assert-Layer -Run (Invoke-Layer -Case 'list-filtered' -Manifest 'valid.ps1' `
                                    -Extra @('-List', '-Only', 'search')) -Exit 0 `
        -Says @('-Tag, -Only and -Skip do not narrow -List',
                "$($baseCount + 1) harnesses, about $($baseMinutes + 1) minutes for a full run",
                "layers: base ($baseCount) + pro (1)")

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

    # The same checkout, one step further on. Everything above stops at -List,
    # which is the READ side; the write side begins after it and had the same
    # defect from the other direction. Start-Process resolves both of its
    # redirect paths as patterns and has no literal form, Set-Content's path
    # bound positionally to -Path, and the retry's Remove-Item took one too - so
    # a bracketed root killed the launch of the first harness, and had it got
    # past that it would have lost summary.json after every verdict was in, and
    # silently kept nothing where the retry is meant to keep the failed attempt.
    #
    # -SelfTestInner is the ordinary report path over the fixtures, so this is a
    # real run: two harnesses launch, one of them retries, the verdicts roll up
    # and the summary is written. The root is spelled with a bracket of its own
    # as well as sitting under one, because -OutRoot is the other way a run
    # arrives at such a path and it is the way an operator arrives at it
    # deliberately.
    #
    # st-flaky is the retry: attempt one leaves 1 and attempt two passes, which
    # is the only case that reaches the keep-the-failed-attempt block. st-findings
    # makes the run exit 2, so the assertion is on a run that had something to
    # report rather than on an empty one.
    $bracketRun = Join-Path $bracketRoot 'run[2]'
    # An attempt directory left by an earlier run into the same root, which is
    # the only thing the removal in front of the retry's rename has to deal
    # with. Without one the rename lands on nothing, and a removal that removed
    # nothing is indistinguishable from one that removed the right thing - which
    # is how a wildcard path went unnoticed there. Left in place, the rename
    # fails, the retry reuses the live directory, and the evidence of the failed
    # attempt is overwritten by the attempt that passed.
    $staleAttempt = Join-Path $bracketRun 'st-flaky.attempt1'
    New-Item -ItemType Directory -Force -Path $staleAttempt | Out-Null
    Set-Content -LiteralPath (Join-Path $staleAttempt 'from-an-earlier-run.txt') -Value 'stale' -Encoding utf8
    Assert-Layer -Run (Invoke-Layer -Case 'bracket-checkout-run' -Root $bracketRoot `
                                    -Extra @('-SelfTestInner', '-Only', 'st-flaky,st-findings',
                                             '-OutRoot', $bracketRun)) -Exit 2 `
        -Says @('retry 1/1 (st-flaky could not run)',
                'st-flaky: pass (exit 0',
                'st-findings: findings (exit 2',
                'FINDINGS in 1 harness(es): st-findings') `
        -Silent @('did not resolve to a file',
                  "parameter name 'Encoding'")
    # No count here: Invoke-Layer took one for this case already, and the
    # filesystem assertions below are the same case read a second way. The
    # blocks further down that DO count are the ones with no Invoke-Layer
    # behind them.
    foreach ($want in @('summary.json',
                        'st-flaky.attempt1\console.log',
                        'st-flaky\console.log')) {
        if (-not (Test-Path -LiteralPath (Join-Path $bracketRun $want))) {
            $bad += "layer/bracket-checkout-run: the run under a bracketed root left no $want, so a path this run writes is still being read as a pattern"
        }
    }
    if (Test-Path -LiteralPath (Join-Path $staleAttempt 'from-an-earlier-run.txt')) {
        $bad += 'layer/bracket-checkout-run: the stale attempt directory survived, so the removal in front of the retry rename matched nothing; the rename then failed and the retry overwrote the failed attempt it is meant to keep'
    }

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

    # Integrity check 6, both halves, from lib/ because check 2 enumerates this
    # directory flat: a fixture dropped beside the runner would be refused as
    # unclassified before check 6 saw it, and the case would assert the wrong
    # refusal.
    #
    # The per-file half. Two definitions of one name in one file, which is the
    # shape #938 shipped -- contrast-oracle.ps1 grew a second Test-RectInside
    # and the first stopped existing for the whole file, calls above it
    # included. The line numbers are asserted, not just the name: they are the
    # only part of the message that sends someone to the right place, and a
    # message naming the file and function alone is a diagnosis that stops
    # short of the address.
    Assert-Layer -Run (Invoke-Layer -Case 'dup-function-in-one-file' -Inject @{
        'layer-scripts/lib/st-dupe-one.ps1' =
            (@('function Test-StDupe { 1 }', 'function Test-StDupe { 2 }') -join [Environment]::NewLine)
    }) -Exit 1 `
        -Says @('st-dupe-one.ps1 defines Test-StDupe 2 times, at lines 1 and 2')

    # The cross-file half, and the more valuable one: neither file repeats a
    # name, so the per-file rule cannot see this at all. Both fixtures are
    # libraries so that neither trips check 2, and the dot-source is written
    # the way every harness here writes it -- through Join-Path, so the leaf
    # has to be read out of the command rather than the path evaluated.
    Assert-Layer -Run (Invoke-Layer -Case 'dup-function-across-dot-source' -Inject @{
        'layer-scripts/lib/st-dupe-lib.ps1' = 'function Test-StShared { 1 }'
        'layer-scripts/lib/st-dupe-user.ps1' =
            (@('. (Join-Path $PSScriptRoot ''st-dupe-lib.ps1'')',
               'function Test-StShared { 2 }') -join [Environment]::NewLine)
    }) -Exit 1 `
        -Says @('defines Test-StShared at line 2 and shares a scope with',
                'which defines it at line 1')

    # And the polarity the two cases above cannot carry between them: a file
    # defining a name ANOTHER file defines, with no dot-source joining them, is
    # not an error -- they never share a scope, and refusing it would make the
    # check unusable across 36 harnesses that all want a Get-Rect.
    Assert-Layer -Run (Invoke-Layer -Case 'same-name-no-dot-source' -Inject @{
        'layer-scripts/lib/st-apart-a.ps1' = 'function Test-StApart { 1 }'
        'layer-scripts/lib/st-apart-b.ps1' = 'function Test-StApart { 2 }'
    }) -Exit 0 `
        -Silent @('Test-StApart')

    # The allow-list, which had no witness at all -- and "a check with no
    # fixture is a claim, not a guard" applies to the exception as much as to
    # the rule. Three cases, because the interesting property is not that it
    # excuses but WHAT it excuses.
    $overrideRunner = Edit-RunnerCopy -Case 'override-honoured' -Edits @(
        @{ from = "`$FunctionOverrides = [ordered]@{`n}"
           to   = "`$FunctionOverrides = [ordered]@{`n    'lib/st-ovr.ps1::Test-StOvr' = 'fixture'`n}" })
    Assert-Layer -Run (Invoke-Layer -Case 'override-honoured' -Inject @{
        'layer-scripts/fuzz-suite.ps1' = $overrideRunner
        'layer-scripts/lib/st-ovr.ps1' =
            (@('function Test-StOvr { 1 }', 'function Test-StOvr { 2 }') -join [Environment]::NewLine)
    }) -Exit 0 `
        -Silent @('Test-StOvr')

    # The two key shapes answer different questions and one must not silence
    # the other: an entry excusing an in-file repeat must NOT excuse that file
    # deliberately overriding a library's name, or an entry written for one
    # waves through the other -- which is the #938 shape arriving by the back
    # door. The per-file key is present here and the cross-file collision is
    # still refused.
    $narrowRunner = Edit-RunnerCopy -Case 'override-is-not-a-blanket' -Edits @(
        @{ from = "`$FunctionOverrides = [ordered]@{`n}"
           to   = "`$FunctionOverrides = [ordered]@{`n    'lib/st-narrow-user.ps1::Test-StNarrow' = 'fixture'`n}" })
    Assert-Layer -Run (Invoke-Layer -Case 'override-is-not-a-blanket' -Inject @{
        'layer-scripts/fuzz-suite.ps1' = $narrowRunner
        'layer-scripts/lib/st-narrow-lib.ps1' = 'function Test-StNarrow { 1 }'
        'layer-scripts/lib/st-narrow-user.ps1' =
            (@('. (Join-Path $PSScriptRoot ''st-narrow-lib.ps1'')',
               'function Test-StNarrow { 2 }') -join [Environment]::NewLine)
    }) -Exit 1 `
        -Says @('shares a scope with')

    # And a reason is not optional. $NotInSuite refuses an empty one; an
    # exception nobody can review is the one that outlives its reason.
    $mutelessRunner = Edit-RunnerCopy -Case 'override-without-a-reason' -Edits @(
        @{ from = "`$FunctionOverrides = [ordered]@{`n}"
           to   = "`$FunctionOverrides = [ordered]@{`n    'lib/st-mute.ps1::Test-StMute' = ''`n}" })
    Assert-Layer -Run (Invoke-Layer -Case 'override-without-a-reason' -Inject @{
        'layer-scripts/fuzz-suite.ps1' = $mutelessRunner
    }) -Exit 1 `
        -Says @('has no reason, so the exception cannot be reviewed')

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
    Write-Host ("SELFTEST OK  {0} exit paths classified correctly, {1} tier layer cases, {2} -SelfTest refusal case(s), and a real run over them exits 2" -f $expect.Count, $script:LayerCases, $script:RefusalCases) -ForegroundColor Green
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
# -LiteralPath, like every other path this file writes. Positionally the path
# binds to -Path, which is a pattern, and a run root holding a '[' then fails
# in a way that names neither: -Encoding is a DYNAMIC parameter contributed by
# whichever provider the path resolves to, so a path that resolves to none of
# them takes the parameter with it and the run dies on "A parameter cannot be
# found that matches parameter name 'Encoding'" - after every harness has run,
# with the verdicts in hand and nowhere to put them.
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutRoot 'summary.json') -Encoding utf8

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
