// Written for the wintty shader gallery. License: MIT (wintty project).
//
// An original CRT look: gentle curvature, resolution-scaled scanlines with
// a slow roll, a whisper of a pixel grid, chromatic edge separation, a
// phosphor hum, and a vignette. Tuned to stay readable as a terminal.

const float CRT_CURVE     = 0.022; // barrel strength (kept subtle)
const float CRT_SCAN_PX   = 4.0;   // screen px per scanline
const float CRT_SCAN_BOLD = 0.45;  // scanline darkening at its peak
const float CRT_GRID_PX   = 2.0;   // screen px per pixel-grid cell
const float CRT_GRID      = 0.04;  // pixel grid darkening (a whisper)
const float CRT_HUM       = 0.012; // global brightness hum
const float CRT_CHROMA    = 0.0012;// chromatic offset at the edges
const float CRT_VIGNETTE  = 0.18;  // corner darkening

vec2 crtCurve(vec2 uv)
{
    // Centered coordinates, bulged outward by CURVE.
    vec2 c = uv * 2.0 - 1.0;
    c *= 1.0 + CRT_CURVE * dot(c, c);
    return c * 0.5 + 0.5;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = crtCurve(fragCoord.xy / iResolution.xy);

    // Outside the bulged tube: black bezel.
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
        fragColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    // Chromatic separation grows toward the edges.
    vec2 c = uv - 0.5;
    vec2 off = c * CRT_CHROMA * (1.0 + 4.0 * dot(c, c));

    vec3 col;
    col.r = texture(iChannel0, uv + off).r;
    col.g = texture(iChannel0, uv).g;
    col.b = texture(iChannel0, uv - off).b;

    // Scanlines, in fragCoord space (same basis as the grid below, which
    // is confirmed visible) so no iResolution/uv math can hide them:
    // static, hard-edged, 4px period, 45% darkening. If this is not
    // visible the shader path itself is broken, not the tuning.
    float scan = 0.5 + 0.5 * sin(fragCoord.y * 6.2831853 / CRT_SCAN_PX);
    // Dark only in the trough of each period (~1.4px of every 4): reads as
    // a scanline, not a band.
    float line = 1.0 - step(0.45, scan);
    col *= 1.0 - CRT_SCAN_BOLD * line;

    // Pixel grid: a faint static mask in screen space -- much subtler than
    // the dedicated Pixels shader, and no content quantization, so text
    // stays crisp.
    vec2 g = abs(fract(fragCoord.xy / CRT_GRID_PX) - 0.5) * 2.0;
    float edge = max(g.x, g.y);
    col *= 1.0 - CRT_GRID * smoothstep(0.6, 1.0, edge);

    // Slow phosphor hum.
    col *= 1.0 + CRT_HUM * sin(iTime * 2.1);

    // Vignette.
    float vig = 1.0 - CRT_VIGNETTE * dot(c, c) * 2.2;
    col *= vig;

    fragColor = vec4(col, 1.0);
}
