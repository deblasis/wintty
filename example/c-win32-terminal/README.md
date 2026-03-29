# Example: Win32 Terminal (C)

Minimal C program that embeds libghostty in a Win32 window.
Uses the ghostty C API to create an app and surface with DX11
rendering. Creates the window, initializes ghostty, and forwards
keyboard, mouse, resize, focus, and DPI events to the surface.

Unlike the `c-vt-*` examples which use the VT parser library,
this example uses the full libghostty runtime (app, surface,
renderer, terminal, PTY).

## Prerequisites

- Windows 10 or later
- ghostty.dll and ghostty.lib built from the repo root:
  ```
  just build-dll
  ```
- MSVC (Visual Studio 2022) or MinGW-w64

## Build

### MSVC

```bat
cl.exe /I..\..\include src\main.c /link /LIBPATH:..\..\zig-out\lib ghostty.lib user32.lib
```

### MinGW-w64

```bash
gcc -I../../include src/main.c -L../../zig-out/lib -lghostty -luser32 -o c_win32_terminal.exe
```

## Run

Make sure ghostty.dll is findable (copy to the example directory or add
`zig-out/lib` to PATH):

```
c_win32_terminal.exe
```
