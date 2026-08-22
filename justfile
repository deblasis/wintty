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
search-fuzz args="": _no-wintty-running build-dll build-win
    pwsh -NoProfile -File windows/scripts/search-fuzz.ps1 \
        -ExePath windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe \
        -OutDir windows/scripts/search-fuzz {{args}}; exit ($LASTEXITCODE ?? 1)

# Real windows and real input, so it needs an interactive desktop and holds
# the foreground for the duration - about 40 minutes budgeted for everything,
# 5 for `-Tag smoke` (measured 3). Each harness is killed if it overruns its
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
fuzz args="": _no-wintty-running build-dll build-win
    pwsh -NoProfile -File windows/scripts/fuzz-suite.ps1 \
        -ExePath windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe {{args}}; exit ($LASTEXITCODE ?? 1)

# Alias for `just fuzz`, for when that is what the fingers type.
[windows]
fuzzy args="": (fuzz args)

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

# === Upstream Sync ===

# Pinned to bash via shebang so the POSIX `[` branch test below works
# regardless of the platform shell. On Windows this requires git-bash on
# PATH; sync is a maintainer command and the maintainer has it.

# Fetch upstream and rebase windows branch.
#
# Fetches origin as well, and refuses to start when the current branch lags
# the ref it tracks. This recipe ends in a force-push, so replaying a stale
# local copy does not merely miss the newer commits, it overwrites them.
sync force="":
    #!/usr/bin/env bash
    set -euo pipefail
    if [ "{{ force }}" != "--force" ] && [ "$(git branch --show-current)" != "windows" ]; then
        echo "WARNING: you are on '$(git branch --show-current)', not 'windows'. Switch to windows branch first. Use 'just sync --force' to override."
        exit 1
    fi
    git fetch upstream
    git fetch origin
    tracked=$(git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>/dev/null || echo "")
    if [ -n "$tracked" ]; then
        # Compare by patch-id, not by SHA. A replay rewrites every SHA, so
        # `HEAD..@{u}` reports the whole stack as missing the moment a sync
        # finishes, and this would then refuse to ever run a second time.
        # --cherry-pick --right-only drops commits that already exist here
        # under a different SHA and leaves only genuinely new work.
        behind=$(git rev-list --count --cherry-pick --right-only "HEAD...${tracked}")
        if [ "$behind" -gt 0 ]; then
            echo "REFUSING: ${tracked} has ${behind} commit(s) with no equivalent here:"
            git rev-list --cherry-pick --right-only --oneline "HEAD...${tracked}" | head -10 || true
            echo "Replaying now would leave them out, and the force-push at the end"
            echo "would make that permanent. Take them first:"
            echo "  git rebase ${tracked}"
            exit 1
        fi
    fi
    git rebase upstream/main
    echo "Rebase complete. Run 'just sync-verify' before pushing:"
    echo "  git push --force-with-lease origin $(git branch --show-current)"

# Post-rebase gate. Exits non-zero on a finding instead of printing a report
# for a human to skim, because the report this replaced was skimmed and passed
# while the product did not compile.
#
# Checks, cheapest first:
#
#   markers   a resolution that left <<<<<<< behind in a file no test compiles
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
# Four of these compare against the pre-rebase tip: dropped commits, file
# surface, range-diff, and the fmt baseline. That tip is the ref this branch
# tracks, and it survives only until the force-push replaces it. After pushing
# there is nothing left to compare against, so those four announce themselves
# as skipped rather than passing silently. Run this before you push, not after.
#
# Exit codes: 0 clean, 1 a finding, 2 bad arguments. Items printed as REVIEW
# exit 0 on purpose, because an upstream rename moves the file surface
# legitimately and a check that cries wolf gets passed a flag instead of read.
#
# `just sync-verify fast` stops before the build ladder.
sync-verify mode="":
    #!/usr/bin/env bash
    set -euo pipefail

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
        exit 1
    fi
    if ! git rev-parse --verify -q refs/remotes/upstream/main >/dev/null; then
        echo "FAIL: no refs/remotes/upstream/main. Add the upstream remote and fetch it;"
        echo "without it nothing below can tell a replay from a fresh checkout."
        exit 1
    fi
    dirty=$(git status --porcelain | grep -v "^?? ${tmpd}/\?\$" || true)
    if [ -n "$dirty" ]; then
        bad "working tree is not clean"
    fi
    if ! git merge-base --is-ancestor refs/remotes/upstream/main HEAD; then
        bad "HEAD does not contain upstream/main; the replay did not land on the fetched upstream"
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

    # The pre-rebase tip is whatever this branch tracks, taken from the branch
    # itself rather than hardcoded: on a stacked branch cut before the last
    # push, a hardcoded origin/windows is not an ancestor and every comparison
    # below would then be against the wrong history and fail bogusly.
    baseline=""
    tracked=$(git rev-parse --symbolic-full-name '@{u}' 2>/dev/null || echo "")
    if [ -n "$tracked" ] && ! git merge-base --is-ancestor "$tracked" HEAD; then
        baseline="$tracked"
    fi

    if [ -z "$baseline" ]; then
        note "=== dropped commits / file surface / range-diff / fmt baseline ==="
        note "  SKIP: ${tracked:-no tracking ref} is at or behind HEAD, so the pre-rebase tip is gone."
        note "  These four checks only work between the replay and the push."
    else
        old_base=$(git merge-base "$baseline" refs/remotes/upstream/main)

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
        note "  SKIP: zig is not on PATH"
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
            if ! {{ just_executable() }} "$leg"; then
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
    echo "  git push --force-with-lease origin windows"
