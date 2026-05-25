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
///
/// Two-pass algorithm: first pass measures the bounding box without
/// allocating, second pass allocates the RGBA buffer and walks the
/// op stream painting each sixel byte's 6-pixel vertical column.
pub fn decode(alloc: Allocator, cmd: Command, ctx: DecodeCtx) Error!Image {
    if (cmd.ops.len == 0) {
        return .{
            .alloc = alloc,
            .rgba = try alloc.alloc(u8, 0),
            .width = 0,
            .height = 0,
        };
    }

    const bounds = measureBounds(cmd);
    const w: u32 = if (cmd.raster.declared_width > 0)
        @min(bounds.w, cmd.raster.declared_width)
    else
        bounds.w;
    const h: u32 = if (cmd.raster.declared_height > 0)
        @min(bounds.h, cmd.raster.declared_height)
    else
        bounds.h;

    if (w == 0 or h == 0) {
        return .{
            .alloc = alloc,
            .rgba = try alloc.alloc(u8, 0),
            .width = 0,
            .height = 0,
        };
    }

    const total_bytes: usize = @as(usize, w) * @as(usize, h) * 4;
    if (total_bytes > ctx.budget) return error.SixelTooLarge;

    var rgba = try alloc.alloc(u8, total_bytes);
    errdefer alloc.free(rgba);
    // Initial fill: P1 (default) uses ctx.bg; P2 and P3 modes land
    // in later commits.
    fillBg(rgba, ctx.bg);

    var palette = Palette.init();
    var paint_x: u32 = 0;
    var paint_y: u32 = 0;
    var current_color: u8 = 0;

    for (cmd.ops) |op| switch (op) {
        .sixel => |s| {
            const color = palette.query(current_color);
            const bits: u8 = s.byte -% '?';
            var col: u32 = 0;
            while (col < s.count) : (col += 1) {
                const px = paint_x + col;
                if (px >= w) break;
                var bit: u3 = 0;
                while (bit < 6) : (bit += 1) {
                    if ((bits >> bit) & 1 == 0) continue;
                    const py = paint_y + bit;
                    if (py >= h) continue;
                    const off = (@as(usize, py) * @as(usize, w) + @as(usize, px)) * 4;
                    rgba[off] = color.r;
                    rgba[off + 1] = color.g;
                    rgba[off + 2] = color.b;
                    rgba[off + 3] = color.a;
                }
            }
            paint_x +|= s.count;
        },
        .select_color => |idx| current_color = idx,
        .carriage_return => paint_x = 0,
        .next_line => {
            paint_x = 0;
            paint_y +|= 6;
        },
        .set_rgb => |rgb| palette.setRgb(rgb.idx, rgb.r, rgb.g, rgb.b),
        .set_hls => |hls| palette.setHls(hls.idx, hls.h, hls.l, hls.s),
    };

    return .{
        .alloc = alloc,
        .rgba = rgba,
        .width = w,
        .height = h,
    };
}

const Bounds = struct { w: u32, h: u32 };

/// First pass: walk the op stream tracking the max x and max y
/// reached. Doesn't allocate.
fn measureBounds(cmd: Command) Bounds {
    var paint_x: u32 = 0;
    var paint_y: u32 = 0;
    var max_x: u32 = 0;
    var max_y: u32 = 0;

    for (cmd.ops) |op| switch (op) {
        .sixel => |s| {
            paint_x +|= s.count;
            if (paint_x > max_x) max_x = paint_x;
            const reach_y = paint_y + 6;
            if (reach_y > max_y) max_y = reach_y;
        },
        .carriage_return => paint_x = 0,
        .next_line => {
            paint_x = 0;
            paint_y +|= 6;
        },
        .select_color, .set_rgb, .set_hls => {},
    };

    return .{ .w = max_x, .h = max_y };
}

fn fillBg(rgba: []u8, bg: Rgba) void {
    var i: usize = 0;
    while (i < rgba.len) : (i += 4) {
        rgba[i] = bg.r;
        rgba[i + 1] = bg.g;
        rgba[i + 2] = bg.b;
        rgba[i + 3] = bg.a;
    }
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

test "decoder: single ? paints a 1x6 column of background" {
    // '?' = 0x3F. byte - '?' = 0, so no bits set → all 6 pixels
    // stay at background (P1 default = ctx.bg = black).
    const alloc = testing.allocator;
    var ops_buf = [_]Op{.{ .sixel = .{ .byte = '?', .count = 1 } }};
    var c = Command{
        .alloc = alloc,
        .raster = .{},
        .ops = try alloc.dupe(Op, &ops_buf),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    var img = try decode(alloc, c, .{});
    defer img.deinit();
    try testing.expectEqual(@as(u32, 1), img.width);
    try testing.expectEqual(@as(u32, 6), img.height);
    for (0..6) |y| {
        const off = y * 4;
        try testing.expectEqual(@as(u8, 0), img.rgba[off]);
        try testing.expectEqual(@as(u8, 0), img.rgba[off + 1]);
        try testing.expectEqual(@as(u8, 0), img.rgba[off + 2]);
        try testing.expectEqual(@as(u8, 255), img.rgba[off + 3]);
    }
}

test "decoder: ~ paints a 1x6 column of foreground (all bits set)" {
    // '~' = 0x7E. byte - '?' = 0x3F = 0b111111 → all 6 pixels painted.
    const alloc = testing.allocator;
    var ops_buf = [_]Op{.{ .sixel = .{ .byte = '~', .count = 1 } }};
    var c = Command{
        .alloc = alloc,
        .raster = .{},
        .ops = try alloc.dupe(Op, &ops_buf),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    var img = try decode(alloc, c, .{
        .bg = .{ .r = 255, .g = 255, .b = 255, .a = 255 }, // white bg
    });
    defer img.deinit();
    try testing.expectEqual(@as(u32, 1), img.width);
    try testing.expectEqual(@as(u32, 6), img.height);
    // All 6 pixels should be black (palette default entry 0).
    for (0..6) |y| {
        const off = y * 4;
        try testing.expectEqual(@as(u8, 0), img.rgba[off]);
        try testing.expectEqual(@as(u8, 0), img.rgba[off + 1]);
        try testing.expectEqual(@as(u8, 0), img.rgba[off + 2]);
    }
}

// ---- Run-length expansion ----

test "decoder: !4 ~ produces 4-wide column" {
    const alloc = testing.allocator;
    var ops_buf = [_]Op{.{ .sixel = .{ .byte = '~', .count = 4 } }};
    var c = Command{
        .alloc = alloc,
        .raster = .{},
        .ops = try alloc.dupe(Op, &ops_buf),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    var img = try decode(alloc, c, .{
        .bg = .{ .r = 200, .g = 200, .b = 200, .a = 255 },
    });
    defer img.deinit();
    try testing.expectEqual(@as(u32, 4), img.width);
    try testing.expectEqual(@as(u32, 6), img.height);
    // Every cell should be black (current_color), not gray bg.
    for (0..6) |y| for (0..4) |x| {
        const off = (y * 4 + x) * 4;
        try testing.expectEqual(@as(u8, 0), img.rgba[off]);
    };
}

test "decoder: count at u16 max produces 65535-wide image" {
    const alloc = testing.allocator;
    var ops_buf = [_]Op{.{ .sixel = .{ .byte = '?', .count = 65535 } }};
    var c = Command{
        .alloc = alloc,
        .raster = .{},
        .ops = try alloc.dupe(Op, &ops_buf),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    var img = try decode(alloc, c, .{});
    defer img.deinit();
    try testing.expectEqual(@as(u32, 65535), img.width);
    try testing.expectEqual(@as(u32, 6), img.height);
}

test "decoder: budget rejection on oversized image" {
    const alloc = testing.allocator;
    var ops_buf = [_]Op{.{ .sixel = .{ .byte = '?', .count = 65535 } }};
    var c = Command{
        .alloc = alloc,
        .raster = .{},
        .ops = try alloc.dupe(Op, &ops_buf),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    const result = decode(alloc, c, .{ .budget = 1024 });
    try testing.expectError(error.SixelTooLarge, result);
}

// ---- Color selection + interleaved palette mutation ----

test "decoder: select_color switches active paint color" {
    const alloc = testing.allocator;
    var ops_buf = [_]Op{
        .{ .set_rgb = .{ .idx = 1, .r = 100, .g = 0, .b = 0 } },
        .{ .select_color = 1 },
        .{ .sixel = .{ .byte = '~', .count = 1 } },
    };
    var c = Command{
        .alloc = alloc,
        .raster = .{},
        .ops = try alloc.dupe(Op, &ops_buf),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    var img = try decode(alloc, c, .{});
    defer img.deinit();
    for (0..6) |y| {
        const off = y * 4;
        try testing.expectEqual(@as(u8, 255), img.rgba[off]);
        try testing.expectEqual(@as(u8, 0), img.rgba[off + 1]);
        try testing.expectEqual(@as(u8, 0), img.rgba[off + 2]);
    }
}

test "decoder: interleaved palette mutation respects source order" {
    // Regression test for the ops-unification refactor: without it,
    // both columns would render green.
    const alloc = testing.allocator;
    var ops_buf = [_]Op{
        .{ .set_rgb = .{ .idx = 1, .r = 100, .g = 0, .b = 0 } },
        .{ .select_color = 1 },
        .{ .sixel = .{ .byte = '~', .count = 1 } },
        .{ .set_rgb = .{ .idx = 1, .r = 0, .g = 100, .b = 0 } },
        .{ .sixel = .{ .byte = '~', .count = 1 } },
    };
    var c = Command{
        .alloc = alloc,
        .raster = .{},
        .ops = try alloc.dupe(Op, &ops_buf),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    var img = try decode(alloc, c, .{});
    defer img.deinit();
    try testing.expectEqual(@as(u32, 2), img.width);
    // Column 0 = red, column 1 = green.
    try testing.expectEqual(@as(u8, 255), img.rgba[0]);
    try testing.expectEqual(@as(u8, 0), img.rgba[1]);
    try testing.expectEqual(@as(u8, 0), img.rgba[4]);
    try testing.expectEqual(@as(u8, 255), img.rgba[5]);
}

test "decoder: set_hls applies HLS color" {
    const alloc = testing.allocator;
    var ops_buf = [_]Op{
        .{ .set_hls = .{ .idx = 1, .h = 0, .l = 50, .s = 100 } },
        .{ .select_color = 1 },
        .{ .sixel = .{ .byte = '~', .count = 1 } },
    };
    var c = Command{
        .alloc = alloc,
        .raster = .{},
        .ops = try alloc.dupe(Op, &ops_buf),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    var img = try decode(alloc, c, .{});
    defer img.deinit();
    // DEC blue (H=0): RGB (0, 0, 255).
    for (0..6) |y| {
        const off = y * 4;
        try testing.expectEqual(@as(u8, 0), img.rgba[off]);
        try testing.expectEqual(@as(u8, 0), img.rgba[off + 1]);
        try testing.expectEqual(@as(u8, 255), img.rgba[off + 2]);
    }
}

// ---- Carriage return + next line ----

test "decoder: $ resets paint cursor to column 0" {
    const alloc = testing.allocator;
    var ops_buf = [_]Op{
        .{ .set_rgb = .{ .idx = 1, .r = 100, .g = 100, .b = 100 } },
        .{ .select_color = 1 },
        .{ .sixel = .{ .byte = '~', .count = 3 } }, // 3 white cells
        .{ .carriage_return = {} },
        .{ .set_rgb = .{ .idx = 1, .r = 100, .g = 0, .b = 0 } },
        .{ .sixel = .{ .byte = '~', .count = 2 } }, // overpaint cols 0-1 red
    };
    var c = Command{
        .alloc = alloc,
        .raster = .{},
        .ops = try alloc.dupe(Op, &ops_buf),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    var img = try decode(alloc, c, .{});
    defer img.deinit();
    try testing.expectEqual(@as(u32, 3), img.width);
    // Col 0, row 0: red (overpainted).
    try testing.expectEqual(@as(u8, 255), img.rgba[0]);
    try testing.expectEqual(@as(u8, 0), img.rgba[1]);
    // Col 2, row 0: white (not overpainted).
    try testing.expectEqual(@as(u8, 255), img.rgba[8]);
    try testing.expectEqual(@as(u8, 255), img.rgba[9]);
}

test "decoder: - advances paint cursor down 6 pixels and resets x" {
    const alloc = testing.allocator;
    var ops_buf = [_]Op{
        .{ .sixel = .{ .byte = '~', .count = 1 } },
        .{ .next_line = {} },
        .{ .sixel = .{ .byte = '~', .count = 1 } },
    };
    var c = Command{
        .alloc = alloc,
        .raster = .{},
        .ops = try alloc.dupe(Op, &ops_buf),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    var img = try decode(alloc, c, .{
        .bg = .{ .r = 200, .g = 200, .b = 200, .a = 255 },
    });
    defer img.deinit();
    try testing.expectEqual(@as(u32, 1), img.width);
    try testing.expectEqual(@as(u32, 12), img.height);
    for (0..12) |y| {
        const off = y * 4;
        try testing.expectEqual(@as(u8, 0), img.rgba[off]);
        try testing.expectEqual(@as(u8, 0), img.rgba[off + 1]);
    }
}

// ---- Raster bounds clamping ----

test "decoder: raster.declared_width clamps output width" {
    const alloc = testing.allocator;
    var ops_buf = [_]Op{.{ .sixel = .{ .byte = '~', .count = 10 } }};
    var c = Command{
        .alloc = alloc,
        .raster = .{ .declared_width = 5, .declared_height = 6 },
        .ops = try alloc.dupe(Op, &ops_buf),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    var img = try decode(alloc, c, .{});
    defer img.deinit();
    try testing.expectEqual(@as(u32, 5), img.width);
    try testing.expectEqual(@as(u32, 6), img.height);
}

test "decoder: raster.declared_height clamps output height" {
    const alloc = testing.allocator;
    var ops_buf = [_]Op{
        .{ .sixel = .{ .byte = '~', .count = 1 } },
        .{ .next_line = {} },
        .{ .sixel = .{ .byte = '~', .count = 1 } },
    };
    var c = Command{
        .alloc = alloc,
        .raster = .{ .declared_width = 1, .declared_height = 6 },
        .ops = try alloc.dupe(Op, &ops_buf),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    var img = try decode(alloc, c, .{});
    defer img.deinit();
    try testing.expectEqual(@as(u32, 1), img.width);
    try testing.expectEqual(@as(u32, 6), img.height);
}

test "decoder: raster larger than paint bounds doesn't expand output" {
    const alloc = testing.allocator;
    var ops_buf = [_]Op{.{ .sixel = .{ .byte = '~', .count = 1 } }};
    var c = Command{
        .alloc = alloc,
        .raster = .{ .declared_width = 100, .declared_height = 100 },
        .ops = try alloc.dupe(Op, &ops_buf),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    var img = try decode(alloc, c, .{});
    defer img.deinit();
    try testing.expectEqual(@as(u32, 1), img.width);
    try testing.expectEqual(@as(u32, 6), img.height);
}
