//! GPU data structs shared between CPU and shader code.
//!
//! Layout must match Metal's because GenericRenderer writes the same bytes
//! for both backends. The comptime assertions guard against drift in this
//! file but do not cross-reference the Metal definitions; verify manually
//! if drift is suspected.
const std = @import("std");
const math = @import("../../math.zig");
const mirrors = @import("../shader_mirrors.zig");

/// The HLSL source the packoffset and mirror checks below both scan.
const hlsl_source = @embedFile("../shaders/hlsl/shaders.hlsl");

/// GPU uniform values for the cell shaders.
pub const Uniforms = extern struct {
    projection_matrix: math.Mat align(16),
    screen_size: [2]f32 align(8),
    cell_size: [2]f32 align(8),
    grid_size: [2]u16 align(4),
    /// The padding around the terminal grid in pixels. In order:
    /// top, right, bottom, left.
    grid_padding: [4]f32 align(16),
    padding_extend: PaddingExtend align(1),
    min_contrast: f32 align(4),
    cursor_pos: [2]u16 align(4),
    cursor_color: [4]u8 align(4),
    bg_color: [4]u8 align(4),

    bools: extern struct {
        cursor_wide: bool align(1),
        use_display_p3: bool align(1),
        use_linear_blending: bool align(1),
        use_linear_correction: bool align(1) = false,
    },

    const PaddingExtend = packed struct(u8) {
        left: bool = false,
        right: bool = false,
        up: bool = false,
        down: bool = false,
        _padding: u4 = 0,
    };
};

/// Single parameter for the cell text shader.
pub const CellText = extern struct {
    glyph_pos: [2]u32 align(8) = .{ 0, 0 },
    glyph_size: [2]u32 align(8) = .{ 0, 0 },
    bearings: [2]i16 align(4) = .{ 0, 0 },
    grid_pos: [2]u16 align(4),
    color: [4]u8 align(4),
    atlas: Atlas align(1),
    bools: packed struct(u8) {
        no_min_contrast: bool = false,
        is_cursor_glyph: bool = false,
        _padding: u6 = 0,
    } align(1) = .{},

    pub const Atlas = enum(u8) {
        grayscale = 0,
        color = 1,
    };
};

/// Single parameter for the cell bg shader.
pub const CellBg = [4]u8;

/// Single parameter for the image shader.
pub const Image = extern struct {
    grid_pos: [2]f32,
    cell_offset: [2]f32,
    source_rect: [4]f32,
    dest_size: [2]f32,
};

/// Single parameter for the bg image shader.
pub const BgImage = extern struct {
    opacity: f32 align(4),
    info: Info align(1),

    pub const Info = packed struct(u8) {
        position: Position,
        fit: Fit,
        repeat: bool,
        _padding: u1 = 0,

        pub const Position = enum(u4) {
            tl = 0,
            tc = 1,
            tr = 2,
            ml = 3,
            mc = 4,
            mr = 5,
            bl = 6,
            bc = 7,
            br = 8,
        };

        pub const Fit = enum(u2) {
            contain = 0,
            cover = 1,
            stretch = 2,
            none = 3,
        };
    };
};

// --- Uniforms vs the HLSL cbuffer -----------------------------------------
//
// shaders.hlsl packs the cbuffer with explicit packoffset because HLSL's
// default packing does not match C layout. The comptime block below parses
// those packoffsets out of the HLSL source and asserts `Uniforms` lands on
// every one, so the numbers live only in shaders.hlsl and drift on either
// side is a compile error rather than a misrendered frame.

/// Zig `Uniforms` field paired with the HLSL cbuffer field it must land on.
/// The HLSL name carries a `_packed` suffix where several Zig fields or a
/// packed struct share one 32-bit register component.
const uniforms_hlsl_fields = .{
    .{ "projection_matrix", "projection_matrix" },
    .{ "screen_size", "screen_size" },
    .{ "cell_size", "cell_size" },
    .{ "grid_size", "grid_size_packed" },
    .{ "grid_padding", "grid_padding" },
    .{ "padding_extend", "padding_extend_packed" },
    .{ "min_contrast", "min_contrast" },
    .{ "cursor_pos", "cursor_pos_packed" },
    .{ "cursor_color", "cursor_color_packed" },
    .{ "bg_color", "bg_color_packed" },
    .{ "bools", "bools_packed" },
};

/// The last identifier of `line`, i.e. the declared name in a
/// "type name : packoffset(...)" cbuffer line.
fn hlslDeclName(line: []const u8) []const u8 {
    var i: usize = line.len;
    while (i > 0 and (std.ascii.isAlphanumeric(line[i - 1]) or line[i - 1] == '_')) i -= 1;
    return line[i..];
}

/// Byte offset of `field`'s packoffset: a `: packoffset(cN[.xyzw])` maps to
/// register N * 16 plus the component (x/y/z/w = 0/4/8/12). Null when the
/// cbuffer does not declare `field`.
fn hlslPackoffset(source: []const u8, field: []const u8) ?usize {
    const marker = ": packoffset(c";
    var rest = source;
    while (std.mem.indexOf(u8, rest, marker)) |hit| {
        const decl = std.mem.trimEnd(u8, rest[0..hit], " \t");
        var i = hit + marker.len;
        var register: usize = 0;
        while (i < rest.len and std.ascii.isDigit(rest[i])) : (i += 1)
            register = register * 10 + (rest[i] - '0');
        var component: usize = 0;
        if (i + 1 < rest.len and rest[i] == '.') component = switch (rest[i + 1]) {
            'x' => 0,
            'y' => 4,
            'z' => 8,
            'w' => 12,
            else => 0,
        };
        if (std.mem.eql(u8, hlslDeclName(decl), field)) return register * 16 + component;
        rest = rest[hit + marker.len ..];
    }
    return null;
}

/// How many cbuffer fields `source` declares with a packoffset.
fn hlslPackoffsetCount(source: []const u8) usize {
    var n: usize = 0;
    var rest = source;
    while (std.mem.indexOf(u8, rest, ": packoffset(c")) |hit| {
        n += 1;
        rest = rest[hit + 1 ..];
    }
    return n;
}

/// The highest c-register any packoffset touches. A cbuffer spans whole
/// 16-byte registers, so its size in bytes is (this + 1) * 16.
fn hlslPackoffsetMaxRegister(source: []const u8) usize {
    const marker = ": packoffset(c";
    var max: usize = 0;
    var rest = source;
    while (std.mem.indexOf(u8, rest, marker)) |hit| {
        var i = hit + marker.len;
        var register: usize = 0;
        while (i < rest.len and std.ascii.isDigit(rest[i])) : (i += 1)
            register = register * 10 + (rest[i] - '0');
        max = @max(max, register);
        rest = rest[hit + 1 ..];
    }
    return max;
}

/// The mask the shader ANDs `bools_packed` with on the line that reads
/// `field` from it, e.g. 1 for cursor_wide's "(bools_packed & 1u)". Null
/// when `field` is not read from bools_packed.
fn hlslBoolsMask(source: []const u8, field: []const u8) ?u64 {
    var rest = source;
    while (std.mem.indexOf(u8, rest, field)) |hit| {
        const line_end = std.mem.indexOfScalarPos(u8, rest, hit, '\n') orelse rest.len;
        const line = rest[hit..line_end];
        rest = rest[line_end..];
        const amp = std.mem.indexOf(u8, line, "bools_packed & ") orelse continue;
        const digits = line[amp + "bools_packed & ".len ..];
        var end: usize = 0;
        while (end < digits.len and (std.ascii.isAlphanumeric(digits[end]) or digits[end] == '_')) end += 1;
        const literal = std.mem.trimEnd(u8, digits[0..end], "uU");
        return std.fmt.parseInt(u64, literal, 0) catch null;
    }
    return null;
}

comptime {
    // The HLSL scans below walk the whole shader source per field; the
    // default comptime branch quota does not cover that.
    @setEvalBranchQuota(1_000_000);
    std.debug.assert(@sizeOf(CellText) == 32);
    std.debug.assert(@offsetOf(CellText, "bools") == 29);
    std.debug.assert(@sizeOf(Image) == 40);
    std.debug.assert(@offsetOf(Image, "dest_size") == 32);
    std.debug.assert(@sizeOf(BgImage) == 8);
    std.debug.assert(@offsetOf(BgImage, "info") == 4);

    // Uniforms must land byte for byte on the cbuffer packoffsets in
    // shaders.hlsl: GenericRenderer writes the struct straight into the
    // constant buffer the shaders read. The two count checks catch a field
    // added on one side only, in either direction.
    if (uniforms_hlsl_fields.len != @typeInfo(Uniforms).@"struct".fields.len)
        @compileError(std.fmt.comptimePrint(
            "Uniforms has {d} fields but uniforms_hlsl_fields maps {d} to HLSL cbuffer fields; every field needs a mapping",
            .{ @typeInfo(Uniforms).@"struct".fields.len, uniforms_hlsl_fields.len },
        ));
    if (hlslPackoffsetCount(hlsl_source) != uniforms_hlsl_fields.len)
        @compileError(std.fmt.comptimePrint(
            "shaders.hlsl declares {d} cbuffer packoffset fields but uniforms_hlsl_fields maps {d}; a field was added on one side only",
            .{ hlslPackoffsetCount(hlsl_source), uniforms_hlsl_fields.len },
        ));
    for (uniforms_hlsl_fields) |pair| {
        const offset = hlslPackoffset(hlsl_source, pair[1]) orelse
            @compileError(std.fmt.comptimePrint(
                "Uniforms.{s} maps to cbuffer field {s}, which shaders.hlsl does not declare",
                .{ pair[0], pair[1] },
            ));
        if (@offsetOf(Uniforms, pair[0]) != offset)
            @compileError(std.fmt.comptimePrint(
                "Uniforms.{s} sits at byte {d} but shaders.hlsl packs {s} at byte {d}",
                .{ pair[0], @offsetOf(Uniforms, pair[0]), pair[1], offset },
            ));
    }
    const hlsl_cbuffer_bytes = (hlslPackoffsetMaxRegister(hlsl_source) + 1) * 16;
    if (@sizeOf(Uniforms) != hlsl_cbuffer_bytes)
        @compileError(std.fmt.comptimePrint(
            "Uniforms is {d} bytes but the shaders.hlsl cbuffer spans {d} bytes ({d} 16-byte registers)",
            .{ @sizeOf(Uniforms), hlsl_cbuffer_bytes, hlsl_cbuffer_bytes / 16 },
        ));

    // The pixel shader reads CellText.bools as raw bits through these
    // defines, so each packed field needs its exact mask there.
    for (std.meta.fields(@FieldType(CellText, "bools")), 0..) |field, bit| {
        if (std.mem.startsWith(u8, field.name, "_")) continue;
        mirrors.expectMirrored(
            hlsl_source,
            mirrors.upper(field.name),
            std.fmt.comptimePrint("{d}u", .{@as(u32, 1) << @as(u5, @intCast(bit))}),
            "shaders.hlsl CellText.bools vs directx12.CellText",
        );
    }

    // No BgImage.Info position/fit check: the HLSL bg_image pipeline takes
    // the info byte but never decodes it, so shaders.hlsl declares no
    // BG_IMAGE_* constants to check against.
}

comptime {
    // The shader reads the bool flags as bytes of one little-endian uint
    // (bools_packed), so each flag must stay one byte and cursor_wide must
    // sit on the bit of bools_packed its byte position implies. Kept in a
    // block of its own so a layout failure above does not mask these.
    @setEvalBranchQuota(1_000_000);
    const bools_ty = @FieldType(Uniforms, "bools");
    if (@sizeOf(bools_ty) != @typeInfo(bools_ty).@"struct".fields.len)
        @compileError("Uniforms.bools must stay one byte per flag: shaders.hlsl reads the flags as bytes of one uint");
    const wide_index = std.meta.fieldIndex(bools_ty, "cursor_wide") orelse
        @compileError("Uniforms.bools must keep a cursor_wide flag");
    const wide_mask: u64 = @as(u64, 1) << @intCast(8 * wide_index);
    const shader_mask = hlslBoolsMask(hlsl_source, "cursor_wide") orelse
        @compileError("shaders.hlsl must read cursor_wide as a bit of bools_packed");
    if (shader_mask != wide_mask)
        @compileError(std.fmt.comptimePrint(
            "shaders.hlsl reads cursor_wide as bools_packed & {d} but it is flag {d} of Uniforms.bools, i.e. mask {d}",
            .{ shader_mask, wide_index, wide_mask },
        ));
}
