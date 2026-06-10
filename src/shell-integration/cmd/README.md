# cmd.exe shell integration

cmd.exe has no rc file or pre/post-exec hooks, so Ghostty's base
integration sets the `PROMPT` environment variable to emit:

- `OSC 133;A` / `OSC 133;B` — prompt start / input start
- `OSC 9;9` — current working directory

`PROMPT` cannot mark when a command *starts* or *finishes*. For those,
`ghostty.lua` adds the remaining marks **when the user runs
[Clink](https://chrisant996.github.io/clink/)**:

- `OSC 133;C` — a command was submitted and is about to run
- `OSC 133;D;<code>` — the command finished, with its exit code

Ghostty loads `ghostty.lua` automatically by prepending this directory to
`CLINK_PATH`; Clink autoloads `*.lua` from `CLINK_PATH` when it is injected
into cmd. If Clink is not installed, the variable is simply ignored and you
still get the `PROMPT`-based A/B/cwd marks.

The script deliberately does **not** emit `133;A`/`133;B` — `PROMPT`
already does, and doubling them would confuse the terminal. It therefore
complements, rather than replaces, the `PROMPT`-based integration.

Reading the previous command's exit code uses Clink's `cmd.get_errorlevel`
setting, which is enabled by default.
