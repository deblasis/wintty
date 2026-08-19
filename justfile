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

# Run all Zig tests
test: test-lib-vt test-full

# Test libghostty-vt (fastest feedback loop)
test-lib-vt:
    zig build test-lib-vt --summary all

# Full Zig test suite
test-full:
    zig build test -Dapp-runtime=none --summary all

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

# Build the DLL and the shell, then launch it.
[windows]
run-win: build-dll build-win
    ./windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe

# Same, optimized on both sides. Startup timings taken from `run-win` are
# Debug timings and are not the ones users see: the C# shell carries no
# optimization and libghostty is a Debug build. Use this before concluding
# anything about how long startup, the launch splash, or a frame takes.
[windows]
run-win-release: build-dll-release build-win-release
    ./windows/Ghostty/bin/x64/Release/net10.0-windows10.0.19041.0/Wintty.exe

# Run the C# test suites. Ghostty.Tests is pure logic and cross-platform;
# Ghostty.Tests.Windows holds the tests that need real Windows semantics
# (named mutexes, file sharing, the registry).
[windows]
test-win:
    dotnet test windows/Ghostty.Tests/Ghostty.Tests.csproj
    dotnet test windows/Ghostty.Tests.Windows/Ghostty.Tests.Windows.csproj /p:Platform=x64

# Launch two instances a few hundred ms apart and watch for a launch splash
# owned by the one that should be forwarding itself to the other. Opens real
# windows, so it needs an interactive desktop and no Wintty already running.
# Pass extra args through, e.g. `just splash-race "-SecondaryFeatureOff"`.
[windows]
splash-race args="": _no-wintty-running build-win
    pwsh -NoProfile -File windows/scripts/splash-single-instance-race.ps1 {{args}}

# Checked before the builds, not after: the harnesses refuse to run while a
# Wintty is open, and dotnet build cannot overwrite a locked Wintty.exe, so
# without this the developer pays a full zig + dotnet build only to be told
# to close a window -- or gets an MSB file-in-use error that hides the real
# reason. Prerequisites run in the order listed.
[windows]
_no-wintty-running:
    $p = @(Get-Process Wintty -ErrorAction SilentlyContinue); if ($p.Count -gt 0) { Write-Host ("close the running Wintty first (pid: " + ($p.Id -join ', ') + ")") -ForegroundColor Red; exit 1 }

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
search-fuzz args="": _no-wintty-running build-dll build-win
    pwsh -NoProfile -File windows/scripts/search-fuzz.ps1 \
        -ExePath windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe \
        -OutDir windows/scripts/search-fuzz {{args}}

# Real windows and real input, so it needs an interactive desktop and holds
# the foreground for the duration - about 40 minutes budgeted for everything,
# 5 for `-Tag smoke` (measured 3). Each harness is killed if it overruns its
# budget, so a wedged one cannot hold the desktop indefinitely.
#
# Exit codes: 0 clean, 2 product findings, 1 one or more harnesses could not
# run (so their area is untested, not proven good).
#
# Args pass through, e.g. `just fuzz "-Tag smoke"` or `just fuzz "-Only search"`.
#
# Run every GUI fuzz harness against the Debug build.
[windows]
fuzz args="": _no-wintty-running build-dll build-win
    pwsh -NoProfile -File windows/scripts/fuzz-suite.ps1 \
        -ExePath windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe {{args}}

# No build, no desktop.
#
# List the suite: what it runs, what each harness catches, what it costs.
[windows]
fuzz-list:
    pwsh -NoProfile -File windows/scripts/fuzz-suite.ps1 -List

# Runs the suite runner against fixtures that exit 0, 1, 2 and 3 on purpose,
# plus ones that throw, hang, and fail once then work. About a minute, no
# build, no desktop, and safe to run with Wintty open.
#
# Prove the suite still tells a product finding from a harness that broke.
[windows]
fuzz-selftest:
    pwsh -NoProfile -File windows/scripts/fuzz-suite.ps1 -SelfTest

# === Upstream Sync ===

# Pinned to bash via shebang so the POSIX `[` branch test below works
# regardless of the platform shell. On Windows this requires git-bash on
# PATH; sync is a maintainer command and the maintainer has it.

# Fetch upstream and rebase windows branch.
sync force="":
    #!/usr/bin/env bash
    set -e
    if [ "{{ force }}" != "--force" ] && [ "$(git branch --show-current)" != "windows" ]; then
        echo "WARNING: you are on '$(git branch --show-current)', not 'windows'. Switch to windows branch first. Use 'just sync --force' to override."
        exit 1
    fi
    git fetch upstream
    git rebase upstream/main
    echo "Rebase complete. Run 'just sync-verify' before pushing:"
    echo "  git push --force-with-lease origin windows"

# Post-rebase sanity check: show what this branch changes relative to
# upstream/main so an accidental revert or a dropped commit is visible
# before the force-push. Read the file list - anything outside the
# expected fork surface is a red flag.
sync-verify:
    #!/usr/bin/env bash
    set -e
    echo "=== commits unique to this branch ==="
    git log --oneline upstream/main..HEAD | head -60
    echo ""
    echo "=== files changed vs upstream/main ==="
    git diff --stat upstream/main HEAD | tail -30
