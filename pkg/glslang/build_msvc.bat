@echo off
setlocal enabledelayedexpansion

REM Discover MSVC tools directory via vswhere (works for VS 2019+).
REM Falls back to VCToolsInstallDir if set (inside VS Developer Shell).
if not defined MSVC_DIR (
    set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
    if exist "!VSWHERE!" (
        for /f "usebackq delims=" %%d in (`"!VSWHERE!" -latest -property installationPath`) do set "VS_PATH=%%d"
    )
    if defined VS_PATH (
        for /f "usebackq delims=" %%t in (`dir /b /ad "!VS_PATH!\VC\Tools\MSVC" 2^>nul ^| sort /r`) do (
            set "MSVC_DIR=!VS_PATH!\VC\Tools\MSVC\%%t"
            goto :found_msvc
        )
    )
    if defined VCToolsInstallDir (
        set "MSVC_DIR=!VCToolsInstallDir:~0,-1!"
    )
)
:found_msvc

if not defined MSVC_DIR (
    echo ERROR: Could not find MSVC tools. Run from a VS Developer Shell or install Visual Studio.
    exit /b 1
)

if not exist "!MSVC_DIR!\bin\Hostx64\x64\cl.exe" (
    echo ERROR: cl.exe not found at !MSVC_DIR!\bin\Hostx64\x64\
    exit /b 1
)

echo Using MSVC: !MSVC_DIR!

REM Discover Windows SDK version (use latest)
set WINSDK_VER=
set WINSDK_ROOT=%ProgramFiles(x86)%\Windows Kits\10
if exist "%WINSDK_ROOT%\Include" (
    for /f "delims=" %%v in ('dir /b /ad "%WINSDK_ROOT%\Include" 2^>nul ^| sort /r') do (
        if exist "%WINSDK_ROOT%\Include\%%v\um" (
            set WINSDK_VER=%%v
            goto :found_sdk
        )
    )
)
:found_sdk

if not defined WINSDK_VER (
    echo ERROR: Could not find Windows SDK
    exit /b 1
)

echo Using Windows SDK: %WINSDK_VER%

set WINSDK_INC=%WINSDK_ROOT%\Include\%WINSDK_VER%
set WINSDK_LIB=%WINSDK_ROOT%\Lib\%WINSDK_VER%

REM Find glslang source from zig cache
for /f "delims=" %%i in ('dir /b /ad "%LOCALAPPDATA%\zig\p"') do (
    if exist "%LOCALAPPDATA%\zig\p\%%i\glslang\Include\glslang_c_interface.h" (
        set GLSLANG_SRC=%LOCALAPPDATA%\zig\p\%%i
    )
)

if not defined GLSLANG_SRC (
    echo ERROR: Could not find glslang source in zig cache
    exit /b 1
)

echo Using glslang source: %GLSLANG_SRC%

set CL=!MSVC_DIR!\bin\Hostx64\x64\cl.exe
set LIB=!MSVC_DIR!\lib\x64;%WINSDK_LIB%\um\x64;%WINSDK_LIB%\ucrt\x64
set INCLUDE=!MSVC_DIR!\include;%WINSDK_INC%\um;%WINSDK_INC%\ucrt;%WINSDK_INC%\shared

set OUTDIR=%~dp0msvc_build
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

set CFLAGS=/nologo /c /std:c++17 /DNDEBUG /DNOMINMAX /D_CRT_SECURE_NO_WARNINGS /EHsc /MT /O2 /W0 /Fo"%OUTDIR%\\"

REM Compile all glslang source files
set FILES=
for %%f in (
    "%GLSLANG_SRC%\glslang\GenericCodeGen\CodeGen.cpp"
    "%GLSLANG_SRC%\glslang\GenericCodeGen\Link.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\glslang_tab.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\attribute.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\Constant.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\iomapper.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\InfoSink.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\Initialize.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\IntermTraverse.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\Intermediate.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\ParseContextBase.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\ParseHelper.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\PoolAlloc.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\RemoveTree.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\Scan.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\ShaderLang.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\SpirvIntrinsics.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\SymbolTable.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\Versions.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\intermOut.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\limits.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\linkValidate.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\parseConst.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\reflection.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\preprocessor\Pp.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\preprocessor\PpAtom.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\preprocessor\PpContext.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\preprocessor\PpScanner.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\preprocessor\PpTokens.cpp"
    "%GLSLANG_SRC%\glslang\MachineIndependent\propagateNoContraction.cpp"
    "%GLSLANG_SRC%\glslang\CInterface\glslang_c_interface.cpp"
    "%GLSLANG_SRC%\glslang\ResourceLimits\ResourceLimits.cpp"
    "%GLSLANG_SRC%\glslang\ResourceLimits\resource_limits_c.cpp"
    "%GLSLANG_SRC%\glslang\OSDependent\Windows\ossource.cpp"
    "%GLSLANG_SRC%\SPIRV\GlslangToSpv.cpp"
    "%GLSLANG_SRC%\SPIRV\InReadableOrder.cpp"
    "%GLSLANG_SRC%\SPIRV\Logger.cpp"
    "%GLSLANG_SRC%\SPIRV\SpvBuilder.cpp"
    "%GLSLANG_SRC%\SPIRV\SpvPostProcess.cpp"
    "%GLSLANG_SRC%\SPIRV\doc.cpp"
    "%GLSLANG_SRC%\SPIRV\disassemble.cpp"
    "%GLSLANG_SRC%\SPIRV\CInterface\spirv_c_interface.cpp"
) do (
    set FILES=!FILES! "%%~f"
)

echo Compiling with MSVC...
%CL% %CFLAGS% %FILES%

if errorlevel 1 (
    echo ERROR: Compilation failed
    exit /b 1
)

echo Creating static library...
"!MSVC_DIR!\bin\Hostx64\x64\lib.exe" /nologo /out:"%OUTDIR%\glslang.lib" "%OUTDIR%\*.obj"

if errorlevel 1 (
    echo ERROR: Library creation failed
    exit /b 1
)

echo Success: %OUTDIR%\glslang.lib
