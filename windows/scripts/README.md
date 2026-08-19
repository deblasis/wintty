# Windows GUI harnesses

These drive a real Wintty window with real input. They are part of the test
suite, not scratch scripts: a harness that catches a defect keeps the check
so the defect cannot come back silently.

They need an interactive desktop and they take the foreground while running,
so they cannot share a machine with someone using it.

## Running

Recipes live in the `justfile`:

```
just search-fuzz                          # scrollback search
just search-fuzz "-Seed 99 -Iterations 40"
just splash-race
```

Outputs (JSON results, screenshots, terminal dumps) land under
`windows/scripts/<harness>/`, which is git-ignored.

## Exit codes

`search-fuzz.ps1` distinguishes the two ways a run can fail, and new
harnesses should follow it:

| code | meaning |
|------|---------|
| 0 | clean |
| 1 | product findings - read `run-<seed>.json` and `shots/` |
| 2 | the harness could not run (no window, foreground stolen, shell never came up); the product was never exercised, so retry rather than file a bug |

Conflating those two is how a broken harness gets mistaken for a broken
product, and how a real defect gets dismissed as flakiness.

## Oracles

A harness is only worth committing if it can tell right from wrong on its
own. `search-fuzz.ps1` reads the terminal's own UIA text document, counts
matches itself, and compares that count against what the search bar reports,
which is what lets it type randomly drawn needles and still judge the answer.
Prefer that shape over "take a screenshot and have a human look at it": a
harness whose verdict is `PASS_PENDING_SCREENSHOT` does not actually verify
anything, and at least one script here shipped for months with a marker that
never appeared on screen.

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
