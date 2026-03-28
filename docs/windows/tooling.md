# Tooling & CI

## Why Just

This project uses [Just](https://github.com/casey/just) as a command runner for the Windows
development inner loop.

Just is like Make but simpler and more natural on Windows. No tabs-vs-spaces footguns,
no implicit rules, just named recipes that run commands. It's common in the Zig and Rust
ecosystems and installs with `winget install Casey.Just`.

The `justfile` at the repo root has recipes for testing, building the DLL, and syncing
with upstream. Run `just --list` to see them all.

## How CI works

There is no automated CI pipeline. GitHub Actions is disabled.

This is a solo passion project developed on a cross-platform homelab - Windows, macOS,
and Linux machines are all available. The primary development loop happens on Windows
since that's where the port runs.

**Inner loop (every change):**

```bash
just test-lib-vt   # ~30s, catches most regressions
just build-dll     # verify the DLL builds
```

**Before merging a PR:**

```bash
just test           # full Zig test suite on Windows
```

**Cross-platform testing (on demand):**

Tests run natively on all three platforms via SSH. This happens before merging
anything that touches shared code (Zig, build system, renderer abstractions).
It's skipped for Windows-only changes like C# code.

Upstream keeps Linux and macOS CI healthy. Since the fork rebases daily, any
cross-platform regression from upstream gets caught during the next sync.

## Why not GitHub Actions

GitHub's free tier gives 2,000 minutes/month, but Windows runners consume minutes
at 2x rate. A single full test run can burn through a meaningful chunk of that budget.
Running locally on real hardware costs nothing and gives faster feedback.

If the project grows beyond solo development, CI can be added back - the `justfile`
recipes define exactly what a pipeline would run.
