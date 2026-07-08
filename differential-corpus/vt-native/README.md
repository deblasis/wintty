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

First candidate-transport result — **a conhost-free raw pipe is cell-identical
to ConPTY for VT-native output**:

- `vt_wrap` → **CELL-IDENTICAL** byte-for-byte, including a 120→121 column
  wrap and a wide char straddling the last column.
- `console_only` → **NO-OUTPUT** (Console-API only; nothing reaches a raw
  pipe — the boundary, as designed).
- `vt_smoke` → differs by exactly **one, visually-invisible** thing: conhost
  paints an interior blank space after a colored word with that word's
  foreground (`\x1b[38;5;1mred \x1b[0m`), where raw VT resets first and leaves
  the space default (`red\x1b[0m `). A space has no foreground glyph, so this
  is a cosmetic cell-attribute difference, not a rendering divergence.

Two harness lessons baked in above: (1) the ConPTY side must set UTF-8
codepage (`SetConsoleOutputCP(65001)`, as production wintty does) or it
mangles UTF-8 while the raw pipe passes it through cleanly; (2) strict
byte-identity of the cell dump is slightly *stricter* than visual identity —
it flags conhost's blank-cell fg painting. A future "visual identity" dump
that ignores fg on undecorated blank cells would make `vt_smoke` pass too and
is the natural next refinement.
