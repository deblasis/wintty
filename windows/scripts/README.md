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

Two have `just` recipes so far:

```
just search-fuzz                          # scrollback search
just search-fuzz "-Seed 99 -Iterations 40"
just splash-race
```

The rest of the scripts in this directory are older and are invoked
directly with `pwsh -File`.

`search-fuzz` writes its JSON results, screenshots and terminal dumps under
`windows/scripts/search-fuzz/`, which is git-ignored. Older scripts vary:
`splash-single-instance-race.ps1`, for instance, uses the system temp
directory and takes no `-OutDir`.

## Exit codes

`search-fuzz.ps1` distinguishes the two ways a run can fail. This is the
convention for new harnesses rather than a description of the existing
ones - `splash-race` exits 1 for both kinds, which is the thing to move
away from:

| code | meaning |
|------|---------|
| 0 | clean |
| 2 | product findings - read `run-<seed>.json` and `shots/` |
| 1 | the harness could not run (no window, foreground stolen, shell never came up); the product was never exercised, so retry rather than file a bug |

The numbering follows `verified-input-probe.ps1`, `mouse-fuzz-loop.ps1` and
`mouse-fuzz-probe.ps1`, which already use 2 for a product failure.

Conflating those two is how a broken harness gets mistaken for a broken
product, and how a real defect gets dismissed as flakiness.

## Before you run search-fuzz

- It **refuses to start while any Wintty is running**, and names the pids so
  you can close them. It will not kill an instance it did not start: builds
  from several worktrees are often open at once, and a running instance
  would absorb the launch anyway when single-instance is on. On the way out
  it kills only the processes that appeared during the run and came from the
  exe under test.
- By default it uses **your real config and state directory**, because a
  throwaway `XDG_CONFIG_HOME` made the app crash at startup on the machine
  it was written on. `-IsolatedConfig` opts into the throwaway dir. The
  default means your session restore, pane layout and theme are in play, so
  a run that starts with several panes open is not a clean run.
- It moves the physical cursor, synthesizes global input, and resizes the
  window without restoring the original geometry.

## What the oracle does and does not judge

Only needles matching `^[\x21-\x7E]{2,}$` are checked against a match count.
Single characters, anything containing whitespace, and anything non-ASCII are
checked only for the counter's *shape* - that it is well formed and the app
did not fall over. Whitespace is excluded deliberately: the UIA document
keeps trailing spaces and the search haystack trims them, so their counts
legitimately differ.

## Oracles

A harness is only worth committing if it can tell right from wrong on its
own. `search-fuzz.ps1` reads the terminal's own UIA text document, counts
matches itself, and compares that count against what the search bar reports,
which is what lets it type randomly drawn needles and still judge the answer.
Prefer that shape over "take a screenshot and have a human look at it". This
is a standard to move the directory towards, not one it currently meets:
`verified-input-probe.ps1` still returns `PASS_PENDING_SCREENSHOT` and
requires a human to confirm its marker, and its marker does not in fact
appear - it posts `WM_CHAR`, which this app never receives.

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
