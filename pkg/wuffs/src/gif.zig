const std = @import("std");
const Allocator = std.mem.Allocator;
const c = @import("wuffs_c");
const Error = @import("error.zig").Error;
const check = @import("error.zig").check;
const ImageData = @import("main.zig").ImageData;
const maximum_image_size = @import("main.zig").maximum_image_size;
const mul = std.math.mul;

const log = std.log.scoped(.wuffs_gif);

/// The largest number of frames decodeAnimated will compose from one GIF.
pub const maximum_frames: usize = 1024;

/// The most memory decodeAnimated will commit to composed frames, and the
/// largest canvas it will animate.
///
/// Both bounds are needed and neither is redundant. A GIF states its canvas
/// in four bytes and each frame costs width * height * 4 once composed, so
/// bounding the frame count alone still lets a file a few tens of kilobytes
/// long ask for terabytes. Every limit the caller applies, including the
/// image storage limit, is checked against frames this function has already
/// allocated, so the bound has to live here to be worth anything.
///
/// Matched to the 400MB per-image ceiling the kitty layer enforces, so a
/// still image that would have been accepted before is accepted now.
pub const maximum_animation_bytes: usize = 400 * 1024 * 1024;

/// A decoded GIF animation. Every frame is composed to the full canvas, so
/// any single frame can be displayed without replaying the ones before it.
pub const AnimatedImageData = struct {
    width: u32,
    height: u32,

    /// The frames in display order, at least one.
    frames: []Frame,

    /// How many times the animation should play in total. Zero means play
    /// forever.
    ///
    /// This is not the number the file stores. A GIF's NETSCAPE extension
    /// counts repeats after the first play, so a file declaring 3 plays four
    /// times; wuffs normalises that to the total, and zero still means
    /// forever. The kitty animation model counts the same way, so this maps
    /// straight onto its loop limit.
    loop_count: u32,

    pub const Frame = struct {
        /// width * height * 4 bytes of RGBA.
        data: []u8,

        /// The delay the file declares, in milliseconds. A declared zero is
        /// reported as zero: what a zero delay ought to mean is a policy
        /// question, and the answer differs between the GIF and kitty
        /// models, so it belongs to the caller rather than here.
        delay_ms: u32,
    };

    pub fn deinit(self: *AnimatedImageData, alloc: Allocator) void {
        for (self.frames) |frame| alloc.free(frame.data);
        alloc.free(self.frames);
        self.* = undefined;
    }
};

/// Decode every frame of a GIF, composing each onto a full-canvas RGBA
/// buffer.
///
/// GIF frames are sequentially dependent. A frame may cover only part of the
/// canvas, may blend with or overwrite what is under it, and may ask that its
/// area be restored afterwards. wuffs reports the rectangle, blend and
/// disposal per frame but composes nothing across frames, so this keeps one
/// canvas, decodes each frame into it, snapshots the result, and then applies
/// the disposal the file asked for before moving on.
///
/// Composing eagerly is what lets the caller treat frames as independent
/// images. It costs width * height * 4 per frame, which is the same shape the
/// kitty animation model already stores.
pub fn decodeAnimated(alloc: Allocator, data: []const u8) Error!AnimatedImageData {
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

    if (size > maximum_animation_bytes) {
        log.warn(
            "gif canvas {d} is larger than the maximum allowed ({d})",
            .{ size, maximum_animation_bytes },
        );
        return error.Overflow;
    }

    // How many frames the budget affords. Checked before anything is
    // allocated, because the canvas alone is already the size of one frame.
    const frame_limit = @min(
        maximum_frames,
        @max(@as(usize, 1), maximum_animation_bytes / @max(size, 1)),
    );

    // The canvas every frame is decoded into and disposed from. Zeroed so
    // pixels no frame ever touches stay transparent.
    const canvas = try alloc.alloc(u8, size);
    defer alloc.free(canvas);
    @memset(canvas, 0);

    var pixel_buffer: c.wuffs_base__pixel_buffer = undefined;
    {
        const status = c.wuffs_base__pixel_buffer__set_from_slice(
            &pixel_buffer,
            &image_config.pixcfg,
            c.wuffs_base__make_slice_u8(canvas.ptr, canvas.len),
        );
        try check(log, &status);
    }

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

    // Only allocated if some frame actually asks for restore-to-previous,
    // which most GIFs never do.
    var previous: ?[]u8 = null;
    defer if (previous) |p| alloc.free(p);

    var frames: std.ArrayListUnmanaged(AnimatedImageData.Frame) = .empty;
    errdefer {
        for (frames.items) |frame| alloc.free(frame.data);
        frames.deinit(alloc);
    }

    // Whether the file ran out before the budget did, which is the only case
    // where the caller is seeing the whole animation.
    var complete = false;

    while (frames.items.len < frame_limit) {
        var frame_config: c.wuffs_base__frame_config = undefined;
        const status = c.wuffs_gif__decoder__decode_frame_config(
            decoder,
            &frame_config,
            &source_buffer,
        );
        // End of data is the normal terminator and arrives as a note rather
        // than an error. Compared against that one note specifically: any
        // other note would mean something we did not ask for happened, and
        // treating it as the end would silently truncate the animation.
        if (status.repr == c.wuffs_base__note__end_of_data) {
            complete = true;
            break;
        }
        try check(log, &status);

        const bounds = c.wuffs_base__frame_config__bounds(&frame_config);
        const disposal = c.wuffs_base__frame_config__disposal(&frame_config);
        const duration = c.wuffs_base__frame_config__duration(&frame_config);

        // Save the canvas before this frame draws, because restore-to-previous
        // means restoring what was here before, not after.
        if (disposal == c.WUFFS_BASE__ANIMATION_DISPOSAL__RESTORE_PREVIOUS) {
            if (previous == null) previous = try alloc.alloc(u8, size);
            @memcpy(previous.?, canvas);
        }

        {
            const blend: c.wuffs_base__pixel_blend = if (c.wuffs_base__frame_config__overwrite_instead_of_blend(&frame_config))
                c.WUFFS_BASE__PIXEL_BLEND__SRC
            else
                c.WUFFS_BASE__PIXEL_BLEND__SRC_OVER;

            const frame_status = c.wuffs_gif__decoder__decode_frame(
                decoder,
                &pixel_buffer,
                &source_buffer,
                blend,
                work_slice,
                null,
            );
            try check(log, &frame_status);
        }

        const snapshot = try alloc.dupe(u8, canvas);
        errdefer alloc.free(snapshot);
        try frames.append(alloc, .{
            .data = snapshot,
            .delay_ms = flicksToMs(duration),
        });

        switch (disposal) {
            c.WUFFS_BASE__ANIMATION_DISPOSAL__RESTORE_BACKGROUND => clearRect(
                canvas,
                width,
                bounds,
            ),
            c.WUFFS_BASE__ANIMATION_DISPOSAL__RESTORE_PREVIOUS => @memcpy(
                canvas,
                previous.?,
            ),
            else => {},
        }
    }

    if (frames.items.len == 0) {
        log.warn("gif contained no frames", .{});
        return error.WuffsError;
    }
    if (!complete) {
        log.warn(
            "gif stopped at {d} frames ({d} bytes); the rest are dropped",
            .{ frames.items.len, frames.items.len * size },
        );
    }

    return .{
        .width = width,
        .height = height,
        .frames = try frames.toOwnedSlice(alloc),
        .loop_count = c.wuffs_gif__decoder__num_animation_loops(decoder),
    };
}

/// Zero the frame's rectangle, which is what restore-to-background means for
/// a canvas that starts transparent.
fn clearRect(
    canvas: []u8,
    width: u32,
    bounds: c.wuffs_base__rect_ie_u32,
) void {
    if (bounds.max_excl_x <= bounds.min_incl_x) return;
    const row_len: usize = @as(usize, bounds.max_excl_x - bounds.min_incl_x) * 4;
    var y = bounds.min_incl_y;
    while (y < bounds.max_excl_y) : (y += 1) {
        const start: usize = (@as(usize, y) * width + bounds.min_incl_x) * 4;
        if (start + row_len > canvas.len) return;
        @memset(canvas[start..][0..row_len], 0);
    }
}

/// wuffs reports durations in flicks. Truncating toward zero matches the
/// centisecond granularity GIF actually stores.
fn flicksToMs(duration: c.wuffs_base__flicks) u32 {
    // Flicks are signed. GIF never yields a negative duration, but clamping
    // costs nothing and is better than aborting if one ever appears.
    const ticks: u64 = @intCast(@max(duration, 0));
    const ms = ticks / (c.WUFFS_BASE__FLICKS_PER_SECOND / 1000);
    return std.math.cast(u32, ms) orelse std.math.maxInt(u32);
}

/// Decode the first frame of a GIF image into RGBA pixels.
///
/// GIFs may carry multiple frames; this wrapper exposes the first frame
/// only, for callers that want a still image. decodeAnimated returns the
/// whole animation.
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

test "gif animated: frames, delays and loop count" {
    const alloc = std.testing.allocator;
    var anim = try decodeAnimated(alloc, @embedFile("anim3.gif"));
    defer anim.deinit(alloc);

    try std.testing.expectEqual(@as(u32, 2), anim.width);
    try std.testing.expectEqual(@as(u32, 2), anim.height);
    try std.testing.expectEqual(@as(usize, 3), anim.frames.len);

    // Declared as 100/50/30 ms, which GIF stores exactly as centiseconds.
    try std.testing.expectEqual(@as(u32, 100), anim.frames[0].delay_ms);
    try std.testing.expectEqual(@as(u32, 50), anim.frames[1].delay_ms);
    try std.testing.expectEqual(@as(u32, 30), anim.frames[2].delay_ms);

    // Zero means loop forever, which is what the fixture declares.
    try std.testing.expectEqual(@as(u32, 0), anim.loop_count);

    // Every frame is composed to the full canvas, so the renderer can
    // display any of them without knowing about the others.
    for (anim.frames) |frame| {
        try std.testing.expectEqual(@as(usize, 2 * 2 * 4), frame.data.len);
    }

    // Solid red, then green, then blue.
    try std.testing.expectEqualSlices(u8, &.{ 255, 0, 0, 255 }, anim.frames[0].data[0..4]);
    try std.testing.expectEqualSlices(u8, &.{ 0, 255, 0, 255 }, anim.frames[1].data[0..4]);
    try std.testing.expectEqualSlices(u8, &.{ 0, 0, 255, 255 }, anim.frames[2].data[0..4]);
}

test "gif animated: a finite loop count counts total plays" {
    const alloc = std.testing.allocator;
    var anim = try decodeAnimated(alloc, @embedFile("anim-loop3.gif"));
    defer anim.deinit(alloc);

    try std.testing.expectEqual(@as(usize, 2), anim.frames.len);

    // The fixture's NETSCAPE extension stores 3, which counts repeats after
    // the first play. Four total plays is the same animation, counted the way
    // the kitty model counts it.
    try std.testing.expectEqual(@as(u32, 4), anim.loop_count);
}

test "gif animated: a zero delay is reported as zero" {
    // The decoder stays faithful to the file. Substituting a default gap is
    // the caller's decision, because zero has a meaning in the kitty
    // animation model (gapless, skipped during playback) that a GIF frame
    // never intends.
    const alloc = std.testing.allocator;
    var anim = try decodeAnimated(alloc, @embedFile("anim-nodelay.gif"));
    defer anim.deinit(alloc);

    try std.testing.expectEqual(@as(usize, 2), anim.frames.len);
    try std.testing.expectEqual(@as(u32, 0), anim.frames[0].delay_ms);
    try std.testing.expectEqual(@as(u32, 0), anim.frames[1].delay_ms);
}

test "gif animated: disposal composes each frame onto the canvas" {
    const alloc = std.testing.allocator;
    var anim = try decodeAnimated(alloc, @embedFile("anim-dispose.gif"));
    defer anim.deinit(alloc);

    try std.testing.expectEqual(@as(u32, 4), anim.width);
    try std.testing.expectEqual(@as(u32, 4), anim.height);
    try std.testing.expectEqual(@as(usize, 3), anim.frames.len);
    for (anim.frames) |frame| {
        try std.testing.expectEqual(@as(usize, 4 * 4 * 4), frame.data.len);
    }

    // The frames differ from one another; a composer that returned the same
    // canvas for every frame, or that never advanced, would pass the length
    // checks above but not this.
    try std.testing.expect(!std.mem.eql(u8, anim.frames[0].data, anim.frames[1].data));
    try std.testing.expect(!std.mem.eql(u8, anim.frames[1].data, anim.frames[2].data));
}

test "gif animated: a single-frame gif yields one frame" {
    const alloc = std.testing.allocator;
    var anim = try decodeAnimated(alloc, @embedFile("1x1#000000.gif"));
    defer anim.deinit(alloc);

    try std.testing.expectEqual(@as(usize, 1), anim.frames.len);
    try std.testing.expectEqualSlices(u8, &.{ 0, 0, 0, 255 }, anim.frames[0].data);
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
