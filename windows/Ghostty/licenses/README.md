# Third-party licences shipped with Wintty

Every file in this directory is copied into the application payload next to
`Wintty.exe`, under `licenses/`. The MSI harvest and the Velopack pack step
both take the publish folder wholesale, so anything added here reaches users
without a packaging change.

This is a hand-maintained set describing what the **Windows** build ships.
Each file starts with an `Applies to:` line naming the components it covers,
because several components share one licence text and reproducing it per
component would be noise.

## Coverage

**Wintty itself** — `Wintty-LICENSE.txt` (MIT), covering Wintty and Ghostty,
which it is based on.

**Binaries we redistribute unmodified.** `WindowsTerminal-LICENSE.txt` covers
`conpty.dll` and `OpenConsole.exe`. `DirectXShaderCompiler-LICENSE-LLVM.txt`
and `-LICENSE-MS.txt` cover `dxcompiler.dll`; those two are staged straight
from the pinned `Microsoft.Direct3D.DXC` package rather than vendored here, so
they cannot drift from the binary they cover.

**Managed dependencies** resolved through NuGet — the Windows App SDK family,
the `Microsoft.Extensions.*` and `System.*` set, WebView2, the Svg.Skia family
and ExCSS. Where a package ships its own licence text that text is used
verbatim; where it declares only an SPDX expression, the body is paired with
the package's own copyright line, since the copyright is the part a notice is
actually required to carry.

**Code compiled into `ghostty.dll`** — freetype, harfbuzz, zlib, libpng,
oniguruma, highway, wuffs, imgui, libxev, uucode, vaxis, zigimg, zf, z2d,
simdutf, and the zioshade cross-compiler with its two dependencies. These have
no package-manager entry in the .NET build; most come from the resolved Zig
dependency cache, but several are missing from it because their upstream
`paths` excludes the licence from the published tarball, and `simdutf` is
vendored in-tree with no package entry at all. Those were taken from the
projects themselves.

**Third-party code inside binaries we ship.** A top-level licence usually
covers only that project's own code, not what it statically links. These
enumerate the rest:

- `WindowsAppSDK-NOTICE.txt` — inside the Windows App SDK runtime binaries
- `WindowsML-ThirdPartyNotices.txt` — inside `onnxruntime.dll`, `DirectML.dll`
- `SkiaSharp-HarfBuzzSharp-native-THIRD-PARTY-NOTICES.txt` — inside
  `libSkiaSharp.dll` and `libHarfBuzzSharp.dll`
- `WinUI-NOTICE.txt`, `WebView2-NOTICE.txt` — inside their respective binaries
- `uucode-sublicence-*.txt` — the two licences uucode's own notice refers to

`Skia-LICENSE.txt` and `harfbuzz-LICENSE.txt` cover those projects' own code;
what they statically link is in the native notices file above.
`HarfBuzzSharp-and-4-more-LICENSE.txt` is the MIT text for the C# bindings and
covers neither.

**Fonts.** `jetbrains_mono-LICENSE.txt` and
`nerd_fonts_symbols_only-LICENSE.txt` cover the fonts embedded in
`ghostty.dll`. `Fonts-OFL-1.1.txt`, `Fonts-MIT.txt` and
`Fonts-BSD-2-Clause.txt` cover the OFL, MIT and BSD fonts that live in
`src/font/res` as test fixtures; Noto Emoji is included there because it is
embedded on other platforms even though the Windows build guards it out. OFL
and MIT both require the copyright line specifically, so each of those files
carries the holders, which the bare upstream templates do not.

## What this does not cover

Tier-specific dependencies added by the release repo's overlays — the Pro
tier's speech and compression packages among them — are not here, because this
directory lives in the base repo and never sees them. They need the same
treatment in their own overlay.

Build-time-only components are absent: the compiler toolchain, source
generators, metadata packages, and Zig graph entries reached only by the build
machine or by tests. The NativeAOT compiler is the exception worth naming —
its output is not redistributed but the .NET runtime it links into
`Wintty.exe` is, which is why the runtime is named on the
`Microsoft.Extensions.*` file rather than given one of its own.

## Adding to this

Adding a component to the payload without adding its licence here is the
failure this directory exists to prevent.

Two traps, both of which have already bitten this set. A package's published
tarball may exclude its own licence file, so the dependency cache is not a
reliable source — check the project. And a project's top-level licence
frequently does not cover what it statically links; look for a `NOTICE` or
`THIRD-PARTY-NOTICES` file beside it. If a component declares only an SPDX
expression, pair the body with the package's own copyright line, and prefer
the project's real licence file over a template — a notice that names no
copyright holder is worse than no notice.
