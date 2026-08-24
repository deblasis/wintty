# wintty shader gallery

Bundled custom shaders for the terminal. Every entry in `shaders.json` is
shown in Settings under Appearance, and every entry is compile- and
render-verified through the real shipped pipeline (GLSL -> zioshade ->
HLSL -> DXC -> DXIL -> D3D12) by `just gallery-verify` at the repo root.

## The contract

Each `.glsl` file here contains only `mainImage()` in Shadertoy style.
wintty prepends `src/renderer/shaders/shadertoy_prefix.glsl` at runtime,
which declares the uniforms:

- `iChannel0` - the terminal frame texture (sample with
  `texture(iChannel0, fragCoord.xy / iResolution.xy)`)
- `iTime`, `iTimeDelta`, `iFrame`, `iFrameRate`
- cursor state: `iCurrentCursor`, `iPreviousCursor`, cursor colors and
  styles, `iCursorVisible`
- `iPalette[256]`, background/foreground/selection colors
- `iMouse`, `iDate`, `iFocus`

Coordinate convention on wintty: `fragCoord` has its origin at the
top-left and +Y points down.

## Licensing

Only shaders that can be redistributed with attribution are bundled:
MIT, Unlicense, or original work in this repo. Each vendored file carries
a provenance header, and the full license texts live in `LICENSES/`.
Anything else (unlicensed collections, Shadertoy ports under the default
CC BY-NC-SA, GPL) must not be added.

## Adding a shader

1. Drop the `.glsl` file here with a provenance header.
2. Add an entry to `shaders.json`.
3. Add the license text under `LICENSES/` if it is not original work,
   and an entry in the repo's `THIRD_PARTY_NOTICES.md`.
4. Run `just gallery-lint` (seconds, no GPU and no zioshade build: it is
   the parse half of the gate, and it is what CI runs on every push).
5. Run `just gallery-verify` and make sure it reports PASS.

The source must compile under `glslang` verbatim: the gate feeds the same
file to our compiler and to the reference one, so anything glslang refuses
takes the whole gate down rather than just that shader. The trap worth
naming is GLSL's reserved-word list (`active`, `filter`, `input`,
`output`, `union`, `resource` and friends in GLSL 4.60 section 3.6): our
compiler accepts them, glslang does not.
