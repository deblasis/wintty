//! Shader management and GPU data structs for DX11.
//!
//! The data structs (Uniforms, CellText, CellBg, Image, BgImage) must match
//! Metal's layout since GenericRenderer writes to them directly. The actual
//! HLSL shaders will interpret these differently but the CPU-side layout is
//! shared across backends.
//!
//! TODO: add comptime assertions cross-referencing @sizeOf against Metal's
//! data structs to catch layout drift across backends.
const std = @import("std");
const math = @import("../../math.zig");

const Pipeline = @import("Pipeline.zig");

/// Shader management for DX11.
/// TODO: Implement HLSL shader compilation and caching.
pub const Shaders = struct {
    pipelines: Pipelines = .{},
    post_pipelines: []const Pipeline = &.{},
    defunct: bool = false,

    pub const Pipelines = struct {
        bg_color: Pipeline = .{},
        cell_bg: Pipeline = .{},
        cell_text: Pipeline = .{},
        image: Pipeline = .{},
        bg_image: Pipeline = .{},
    };

    pub fn deinit(self: *Shaders, alloc: std.mem.Allocator) void {
        _ = self;
        _ = alloc;
        @panic("TODO: DX11 Shaders.deinit");
    }
};

/// GPU uniform values for the cell shaders.
pub const Uniforms = extern struct {
    projection_matrix: math.Mat align(16),
    screen_size: [2]f32 align(8),
    cell_size: [2]f32 align(8),
    grid_size: [2]u16 align(4),
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

    test {
        try std.testing.expectEqual(32, @sizeOf(CellText));
    }
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
