# Differential corpus

Console-API workload programs used as **inputs** to the ConPTY differential
cell-identity oracle (`tools/conpty-oracle/`). Each program drives the Win32
Console API in a specific pattern (cell writes, cursor moves, scroll regions,
SGR, wide chars, code pages, …). The oracle runs a program under a transport,
feeds the resulting VT into the `ghostty-vt` terminal model, and dumps the
cell grid — so two runs (e.g. ConPTY vs a candidate transport, or ConPTY vs
itself) can be diffed cell-for-cell.

## Provenance

`programs/*.c` are lifted from `deblasis/wintty-pcon`'s conformance suite
(`tests/conformance/*.c`). That project is archived; its DLL-injection
delivery mechanism is a dead end, but this curated set of console-API
workloads — each built by discovering a real ConPTY divergence — is the
highest-value salvage. See the shipyard plan
`plans/2026-07-07-wintty-conpty-free-transport.md`.

## Important: assertions are ignored

Each program self-asserts via console-API read-back (`WriteConsoleW` then
`ReadConsoleOutputCharacterW`) and prints `PASS:`/`FAIL:`. **The oracle does
not care about that output** — it only compares the *cell grid* the program
produces. The PASS/FAIL text is just more deterministic content in the grid.
So a program that "fails" its own internal check is still a valid differential
input.

## Suitability

Not every program is a clean oracle input. Ones that read stdin
(`test_input*`, `test_mouse_input`, `test_ctrl_c`), probe tty-ness
(`test_isatty`, `test_getfiletype`), or depend on injection-only behavior may
hang or diverge for reasons unrelated to the transport. The oracle's
`selfcheck` mode (run the same program twice under ConPTY, assert identical)
is the triage: anything non-deterministic under ConPTY-vs-ConPTY is excluded
from the differential set before any candidate transport is compared.
