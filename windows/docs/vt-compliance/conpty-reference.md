# ConPTY sequence handling on Windows (reference)

How the Windows console host (ConPTY) affects VT sequences flowing between a
shell and Wintty, what Wintty does about it, and what remains a ConPTY
limitation. This is the catalog that previously existed only as scattered inline
comments.

ConPTY (`CreatePseudoConsole`) is the **sole** Windows transport: the earlier
raw-pipe bypass was removed in #474. Everything below applies to every Windows
session.

For the empirical VT-query measurement (what is clean vs what is not), see
[`esctest-baseline.md`](esctest-baseline.md). The short version of that result is
restated under "What ConPTY does *not* break" below.

## How Wintty drives ConPTY

`WindowsPty` in [`src/pty.zig`](../../../src/pty.zig) owns the pseudo-console:

- Wintty writes keystrokes to the shell through a **named pipe**
  (`CreateNamedPipeW`, `FILE_FLAG_OVERLAPPED`); the shell's output comes back
  through an anonymous `CreatePipe` pipe read on a dedicated thread. The input
  side must be a named pipe because libxev's IOCP backend uses overlapped I/O for
  that write path, and anonymous pipes do not support overlapped operations
  (`pty.zig` `open`).
- `CreatePseudoConsole` is created over that pipe pair and owns the handles for
  the PTY's life; `ResizePseudoConsole` / `ClosePseudoConsole` manage it.
- The pseudo-console is created with `dwFlags = 0` (`pty.zig` `open`), so
  `PSEUDOCONSOLE_INHERIT_CURSOR` is never set. That flag is the documented trigger
  for `ClosePseudoConsole` deadlocks; passing `0` sidesteps it.

Because the child runs inside ConPTY's own job object, a few POSIX-shaped
operations are no-ops on Windows and have ConPTY-specific bridges instead
(process-group assignment, `xev.Process` waiting). Those are documented inline in
[`src/termio/Exec.zig`](../../../src/termio/Exec.zig).

## Bundled `conpty.dll` (the central workaround)

**Problem.** The in-box Windows conhost only dispatches the VT sequences it
recognizes and silently drops the rest. That strips the sequences Wintty most
needs to pass through untouched: **Kitty graphics (APC)** and **Sixel (DCS)**.

**Fix.** Wintty ships a newer OpenConsole as `conpty.dll` next to the executable
and loads it in preference to the OS host. The newer host forwards unknown VT
pass-through (Kitty APCs, Sixel DCS) instead of eating it.

Resolution lives in `pty.zig` `resolvePseudoConsoleApi`, run once per process via
`std.once`:

1. Look for `<exe-dir>\conpty.dll` (`adjacentConptyPathW`).
2. `LoadLibraryW` it and resolve `CreatePseudoConsole` / `ResizePseudoConsole` /
   `ClosePseudoConsole` via `GetProcAddress`.
3. On any failure (missing file, load error, missing export), fall back to the
   kernel32 (OS conhost) trio.

The fallback is a **degraded mode** and every fallback path logs at `warn`
("...using OS conhost (Kitty graphics and Sixel will not work)"), so the capability
loss is visible in diagnostics. Success logs `pty: using bundled conpty.dll` at
`info`.

| Capability | Bundled `conpty.dll` | OS conhost fallback |
|---|---|---|
| Kitty graphics (APC) | forwarded | stripped |
| Sixel (DCS) | forwarded | stripped |
| Standard CSI/SGR/OSC | works | works |

## UTF-8 and the console code page

**Problem.** ConPTY's conhost does **not** inherit the caller's console code
page. A shell that writes bytes assuming the process code page (notably
PowerShell and cmd) will mojibake non-ASCII output -- e.g. Nerd Font glyphs from
Oh-My-Posh / Starship rendering as `?`.

**Fix.** For the shells that observe the Windows console code page, Wintty injects
a one-time UTF-8 setup preamble at startup. The selection logic is in
[`src/os/windows_shell.zig`](../../../src/os/windows_shell.zig) (`Preamble`):

- **cmd.exe** -> `chcp 65001`.
- **PowerShell** (5.1 and pwsh 7+) -> `chcp 65001` *and*
  `[Console]::OutputEncoding` / `InputEncoding` set to UTF-8. The `chcp` is
  required even though pwsh's .NET encoding is UTF-8: without it the conhost
  interpreter stays on the system code page and glyphs still render as `?`.
- **VT-aware shells** (wsl, bash, nu, zsh, fish, ssh, ...) -> **no preamble**.
  They decode their own output regardless of the Windows console code page; a
  `chcp` would be ignored at best and misleading at worst.

Whether the preamble is actually emitted is gated by the `utf8-console` policy
(`auto` / `always` / `never`), resolved in `Exec.zig`. Two guards matter:

- On the legacy double-byte **CJK** ANSI code pages (932/936/949/950/1361),
  `auto` does *not* force UTF-8, because that would mojibake legacy `.bat`
  scripts whose text is stored in that code page (`isCjkAnsiCodePage`). Such
  users opt in with `utf8-console = always`.
- The preamble strings are compile-time constants; user input is never
  interpolated into them (shell-injection guard, noted in `windows_shell.zig`).

Background: #299 (OEM code page glyphs, cmd/pwsh only), #302 / #308 (UTF-8
preamble for pwsh).

## VT queries ConPTY intercepts

Some host-answered queries never reach Wintty because ConPTY answers or swallows
them itself:

- **OSC 10/11/12** (foreground / background / cursor color queries). libghostty
  *does* answer these queries -- the reply is generated in the termio stream
  handler ([`src/termio/stream_handler.zig`](../../../src/termio/stream_handler.zig);
  [`src/terminal/osc.zig`](../../../src/terminal/osc.zig) is only the parser) and
  is on by default (`osc-color-report-format`). So an unanswered color query on
  Windows is a ConPTY interception, not a Wintty gap. With the raw-pipe bypass
  removed (#474) there is currently no workaround.
- **DCS mid-stream corruption** *(documented ConPTY behavior, not independently
  re-confirmed here).* When a DCS payload is split across write packets, ConPTY
  can inject an SGR reset into the middle of the stream, corrupting Sixel,
  `DECRQSS`, `XTGETTCAP`, and similar DCS protocols. The bundled host reduces
  unknown-DCS *dropping*; it does not eliminate split-packet injection.

## Known ConPTY mangling -- catalog

"Status" is what Wintty does about it today. Items marked *general* are
documented ConPTY behavior we have not independently re-confirmed in this fork.

| ConPTY behavior | Status in Wintty | Workaround |
|---|---|---|
| Unknown DCS/APC dropped by in-box host (Kitty graphics, Sixel) | mitigated | bundled `conpty.dll` forwards them |
| SGR reset injected mid-DCS on split packets (general) | not mitigated | none today; rare in practice |
| OSC 10/11/12 color queries intercepted | not mitigated | none since the bypass was removed (#474) |
| conhost ignores caller console code page | mitigated | UTF-8 preamble (`windows_shell.zig`) |
| `ClosePseudoConsole` deadlock with `INHERIT_CURSOR` (general) | avoided | we create with `dwFlags = 0` |
| Buffer desync / differing reflow on resize | inherent | `ResizePseudoConsole`; ConPTY owns its reflow (general) |
| Curly underline / extended SGR stripped by in-box host | partly mitigated | bundled host forwards more; not exhaustively verified (general) |
| `ResizePseudoConsole` ignored if called near client attach | inherent | race window is small; not observed in practice (general) |

## What ConPTY does *not* break

The Windows-specific risk for VT compliance was the **transport**, and direct
measurement shows it is clean: query replies round-trip in ~1 ms with zero loss
through the ConPTY + WSL double PTY (see
[`esctest-baseline.md`](esctest-baseline.md)). The esctest "timeouts" are not
transport loss; they are VT queries libghostty deliberately does not answer
(dominated by **DECRQCRA**, a screen-readback / exfiltration primitive used only
by conformance harnesses).

VT-core correctness -- parsing, the grid state machine, editing / erase / scroll
-- is platform-independent and covered by libghostty's own unit tests
(`src/terminal/Terminal.zig`, `src/terminal/Screen.zig`,
`src/terminal/Parser.zig`). ConPTY sits in front of that core but does not change
it.

## Keeping this current

ConPTY evolves (Microsoft is working on an in-process host that removes the
dual-buffer problem). When the bundled OpenConsole is updated, or when a ConPTY
behavior here is confirmed fixed, update the catalog above and the bundled
`conpty.dll` together.

## References

- esctest VT-query measurement: [`esctest-baseline.md`](esctest-baseline.md)
- ConPTY introduction:
  <https://devblogs.microsoft.com/commandline/windows-command-line-introducing-the-windows-pseudo-console-conpty/>
- In-process ConPTY spec: `microsoft/terminal` under `doc/specs`
