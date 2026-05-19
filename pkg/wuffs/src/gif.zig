const std = @import("std");
const Allocator = std.mem.Allocator;
const c = @import("c.zig").c;
const Error = @import("error.zig").Error;
const check = @import("error.zig").check;
const ImageData = @import("main.zig").ImageData;
const maximum_image_size = @import("main.zig").maximum_image_size;
const mul = std.math.mul;

const log = std.log.scoped(.wuffs_gif);

/// Decode the first frame of a GIF image into RGBA pixels.
///
/// GIFs may carry multiple frames; this wrapper exposes the first
/// frame only. Multi-frame emitters get a single still image rather
/// than an animation. A separate design pass is needed to expose
/// timing and disposal modes to the renderer.
pub fn decode(alloc: Allocator, data: []const u8) Error!ImageData {
    // See pkg/wuffs/src/png.zig for the rationale behind allocating the
    // decoder buffer through the Zig allocator rather than letting
    // wuffs use the C malloc.

    const decoder_buf = try alloc.alloc(u8, c.sizeof__wuffs_gif__decoder());
    defer alloc.free(decoder_buf);

    const decoder: ?*c.wuffs_gif__decoder = @ptrCast(decoder_buf);
    {
        const status = c.wuffs_gif__decoder__initialize(
            decoder,
            c.sizeof__wuffs_gif__decoder(),
            c.WUFFS_VERSION,
            0,
        );
        try check(log, &status);
    }

    var source_buffer: c.wuffs_base__io_buffer = .{
        .data = .{ .ptr = @ptrCast(@constCast(data.ptr)), .len = data.len },
        .meta = .{
            .wi = data.len,
            .ri = 0,
            .pos = 0,
            .closed = true,
        },
    };

    var image_config: c.wuffs_base__image_config = undefined;
    {
        const status = c.wuffs_gif__decoder__decode_image_config(
            decoder,
            &image_config,
            &source_buffer,
        );
        try check(log, &status);
    }

    const width = c.wuffs_base__pixel_config__width(&image_config.pixcfg);
    const height = c.wuffs_base__pixel_config__height(&image_config.pixcfg);

    c.wuffs_base__pixel_config__set(
        &image_config.pixcfg,
        c.WUFFS_BASE__PIXEL_FORMAT__RGBA_NONPREMUL,
        c.WUFFS_BASE__PIXEL_SUBSAMPLING__NONE,
        width,
        height,
    );

    const size: usize = try mul(
        usize,
        try mul(usize, width, height),
        @sizeOf(c.wuffs_base__color_u32_argb_premul),
    );

    if (size > maximum_image_size) {
        log.warn("image size {d} is larger than the maximum allowed ({d})", .{ size, maximum_image_size });
        return error.Overflow;
    }

    const destination = try alloc.alloc(u8, size);
    errdefer alloc.free(destination);

    // GIF frames may be smaller than the canvas; wuffs only writes
    // the frame's sub-rectangle, leaving the rest untouched. Zero
    // the buffer so any un-touched pixels stay transparent. The
    // explicit memset also shields us from debug allocators that
    // poison fresh allocations with non-zero bytes.
    @memset(destination, 0);

    const work_buffer = try alloc.alloc(
        u8,
        std.math.cast(
            usize,
            c.wuffs_gif__decoder__workbuf_len(decoder).max_incl,
        ) orelse return error.OutOfMemory,
    );
    defer alloc.free(work_buffer);

    const work_slice = c.wuffs_base__make_slice_u8(
        work_buffer.ptr,
        work_buffer.len,
    );

    var pixel_buffer: c.wuffs_base__pixel_buffer = undefined;
    {
        const status = c.wuffs_base__pixel_buffer__set_from_slice(
            &pixel_buffer,
            &image_config.pixcfg,
            c.wuffs_base__make_slice_u8(destination.ptr, destination.len),
        );
        try check(log, &status);
    }

    // GIF requires decode_frame_config before decode_frame; PNG and
    // JPEG skip straight to decode_frame. This step also lets a
    // future animation-aware wrapper peek at the per-frame bounds,
    // disposal, blend, and duration.
    var frame_config: c.wuffs_base__frame_config = undefined;
    {
        const status = c.wuffs_gif__decoder__decode_frame_config(
            decoder,
            &frame_config,
            &source_buffer,
        );
        try check(log, &status);
    }

    {
        const status = c.wuffs_gif__decoder__decode_frame(
            decoder,
            &pixel_buffer,
            &source_buffer,
            c.WUFFS_BASE__PIXEL_BLEND__SRC,
            work_slice,
            null,
        );
        try check(log, &status);
    }

    // Detect multi-frame source so the caller can see in debug logs
    // that more frames were available but dropped. We try one more
    // decode_frame_config; if it succeeds (status code 0 instead of
    // end-of-data) the GIF has additional frames we are not
    // rendering. Decode errors here are ignored on purpose: the
    // first frame already decoded cleanly and that is what we are
    // returning.
    var next_frame_config: c.wuffs_base__frame_config = undefined;
    const next_status = c.wuffs_gif__decoder__decode_frame_config(
        decoder,
        &next_frame_config,
        &source_buffer,
    );
    if (next_status.repr == null) {
        log.debug("GIF has additional frames; first frame rendered only", .{});
    }

    return .{
        .width = width,
        .height = height,
        .data = destination,
    };
}

test "gif_decode_000000" {
    const data = try decode(std.testing.allocator, @embedFile("1x1#000000.gif"));
    defer std.testing.allocator.free(data.data);

    try std.testing.expectEqual(1, data.width);
    try std.testing.expectEqual(1, data.height);
    try std.testing.expectEqualSlices(u8, &.{ 0, 0, 0, 255 }, data.data);
}

test "gif_decode_FFFFFF" {
    const data = try decode(std.testing.allocator, @embedFile("1x1#FFFFFF.gif"));
    defer std.testing.allocator.free(data.data);

    try std.testing.expectEqual(1, data.width);
    try std.testing.expectEqual(1, data.height);
    try std.testing.expectEqualSlices(u8, &.{ 255, 255, 255, 255 }, data.data);
}
