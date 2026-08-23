// Written for the wintty shader gallery. License: MIT (wintty project).
//
// Cursor teleport: when the cursor jumps between cells it blinks out at
// the old position, a ghost streak smears the frame along the jump path
// (iChannel0 sampled back along the direction of travel), and the cursor
// materializes at the new position with a short flash. The travel curve
// leaves fast and settles, so the streak reads as a teleport, not a
// slide. Fires on position jumps (arrows, clicks, prompt hops), not on
// shape flips: the cursor's bottom-left corner only moves when the cell
// moves.
//
// Kept deliberately branchless. Written around a zioshade v0.6.x bug
// that sank the incoming store of a local modified inside a conditional
// containing a loop, so the not-taken path read an uninitialized
// variable and the idle frame rendered black. The store-sink is fixed
// as of v0.8.2; the shape stays because it costs nothing extra. The
// ghost loop runs unconditionally (8 taps, same shape as text_glow) and
// the whole effect is gated by multipliers.

const float DURATION       = 0.22; // total effect, seconds
const int   GHOST_TAPS     = 8;    // samples averaged into the streak
const float JUMP_THRESHOLD = 0.30; // min corner jump (cell heights) to fire
const float GHOST_STRENGTH = 0.85; // streak opacity at its peak
const float OUT_FLASH      = 1.60; // departure flash brightness
const float IN_FLASH       = 1.60; // arrival flash brightness
const float BEAM_BRIGHT    = 0.90; // traveling beam brightness

float easeOutQuart(float x) {
    return 1.0 - pow(1.0 - x, 4.0);
}

float sdRect(vec2 p, vec2 center, vec2 halfSize) {
    vec2 d = abs(p - center) - halfSize;
    return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0);
}

float sdSegment(vec2 p, vec2 a, vec2 b) {
    vec2 e = b - a;
    vec2 q = p - a;
    float t = clamp(dot(q, e) / max(dot(e, e), 1.0), 0.0, 1.0);
    return length(q - e * t);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord.xy / iResolution.xy;
    vec3 col = texture(iChannel0, uv).rgb;

    // Cursor geometry in pixels. Convention (y down): xy = left edge +
    // bottom edge of the cell, zw = size, so the center is (x+w/2, y-h/2).
    vec2 curCenter  = iCurrentCursor.xy
                    + vec2(iCurrentCursor.z, -iCurrentCursor.w) * 0.5;
    vec2 prevCenter = iPreviousCursor.xy
                    + vec2(iPreviousCursor.z, -iPreviousCursor.w) * 0.5;
    vec2 curHalf = iCurrentCursor.zw * 0.5;

    float cellH = max(iCurrentCursor.w, iPreviousCursor.w);
    vec2 jump = curCenter - prevCenter;

    // Gate on the corner, not the center: shape flips (bar/block/underline)
    // resize the rect around a fixed corner, only a real move shifts it.
    float cornerJump = length(iCurrentCursor.xy - iPreviousCursor.xy);
    float age = iTime - iTimeCursorChange;
    float active = step(cellH * JUMP_THRESHOLD, cornerJump)
                 * step(age, DURATION) * step(0.0, age);
    float p = clamp(age / DURATION, 0.0, 1.0);
    float e = easeOutQuart(p); // travel: leaves fast, settles in
    float fade = 1.0 - p;
    vec3 tint = iCurrentCursorColor.rgb;

    // 1) Ghost streak: blur the frame along the jump direction. The
    // sampling span covers the whole path at p=0 and collapses onto the
    // current pixel as the teleport resolves, so text smears and then
    // snaps back into focus.
    vec2 head = mix(prevCenter, curCenter, e);
    float dLine = sdSegment(fragCoord.xy, prevCenter, head);
    float band = 1.0 - smoothstep(cellH * 0.7, cellH * 2.0, dLine);
    vec3 acc = vec3(0.0);
    float wsum = 0.0;
    for (int i = 0; i < GHOST_TAPS; i++) {
        float t = (float(i) + 0.5) / float(GHOST_TAPS);
        float w = 1.0 - t * 0.6;
        vec2 off = -jump * (1.0 - e) * t;
        acc += texture(iChannel0, uv + off / iResolution.xy).rgb * w;
        wsum += w;
    }
    vec3 ghost = acc / wsum;
    // The streak lives in the band only; keep the cursor cell crisp.
    float hole = 1.0 - step(0.0, sdRect(fragCoord.xy, curCenter, curHalf * 1.05));
    float streakA = band * fade * GHOST_STRENGTH * hole * active;
    vec3 newColor = mix(col, ghost, streakA);
    // A tint over the band so it reads as energy, not smudge.
    newColor += mix(tint, vec3(1.0), 0.25) * band * fade * 0.30
              * hole * active;

    // 1b) Traveling beam: a bright core inside the band, running from
    // the old position to the easing head. It spans the whole path at
    // p=0 and collapses into the arrival point. The radius is sized by
    // the cell, not the jump, so single-cell moves still get a fat
    // visible streak.
    float beam = exp(-dLine / max(cellH * 0.30, 3.0)) * fade * hole * active;
    newColor += mix(tint, vec3(1.0), 0.5) * beam * BEAM_BRIGHT;

    // 2) Departure: the old cell flashes and collapses to nothing over
    // the first 30% of the animation.
    float outP = clamp(p / 0.30, 0.0, 1.0);
    vec2 oldHalf = max(curHalf * (1.0 - outP), vec2(0.0));
    float sdfOld = sdRect(fragCoord.xy, prevCenter, oldHalf);
    float outGlow = exp(-max(sdfOld, 0.0) / max(cellH * 0.50, 3.0));
    newColor += mix(tint, vec3(1.0), 0.4) * outGlow * (1.0 - outP)
              * (1.0 - outP) * OUT_FLASH * active;

    // 3) Arrival: a pulse of light where the cursor materializes,
    // peaking around 65% and gone by the end.
    float inP = clamp((p - 0.30) / 0.70, 0.0, 1.0);
    float pulse = sin(inP * 3.14159265);
    float sdfNew = sdRect(fragCoord.xy, curCenter,
                          curHalf * (1.0 + 0.35 * pulse));
    float inGlow = exp(-max(sdfNew, 0.0) / max(cellH * 0.45, 3.0));
    newColor += mix(tint, vec3(1.0), 0.6) * inGlow * pulse
              * IN_FLASH * active;

    fragColor = vec4(newColor, 1.0);
}
