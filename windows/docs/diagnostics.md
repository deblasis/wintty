# Windows diagnostics

Wintty captures evidence of failures locally and never sends anything
anywhere on its own. When something goes wrong, files appear in fixed
locations under the state directory, `%LOCALAPPDATA%\Wintty`, and a bug
report is built from those files. This page covers every artifact: what
it is, when it is written, what it contains, how sensitive it is, and
how long it stays.

The rolling application log has its own page, [logging.md](logging.md);
it appears in the tables below for completeness.

> Packaged (MSIX) builds: Windows virtualizes `%LOCALAPPDATA%` into the
> package's private app-data directory. The artifacts are the same; the
> literal path is not. The Settings app shows the real location.

## Where things live

| Artifact | Path |
|---|---|
| Unhandled exceptions, UI stalls | `%LOCALAPPDATA%\Wintty\crash.log` |
| Hang dumps | `%LOCALAPPDATA%\Wintty\hangs\hang-<pid>-<timestamp>.dmp` |
| Native (Zig) stderr capture | `%LOCALAPPDATA%\Wintty\native-stderr.log` |
| Rolling application log | `%LOCALAPPDATA%\Wintty\logs\ghostty-YYYYMMDD[-N].log` |
| Startup-fatal errors | `ghostty-crash.log` beside `Wintty.exe`, else `%LOCALAPPDATA%\Wintty\ghostty-crash.log` |

## crash.log

Append-only file. It receives an entry whenever an unhandled exception
reaches one of the handlers (UI thread, `AppDomain`, `TaskScheduler`),
and it receives `[UI-THREAD STALL]` entries from the hang watchdog
described below.

Entries carry a round-trip UTC timestamp (`2026-04-17T14:23:17.042Z`)
and a handler tag, followed by the exception detail.

Never pruned, never truncated: it only grows when something goes wrong.
Delete it at any time; the next failure recreates it.

Sensitivity: exception text and stack frames, usually file paths and
window titles. Redact before sharing.

## Hang dumps

A hang produces no exception: the unhandled-exception handlers never
run for a frozen UI thread. The hang watchdog closes that gap. A
heartbeat timer on the UI thread proves it is pumping messages; a
background thread samples the counter. When the UI thread has not
pumped for 20 seconds:

1. `crash.log` gains a `[UI-THREAD STALL]` entry with a UTC timestamp
2. a dump of the still-hung process is written to
   `%LOCALAPPDATA%\Wintty\hangs\hang-<pid>-<timestamp>.dmp`
3. a second entry records the dump size, or a capture failure

The watchdog then disarms: one dump per process, because a second dump
of the same stuck stacks helps nobody. The 20 second window is
deliberately long so a synchronous dialog pump or a slow layout pass is
not mistaken for a hang.

### Triage vs full

| Mode | Config | Dump contents |
|---|---|---|
| triage (default) | `hang-dump = "triage"` | thread stacks, handles, stack-referenced memory |
| full | `hang-dump = "full"` | all of the above plus all process memory |

`hang-dump` lives in the regular Ghostty config file. The default,
`triage`, keeps the useful evidence (what every thread is stuck on)
without a RAM image.

A full dump is a RAM image: every scrollback line, every token the
terminal echoed, every pasted and echoed text, including passwords. If
you run with `hang-dump = "full"`, treat the files in `hangs\` as
secrets.

Dumps are never pruned automatically. They stay in `hangs\` until you
delete them.

### Sensitivity rules

- Triage dump: thread stacks and handles only, far less sensitive, but
  still contains module paths and stack memory. Attaching one to a
  public issue is acceptable if you are comfortable with that.
- Full dump: never attach to a public issue. Send it to the maintainer
  over a private channel, or delete it and reproduce with
  `hang-dump = "triage"`.
- Binary dumps cannot be redacted by a prompt; only text artifacts can
  (see below).

## native-stderr.log

Everything the Zig core writes to stderr, captured to a file because a
GUI process has no usable stderr. Panic messages and their backtraces
land here verbatim; a native abort otherwise disappears with only an
exit code.

Capped at 8 MB. When a launch finds the file past the cap it is emptied
before the capture installs, so the interesting content (a panic at the
tail) survives.

Sensitivity: whatever the core printed, which can include echoed text
and tokens. Redact before sharing, or send it privately.

## logs\ghostty-YYYYMMDD[-N].log

The rolling application log: one file per UTC day, 16 MB per file with
`-1`, `-2` suffixes on rollover, 14 day retention, pruned at startup
and at UTC-day boundaries. Format, config keys, and the event id
taxonomy are on [logging.md](logging.md).

Sensitivity: structured log lines, but they can carry file paths and
other identifying strings. A redaction pass is cheap; do one.

## ghostty-crash.log

Startup-fatal errors: the launch paths that die before the UI exists.
Written beside `Wintty.exe`; when that directory is not writable
(machine-wide installs, protected portable copies) it falls back to
`%LOCALAPPDATA%\Wintty\ghostty-crash.log`. Entry shape, sensitivity,
and retention match crash.log.

## What to attach to a bug report

Version first: the Version dialog's Copy button, or `wintty +version`
on the command line, pasted verbatim.

Then, by symptom:

| Symptom | Attach |
|---|---|
| crash | `crash.log`; add `ghostty-crash.log` if the app died at startup |
| hang or freeze | `crash.log` (the `[UI-THREAD STALL]` entries) and the dump from `hangs\`, triage only |
| silent exit | redacted `native-stderr.log` |
| anything else | the newest two `logs\ghostty-*.log` files |

Text artifacts are safe to attach once redacted. Binary dumps follow
the sensitivity rules above.

## Before you share logs

The four text artifacts (`crash.log`, `native-stderr.log`,
`logs\ghostty-*.log`, `ghostty-crash.log`) can be scrubbed before they
are posted. The prompt below is copy-paste ready: give it to any LLM
agent together with the log text, and attach the redacted output it
returns.

Binary `.dmp` files cannot be redacted this way and must never be
posted publicly; they follow the sensitivity rules above.

```
You are redacting a log file before I post it in public.

Replace every secret or identifying value with a same-shaped
placeholder, and change nothing else. Redact:
- tokens, passwords, API keys, cookies, and anything that looks like a
  credential, including long random strings in URLs and headers
- usernames and account names
- hostnames and machine names
- file paths that identify me or my organization, keeping the path
  shape (C:\Users\alice\... becomes C:\Users\<user>\...)

Preserve exactly:
- line order, line structure, and the pipe-separated field layout
- timestamps, log levels, event ids, thread ids, error codes, and hex
  addresses
- the wording of error messages, exception types, and function names

Ambiguous values are redacted, not kept. Output the full redacted log
verbatim and nothing else: no commentary, no summary, no surrounding
markup.
```
