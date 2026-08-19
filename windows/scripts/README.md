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
just fuzz                       # everything, about 40 minutes budgeted
just fuzz "-Tag smoke"          # the fast, high-signal subset, 5 budgeted / 3 measured
just fuzz "-Only search,loop"
```

Individual harnesses still run standalone, which is what you want while
fixing something:

```
just search-fuzz "-Seed 99 -Iterations 40"
just splash-race
pwsh -NoProfile -File windows/scripts/mouse-fuzz-loop.ps1 -ExePath ... -OutDir ...
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
that fails once and then works, and one that hangs forever. It asserts the
verdict *and* the attempt count for each. It takes about a minute, needs no
build or desktop, and is safe to run with Wintty open.

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
- `loop` saves screenshots and reads none of them back, and skips any
  affordance it cannot find. Its verdict is liveness.
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
| `verified-input-probe.ps1` | leaves its window up for inspection by design, which would make the next harness refuse; and its `PASS_PENDING_SCREENSHOT` is not self-checked - its marker does not in fact appear, because it posts `WM_CHAR`, which this app never receives |
| `mouse-smoke-run.ps1` | the operator drives the checklist by hand |
| `vtabs-layout-switch-capture.ps1`, `vtabs-switcher-capture.ps1`, `vtabs-morph-filmstrip.ps1` | produce frames for a human to look at; no verdict to aggregate |
| `gen-bell.ps1` | generates a test asset |
| `aot-fuzz.ps1`, `vtabs-visual-qa.ps1`, `release-smoke.ps1` | runners in their own right. `aot-fuzz` targets the NativeAOT publish, which the suite can also do with `-ExePath` |

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

`vtabs-visual-qa.ps1` launches nothing directly - each sub-script gates and
reaps its own, including `vtabs-layout-switch-capture.ps1`, which it runs
first and which had neither until it was given both.

`justfile` also has its own copy of the gate, so `just fuzz`, `just
search-fuzz` and `just splash-race` can refuse before paying for a build.

Two scripts need care beyond a single gate. `mouse-fuzz-jumplist.ps1`
launches secondaries against a running primary, so its sweep rather than a
per-process kill is what reaps them. `verified-input-probe.ps1` deliberately
leaves its window up and therefore takes no stamp and runs no sweep; close it
by hand before the next harness.

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

## Driving input

Posted `WM_CHAR` / `WM_KEYDOWN` messages **do not reach Wintty** - measured at
zero characters landing across every inter-character delay. Use `SendInput`,
and note two prerequisites:

1. One real mouse click on the app's own pixels before the first keystroke.
   The WinUI content island does not take focus from the window merely being
   foreground, and without a focused element every keystroke is dropped.
2. A foreground guard on every send. A bare `SetForegroundWindow` loses to any
   app that repaints often; attach to the current foreground thread's input
   queue first, then set foreground, bring to top and set focus.

Do not sniff the shell prompt to decide the shell is ready - a themed prompt
looks nothing like `PS >`. Ask it instead, and wait for the answer to appear
in the document; that also proves keystrokes are landing at all.

Be equally suspicious of a UIA check that can only pass. `Ensure-VerticalLayout`
in the tab-colours harness bailed out early because the NavigationView is in
the tree even when collapsed, so the script never switched layout and still
reported a clean parity check.
