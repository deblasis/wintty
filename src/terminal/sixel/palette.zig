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

    pub fn query(self: Palette, idx: u8) Rgba {
        return self.entries[idx];
    }
};

/// Scale a 0-100 DEC color value to 0-255. Saturates at 100.
/// The `+ 50` rounds to nearest instead of truncating, so 50/100
/// maps to 128 rather than 127.
fn scale100to255(v: u8) u8 {
    const clamped = if (v > 100) 100 else v;
    return @intCast((@as(u32, clamped) * 255 + 50) / 100);
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
