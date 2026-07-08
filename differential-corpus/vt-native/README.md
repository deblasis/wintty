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

**A conhost-free raw pipe is visually cell-identical to ConPTY for every
VT-native program tested.**

- `vt_wrap` → **CELL-IDENTICAL** byte-for-byte, incl. a 120→121 column wrap
  and a wide char straddling the last column.
- `vt_smoke` → **CELL-IDENTICAL** (SGR bold/italic/underline, 16/256/truecolor
  fg+bg, absolute + relative cursor moves, wrap, UTF-8 CJK wide chars, a
  combining sequence).
- `console_only` → **NO-OUTPUT** (Console-API only; nothing reaches a raw
  pipe — the boundary, as designed).

The *only* divergence ever seen was conhost painting an interior blank space
after a colored word with that word's foreground (`\x1b[38;5;1mred \x1b[0m`),
where raw VT resets first (`red\x1b[0m `). A space has no foreground glyph, so
it is invisible. `dumpCells` applies a **visual-identity normalization** —
reset blank cells whose only attributes are invisible-on-blank (fg / bold /
italic / faint / blink), while keeping background, inverse, and line
decorations (underline / strikethrough / overline), which *are* visible when
blank. Applied identically to both transports, it collapses `vt_smoke` to
byte-identical, proving fg-on-blank was the sole difference.

Harness lesson also baked in: the ConPTY side must set UTF-8 codepage
(`SetConsoleOutputCP(65001)`, as production wintty does) or it mangles UTF-8,
while the raw pipe passes it through cleanly.
