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

## Fundamentals compliance sweep (2026-07-09)

Tested the primitives everything in a terminal composes from.
**CELL-IDENTICAL (11):**

- `vt_smoke`, `vt_wrap` — SGR (16/256/truecolor, all flags), wrap, wide chars.
- `vt_cursor_ops` — CUU/CUD/CUF/CUB, CHA/VPA, CNL/CPL, edge clamping, **DECSC/DECRC**.
- `vt_autowrap` — DECAWM on/off and the **deferred / pending-wrap** at the last
  column (the classic minefield) incl. the captured pending-wrap flag.
- `vt_tabs` — HT default 8-col stops, CHT/CBT, HTS/TBC.
- `vt_scroll_region` (DECSTBM+RI), `vt_altscreen` (DECSET 1049), `vt_erase`
  (EL/ED + colored-bg fill), `vt_edit` (IL/DL/ICH/DCH/ECH).
- `vt_charset` box-drawing — DEC Special Graphics `lqkxmj` → `┌─┐│└┘`, and
  SO/SI locking shifts — **identical** (see the one glyph caveat below).
- `vt_newline`, `vt_index` — **with console line-control processing** (below).

**Divergence class — console line-control (LF/VT/FF) — FOUND AND FIXED:**
conhost's `ENABLE_PROCESSED_OUTPUT` treats LF (0x0A), **VT (0x0B) and FF
(0x0C)** all as a newline (col 1 + down); raw VT treats them as index (down,
same column) → staircase. `captureRawPipe` with `CONPTY_ORACLE_RAW_LF_TO_CRLF=1`
prepends a CR before all three, and both `vt_newline` and `vt_index` become
CELL-IDENTICAL (CI-validated). A raw-pipe transport must reproduce this one
output-processing rule; it closes the whole class.

**Two remaining divergences (characterized):**

1. **DEC diamond glyph** (`vt_charset`): DEC Special Graphics `` ` `` maps to
   `♦` (U+2666) under conhost vs `◆` (U+25C6) under ghostty-vt — a per-glyph
   Unicode-mapping difference. Box-drawing and every other special glyph
   match; cosmetic.
2. **DECSTR soft reset** (`vt_softreset`): **ghostty-vt does not implement
   `CSI ! p`** — it logs `ignoring unimplemented CSI p with intermediates: !`,
   so region/origin/attrs/charset are not reset. This is masked under ConPTY
   (conhost performs the reset, then re-serializes the result), but a
   **raw-pipe transport needs ghostty-vt to implement DECSTR**. The oracle
   is thus also a finder of ghostty-vt VT-coverage gaps a raw pipe must fill.

Net: across the fundamental building blocks, a raw-pipe transport is
cell-identical to ConPTY given (a) UTF-8 CP, (b) console LF/VT/FF processing,
with two known items — a cosmetic glyph mapping and a concrete ghostty-vt gap
(DECSTR).
