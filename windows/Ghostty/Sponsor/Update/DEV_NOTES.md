# Update UI - developer notes (D.1)

Everything here is dev-only and only active under `SPONSOR_BUILD` + `DEBUG`.

## Running

```
$env:SPONSOR_BUILD='true'
just run-win
```

## Command palette (Ctrl+Shift+P)

Drive the simulator from the palette. Entries live under the "Custom" category:

| Palette title | State |
|---|---|
| Simulate: Idle | Idle |
| Simulate: No Updates | NoUpdatesFound |
| Simulate: Update Available | UpdateAvailable (v1.4.2) |
| Simulate: Downloading 42% | Downloading (42%) |
| Simulate: Extracting | Extracting |
| Simulate: Installing | Installing |
| Simulate: Restart Pending | RestartPending |
| Simulate: Error | Error |

The palette is built inside `MainWindow`'s constructor. Simulator availability
is ensured by lazily materializing `App.SharedSimulator` on first access, so
the palette registration and the bootstrapper's later `Wire()` call both see
the same instance.

## What each state should show

- **Pill** in the title bar: see PillDisplayModel.MapFromState for the full mapping.
- **Taskbar overlay**: appears for UpdateAvailable / RestartPending / Error only.
- **Toast**: fires once when entering UpdateAvailable or RestartPending *and* the window is unfocused *and* Focus Assist is not on.
- **Jump list**: "Install Pending Update" under "Updates" for UpdateAvailable / RestartPending.
- **Exit dialog**: appears only when closing the window in RestartPending.

## What is NOT working in D.1

- Real Velopack integration - simulator only.
- OAuth / sponsor auth - deferred to D.2.
- `auto-update` mode interplay - deferred to D.3.
- Release notes links - deferred to D.3.
- "Unobtrusive target" check (no-tab suppression) - deferred to D.3.

## Threading contract

Every class subscribing to `UpdateService.StateChanged` (TaskbarOverlayProvider,
UpdateToastPublisher, RestartPendingExitInterceptor) takes a `DispatcherQueue`
in its constructor and uses `TryEnqueue` to hop back to the UI thread before
touching WinUI or STA COM state. `UpdateJumpListProvider` is the exception -
its WinRT `JumpList.LoadCurrentAsync` is thread-safe. Mirror the dispatcher
pattern in any new subscriber; see `UpdatePillViewModel` for the reference.

## Windows SDK projections

`FocusAssistProbe` references `Windows.UI.Shell.FocusSessionManager` which only
ships in the 22000+ SDK projections. The base TFM is 19041 to keep OSS builds
on older toolchains; sponsor builds bump `WindowsSdkPackageVersion` to
`10.0.26100.57` inside the `SponsorBuild=true` PropertyGroup. A runtime
`IsSupported` check plus a try/catch keep the binary safe on Win10 hosts where
the type is absent.

## Native handle leak in TaskbarOverlayProvider

`LoadImage` loads each overlay icon but the returned `HICON` is never released.
Windows reclaims process-scope handles on exit. State transitions are infrequent
in practice, so the leak is bounded. D.3 may cache the three icons if profiling
shows a hot path.

## D.2 - real driver

Run with `$env:WINTTY_DEV_JWT` set to a dev JWT (mint via
`../wintty-release/worker/scripts/smoke-mint-jwt.ts`). Open the palette
and run "Check for updates (real)" to drive the real Velopack-backed
`UpdateManager` against `api.wintty.io/manifest/stable`. Full walkthrough
in `docs/dev/smoke-update.md`.

The simulator palette entries ("Simulate: *") remain in DEBUG builds as
a secondary driver attached to `UpdateService`; they still emit into
the pill / taskbar / toast pipeline for UI exercise without network.

### Palette re-registration wart

`SponsorUpdateCommandSource` is constructed inside `MainWindow`'s ctor,
which runs *before* `Wire()`. So the "Check for updates (real)" entry
(which needs `UpdateService`) is added via a post-Wire re-registration:
`App.OnLaunched` calls `window.RegisterSponsorService(_sponsorOverlay.Service)`
which in turn calls `_sponsorSource.SetService(service)` + `Refresh()`.
Same wart as D.1; D.3 is the right time to move palette construction
to post-Wire and drop the re-register.

### Apply-and-restart is un-testable on dev

Clicking "Restart Now" correctly errors with "Couldn't apply the update"
because Velopack's on-disk stub (`Update.exe`) isn't present in a dev
checkout. Real apply-and-restart validation lands with Plan C's signed
`Setup.exe`. This is documented, not a bug.
