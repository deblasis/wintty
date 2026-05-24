const std = @import("std");
const testing = std.testing;
const Raster = @import("command.zig").Raster;

/// Per-image RGBA byte budget. 48 MiB ≈ 3500×3500 RGBA, the upper
/// bound we're willing to allocate per inline image. The DCS layer's
/// 1 MiB source-byte cap (`dcs.Handler.max_bytes`) already bounds
/// pathological inputs; this catches large *declared* geometries
/// before the decoder allocates.
pub const MAX_RGBA_BYTES: usize = 48 * 1024 * 1024;

pub const Error = error{
    /// Declared geometry would exceed MAX_RGBA_BYTES.
    SixelTooLarge,
    /// Raster attribs had a malformed parameter (non-digit, overflow,
    /// etc.). Parser recovers by ignoring the raster and emitting
    /// defaults.
    Malformed,
};

/// Parse a raster-attribs body — the bytes between `"` and the next
/// non-attribute byte. Caller passes the slice WITHOUT the leading
/// `"` AND WITHOUT the terminating non-digit byte (the parser
/// upstream strips both). Format: `Pa;Pb;Ph;Pn3;Pn4` (all optional).
///
/// On `error.SixelTooLarge`, the declared geometry exceeded the
/// budget; the caller should reject the whole sixel image.
///
/// On `error.Malformed`, the raster header was unparseable; the
/// caller should emit a default `Raster{}` and continue parsing
/// paint ops.
pub fn parseRasterAttribs(body: []const u8) Error!Raster {
    var r = Raster{};
    var fields: [5]?u16 = .{ null, null, null, null, null };
    var field_idx: usize = 0;
    var acc: u32 = 0;
    var has_digit: bool = false;

    for (body) |b| {
        switch (b) {
            '0'...'9' => {
                acc = acc * 10 + (b - '0');
                if (acc > std.math.maxInt(u16)) return error.Malformed;
                has_digit = true;
            },
            ';' => {
                if (field_idx >= fields.len) return error.Malformed;
                fields[field_idx] = if (has_digit) @intCast(acc) else null;
                field_idx += 1;
                acc = 0;
                has_digit = false;
            },
            else => return error.Malformed,
        }
    }
    if (field_idx < fields.len) {
        fields[field_idx] = if (has_digit) @intCast(acc) else null;
    } else if (has_digit) {
        // All five slots filled and there's still digit content waiting
        // — the input declared a sixth field, which is not valid DEC.
        // Without this branch the loop's `field_idx >= fields.len`
        // guard never fires (it only triggers on a sixth `;`), so the
        // trailing digits would be silently dropped.
        return error.Malformed;
    }

    if (fields[0]) |v| r.aspect_num = if (v == 0) 1 else v;
    if (fields[1]) |v| r.aspect_den = if (v == 0) 1 else v;
    if (fields[2]) |v| r.grid_size = v;
    if (fields[3]) |v| r.declared_width = v;
    if (fields[4]) |v| r.declared_height = v;

    // Geometry budget check (only applies if both dims declared).
    if (r.declared_width > 0 and r.declared_height > 0) {
        const bytes = @as(usize, r.declared_width) *
            @as(usize, r.declared_height) * 4;
        if (bytes > MAX_RGBA_BYTES) return error.SixelTooLarge;
    }

    return r;
}

test "raster: empty body returns defaults" {
    const r = try parseRasterAttribs("");
    try testing.expectEqual(@as(u16, 1), r.aspect_num);
    try testing.expectEqual(@as(u16, 1), r.aspect_den);
}

test "raster: 1;1;0;100;200 sets declared dims" {
    const r = try parseRasterAttribs("1;1;0;100;200");
    try testing.expectEqual(@as(u16, 100), r.declared_width);
    try testing.expectEqual(@as(u16, 200), r.declared_height);
}

test "raster: trailing semicolons leave fields null" {
    const r = try parseRasterAttribs("2;1");
    try testing.expectEqual(@as(u16, 2), r.aspect_num);
    try testing.expectEqual(@as(u16, 1), r.aspect_den);
    try testing.expectEqual(@as(u16, 0), r.grid_size);
}

test "raster: null first field is skipped" {
    // ";1;1" — empty first field stays at struct default (no field
    // write); only the second and third fields populate.
    const r = try parseRasterAttribs(";1;1");
    try testing.expectEqual(@as(u16, 1), r.aspect_num); // struct default
    try testing.expectEqual(@as(u16, 1), r.aspect_den);
}

test "raster: zero-coerce first field exercises v==0 branch" {
    // "0;1;1" — explicit 0 trips the if (v == 0) 1 else v coercion,
    // distinct from the null path above.
    const r = try parseRasterAttribs("0;1;1");
    try testing.expectEqual(@as(u16, 1), r.aspect_num); // coerced from 0
    try testing.expectEqual(@as(u16, 1), r.aspect_den);
}

test "raster: zero aspect coerces to 1" {
    const r = try parseRasterAttribs("0;0");
    try testing.expectEqual(@as(u16, 1), r.aspect_num);
    try testing.expectEqual(@as(u16, 1), r.aspect_den);
}

test "raster: oversized geometry returns SixelTooLarge" {
    // 8192 × 8192 × 4 = 256 MiB > 48 MiB cap
    const err = parseRasterAttribs("1;1;0;8192;8192");
    try testing.expectError(error.SixelTooLarge, err);
}

test "raster: at-cap geometry succeeds" {
    // 3500 × 3500 × 4 ≈ 46.7 MiB, under 48 MiB
    const r = try parseRasterAttribs("1;1;0;3500;3500");
    try testing.expectEqual(@as(u16, 3500), r.declared_width);
}

test "raster: malformed non-digit returns Malformed" {
    const err = parseRasterAttribs("1;abc");
    try testing.expectError(error.Malformed, err);
}

test "raster: u16 overflow returns Malformed" {
    // 99999 > u16 max
    const err = parseRasterAttribs("99999");
    try testing.expectError(error.Malformed, err);
}

test "raster: too many fields returns Malformed" {
    const err = parseRasterAttribs("1;2;3;4;5;6");
    try testing.expectError(error.Malformed, err);
}
