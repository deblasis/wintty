const std = @import("std");
const testing = std.testing;
const Allocator = std.mem.Allocator;
const Command = @import("command.zig").Command;
const Op = @import("command.zig").Op;
const palette_mod = @import("palette.zig");
const Palette = palette_mod.Palette;
const Rgba = palette_mod.Rgba;
const raster = @import("raster.zig");

const log = std.log.scoped(.terminal_sixel);

/// A decoded sixel image. Owns its RGBA buffer; caller releases via
/// `deinit`. Layout is row-major, 4 bytes per pixel (R, G, B, A).
pub const Image = struct {
    alloc: Allocator,
    rgba: []u8,
    width: u32,
    height: u32,

    pub fn deinit(self: *Image) void {
        self.alloc.free(self.rgba);
    }
};

/// Decoder context — environment data the decoder needs that isn't
/// in the Command itself.
pub const DecodeCtx = struct {
    /// Background color used by P1 mode for unpainted pixels.
    /// Defaults to opaque black if not set by caller.
    bg: Rgba = .{ .r = 0, .g = 0, .b = 0, .a = 255 },
    /// Maximum total RGBA bytes the decoder may allocate. Defaults
    /// to MAX_RGBA_BYTES (the same per-image cap raster.zig enforces).
    budget: usize = raster.MAX_RGBA_BYTES,
};

pub const Error = error{
    SixelTooLarge,
    OutOfMemory,
};

/// Decode a parsed sixel Command into an RGBA Image. The Palette
/// starts in its DEC default state; set_rgb/set_hls ops in the
/// stream mutate it as encountered.
pub fn decode(alloc: Allocator, cmd: Command, ctx: DecodeCtx) Error!Image {
    if (cmd.ops.len == 0) {
        return .{
            .alloc = alloc,
            .rgba = try alloc.alloc(u8, 0),
            .width = 0,
            .height = 0,
        };
    }
    _ = ctx;
    // Full implementation lands in a follow-on commit.
    return .{
        .alloc = alloc,
        .rgba = try alloc.alloc(u8, 0),
        .width = 0,
        .height = 0,
    };
}

test "decoder: empty Command yields 0x0 image" {
    const alloc = testing.allocator;
    var c = Command{
        .alloc = alloc,
        .raster = .{},
        .ops = try alloc.alloc(Op, 0),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    var img = try decode(alloc, c, .{});
    defer img.deinit();
    try testing.expectEqual(@as(u32, 0), img.width);
    try testing.expectEqual(@as(u32, 0), img.height);
    try testing.expectEqual(@as(usize, 0), img.rgba.len);
}
