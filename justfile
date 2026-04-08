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

# === WinUI 3 app shell ===

# Build the WinUI 3 app shell (expects ghostty.dll at zig-out/bin/).
[windows]
build-win:
    dotnet build windows/Ghostty/Ghostty.sln /p:Platform=x64

# Recipe body has no shebang so it runs under the platform shell selected by
# `set windows-shell` above (pwsh on Windows). The previous version used a
# bash shebang to `exec` the .exe, which forced git-bash on Windows for no
# reason - launching a Windows .exe works fine from pwsh.

# Recipe body has no shebang so it runs under the platform shell selected by
# `set windows-shell` above (pwsh on Windows). The previous version used a
# bash shebang to `exec` the .exe, which forced git-bash on Windows for no
# reason - launching a Windows .exe works fine from pwsh.

# Build the DLL and the shell, then launch it.
[windows]
run-win: build-dll build-win
    ./windows/Ghostty/bin/x64/Debug/net9.0-windows10.0.19041.0/Ghostty.exe

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
    echo "Rebase complete. Review any conflicts, then: git push --force-with-lease origin windows"
