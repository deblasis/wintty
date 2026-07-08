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

**Real divergence (1) — bare LF processing:**

- `vt_newline` — words separated by a bare `\n` (no CR). conhost applies
  `ENABLE_PROCESSED_OUTPUT`, treating `\n` as a full newline (column 1 + line
  feed), so each word lands at column 1; raw VT to ghostty-vt treats `\n` as
  pure line feed (down, **same column**), producing a staircase. **This was
  the entire `vt_scroll_region` divergence too** — not scroll regions.
  **Transport consequence:** a raw-pipe transport must reproduce console
  LF→newline processing (translate `\n`→`\r\n`, or treat LF as newline) or
  restrict to programs that emit explicit CRLF. Small, well-defined, not a
  dealbreaker.

Two harness lessons baked in: (1) the ConPTY side must set UTF-8 codepage
(`SetConsoleOutputCP(65001)`, as production wintty does) or it mangles UTF-8,
while the raw pipe passes it through cleanly; (2) `dumpCells` applies a
**visual-identity normalization** — it resets blank cells whose only
attributes are invisible-on-blank (fg/bold/italic/faint/blink) while keeping
background, inverse, and line decorations (visible when blank) — which
neutralizes conhost's cosmetic "paint the trailing space with the active fg"
quirk (the sole difference on `vt_smoke` before normalization).
