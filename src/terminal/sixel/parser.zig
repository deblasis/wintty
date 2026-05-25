const std = @import("std");
const testing = std.testing;
const Allocator = std.mem.Allocator;
const cmd = @import("command.zig");
const Command = cmd.Command;
const Op = cmd.Op;
const Raster = cmd.Raster;
const raster = @import("raster.zig");

const log = std.log.scoped(.terminal_sixel);

/// Parser state.
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

    /// Accumulator for repeat-count digits (after `!`). Cleared when
    /// `!` is seen, applied when the next sixel byte arrives.
    /// Saturating add/multiply keep this clamped at u16::MAX, which
    /// matches Op.sixel.count's width.
    repeat_acc: u16,

    /// All paint and palette operations in source order. The decoder
    /// applies them in stream order so mid-stream palette mutation
    /// has the spec-correct effect (rather than all palette ops
    /// applying as a separate pre-pass).
    ops: std.ArrayListUnmanaged(Op),

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
            .repeat_acc = 0,
            .ops = .empty,
            .accum = .empty,
        };
    }

    pub fn deinit(self: *Parser) void {
        self.ops.deinit(self.alloc);
        self.accum.deinit(self.alloc);
    }

    /// Consume one byte. Errors are non-fatal: the parser transitions
    /// to `.ignore` on internal errors and silently drops remaining
    /// bytes until `finalize`.
    pub fn put(self: *Parser, byte: u8) void {
        self.tryPut(byte) catch |err| {
            log.debug("sixel parser error, ignoring rest: {}", .{err});
            // Drop any partially-accumulated state so we don't sit on
            // up to max_bytes of accum until deinit.
            self.accum.clearAndFree(self.alloc);
            self.state = .ignore;
        };
    }

    fn tryPut(self: *Parser, byte: u8) Allocator.Error!void {
        switch (self.state) {
            .ignore => return,

            .initial, .data => switch (byte) {
                '?'...'~' => try self.appendSixel(byte, 1),
                '!' => {
                    self.repeat_acc = 0;
                    self.state = .repeat_count;
                },
                '#' => {
                    self.accum.clearRetainingCapacity();
                    self.state = .color_def;
                },
                '$' => {
                    try self.ops.append(self.alloc, .carriage_return);
                    self.state = .data;
                },
                '-' => {
                    try self.ops.append(self.alloc, .next_line);
                    self.state = .data;
                },
                '"' => {
                    self.accum.clearRetainingCapacity();
                    self.state = .raster_attribs;
                },
                else => {
                    // Bytes outside the sixel data alphabet are
                    // silently ignored. We also promote .initial to
                    // .data so subsequent non-alphabet bytes stay
                    // anchored in the data phase rather than waiting
                    // for a raster prelude that will never arrive.
                    self.state = .data;
                },
            },

            .repeat_count => switch (byte) {
                '0'...'9' => {
                    // Saturating ops match Parser.zig's CSI param
                    // accumulator; once we hit u16::MAX further digits
                    // are absorbed without overflow.
                    self.repeat_acc *|= 10;
                    self.repeat_acc +|= byte - '0';
                },
                '?'...'~' => {
                    // DEC spec: missing repeat count means 1. Also
                    // applies when the user explicitly typed `!0`, which
                    // foot and libsixel both coerce to 1 rather than
                    // emitting nothing.
                    const count: u16 = if (self.repeat_acc == 0) 1 else self.repeat_acc;
                    try self.appendSixel(byte, count);
                },
                else => {
                    // Non-digit, non-alphabet byte after `!` — abandon
                    // the pending count, drop back to .data, and
                    // re-dispatch the byte so command bytes (#, $, -,
                    // ", !) get their own handling.
                    self.state = .data;
                    try self.tryPut(byte);
                },
            },

            .color_def => switch (byte) {
                '0'...'9', ';' => {
                    try self.accum.append(self.alloc, byte);
                },
                else => {
                    // End of color def — flush, then re-dispatch this
                    // byte in data state so it gets interpreted
                    // (e.g. a sixel byte after `#5?` should paint).
                    try self.flushColorDef();
                    self.state = .data;
                    try self.tryPut(byte);
                },
            },

            .raster_attribs => switch (byte) {
                '0'...'9', ';' => {
                    try self.accum.append(self.alloc, byte);
                },
                else => {
                    // End of raster — flush, then re-dispatch this
                    // byte in data state (matches the .color_def
                    // else-arm pattern).
                    //
                    // Note: flushRaster may set self.state = .ignore
                    // on oversized geometry. The guard below skips the
                    // .data transition + re-dispatch in that case.
                    // flushColorDef has no analogous failure mode (no
                    // size check there), so it doesn't need the guard.
                    self.flushRaster();
                    if (self.state == .ignore) return;
                    self.state = .data;
                    try self.tryPut(byte);
                },
            },
        }
    }

    fn appendSixel(self: *Parser, byte: u8, count: u16) Allocator.Error!void {
        try self.ops.append(self.alloc, .{
            .sixel = .{ .byte = byte, .count = count },
        });
        self.state = .data;
    }

    /// Finalize the accumulated state into a `Command`. Caller owns
    /// the returned slices via `Command.deinit`. After `finalize`,
    /// the parser is consumed — do not call `put` or `finalize` again.
    ///
    /// Partial mid-state at end-of-stream (an incomplete color def or
    /// raster prelude that never received a terminator) is silently
    /// discarded; matches foot/libsixel's behavior for truncated DCS.
    pub fn finalize(self: *Parser) Allocator.Error!Command {
        return .{
            .alloc = self.alloc,
            .raster = self.raster,
            .ops = try self.ops.toOwnedSlice(self.alloc),
            .intro_params = self.intro_params,
        };
    }

    fn flushColorDef(self: *Parser) Allocator.Error!void {
        // Empty accumulator (e.g. `##` or `#?` arrived back-to-back)
        // produces no op at all — there's no register index to act on.
        if (self.accum.items.len == 0) {
            log.debug("sixel color def empty, dropped", .{});
            return;
        }

        var it = std.mem.splitScalar(u8, self.accum.items, ';');
        const idx_str = it.next() orelse return;
        const idx = parseU8(idx_str) orelse {
            log.debug("sixel color def malformed: bad register idx, dropped", .{});
            return;
        };

        // Selection form: bare "#N" with no further fields.
        const pu_str = it.next() orelse {
            try self.ops.append(self.alloc, .{ .select_color = idx });
            return;
        };

        const pu = parseU8(pu_str) orelse {
            log.debug("sixel color def malformed: bad Pu, dropped", .{});
            return;
        };
        const a_str = it.next() orelse {
            log.debug("sixel color def malformed: missing Pa, dropped", .{});
            return;
        };
        const b_str = it.next() orelse {
            log.debug("sixel color def malformed: missing Pb, dropped", .{});
            return;
        };
        const c_str = it.next() orelse {
            log.debug("sixel color def malformed: missing Pc, dropped", .{});
            return;
        };
        const a = parseU16(a_str) orelse {
            log.debug("sixel color def malformed: bad Pa, dropped", .{});
            return;
        };
        const b = parseU8(b_str) orelse {
            log.debug("sixel color def malformed: bad Pb, dropped", .{});
            return;
        };
        const c = parseU8(c_str) orelse {
            log.debug("sixel color def malformed: bad Pc, dropped", .{});
            return;
        };

        switch (pu) {
            1 => try self.ops.append(self.alloc, .{
                .set_hls = .{ .idx = idx, .h = a, .l = b, .s = c },
            }),
            2 => try self.ops.append(self.alloc, .{
                .set_rgb = .{
                    .idx = idx,
                    // r is narrowed from u16, so we clamp explicitly to
                    // keep @intCast safe. g and b come from parseU8
                    // already in u8 range; out-of-spec values >100 pass
                    // through here and get clamped downstream in
                    // palette.setRgb's scale100to255.
                    .r = @intCast(@min(a, 100)),
                    .g = b,
                    .b = c,
                },
            }),
            else => {
                // Unknown Pu — silently drop (spec leaves room for
                // future extensions).
                log.debug("sixel color def unknown Pu={d}, dropped", .{pu});
            },
        }
    }

    fn flushRaster(self: *Parser) void {
        const r = raster.parseRasterAttribs(self.accum.items) catch |err| {
            switch (err) {
                error.SixelTooLarge => {
                    log.warn("sixel raster oversized, dropping image", .{});
                    self.state = .ignore;
                    return;
                },
                error.Malformed => {
                    log.debug("sixel raster malformed, using defaults", .{});
                    return;
                },
            }
        };
        self.raster = r;
    }

    fn parseU8(s: []const u8) ?u8 {
        return std.fmt.parseInt(u8, s, 10) catch null;
    }

    fn parseU16(s: []const u8) ?u16 {
        return std.fmt.parseInt(u16, s, 10) catch null;
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
    try testing.expectEqual(@as(usize, 0), c.ops.len);
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

test "sixel parser: single sixel byte appends count=1" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    p.put('?');
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 1), c.ops.len);
    try testing.expect(c.ops[0] == .sixel);
    try testing.expectEqual(@as(u8, '?'), c.ops[0].sixel.byte);
    try testing.expectEqual(@as(u16, 1), c.ops[0].sixel.count);
}

test "sixel parser: multiple sixel bytes append separately" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("?@AB") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 4), c.ops.len);
    for (c.ops, "?@AB") |op, expected| {
        try testing.expectEqual(@as(u8, expected), op.sixel.byte);
    }
}

test "sixel parser: byte outside ?..~ in data state is ignored" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    p.put('?');
    p.put(0x07); // bell, not a valid sixel byte
    p.put('@');
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 2), c.ops.len);
}

test "sixel parser: !3 ? produces count=3" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("!3?") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 1), c.ops.len);
    try testing.expectEqual(@as(u16, 3), c.ops[0].sixel.count);
    try testing.expectEqual(@as(u8, '?'), c.ops[0].sixel.byte);
}

test "sixel parser: !65535 saturates at u16 max" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("!65535~") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(u16, 65535), c.ops[0].sixel.count);
}

test "sixel parser: !99999 saturates without overflow" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("!99999~") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    // Saturated to u16 max
    try testing.expectEqual(@as(u16, 65535), c.ops[0].sixel.count);
}

test "sixel parser: ! with no digits then sixel emits count=1" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("!?") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 1), c.ops.len);
    try testing.expectEqual(@as(u16, 1), c.ops[0].sixel.count);
}

test "sixel parser: #5 selects color register 5" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("#5?") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 2), c.ops.len);
    try testing.expect(c.ops[0] == .select_color);
    try testing.expectEqual(@as(u8, 5), c.ops[0].select_color);
    try testing.expect(c.ops[1] == .sixel);
}

test "sixel parser: #1;2;100;50;0 defines RGB" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("#1;2;100;50;0") |b| p.put(b);
    // Color def needs a terminator byte to flush; feed a sixel byte.
    p.put('?');
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 2), c.ops.len);
    try testing.expect(c.ops[0] == .set_rgb);
    const op = c.ops[0].set_rgb;
    try testing.expectEqual(@as(u8, 1), op.idx);
    try testing.expectEqual(@as(u8, 100), op.r);
    try testing.expectEqual(@as(u8, 50), op.g);
    try testing.expectEqual(@as(u8, 0), op.b);
    try testing.expect(c.ops[1] == .sixel);
}

test "sixel parser: #1;1;180;50;75 defines HLS" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("#1;1;180;50;75") |b| p.put(b);
    p.put('?');
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 2), c.ops.len);
    try testing.expect(c.ops[0] == .set_hls);
    const op = c.ops[0].set_hls;
    try testing.expectEqual(@as(u8, 1), op.idx);
    try testing.expectEqual(@as(u16, 180), op.h);
    try testing.expectEqual(@as(u8, 50), op.l);
    try testing.expectEqual(@as(u8, 75), op.s);
    try testing.expect(c.ops[1] == .sixel);
}

test "sixel parser: #N with invalid Pu silently ignored" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    // Pu=3 is invalid (only 1=HLS, 2=RGB defined).
    for ("#1;3;0;0;0") |b| p.put(b);
    p.put('?');
    var c = try p.finalize();
    defer c.deinit();
    // Only the trailing ? produces an op; the bad color def is dropped.
    try testing.expectEqual(@as(usize, 1), c.ops.len);
    try testing.expect(c.ops[0] == .sixel);
}

test "sixel parser: #N followed by sixel byte selects then paints" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("#7~") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 2), c.ops.len);
    try testing.expectEqual(@as(u8, 7), c.ops[0].select_color);
    try testing.expectEqual(@as(u8, '~'), c.ops[1].sixel.byte);
}

test "sixel parser: !3#5 re-dispatches # into color_def" {
    // Regression: previously `#` was swallowed by the .repeat_count
    // else-arm. Now the else-arm re-dispatches the byte after dropping
    // the pending count.
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("!3#5?") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    // !3 had no terminator paint byte (the next byte was #), so the
    // pending repeat is discarded entirely. The #5 selects color 5,
    // and the trailing ? paints with count=1.
    try testing.expectEqual(@as(usize, 2), c.ops.len);
    try testing.expect(c.ops[0] == .select_color);
    try testing.expectEqual(@as(u8, 5), c.ops[0].select_color);
    try testing.expect(c.ops[1] == .sixel);
    try testing.expectEqual(@as(u8, '?'), c.ops[1].sixel.byte);
    try testing.expectEqual(@as(u16, 1), c.ops[1].sixel.count);
}

test "sixel parser: #1;2;200;200;200 clamps r but passes g/b through" {
    // Documents the intentional asymmetry: r is clamped at the parser
    // because the u16→u8 narrowing requires it; g and b pass through
    // unclamped and get scaled by palette.setRgb downstream.
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("#1;2;200;200;200") |b| p.put(b);
    p.put('?');
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 2), c.ops.len);
    try testing.expect(c.ops[0] == .set_rgb);
    const op = c.ops[0].set_rgb;
    try testing.expectEqual(@as(u8, 100), op.r); // clamped
    try testing.expectEqual(@as(u8, 200), op.g); // not clamped here
    try testing.expectEqual(@as(u8, 200), op.b); // not clamped here
}

test "sixel parser: $ emits carriage_return" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    p.put('$');
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 1), c.ops.len);
    try testing.expect(c.ops[0] == .carriage_return);
}

test "sixel parser: - emits next_line" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    p.put('-');
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 1), c.ops.len);
    try testing.expect(c.ops[0] == .next_line);
}

test "sixel parser: ?$-? emits sixel/CR/NL/sixel" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("?$-?") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 4), c.ops.len);
    try testing.expect(c.ops[0] == .sixel);
    try testing.expect(c.ops[1] == .carriage_return);
    try testing.expect(c.ops[2] == .next_line);
    try testing.expect(c.ops[3] == .sixel);
}

test "sixel parser: \" then 1;1;0;100;200 sets raster" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("\"1;1;0;100;200?") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(u16, 100), c.raster.declared_width);
    try testing.expectEqual(@as(u16, 200), c.raster.declared_height);
    try testing.expectEqual(@as(usize, 1), c.ops.len);
}

test "sixel parser: oversized raster transitions to ignore" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("\"1;1;0;8192;8192") |b| p.put(b);
    p.put('?'); // would normally paint, but parser is in .ignore
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 0), c.ops.len);
}

test "sixel parser: malformed raster falls back to defaults" {
    // 99999 overflows u16 and trips parseRasterAttribs's Malformed
    // branch. The `<` (0x3C, below the `?..~` sixel data alphabet)
    // terminates raster mode and re-dispatches as a silently-dropped
    // byte. `?` then paints.
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("\"99999<?") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    // Raster defaults preserved
    try testing.expectEqual(@as(u16, 1), c.raster.aspect_num);
    try testing.expectEqual(@as(u16, 0), c.raster.declared_width);
    // Painting continues after malformed raster
    try testing.expectEqual(@as(usize, 1), c.ops.len);
}

test "sixel parser: ignore state drops all subsequent bytes" {
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    p.state = .ignore;
    p.put('?');
    p.put('#');
    p.put('!');
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 0), c.ops.len);
}

test "sixel parser: incomplete color def at finalize is dropped" {
    // No terminator byte after `#1;2;100` — flushColorDef never runs.
    // The accumulated buffer is silently discarded by finalize.
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("#1;2;100") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 0), c.ops.len);
}

test "sixel parser: incomplete raster attribs at finalize is dropped" {
    // No terminator byte after `"1;1;0;100` — flushRaster never runs.
    // The accumulated buffer is silently discarded; raster stays default.
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("\"1;1;0;100") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(u16, 1), c.raster.aspect_num);
    try testing.expectEqual(@as(u16, 0), c.raster.declared_width);
}

test "sixel parser: incomplete repeat count at finalize is dropped" {
    // The parser sits in .repeat_count with repeat_acc=42 but never
    // sees a terminator. The orphan count produces no op.
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("!42") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 0), c.ops.len);
}

test "sixel parser: empty color def #? produces no op" {
    // `#` enters .color_def with empty accum; `?` terminates and triggers
    // flushColorDef with an empty buffer. Must not append a spurious
    // select_color=0; must then re-dispatch ? as a paint byte.
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("#?") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 1), c.ops.len);
    try testing.expect(c.ops[0] == .sixel);
    try testing.expectEqual(@as(u8, '?'), c.ops[0].sixel.byte);
}

test "sixel parser: back-to-back # # is dropped" {
    // `##` — first # enters .color_def, second # terminates with empty
    // accum (no op) then re-dispatches as a fresh color_def entry.
    // Followed by `5?` to verify the second # actually re-armed
    // color_def correctly.
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("##5?") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    // One select_color from the second #5, then the paint byte.
    try testing.expectEqual(@as(usize, 2), c.ops.len);
    try testing.expect(c.ops[0] == .select_color);
    try testing.expectEqual(@as(u8, 5), c.ops[0].select_color);
    try testing.expect(c.ops[1] == .sixel);
}

test "sixel parser: !0 ? coerces to count=1" {
    // DEC spec is silent on `!0`; foot and libsixel both treat it as
    // count=1 rather than emitting nothing. Pin that behavior.
    var p = Parser.init(testing.allocator, .{ null, null, null });
    defer p.deinit();
    for ("!0?") |b| p.put(b);
    var c = try p.finalize();
    defer c.deinit();
    try testing.expectEqual(@as(usize, 1), c.ops.len);
    try testing.expectEqual(@as(u16, 1), c.ops[0].sixel.count);
}
