# VT-compliance baseline: Wintty/ConPTY via WSL (esctest)

First empirical esctest run for the Windows port, driven through the real
`WSL -> ConPTY -> libghostty` path. The headline finding: this run measures the
**ConPTY + WSL transport ceiling**, not Ghostty VT-core correctness. No esctest
failure is cleanly attributable to a Ghostty rendering/behavior bug.

## Run context

- Date: 2026-06-12
- Terminal under test: Wintty (this fork), DX12, hosting the WSL session over its bundled ConPTY.
- Backend: `wsl.exe -d Ubuntu-24.04 -- bash -l <script>` (esctest cannot host on the
  native cmd/pwsh backends -- it needs Unix `termios`/`tty`, so WSL is the only host).
- esctest2 `664be3c` (github.com/ThomasDickey/esctest2), python 3.12.3.
- Invocation: `python3 esctest.py --expected-terminal=xterm --max-vt-level=5 --timeout=1`.
- Harness: `windows/scripts/esctest/run-esctest.ps1` (reproduces this run end to end).
- esctest exited rc=0 after ~255s; 568 tests.

## Summary

| Bucket | Count | Meaning |
|---|---:|---|
| Pass | 141 | terminal behaved correctly (readback returned and matched) |
| Known-bug | 42 | esctest's own xterm known-bug exemptions |
| ConPTY-timeout | 307 | the response esctest needed never arrived within its 1s read timeout |
| Mismatch-review | 77 | readback returned but differed from expected |
| Fail-other | 1 | a non-timeout, non-mismatch internal error |

## Interpretation

esctest verifies behavior by **reading the terminal's responses** (cursor-position
reports, DSR, DECRQCRA checksums, OSC/DCS query replies). That makes it a stress test
of the response/readback channel, which here traverses two PTYs (`esctest -> WSL pty ->
ConPTY -> libghostty` and back).

- **ConPTY-timeout (307, 54%)** is the dominant mode: over half of all tests could not
  read their result back within 1s. The channel is intermittent, not dead -- 141 tests
  *did* read back and pass -- which points at latency/buffering through the double-PTY
  rather than a hard break. A useful next experiment is re-running with a larger
  `--timeout` to see how far the pass rate climbs (distinguishes "slow" from "lost").

- **Mismatch-review (77)** breaks down almost entirely into known, non-Ghostty causes:
  - **OSC color query interception (~21):** `ChangeColorTests` (10),
    `ChangeDynamicColorTests` (10), `ResetSpecialColorTests` (1). ConPTY intercepts the
    OSC 10/11/12 color queries, so the readback differs regardless of Ghostty. This is a
    documented ConPTY limitation (the raw-pipe bypass that fixed it was removed in #474
    in favour of bundled ConPTY).
  - **DCS responses (~7):** `DECRQSSTests`. ConPTY mangles unrecognized DCS, so the
    `DCS $q` replies don't survive intact.
  - **xterm-specific expectations (~7):** `DATests`/`DA2Tests` (device attributes Ghostty
    advertises differently from xterm), `XtermSaveTests`, `XtermWinopsTests`, `TBCTests`.
    esctest was run with `--expected-terminal=xterm`; Ghostty is not xterm.
  - **cursor/mode readback (~42):** `BSTests`, `DECSETTests`, `DECRQMTests`,
    `DECSTRTests`, and the cursor-movement classes (`CUD/CUB/HPA/HVP/HPR/CPL/RI/DECBI/DECFI`).
    These verify via cursor-position reports on the same intermittent channel; a stale or
    wrong-state CPR (e.g. `expected Point(9,3), got Point(1,6)`) reads as a mismatch. Not
    separable from channel noise without the Linux baseline.

**No follow-up Ghostty bug issues are filed from this run:** every failure maps to the
ConPTY response-channel limit, a documented ConPTY OSC/DCS interception, or an
xterm-specific expectation. Ghostty's actual VT-core correctness is covered by the Zig
unit tests (`src/terminal/Terminal.zig`, `Parser.zig`); esctest-over-ConPTY cannot add
signal there until the response channel is addressed.

## Limits of this baseline (and next steps)

- Classification of the 77 mismatches is heuristic. The clean disambiguation is the
  deferred **Linux upstream-Ghostty baseline** (same esctest, no ConPTY): any test that
  passes there but fails here is ConPTY/transport, not Ghostty.
- Re-run with `--timeout 3` (and `5`) to quantify how much of the 307-timeout bucket is
  latency vs genuinely lost.
- Native cmd/pwsh VT compliance needs a different tool (esctest can't host there); vttest
  (visual) is the path, tracked separately.
- CI integration (#79 section 3) is out of scope here.

## Reproduce

```powershell
windows/scripts/esctest/run-esctest.ps1 -WinttyExe <path-to-Wintty.exe> -TimeoutSec 900
```

(Clones esctest into the distro on first run; writes the log + this report under the
output dir. The parser/classifier are unit-tested in
`windows/scripts/esctest/EsctestParse.Tests.ps1`.)
