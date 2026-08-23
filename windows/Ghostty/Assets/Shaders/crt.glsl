// Written for the wintty shader gallery. License: MIT (wintty project).
//
// An original CRT look: gentle barrel curvature, a rolling scanline,
// a slow phosphor hum, chromatic edge separation, and a vignette.
// Deliberately subtle so text stays crisp and readable.

const float CRT_CURVE     = 0.06;  // barrel strength
const float CRT_SCAN_BOLD = 0.10;  // scanline darkening at its peak
const float CRT_SCAN_ROWS = 320.0; // scanlines across the screen height
const float CRT_SCAN_ROLL = 0.09;  // scanline travel cycles per second
const float CRT_HUM       = 0.015; // global brightness hum
const float CRT_CHROMA    = 0.0012;// chromatic offset at the edges
const float CRT_VIGNETTE  = 0.22;  // corner darkening

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

    // Rolling scanline: a soft dark band traveling down the tube.
    float scan = sin((uv.y + iTime * CRT_SCAN_ROLL) * CRT_SCAN_ROWS * 6.2831853);
    col *= 1.0 - CRT_SCAN_BOLD * (0.5 + 0.5 * scan);

    // Fine interlace texture, static so it reads as a mask not motion.
    float mask = sin(uv.y * iResolution.y * 3.14159265);
    col *= 1.0 - 0.03 * mask;

    // Slow phosphor hum.
    col *= 1.0 + CRT_HUM * sin(iTime * 2.1);

    // Vignette.
    float vig = 1.0 - CRT_VIGNETTE * dot(c, c) * 2.2;
    col *= vig;

    fragColor = vec4(col, 1.0);
}
