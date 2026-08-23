#!/usr/bin/env bash
# verify.sh — compile and render the bundled shader gallery through the real
# shipped pipeline, end to end, DIFFERENTIALLY:
#
#   GLSL (prefix + gallery shader)
#     -> zioshade HLSL            (local; the same compileGlslToHlsl wintty runs)
#     -> reference HLSL           (local; glslang -> SPIR-V -> spirv-cross,
#                                  the flags zioshade's own oracle scripts use)
#     -> DXC DXIL                 (Windows box; the SDK dxc, DXIL-capable)
#     -> D3D12 WARP render        (zioshade tools/warp, gallery pair-diff mode)
#     -> <name>.zs.ppm / .sc.ppm  (fetched back to previews/)
#
# A shader that passes here compiles AND renders identically to the independent
# spirv-cross reference on the exact path a user's terminal runs. A GALLERY
# DIFFER means zioshade miscompiled the shader: that is a zioshade bug (fix it
# or file it upstream, and do NOT ship the shader in that state). A compile or
# render FAIL is a broken gallery entry: fix the shader, do not ship it.
#
# Usage:   tools/gallery/verify.sh
# Env:
#   ZIOSHADE_BIN   zioshade CLI (default: ../zioshade/zig-out/bin/zioshade)
#   WARP_HOST      ssh target with the Windows SDK (default alessandro@ryzen7pro)
#   WARP_DIR       remote working dir (default C:/wintty_gallery)
#   ZIOSHADE_DIR   zioshade checkout (default ../zioshade), for tools/warp
#   GLSLANG        glslangValidator binary (default: glslang on PATH)
#   SPIRV_CROSS    spirv-cross binary (default: spirv-cross on PATH)

set -euo pipefail

repo=$(cd "$(dirname "$0")/../.." && pwd)
shaders_dir="$repo/windows/Ghostty/Assets/Shaders"
prefix="$repo/src/renderer/shaders/shadertoy_prefix.glsl"
previews="$repo/tools/gallery/previews"

ZIOSHADE_DIR=${ZIOSHADE_DIR:-$(cd "$repo/.." && pwd)/zioshade}
ZIOSHADE_BIN=${ZIOSHADE_BIN:-$ZIOSHADE_DIR/zig-out/bin/zioshade}
WARP_HOST=${WARP_HOST:-alessandro@ryzen7pro}
WARP_DIR=${WARP_DIR:-C:/wintty_gallery}
GLSLANG=${GLSLANG:-glslang}
SPIRV_CROSS=${SPIRV_CROSS:-spirv-cross}
DXC_WIN='C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\dxc.exe'

if [ ! -x "$ZIOSHADE_BIN" ]; then
  echo "zioshade CLI not found at $ZIOSHADE_BIN (build it: cd $ZIOSHADE_DIR && zig build cli)" >&2
  exit 2
fi
command -v "$GLSLANG" >/dev/null     || { echo "glslang not found (brew install glslang); the reference path needs it" >&2; exit 2; }
command -v "$SPIRV_CROSS" >/dev/null || { echo "spirv-cross not found (brew install spirv-cross); the reference path needs it" >&2; exit 2; }

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

# ── 2. Reference HLSL: same GLSL -> glslang SPIR-V -> spirv-cross ───────────
# An independent oracle for every gallery shader (the same glslang/spirv-cross
# flags zioshade's oracle scripts use, e.g. tools/frag_oracle_check.sh and
# tools/warp/stage_pairs.sh). run.ps1 -Gallery pairs <name>.hlsl with this
# <name>.sc.hlsl and render-diffs them on WARP. A rejection here FAILS the
# gate: a reference gap is a gate gap (that shader would silently lose its
# differential).
# The one normalization: glslang is stricter than zioshade's frontend about
# reserved words. The gallery uses `active` (reserved in desktop GLSL) as an
# identifier and zioshade deliberately accepts it (wintty compiles via zioshade
# alone). Renaming just that identifier on the REFERENCE side is
# semantics-preserving (a local variable), so the oracle still covers these
# shaders; anything else glslang or spirv-cross rejects is a loud FAIL.
refpass=0; reffail=0; reffailed=""
for glsl in "$shaders_dir"/*.glsl; do
  name=$(basename "$glsl" .glsl)
  perl -pe 's/\bactive\b/active_/g' "$stage/$name.full.glsl" > "$stage/$name.ref.glsl"
  if "$GLSLANG" -V -S frag "$stage/$name.ref.glsl" -o "$stage/$name.spv" >/dev/null 2>"$stage/$name.ref.err" &&
     "$SPIRV_CROSS" --hlsl --shader-model 60 "$stage/$name.spv" > "$stage/$name.sc.hlsl" 2>>"$stage/$name.ref.err"; then
    refpass=$((refpass+1))
  else
    echo "FAIL reference $name: $(head -1 "$stage/$name.ref.err")"
    reffail=$((reffail+1)); reffailed="$reffailed $name"
  fi
  rm -f "$stage/$name.spv"
done
echo "reference HLSL (glslang -> spirv-cross): $refpass pass, $reffail fail"
[ "$reffail" -eq 0 ] || { echo "reference generation failed (a reference gap is a gate gap):$reffailed"; exit 1; }

# ── 3. Stage everything to the Windows box ─────────────────────────────────
# <name>.hlsl (zioshade) + <name>.sc.hlsl (reference) pair up in run.ps1.
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
ssh "$WARP_HOST" "if not exist $win_dir mkdir $win_dir"
scp -q "$ZIOSHADE_DIR/tools/warp/run.ps1" \
       "$ZIOSHADE_DIR/tools/warp/warp_render.cpp" \
       "$ZIOSHADE_DIR/tools/warp/fullscreen_vs.hlsl" \
       "$stage/build_warp.cmd" \
       "$stage"/*.hlsl \
       "$WARP_HOST:$WARP_DIR/"

# ── 4. Build warp_render.exe and run the gallery sweep ──────────────────────
# build_warp.cmd caches an existing exe; the staged warp_render.cpp must be the
# one that actually runs (a stale cached exe predating --gallery-diff fails the
# paired mode confusingly), so drop the exe and let it rebuild.
ssh "$WARP_HOST" "if exist $win_dir\warp_render.exe del $win_dir\warp_render.exe"
ssh "$WARP_HOST" "cd /d $win_dir && build_warp.cmd" || { echo "warp_render build failed" >&2; exit 3; }

echo "running WARP gallery render on $WARP_HOST..."
ssh "$WARP_HOST" "cd /d $win_dir && powershell -ExecutionPolicy Bypass -File run.ps1 -Gallery -Dir . -Dxc \"$DXC_WIN\" -Warp .\warp_render.exe"

# ── 5. Fetch the rendered frames back as previews ──────────────────────────
mkdir -p "$previews"
scp -q "$WARP_HOST:$WARP_DIR/*.ppm" "$previews/" || echo "(no PPMs fetched)"
echo "previews in $previews"
