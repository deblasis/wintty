// Written for the wintty shader gallery. License: MIT (wintty project).
//
// An original CRT look, built the way the classic CRT shaders do it: a
// SMOOTH sine luminance wave for scanlines (hard-edged step lines alias
// into moire mush at real resolutions) plus a vertical 2px aperture
// grille for the phosphor-mask feel, gentle barrel curvature, faint
// chromatic separation, and a vignette. Tuned to stay readable.

const float CRT_CURVE   = 0.022; // barrel strength (kept subtle)
const float CRT_SCAN_MIN = 3.5;  // min screen px per scanline period
const float CRT_SCAN_DEPTH = 0.5; // scanline wave depth (0..1)
const float CRT_GRILLE   = 0.22;  // vertical grille darkening
const float CRT_CHROMA   = 0.0012; // chromatic offset at the edges
const float CRT_VIGNETTE = 0.18;  // corner darkening

vec2 crtCurve(vec2 uv)
{
    vec2 c = uv * 2.0 - 1.0;
    c *= 1.0 + CRT_CURVE * dot(c, c);
    return c * 0.5 + 0.5;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = crtCurve(fragCoord.xy / iResolution.xy);

    // Bezel via multiply, not an early return: an early return in
    // mainImage lowers to a WGSL path that renders the whole frame black
    // (zioshade-8h7), and the multiply reads identically on every backend.
    float inside = step(0.0, uv.x) * step(uv.x, 1.0)
                 * step(0.0, uv.y) * step(uv.y, 1.0);

    // Chromatic separation grows toward the edges.
    vec2 c = uv - 0.5;
    vec2 off = c * CRT_CHROMA * (1.0 + 4.0 * dot(c, c));

    vec3 col;
    col.r = texture(iChannel0, uv + off).r;
    col.g = texture(iChannel0, uv).g;
    col.b = texture(iChannel0, uv - off).b;

    // Scanlines: a smooth luminance wave in screen space, gently rolling.
    // Resolution-adaptive period; the wave shape (not a hard step) is what
    // keeps the lines visible instead of aliasing into noise.
    float scan_px = max(CRT_SCAN_MIN, iResolution.y / 320.0);
    float scans = 0.5 + 0.5 * sin(fragCoord.y * 6.2831853 / scan_px + iTime * 1.5);
    float m = 1.0 - CRT_SCAN_DEPTH * pow(scans, 1.6);
    col *= m;

    // Aperture grille: vertical 2px phosphor columns. This, not a pixel
    // grid, is what reads as "CRT" -- horizontal scanlines above, vertical
    // grille here.
    float gx = clamp((mod(fragCoord.x, 2.0) - 1.0) * 2.0, 0.0, 1.0);
    col *= 1.0 - CRT_GRILLE * gx;

    // Vignette.
    float vig = 1.0 - CRT_VIGNETTE * dot(c, c) * 2.2;
    col *= vig;

    fragColor = vec4(col * inside, 1.0);
}
