#!/usr/bin/env bash
# Build vttest inside a WSL distro without root.
#
# vttest is not packaged-installable here because `apt-get install` needs a
# sudo password (non-interactive), so we build the upstream tarball to a
# per-user prefix instead. gcc/make/termios are already present in a default
# Ubuntu WSL image; the build itself needs no network.
#
# Usage:
#   build-vttest.sh [path-to-vttest.tar.gz]
#
# With no argument the script downloads the tarball (needs WSL network). When
# the WSL distro has no outbound network (common -- DNS often fails in WSL while
# the Windows host is online), download it on the Windows side and pass the
# /mnt/c/... path:
#   curl/Invoke-WebRequest https://invisible-island.net/archives/vttest/vttest.tar.gz
#   build-vttest.sh /mnt/c/temp/vttest.tar.gz
#
# Result: a `vttest` binary plus a stable ~/vttest symlink the runner uses.
set -euo pipefail

src="${1:-}"
build="$HOME/vttest-build"
mkdir -p "$build"

if [ -z "$src" ]; then
    src="$build/vttest.tar.gz"
    echo "downloading vttest tarball (needs WSL network)..."
    curl -fSL --max-time 60 -o "$src" \
        https://invisible-island.net/archives/vttest/vttest.tar.gz
fi

rm -rf "$build/src"
mkdir -p "$build/src"
tar -xzf "$src" -C "$build/src" --strip-components=1

cd "$build/src"
./configure >/tmp/vttest-configure.log 2>&1
make >/tmp/vttest-make.log 2>&1

ln -sf "$build/src/vttest" "$HOME/vttest"
# Smoke check only -- never fail the build on it (guard against a future vttest
# changing the version flag, which `set -e` would otherwise turn into a failure).
"$HOME/vttest" -V || true
echo "vttest ready: $HOME/vttest -> $(readlink -f "$HOME/vttest")"
