const std = @import("std");
const testing = std.testing;

/// A fully-parsed sixel image command. Output of the parser, input
/// to the decoder (future commit).
///
/// Ownership: two heap-allocated slices are released by `deinit`.
/// We carry the allocator on the struct rather than using an arena
/// because there are only two slices and a `Command.deinit()` is
/// easier to chain into the existing `dcs.Command.deinit` switch arm
/// than an arena handle.
pub const Command = struct {
    /// Allocator that owns palette_ops and paint_ops slices.
    /// Set by Parser.finalize; freed by deinit.
    alloc: std.mem.Allocator,

    /// Raster attributes (geometry, aspect ratio). May be defaulted
    /// if the sender omitted the `"` prelude.
    raster: Raster,

    /// Palette register set operations, in source order.
    palette_ops: []const PaletteOp,

    /// Paint operations (sixel bytes, color selects, CR, NL), in
    /// source order.
    paint_ops: []const PaintOp,

    /// The `Pa;Pb;Ph` parameters from the DCS introducer
    /// (`ESC P Pa;Pb;Ph q`). All optional; null when omitted.
    /// Kept raw on purpose: the decoder applies DEC default semantics
    /// (e.g. Pa=0 → 1) so this struct stays a faithful record of the
    /// introducer bytes.
    intro_params: [3]?u16,

    pub fn deinit(self: *Command) void {
        self.alloc.free(self.palette_ops);
        self.alloc.free(self.paint_ops);
    }
};

pub const Raster = struct {
    /// Pixel aspect ratio numerator. Default 1.
    aspect_num: u16 = 1,
    /// Pixel aspect ratio denominator. Default 1.
    aspect_den: u16 = 1,
    /// Horizontal grid size (pixels per inch hint). 0 = unspecified.
    grid_size: u16 = 0,
    /// Declared image width in pixels (Pn3 in raster attribs). 0 = undeclared.
    declared_width: u16 = 0,
    /// Declared image height in pixels (Pn4). 0 = undeclared.
    declared_height: u16 = 0,
};

pub const PaintOp = union(enum) {
    /// A sixel data byte (?..~ in source), optionally run-length-repeated.
    /// `byte` is the raw character; the decoder maps it to 6 vertical pixels.
    /// `count` is u16 because the `!N` run-length is unbounded in the
    /// DEC spec — u8 would clip common encoder output, u32 wastes
    /// memory in the op stream. The parser saturates at u16 max.
    sixel: struct { byte: u8, count: u16 },
    /// Select color register `idx` as the current paint color.
    select_color: u8,
    /// `$` — return paint cursor to leftmost column.
    carriage_return,
    /// `-` — advance paint cursor down 6 pixels, reset to leftmost column.
    next_line,
};

pub const PaletteOp = union(enum) {
    /// `#N;2;Pr;Pg;Pb` — set register N to RGB triple.
    /// Values normalized to 0-255 from the source 0-100 range.
    set_rgb: struct { idx: u8, r: u8, g: u8, b: u8 },
    /// `#N;1;Ph;Pl;Ps` — set register N to DEC HLS triple.
    /// H is 0-360, L is 0-100, S is 0-100. Decoder converts to RGB.
    set_hls: struct { idx: u8, h: u16, l: u8, s: u8 },
};

test "Command: deinit frees slices" {
    const alloc = testing.allocator;
    var cmd = Command{
        .alloc = alloc,
        .raster = .{},
        .palette_ops = try alloc.alloc(PaletteOp, 2),
        .paint_ops = try alloc.alloc(PaintOp, 3),
        .intro_params = .{ null, null, null },
    };
    cmd.deinit();
}

test "Raster: defaults match spec" {
    const r = Raster{};
    try testing.expectEqual(@as(u16, 1), r.aspect_num);
    try testing.expectEqual(@as(u16, 1), r.aspect_den);
    try testing.expectEqual(@as(u16, 0), r.grid_size);
    try testing.expectEqual(@as(u16, 0), r.declared_width);
    try testing.expectEqual(@as(u16, 0), r.declared_height);
}

test "PaintOp: sixel variant carries byte and count" {
    const op = PaintOp{ .sixel = .{ .byte = '?', .count = 1 } };
    try testing.expectEqual(@as(u8, '?'), op.sixel.byte);
    try testing.expectEqual(@as(u16, 1), op.sixel.count);
}

test "PaletteOp: set_rgb variant carries normalized values" {
    const op = PaletteOp{ .set_rgb = .{ .idx = 0, .r = 255, .g = 128, .b = 0 } };
    try testing.expectEqual(@as(u8, 0), op.set_rgb.idx);
    try testing.expectEqual(@as(u8, 255), op.set_rgb.r);
}
