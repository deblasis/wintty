# VT-compliance baseline: Wintty/ConPTY via WSL (esctest)

Empirical esctest run for the Windows port, driven through the real
`WSL -> ConPTY -> libghostty` path, plus a follow-up that pins down *why* most
tests fail. The headline, corrected by direct measurement:

**The ConPTY + WSL response channel is fast and lossless. It is not the limiter.**
The timeouts come from VT query types libghostty does not answer -- dominated by
**DECRQCRA** (rectangular-area checksum), which is esctest's own screen-readback
primitive. ~2/3 of the timeout bucket is tests that ran their operation correctly
and then could not verify it because that one query goes unanswered.

> An earlier draft of this baseline read the 307 timeouts as a "ConPTY + WSL
> transport ceiling" (latency/buffering through the double PTY). The latency probe
> below disproves that: query replies return in ~1 ms with zero loss. The prior
> conclusion is corrected here.

## Run context

- Date: 2026-06-12
- Terminal under test: Wintty (this fork), DX12, hosting WSL over its bundled ConPTY.
- Backend: `wsl.exe -d Ubuntu-24.04 -- bash -l <script>` (esctest needs Unix
  `termios`/`tty`, so WSL is the only host; native cmd/pwsh cannot run it).
- esctest2 `664be3c` (github.com/ThomasDickey/esctest2), python 3.12.3.
- Invocation: `python3 esctest.py --expected-terminal=xterm --max-vt-level=5 --timeout=N`.
- Harness: `windows/scripts/esctest/run-esctest.ps1` (`-ReadTimeoutSec` sets N).

## Summary (read-timeout 1s, 568 tests)

| Bucket | Count | Meaning |
|---|---:|---|
| Pass | 141 | terminal behaved correctly (readback returned and matched) |
| Known-bug | 42 | esctest's own xterm known-bug exemptions |
| ConPTY-timeout | 307 | a query reply esctest needed never arrived |
| Mismatch-review | 77 | readback returned but differed from expected |
| Fail-other | 1 | a non-timeout, non-mismatch internal error |

The "ConPTY-timeout" label is retained for continuity but is a misnomer: the
measurements below show these are **unanswered-query** failures, not transport.

## What actually causes the timeouts (measured)

Three measurements, each disproving a transport explanation:

1. **Read-timeout sweep is flat.** Re-running at `--timeout` 1s / 3s / 5s, restricted
   to the identical test set, leaves Pass and timeout counts unchanged (105/105/105
   pass, 170/170/169 timeout on the common 369-test subset). If replies were merely
   *slow*, a wider window would convert timeouts to passes. None convert -> nothing is
   arriving late.

2. **The response channel is fast and lossless.** A direct round-trip probe
   (`latency-probe.py`) issues each query 20x and times the reply:

   | Query | Got | Lost | Median |
   |---|---:|---:|---:|
   | CPR `CSI 6n` | 20/20 | 0 | 0.8 ms |
   | DSR `CSI 5n` | 20/20 | 0 | 0.9 ms |
   | DA1 `CSI c` | 20/20 | 0 | 0.8 ms |
   | DA2 `CSI >c` | 20/20 | 0 | 0.9 ms |
   | XTWINOPS `CSI 18 t` / `14 t` | 20/20 | 0 | ~0.9 ms |
   | DECRQM `CSI ?25 $p` | 20/20 | 0 | 1.0 ms |
   | **DECRQCRA `CSI ...*y`** | **0/20** | **20** | -- never answers |

   Every query Ghostty *does* implement returns in about a millisecond, every time.
   The double PTY is not slow and does not drop replies.

3. **DECRQCRA is unanswered.** `CSI ...*y` (Request Checksum of Rectangular Area)
   gets no reply. libghostty recognizes it and deliberately drops it (the "ignoring
   unimplemented CSI" path in `src/terminal/stream.zig`).

### Why DECRQCRA dominates

esctest verifies screen contents with `AssertScreenCharsInRectEqual`
(`escutil.py`), which reads each cell back via `GetChecksumOfRect` -> `DECRQCRA`.
With no reply, every test that checks the screen blocks at its verification step --
*after* it has already performed the operation under test correctly. That is the
entire editing / erase / scroll / cursor-position population.

Splitting the 354 timeouts (a `--timeout 1` full run) by cause:

- **~239 (67%) DECRQCRA-driven:** `DL`, `ED`, `EL`, `SU`, `SD`, `ICH`, `DCH`, `IL`,
  `ECH`, `IND`, `RI`, `NEL`, `LF`, `FF`, `VT`, `CR`, `BS`, `CHA`, `CUP`, `VPA`, `VPR`,
  `DECCRA`, `DECFRA`, `DECERA`, `DECSERA`, `DECDC`, `DECIC`, `DECBI`, `DECFI`,
  `DECALN`, `DECSTBM`, `DECSTR`, `DECRC`, `DECSED`, `DECSEL`, `SCORC`, `REP`, `RIS`, ...
  -- all verify only via the checksum readback.
- **~115 (33%) own-query gaps:** specific unimplemented query variants, not the whole
  class -- `XtermWinops` (28; note `18t`/`14t` *do* answer, so these are other report
  variants), `DECRQM` modes (17; mode 25 *does* answer), `ChangeSpecialColor`/OSC 4/5
  (14), extended `DECDSR` (11), `ResetSpecialColor`/`ResetColor` OSC (7), mode
  set+verify (`DECSET`/`SM`/`RM` ~15), title-stack and C1-string classes (rest).

The `Mismatch-review` bucket (77) is unchanged from the original analysis: OSC 10/11/12
color-query interception (a documented ConPTY limit, ~21), DCS mangling
(`DECRQSS`, ~7), and xterm-specific expectations / cursor-state reads run with
`--expected-terminal=xterm` against a non-xterm terminal.

## Decision: DECRQCRA is not implemented to chase this score

The pass rate would climb substantially if libghostty answered DECRQCRA. We are not
doing that, for three reasons:

- **It is teaching to the test.** DECRQCRA is, in practice, a conformance-harness
  self-verification primitive (esctest, vttest). Real applications almost never use
  it. The correctness it would let esctest re-check -- delete/erase/scroll/insert -- is
  already covered directly by libghostty's own unit tests (`Terminal.zig` 374 tests,
  `Screen.zig` 187 tests; e.g. `deleteLines` x20, `eraseLine` x22, `eraseDisplay` x19).
- **It is a security surface.** A rectangular-checksum report lets an application read
  back arbitrary screen contents (an exfiltration vector). xterm gates it; libghostty
  currently drops it.
- **It diverges shared core.** `src/terminal/` rebases on upstream; upstream does not
  implement DECRQCRA either, so this would be permanent rebase friction for a
  test-only feature.

The genuine own-query gaps (a DECRQM mode libghostty supports but does not report; a
benign XTWINOPS *report* variant) are worth fixing only after per-gap review that they
are real-world-relevant and non-sensitive -- and never the window-manipulation
XTWINOPS variants. None are filed as Ghostty bugs from this run.

## Bottom line for #79

The Windows-specific risk for VT compliance was the transport, and it is clean
(~1 ms, lossless). VT-core correctness is platform-independent and unit-tested.
esctest-over-ConPTY adds little signal beyond those unit tests without implementing a
test-only, security-sensitive feature, so it is not the right conformance instrument
for this port. The valuable output of this slice is this corrected characterization
and the reusable probe that produced it.

## Residual timeout triage (#79 phase 1)

Classifying every non-DECRQCRA timeout class by direct evidence (the extended
`latency-probe.py` for query responses; esctest source for the verify mechanism). The
probe control queries (CPR/DSR/DA) answer in ~1 ms; the table records what each residual
query actually does.

| Class / query | Result | Bucket |
|---|---|---|
| DECRQM ANSI form (`CSI Ps $ p`) | silent (0/10) | **genuine gap -> fix** |
| DECXCPR (`CSI ? 6 n`) | silent (0/10) | **genuine gap -> fix** |
| DECRQM DEC form (`CSI ? Ps $ p`) | answers | already correct |
| XTWINOPS 14/16/18 t (text-area / cell size) | answers | already correct |
| XTWINOPS 11/13/15/19 t (window state / position / screen geometry) | silent (0/10) | deliberate omission (won't fix) |
| XTWINOPS manipulate (1/2/3/4/8/9/10/24/30) | n/a | security-sensitive (won't fix) |
| DECDSR `?15n`/`?25n`/`?26n` (printer / UDK / keyboard) | silent (0/10) | legacy, no consumer (defer) |
| DECDSR `?62n`/`?55n`/`?75n`/`?85n` | n/a | legacy/niche (defer) |
| DECDSR `?63n` (DECCKSR memory checksum) | n/a | test-only, exfil class (won't fix) |
| DECSET / SM / RM / DECSTR | n/a | DECRQCRA-blocked (verify via checksum: decset 23, sm 6, rm 2, decstr 7 calls; decrqm 0) |
| ChangeColor / ChangeSpecialColor / ResetColor (OSC 4/5) | n/a | ConPTY-intercepted, not Ghostty-fixable (OSC 10/11/12 finding) |

Two deliberate boundaries are worth stating, because they are stances rather than gaps:

- **XTWINOPS geometry (11/13/15/19) is intentionally omitted.** The handler implements
  the terminal's own area (text-area / cell size, needed for image protocols) but not the
  user's window position or display geometry, which is a fingerprinting surface. The
  explicit per-op list in `src/terminal/stream.zig` (with a "we only support window title"
  comment) shows the subset is curated, not unfinished. Not implemented.
- **DECRQCRA / DECCKSR (screen + memory checksum) is not implemented** (decided in #504):
  a screen-content exfiltration primitive whose only consumers are conformance harnesses.

**Phase 2 (separate work):** the two genuine, safe, real-world-relevant gaps -- DECRQM in
its ANSI form (apps querying IRM/LNM/KAM hang on no reply; the handler already intends to
support it) and DECXCPR (completes the CPR pair, exposes nothing CPR does not). The legacy
DECDSR status reports are deferred (no modern consumer); the geometry and checksum classes
are not implemented by design.

## Reproduce

```powershell
# Aggregate run (classified report under the output dir):
windows/scripts/esctest/run-esctest.ps1 -WinttyExe <Wintty.exe> -ReadTimeoutSec 1

# Direct query round-trip latency + loss (the measurement that found the cause):
windows/scripts/esctest/run-latency-probe.ps1 -WinttyExe <Wintty.exe>
```

(First esctest run clones esctest2 into the distro. The parser/classifier are
unit-tested in `windows/scripts/esctest/EsctestParse.Tests.ps1`.)
