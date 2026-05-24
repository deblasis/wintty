const std = @import("std");
const testing = std.testing;
const Allocator = std.mem.Allocator;
const cmd = @import("command.zig");
const Command = cmd.Command;
const PaintOp = cmd.PaintOp;
const PaletteOp = cmd.PaletteOp;
const Raster = cmd.Raster;
const raster = @import("raster.zig");

const log = std.log.scoped(.terminal_sixel);

/// Parser state. Modeled on foot's `enum sixel_state`.
const State = enum {
    /// Expecting either a `"` prelude (raster attribs) or first
    /// paint byte.
    initial,
    /// Inside the `"..` raster-attribs body, accumulating bytes
    /// until a non-attribute byte arrives.
    raster_attribs,
    /// Normal sixel data: ?..~, #, !, $, -.
    data,
    /// After `!`, accumulating decimal digits for the repeat count.
    repeat_count,
    /// After `#`, accumulating "N" or "N;Pu;Pa;Pb;Pc" for color def
    /// or selection.
    color_def,
    /// Permanently ignoring remaining bytes due to a non-recoverable
    /// error mid-stream.
    ignore,
};

/// Streaming sixel parser. Consume bytes via `put`, finalize with
/// `finalize` to extract the `Command`.
pub const Parser = struct {
    alloc: Allocator,
    state: State,
    raster: Raster,
    intro_params: [3]?u16,

    paint_ops: std.ArrayListUnmanaged(PaintOp),
    palette_ops: std.ArrayListUnmanaged(PaletteOp),

    /// Working buffer for raster_attribs / color_def / repeat_count.
    accum: std.ArrayListUnmanaged(u8),

    /// Initialize a parser. `intro_params` are the `Pa;Pb;Ph`
    /// parameters from the DCS introducer (`ESC P Pa;Pb;Ph q`).
    pub fn init(alloc: Allocator, intro_params: [3]?u16) Parser {
        return .{
            .alloc = alloc,
            .state = .initial,
            .raster = .{},
            .intro_params = intro_params,
            .paint_ops = .empty,
            .palette_ops = .empty,
            .accum = .empty,
        };
    }

    pub fn deinit(self: *Parser) void {
        self.paint_ops.deinit(self.alloc);
        self.palette_ops.deinit(self.alloc);
        self.accum.deinit(self.alloc);
    }

    /// Consume one byte. Errors are non-fatal: the parser transitions
    /// to `.ignore` on internal errors and silently drops remaining
    /// bytes until `finalize`.
    pub fn put(self: *Parser, byte: u8) void {
        _ = self;
        _ = byte;
        // State-machine body lands as the parser grows; see follow-on
        // commits in this module.
    }

    /// Finalize the accumulated state into a `Command`. Caller owns
    /// the returned slices via `Command.deinit`. After `finalize`,
    /// the parser is consumed — do not call `put` or `finalize` again.
    pub fn finalize(self: *Parser) Allocator.Error!Command {
        return .{
            .alloc = self.alloc,
            .raster = self.raster,
            .palette_ops = try self.palette_ops.toOwnedSlice(self.alloc),
            .paint_ops = try self.paint_ops.toOwnedSlice(self.alloc),
            .intro_params = self.intro_params,
        };
    }
};

test "sixel parser: init and deinit do not leak" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    try testing.expect(p.state == .initial);
}

test "sixel parser: empty finalize yields empty Command" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 0), c.paint_ops.len);
    try testing.expectEqual(@as(usize, 0), c.palette_ops.len);
}

test "sixel parser: intro params round-trip" {
    var p = Parser.init(testing.allocator, .{ 7, 1, 75 });
    defer p.deinit();
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(?u16, 7), c.intro_params[0]);
    try testing.expectEqual(@as(?u16, 1), c.intro_params[1]);
    try testing.expectEqual(@as(?u16, 75), c.intro_params[2]);
}
