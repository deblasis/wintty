// Written for the wintty shader gallery. License: MIT (wintty project).
//
// Text glow: a soft bloom around bright pixels. Static effect; the glow
// follows whatever the terminal is showing, so it reads as luminescent
// text without touching anything else.

const float GLOW_THRESHOLD = 0.55; // luminance above which a pixel glows
const float GLOW_INTENSITY = 0.35; // how much glow is added back
const float GLOW_RADIUS    = 3.0;  // in pixels, at the base scale

float luma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }

vec3 brightPass(vec2 uv) {
    vec3 c = texture(iChannel0, uv).rgb;
    float l = luma(c);
    return c * smoothstep(GLOW_THRESHOLD, GLOW_THRESHOLD + 0.2, l);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    vec2 uv = fragCoord.xy / iResolution.xy;
    vec4 base = texture(iChannel0, uv);

    vec2 px = GLOW_RADIUS / iResolution.xy;

    // 12-tap ring blur of the bright pass, two radii for a softer falloff.
    vec3 glow = vec3(0.0);
    const int TAPS = 12;
    const float TAU = 6.28318530718;
    for (int r = 1; r <= 2; r++) {
        float scale = float(r) * 0.5;
        float w = 1.0 / float(r) / float(TAPS);
        for (int i = 0; i < TAPS; i++) {
            float a = (float(i) + 0.5) / float(TAPS) * TAU;
            vec2 off = vec2(cos(a), sin(a)) * px * scale;
            glow += brightPass(uv + off) * w;
        }
    }

    fragColor = vec4(base.rgb + glow * GLOW_INTENSITY, base.a);
}
