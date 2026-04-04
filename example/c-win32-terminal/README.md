# Example: Win32 Terminal (C)

Minimal C program that embeds libghostty in a Win32 window.
Uses the ghostty C API to create an app and surface with DX12
rendering. Creates the window, initializes ghostty, and forwards
keyboard, mouse, resize, focus, and DPI events to the surface.

Unlike the `c-vt-*` examples which use the VT parser library,
this example uses the full libghostty runtime (app, surface,
renderer, terminal, PTY).

## Prerequisites

- Windows 10 or later
- ghostty.dll built from the repo root:
  ```
  just build-dll
  ```
- An import library (ghostty.lib) -- see below
- Zig 0.15+ (used as C compiler), or MSVC, or MinGW-w64

## Creating the Import Library

The build produces ghostty.dll but not the import library needed by the
linker. Generate it from the DLL exports:

```bash
# From the repo root, after just build-dll:
python3 -c "
import struct
dll = 'zig-out/lib/ghostty.dll'
with open(dll, 'rb') as f: data = f.read()
pe = struct.unpack_from('<I', data, 0x3C)[0]
ns = struct.unpack_from('<H', data, pe+6)[0]
so = pe + 264
def r2o(rva):
    for i in range(ns):
        s = so + i*40
        va = struct.unpack_from('<I', data, s+12)[0]
        vs = struct.unpack_from('<I', data, s+8)[0]
        ro = struct.unpack_from('<I', data, s+20)[0]
        if va <= rva < va+vs: return rva-va+ro
erva = struct.unpack_from('<I', data, pe+24+112)[0]
eo = r2o(erva)
nn = struct.unpack_from('<I', data, eo+24)[0]
nto = r2o(struct.unpack_from('<I', data, eo+32)[0])
names = []
for i in range(nn):
    nr = struct.unpack_from('<I', data, nto+i*4)[0]
    no = r2o(nr); e = data.index(b'\0', no)
    names.append(data[no:e].decode())
with open('zig-out/lib/ghostty.def','w') as f:
    f.write('LIBRARY ghostty\nEXPORTS\n')
    for n in sorted(names): f.write(f'    {n}\n')
"
zig dlltool -m i386:x86-64 -d zig-out/lib/ghostty.def -l zig-out/lib/ghostty.lib
```

## Build

### Using Zig as C compiler (recommended)

```bash
cd example/c-win32-terminal
zig cc -target x86_64-windows-msvc src/main.c -I ../../include ../../zig-out/lib/ghostty.lib -luser32 -lgdi32 -o c_win32_terminal.exe
```

### MSVC (from Developer Command Prompt)

```bat
cd example\c-win32-terminal
cl.exe /I..\..\include src\main.c /link /LIBPATH:..\..\zig-out\lib ghostty.lib user32.lib
```

### MinGW-w64

```bash
cd example/c-win32-terminal
gcc -I../../include src/main.c -L../../zig-out/lib -lghostty -luser32 -o c_win32_terminal.exe
```

## Run

Make sure ghostty.dll is findable (copy to the example directory or add
`zig-out/lib` to PATH):

```
c_win32_terminal.exe
```
