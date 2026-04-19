# validate-transport fixtures

Config fixtures for the ConPTY-mode smoke test. Consumed by
`just validate-transport-smoke ROW` and the sibling assertion script
at `scripts/validate-transport-assert.ps1`.

Each fixture pins a `conpty-mode` value and a one-shot `command`
whose only job is to emit a known OSC 11 query (or to sanity-spawn,
for cmd) and then exit. `quit-after-last-window-closed = true` plus
the default `wait-after-command = false` brings the app down
automatically, and `confirm-close-surface = false` skips the exit
confirmation prompt.

The pwsh fixtures emit OSC 11 via `pwsh.exe -EncodedCommand <base64>`.
The base64 payload is UTF-16LE of:

```
[Console]::Out.Write([char]0x1B + ']11;?' + [char]0x1B + '\')
```

which writes `ESC ]11;? ESC \` (OSC 11 query with ST terminator) to
stdout and exits. Inlining via `-EncodedCommand` avoids three things
that broke earlier iterations: cwd-relative script paths (Ghostty's
working-directory resolution is surprising under launch-by-test),
PowerShell execution policy blocking `.ps1` loads, and cmd-metachar
wrapping in ghostty's spawn path (ghostty wraps commands containing
`&`, `|`, `(`, `)`, `%`, `!` with `cmd /c`, which mangles quoted
arguments). The base64 alphabet (`A-Za-z0-9+/=`) contains none of
those.

All `command =` values use the full `.exe` suffix (`pwsh.exe`,
`cmd.exe`). Ghostty's `internal_os.path.expand` does PATH lookup
with the literal name and does not try PATHEXT-style suffix hunting;
a bare `pwsh` would fail to resolve to `pwsh.exe` and CreateProcessW
would error with ERROR_FILE_NOT_FOUND.

Never edit these to chase a test failure. If an expected verdict
changes, update the assertion table in
`scripts/validate-transport-assert.ps1` in the same commit.
