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
    dotnet build windows/Ghostty.sln /p:Platform=x64

# Recipe body has no shebang so it runs under the platform shell selected by
# `set windows-shell` above (pwsh on Windows). The previous version used a
# bash shebang to `exec` the .exe, which forced git-bash on Windows for no
# reason - launching a Windows .exe works fine from pwsh.

# Build the DLL and the shell, then launch it.
[windows]
run-win: build-dll build-win
    ./windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0/Wintty.exe

# === ConPTY-mode validation ===

# Run one smoke row. ROW = pwsh-auto | pwsh-always | pwsh-never | cmd-auto.
[windows]
validate-transport-smoke ROW: build-dll build-win
    pwsh -NoProfile -File scripts/validate-transport-run.ps1 -Row {{ROW}}

# Run all four smoke rows; summarize, non-zero exit if any fail.
[windows]
validate-transport-smoke-all: build-dll build-win
    pwsh -NoProfile -File scripts/validate-transport-run-all.ps1

# === Shader Wrapper DLL (MSVC-compiled glslang + SPIRV-Cross) ===

# Build shader_wrapper.dll with MSVC (isolates C++ ABI from ghostty.dll).
# Must be run from a VS Developer Shell or with MSVC on PATH.
build-shader-wrapper:
    #!/usr/bin/env bash
    set -euo pipefail
    ROOT="{{justfile_directory()}}"
    PKG="$ROOT/pkg/glslang"
    OUTDIR="$PKG/glslang_dll"
    mkdir -p "$OUTDIR"

    # Resolve zig cache paths
    GLSLANG_SRC=""
    SPIRV_CROSS_SRC=""
    for d in "$LOCALAPPDATA/zig/p/"*/; do
        if [ -f "${d}glslang/Include/glslang_c_interface.h" ]; then
            GLSLANG_SRC="$d"
        fi
        if [ -f "${d}spirv_cross_c.h" ]; then
            SPIRV_CROSS_SRC="$d"
        fi
    done
    if [ -z "$GLSLANG_SRC" ]; then echo "ERROR: glslang not found in zig cache" >&2; exit 1; fi
    if [ -z "$SPIRV_CROSS_SRC" ]; then echo "ERROR: SPIRV-Cross not found in zig cache" >&2; exit 1; fi
    echo "glslang: $GLSLANG_SRC"
    echo "SPIRV-Cross: $SPIRV_CROSS_SRC"

    # Convert to Windows paths
    win_outdir=$(cygpath -w "$OUTDIR")
    win_glslang=$(cygpath -w "$GLSLANG_SRC")
    win_spvc=$(cygpath -w "$SPIRV_CROSS_SRC")
    win_pkg=$(cygpath -w "$PKG")

    # Write a response file so we avoid shell quoting hell with cl.exe
    resp="$OUTDIR/compile.rsp"
    {
        echo /nologo
        echo /c
        echo /std:c++17
        echo /DNDEBUG
        echo /DNOMINMAX
        echo /D_CRT_SECURE_NO_WARNINGS
        echo /EHsc
        echo /MT
        echo /O2
        echo /W0
        echo "/Fo${win_outdir}\\"
        echo "/I${win_glslang}"
        echo "/I${win_glslang}\\glslang\\Include"
        echo "/I${win_glslang}\\SPIRV"
        echo "/I${win_spvc}"
        echo "/I${win_pkg}\\override"
        echo /DSPIRV_CROSS_C_API_HLSL=1
        echo /DSPIRV_CROSS_C_API_GLSL=1
        echo /DSPIRV_CROSS_C_API_MSL=1
        echo /DSPIRV_CROSS_C_API_CPP=1
        echo /DSPIRV_CROSS_C_API_REFLECT=1

        # glslang sources
        echo "${win_glslang}\\glslang\\GenericCodeGen\\CodeGen.cpp"
        echo "${win_glslang}\\glslang\\GenericCodeGen\\Link.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\glslang_tab.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\attribute.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\Constant.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\iomapper.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\InfoSink.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\Initialize.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\IntermTraverse.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\Intermediate.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\ParseContextBase.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\ParseHelper.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\PoolAlloc.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\RemoveTree.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\Scan.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\ShaderLang.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\SpirvIntrinsics.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\SymbolTable.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\Versions.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\intermOut.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\limits.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\linkValidate.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\parseConst.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\reflection.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\preprocessor\\Pp.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\preprocessor\\PpAtom.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\preprocessor\\PpContext.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\preprocessor\\PpScanner.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\preprocessor\\PpTokens.cpp"
        echo "${win_glslang}\\glslang\\MachineIndependent\\propagateNoContraction.cpp"
        echo "${win_glslang}\\glslang\\CInterface\\glslang_c_interface.cpp"
        echo "${win_glslang}\\glslang\\ResourceLimits\\ResourceLimits.cpp"
        echo "${win_glslang}\\glslang\\ResourceLimits\\resource_limits_c.cpp"
        echo "${win_glslang}\\glslang\\OSDependent\\Windows\\ossource.cpp"
        echo "${win_glslang}\\SPIRV\\GlslangToSpv.cpp"
        echo "${win_glslang}\\SPIRV\\InReadableOrder.cpp"
        echo "${win_glslang}\\SPIRV\\Logger.cpp"
        echo "${win_glslang}\\SPIRV\\SpvBuilder.cpp"
        echo "${win_glslang}\\SPIRV\\SpvPostProcess.cpp"
        echo "${win_glslang}\\SPIRV\\doc.cpp"
        echo "${win_glslang}\\SPIRV\\disassemble.cpp"
        echo "${win_glslang}\\SPIRV\\CInterface\\spirv_c_interface.cpp"

        # SPIRV-Cross sources
        echo "${win_spvc}\\spirv_cross.cpp"
        echo "${win_spvc}\\spirv_cross_c.cpp"
        echo "${win_spvc}\\spirv_cfg.cpp"
        echo "${win_spvc}\\spirv_glsl.cpp"
        echo "${win_spvc}\\spirv_hlsl.cpp"
        echo "${win_spvc}\\spirv_msl.cpp"
        echo "${win_spvc}\\spirv_cpp.cpp"
        echo "${win_spvc}\\spirv_parser.cpp"
        echo "${win_spvc}\\spirv_reflect.cpp"
        echo "${win_spvc}\\spirv_cross_parsed_ir.cpp"
        echo "${win_spvc}\\spirv_cross_util.cpp"

        # wrapper entry point
        echo "${win_pkg}\\shader_wrapper.cpp"
    } > "$resp"

    echo "Compiling glslang + SPIRV-Cross + shader_wrapper with MSVC..."
    win_resp=$(cygpath -w "$resp")
    cl.exe @"$win_resp"
    if [ $? -ne 0 ]; then echo "ERROR: Compilation failed" >&2; exit 1; fi

    echo "Linking shader_wrapper.dll..."
    # Discover libcpmt.lib via VCToolsInstallDir (set in VS Developer Shell)
    # or vswhere. Using /DEFAULTLIB:libcpmt would work too but explicit path
    # avoids ambiguity when multiple MSVC versions are installed.
    LIBCPMT=""
    if [ -n "$VCToolsInstallDir" ]; then
        LIBCPMT="$(cygpath -w "${VCToolsInstallDir}lib\\x64\\libcpmt.lib")"
    else
        VSWHERE="/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe"
        if [ -f "$VSWHERE" ]; then
            VS_PATH=$("$VSWHERE" -latest -property installationPath 2>/dev/null)
            if [ -n "$VS_PATH" ]; then
                MSVC_VER=$(ls -1 "${VS_PATH}/VC/Tools/MSVC/" 2>/dev/null | sort -rV | head -1)
                if [ -n "$MSVC_VER" ]; then
                    LIBCPMT="$(cygpath -w "${VS_PATH}\\VC\\Tools\\MSVC\\${MSVC_VER}\\lib\\x64\\libcpmt.lib")"
                fi
            fi
        fi
    fi
    if [ -z "$LIBCPMT" ]; then echo "ERROR: Could not find MSVC libcpmt.lib" >&2; exit 1; fi
    link.exe /nologo /dll /out:"${win_outdir}\\shader_wrapper.dll" \
      "${win_outdir}\\*.obj" \
      "$LIBCPMT"
    if [ $? -ne 0 ]; then echo "ERROR: Linking failed" >&2; exit 1; fi

    echo "Success: ${OUTDIR}/shader_wrapper.dll"

# Build everything and deploy to .NET output (full dev cycle)
[windows]
deploy-shader-wrapper: build-dll build-shader-wrapper build-win
    #!/usr/bin/env bash
    set -e
    DOTNET_OUT="windows/Ghostty/bin/x64/Debug/net10.0-windows10.0.19041.0"
    cp zig-out/lib/ghostty.dll "${DOTNET_OUT}/native/"
    cp pkg/glslang/glslang_dll/shader_wrapper.dll "${DOTNET_OUT}/"
    echo "Deployed ghostty.dll + shader_wrapper.dll"

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
