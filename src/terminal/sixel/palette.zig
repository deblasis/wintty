const std = @import("std");
const testing = std.testing;

pub const Rgba = packed struct(u32) {
    r: u8,
    g: u8,
    b: u8,
    a: u8,
};

/// DEC default 16-color palette.
///
/// These are the libsixel reference values for the VT340 palette
/// (the de facto target every modern sixel encoder writes for).
/// They do not round-trip through scale100to255 from the DEC VT3xx
/// manual's percentage table — the hardware-measured values diverge
/// slightly (e.g. red is documented as 80,13,13 but ships as
/// 204,36,36 in libsixel). Match the reference, not the manual.
pub const dec_default_palette: [16]Rgba = .{
    .{ .r = 0,   .g = 0,   .b = 0,   .a = 255 }, // 0: black
    .{ .r = 51,  .g = 51,  .b = 204, .a = 255 }, // 1: blue
    .{ .r = 204, .g = 36,  .b = 36,  .a = 255 }, // 2: red
    .{ .r = 51,  .g = 204, .b = 51,  .a = 255 }, // 3: green
    .{ .r = 204, .g = 51,  .b = 204, .a = 255 }, // 4: magenta
    .{ .r = 51,  .g = 204, .b = 204, .a = 255 }, // 5: cyan
    .{ .r = 204, .g = 204, .b = 51,  .a = 255 }, // 6: yellow
    .{ .r = 120, .g = 120, .b = 120, .a = 255 }, // 7: grey 50%
    .{ .r = 69,  .g = 69,  .b = 69,  .a = 255 }, // 8: grey 25%
    .{ .r = 92,  .g = 92,  .b = 158, .a = 255 }, // 9: blue*
    .{ .r = 158, .g = 92,  .b = 92,  .a = 255 }, // 10: red*
    .{ .r = 92,  .g = 158, .b = 92,  .a = 255 }, // 11: green*
    .{ .r = 158, .g = 92,  .b = 158, .a = 255 }, // 12: magenta*
    .{ .r = 92,  .g = 158, .b = 158, .a = 255 }, // 13: cyan*
    .{ .r = 158, .g = 158, .b = 92,  .a = 255 }, // 14: yellow*
    .{ .r = 204, .g = 204, .b = 204, .a = 255 }, // 15: grey 75%
};

/// 256-entry palette. Modern emitters use 256 registers per DEC
/// private extension; baseline DEC hardware was 16 registers.
pub const Palette = struct {
    entries: [256]Rgba,

    /// Build a fresh palette. Indices 0-15 hold the DEC default
    /// colors; indices 16-255 default to opaque black.
    pub fn init() Palette {
        var p: Palette = .{ .entries = undefined };
        for (0..16) |i| p.entries[i] = dec_default_palette[i];
        for (16..256) |i| p.entries[i] = .{ .r = 0, .g = 0, .b = 0, .a = 255 };
        return p;
    }

    /// Set register `idx` from a DEC RGB triple. Source values are
    /// 0-100; this scales to 0-255.
    pub fn setRgb(self: *Palette, idx: u8, r: u8, g: u8, b: u8) void {
        self.entries[idx] = .{
            .r = scale100to255(r),
            .g = scale100to255(g),
            .b = scale100to255(b),
            .a = 255,
        };
    }

    /// Set register `idx` from a DEC HLS triple.
    ///
    /// DEC HLS conventions:
    ///   H: 0-359 degrees canonical; taken mod 360 (so H=360 wraps to 0).
    ///      Hue 0=blue, 120=red, 240=green — rotated 120° from standard
    ///      HSL where 0=red.
    ///   L: 0-100, where 0=black, 50=full chroma, 100=white
    ///   S: 0-100, where 0=grayscale
    ///
    /// Source: DEC VT3xx Programmer Reference, libsixel reference impl.
    pub fn setHls(self: *Palette, idx: u8, h: u16, l: u8, s: u8) void {
        self.entries[idx] = hlsToRgba(h, l, s);
    }

    /// Look up register `idx`. u8 indexing guarantees in-range access.
    pub fn query(self: Palette, idx: u8) Rgba {
        return self.entries[idx];
    }
};

/// Scale a 0-100 DEC color value to 0-255. Saturates at 100.
/// The `+ 50` rounds to nearest instead of truncating, so 50/100
/// maps to 128 rather than 127. Matches libsixel's integer formula
/// `(v * 255 + 50) / 100` for bit-exact round-trip with the
/// reference encoder.
fn scale100to255(v: u8) u8 {
    const clamped = if (v > 100) 100 else v;
    return @intCast((@as(u32, clamped) * 255 + 50) / 100);
}

/// Convert a DEC HLS triple to RGBA. The DEC hue space is rotated
/// 120° from standard HSL so that hue=0 maps to DEC's blue. L=0
/// always returns black, L=100 always returns white, S=0 returns
/// grayscale at the requested L.
fn hlsToRgba(h_in: u16, l: u8, s: u8) Rgba {
    const h_mod: u16 = h_in % 360;
    const l_c: u32 = @min(l, 100);
    const s_c: u32 = @min(s, 100);

    if (l_c == 0) return .{ .r = 0, .g = 0, .b = 0, .a = 255 };
    if (l_c == 100) return .{ .r = 255, .g = 255, .b = 255, .a = 255 };
    if (s_c == 0) {
        const v = scale100to255(@intCast(l_c));
        return .{ .r = v, .g = v, .b = v, .a = 255 };
    }

    // Rotate DEC hue into standard HSL coordinate space. DEC hue 0
    // is blue; standard HSL blue lives at 240°. Adding 240 (mod 360)
    // maps DEC→HSL: 0→240 (blue), 120→0 (red), 240→120 (green).
    const h_std: f64 = @floatFromInt((h_mod + 240) % 360);
    const l_f: f64 = @as(f64, @floatFromInt(l_c)) / 100.0;
    const s_f: f64 = @as(f64, @floatFromInt(s_c)) / 100.0;

    const c: f64 = (1.0 - @abs(2.0 * l_f - 1.0)) * s_f;
    const h_sector: f64 = h_std / 60.0;
    const x: f64 = c * (1.0 - @abs(@mod(h_sector, 2.0) - 1.0));
    const m: f64 = l_f - c / 2.0;

    var r1: f64 = 0;
    var g1: f64 = 0;
    var b1: f64 = 0;
    if (h_sector < 1) { r1 = c; g1 = x; b1 = 0; }
    else if (h_sector < 2) { r1 = x; g1 = c; b1 = 0; }
    else if (h_sector < 3) { r1 = 0; g1 = c; b1 = x; }
    else if (h_sector < 4) { r1 = 0; g1 = x; b1 = c; }
    else if (h_sector < 5) { r1 = x; g1 = 0; b1 = c; }
    else { r1 = c; g1 = 0; b1 = x; }

    // The L/S clamps above mean (r1+m)*255 etc. can't escape [0, 255]
    // mathematically; the saturating min/max here is defensive against
    // float-rounding spillover near the bounds.
    return .{
        .r = @intFromFloat(@max(0.0, @min(255.0, @round((r1 + m) * 255.0)))),
        .g = @intFromFloat(@max(0.0, @min(255.0, @round((g1 + m) * 255.0)))),
        .b = @intFromFloat(@max(0.0, @min(255.0, @round((b1 + m) * 255.0)))),
        .a = 255,
    };
}

test "palette: init populates DEC 16 defaults" {
    const p = Palette.init();
    try testing.expectEqual(@as(u8, 0), p.entries[0].r);
    try testing.expectEqual(@as(u8, 255), p.entries[0].a);
    try testing.expectEqual(@as(u8, 204), p.entries[2].r); // red
}

test "palette: init zeros registers 16..255 to opaque black" {
    const p = Palette.init();
    try testing.expectEqual(@as(u8, 0), p.entries[16].r);
    try testing.expectEqual(@as(u8, 0), p.entries[16].g);
    try testing.expectEqual(@as(u8, 0), p.entries[16].b);
    try testing.expectEqual(@as(u8, 255), p.entries[16].a);
    try testing.expectEqual(@as(u8, 0), p.entries[255].r);
}

test "palette: setRgb scales 0-100 to 0-255" {
    var p = Palette.init();
    p.setRgb(0, 100, 50, 0);
    try testing.expectEqual(@as(u8, 255), p.entries[0].r);
    try testing.expectEqual(@as(u8, 128), p.entries[0].g);
    try testing.expectEqual(@as(u8, 0), p.entries[0].b);
}

test "palette: setRgb saturates values above 100" {
    var p = Palette.init();
    p.setRgb(0, 200, 100, 100);
    try testing.expectEqual(@as(u8, 255), p.entries[0].r);
}

test "palette: query returns set value" {
    var p = Palette.init();
    p.setRgb(42, 100, 100, 100);
    const rgba = p.query(42);
    try testing.expectEqual(@as(u8, 255), rgba.r);
    try testing.expectEqual(@as(u8, 255), rgba.g);
    try testing.expectEqual(@as(u8, 255), rgba.b);
}

test "palette: setHls H=0 L=0 S=0 is black" {
    var p = Palette.init();
    p.setHls(0, 0, 0, 0);
    const c = p.query(0);
    try testing.expectEqual(@as(u8, 0), c.r);
    try testing.expectEqual(@as(u8, 0), c.g);
    try testing.expectEqual(@as(u8, 0), c.b);
}

test "palette: setHls L=100 is white regardless of H,S" {
    var p = Palette.init();
    p.setHls(0, 180, 100, 100);
    const c = p.query(0);
    try testing.expectEqual(@as(u8, 255), c.r);
    try testing.expectEqual(@as(u8, 255), c.g);
    try testing.expectEqual(@as(u8, 255), c.b);
}

test "palette: setHls S=0 produces grayscale" {
    var p = Palette.init();
    p.setHls(0, 90, 50, 0);
    const c = p.query(0);
    try testing.expectEqual(@as(u8, 128), c.r);
    try testing.expectEqual(@as(u8, 128), c.g);
    try testing.expectEqual(@as(u8, 128), c.b);
}

test "palette: setHls H=0 L=50 S=100 is DEC blue" {
    var p = Palette.init();
    p.setHls(0, 0, 50, 100);
    const c = p.query(0);
    try testing.expectEqual(@as(u8, 0), c.r);
    try testing.expectEqual(@as(u8, 0), c.g);
    try testing.expectEqual(@as(u8, 255), c.b);
}

test "palette: setHls H=120 L=50 S=100 is DEC red" {
    var p = Palette.init();
    p.setHls(0, 120, 50, 100);
    const c = p.query(0);
    try testing.expectEqual(@as(u8, 255), c.r);
    try testing.expectEqual(@as(u8, 0), c.g);
    try testing.expectEqual(@as(u8, 0), c.b);
}

test "palette: setHls H=240 L=50 S=100 is DEC green" {
    var p = Palette.init();
    p.setHls(0, 240, 50, 100);
    const c = p.query(0);
    try testing.expectEqual(@as(u8, 0), c.r);
    try testing.expectEqual(@as(u8, 255), c.g);
    try testing.expectEqual(@as(u8, 0), c.b);
}

test "palette: setHls H=360 wraps to H=0" {
    var p = Palette.init();
    p.setHls(0, 360, 50, 100);
    const c = p.query(0);
    try testing.expectEqual(@as(u8, 0), c.r);
    try testing.expectEqual(@as(u8, 0), c.g);
    try testing.expectEqual(@as(u8, 255), c.b);
}

test "palette: setHls H=60 sits between DEC blue and red" {
    // H=60 in DEC is halfway between blue (0) and red (120) — should
    // produce a purple/magenta. Pins the rotation direction beyond the
    // primary hues.
    var p = Palette.init();
    p.setHls(0, 60, 50, 100);
    const c = p.query(0);
    // Both red and blue channels active, green absent.
    try testing.expect(c.r > 0);
    try testing.expect(c.b > 0);
    try testing.expectEqual(@as(u8, 0), c.g);
}

test "palette: setHls L=25 darker than L=50 at same hue" {
    var p1 = Palette.init();
    p1.setHls(0, 120, 50, 100);
    const c1 = p1.query(0);

    var p2 = Palette.init();
    p2.setHls(0, 120, 25, 100);
    const c2 = p2.query(0);

    try testing.expect(c2.r < c1.r);
    try testing.expectEqual(@as(u8, 0), c2.g);
    try testing.expectEqual(@as(u8, 0), c2.b);
}
