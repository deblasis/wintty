// Written for the wintty shader gallery. License: MIT (wintty project).
//
// Snowfall: three parallax layers of drifting flakes over the terminal.
// Subtle by design; the terminal stays fully readable.
//
// Convention note: wintty passes fragCoord with the origin at the
// top-left and +Y pointing down, so the snow drifts toward +Y.

float hash(vec2 p) {
    p = fract(p * vec2(443.897, 441.423));
    p += dot(p, p + 19.19);
    return fract(p.x * p.y);
}

// One snow layer: repeat a grid of cells, each holding one flake that
// falls (y grows with time) and sways. Returns the flake's coverage.
float snowLayer(vec2 uv, float scale, float speed, float sway, float t) {
    vec2 p = uv * scale;
    p.y -= t * speed;         // fall
    p.x += sin(t * sway + p.y * 0.35) * sway; // drift

    vec2 cell = floor(p);
    vec2 f = fract(p);

    // Flake position inside the cell, per-cell random.
    vec2 flake = vec2(hash(cell), hash(cell + 7.31));
    flake = mix(vec2(0.2), vec2(0.8), flake);

    float d = length(f - flake);
    // Soft dot; a touch of twinkle.
    float twinkle = 0.75 + 0.25 * sin(t * 2.0 + hash(cell + 3.7) * 6.28);
    return smoothstep(0.14, 0.02, d) * twinkle;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    vec2 uv = fragCoord.xy / iResolution.xy;

    vec4 base = texture(iChannel0, uv);

    float t = iTime;
    // Parallax: far layers are smaller, slower, dimmer.
    float snow = 0.0;
    snow += snowLayer(uv + vec2(0.00, 0.00), 14.0, 0.06, 0.15, t) * 0.25;
    snow += snowLayer(uv + vec2(0.03, 0.00), 22.0, 0.10, 0.25, t) * 0.30;
    snow += snowLayer(uv + vec2(0.00, 0.00), 32.0, 0.16, 0.35, t) * 0.35;

    // Cool white flakes, capped so text stays dominant.
    vec3 flakeCol = vec3(0.92, 0.95, 1.0) * clamp(snow, 0.0, 0.6);

    fragColor = vec4(base.rgb + flakeCol * (1.0 - base.rgb * 0.35), base.a);
}
