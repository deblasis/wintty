# Ghostty Windows Fork - Build Orchestration
# Run `just` for the default (full test + build), or `just <recipe>` for individual steps.

# Cross-platform shell selection.
#
# On unix the default `sh` is fine and most recipes are single program
# invocations (zig build, dotnet build) that work in any POSIX shell.
#
# On Windows we pin pwsh.exe so users do not need git-bash on PATH for the
# common build/run recipes. The few recipes that genuinely need bash (the
# example test loops, the gallery lint) carry an explicit `#!/usr/bin/env bash`
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
# slower at runtime. Distributed DLLs must not inherit the build host CPU
# (for example AVX-512).
build-dll-release:
    zig build -Dapp-runtime=none -Doptimize=ReleaseFast -Dcpu=baseline

# === WinUI 3 app shell ===

# Build the WinUI 3 app shell (expects ghostty.dll at zig-out/bin/).
# RestoreLockedMode (#1085): restore exactly what the committed
# packages.lock.json files resolve, so the Windows App SDK component set
# (WinUI, AI, ML, Base, ... all open-floored by the 2.2.0 metapackage)
# cannot move under a fresh restore. A PackageReference edit is refused
# until `dotnet restore` regenerates the lock and it is committed.
[windows]
build-win:
    dotnet build windows/Ghostty.sln /p:Platform=x64 /p:RestoreLockedMode=true

# Recipe body has no shebang so it runs under the platform shell selected by
# `set windows-shell` above (pwsh on Windows). The previous version used a
# bash shebang to `exec` the .exe, which forced git-bash on Windows for no
# reason - launching a Windows .exe works fine from pwsh.

# Build the WinUI 3 app shell in Release. Locked restore, as build-win.
[windows]
build-win-release: build-dll-release
    dotnet build windows/Ghostty.sln /p:Platform=x64 /p:Configuration=Release /p:RestoreLockedMode=true

# Prove the release DLL only carries runtime-dispatched MSVC CRT AVX-512,
# never a ghostty-owned unconditional path. Requires LLVM tools.
[windows]
check-dll-portable:
    pwsh -NoProfile -File windows/scripts/check-baseline-isa.ps1 -Dll zig-out/lib/ghostty.dll -Pdb zig-out/lib/ghostty.pdb

# Build the DLL and the shell under the build lane, then launch it. The
# launch itself is outside any lane on purpose: pwsh returns as soon as a
# GUI process is up, so a lane held here would be released while the
# window is still open. The harnesses' own Assert-NoWinttyFrom is what
# guards their runs against that window, which runs from the Debug exe they
# launch.
#
# run-win is the USER launcher: a human terminal gets the real config by
# default, unchanged. Set ISOLATED_CONFIG=1 in the environment (e.g.
# `$env:ISOLATED_CONFIG='1'; just run-win`) for a fresh random temp config
# root. An agent session (Claude Code sets CLAUDECODE=1 in its children)
# is isolated by default; REAL_CONFIG=1 overrides that, loudly. The modes
# and their precedence live in windows/scripts/run-win-launch.ps1.
[windows]
run-win: (_build-in-lane "run-win" "build-dll" "build-win")
    pwsh -NoProfile -File windows/scripts/run-win-launch.ps1 -ExePath windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe

# Same, optimized on both sides. Startup timings taken from `run-win` are
# Debug timings and are not the ones users see: the C# shell carries no
# optimization and libghostty is a Debug build. Use this before concluding
# anything about how long startup, the launch splash, or a frame takes.
# Same launcher and the same config modes as run-win.
[windows]
run-win-release: (_build-in-lane "run-win-release" "build-dll-release" "build-win-release")
    pwsh -NoProfile -File windows/scripts/run-win-launch.ps1 -ExePath windows/Ghostty/bin/x64/Release/net10.0-windows10.0.19041.0/Wintty.exe

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
    # Lock drift gate (#1085), and it comes first so the leg never reports
    # green off a graph the committed locks do not describe. An UNLOCKED
    # restore is exactly the operation that floated the transitive Windows
    # App SDK to WinUI 2.2.1 under the 2.2.0 pin, so it is run here on
    # purpose: --force-evaluate makes it re-resolve and regenerate every
    # packages.lock.json the way a fresh machine would, even on a warm
    # tree whose incremental restore would otherwise be a no-op, and if
    # that differs from what is committed the leg stops and says so.
    # Locked mode below cannot catch this class of drift on its own,
    # because locked restore replays the committed lock without ever
    # asking what the sources would resolve today.
    dotnet restore windows/Ghostty.sln --force-evaluate
    # The three non-solution helper projects have committed locks too, and
    # nothing in the ladder builds them, so the gate force-evaluates them
    # here or their drift would never be produced and caught (#1087 L3).
    dotnet restore windows/build/RasterizeIcons/RasterizeIcons.csproj --force-evaluate
    dotnet restore windows/scripts/lib/BackdropStage/BackdropStage.csproj --force-evaluate
    dotnet restore windows/scripts/lib/WindowCapture/WindowCapture.csproj --force-evaluate
    # The check is a CONTENT diff against HEAD, not `git status`: NuGet
    # rewrites lock files with CRLF while the repo stores them LF, and
    # status flags that EOL-only rewrite as modified on any fresh
    # checkout, which would false-RED the gate on every CI runner. diff
    # applies the clean filter, so it sees content only. Untracked locks
    # (a lock regenerated for a new project and never committed) are
    # caught separately, and on a clean pass the checkout line below puts
    # the files back byte-for-byte as committed.
    git diff --quiet HEAD -- ':(glob)**/packages.lock.json'; $drift = ($LASTEXITCODE -ne 0); $untracked = @(git ls-files --others --exclude-standard -- ':(glob)**/packages.lock.json'); if ($drift -or $untracked.Count -gt 0) { git --no-pager diff HEAD -- ':(glob)**/packages.lock.json' | Write-Host; if ($untracked) { ('UNTRACKED: ' + ($untracked -join ', ')) | Write-Host }; Write-Error 'packages.lock.json drift: a fresh restore would change a committed lock file. Regenerate with the restores at the top of this recipe (the solution plus the three helper projects), review, and commit the lock with the change that caused it (#1085).'; exit 1 }
    git checkout -- ':(glob)**/packages.lock.json'
    dotnet build windows/Ghostty.sln /p:Platform=x64 /p:RestoreLockedMode=true
    dotnet test windows/Ghostty.Tests/Ghostty.Tests.csproj /p:Platform=x64 /p:RestoreLockedMode=true --blame-hang --blame-hang-timeout 5m
    dotnet test windows/Ghostty.Tests.Windows/Ghostty.Tests.Windows.csproj /p:Platform=x64 /p:RestoreLockedMode=true --blame-hang --blame-hang-timeout 5m
    # IconGen/SplashGen were compiled by the solution line above but executed
    # by nothing until a coverage audit caught it; AnyCPU in the sln, so they
    # take no /p:Platform=x64 unlike the two above.
    dotnet test dist/windows/IconGen.Tests/IconGen.Tests.csproj /p:RestoreLockedMode=true --blame-hang --blame-hang-timeout 5m
    dotnet test dist/windows/SplashGen.Tests/SplashGen.Tests.csproj /p:RestoreLockedMode=true --blame-hang --blame-hang-timeout 5m
    # The helper locks get a locked RESTORE, not a build: nothing in the
    # ladder compiles them (the GUI harness scripts do, unlocked, when used),
    # but a locked restore still refuses a helper lock that is out of sync
    # with its csproj (NU1004), which is the project-side half of the gate.
    dotnet restore windows/build/RasterizeIcons/RasterizeIcons.csproj /p:RestoreLockedMode=true
    dotnet restore windows/scripts/lib/BackdropStage/BackdropStage.csproj /p:RestoreLockedMode=true
    dotnet restore windows/scripts/lib/WindowCapture/WindowCapture.csproj /p:RestoreLockedMode=true

# === Heavy job lanes (AGENTS.md) ===
#
# incoda is looked up on PATH and then in its installer's location, because
# the agent shell this was written under had the latter and not the former.
# This is the expression, not the path: it is evaluated inside each recipe
# body's pwsh, so a `just test` on a Linux host never runs it.
inc := '(Get-Command incoda -ErrorAction SilentlyContinue)?.Source ?? (Join-Path $env:LOCALAPPDATA "Programs\incoda\incoda.exe")'

# Apply the lane configuration AGENTS.md promises (slots, descriptions, the
# --reason requirement, the retired `wintty` key closed) from
# .agents/scripts/lanes.ps1, the one record of it in the repo; only a lane
# that drifted is touched. `just doctor` runs the same script's -Check.
#
# [windows] like every other lane recipe: it writes machine-global state
# through an incoda whose fallback location is under %LOCALAPPDATA%, which
# exists nowhere else. The script's own -SelfTest needs neither and runs
# cross-platform in `gates-selftest`.
[windows]
lanes:
    pwsh -NoProfile -File .agents/scripts/lanes.ps1

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
# windows, so it needs an interactive desktop and no Wintty already running
# from the Debug exe.
# Pass extra args through, e.g. `just splash-race "-SecondaryFeatureOff"`.
[windows]
splash-race args="": _no-wintty-from (_build-in-lane "splash-race" "build-win")
    $inc = {{inc}}; & $inc run --queue wintty-desktop --reason ("splash-race {{replace(args, '"', '""')}}".Trim()) -- just _splash-race-in-lane "{{replace(args, '"', '""')}}"; exit ($LASTEXITCODE ?? 1)

# The desktop phase of `just splash-race`, inside the lane it took.
[windows]
_splash-race-in-lane args="":
    pwsh -NoProfile -File windows/scripts/splash-single-instance-race.ps1 {{args}}; exit ($LASTEXITCODE ?? 1)

# Checked before the builds, not after: dotnet build cannot overwrite a
# Wintty.exe that is running, and the harnesses refuse to start beside an
# instance of the exe they launch, so without this the developer pays a full
# zig + dotnet build only to be told to close a window -- or gets an MSB
# file-in-use error that hides the real reason. Only an instance of EXE
# counts: an installed Wintty or another worktree's build runs from another
# path and locks nothing here, and whether a harness may launch beside it is
# the coexistence guard's call at launch time. Prerequisites run in the order
# listed.
#
# The trailing `exit 0` is load-bearing: `pwsh -Command` returns the success
# of the LAST STATEMENT EXECUTED, and a process lookup that matched nothing
# can leave $? false, so the recipe would fall off its end failed, on a clear
# desktop, with no message.
#
# Treat this as a convenience gate only: the authority is Assert-NoWinttyFrom
# in lib/wintty-process.ps1, which every harness calls for itself.
[windows]
_no-wintty-from exe="windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe":
    . ./windows/scripts/lib/wintty-process.ps1; try { Assert-NoWinttyFrom -ExePath (Join-Path $PWD '{{exe}}') -Context 'This recipe' } catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }; exit 0

# Fuzz in-pane scrollback search against a real oracle: the harness reads the
# terminal's own UIA text document, counts matches itself, and compares every
# needle it sets against that count. Drives the app over the test seam and
# UIA, so it synthesizes no input and never takes the foreground; it still
# needs an interactive desktop, because its highlight check reads pixels.
#
# Exit codes: 0 clean, 2 product findings (see the JSON and shots under
# windows/scripts/search-fuzz/), 1 the harness could not run.
#
# Pass extra args through, e.g. `just search-fuzz "-Seed 99 -Iterations 40"`.
[windows]
search-fuzz args="": _no-wintty-from (_build-in-lane "search-fuzz" "build-dll" "build-win")
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
shader-notice-fuzz args="": _no-wintty-from (_build-in-lane "shader-notice-fuzz" "build-dll" "build-win")
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
frame-style-fuzz args="": _no-wintty-from (_build-in-lane "frame-style-fuzz" "build-dll" "build-win")
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
# The same-exe check runs before the build lane rather than inside the
# desktop lane, like every other harness: the check that matters is the
# coexistence guard Start-SeamSession runs before each launch.
#
# Exit codes: 0 clean, 2 findings, 1 could not run or a surface went unmeasured.
#
# Run the theme matrix (#937) under the incoda lanes against the Debug build.
[windows]
theme-matrix args="": _no-wintty-from (_build-in-lane "theme matrix (#937)" "build-dll" "build-win")
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
fuzz args="": _no-wintty-from (_build-in-lane "fuzz" "build-dll" "build-win")
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
# registration, and where incoda is present, that the heavy job lanes match
# .agents/scripts/lanes.ps1. A SessionStart hook runs the fast subset
# automatically.
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
# Why it exists: the retired replay flow tagged every published snapshot
# `series/vN` (those tags stay on the remote), and Config.init panics on a
# tag that is neither `tip` nor `vX.Y.Z`, so the
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
# guard's refusal matrix and golden issue body, the nightly scripts'
# helpers roundtripping, and the lane table's drift and apply argv.
# `gitversion-selftest` runs first.
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
    pwsh -NoProfile -File .agents/scripts/lanes.ps1 -SelfTest

# Prove the shipping-build gate refuses a leak in a real Release evaluation (#929)
[windows]
release-gate-check:
    pwsh -NoProfile -File .agents/scripts/release_gate_check.ps1
