# Third-party licences shipped with Wintty

Every file in this directory is copied into the application payload next to
`Wintty.exe`, under `licenses/`. The MSI harvest and the Velopack pack step
both take the publish folder wholesale, so anything added here reaches users
without a packaging change.

This is a hand-maintained set, not a generated one. It covers the components
we redistribute as binaries and the fonts compiled into `ghostty.dll`. It does
**not** yet cover the full managed dependency graph — see "Known gaps" below.

## What covers what

| File | Component | Licence |
|---|---|---|
| `Wintty-LICENSE.txt` | Wintty itself, and Ghostty which it is based on | MIT |
| `WindowsTerminal-LICENSE.txt` | `conpty.dll`, `OpenConsole.exe` | MIT |
| `DirectXShaderCompiler-LICENSE-LLVM.txt` | `dxcompiler.dll` | LLVM / University of Illinois NCSA |
| `DirectXShaderCompiler-LICENSE-MS.txt` | `dxcompiler.dll` redistribution grant | Microsoft |
| `Skia-LICENSE.txt` | Skia, compiled into `libSkiaSharp.dll` | BSD-3-Clause |
| `SkiaSharp-LICENSE.txt` | the SkiaSharp binding itself | MIT |
| `HarfBuzz-COPYING.txt` | HarfBuzz, compiled into `libHarfBuzzSharp.dll` | "Old MIT" |
| `HarfBuzzSharp-LICENSE.txt` | the HarfBuzzSharp binding itself | MIT |
| `Fonts-OFL-1.1.txt` | JetBrains Mono, Nerd Fonts Symbols, Noto Emoji | SIL OFL 1.1 |
| `Fonts-MIT.txt` | MIT-licensed embedded fonts | MIT |
| `Fonts-BSD-2-Clause.txt` | BSD-licensed embedded fonts | BSD-2-Clause |

The binding libraries and the native libraries inside them are listed
separately on purpose. `SkiaSharp-LICENSE.txt` is the MIT text for the C#
binding; it does not cover Skia, which is BSD-3-Clause and carries its own
notice requirement. The same split applies to HarfBuzz. Shipping only the
binding licence would under-report what is actually in the DLL.

The two DXC files are staged directly from the pinned
`Microsoft.Direct3D.DXC` package rather than checked in here, so they cannot
drift from the binary they cover. Everything else is vendored, because the
upstream package either ships no licence text at all or ships only the
binding's.

Fonts are grouped by licence rather than by font because the mapping from
font to licence already lives in `src/font/res/README.md`, which names each
embedded font with its copyright line. OFL 1.1 requires the licence travel
with the font, and the upstream JetBrains Mono archive we fetch carries none,
which is why these are vendored here.

## Known gaps

Managed dependencies resolved through NuGet are not covered yet. Most declare
an SPDX expression and ship no licence text, so collecting them means pairing
each package's `<copyright>` with the SPDX body — mechanical, but more than a
file copy. The same applies to the C libraries statically linked into
`ghostty.dll` (freetype, zlib, libpng, oniguruma, libxml2, simdutf, highway,
fontconfig, wuffs) and to the Zig package graph.

Adding a component here is the right move whenever we start redistributing a
new binary. Adding one to the payload without a licence file is the failure
mode this directory exists to prevent.
