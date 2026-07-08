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
