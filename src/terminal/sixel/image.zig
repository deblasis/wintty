const std = @import("std");
const testing = std.testing;
const Allocator = std.mem.Allocator;
const command_mod = @import("command.zig");
const decoder_mod = @import("decoder.zig");
const kitty_graphics = @import("../kitty/graphics.zig");
const kitty_command = @import("../kitty/graphics_command.zig");

pub const Error = error{
    /// Decoded image had zero dimensions; nothing to render. Caller
    /// should skip the kittyGraphics dispatch entirely.
    EmptyImage,
} || decoder_mod.Error;

/// Decode a parsed sixel Command and synthesize a kitty graphics
/// transmit_and_display command carrying the decoded RGBA. The
/// returned Command owns its data slice; caller must free via
/// `Command.deinit(alloc)`.
///
/// Routing sixel through the kitty graphics pipeline reuses the
/// image storage, placement, scroll-tracking, and rendering work
/// the kitty + iTerm2 protocols already exercise. There's no
/// separate sixel render path.
pub fn synthKittyCommand(
    alloc: Allocator,
    sixel_cmd: command_mod.Command,
    ctx: decoder_mod.DecodeCtx,
) Error!kitty_graphics.Command {
    var img = try decoder_mod.decode(alloc, sixel_cmd, ctx);
    // Zero-dim image means there's nothing to render. Free the
    // (empty) rgba and signal the caller to skip dispatch.
    if (img.width == 0 or img.height == 0) {
        img.deinit();
        return error.EmptyImage;
    }

    // Transfer rgba ownership into the kitty Command. img.deinit
    // would free what the Command now owns, so we deliberately don't
    // call it — img.rgba is moved, not borrowed.
    return .{
        .control = .{ .transmit_and_display = .{
            .transmission = .{
                .format = .rgba,
                .medium = .direct,
                .width = img.width,
                .height = img.height,
            },
            .display = .{},
        } },
        .data = img.rgba,
    };
}

test "image: synthKittyCommand on empty Command returns EmptyImage" {
    const alloc = testing.allocator;
    var c = command_mod.Command{
        .alloc = alloc,
        .raster = .{},
        .ops = try alloc.alloc(command_mod.Op, 0),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    const result = synthKittyCommand(alloc, c, .{});
    try testing.expectError(error.EmptyImage, result);
}

test "image: synthKittyCommand wraps a single ~ as a 1x6 RGBA transmit" {
    const alloc = testing.allocator;
    var ops_buf = [_]command_mod.Op{
        .{ .sixel = .{ .byte = '~', .count = 1 } },
    };
    var c = command_mod.Command{
        .alloc = alloc,
        .raster = .{},
        .ops = try alloc.dupe(command_mod.Op, &ops_buf),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    var kcmd = try synthKittyCommand(alloc, c, .{});
    defer kcmd.deinit(alloc);

    // Action must be transmit_and_display so the kitty pipeline both
    // stores AND immediately renders the image.
    try testing.expect(kcmd.control == .transmit_and_display);
    const t = kcmd.control.transmit_and_display.transmission;
    try testing.expectEqual(kitty_command.Transmission.Format.rgba, t.format);
    try testing.expectEqual(kitty_command.Transmission.Medium.direct, t.medium);
    try testing.expectEqual(@as(u32, 1), t.width);
    try testing.expectEqual(@as(u32, 6), t.height);

    // Data is the raw RGBA: 1 column × 6 rows × 4 bytes = 24 bytes.
    try testing.expectEqual(@as(usize, 24), kcmd.data.len);
    // First pixel R/G/B/A — black (palette default entry 0).
    try testing.expectEqual(@as(u8, 0), kcmd.data[0]);
    try testing.expectEqual(@as(u8, 0), kcmd.data[1]);
    try testing.expectEqual(@as(u8, 0), kcmd.data[2]);
    try testing.expectEqual(@as(u8, 255), kcmd.data[3]);
}

test "image: synthKittyCommand respects palette mutation in source order" {
    // Same regression as the decoder's interleaved-palette test, but
    // verified end-to-end through the kitty Command's data buffer.
    const alloc = testing.allocator;
    var ops_buf = [_]command_mod.Op{
        .{ .set_rgb = .{ .idx = 1, .r = 100, .g = 0, .b = 0 } },
        .{ .select_color = 1 },
        .{ .sixel = .{ .byte = '~', .count = 1 } },
        .{ .set_rgb = .{ .idx = 1, .r = 0, .g = 100, .b = 0 } },
        .{ .sixel = .{ .byte = '~', .count = 1 } },
    };
    var c = command_mod.Command{
        .alloc = alloc,
        .raster = .{},
        .ops = try alloc.dupe(command_mod.Op, &ops_buf),
        .intro_params = .{ null, null, null },
    };
    defer c.deinit();

    var kcmd = try synthKittyCommand(alloc, c, .{});
    defer kcmd.deinit(alloc);

    // 2-wide image: col 0 red, col 1 green.
    try testing.expectEqual(@as(u32, 2), kcmd.control.transmit_and_display.transmission.width);
    try testing.expectEqual(@as(u8, 255), kcmd.data[0]); // (0,0).r
    try testing.expectEqual(@as(u8, 0), kcmd.data[1]); // (0,0).g
    try testing.expectEqual(@as(u8, 0), kcmd.data[4]); // (1,0).r
    try testing.expectEqual(@as(u8, 255), kcmd.data[5]); // (1,0).g
}
