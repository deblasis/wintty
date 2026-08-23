// Written for the wintty shader gallery. License: MIT (wintty project).
//
// Lightning strike: every time the cursor jumps, a jagged bolt drops from
// the top edge down to the new cursor cell, forking twice on the way, and
// lands with a flash and bloom on the character it hits. White-hot core
// with a cursor-colored glow, a per-frame flicker, and a short decaying
// afterglow. Everything is additive, so the text stays readable. Each
// strike is seeded from iTimeCursorChange, so every jump draws a
// different bolt.
//
// Kept deliberately branchless. Written around a zioshade v0.6.x bug
// that sank the incoming store of a local modified inside a conditional
// containing a loop, so the not-taken path read an uninitialized
// variable and the idle frame rendered black. The store-sink is fixed
// as of v0.8.2; the shape stays because it costs nothing extra. The
// gate is a multiplier instead, and the loop trip counts collapse to
// zero when the effect is idle so the per-pixel cost only lands during
// a strike.

const float DURATION       = 0.30; // total effect, seconds
const float STRIKE_FRAC    = 0.30; // fraction of DURATION the bolt travels
const int   MAIN_SEGS      = 20;   // polyline segments of the main channel
const int   BRANCH_SEGS    = 7;    // polyline segments of each fork
const float CORE_WIDTH     = 1.8;  // px, white-hot line
const float GLOW_WIDTH     = 9.0;  // px, colored halo falloff
const float JUMP_THRESHOLD = 0.30; // min corner jump (cell heights) to fire
const float BOLT_BRIGHT    = 0.85;
const float FLASH_BRIGHT   = 1.30;

float hash1(float n)
{
    return fract(sin(n) * 43758.5453123);
}

// Piecewise-linear value noise: sharp zigzag kinks, right for lightning.
// Centered on zero.
float zig1(float x)
{
    float i = floor(x);
    float f = fract(x);
    return mix(hash1(i), hash1(i + 1.0), f) - 0.5;
}

float sdSegment(vec2 p, vec2 a, vec2 b)
{
    vec2 e = b - a;
    vec2 q = p - a;
    float t = clamp(dot(q, e) / max(dot(e, e), 1.0), 0.0, 1.0);
    return length(q - e * t);
}

float sdRect(vec2 p, vec2 center, vec2 halfSize)
{
    vec2 d = abs(p - center) - halfSize;
    return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0);
}

// Point on the main channel at t in [0,1]: 0 = top edge start, 1 = impact.
// The wander is pinned to both endpoints so the bolt always starts free
// and always hits the cursor dead on.
vec2 boltPoint(float t, float xTop, vec2 impact, float seed, float amp)
{
    float env = sin(t * 3.14159265);
    float x = mix(xTop, impact.x, t)
            + zig1(t * 6.0 + seed) * amp * env
            + zig1(t * 19.0 + seed * 1.7) * amp * 0.35 * env;
    return vec2(x, mix(0.0, impact.y, t));
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord.xy / iResolution.xy;
    vec3 col = texture(iChannel0, uv).rgb;

    // Cursor geometry in pixels (y down): xy = left edge + bottom edge of
    // the cell, zw = size, so the center is (x+w/2, y-h/2).
    vec2 impact = iCurrentCursor.xy
                + vec2(iCurrentCursor.z, -iCurrentCursor.w) * 0.5;
    vec2 cellHalf = iCurrentCursor.zw * 0.5;
    float cellH = max(iCurrentCursor.w, iPreviousCursor.w);

    // Gate on the corner, not the center: shape flips resize the rect
    // around a fixed corner, only a real move shifts it.
    float cornerJump = length(iCurrentCursor.xy - iPreviousCursor.xy);
    float age = iTime - iTimeCursorChange;
    float active = step(cellH * JUMP_THRESHOLD, cornerJump)
                 * step(age, DURATION) * step(0.0, age);
    float p = clamp(age / DURATION, 0.0, 1.0);
    vec3 tint = iCurrentCursorColor.rgb;
    vec3 boltCol = mix(tint, vec3(1.0), 0.55);

    // Seed the whole bolt from the change time: every strike is new.
    float seed = iTimeCursorChange * 12.9898;
    float xTop = impact.x
               + (hash1(seed + 1.3) - 0.5) * iResolution.x * 0.8;
    float amp = clamp(distance(impact, vec2(xTop, 0.0)) * 0.16,
                      12.0, 80.0);

    // Growth front: where the bolt has reached on its way down.
    float g = clamp(p / STRIKE_FRAC, 0.0, 1.0);
    // Per-frame flicker keeps the channel alive while it glows.
    float flicker = 0.7 + 0.5 * hash1(seed + floor(iTime * 48.0) * 0.618);
    // Full brightness while traveling, then a decaying afterglow.
    float boltEnv = exp(-max(p - STRIKE_FRAC, 0.0) * 6.0) * flicker * active;

    // Loop trip counts collapse to zero when idle.
    int mainSegs = int(active) * MAIN_SEGS;
    int branchSegs = int(active) * BRANCH_SEGS;

    float glowI = 0.0;
    float coreI = 0.0;

    // Main channel: a jagged polyline from the top edge to the cursor.
    // Segments ahead of the growth front stay dark.
    for (int i = 0; i < mainSegs; i++) {
        float t0 = float(i) / float(MAIN_SEGS);
        float t1 = float(i + 1) / float(MAIN_SEGS);
        vec2 a = boltPoint(t0, xTop, impact, seed, amp);
        vec2 b = boltPoint(t1, xTop, impact, seed, amp);
        float lit = 1.0 - smoothstep(g - 0.08, g, t0);
        float d = sdSegment(fragCoord.xy, a, b);
        glowI = max(glowI, lit * exp(-d / GLOW_WIDTH));
        coreI = max(coreI, lit * exp(-d / CORE_WIDTH));
    }

    // Two forks: they split off the main channel where the front has
    // already passed, angle away, and fade sooner.
    for (int k = 0; k < 2; k++) {
        float fk = float(k);
        float t0 = 0.2 + 0.5 * hash1(seed + 7.3 + fk * 11.1);
        vec2 start = boltPoint(t0, xTop, impact, seed, amp);
        float ang = (0.7 + 0.6 * hash1(seed + 3.1 + fk * 5.7))
                  * (fk * 2.0 - 1.0);
        vec2 v = (impact - start) * 0.35;
        vec2 end = start + vec2(v.x * cos(ang) - v.y * sin(ang),
                                v.x * sin(ang) + v.y * cos(ang));
        float bSeed = seed + 31.0 + fk * 17.0;
        float bAmp = amp * 0.5;
        // A fork lights up only after the main front passes its root.
        float lit0 = smoothstep(g, g - 0.15, t0);
        for (int j = 0; j < branchSegs; j++) {
            float s0 = float(j) / float(BRANCH_SEGS);
            float s1 = float(j + 1) / float(BRANCH_SEGS);
            vec2 a = mix(start, end, s0)
                   + vec2(zig1(s0 * 5.0 + bSeed),
                          zig1(s0 * 5.0 + bSeed + 13.0) * 0.6)
                     * bAmp * s0;
            vec2 b = mix(start, end, s1)
                   + vec2(zig1(s1 * 5.0 + bSeed),
                          zig1(s1 * 5.0 + bSeed + 13.0) * 0.6)
                     * bAmp * s1;
            float d = sdSegment(fragCoord.xy, a, b);
            glowI = max(glowI, lit0 * 0.6 * exp(-d / (GLOW_WIDTH * 0.8)));
            coreI = max(coreI, lit0 * 0.5 * exp(-d / CORE_WIDTH));
        }
    }

    col += tint * glowI * boltEnv * BOLT_BRIGHT;
    col += boltCol * coreI * boltEnv;

    // Impact: the flash fires as the front lands and decays from there;
    // a radial bloom plus a bright cell hit the character.
    float flashEnv = smoothstep(STRIKE_FRAC * 0.8, STRIKE_FRAC, p)
                   * exp(-max(p - STRIKE_FRAC, 0.0) * 9.0) * active;
    float dImp = distance(fragCoord.xy, impact);
    float sdfCell = sdRect(fragCoord.xy, impact, cellHalf);
    col += mix(tint, vec3(1.0), 0.6)
         * (exp(-dImp / max(cellH * 1.4, 8.0))
          + exp(-max(sdfCell, 0.0) / max(cellH * 0.25, 2.0)))
         * flashEnv * FLASH_BRIGHT;

    fragColor = vec4(col, 1.0);
}
