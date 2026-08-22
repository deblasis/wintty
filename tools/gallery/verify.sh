#!/usr/bin/env bash
# verify.sh — compile and render the bundled shader gallery through the real
# shipped pipeline, end to end:
#
#   GLSL (prefix + gallery shader)
#     -> zioshade HLSL            (local; the same compileGlslToHlsl wintty runs)
#     -> DXC DXIL                 (Windows box; the SDK dxc, DXIL-capable)
#     -> D3D12 WARP render        (zioshade tools/warp, gallery mode)
#     -> <name>.ppm frame         (fetched back to previews/)
#
# A shader that passes here compiles and renders on the exact path a user's
# terminal runs. A FAIL is a broken gallery entry: fix the shader or file a
# zioshade bug — do not ship it.
#
# Usage:   tools/gallery/verify.sh
# Env:
#   ZIOSHADE_BIN   zioshade CLI (default: ../zioshade/zig-out/bin/zioshade)
#   WARP_HOST      ssh target with the Windows SDK (default alessandro@ryzen7pro)
#   WARP_DIR       remote working dir (default C:/wintty_gallery)
#   ZIOSHADE_DIR   zioshade checkout (default ../zioshade), for tools/warp

set -euo pipefail

repo=$(cd "$(dirname "$0")/../.." && pwd)
shaders_dir="$repo/windows/Ghostty/Assets/Shaders"
prefix="$repo/src/renderer/shaders/shadertoy_prefix.glsl"
previews="$repo/tools/gallery/previews"

ZIOSHADE_DIR=${ZIOSHADE_DIR:-$(cd "$repo/.." && pwd)/zioshade}
ZIOSHADE_BIN=${ZIOSHADE_BIN:-$ZIOSHADE_DIR/zig-out/bin/zioshade}
WARP_HOST=${WARP_HOST:-alessandro@ryzen7pro}
WARP_DIR=${WARP_DIR:-C:/wintty_gallery}
DXC_WIN='C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\dxc.exe'

if [ ! -x "$ZIOSHADE_BIN" ]; then
  echo "zioshade CLI not found at $ZIOSHADE_BIN (build it: cd $ZIOSHADE_DIR && zig build cli)" >&2
  exit 2
fi

stage=$(mktemp -d /tmp/gallery_verify.XXXXXX)
trap 'rm -rf "$stage"' EXIT

# ── 1. Local compile gate: prefix + shader -> HLSL via zioshade ────────────
pass=0; fail=0; failed=""
for glsl in "$shaders_dir"/*.glsl; do
  name=$(basename "$glsl" .glsl)
  cat "$prefix" "$glsl" > "$stage/$name.full.glsl"
  if "$ZIOSHADE_BIN" hlsl "$stage/$name.full.glsl" -o "$stage/$name.hlsl" --stage fragment 2>"$stage/$name.err"; then
    pass=$((pass+1))
  else
    echo "FAIL zioshade $name: $(head -1 "$stage/$name.err")"
    fail=$((fail+1)); failed="$failed $name"
  fi
done
echo "zioshade HLSL: $pass pass, $fail fail"
[ "$fail" -eq 0 ] || { echo "not staging (fix zioshade failures first):$failed"; exit 1; }

# ── 2. Stage everything to the Windows box ─────────────────────────────────
# build_warp.cmd carries the clang-cl recipe from zioshade tools/warp/README.md
# (that box has no vcvars; a batch file sidesteps every ssh quoting problem).
cat > "$stage/build_warp.cmd" <<'EOF'
@echo off
set "PATH=C:\Program Files\LLVM\bin;C:\Windows\System32"
set "MSVC=C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231"
set "SDK=C:\Program Files (x86)\Windows Kits\10"
set "SDKVER=10.0.26100.0"
set "INCLUDE=%MSVC%\include;%SDK%\Include\%SDKVER%\ucrt;%SDK%\Include\%SDKVER%\um;%SDK%\Include\%SDKVER%\shared;%SDK%\Include\%SDKVER%\winrt"
set "LIB=%MSVC%\lib\x64;%SDK%\Lib\%SDKVER%\ucrt\x64;%SDK%\Lib\%SDKVER%\um\x64"
if exist warp_render.exe exit /b 0
clang-cl /std:c++17 /EHsc /O2 /D_CRT_SECURE_NO_WARNINGS warp_render.cpp /Fe:warp_render.exe -fuse-ld=lld /link d3d12.lib dxgi.lib
exit /b %ERRORLEVEL%
EOF

win_dir=$(echo "$WARP_DIR" | sed 's|/|\\|g')
ssh "$WARP_HOST" "if not exist ${win_dir%\\*} mkdir ${win_dir%\\*}"
scp -q "$ZIOSHADE_DIR/tools/warp/run.ps1" \
       "$ZIOSHADE_DIR/tools/warp/warp_render.cpp" \
       "$ZIOSHADE_DIR/tools/warp/fullscreen_vs.hlsl" \
       "$stage/build_warp.cmd" \
       "$stage"/*.hlsl \
       "$WARP_HOST:$WARP_DIR/"

# ── 3. Build warp_render.exe (once) and run the gallery sweep ───────────────
ssh "$WARP_HOST" "cd /d $win_dir && build_warp.cmd" || { echo "warp_render build failed" >&2; exit 3; }

echo "running WARP gallery render on $WARP_HOST..."
ssh "$WARP_HOST" "cd /d $win_dir && powershell -ExecutionPolicy Bypass -File run.ps1 -Gallery -Dir . -Dxc \"$DXC_WIN\" -Warp .\warp_render.exe"

# ── 4. Fetch the rendered frames back as previews ──────────────────────────
mkdir -p "$previews"
scp -q "$WARP_HOST:$WARP_DIR/*.ppm" "$previews/" || echo "(no PPMs fetched)"
echo "previews in $previews"
