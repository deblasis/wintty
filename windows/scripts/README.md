# Windows GUI harnesses

These drive a real Wintty window with real input. They are part of the test
suite, not scratch scripts: a harness that catches a defect keeps the check
so the defect cannot come back silently.

They need an interactive desktop and they take the foreground while running,
so they cannot share a machine with someone using it.

## Running

CI does not run any of these, and realistically cannot: they need a
logged-in interactive desktop with a real input queue, which hosted runners
do not have. They are local gates, run by hand before merging a change in
the area they cover. (`.github/workflows/` currently runs no .NET tests at
all either, so `just test-win` is also a local gate.)

`fuzz-suite.ps1` is the entry point:

```
just fuzz-list                  # what it runs, no build and no desktop needed
just fuzz-selftest              # prove the runner classifies exit codes, seconds
just fuzz                       # everything, about 43 minutes budgeted
just fuzz "-Tag smoke"          # the fast, high-signal subset, 7 budgeted / 5 measured
just fuzz "-Only search,probe"
```

Individual harnesses still run standalone, which is what you want while
fixing something:

```
just search-fuzz "-Seed 99 -Iterations 40"
just splash-race
```

Results go under `windows/scripts/fuzz-out/`, which is git-ignored;
`search-fuzz` on its own writes to `windows/scripts/search-fuzz/`. A suite
run writes `summary.json` at the root and one directory per harness holding
that harness's own `result.json`, screenshots and `console.log`.

## Exit codes

Every harness in the suite manifest follows this. It is the whole basis for
telling a broken product from a broken harness, and for deciding what is
worth retrying:

| code | meaning |
|------|---------|
| 0 | clean |
| 2 | product findings - read the harness's `result.json` and `shots/` |
| 1 | the harness could not run; the product was never exercised, so do not file a bug. Retrying helps when the cause was transient (no window, foreground stolen, shell never came up); it will not help when the run was refused because a Wintty is open - close it first |

The suite retries 1 and never retries 2. Re-running a real defect until it
passes is how a regression gets buried, which is what `aot-fuzz.ps1` used to
do by retrying every non-zero exit and keeping only the last attempt.

Conflating the two is also how a broken harness gets mistaken for a broken
product, and getting it right needed more than fixing the tail of each
script. The harnesses signal a defect by throwing, and an unhandled throw
makes `pwsh -File` return 1 - so 56 `throw "PRODUCT_FAIL: ..."` sites across
16 harnesses were reporting real defects on the code that means "nothing was
measured", to a runner that then retried them. Each of those scripts now
opens with:

```powershell
trap {
    if ("$_" -like 'PRODUCT_FAIL*') { Write-Host "$_"; exit 2 }
    break
}
```

`exit` from a trap still unwinds the `finally` blocks, so the config restore
and the process sweep both still run - `lib/fuzz-selftest/product-throw.ps1`
asserts exactly that, and `just fuzz-selftest` fails if either half breaks.

The prefix is the whole contract, so it has to be right at the throw site.
`HARVEST_MISS` (a UIA element that was not found, a click the window refused)
and `FOREGROUND_MISS` deliberately stay 1: those are usually another app
stealing the foreground, and filing them as defects would put noise in front
of real findings.

## The suite cannot quietly pass

The most likely defect in a runner is the one that looks like success, and
this is not hypothetical. `vtabs-visual-qa.ps1` judged each step on whether
its body threw - and a sub-script that exits 2 does not throw, so a run that
found real defects printed all green. That runner was what the vertical-tabs
work leaned on.

`just fuzz-selftest` runs the suite's own runner against fixtures in
`lib/fuzz-selftest/` that exit 0, 1, 2 and 3 on purpose, plus one that
throws, one that throws `PRODUCT_FAIL` from inside a `try`/`finally`, one
that fails once and then works, one that hangs forever, and one that
classifies its own failure to establish a corpus as the retryable 1 rather
than a finding. It asserts the verdict *and* the attempt count for each. It
takes a minute and a half, needs no build or desktop, and is safe to run with
Wintty open.

Two details make it worth trusting rather than just running:

- It asserts against `Get-SuiteOutcome`, the same function the real run exits
  on, instead of re-deriving the rules. An earlier version checked a copy of
  that logic, so neutering the shipped roll-up left the self-test green.
- It then spawns a child run over the same fixtures through the ordinary
  report path and asserts the child's real process exit code, because
  everything else happens before the two lines that actually end a run.

The way to keep it honest is to break the runner on purpose and check that it
notices: make it retry findings, make it treat 2 as 0, invert `-Skip`, force
the final `exit` to 0. All of those are caught today.

## What a green run does and does not mean

The suite judges nothing itself; it reports what each harness reports, and
several harnesses check much less than their names suggest. `just fuzz-list`
prints what each one actually rules out. Three worth knowing before trusting
a green run:

- `tab-colors` reads no pixel at all. It confirms the swatches were findable
  and the layout switched; a build that painted every tab the same colour
  would pass.
- `mica-dpi` never changes the DPI - it reads it once - and checks
  `PerMonitorV2` by grepping the manifest in the source tree rather than the
  binary under test.

Those strings were wrong in the first version of this manifest, in the
direction that matters: they promised checks the code did not contain.

A harness is only worth committing if it can tell right from wrong on its
own. `search-fuzz.ps1` reads the terminal's own UIA text document, counts
matches itself, and compares that count against what the search bar reports,
which is what lets it type randomly drawn needles and still judge the
answer. Prefer that shape over "take a screenshot and have a human look at
it". It is a standard to move the directory towards, not one it currently
meets.

## Not in the suite

| script | why |
|---|---|
| `mouse-smoke-run.ps1` | the operator drives the checklist by hand |
| `gen-bell.ps1` | generates a test asset |
| `aot-fuzz.ps1`, `vtabs-visual-qa.ps1`, `release-smoke.ps1` | runners in their own right. `aot-fuzz` targets the NativeAOT publish, which the suite can also do with `-ExePath` |
| `tab-tag-ink.ps1` | one regression, not a sweep: it measures whether a colour-tagged tab's pin glyph is painted in the tag foreground (#883). Like `contrast-oracle.ps1` it needs the seam pipe to itself |

## Filming motion

`lib/window-capture.ps1` plus `lib/WindowCapture/` is the shared camera. Use
it for anything where the question is how a change LOOKS over time rather
than what it ends up as.

- `Assert-WindowCaptureReady` builds the tool on first use. It is deliberately
  not in `Ghostty.sln`: a product build should not pay for test tooling.
- `Start-WindowCapture` returns once the camera is rolling, so the thing being
  filmed can be fired straight afterwards without racing it.
- `Stop-WindowCapture` waits out the duration and hands back the frame index.
- `Measure-WindowCapture` reports the rate actually achieved. Run it when a
  film looks thin, before concluding anything from it.

Do not reach for `Graphics.CopyFromScreen`. Measured on the machine this was
written for it costs **about 175ms per grab regardless of region size** (the
same at 1280x820, 640x820, 1280x200 and 400x400, and unchanged by
`CAPTUREBLT`), which is under six frames a second. That cannot judge a 340ms
animation; `layout-switch-filmstrip.ps1` got three frames for a whole
transition that way, the first of them a third of a second in.

`WindowCapture.exe` uses Windows.Graphics.Capture instead: frames from the
compositor, delivered on a pool thread, **in a separate process**. The
separation is load-bearing rather than tidy - these harnesses film apps that
block their own UI thread for hundreds of milliseconds at a time, so an
in-process camera would be looking through the stall it is trying to observe.

Two things to know when reading its `SUMMARY` line:

- The frame rate is the window's own PRESENT rate, not a capture setting. An
  idle terminal reports 2-3 fps because it presents 2-3 times a second, and
  the same window under a layout switch reports 30. A low number is a finding
  about the app, not about the camera.
- `dropped` above zero means the encoder fell behind the compositor and the
  film has holes in it. It is reported rather than smoothed over, because a
  filmstrip with gaps that does not say so is worse than one that does.

`layout-motion-profile.ps1` reads a film after the fact: per consecutive
frame pair it prints the time, the gap to the previous frame (presentation
holes show here), the mean pixel delta, and the bounding box of what
changed, in window coordinates. The box is the column that finds
misdirected motion - it is how the impact nudge was caught translating the
entire window (change boxes of `(4,4)-(1264,808)` at four to six times the
switch's own amplitude) and how the retargeted version was verified: after
the fix, no post-switch box is wider than the struck strip's own band.
Point it at a `layout-switch-filmstrip.ps1` out dir and a leg tag.

## Scenery

`lib/backdrop-stage.ps1` plus `lib/BackdropStage/` is the shared backdrop: a
window a harness parks UNDER the one it is measuring and paints a named scene
on. Use it whenever the question depends on what is behind the chrome, which
for a translucent material is every question.

The scene goes to two places because the materials look at two things:
crystal and frosted show or blur the **window behind**, solid is Mica and
samples the **desktop wallpaper**. `Set-BackdropScene -Wallpaper` paints the
stage AND sets the same PNG as the wallpaper. A harness that passes it owns
putting the wallpaper back: `$before = Get-DesktopWallpaper` first and
`Set-DesktopWallpaper -Snapshot $before` in a `finally`, which restores the
user's own style and tiling and reads the registry back. `lib/env-guard.ps1`'s
snapshot covers the registry side for `just env-restore` after a crash, but a
registry restore does not repaint the desktop: after a crashed `-Wallpaper`
run, re-run `Set-DesktopWallpaper` (or log off) to see the old wallpaper
again. Only a single static image is captured; a slideshow, Spotlight or
per-monitor wallpaper comes back as its current image.

- `Start-BackdropStage -X -Y -W -H` builds the tool on first use (not in
  `Ghostty.sln`, like WindowCapture) and launches it at a device-pixel rect,
  TOPMOST and non-activating. Place the window under test TOPMOST afterwards
  and it lands above the stage.
- `Get-BackdropScenes` is the catalogue: black, white, grey, brand, sunrise,
  photo, editor, checker, each generated from its name, size and seed.
- `Get-ScreenPixel` reads one device pixel: read the stage's own margin before
  photographing, so the scene on screen is the one asked for, and ask
  `Get-WindowPidAt` first so the pixel is known to be the stage's.
- The stage closes itself when the shell that launched it dies, so a
  Ctrl+C cannot leave a window that has no taskbar entry and cannot be
  focused for Alt+F4.

`just backdrop-stage-selftest` proves the instrument against the screen. It
launches no Wintty and is safe with one open.

## The theme matrix

`theme-matrix.ps1` (#937) is the one harness here that SETS machine state:
it flips the desktop light/dark theme and the wallpaper while it runs, and
puts everything back in its `finally`: the wallpaper through the API that
applies one, the polarity through the broadcast, then the env guard's
restore and read-back. A restore that fails is the run's exit code (1),
whatever it measured. That is why it is not in the suite and why
`just theme-matrix` holds the desktop lane (`wintty-desktop`) for the whole
run, after building under `wintty-build`. `-NoFlip` keeps the read-only
policy every other harness has.

After a hard kill (a `taskkill`, the lane's own timeout) the `finally`
never ran, and the manual recovery is: end `BackdropStage.exe` if it is
still up, `just env-restore` for the registry, then toggle the desktop
light/dark setting once by hand and re-apply the wallpaper, because a
registry restore alone repaints neither. The snapshot the run took is also
kept as `env-before.json` in its output dir, since the well-known one is
overwritten by whatever harness runs next.

Every axis (theme, polarity, app, frame, layout, scene) takes one value, a
comma list, or `all`; `just theme-matrix-plan` prints what a filter selects
and what it would cost without touching anything. Themes go into the config
as absolute paths under a staged copy of the catalogue, because a bare name
that resolves to nothing falls back silently (#877); each process then
proves the theme reached the glass by comparing the terminal ground it
photographs with the theme file's own `background`, and a process whose
ground is not the theme's is reported unmeasured rather than scored.

The run leaves `result.json`, `shots/`, `scenes/` and `matrix.md`; the
markdown is what gets pasted into #937, one comment per run.

## Process policy

`lib/wintty-process.ps1` holds the rule:

- `Assert-NoWintty` - refuse to start while any Wintty is running, naming the
  pids. Call it once, before the first launch.
- `Get-WinttyLaunchStamp` / `Stop-WinttyStartedAfter` - clean up only what the
  run started, matched on start time and, where the caller knows it, image
  path. Anything that cannot be positively identified is skipped: an
  unreadable path or start time is a reason to leave a process alone, never a
  reason to kill it.

Most scripts here used to open with `Get-Process Wintty | Stop-Process -Force`,
which takes down builds from other worktrees and the window the developer is
working in. That is not a harness's call to make.

Take the stamp immediately before the launch it covers, not at script start.
`release-smoke.ps1` takes one per launch for that reason: a single stamp at
the top would be minutes stale behind a ReleaseFast build, and every Wintty
opened while it ran would look like the run's own.

Every script here that launches Wintty uses the helper except two:

| script | why |
|---|---|
| `splash-single-instance-race.ps1` | its gate is deliberately *narrower* than `Assert-NoWintty`: it refuses only over instances running from the exe under test, because the mutex it is measuring is keyed on that path, and it needs to be able to launch a second instance itself |
| `mouse-smoke-run.ps1` | the operator drives it by hand and quits the app themselves |
| `contrast-oracle.ps1`, `tab-tag-ink.ps1`, `switcher-preview-theme.ps1` | they are meant to be runnable beside a Wintty somebody else is using: they read crash.log not at all, launch with `windows-single-instance` off against an isolated `XDG_CONFIG_HOME`, move only their own window and reap only what they started. Each session now names its pipe after its own token, so two runs no longer collide on the name; what still makes them exit 1 rather than measure the wrong window is that each waits for the pipe belonging to the app it launched |

`vtabs-visual-qa.ps1` launches nothing directly - each sub-script gates and
reaps its own, including `layout-switch-filmstrip.ps1`, which it runs
first.

`justfile` also has its own copy of the gate, so `just fuzz`, `just
search-fuzz` and `just splash-race` can refuse before paying for a build.

One script needs care beyond a single gate. `mouse-fuzz-jumplist.ps1`
launches secondaries against a running primary, so its sweep rather than a
per-process kill is what reaps them.

Kill the tree, not the process. `Stop-Process -Id` leaves the ConPTY shell
running as an orphan, and an orphan does not trip `Assert-NoWintty` because
its image name is not Wintty - so it is invisible to the gate and simply
accumulates. Use `$proc.Kill($true)`.

Take the stamp immediately before the launch it covers. `fuzz-suite.ps1`
takes one per harness rather than one per run for that reason: a single
stamp at minute zero means every sweep for the next 40 minutes matches
anything from that exe, and the exe it defaults to is the one `just run-win`
opens.

Put the gate above the top-level `try`, or guard the sweep in the `finally`
on the stamp being set. With the gate inside the `try` and an unguarded
sweep below, a refusal binds `$null` to a mandatory `[datetime]`, and that
binding error *replaces* the refusal message and abandons the rest of the
`finally` - including the `XDG_CONFIG_HOME` restore. `search-fuzz.ps1` keeps
its gate inside the `try` on purpose, because its `catch` records the refusal
as a harness finding; its sweep is guarded for exactly this reason.

## Before you run search-fuzz

- It **refuses to start while any Wintty is running**, and names the pids so
  you can close them. That includes a Wintty you launched it from, so run it
  from another terminal. The reason is not single-instance - that mutex is
  keyed on a hash of the exe path, so another worktree's build would not
  absorb the launch. It is that `crash.log` is shared: it lives under
  `%LOCALAPPDATA%` per user rather than per exe path, `XDG_CONFIG_HOME` does
  not move it, and the harness reports everything the file gains during a
  run as a defect in the build under test.
- By default it uses **your real config and state directory**, because a
  throwaway `XDG_CONFIG_HOME` made the app crash at startup on the machine
  it was written on. `-IsolatedConfig` opts into the throwaway dir. The
  default means your session restore, pane layout and theme are in play, so
  a run that starts with several panes open is not a clean run.
- It moves the physical cursor, synthesizes global input, and resizes the
  window without restoring the original geometry.

### What its oracle does and does not judge

Only needles matching `^[\x21-\x7E]{2,}$` are checked against a match count.
Single characters, anything containing whitespace, and anything non-ASCII are
checked only for the counter's *shape* - that it is well formed and the app
did not fall over. Whitespace is excluded deliberately: the UIA document
keeps trailing spaces and the search haystack trims them, so their counts
legitimately differ.

## Arming the seam

`WINTTY_TEST_SEAM=1` no longer arms anything. The variable now carries a
per-session token - 32 hex characters - and the pipe is named after it
(`wintty-test-seam-<token>`), so the name is a secret rather than a well-known
address that anything on the box can find or take first. An unset, empty, `0`
or `1` value leaves the seam off.

Harnesses get this for free from `lib/seam-client.ps1`: `Start-SeamSession`
mints the token, sets the variable and connects. Driving an app by hand:

```powershell
. windows/scripts/lib/seam-client.ps1
$token = New-SeamToken
$env:WINTTY_TEST_SEAM = $token
# ... launch Wintty ...
$pipe = Connect-SeamPipe -Token $token
```

Two more things will stop a seam session that used to work:

- **The build.** The seam is compiled out of Release. A Debug build has it; a
  Release build needs `-p:TestSeam=true`, and is then a build nobody should
  install. Against a public build there is no pipe at all.
- **`send-text`.** It hands arbitrary bytes to a live shell, which is running
  commands as the user, so it has a second opt-in of its own and is off by
  default. Pass `-AllowInput` to `Start-SeamSession` (or set
  `WINTTY_TEST_SEAM_INPUT=1`) only in a harness that genuinely needs the shell
  to run something. The harnesses that arm it: `seam-cwd-tab-label.ps1` (the
  cwd round trip), `mouse-fuzz-inspector.ps1` (shell seeding so the inspector
  has surface state) and `mouse-fuzz-undo-osc.ps1` (the OSC title command).
  That list expanding is a policy change and belongs in the PR that does it.

## Driving input

**Prefer the seam.** It exists because synthesized input is not targeted:
`SendInput`, `keybd_event` and `mouse_event` go to whatever window is
foreground, which on a machine somebody is using is *their* window, not the
app under test. A harness that types has taken over the human's keyboard for
as long as it runs, and a `Ctrl`+wheel in one of these has already been
observed to zoom a bystander's terminal. The seam drives the real handlers
in-process with nothing focused and nothing synthesized, which is the whole
reason it can run beside a person. Reach for what follows only for the
handful of facts the seam genuinely cannot reach - "did the framework deliver
this key?" - and never on a machine in use.

Posted `WM_CHAR` / `WM_KEYDOWN` messages **do not reach Wintty** - measured at
zero characters landing across every inter-character delay. Use `SendInput`,
and note two prerequisites:

1. One real mouse click on the app's own pixels before the first keystroke.
   The WinUI content island does not take focus from the window merely being
   foreground, and without a focused element every keystroke is dropped.
2. A foreground guard on every send. A bare `SetForegroundWindow` loses to any
   app that repaints often; attach to the current foreground thread's input
   queue first, then set foreground, bring to top and set focus.
3. Nothing typed in the moment after XAML hands focus over. Closing the search
   bar returns focus to the terminal asynchronously, and the first character
   after it lands nowhere - `search-fuzz.ps1` typed `$a='ZQ'+'XW'; ...` after
   an Escape and the shell received it without the `$`. Wait for the handoff,
   spend a sacrificial first character, and read back what landed. The
   foreground guard does not cover this: the foreground was never lost.

Do not sniff the shell prompt to decide the shell is ready - a themed prompt
looks nothing like `PS >`. Ask it instead, and wait for the answer to appear
in the document; that also proves keystrokes are landing at all.

Be equally suspicious of a UIA check that can only pass. `Ensure-VerticalLayout`
in the tab-colours harness bailed out early because the NavigationView is in
the tree even when collapsed, so the script never switched layout and still
reported a clean parity check.
