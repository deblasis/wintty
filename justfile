# Ghostty Windows Fork - Build Orchestration
# Run `just` for the default (full test + build), or `just <recipe>` for individual steps.

# Cross-platform shell selection.
#
# On unix the default `sh` is fine and most recipes are single program
# invocations (zig build, dotnet build) that work in any POSIX shell.
#
# On Windows we pin pwsh.exe so users do not need git-bash on PATH for the
# common build/run recipes. The few recipes that genuinely need bash (the
# example test loops, the sync helper) carry an explicit `#!/usr/bin/env bash`
# shebang, which bypasses this setting and runs under bash regardless. Those
# recipes still need git-bash on Windows; the build/run path does not.
set windows-shell := ["pwsh.exe", "-NoLogo", "-NoProfile", "-Command"]

# Default: run tests and build the DLL
default: test build-dll

# === Testing ===

# The version the Zig test steps compile with, in place of the one git
# would report. Without it every test binary carries the branch name and
# short hash in build_options, which every module imports, so any new
# commit (an amend, a rebase, a docs-only merge) recompiles every test
# binary and re-runs every test even though no Zig source moved. Zig
# already caches a test run against the binary's own hash; a fixed
# version is what lets that cache see two commits as the same input, and
# it is also what lets worktrees on different branches share the central
# cache for src/. Only the test steps use it: the DLL keeps its real
# version. .agents/scripts/test_reachability.py compiles the same
# binaries and must pass the same string, or the reachability build is a
# second cold compile of everything - `gates-selftest` holds them equal.
TEST_VERSION := "0.0.0-test"

# The seed the test steps run with. `zig build` draws a random one per
# invocation and hands it to every test binary as `--seed=0x...`, and
# that argument is part of the run step's cache manifest: measured on
# this machine, a second `zig build test-lib-vt` at the same commit
# compiled nothing and still ran both binaries for sixteen minutes. A
# fixed seed is what makes an unchanged binary's run a cache hit.
# Override it for a run that wants fresh randomness (the nightly does):
#   $env:WINTTY_TEST_SEED = '0x1234'; just test
TEST_SEED := env_var_or_default("WINTTY_TEST_SEED", "0x2a")

# Run all Zig tests
test: test-configure test-lib-vt test-full test-pkg test-reachability

# With every test step on the fixed version, nothing in the ladder runs
# build.zig's own version detection any more, and that path has broken
# before: Config.init panics on a tag it does not recognise, which is
# what `gitversion-selftest` guards from the git side. This runs the
# configure phase alone, with the real git lookup, so a build.zig that
# cannot configure still fails the Zig leg. Listing the steps is the
# cheapest thing that forces build() to run to completion.
#
# Prove build.zig configures with git version detection.
test-configure:
    zig build --list-steps -Dapp-runtime=none

# The quotes around the version argument are load-bearing on Windows:
# the recipe body runs under `pwsh -Command`, whose parser splits an
# unquoted `-Dversion-string=0.0.0-test` at the first dot into
# `-Dversion-string=0` and `.0.0-test`, and build.zig then fails with
# InvalidVersion before compiling anything. `-Dapp-runtime=none` survives
# only because it has no dot. test_reachability's self-test checks the
# quotes are still there.
#
# Test libghostty-vt (fastest feedback loop)
test-lib-vt:
    zig build test-lib-vt "-Dversion-string={{TEST_VERSION}}" --seed {{TEST_SEED}} --summary all

# Full Zig test suite
test-full:
    zig build test -Dapp-runtime=none "-Dversion-string={{TEST_VERSION}}" --seed {{TEST_SEED}} --summary all

# Tests that live in the vendored packages rather than in src/.
#
# `zig build test` roots at src/main.zig and the packages under pkg/ are
# separate builds, so nothing reachable from that root ever compiles their test
# blocks. A wrong assertion in pkg/wuffs/src/gif.zig passes the whole suite
# here. CI does run them, in the test-pkg-linux job, which is a required check,
# so this closes a local gap rather than a total one.
#
# The list mirrors that job's matrix, which is only wuffs. Eight other packages
# under pkg/ carry test blocks; why CI covers just this one is that job's
# business, and duplicating a different list here would leave two to maintain.
test-pkg:
    cd pkg/wuffs && zig build test --seed {{TEST_SEED}} --summary all

# Zig collects `test` blocks from the files a test binary's own test and
# comptime blocks reach, so a file can carry assertions that no test step has
# ever executed. That reads as coverage and is worse than no tests: it is what
# happened to src/build/GitVersion.zig and to every block in
# src/build/wasm_patch_growable_table.zig.
#
# So this compiles every test binary the build runs and asks each one, over
# the std.zig.Server protocol the stock test runner speaks on `--listen=-`,
# for the qualified name of every test it carries. Names are module-relative
# paths, so they map back onto files. A file with test blocks and no name in
# any binary is a finding. What is knowingly out of reach is registered file
# by file with its reason, and the check prints all of it on every run.
#
# It costs a full test-binary build, which is why it rides here with the rest
# of the Zig ladder rather than with the cheap gates. The part that runs
# without a build is wired into `gates-selftest`.
#
# Prove that no file's test blocks are dead.
test-reachability:
    python .agents/scripts/test_reachability.py --version-string {{TEST_VERSION}}

# Cross-platform sanity check (on demand)
# Uses the cross-platform-test Claude Code skill for native SSH-based testing.
test-cross:
    @echo "Use the cross-platform-test Claude Code skill for native multi-platform testing."
    @echo "It runs zig build test natively on Windows, Linux, and Mac via SSH."

# Build and test all examples (mirrors CI: clean zig-out, build zig + cmake examples)
test-examples: _test-examples-zig _test-examples-cmake
    @echo "All examples done."

# Zig examples (zig build in each example dir)
_test-examples-zig:
    #!/usr/bin/env bash
    set -e
    rm -rf zig-out .zig-cache
    failed=""
    for dir in example/*/; do
        [ -f "$dir/build.zig.zon" ] || continue
        name=$(basename "$dir")
        echo "=== zig: $name ==="
        (cd "$dir" && zig build 2>&1) || failed="$failed $name"
    done
    if [ -n "$failed" ]; then
        echo "FAILED:$failed"
        exit 1
    fi

# CMake examples (requires VS Dev Shell on Windows)
_test-examples-cmake:
    #!/usr/bin/env bash
    set -e
    failed=""
    # Convert MSYS /c/... paths to C:\... for PowerShell/CMake
    if [[ "$OSTYPE" == "msys"* || "$OSTYPE" == "cygwin"* || -n "$WINDIR" ]]; then
        win_root=$(cygpath -w "$PWD")
    fi
    for dir in example/*/; do
        [ -f "$dir/CMakeLists.txt" ] || continue
        name=$(basename "$dir")
        echo "=== cmake: $name ==="
        rm -rf "$dir/build"
        if [ -n "$win_root" ]; then
            win_dir="$win_root\\$dir"
            powershell.exe -NoProfile -Command "
                Import-Module 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
                Enter-VsDevShell -VsInstallPath 'C:\Program Files\Microsoft Visual Studio\18\Community' -DevCmdArguments '-arch=x64' -SkipAutomaticLocation
                cd '$win_dir'
                cmake -B build -DFETCHCONTENT_SOURCE_DIR_GHOSTTY='$win_root'
                cmake --build build
            " || failed="$failed $name"
        else
            repo_root="$PWD"
            (cd "$dir" && cmake -B build -DFETCHCONTENT_SOURCE_DIR_GHOSTTY="$repo_root" && cmake --build build) || failed="$failed $name"
        fi
    done
    if [ -n "$failed" ]; then
        echo "FAILED:$failed"
        exit 1
    fi

# === Building ===

# Build libghostty DLL
build-dll:
    zig build -Dapp-runtime=none

# Build libghostty DLL optimized. Much slower to compile; the only build
# worth drawing timing conclusions from, since a Debug DLL is several times
# slower at runtime.
build-dll-release:
    zig build -Dapp-runtime=none -Doptimize=ReleaseFast

# === WinUI 3 app shell ===

# Build the WinUI 3 app shell (expects ghostty.dll at zig-out/bin/).
[windows]
build-win:
    dotnet build windows/Ghostty.sln /p:Platform=x64

# Recipe body has no shebang so it runs under the platform shell selected by
# `set windows-shell` above (pwsh on Windows). The previous version used a
# bash shebang to `exec` the .exe, which forced git-bash on Windows for no
# reason - launching a Windows .exe works fine from pwsh.

# Build the WinUI 3 app shell in Release.
[windows]
build-win-release:
    dotnet build windows/Ghostty.sln /p:Platform=x64 /p:Configuration=Release

# Build the DLL and the shell under the build lane, then launch it. The
# launch itself is outside any lane on purpose: pwsh returns as soon as a
# GUI process is up, so a lane held here would be released while the
# window is still open. The harnesses' own Assert-NoWintty is what guards
# the desktop against that window.
[windows]
run-win: (_build-in-lane "run-win" "build-dll" "build-win")
    ./windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe

# Same, optimized on both sides. Startup timings taken from `run-win` are
# Debug timings and are not the ones users see: the C# shell carries no
# optimization and libghostty is a Debug build. Use this before concluding
# anything about how long startup, the launch splash, or a frame takes.
[windows]
run-win-release: (_build-in-lane "run-win-release" "build-dll-release" "build-win-release")
    ./windows/Ghostty/bin/x64/Release/net10.0-windows10.0.19041.0/Wintty.exe

# Run the C# test suites. Ghostty.Tests is pure logic and cross-platform;
# Ghostty.Tests.Windows holds the tests that need real Windows semantics
# (named mutexes, file sharing, the registry). Both need Platform=x64:
# Ghostty.Core's CsWin32 bindings include pointer-size-dependent structs
# (SHFILEINFOW) that cannot generate for AnyCPU, so an unqualified build
# fails before any test runs.
# --blame-hang: a hung testhost self-recovers with a dump after 5m instead of
# stalling a signoff. Added after a proven FakeTransport pipe deadlock hung
# the whole Ghostty.Tests host at zero CPU; the pipe-buffer fix removed that
# hang, and blame-hang turns any future one into evidence.
#
# The APP is built first, and that is not redundant. Neither test project
# references Ghostty.csproj -- that is a deliberate boundary, and it is why
# ShippingBuildGateTests cannot open Wintty.dll -- so `dotnet test` alone never
# compiles the shell. A change that breaks only the app therefore left this leg
# green, which is not a theory: #921 merged a file that did not compile, with a
# signoff PASS recorded against it, because the ladder's only Windows leg had no
# reason to build the project the mistake was in. The whole solution is built
# rather than the one project, so a new project cannot join the tree and be
# missed the same way.
[windows]
test-win:
    dotnet build windows/Ghostty.sln /p:Platform=x64
    dotnet test windows/Ghostty.Tests/Ghostty.Tests.csproj /p:Platform=x64 --blame-hang --blame-hang-timeout 5m
    dotnet test windows/Ghostty.Tests.Windows/Ghostty.Tests.Windows.csproj /p:Platform=x64 --blame-hang --blame-hang-timeout 5m
    # IconGen/SplashGen were compiled by the solution line above but executed
    # by nothing until a coverage audit caught it; AnyCPU in the sln, so they
    # take no /p:Platform=x64 unlike the two above.
    dotnet test dist/windows/IconGen.Tests/IconGen.Tests.csproj --blame-hang --blame-hang-timeout 5m
    dotnet test dist/windows/SplashGen.Tests/SplashGen.Tests.csproj --blame-hang --blame-hang-timeout 5m

# === Heavy job lanes (AGENTS.md) ===
#
# incoda is looked up on PATH and then in its installer's location, because
# the agent shell this was written under had the latter and not the former.
# This is the expression, not the path: it is evaluated inside each recipe
# body's pwsh, so a `just test` on a Linux host never runs it.
inc := '(Get-Command incoda -ErrorAction SilentlyContinue)?.Source ?? (Join-Path $env:LOCALAPPDATA "Programs\incoda\incoda.exe")'

# The build phase of a harness recipe, under the build lane. Every harness
# recipe depends on this instead of on `build-dll build-win` directly: the
# lane a build needs (CPU and RAM, three at a time) is not the lane a
# harness needs (the desktop, alone), and a recipe that held one key across
# both phases would either serialise every build behind a two-hour fuzz run
# or run the harness while a build churns next door. So the build takes
# wintty-build and releases it, and the caller takes wintty-desktop for the
# harness. RECIPES is what to build, in order.
#
# Do not wrap one of these recipes in `incoda run` on a single key: the two
# phases take different keys, so whichever phase is on the other key nests
# where the outer run holds nothing, and an outer wintty-desktop waiting on
# wintty-build inverts the order the exclusive pair takes: against a live
# exclusive run the two wait on each other until both --wait budgets
# elapse. Call them bare; the one safe
# wrapper is the exclusive pair, which holds both keys before either phase
# starts and lets both nested runs pass through.
#
# The args reach the desktop phase inside double quotes with every double
# quote doubled (pwsh's own escape), so a `$` or a backtick is what cannot
# be passed; a single quote and a double quote both survive.
[windows]
_build-in-lane reason +recipes:
    $inc = {{inc}}; if (-not (Test-Path $inc)) { Write-Host "incoda not found on PATH or in Programs\incoda: the heavy job lanes need it (AGENTS.md; https://github.com/deblasis/incoda)" -ForegroundColor Red; exit 1 }; & $inc run --queue wintty-build --reason "{{reason}}: build" -- just {{recipes}}; exit ($LASTEXITCODE ?? 1)

# Launch two instances a few hundred ms apart and watch for a launch splash
# owned by the one that should be forwarding itself to the other. Opens real
# windows, so it needs an interactive desktop and no Wintty already running.
# Pass extra args through, e.g. `just splash-race "-SecondaryFeatureOff"`.
[windows]
splash-race args="": _no-wintty-running (_build-in-lane "splash-race" "build-win")
    $inc = {{inc}}; & $inc run --queue wintty-desktop --reason ("splash-race {{replace(args, '"', '""')}}".Trim()) -- just _splash-race-in-lane "{{replace(args, '"', '""')}}"; exit ($LASTEXITCODE ?? 1)

# The desktop phase of `just splash-race`, inside the lane it took.
[windows]
_splash-race-in-lane args="":
    pwsh -NoProfile -File windows/scripts/splash-single-instance-race.ps1 {{args}}; exit ($LASTEXITCODE ?? 1)

# Checked before the builds, not after: the harnesses refuse to run while a
# Wintty is open, and dotnet build cannot overwrite a locked Wintty.exe, so
# without this the developer pays a full zig + dotnet build only to be told
# to close a window -- or gets an MSB file-in-use error that hides the real
# reason. Prerequisites run in the order listed.
#
# The trailing `exit 0` is load-bearing, and the reason is narrower than it
# looks: `pwsh -Command` returns the success of the LAST STATEMENT EXECUTED.
# `Get-Process Wintty` finding no match leaves $? false even under
# -ErrorAction SilentlyContinue or Ignore, and an `if` that is not taken does
# not reset it - so the recipe fell off its end in the failed state, on a clear
# desktop, with no message, because the pid list only prints on the branch that
# was not taken.
#
# It does not mask a `throw`, which never reaches the exit, but it does mask a
# trailing non-terminating error. Treat this as a convenience gate only: the
# authority is Assert-NoWintty in lib/wintty-process.ps1, which every harness
# calls for itself.
[windows]
_no-wintty-running:
    $p = @(Get-Process Wintty -ErrorAction SilentlyContinue); if ($p.Count -gt 0) { Write-Host ("close the running Wintty first (pid: " + ($p.Id -join ', ') + ")") -ForegroundColor Red; exit 1 }; exit 0

# Fuzz in-pane scrollback search against a real oracle: the harness reads the
# terminal's own UIA text document, counts matches itself, and compares every
# needle it types against that count. Drives real input, so it needs an
# interactive desktop and takes the foreground for the duration.
#
# Exit codes: 0 clean, 2 product findings (see the JSON and shots under
# windows/scripts/search-fuzz/), 1 the harness could not run.
#
# Pass extra args through, e.g. `just search-fuzz "-Seed 99 -Iterations 40"`.
[windows]
search-fuzz args="": _no-wintty-running (_build-in-lane "search-fuzz" "build-dll" "build-win")
    $inc = {{inc}}; & $inc run --queue wintty-desktop --reason ("search-fuzz {{replace(args, '"', '""')}}".Trim()) -- just _search-fuzz-in-lane "{{replace(args, '"', '""')}}"; exit ($LASTEXITCODE ?? 1)

# The desktop phase of `just search-fuzz`, inside the lane it took.
[windows]
_search-fuzz-in-lane args="":
    pwsh -NoProfile -File windows/scripts/search-fuzz.ps1 \
        -ExePath windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe \
        -OutDir windows/scripts/search-fuzz {{args}}; exit ($LASTEXITCODE ?? 1)

# Fuzz the "Custom shader not applied" banner from both sides, against configs
# the harness stages itself: no shader configured must raise no banner at all,
# and a shader that cannot be read or translated must raise one. It shipped
# wrong in the first direction once, when the C# action-tag enum drifted from
# include/ghostty.h and first_render arrived at the shader handler.
#
# Launches Wintty five times, so it needs an interactive desktop and takes the
# foreground for a few minutes.
#
# Exit codes: 0 clean, 2 product findings (see the JSON and shots under
# windows/scripts/shader-notice-fuzz/), 1 the harness could not run.
#
# Pass extra args through, e.g. `just shader-notice-fuzz "-Seed 99"`.
[windows]
shader-notice-fuzz args="": _no-wintty-running (_build-in-lane "shader-notice-fuzz" "build-dll" "build-win")
    $inc = {{inc}}; & $inc run --queue wintty-desktop --reason ("shader-notice-fuzz {{replace(args, '"', '""')}}".Trim()) -- just _shader-notice-fuzz-in-lane "{{replace(args, '"', '""')}}"; exit ($LASTEXITCODE ?? 1)

# The desktop phase of `just shader-notice-fuzz`, inside the lane it took.
[windows]
_shader-notice-fuzz-in-lane args="":
    pwsh -NoProfile -File windows/scripts/shader-notice-fuzz.ps1 \
        -ExePath windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe \
        -OutDir windows/scripts/shader-notice-fuzz {{args}}; exit ($LASTEXITCODE ?? 1)

# Fuzz frame-style against window-theme and background-style over a fixed
# spanning set run once per half of the built-in theme pair, plus `-Random <n>`
# extra cases drawn from the staged theme catalogue. Two oracles: WCAG
# contrast of the title row's and tab strip's own
# text against their own fill, and a relative check that solid does not paint
# the same chrome as frosted. frosted against crystal is reported and never
# asserted - one SystemBackdrop per window means they are the same frame.
#
# Reads the desktop light/dark setting and High Contrast; sets neither.
#
# Launches Wintty nineteen times, so it needs an interactive desktop and takes
# the foreground for several minutes.
#
# Exit codes: 0 clean, 2 product findings (see the JSON and shots under
# windows/scripts/frame-style-fuzz/), 1 the harness could not run.
#
# Pass extra args through, e.g. `just frame-style-fuzz "-Seed 99 -Random 3"`.
[windows]
frame-style-fuzz args="": _no-wintty-running (_build-in-lane "frame-style-fuzz" "build-dll" "build-win")
    $inc = {{inc}}; & $inc run --queue wintty-desktop --reason ("frame-style-fuzz {{replace(args, '"', '""')}}".Trim()) -- just _frame-style-fuzz-in-lane "{{replace(args, '"', '""')}}"; exit ($LASTEXITCODE ?? 1)

# The desktop phase of `just frame-style-fuzz`, inside the lane it took.
[windows]
_frame-style-fuzz-in-lane args="":
    pwsh -NoProfile -File windows/scripts/frame-style-fuzz.ps1 \
        -ExePath windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe \
        -OutDir windows/scripts/frame-style-fuzz {{args}}; exit ($LASTEXITCODE ?? 1)

# The theme matrix (#937): every selected theme against desktop polarity,
# app and frame material, tab layout and a backdrop scene behind the window,
# measured in rendered pixels against lib/contrast.ps1's floors. Hours for the
# curated set, a day and more for `-Theme all`; a red run is the expected
# outcome and windows/scripts/theme-matrix/matrix.md is the deliverable.
#
# It takes the lanes itself, one per phase (see _build-in-lane): the DLL
# and the shell build under wintty-build, then the harness holds
# wintty-desktop for the whole run, because it flips the desktop theme and
# the wallpaper and puts a topmost stage on screen, so nothing else may own
# the desktop while it runs. Hence two recipes: this one takes the lanes,
# the private one below it does the work inside the desktop lane.
#
# Every axis takes one value, a comma list, or all. Pass args through, e.g.
#   just theme-matrix "-Theme wintty-dark -Polarity dark -Layout vertical"
#   just theme-matrix "-Theme all -App solid -Frame inherit -Scene black,photo"
#   just theme-matrix "-Theme 'Catppuccin Mocha,Nord' -App crystal"
#   just theme-matrix "-NoFlip"    never touch the desktop theme
# A theme list with a space in it is ONE inner single-quoted string with the
# commas inside, as in the third line. The recipe body is PowerShell:
# `"Catppuccin Mocha",Nord` there would be an array that reaches the harness
# as two arguments, the second of which lands on -Polarity, and a `\"` is
# not an escape at all. The args cross the lane inside double quotes with
# any double quote doubled, so a `$` or a backtick is what cannot be passed.
# The no-Wintty check runs before the build lane rather than inside the
# desktop lane, like every other harness: the window check that matters is
# the harness's own Assert-NoWintty once it holds the desktop.
#
# Exit codes: 0 clean, 2 findings, 1 could not run or a surface went unmeasured.
#
# Run the theme matrix (#937) under the incoda lanes against the Debug build.
[windows]
theme-matrix args="": _no-wintty-running (_build-in-lane "theme matrix (#937)" "build-dll" "build-win")
    $inc = {{inc}}; & $inc run --queue wintty-desktop --reason ("theme matrix (#937) {{replace(args, '"', '""')}}".Trim()) -- just _theme-matrix-in-lane "{{replace(args, '"', '""')}}"; exit ($LASTEXITCODE ?? 1)

# The desktop phase of `just theme-matrix`, inside the lane it took.
[windows]
_theme-matrix-in-lane args="":
    pwsh -NoProfile -File windows/scripts/theme-matrix.ps1 \
        -ExePath windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe \
        -OutDir windows/scripts/theme-matrix {{args}}; exit ($LASTEXITCODE ?? 1)

# No lane, no build, no desktop, same args as theme-matrix.
#
# Print the processes a theme-matrix filter selects and their cost; launch nothing.
[windows]
theme-matrix-plan args="":
    pwsh -NoProfile -File windows/scripts/theme-matrix.ps1 -DryRun \
        -ExePath windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe \
        -OutDir windows/scripts/theme-matrix {{args}}; exit ($LASTEXITCODE ?? 1)

# The harness writes matrix.md itself; this is for a run dir kept from
# earlier. Paste the result into #937.
#
# Rebuild matrix.md from a theme-matrix run that already happened.
[windows]
theme-matrix-report run="windows/scripts/theme-matrix":
    pwsh -NoProfile -File windows/scripts/theme-matrix-report.ps1 -RunDir "{{run}}"; exit ($LASTEXITCODE ?? 1)

# Real windows and real input, so it needs an interactive desktop and holds
# the foreground for the duration - about 43 minutes budgeted for everything,
# 7 for `-Tag smoke` (measured 5). Each harness is killed if it overruns its
# budget, so a wedged one cannot hold the desktop indefinitely.
#
# Exit codes: 0 clean, 2 product findings, 1 one or more harnesses could not
# run (so their area is untested, not proven good).
#
# `; exit ($LASTEXITCODE ?? 1)` is not decoration. The recipe body runs under
# `pwsh -Command`, which reports the last statement's success as 0 or 1 - so
# without it a product finding (2) and a harness that could not run (1) arrive
# here identical, which is the whole distinction these harnesses exist to make.
# The `?? 1` carries its own weight: $LASTEXITCODE is only assigned by a native
# command that actually ran, so a pwsh that fails to launch would otherwise
# `exit $null`, which is `exit 0` - a clean fuzz run that never happened.
#
# Args pass through, e.g. `just fuzz "-Tag smoke"` or `just fuzz "-Only search"`.
#
# Run every GUI fuzz harness against the Debug build.
[windows]
fuzz args="": _no-wintty-running (_build-in-lane "fuzz" "build-dll" "build-win")
    $inc = {{inc}}; & $inc run --queue wintty-desktop --reason ("fuzz {{replace(args, '"', '""')}}".Trim()) -- just _fuzz-in-lane "{{replace(args, '"', '""')}}"; exit ($LASTEXITCODE ?? 1)

# The desktop phase of `just fuzz`, inside the lane it took.
[windows]
_fuzz-in-lane args="":
    pwsh -NoProfile -File windows/scripts/fuzz-suite.ps1 \
        -ExePath windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe {{args}}; exit ($LASTEXITCODE ?? 1)

# Alias for `just fuzz`, for when that is what the fingers type.
[windows]
fuzzy args="": (fuzz args)

# Round-trip a payload through the system clipboard over OSC 5522.
#
# RUN THIS INSIDE WINTTY. It drives the terminal with escape sequences and
# reads the replies off its own stdin, so it needs no GUI automation and no
# synthesized input. Oracle: SHA-256 of the bytes written against the bytes
# read back.
#
# This is the only harness here that exercises the LIVE C ABI end to end --
# write_clipboard_cb out to the Windows clipboard and read_clipboard_cb back
# again. `just clipboard-fuzz` checks our reader against our writer; this
# checks that libghostty drives them the way we think.
#
# Attended by default: the read raises the permission prompt and waits for a
# human to allow it, which is also how you check the prompt previews an image
# as an image. Pass `unattended` once clipboard-read and clipboard-write are
# both `allow` in the config, and then iterations above 1 loop without anyone
# watching.
# Exercise the round-trip harness's own reply parser against synthetic
# replies. No terminal, no clipboard, no human, about a second.
#
# It exists because every failure that harness reported on its first outing
# was a bug in the parser rather than in the clipboard, and each one cost a
# full attended run to find. Parsing a reply is string processing; it does
# not need a GUI to be checked, and checking it here keeps the attended run
# for verifying the product instead of debugging the harness.
[windows]
clipboard-roundtrip-selftest:
    pwsh -NoProfile -File windows/scripts/kitty-clipboard-roundtrip.ps1 -SelfTest; exit ($LASTEXITCODE ?? 1)

[windows]
clipboard-roundtrip args="":
    pwsh -NoProfile -File windows/scripts/kitty-clipboard-roundtrip.ps1 {{args}}; exit ($LASTEXITCODE ?? 1)

# Write cost against payload size, run INSIDE Wintty.
#
# A single round-trip timing cannot say whether a slow clipboard write is a
# fixed per-write expense or a per-byte one, and those have opposite fixes.
# This writes five sizes and reports the shape, so the answer comes from a
# slope rather than from a guess. Set clipboard-write = allow first, or every
# size waits on a dialog and the numbers measure the human.
[windows]
clipboard-sweep:
    pwsh -NoProfile -File windows/scripts/kitty-clipboard-roundtrip.ps1 -Sweep; exit ($LASTEXITCODE ?? 1)

# Randomized round-trip fuzz over the clipboard marshalling boundary.
#
# No build of the app, no desktop, safe to run with Wintty open. The ladder
# already runs these oracles at a cheap iteration count via `just test-win`;
# this is the deep pass.
#
# Oracle: round-trip fidelity, not liveness. It builds real unmanaged memory
# with the writer, reads it back with the reader, and asserts the bytes match,
# that the adjacent confirmed/remember pair did not swap, and that a formatted
# file URI parses back to the path it came from. It does NOT check the live C
# ABI: the layouts are pinned against include/ghostty.h separately, and
# callback ordering needs `just run-win`.
[windows]
clipboard-fuzz iterations="20000":
    pwsh -NoProfile -File windows/scripts/clipboard-fuzz.ps1 -Iterations {{iterations}}; exit ($LASTEXITCODE ?? 1)

# No build, no desktop.
#
# List the suite: what it runs, what each harness catches, what it costs.
[windows]
fuzz-list:
    pwsh -NoProfile -File windows/scripts/fuzz-suite.ps1 -List; exit ($LASTEXITCODE ?? 1)

# Runs the suite runner against fixtures that exit 0, 1, 2 and 3 on purpose,
# plus ones that throw, hang, and fail once then work. About a minute, no
# build, no desktop, and safe to run with Wintty open.
#
# Prove the suite still tells a product finding from a harness that broke.
[windows]
fuzz-selftest:
    pwsh -NoProfile -File windows/scripts/fuzz-suite.ps1 -SelfTest; exit ($LASTEXITCODE ?? 1)

# Prove the backdrop stage (windows/scripts/lib/BackdropStage) is an
# instrument: it comes up where it was told, never takes the foreground,
# paints every catalogued scene so the screen reads it back, moves, survives
# a refused op, and exits 0 on quit. Builds the stage on first use. Launches
# no Wintty and sets no wallpaper, so it is safe with a Wintty open; it does
# put a small topmost window on screen for about ten seconds.
#
# Exit codes: 0 sound, 1 not sound or could not start. No 2: this judges the
# instrument, never the product.
#
# Prove the backdrop stage paints what it is told and never takes focus.
[windows]
backdrop-stage-selftest:
    pwsh -NoProfile -File windows/scripts/backdrop-stage-selftest.ps1; exit ($LASTEXITCODE ?? 1)

# Manual recovery after a harness crashed with system state (High Contrast,
# desktop colour, app theme) left behind: restores from the snapshot the
# env-guard library takes, and throws if the read-back does not match it.
[windows]
env-restore:
    pwsh -NoProfile -File windows/scripts/lib/env-guard.ps1 -Restore; exit ($LASTEXITCODE ?? 1)

# === Upstream Sync ===

# Pinned to bash via shebang so the POSIX `[` branch test below works
# regardless of the platform shell. On Windows this requires git-bash on
# PATH; sync is a maintainer command and the maintainer has it.

# The flow: `just sync-bootstrap` once, then per sync `just sync` (replay),
# `just sync-verify` (gate), `just sync-publish` (publish). The windows branch
# itself is never rewritten and never force-pushed, which is what lets branch
# protection hold on it.
#
# REQUIRED reading before resolving anything a replay raises:
# .agents/skills/syncing-the-windows-fork/SKILL.md - it carries the breakage
# shapes that never conflict, the traps, and the recovery paths. It lives in
# .agents/ (not .claude/), so agent tooling does not auto-load it; this
# pointer is the discovery mechanism.
#
# Two representations of the same content, on purpose:
#
#   series/vN tags   the fork as a linear patch series, rebased onto the
#                    upstream of its day. Where conflicts get resolved and
#                    what sync-verify measures.
#   windows          what everything consumes: PRs merge into it, the nightly
#                    tracks it, tier pins point into it. It only moves
#                    forward. Each sync lands as ONE merge commit whose tree
#                    is exactly the verified series tree.
#
# The invariant tying them together: the last snapshot merge on windows
# carries the tree of the latest series/vN tag. sync refuses to start when
# that does not hold, because the fold-in below relies on it.

# Builds the candidate on the series-wip branch: latest series tag, plus the
# PRs windows merged since the last snapshot, rebased onto upstream/main. The
# fold-in applies clean by construction - at the last snapshot both sides had
# the same tree - so on a fresh replay any fold-in conflict means the
# invariant broke earlier.
#
# Re-running is safe at every point. A series-wip left over from a published
# sync still carries its series tag and is rebuilt; a tagless one holds an
# unpublished replay and is resumed, folding in only commits (by patch-id) it
# does not already carry, so a publish that lost a race to a mid-sync PR
# merge is retried with this same command. The exception is a series-wip built from a
# generation that is no longer the latest: publishing it would drop whatever
# the newer generation folded in, so it is refused, not resumed.
#
# Replay the fork onto the latest upstream.
sync force="":
    #!/usr/bin/env bash
    set -euo pipefail
    # Probed before the branch guard: a conflicted rebase leaves
    # `git branch --show-current` EMPTY, so without this the operator who
    # re-runs sync mid-conflict (the habit the fold-in message trains) gets
    # a wrong-branch warning naming '', and --force would then abandon the
    # rebase state half-applied.
    git_dir=$(git rev-parse --git-dir)
    if [ -d "$git_dir/rebase-merge" ] || [ -d "$git_dir/rebase-apply" ]; then
        echo "REFUSING: a rebase is in progress. Resolve and 'git rebase --continue'"
        echo "(or 'git rebase --abort'), then re-run 'just sync'."
        exit 1
    fi
    if [ -f "$git_dir/CHERRY_PICK_HEAD" ]; then
        echo "REFUSING: a cherry-pick is in progress. Resolve and"
        echo "'git cherry-pick --continue' (or 'git cherry-pick --abort'), then"
        echo "re-run 'just sync'."
        exit 1
    fi
    branch=$(git branch --show-current)
    if [ "{{ force }}" != "--force" ] && [ "$branch" != "windows" ] && [ "$branch" != "series-wip" ]; then
        echo "WARNING: you are on '$branch', not 'windows' or 'series-wip'. sync checks"
        echo "out series-wip, which would pull this worktree off your branch. Use"
        echo "'just sync --force' to override."
        exit 1
    fi
    if ! git diff --quiet || ! git diff --cached --quiet; then
        echo "REFUSING: the working tree is not clean, and the checkout below would"
        echo "carry the dirt onto series-wip."
        exit 1
    fi
    git fetch upstream
    git fetch origin
    # Explicit, because tag auto-following cannot deliver these: series tags
    # point at replay tips no branch reaches, so a plain fetch never brings
    # them and a second machine would number generations from a stale set and
    # read the invariant check below as corruption. Unforced on purpose - a
    # tag that moved on the remote surfaces as an error instead of being
    # silently adopted.
    git fetch origin 'refs/tags/series/*:refs/tags/series/*'
    prev_n=$(git tag --list 'series/v*' | sed 's|^series/v||' | grep -E '^[0-9]+$' | sort -n | tail -1 || true)
    if [ -z "$prev_n" ]; then
        echo "REFUSING: no series/v* tag. This flow publishes windows by snapshot merge"
        echo "and needs the series baseline a one-time 'just sync-bootstrap' creates."
        exit 1
    fi
    prev="refs/tags/series/v${prev_n}"
    # A snapshot merge is the one shape on windows whose SECOND parent is an
    # upstream commit; every other merge there joins two fork commits. Reading
    # it as 'the newest merge' instead assumed every PR squash-merges, and a
    # PR that landed unsquashed sat on the same first-parent chain, was taken
    # for the snapshot, and refused the sync while hiding its own commits from
    # the fold below. The range bound stays: unbounded, the walk sails into
    # upstream's first-parent chain, which is all merges. Before the first
    # snapshot exists the range holds no merge at all, and the v0 tag plays
    # the role.
    mprev=""
    for c in $(git rev-list --min-parents=2 --first-parent refs/remotes/upstream/main..refs/remotes/origin/windows); do
        if git merge-base --is-ancestor "${c}^2" refs/remotes/upstream/main; then
            mprev="$c"
            break
        fi
    done
    if [ -z "$mprev" ]; then
        mprev=$(git rev-parse "${prev}^{commit}")
    fi
    if ! git merge-base --is-ancestor "$mprev" refs/remotes/origin/windows; then
        echo "REFUSING: the snapshot baseline (${mprev:0:9}) is not on origin/windows."
        exit 1
    fi
    if [ "$(git rev-parse "${mprev}^{tree}")" != "$(git rev-parse "${prev}^{tree}")" ]; then
        echo "REFUSING: the last snapshot on windows (${mprev:0:9}) does not carry the"
        echo "tree of series/v${prev_n}. The published branch and the series diverged;"
        echo "find out which one moved before replaying anything."
        exit 1
    fi
    if [ "$branch" != "series-wip" ] && git worktree list --porcelain | grep -qx "branch refs/heads/series-wip"; then
        echo "REFUSING: series-wip is checked out in another worktree, and sync"
        echo "rebuilds it in place. Run from that worktree, or remove it first."
        exit 1
    fi
    # A wip some series tag points at was published; it holds nothing of its
    # own and is rebuilt from the latest generation. Only a TAGLESS wip is an
    # unpublished replay worth resuming.
    if git rev-parse --verify -q refs/heads/series-wip >/dev/null && \
       ! git tag --list 'series/v*' --points-at refs/heads/series-wip | grep -q .; then
        # Resume only a replay built from the generation that is STILL the
        # latest. The fold-in below only looks past the last snapshot, so a
        # wip stranded across someone else's publish is missing whatever that
        # publish folded in - and publishing it would fast-forward windows to
        # a tree without those commits, which no branch protection can catch.
        stamp=$(git config --get branch.series-wip.seriesbase || echo "")
        if [ "$stamp" != "series/v${prev_n}" ]; then
            echo "REFUSING: series-wip was built from '${stamp:-an unknown generation}',"
            echo "but the series is now at series/v${prev_n}; publishing it would drop"
            echo "whatever the newer generation folded in. If it holds nothing you"
            echo "need: git checkout windows && git branch -D series-wip, then re-run"
            echo "'just sync'. Anything it does hold must be carried over by hand."
            exit 1
        fi
        echo "resuming the unpublished replay on series-wip"
        git checkout series-wip
    else
        git checkout -B series-wip "${prev}^{commit}"
        git config branch.series-wip.seriesbase "series/v${prev_n}"
    fi
    # Fold in what windows merged since the last snapshot. Patch-id, not SHA:
    # on a resume the earlier fold already carries some of these under new
    # SHAs, and the rebase moved the rest.
    plus=$(git cherry HEAD refs/remotes/origin/windows "$mprev" | awk '$1 == "+" { print $2 }')
    fold=""
    if [ -n "$plus" ]; then
        # git cherry answers membership; the rev-list keeps topological order.
        # Not --first-parent: a PR that lands as a merge keeps its content off
        # that chain, so the narrower walk folded in every squash around it
        # and dropped the whole PR without a word. The full walk minus merges
        # is exactly the set git cherry measures, which is why the two agree.
        fold=$(git rev-list --reverse --topo-order --no-merges "${mprev}..refs/remotes/origin/windows" \
            | grep -Fx -f <(printf '%s\n' "$plus") || true)
    fi
    if [ -n "$fold" ]; then
        echo "folding $(printf '%s\n' "$fold" | wc -l | tr -d ' ') commit(s) windows merged since the last snapshot:"
        git log --no-walk --oneline $fold
        if ! git cherry-pick $fold; then
            echo "The fold-in conflicted. On a fresh replay the tree invariant rules that"
            echo "out; on a resume it can be real, when the replay moved code a later PR"
            echo "touched. Resolve, 'git cherry-pick --continue', then re-run 'just sync'."
            exit 1
        fi
    fi
    git rebase refs/remotes/upstream/main
    echo "Replay complete. Gate it, then publish:"
    echo "  just sync-verify"
    echo "  just sync-publish"
    echo "Guidance (conflict shapes, traps): .agents/skills/syncing-the-windows-fork/SKILL.md"

# One-time cutover from the force-push flow. Tags origin/windows as series/v0,
# which the first 'just sync' after this uses as both the series baseline and
# the snapshot marker. Requires the branch to still be linear: after the first
# snapshot merge lands there is nothing meaningful left for this to tag, and
# it refuses rather than bless a merge as a series.
#
# Tag origin/windows as series/v0, the one-time cutover to this flow.
sync-bootstrap:
    #!/usr/bin/env bash
    set -euo pipefail
    git fetch upstream
    git fetch origin
    # Fetched before the already-bootstrapped check so a clone that never
    # received the series tags (plain fetches cannot deliver them) does not
    # bootstrap a second, conflicting v0.
    git fetch origin 'refs/tags/series/*:refs/tags/series/*'
    if [ -n "$(git tag --list 'series/v*')" ]; then
        echo "REFUSING: series tags already exist; bootstrap is one-time."
        git tag --list 'series/v*' | sed 's/^/  /'
        exit 1
    fi
    tip=$(git rev-parse refs/remotes/origin/windows)
    if [ "$(git rev-list --count --min-parents=2 "refs/remotes/upstream/main..${tip}")" -ne 0 ]; then
        echo "REFUSING: origin/windows carries merge commits, so it is not the linear"
        echo "series this tag would claim it is."
        exit 1
    fi
    git tag -a series/v0 -m "series v0: the windows branch as last rebased" "$tip"
    git push origin refs/tags/series/v0
    echo "series/v0 = ${tip:0:9}. From here on: just sync / sync-verify / sync-publish."

# Publish the verified replay without rewriting windows: one merge commit
# whose parents are the current origin/windows and upstream/main, and whose
# tree is taken WHOLESALE from series-wip. git merge cannot express that - it
# would re-merge and could resolve differently than the replay did - so the
# commit is built directly with commit-tree.
#
# Because the tree arrives wholesale, two assertions gate the stamp itself:
# containment (windows holds nothing the replay lacks, else the stamp drops
# it forever) and unreviewed work (the replay carries nothing windows has
# never seen through a PR, unless acknowledged with a reason that is written
# into the merge message). Both re-run here at publish time; the replay being
# hours old is the normal case, not an anomaly.
#
# The push is deliberately not forced. If windows gained a PR between the
# publish-time fetch and the push, the push is rejected as non-fast-forward,
# the tag is rolled back, and re-running 'just sync' folds the new commits
# into the standing replay. It is atomic across the branch and the series tag
# so a half-published sync cannot exist on the remote.
#
# Publish the verified replay onto windows as a snapshot merge.
sync-publish ack="":
    #!/usr/bin/env bash
    set -euo pipefail
    if [ "$(git branch --show-current)" != "series-wip" ]; then
        echo "REFUSING: publish runs from the series-wip branch 'just sync' builds;"
        echo "you are on '$(git branch --show-current)'."
        exit 1
    fi
    git_dir=$(git rev-parse --git-dir)
    if [ -d "$git_dir/rebase-merge" ] || [ -d "$git_dir/rebase-apply" ] || [ -f "$git_dir/CHERRY_PICK_HEAD" ]; then
        echo "REFUSING: a rebase or cherry-pick is still in progress."
        exit 1
    fi
    if ! git merge-base --is-ancestor refs/remotes/upstream/main HEAD; then
        echo "REFUSING: series-wip does not sit on refs/remotes/upstream/main."
        echo "Run 'just sync' (it fetches and rebases) before publishing."
        exit 1
    fi
    # Same explicit namespace fetch as sync: the next generation is numbered
    # from these tags, and a stale set would mint a duplicate number.
    git fetch origin 'refs/tags/series/*:refs/tags/series/*'
    # Fetched here, not inherited from the last 'just sync', because both
    # assertions below measure against the windows the remote actually has.
    # The push rejects a race that lands between this fetch and the push; the
    # gap these checks close is the one that cannot be caught at push time -
    # a replay built hours ago that a PR merged in between would stamp out.
    git fetch origin windows
    prev_n=$(git tag --list 'series/v*' | sed 's|^series/v||' | grep -E '^[0-9]+$' | sort -n | tail -1 || true)
    if [ -z "$prev_n" ]; then
        echo "REFUSING: no series/v* tag; run 'just sync-bootstrap' first."
        exit 1
    fi
    if [ "$(git rev-parse 'HEAD^{tree}')" = "$(git rev-parse 'refs/remotes/origin/windows^{tree}')" ] \
       && git merge-base --is-ancestor refs/remotes/upstream/main refs/remotes/origin/windows; then
        echo "nothing to publish: windows already carries this tree on the current upstream."
        exit 0
    fi
    # Re-checked here, not just in sync: an accidental publish of a replay
    # stranded across someone else's publish would be an ordinary
    # fast-forward that silently drops what the newer generation folded in.
    stamp=$(git config --get branch.series-wip.seriesbase || echo "")
    if [ "$stamp" != "series/v${prev_n}" ]; then
        echo "REFUSING: series-wip was built from '${stamp:-an unknown generation}', but"
        echo "the series is now at series/v${prev_n}; publishing would drop whatever the"
        echo "newer generation folded in. Re-run 'just sync'."
        exit 1
    fi
    # Both assertions below are re-evaluated HERE, at publish time, because the
    # gap that bit series v2 was the hours between 'just sync' and this
    # command: four merged PRs were stamped out by a replay that predated
    # them, and once stamped they became ancestors of the snapshot merge, so
    # no later sync ever re-folded them. A publish joins two surfaces that
    # moved independently since the replay was built, and must prove two
    # things about them:
    #
    #   containment  windows holds nothing this replay lacks. This is the
    #                fold-in computation from 'just sync', pointed at the
    #                replay: anything sync would fold in right now is exactly
    #                what this publish would drop.
    #   unreviewed   the replay carries nothing windows has never reviewed.
    #                This command lands on windows without passing pr_gate -
    #                that is its design - so it pays a toll instead: every
    #                commit entering windows that is not a replay of an
    #                already-merged PR and not a patch upstream absorbed is
    #                named, and the publish refuses unless each is explicitly
    #                acknowledged with a reason that lands in history.
    prev="refs/tags/series/v${prev_n}"
    # The snapshot is identified by its shape, not by being newest; see sync.
    mprev=""
    for c in $(git rev-list --min-parents=2 --first-parent refs/remotes/upstream/main..refs/remotes/origin/windows); do
        if git merge-base --is-ancestor "${c}^2" refs/remotes/upstream/main; then
            mprev="$c"
            break
        fi
    done
    if [ -z "$mprev" ]; then
        mprev=$(git rev-parse "${prev}^{commit}")
    fi
    # The same baseline sync validates, re-checked here because publish
    # reads it after its own fetch: a bogus snapshot marker would make the
    # containment check below measure nothing while appearing to pass.
    if ! git merge-base --is-ancestor "$mprev" refs/remotes/origin/windows; then
        echo "REFUSING: the snapshot baseline (${mprev:0:9}) is not on origin/windows."
        exit 1
    fi
    if [ "$(git rev-parse "${mprev}^{tree}")" != "$(git rev-parse "${prev}^{tree}")" ]; then
        echo "REFUSING: the last snapshot on windows (${mprev:0:9}) does not carry the"
        echo "tree of series/v${prev_n}. The published branch and the series diverged."
        exit 1
    fi
    # Containment: windows holds nothing this replay lacks. Pairing is by
    # range-diff, not patch-id, because a fold-in resolved by hand carries a
    # different patch than the PR it folds, and patch-id alone would refuse
    # that publish forever while its own remedy ('re-run just sync') re-folds
    # the same conflict - an impassable release path. range-diff is the one
    # instrument that pairs hand-resolved work; it is what sync-verify
    # already trusts for its dropped-commit check.
    dropped=""
    if [ "$(git rev-list --count "${mprev}..refs/remotes/origin/windows")" -gt 0 ]; then
        if ! rd_windows=$(git range-diff --no-color \
                "${mprev}..refs/remotes/origin/windows" \
                "refs/remotes/upstream/main..HEAD" 2>&1); then
            echo "REFUSING: range-diff could not pair the replay against windows:"
            printf '%s\n' "$rd_windows" | head -3
            exit 1
        fi
        while IFS= read -r line; do
            [ -n "$line" ] || continue
            sha=$(printf '%s' "$line" | awk '{print $2}')
            # Upstream absorbing a fork PR legitimately removes it from the
            # fold set: the patch is already arriving through parent 2.
            if [ "$(git cherry refs/remotes/upstream/main "$sha" "${sha}^" 2>/dev/null | cut -c1)" = "-" ]; then
                continue
            fi
            dropped="${dropped}$(git log -1 --oneline "$sha")"$'\n'
        done <<< "$(printf '%s\n' "$rd_windows" | grep -E '^ *[0-9]+: *[0-9a-f]+ *< ' || true)"
    fi
    if [ -n "$dropped" ]; then
        echo "REFUSING: windows has commits this replay does not carry. Publishing now"
        echo "would stamp a tree without them, and after the stamp they are ancestors"
        echo "of the merge, so no later sync re-folds them. Re-run 'just sync' (it"
        echo "folds these in), verify, then publish again:"
        printf '%s' "$dropped"
        exit 1
    fi
    # Unreviewed: the replay carries nothing windows has never reviewed.
    # Candidates come from patch-id (git cherry over commits newer than the
    # last series tag). Classification then runs, in order: upstream's own
    # commits are skipped (they enter through parent 2 by design); patches
    # upstream absorbed pass (the absorbed mark only fires when upstream
    # carries the patch under a different sha, which is why reachability is
    # checked first); commits whose subject matches a PR windows merged
    # since the last snapshot pass (the pool is RECENT commits only, read
    # into a variable once - piping git log into grep under pipefail flips
    # the test false when git log SIGPIPEs, and matches near the top are
    # exactly the folded-PR case; a full-history pool would let new work
    # hide behind any old subject); and finally commits range-diff pairs
    # with the standing series stack pass, which is what clears a
    # hand-resolved replay of an old patch. Anything left is named.
    recent_subjects=$(git log --format=%s "${mprev}..refs/remotes/origin/windows")
    old_base=$(git merge-base "${prev}^{commit}" refs/remotes/upstream/main 2>/dev/null || true)
    paired=""
    if [ -n "$old_base" ]; then
        # range-diff prints abbreviated shas; resolved to full ones so the
        # membership test below compares like with like.
        paired=$(git range-diff --no-color \
                "${old_base}..${prev}^{commit}" \
                "refs/remotes/upstream/main..HEAD" 2>/dev/null \
            | awk '$3 == "=" || $3 == "!" { print $5 }' \
            | while IFS= read -r p; do git rev-parse "$p"; done \
            || true)
    fi
    unreviewed=""
    reviewed_rewrites=0
    for sha in $(git cherry refs/remotes/origin/windows HEAD "$prev" | awk '$1 == "+" { print $2 }'); do
        if git merge-base --is-ancestor "$sha" refs/remotes/upstream/main 2>/dev/null; then
            continue
        elif [ "$(git cherry refs/remotes/upstream/main "$sha" "${sha}^" 2>/dev/null | cut -c1)" = "-" ]; then
            reviewed_rewrites=$((reviewed_rewrites + 1))
        elif [ -n "$(printf '%s\n' "$recent_subjects" | grep -Fx "$(git log -1 --format=%s "$sha")" || true)" ]; then
            reviewed_rewrites=$((reviewed_rewrites + 1))
        elif [ -n "$(printf '%s\n' "$paired" | grep -Fx "$sha" || true)" ]; then
            reviewed_rewrites=$((reviewed_rewrites + 1))
        else
            unreviewed="${unreviewed}$(git log -1 --oneline "$sha")"$'\n'
        fi
    done
    if [ "$reviewed_rewrites" -gt 0 ]; then
        echo "${reviewed_rewrites} rewritten patch(es) entering windows (each reviewed where it first landed)."
    fi
    if [ -n "$unreviewed" ]; then
        # ack is a fixed token, not freeform prose: just interpolates
        # arguments into the script text verbatim, so a prose reason would
        # be code the recipe executes. The named commits below are the
        # permanent record; the token only says the operator read them.
        if [ '{{ ack }}' = yes ]; then
            echo ""
            echo "ACKNOWLEDGED unreviewed work entering windows; recorded in the merge message:"
            printf '%s' "$unreviewed"
        else
            echo "REFUSING: this publish would carry commits into windows that no PR"
            echo "reviewed (not replays of merged PRs, not absorbed upstream):"
            printf '%s' "$unreviewed"
            echo "Land them through a PR, or acknowledge explicitly after reading them:"
            echo "  just sync-publish yes"
            exit 1
        fi
    fi
    next=$((10#$prev_n + 1))
    base=$(git rev-parse refs/remotes/origin/windows)
    up=$(git rev-parse refs/remotes/upstream/main)
    tree=$(git rev-parse 'HEAD^{tree}')
    msg=(-m "sync: merge upstream $(git rev-parse --short "$up") (series v${next})")
    if [ -n "$unreviewed" ]; then
        msg+=(-m "unreviewed work entering windows, acknowledged at publish:"$'\n'"$(printf '%s' "$unreviewed" | sed 's/^/  /')")
    fi
    m=$(git commit-tree "$tree" -p "$base" -p "$up" "${msg[@]}")
    # By construction, and still checked: tier pins, the next sync's fold-in,
    # and sync-verify all ride on this equality.
    if [ "$(git rev-parse "${m}^{tree}")" != "$tree" ]; then
        echo "BUG: the snapshot merge does not carry the series tree; not pushing."
        exit 1
    fi
    git tag -a "series/v${next}" -m "series v${next} on upstream $(git rev-parse --short "$up")" HEAD
    if ! git push --atomic origin "${m}:refs/heads/windows" "refs/tags/series/v${next}"; then
        git tag -d "series/v${next}" >/dev/null
        echo ""
        echo "Push rejected. If windows moved (a PR merged mid-sync), re-run 'just sync':"
        echo "it folds the new commits into this replay; then verify and publish again."
        exit 1
    fi
    git fetch origin
    # Convenience only; the remote is already right. branch -f refuses while
    # another worktree has windows checked out, and the ancestor check skips a
    # local windows that diverged (a stale pre-cutover generation), so this
    # cannot destroy local state.
    if git rev-parse --verify -q refs/heads/windows >/dev/null \
       && git merge-base --is-ancestor refs/heads/windows "$m" 2>/dev/null \
       && git branch -f windows "$m" 2>/dev/null; then
        echo "local branch 'windows' fast-forwarded to the snapshot"
    else
        echo "local branch 'windows' left alone (missing, diverged, or checked out"
        echo "elsewhere); update it with a fetch and a fast-forward merge where it"
        echo "is checked out."
    fi
    echo "published: windows snapshot $(git rev-parse --short "$m") (series v${next})"

# Drives the real recipes against throwaway git fixtures: bootstrap, a plain
# sync, a fold-in, a raced publish, a conflicted replay, a second clone
# joining mid-series, and a stale leftover replay that must be refused. A few
# minutes; no network, no desktop, safe alongside real work.
#
# Prove the sync flow still refuses, folds, races, and publishes correctly.
sync-selftest:
    @bash .agents/scripts/syncflow-selftest.sh

# Post-rebase gate. Exits non-zero on a finding instead of printing a report
# for a human to skim, because the report this replaced was skimmed and passed
# while the product did not compile.
#
# Checks, roughly cheapest first. `base` is the exception: it makes one network
# round trip, bounded at 15 seconds, and it sits early because everything after
# it is measured against the ref it checks.
#
#
#   markers   a resolution that left <<<<<<< behind in a file no test compiles
#   base      what the replay landed on, how far the ref has moved since, and
#             whether that ref is still what upstream has
#   dropped   commits that replayed empty and silently left the stack
#   surface   a file the fork used to change and now does not, or the reverse
#   fmt       a NEW zig fmt offender under src/; the fork carries pre-existing
#             ones, so a bare `zig fmt --check` is mostly noise and gets
#             ignored. Narrower than upstream CI, which checks the whole tree
#   build     fork code compiled against upstream code the fork never edits
#
# `build` is the leg that matters most and the one a test run will not give
# you. Zig analyses lazily, so `zig build test` never reaches code no test
# instantiates. A fork call into an upstream function whose signature moved,
# or new upstream code switching over an enum the fork extended, both fail to
# compile here while the entire test suite stays green. Neither one produces a
# merge conflict, because only one side edited the text: that is exactly why
# finishing the rebase without conflicts proves so little.
#
# Nothing here fetches, and `base` is why that needs saying. Every check in this
# gate is measured against refs/remotes/upstream/main, and a gate that moves its
# own yardstick mid-run is measuring nothing. Fetching upstream moves the
# ref past what the replay targeted and hides, rather than reports, the one
# thing `base` exists to catch. Fetching origin can change which series tag
# counts as the latest generation, and on the tracking-ref fallback path it
# moves the pre-rebase tip itself. So `base` asks the remote what it has with
# `git ls-remote`, which writes no ref, and says out loud when it could not ask
# instead of passing quietly.
#
# `base` answers with commit counts and with upstream's own commit dates, not
# with local timestamps. An earlier version of this leg dated the ref from its
# reflog and the replay from the fork's committer dates, and both are the wrong
# clock: a fetch that brings nothing new writes no reflog entry, so a correct
# sync across a quiet weekend upstream looked stale, while any later fetch made
# a months-old replay look current. Commit counts do not have that problem, and
# the committer date of upstream's own tip travels with the object, needs no
# reflog, and survives a rebase that rewrites every date on the fork side.
#
# Four of these compare against the pre-rebase series: dropped commits, file
# surface, range-diff, and the fmt baseline. On series-wip that is the latest
# series/v* tag, which is durable: publishing does not destroy it, it makes
# the comparison moot, because sync-publish tags the replay as the new latest
# and the four then announce themselves as skipped rather than passing
# silently. On any other branch the branch's own tracking ref plays the role,
# which it can only do until a push replaces it.
#
# Exit codes: 0 clean, 1 a finding, 2 bad arguments. Items printed as REVIEW
# exit 0 on purpose, because an upstream rename moves the file surface
# legitimately and a check that cries wolf gets passed a flag instead of read.
#
# `just sync-verify fast` stops before the build ladder.
#
# Gate the replay before publishing.
sync-verify mode="":
    #!/usr/bin/env bash
    set -euo pipefail

    # No `offline` mode. The freshness leg detects an unreachable remote on its
    # own and reports the skip, so the mode would only save the timeout; and to
    # be usable it would have to combine with `fast`, which one positional
    # argument cannot express without growing a parser.
    case "{{ mode }}" in
        ""|fast) ;;
        *) echo "unknown mode '{{ mode }}'; use 'just sync-verify' or 'just sync-verify fast'"; exit 2 ;;
    esac

    # Deliberately inside the work tree. zig is a native Windows binary and
    # cannot resolve an msys path, so a `mktemp -d` directory would make every
    # fmt lookup below fail with FileNotFound under git-bash. The clean-tree
    # check below ignores this one path because we own it.
    tmpd=".sync-verify-tmp"
    rm -rf "$tmpd"
    trap 'rm -rf "$tmpd"' EXIT
    fail=0
    review=0
    note() { printf '%s\n' "$*"; }
    bad() { printf 'FAIL: %s\n' "$*"; fail=1; }
    look() { printf 'REVIEW: %s\n' "$*"; review=$((review + 1)); }

    git_dir=$(git rev-parse --git-dir)
    if [ -d "$git_dir/rebase-merge" ] || [ -d "$git_dir/rebase-apply" ]; then
        echo "FAIL: a rebase is still in progress; finish or abort it first"
        echo ""
        echo "sync-verify FAILED"
        exit 1
    fi
    if ! git rev-parse --verify -q refs/remotes/upstream/main >/dev/null; then
        echo "FAIL: no refs/remotes/upstream/main. Add the upstream remote and fetch it;"
        echo "without it nothing below can tell a replay from a fresh checkout."
        echo ""
        echo "sync-verify FAILED"
        exit 1
    fi
    # A plain prefix match, because BRE `\?` is GNU-only and a literal on BSD
    # grep; the porcelain line is `?? .sync-verify-tmp/` with or without the
    # trailing slash and nothing else starts with that prefix.
    dirty=$(git status --porcelain | grep -v "^?? ${tmpd}" || true)
    if [ -n "$dirty" ]; then
        bad "working tree is not clean"
    fi
    note "=== conflict markers ==="
    # Only the <<<<<<< and >>>>>>> forms. A bare ======= line is a legitimate
    # markdown heading underline, and matching it produces pure noise.
    markers=$(git grep -n -I -E '^(<{7}|>{7}) ' -- . || true)
    if [ -n "$markers" ]; then
        bad "conflict markers survived a resolution:"
        printf '%s\n' "$markers" | head -20 || true
    else
        note "  none"
    fi

    # Everything from here down measures the fork against
    # refs/remotes/upstream/main, and until this leg runs, nothing has
    # established what that ref is worth. It is usually the current upstream
    # when this runs straight after `just sync`, because sync fetches. Run
    # standalone a week later it is not, and every check below then reports
    # honestly about the wrong upstream.
    #
    # `git ls-remote` and not `git fetch` on purpose: see the header. A fetch
    # would move the very refs the rest of this run is comparing against.
    note "=== upstream base ==="
    # What the replay landed on, and how far the ref has moved since. Both are
    # local questions and are answered even when the remote cannot be reached.
    #
    # merge-base is guarded because `--is-ancestor` returning non-zero does not
    # only mean "not an ancestor", it also means "no common ancestor at all",
    # and on that second path an unguarded command substitution takes the whole
    # recipe down through set -e with no verdict printed.
    replay_base=$(git merge-base refs/remotes/upstream/main HEAD 2>/dev/null || true)
    if [ -z "$replay_base" ]; then
        bad "HEAD and upstream/main share no history; this is not a replay of this fork"
        note "  Nothing below can mean anything measured against an unrelated history,"
        note "  so the rest is skipped rather than run on a base that does not exist."
        echo ""
        echo "sync-verify FAILED"
        exit 1
    else
        behind=$(git rev-list --count "${replay_base}..refs/remotes/upstream/main")

        # Asking the remote what it has. Nothing here fetches, so this is
        # ls-remote, which writes no ref.
        #
        # `timeout` is GNU coreutils. git-bash and Linux have it; a stock macOS
        # does not, and because the redirection below is applied before bash
        # reports an unknown command, a missing `timeout` would otherwise be
        # swallowed and reported as an unreachable remote. So it is used only
        # when it is there. The ssh command this recipe builds carries its own
        # ConnectTimeout so the one platform without `timeout` still has a bound;
        # a user who configured their own transport keeps it, and on that branch
        # the only bound is whatever their command and `timeout` provide.
        #
        # GIT_SSH_COMMAND is set only when the user has configured no transport
        # of their own, and setting it empty is NOT the neutral thing it looks
        # like: git reads the environment ahead of core.sshCommand, so an empty
        # value wins and then fails to spawn. That turns this whole leg into a
        # permanent SKIP for anyone on plink, a jump host or a per-repo key,
        # with the reason discarded by the redirection.
        ls_rc=0
        ls_out=""
        have_remote=1
        if ! git remote get-url upstream >/dev/null 2>&1; then
            have_remote=0
        else
            runner=""
            if command -v timeout >/dev/null 2>&1; then runner="timeout 15"; fi
            if [ -n "${GIT_SSH_COMMAND:-}" ] || [ -n "$(git config --get core.sshCommand || true)" ]; then
                ls_out=$(GIT_TERMINAL_PROMPT=0 $runner git ls-remote upstream main 2>/dev/null) || ls_rc=$?
            else
                ls_out=$(GIT_TERMINAL_PROMPT=0 GIT_SSH_COMMAND="ssh -o BatchMode=yes -o ConnectTimeout=10" $runner git ls-remote upstream main 2>/dev/null) || ls_rc=$?
            fi
        fi

        # ls-remote prints the sha, a tab, then the ref. Match the branch
        # exactly: a tag named main, and its peeled form, both answer this too.
        remote_up=$(printf '%s\n' "$ls_out" | awk '$2 == "refs/heads/main" { print $1; exit }')
        local_up=$(git rev-parse refs/remotes/upstream/main)

        # How old the ref is, measured by the committer date of upstream's own
        # tip commit. That date is written upstream and travels with the object,
        # so it needs no reflog and survives a rebase that rewrites every date
        # on the fork side. Worked out on every path, including the ones that
        # never reach the remote: a ref nothing can refresh is the state where
        # its age matters most.
        #
        # Validated rather than inlined into the arithmetic. An empty or
        # non-numeric operand inside an arithmetic expansion aborts the entire
        # recipe under set -e, and it would do so after the verdict block, so
        # the run would end with no verdict at all.
        tip_ct=$(git log -1 --format=%ct refs/remotes/upstream/main 2>/dev/null || true)
        now_ct=$(date +%s 2>/dev/null || true)
        case "$tip_ct" in ""|*[!0-9]*) tip_ct="" ;; esac
        case "$now_ct" in ""|*[!0-9]*) now_ct="" ;; esac
        tip_age_days=""
        if [ -n "$tip_ct" ] && [ -n "$now_ct" ]; then
            tip_age_days=$(( (now_ct - tip_ct) / 86400 ))
            # A clock skewed forward, or a future-dated commit upstream, would
            # otherwise report a negative age and quietly disable the finding
            # below for as long as that commit is the tip.
            if [ "$tip_age_days" -lt 0 ]; then tip_age_days=0; fi
        fi
        stale=0
        if [ -n "$tip_age_days" ] && [ "$tip_age_days" -ge 7 ]; then stale=1; fi

        if [ "$have_remote" -eq 0 ]; then
            look "no remote named 'upstream', though refs/remotes/upstream/main is here:"
            note "    Nothing can refresh that ref, so every check below reads wherever it"
            note "    was frozen${tip_age_days:+, and its tip is ${tip_age_days} day(s) old}."
            note "    Neither 'git remote remove' nor 'git remote rename' leaves a ref in"
            note "    this state; both take the tracking refs with them. It comes from"
            note "    fetching a URL straight into refs/remotes, or from an edited config."
            if [ "$stale" -eq 1 ]; then
                bad "refs/remotes/upstream/main is ${tip_age_days} day(s) old and nothing can refresh it"
            fi
        elif [ "$ls_rc" -ne 0 ]; then
            # look, not note. Verifying offline is legitimate, but a bare note
            # leaves the run ending in an unqualified PASSED, which reads as
            # "the ref was checked" when it is the one thing that was not.
            look "the upstream remote did not answer (rc=${ls_rc}), so the ref is unverified:"
            note "    Not a finding about the replay. It does mean a clean run below says"
            note "    the fork agrees with the upstream you last fetched, and says nothing"
            note "    about the one that exists now${tip_age_days:+; that fetch left a tip ${tip_age_days} day(s) old}."
            if [ "$stale" -eq 1 ]; then
                bad "refs/remotes/upstream/main is ${tip_age_days} day(s) old and could not be checked against the remote"
            fi
        elif [ -z "$remote_up" ]; then
            look "the upstream remote answered with no refs/heads/main:"
            note "    Either the branch is gone or 'upstream' points somewhere unexpected."
            note "    Check 'git remote get-url upstream'."
        elif [ "$remote_up" = "$local_up" ]; then
            note "  refs/remotes/upstream/main is the remote tip (${local_up:0:9})"
        else
            note "    local  upstream/main:   ${local_up:0:9}${tip_age_days:+, tip is ${tip_age_days} day(s) old}"
            note "    remote refs/heads/main: ${remote_up:0:9}"
            # Differing is not the same as being behind, and a bare inequality
            # cannot tell them apart. If upstream rewound or force-pushed main,
            # the local ref is AHEAD of the remote and the fork was replayed
            # onto commits upstream no longer has, which is worse than being out
            # of date and would otherwise be reported as upstream moving on.
            # Whenever that is what happened the remote commit is already in the
            # local object database, so telling them apart costs no fetch.
            if git cat-file -e "${remote_up}^{commit}" 2>/dev/null && git merge-base --is-ancestor "$remote_up" "$local_up" 2>/dev/null; then
                bad "refs/remotes/upstream/main is ahead of the remote; upstream rewound or was repointed"
                note "    The replay landed on commits the remote no longer has, so the next"
                note "    'just sync' would rebase onto a history that diverged from this one."
            elif [ "$stale" -eq 1 ]; then
                bad "refs/remotes/upstream/main is ${tip_age_days} day(s) stale and every check below reads it"
                note "    Upstream lands commits most days, so at this distance the fork is"
                note "    being compared against an upstream that has substantially moved."
                note "    Re-run 'just sync', which fetches first. Do not fetch and re-run"
                note "    this gate alone: that moves the upstream ref every check below"
                note "    measures against (and, off series-wip, the tracking-ref baseline)."
            else
                note "    Upstream has commits this ref does not. Recent enough that the"
                note "    checks below are still measuring the right thing."
            fi
        fi

        # How far the replay itself is from the ref. After a correct `just sync`
        # this is exactly zero: sync fetches and then rebases onto what it
        # fetched, and nothing moves the ref again in between. Commits upstream
        # pushed while the replay was running are not in this ref and cannot
        # show up here. So any non-zero number means the ref moved by a later
        # fetch and this is not a fresh replay waiting to be pushed, which is
        # the only state the rest of this gate is built for.
        if [ "$behind" -eq 0 ]; then
            note "  the replay landed on refs/remotes/upstream/main"
        else
            look "the replay landed ${behind} commit(s) behind refs/remotes/upstream/main:"
            note "    replay landed on: $(git rev-parse --short "$replay_base")"
            note "    upstream/main is: $(git rev-parse --short refs/remotes/upstream/main)"
            note "    After a correct sync this is zero, because sync fetches and then"
            note "    replays onto what it fetched. Any number here means the ref moved by"
            note "    a later fetch, so this is not a fresh replay waiting to be pushed;"
            note "    the checks below compare against the ref, not against what the"
            note "    replay targeted, so read them with that in mind."
        fi
    fi

    # The pre-rebase baseline. On series-wip it is the latest series/v* tag:
    # the exact series this replay started from, durable across the publish.
    # On any other branch it is whatever that branch tracks, taken from the
    # branch itself rather than hardcoded: on a stacked branch cut before the
    # last push, a hardcoded origin/windows is not an ancestor and every
    # comparison below would then be against the wrong history and fail
    # bogusly.
    baseline=""
    baseline_name=""
    if [ "$(git branch --show-current)" = "series-wip" ]; then
        series_n=$(git tag --list 'series/v*' | sed 's|^series/v||' | grep -E '^[0-9]+$' | sort -n | tail -1 || true)
        if [ -n "$series_n" ]; then
            baseline_name="series/v${series_n}"
            # Peeled to the commit: the tag object itself satisfies ref
            # lookups but not the `${baseline}:${f}` file lookups the fmt leg
            # does below.
            t=$(git rev-parse "refs/tags/series/v${series_n}^{commit}")
            if ! git merge-base --is-ancestor "$t" HEAD; then
                baseline="$t"
            fi
        else
            baseline_name="no series/v* tag"
        fi
    else
        tracked=$(git rev-parse --symbolic-full-name '@{u}' 2>/dev/null || echo "")
        baseline_name="${tracked:-no tracking ref}"
        if [ -n "$tracked" ] && ! git merge-base --is-ancestor "$tracked" HEAD; then
            baseline="$tracked"
        fi
    fi

    if [ -z "$baseline" ]; then
        note "=== dropped commits / file surface / range-diff / fmt baseline ==="
        # look, not note. These four are a third of what this gate checks, and a
        # bare note leaves the run ending in an unqualified PASSED that reads as
        # though they ran.
        look "there is no pre-rebase baseline, so four checks did not run:"
        note "    ${baseline_name} is at or behind HEAD, so there is no older series to"
        note "    compare the replay against. These four only mean something between"
        note "    the replay and its publish."
    else
        # Guarded for the same reason as replay_base above: git merge-base
        # exits non-zero when two histories have no common ancestor, and an
        # unguarded command substitution turns that into a silent set -e abort
        # with no verdict.
        old_base=$(git merge-base "$baseline" refs/remotes/upstream/main 2>/dev/null || true)
        if [ -z "$old_base" ]; then
            bad "${baseline_name} and upstream/main share no common ancestor"
            note "  The four checks that compare against the pre-rebase tip cannot run."
            echo ""
            echo "sync-verify FAILED"
            exit 1
        fi

        # One range-diff drives both checks below. It pairs each pre-rebase
        # commit with its replayed twin, which is the only thing that can tell
        # "this commit was rewritten" from "this commit is gone". Counting
        # commits misses a swap; comparing patch-ids flags every hand-resolved
        # commit as missing, because resolving changes the patch.
        rd=""
        rd_ok=1
        if ! rd=$(git range-diff --no-color "${old_base}..${baseline}" refs/remotes/upstream/main..HEAD 2>&1); then
            rd_ok=0
            bad "range-diff failed: $(printf '%s' "$rd" | head -2)"
        fi

        note "=== dropped commits ==="
        note "  $(git rev-list --count "${old_base}..${baseline}") before, $(git rev-list --count refs/remotes/upstream/main..HEAD) after"
        if [ "$rd_ok" -eq 1 ]; then
            dropped=$(printf '%s\n' "$rd" | grep -E '^ *[0-9]+: *[0-9a-f]+ *< ' || true)
            if [ -z "$dropped" ]; then
                note "  none"
            else
                while IFS= read -r line; do
                    [ -n "$line" ] || continue
                    sha=$(printf '%s' "$line" | awk '{print $2}')
                    # Upstream absorbing a fork patch drops it from the stack
                    # legitimately, and that is the expected end state for
                    # anything sent upstream. `git cherry` marks a commit `-`
                    # when an equivalent patch already exists there.
                    if [ "$(git cherry refs/remotes/upstream/main "$sha" "${sha}^" 2>/dev/null | cut -c1)" = "-" ]; then
                        note "  absorbed upstream: $(git log -1 --oneline "$sha")"
                    else
                        bad "commit left the stack and is not upstream: $(git log -1 --oneline "$sha")"
                    fi
                done <<< "$dropped"
            fi
        fi

        # A set comparison, not a stat diff: a file the fork stops touching is
        # how a dropped hunk shows up when the commit itself survived.
        note "=== file surface ==="
        gone=$(comm -23 <(git diff --name-only "$old_base" "$baseline" | sort) \
                        <(git diff --name-only refs/remotes/upstream/main HEAD | sort))
        added=$(comm -13 <(git diff --name-only "$old_base" "$baseline" | sort) \
                         <(git diff --name-only refs/remotes/upstream/main HEAD | sort))
        if [ -n "$gone" ] || [ -n "$added" ]; then
            # Not fatal: an upstream rename legitimately moves the surface, and
            # that is common enough that failing here would train you to pass
            # the flag. Read these, do not skim them.
            look "the fork touches a different set of files than before:"
            if [ -n "$gone" ]; then
                printf '%s\n' "$gone" | sed 's/^/    no longer changed: /' | head -20 || true
            fi
            if [ -n "$added" ]; then
                printf '%s\n' "$added" | sed 's/^/    newly changed:     /' | head -20 || true
            fi
        else
            note "  unchanged"
        fi

        note "=== commits whose content changed in the replay ==="
        note "  (the ones you resolved by hand, plus any the replay shifted;"
        note "   read anything here you do not recognise)"
        if [ "$rd_ok" -eq 1 ]; then
            changed=$(printf '%s\n' "$rd" | grep -E '^ *[0-9]+: *[0-9a-f]+ *!' || true)
            if [ -z "$changed" ]; then
                note "  none"
            else
                note "  $(printf '%s\n' "$changed" | wc -l | tr -d ' ') commit(s), showing at most 20:"
                printf '%s\n' "$changed" | head -20 || true
            fi
        fi
    fi

    note "=== zig fmt (new offenders only) ==="
    if ! command -v zig >/dev/null 2>&1; then
        look "zig is not on PATH, so the fmt check did not run"
    else
        rm -rf "$tmpd"; mkdir -p "$tmpd"
        # zig reports paths in the platform separator; git only understands
        # forward slashes, so every lookup below would miss on Windows.
        offenders=$(zig fmt --check src 2>/dev/null | tr '\\' '/' || true)
        if [ -z "$offenders" ]; then
            note "  none"
        elif [ -z "$baseline" ]; then
            # Without the pre-rebase tip there is nothing to subtract the known
            # offenders against. Failing here would flag a standing condition of
            # the fork as if this replay caused it, which is how a check earns
            # its way onto the ignore list.
            look "cannot tell new zig fmt offenders from pre-existing ones without the pre-rebase tip:"
            printf '%s\n' "$offenders" | sed 's/^/    /' | head -20 || true
        else
            known=0
            while IFS= read -r f; do
                [ -n "$f" ] || continue
                if git cat-file -e "${baseline}:${f}" 2>/dev/null; then
                    # Pre-existing offenders are not this replay's problem.
                    # Compare each against the same file before the replay.
                    git show "${baseline}:${f}" > "$tmpd/base.zig"
                    rc=0
                    zig fmt --check "$tmpd/base.zig" >/dev/null 2>&1 || rc=$?
                    case "$rc" in
                        0) bad "zig fmt: ${f} is newly unformatted" ;;
                        1) known=$((known + 1)) ;;
                        # Anything else means zig could not read or parse the
                        # old copy at all, which is not evidence either way.
                        *) look "zig fmt could not read ${f} at the pre-rebase tip (rc=${rc}); check it by hand" ;;
                    esac
                else
                    # A file the fork adds in this replay has no excuse.
                    bad "zig fmt: ${f} is unformatted"
                fi
            done <<< "$offenders"
            note "  ${known} pre-existing offender(s) carried over and ignored"
        fi
    fi

    # Stop before the ladder if anything already failed. The ladder is 20+
    # minutes and cannot tell you anything the findings above have not.
    if [ "$fail" -ne 0 ]; then
        echo ""
        echo "sync-verify FAILED (build ladder skipped; fix the above first)"
        exit 1
    fi

    note "=== build ladder ==="
    if [ "{{ mode }}" = "fast" ]; then
        note "  SKIP (fast)"
    else
        # build-dll goes first: it is the cheapest leg that compiles fork code
        # against upstream code, so it fails fastest on the exact breakage the
        # test suite cannot see.
        legs="build-dll test"
        case "$(uname -s)" in
            MINGW*|MSYS*|CYGWIN*) legs="$legs build-win test-win" ;;
            *) note "  not Windows: skipping the C# legs" ;;
        esac
        for leg in $legs; do
            note "--- just ${leg} ---"
            # Route a failure through bad() so the verdict below still prints;
            # letting set -e abort here makes a gate failure look like a plain
            # build error.
            # Quoted. This expands to a Windows path with backslash
            # separators, and unquoted bash reads each one as an escape and
            # eats it, so the leg dies as "command not found" and reports as
            # a build failure that never built anything.
            if ! "{{ just_executable() }}" "$leg"; then
                bad "build ladder: just ${leg} failed"
                break
            fi
        done
    fi

    echo ""
    if [ "$fail" -ne 0 ]; then
        echo "sync-verify FAILED"
        exit 1
    fi
    if [ "$review" -gt 0 ]; then
        echo "sync-verify PASSED with ${review} item(s) marked REVIEW above."
        echo "Those are not automatically wrong, but nothing checked them for you."
    else
        echo "sync-verify PASSED"
    fi
    # Only where publish would not refuse; on any other branch the hint
    # would advertise a command whose first guard rejects it.
    if [ "$(git branch --show-current)" = "series-wip" ]; then
        echo "Publish with:"
        echo "  just sync-publish"
    fi

# ── shader gallery ─────────────────────────────────────────────────────────
# Compile + render every bundled gallery shader (windows/Ghostty/Assets/Shaders)
# through the real shipped pipeline: zioshade HLSL (local) -> DXC DXIL -> D3D12
# WARP render on the Windows box, fetching PPM preview frames back. A FAIL is a
# broken gallery entry. See tools/gallery/verify.sh for the env knobs.
gallery-verify:
    @bash tools/gallery/verify.sh

# Parse every bundled gallery shader with glslang, prefixed exactly the way the
# renderer assembles it (shadertoy_prefix.glsl + the shader).
#
# This is gallery-verify's reference leg with everything unhermetic removed: no
# zioshade build, no spirv-cross, no Windows box, so CI can run it on every
# push and a contributor can run it in a second. What it buys is the class of
# break gallery-verify would otherwise find late and by hand: `active` was a
# GLSL reserved word sitting in two shipped shaders, and `filter`, `input`,
# `output`, `union`, `resource` and the rest of GLSL 4.60 section 3.6 are still
# accepted by the renderer's own lenient frontend. Leniency there is correct
# for shaders users load from the wild; the shaders WE ship have to be
# acceptable to a spec-conforming compiler, because gallery-verify's
# independent reference path compiles them with one.
#
# Deliberately the same tool and flags as verify.sh step 2, so a shader that
# passes here cannot fail there for a parse reason.
gallery-lint:
    #!/usr/bin/env bash
    set -euo pipefail
    glslang=${GLSLANG:-glslang}
    command -v "$glslang" >/dev/null || {
        echo "glslang not found (nix develop, or brew install glslang)" >&2
        exit 2
    }
    prefix=src/renderer/shaders/shadertoy_prefix.glsl
    prefix_lines=$(wc -l < "$prefix" | tr -d ' ')
    stage=$(mktemp -d "${TMPDIR:-/tmp}/gallery_lint.XXXXXX")
    trap 'rm -rf "$stage"' EXIT
    pass=0
    failed=""
    for glsl in windows/Ghostty/Assets/Shaders/*.glsl; do
        name=$(basename "$glsl" .glsl)
        cat "$prefix" "$glsl" > "$stage/$name.full.glsl"
        if "$glslang" -V -S frag "$stage/$name.full.glsl" -o "$stage/$name.spv" \
                > "$stage/$name.log" 2>&1; then
            pass=$((pass + 1))
        else
            echo "FAIL $glsl"
            # Point the diagnostics back at a file that exists, and say what
            # the line numbers are relative to: they are counted in the
            # concatenation, whose first $prefix_lines lines are the prefix.
            sed -e "s|$stage/$name.full.glsl|$glsl|g" "$stage/$name.log" | sed '/^$/d'
            echo "  (line numbers are in ${prefix} + ${glsl}; the prefix is ${prefix_lines} lines)"
            failed="$failed $name"
        fi
    done
    if [ -n "$failed" ]; then
        echo "glslang: $pass pass, $(echo $failed | wc -w | tr -d ' ') fail:$failed"
        echo "A gallery shader must be accepted by glslang verbatim, which among other"
        echo "things means no GLSL reserved word (section 3.6) used as an identifier."
        exit 1
    fi
    echo "glslang: $pass pass, 0 fail"

# ── quality control ────────────────────────────────────────────────────────
# These recipes need Python 3 on PATH as `python`. The signoff ladder
# includes test-win, so signoff is a Windows-host recipe like the rest of
# the win targets above.
#
# Run the full local test ladder (zig fmt check, zig tests, Windows tests)
# and record a signoff for the current HEAD. The pr-gate merge hook requires
# a green signoff for a PR's head commit before a merge is allowed, so local
# runners are the merge authority while CI is unavailable.
#
# A leg whose inputs (its source trees, its recipes, its toolchain) already
# have a green, observed by an earlier run in any worktree of this repo, is
# carried into the record instead of run; the line says which commit that
# green was observed at. `just leg-cache plan` shows the digests.
signoff:
    python .agents/scripts/signoff.py

# Show which legs a signoff would run for the current branch, and why,
# and which of them would be carried from an earlier green.
signoff-plan:
    python .agents/scripts/signoff.py --plan

# Run every leg regardless of what changed, and settle any deferred debt.
# Legs still carry here; the debt settles only when HEAD contains
# origin/windows (where the deferred merges live) and no carried green
# was asserted rather than observed. For a full ladder that must execute
# everything, see signoff-fresh.
signoff-full:
    python .agents/scripts/signoff.py --full

# Scoped like signoff, but nothing is carried: every selected leg runs,
# and what it observes is recorded over the green it did not trust. For
# a leg you suspect is flaky, or a green you no longer believe. Add
# --full through the script for the fresh full ladder:
#   python .agents/scripts/signoff.py --full --no-cache
signoff-fresh:
    python .agents/scripts/signoff.py --no-cache

# The content-keyed green store behind signoff's carrying: `plan` (which
# leg would carry, which input moved), `check` (exit 1 while any leg
# would run), `snapshot FILE` (the toolchain identity, taken before a
# long run), `record LEG --from-sha HEAD [--env-snapshot FILE]` (assert a
# green you saw at exactly HEAD), `gc`. The args cross pwsh unquoted, so
# a path with a space needs its own quotes inside: '"C:\a b\x.json"'.
leg-cache *args:
    python .agents/scripts/leg_cache.py {{args}}

# Merge without running the legs, on the record. For batching a run of small
# PRs behind one later ladder: the motivation is stored, the debt is capped,
# and only a green signoff-full clears it.
signoff-defer reason:
    python .agents/scripts/signoff.py --defer {{quote(reason)}}

# What is currently merged on credit.
signoff-debt:
    python .agents/scripts/signoff.py --debt

# Re-advertise an already-recorded signoff as a GitHub status, without
# running anything. For the run-then-push order: the automatic post fails
# harmlessly when the head SHA is not on GitHub yet, and this closes the
# gap after the push. Takes the record's sha (full or unique prefix).
signoff-post sha:
    python .agents/scripts/signoff.py --post {{sha}}

# Validate a PR against the merge quality gate without merging.
pr-gate pr:
    python .agents/scripts/pr_gate.py --check-pr {{pr}}

# Squash-merge a PR through the merge guard: the record is validated, the
# delta between its base and origin/windows is measured, and a moved
# window is REFUSED with the fix named (rebase, `just signoff` again, which
# carries every leg the rebase did not touch, merge). Raw `gh pr merge` of a
# moved-window PR is denied by the pr_gate hook the same way. The owner's
# override `--carry-risk` merges on the old green anyway and files the
# resignoff-required issue with the delta (#969's ceremony). `--dry-run`
# shows the inputs, delta, risks and the would-be verdict without touching
# anything: python .agents/scripts/merge_guard.py --dry-run <pr>.
merge-checked pr *args:
    python .agents/scripts/merge_guard.py {{pr}} {{args}}

# Work the resignoff-required pile the merge guard files (#969 phase 2): an
# operator loop, not a merge step. Each invocation spends at most --max
# signoff runs (default 1, an hour of lane time each) on the open issues,
# newest window first, closing the ones a green record retires and
# bisecting the recorded squash SHAs to a culprit when a window goes red.
# --max 0 is the greens-only pass: close what the records already retire,
# spend nothing. An agent merging a PR never runs this; the pile is
# designed to sit until it is worked. --dry-run prints the decisions and
# exact commands and touches nothing.
resignoff-bot *args:
    python .agents/scripts/resignoff_bot.py {{args}}

# Check that everything the gates depend on is present and wired: tools on
# PATH, scripts where the hooks point, settings parseable, nightly task
# registration. A SessionStart hook runs the fast subset automatically.
doctor:
    python .agents/scripts/doctor.py

# Runs real `git describe` against a throwaway repo, over ten tag layouts:
# `series/vN`, a namespace renamed to a `v` name, plain non-release names, a
# release tag on an ancestor rather than on HEAD, real releases, and `tip`.
# The argument list is read out of GitVersion.zig rather than copied here,
# because a copy keeps passing after the source stops matching it. Three
# broken argument lists must also be caught, so the check cannot silently
# stop checking. About eight seconds, no build, no desktop.
#
# Why it exists: `sync-publish` tags every published snapshot `series/vN` and
# Config.init panics on a tag that is neither `tip` nor `vX.Y.Z`, so the
# version lookup filters with `--match v* --match tip --exclude */*`. The
# subtlety is that `--match` does not stop at a slash: `v*` rejects
# `series/v2` only because that name starts with `s`, and lets `vendor/v2`
# through. `--exclude */*` is what carries the namespace rule, and dropping
# it turns a release build into a pre-release version string silently.
#
# Deliberately not gated to Windows and deliberately not using the
# `exit ($LASTEXITCODE ?? 1)` idiom the fuzz recipes use: `gates-selftest`
# depends on this, and that idiom is a syntax error under sh. A gate needs
# pass or fail, not the fuzz suite's finding-versus-broken-harness split.
#
# Prove a tag outside the release namespace cannot name a version.
gitversion-selftest:
    pwsh -NoProfile -File .agents/scripts/gitversion_selftest.ps1

# Recorded-PR replays, matcher escapes, exemption anchoring, the merge
# guard's refusal matrix and golden issue body, and the nightly scripts'
# helpers roundtripping. `gitversion-selftest` runs first.
#
# Prove the gates still catch what they exist for.
gates-selftest: gitversion-selftest
    python .agents/scripts/pr_gate.py --self-test
    python .agents/scripts/signoff.py --self-test
    python .agents/scripts/merge_guard.py --self-test
    python .agents/scripts/resignoff_bot.py --self-test
    python .agents/scripts/workspace_guard.py --self-test
    python .agents/scripts/doctor.py --self-test
    python .agents/scripts/test_reachability.py --self-test
    python .agents/scripts/leg_cache.py --self-test
    pwsh -NoProfile -File .agents/scripts/nightly_fuzz.ps1 -SelfTest
    pwsh -NoProfile -File .agents/scripts/nightly_control.ps1 -SelfTest

# Prove the shipping-build gate refuses a leak in a real Release evaluation (#929)
[windows]
release-gate-check:
    pwsh -NoProfile -File .agents/scripts/release_gate_check.ps1
