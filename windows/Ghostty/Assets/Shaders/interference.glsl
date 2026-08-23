// Written for the wintty shader gallery. License: MIT (wintty project).
//
// Fallout-style CRT interference: a broad interference band slowly rolls
// down the screen brightening and jittering everything it passes, rows
// twitch horizontally, static grain flickers, and rare tears displace a
// line for a beat. Tuned to stay readable as a terminal.

const float BAND_SPEED = 0.05;  // band cycles the screen every ~20s
const float BAND_H     = 0.10;  // band height (fraction of screen height)
const float JITTER_PX  = 2.5;   // baseline row twitch, pixels
const float GRAIN      = 0.06;  // static noise amplitude
const float TEAR_CHANCE = 0.006; // per row, per coarse tick
const vec3  PHOSPHOR   = vec3(0.25, 1.0, 0.35); // Pip-Boy green
const float TINT_MIX   = 0.85;  // how far toward monochrome green

float hash(vec2 p)
{
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord.xy / iResolution.xy;
    float row = floor(fragCoord.y);

    // Interference band: bright, unstable region rolling down the screen.
    float band_y = fract(iTime * BAND_SPEED);
    float in_band = smoothstep(BAND_H, 0.0, abs(uv.y - band_y));

    // Rows twitch horizontally; much harder inside the band.
    float tick = floor(iTime * 24.0);
    float n = hash(vec2(row, tick));
    float j = (n - 0.5) * JITTER_PX * (1.0 + in_band * 8.0);

    // Rare tear: one line jumps sideways for a single coarse beat.
    float tear = step(1.0 - TEAR_CHANCE, hash(vec2(row, floor(iTime * 3.0))));
    j += tear * (hash(vec2(row, 1.7)) - 0.5) * 16.0;

    vec3 col = texture(iChannel0, uv + vec2(j / iResolution.x, 0.0)).rgb;

    // Chroma split inside the band, like a mistracking signal.
    float ca = in_band * 1.5;
    col.r = mix(col.r, texture(iChannel0, uv + vec2((j + ca) / iResolution.x, 0.0)).r, 0.6);
    col.b = mix(col.b, texture(iChannel0, uv + vec2((j - ca) / iResolution.x, 0.0)).b, 0.6);

    // Static grain, denser in the band.
    float g = hash(fragCoord.xy + vec2(mod(iTime, 10.0) * 61.0, mod(iTime, 10.0) * 83.0)) - 0.5;
    col += g * (GRAIN + in_band * 0.10);

    // The band itself: brightness lift with a traveling shimmer.
    col *= 1.0 + in_band * (0.25 + 0.12 * sin(fragCoord.y * 1.7 + iTime * 40.0));

    // Pip-Boy phosphor: luminance mapped onto bright green, a little
    // bloom on the highlights. Mixed, not full-force, so syntax colors
    // in editors are tinted rather than erased.
    float lum = dot(col, vec3(0.2126, 0.7152, 0.0722));
    vec3 green = PHOSPHOR * (0.18 + 0.95 * lum) + vec3(0.0, 0.06 * lum * lum, 0.0);
    col = mix(col, green, TINT_MIX);

    fragColor = vec4(col, 1.0);
}
