// Written for the wintty shader gallery. License: MIT (wintty project).
//
// Aurora: a slow, dim aurora that shows through the terminal background.
// Bright text pixels are left alone; dark (background) pixels get tinted
// by moving noise bands. Readability first.

float hash(vec2 p) {
    p = fract(p * vec2(443.897, 441.423));
    p += dot(p, p + 19.19);
    return fract(p.x * p.y);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash(i);
    float b = hash(i + vec2(1.0, 0.0));
    float c = hash(i + vec2(0.0, 1.0));
    float d = hash(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 4; i++) {
        v += a * noise(p);
        p = p * 2.03 + vec2(11.7, 5.3);
        a *= 0.5;
    }
    return v;
}

float luma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }

void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    vec2 uv = fragCoord.xy / iResolution.xy;
    vec4 base = texture(iChannel0, uv);

    float t = iTime * 0.06;

    // Vertical bands drifting sideways; stretch x so bands look like curtains.
    vec2 q = vec2(uv.x * 2.5 - t * 0.8, uv.y * 1.8 + t * 0.3);
    float n = fbm(q);

    // Classic aurora palette: teal through green to a hint of violet.
    vec3 aurora = mix(vec3(0.05, 0.35, 0.30), vec3(0.10, 0.55, 0.25), n);
    aurora = mix(aurora, vec3(0.25, 0.20, 0.45), smoothstep(0.6, 1.0, n));

    // Stronger toward the top of the screen (wintty: small y is up top).
    float band = smoothstep(-0.2, 0.6, 1.0 - uv.y) * (0.55 + 0.45 * n);

    // Only tint dark pixels; bright text keeps its color.
    float bg = 1.0 - smoothstep(0.08, 0.35, luma(base.rgb));
    vec3 outc = mix(base.rgb, base.rgb * 0.4 + aurora * band, bg * 0.85);

    fragColor = vec4(outc, base.a);
}
