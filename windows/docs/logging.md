# Windows logging

Ghostty's Windows shell emits diagnostics through `Microsoft.Extensions.Logging`
with two sinks wired at startup.

## Sinks

### ETW (EventSource)

`Microsoft-Extensions-Logging` EventSource, via the built-in
`EventSourceLoggerProvider`. Capture with:

```
dotnet-trace collect -n Ghostty --providers Microsoft-Extensions-Logging
```

The same provider is visible in PerfView and Windows Performance Analyzer.

> Known issue (# 269): the provider is registered and the file sink
> captures events end-to-end, but `dotnet-trace` does not currently
> surface our events. If you hit empty output, the file sink below is
> the reliable channel until # 269 lands. PerfView is a useful
> independent check.

### Rolling file

`%LOCALAPPDATA%\Ghostty\logs\ghostty-YYYYMMDD.log`. One file per UTC day.
Each file caps at 16 MB; when full, the writer rolls to
`ghostty-YYYYMMDD-1.log`, `-2.log`, and so on. Files older than 14 days
are deleted at startup and again whenever the writer crosses a UTC-day
boundary, so a long-running session prunes itself.

Line format is pipe-separated, easy to grep:

```
2026-04-17T14:23:17.042Z | Warn  | 2100 | Ghostty.Clipboard.WinUiClipboardBackend | clipboard read failed: 0x8001010E
```

When a log call includes an exception, the record line is followed by
indented frames (up to 10) so the stack is readable in the same file.

The writer is backed by a bounded channel with drop-oldest semantics, so
a logging storm never blocks the UI or termio threads. On overflow a
synthetic "N log record(s) dropped" warning is flushed at the top of the
next batch, so drops are visible to operators.

## Config keys

Both keys live in the regular Ghostty config file.

| Key | Type | Default | Values |
|---|---|---|---|
| `log-level` | enum | `info` | `trace`, `debug`, `info`, `warn`, `error`, `off` |
| `log-filter` | list of `CATEGORY=LEVEL` pairs | empty | see below |

`log-filter` lets you override the default level per component:

```
log-filter = Ghostty.Services.ThemePreviewService=trace, Ghostty.Core.Config=warn
```

Longest matching category prefix wins, mirroring the
`Microsoft.Extensions.Logging` filter semantics. Unknown levels in a
filter pair are silently skipped rather than failing the config load.

Both keys are re-read whenever the config file changes on disk; the
filter rules rebuild in place via a volatile reference swap, no restart
needed.

## Event id taxonomy

EventIds are assigned from disjoint per-component ranges so they stay
stable across renames. You can assert on an id by itself without looking
at the message text.

| Range | Component |
|---|---|
| 1000-1099 | Config (ConfigService, ConfigWriteScheduler, SystemSchedulerTimer) |
| 1100-1199 | FrecencyStore (command palette history) |
| 2000-2099 | Startup (AUMID, jump list) |
| 2100-2199 | Clipboard (backend + bridge + confirm dialog) |
| 2200-2299 | ThemePreviewService |
| 2300-2399 | WindowState + WindowStateMigration |
| 2400-2499 | Shell (taskbar host, backdrop) |
| 2500-2599 | MainWindow |
| 2600-2699 | Settings UI |

The constants live in `windows/Ghostty.Core/Logging/LogEvents.cs` (Core
types) and `windows/Ghostty/Logging/LogEvents.cs` (WinUI shell types).
Each id appears in exactly two places: the constant definition and one
`[LoggerMessage(EventId = ...)]` attribute.

## Relationship to instrumentation (#54-#59)

Logging, as described here, is the coarse-grained "what happened" channel
for diagnostics, errors, and user-reportable events.

The `[Conditional("GHOSTTY_INSTRUMENT")]` trace primitives tracked in
issues #54-#59 are a separate channel for nanosecond-precision
performance data. They emit Chrome Tracing JSON, not ETW or file, and
are compiled out entirely when the symbol is undefined. The two
channels share nothing at runtime.

When you need to know *what went wrong*, use logging. When you need to
know *when it happened, to the microsecond*, use the instrumentation
channel (once it lands).
