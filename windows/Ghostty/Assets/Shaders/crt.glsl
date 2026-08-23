// Written for the wintty shader gallery. License: MIT (wintty project).
//
// An original CRT look: gentle curvature, visible scanlines with a slow
// roll, light pixelation with a grid mask, chromatic edge separation,
// a phosphor hum, and a vignette. Tuned to stay readable as a terminal.

const float CRT_CURVE     = 0.022; // barrel strength (kept subtle)
const float CRT_ROWS      = 270.0; // scanlines top to bottom
const float CRT_SCAN_BOLD = 0.30;  // scanline darkening at its peak
const float CRT_ROLL      = 0.05;  // scan phase roll, cycles per second
const float CRT_PIXELS    = 170.0; // pixelation rows (blocks per height)
const float CRT_GRID      = 0.05;  // pixel grid line darkening
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

    // Pixelation: sample at the center of coarse blocks (blocks per
    // CRT_PIXELS rows, aspect-corrected columns).
    float aspect = iResolution.x / iResolution.y;
    vec2 grid = vec2(CRT_PIXELS * aspect, CRT_PIXELS);
    vec2 cell = floor(uv * grid) + 0.5;
    vec2 puv = cell / grid;

    // Chromatic separation grows toward the edges.
    vec2 c = uv - 0.5;
    vec2 off = c * CRT_CHROMA * (1.0 + 4.0 * dot(c, c));

    vec3 col;
    col.r = texture(iChannel0, puv + off).r;
    col.g = texture(iChannel0, puv).g;
    col.b = texture(iChannel0, puv - off).b;

    // Scanlines: the dominant texture, rolling slowly downward. The cubed
    // term narrows the dark crest so lines read as crisp CRT raster rows
    // rather than a soft gradient.
    float scan = sin(uv.y * CRT_ROWS * 6.2831853 - iTime * CRT_ROLL * 6.2831853);
    float line = pow(0.5 + 0.5 * scan, 3.0);
    col *= 1.0 - CRT_SCAN_BOLD * line;

    // Pixel grid: darken toward each block edge so the pixelation reads.
    vec2 f = abs(fract(uv * grid) - 0.5) * 2.0; // 0 center, 1 edge
    float edge = max(f.x, f.y);
    col *= 1.0 - CRT_GRID * smoothstep(0.72, 1.0, edge);

    // Slow phosphor hum.
    col *= 1.0 + CRT_HUM * sin(iTime * 2.1);

    // Vignette.
    float vig = 1.0 - CRT_VIGNETTE * dot(c, c) * 2.2;
    col *= vig;

    fragColor = vec4(col, 1.0);
}
