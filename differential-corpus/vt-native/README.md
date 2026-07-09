# VT-native programs

Purpose-built inputs for the oracle's `compare-transports` mode (ConPTY vs
raw pipe). Unlike the pcon-derived `../programs/`, these:

- write **only** via `WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), …)` — the
  one path that works identically whether stdout is a ConPTY or a raw pipe;
- do **no** Console-API read-back (the `programs/` corpus asserts via
  `ReadConsoleOutputCharacter`, which fails over a raw pipe and pollutes the
  grid with PASS/FAIL text);
- print no PID / handle / time — fully deterministic;
- stay within a 120×30 grid and never query the console size.

So they emit a byte-identical VT stream under both transports, and the
oracle checks whether the resulting **cell grids** match — i.e. whether a
conhost-free path is cell-identical to ConPTY for VT-native output.

- `vt_smoke.c` — SGR (bold/italic/underline, 16/256/truecolor), absolute +
  relative cursor moves, wrap, UTF-8 CJK wide chars, a combining sequence.
- `vt_wrap.c` — column-boundary wrap (120 chars + 1) and a wide char
  straddling the last column.

Console-API programs (most of `../programs/`) produce **no** raw-pipe output;
`compare-transports` reports that as `NO-OUTPUT` (the VT-native boundary),
not a failure.

## Findings (2026-07-08, windows-latest)

Divergence-surface map — **a conhost-free raw pipe is cell-identical to ConPTY
across the VT feature surface, with exactly one characterized, addressable
divergence (bare-LF processing).**

**CELL-IDENTICAL (6):**

- `vt_smoke` — SGR bold/italic/underline, 16/256/truecolor fg+bg, absolute +
  relative cursor moves, wrap, UTF-8 CJK wide chars, a combining sequence.
- `vt_wrap` — 120→121 column wrap and a wide char straddling the last column.
- `vt_scroll_region` — DECSTBM scroll region with CR/LF + reverse-index
  scrolling (scroll regions **are** faithful).
- `vt_altscreen` — DECSET 1049 alt-screen save/restore round-trip.
- `vt_erase` — EL/ED, including an **erase-with-red-background** (a visible
  fill — matches).
- `vt_edit` — IL/DL/ICH/DCH/ECH line and char editing.

**NO-OUTPUT boundary (1):**

- `console_only` — Console-API only; nothing reaches a raw pipe (by design).

**Real divergence (1) — bare LF processing — FOUND AND FIXED:**

- `vt_newline` — words separated by a bare `\n` (no CR). conhost applies
  `ENABLE_PROCESSED_OUTPUT`, treating `\n` as a full newline (column 1 + line
  feed), so each word lands at column 1; raw VT to ghostty-vt treats `\n` as
  pure line feed (down, **same column**), producing a staircase. **This was
  the entire `vt_scroll_region` divergence too** — not scroll regions.
- **Fix, validated in CI:** with `CONPTY_ORACLE_RAW_LF_TO_CRLF=1`,
  `captureRawPipe` inserts a CR before any lone LF — reproducing the console
  newline processing a production raw-pipe transport would provide — and
  `vt_newline` becomes **CELL-IDENTICAL**. So a raw-pipe Tier-1 transport that
  reproduces LF→newline (translate `\n`→`\r\n`, or treat LF as newline) is
  cell-identical to ConPTY across the **entire** tested VT feature surface.
  Small, well-defined, not a dealbreaker.

Two harness lessons baked in: (1) the ConPTY side must set UTF-8 codepage
(`SetConsoleOutputCP(65001)`, as production wintty does) or it mangles UTF-8,
while the raw pipe passes it through cleanly; (2) `dumpCells` applies a
**visual-identity normalization** — it resets blank cells whose only
attributes are invisible-on-blank (fg/bold/italic/faint/blink) while keeping
background, inverse, and line decorations (visible when blank) — which
neutralizes conhost's cosmetic "paint the trailing space with the active fg"
quirk (the sole difference on `vt_smoke` before normalization).

## Fundamentals compliance sweep (2026-07-09) — COMPLETE

Tested the primitives everything in a terminal composes from. The
output-fidelity surface is essentially exhausted (remaining VT is input/query
sequences not observable in an output cell grid).

**CELL-IDENTICAL (17 + 2 with line-control processing = 19):**

- **Text/attrs:** SGR 16/256/truecolor + every flag, wrap, wide chars, combining
  (`vt_smoke`, `vt_wrap`).
- **Cursor:** CUU/CUD/CUF/CUB, CHA/VPA, CNL/CPL, edge clamping, **DECSC/DECRC**
  (`vt_cursor_ops`).
- **Autowrap:** DECAWM on/off and the **deferred / pending-wrap** at the last
  column — the classic minefield — incl. the captured pending-wrap flag
  (`vt_autowrap`).
- **Tabs:** HT 8-col stops, CHT/CBT, HTS/TBC (`vt_tabs`).
- **Scrolling:** scroll regions DECSTBM+RI (`vt_scroll_region`), explicit SU/SD
  within a region (`vt_scroll_su_sd`).
- **Screens:** alt-screen DECSET 1049 round-trip (`vt_altscreen`).
- **Erase/edit:** EL/ED + colored-bg fill (`vt_erase`), IL/DL/ICH/DCH/ECH
  (`vt_edit`), IRM insert mode (`vt_insert_mode`).
- **Modes/misc:** DECOM origin mode (`vt_origin`), REP repeat (`vt_rep`),
  **DECDWL/DECDHL/DECSWL** double width+height lines (`vt_dwdh`), **DECSCA
  protected fields + selective erase** DECSEL/DECSED (`vt_protect`), **DECSLRM
  left/right margins** (`vt_margins`), **RIS hard reset** (`vt_ris`).
- **Box-drawing:** DEC Special Graphics `lqkxmj`→`┌─┐│└┘` + SO/SI locking shifts
  (`vt_charset`; one glyph caveat below).
- **Line control:** LF/VT/FF (`vt_newline`, `vt_index`) — with console
  line-control processing (below).

**Divergence class — console line-control (LF/VT/FF) — FIXED + validated:**
conhost's `ENABLE_PROCESSED_OUTPUT` treats LF (0x0A), VT (0x0B) and FF (0x0C)
all as a newline (col 1 + down); raw VT treats them as index. `captureRawPipe`
with `CONPTY_ORACLE_RAW_LF_TO_CRLF=1` prepends CR before all three → both
CELL-IDENTICAL (CI-gated). A raw-pipe transport reproduces this one rule.

**Open items (all characterized):**

1. **Scrollback on full-screen SU** — ghostty pushes SU-scrolled lines to
   scrollback; ConPTY doesn't expose conhost's. The *visible grid is identical*
   (SU/SD within a region is byte-identical); the `.screen` (scrollback-
   inclusive) dump differs by exactly those lines. Structural: ConPTY never
   conveys conhost's scrollback — the terminal builds its own from the stream.
2. **DEC diamond glyph** — Special Graphics `` ` `` → `♦` (U+2666) conhost vs
   `◆` (U+25C6) ghostty. Cosmetic per-glyph Unicode mapping; box-drawing itself
   identical.
3. **DECSTR soft reset (`CSI ! p`) unimplemented in ghostty-vt** — masked under
   ConPTY (conhost does it), but a raw-pipe transport needs ghostty-vt to
   implement it. **The oracle doubles as a finder of ghostty-vt VT-coverage
   gaps a raw pipe must fill** — an enumerable to-do list, not a blocker.

**Net:** across the fundamental building blocks, a conhost-free raw pipe is
cell-identical to ConPTY given (a) UTF-8 CP and (b) console LF/VT/FF
processing — two small, validated rules — with one cosmetic glyph note, one
scrollback-semantics note, and a short list of ghostty-vt VT gaps to fill
(DECSTR first). The fidelity question is answered; the next frontier is the
non-fidelity transport realities (resize, signals, teardown) the oracle can't
measure.
