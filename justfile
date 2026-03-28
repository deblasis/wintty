# Ghostty Windows Fork - Build Orchestration
# Run `just` for the default (full test + build), or `just <recipe>` for individual steps.

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

# === Building ===

# Build libghostty DLL
build-dll:
    zig build -Dapp-runtime=none

# === Upstream Sync ===

# Fetch upstream and rebase windows branch
sync force="":
    @if [ "{{ force }}" != "--force" ] && [ "$(git branch --show-current)" != "windows" ]; then echo "WARNING: you are on '$(git branch --show-current)', not 'windows'. Switch to windows branch first. Use 'just sync --force' to override." && exit 1; fi
    git fetch upstream
    git rebase upstream/main
    @echo "Rebase complete. Review any conflicts, then: git push --force-with-lease origin windows"
