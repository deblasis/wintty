const std = @import("std");
const Allocator = std.mem.Allocator;
const c = @import("wuffs_c");
const Error = @import("error.zig").Error;
const check = @import("error.zig").check;
const ImageData = @import("main.zig").ImageData;
const maximum_image_size = @import("main.zig").maximum_image_size;
const mul = std.math.mul;

const log = std.log.scoped(.wuffs_gif);

/// The largest number of frames decodeAnimated will decode from one GIF.
///
/// Every frame is decoded and disposed whether or not it is kept, so this
/// bounds decode time rather than memory. An upper bound and not the ceiling
/// itself: maximum_decode_bytes lowers it as the canvas grows.
pub const maximum_frames: usize = 1024;

/// How many times a frame writes the whole canvas in the worst case: the
/// snapshot before a restore-to-previous frame draws, the decode, the restore.
/// Canvas-sized writes each decoded frame can cost: the decode itself, the
/// restore-previous snapshot taken before it, the restore afterwards, and the
/// dupe kept when the frame is retained. Not every frame pays all four, so
/// this is the ceiling rather than the typical cost.
const canvas_passes_per_frame: usize = 4;

/// The most composition work decodeAnimated will do, counted in bytes written
/// across the canvas.
///
/// Bounding the frame count bounds memory but not time: a file of a few tens
/// of kilobytes can declare a huge canvas and a thousand one-pixel frames,
/// which allocates almost nothing and touches hundreds of gigabytes. This is
/// roughly a quarter of a second of memory traffic, about where the old
/// budget/size frame ceiling put it before decimation took that bound away.
///
/// In bytes rather than frames, so the ceiling falls as the canvas grows: the
/// full maximum_frames up to 256x256, 32 frames at 1080p, one frame at the
/// storage budget. For a large canvas it binds well before the frame count, so
/// a long large-canvas animation is still cut short, and decimation cannot
/// help there: covering a whole loop means decoding every frame of it.
///
/// The floor of one frame is outside this budget: a single frame at the still
/// ceiling costs four passes over 400MB whatever this says, because a file with
/// one frame has to decode that frame. That is what the ceiling this replaced
/// also did, so it is not a widening.
pub const maximum_decode_bytes: usize = 1024 * 1024 * 1024;

/// The largest canvas edge decodeAnimated will animate, matching the
/// dimension the kitty layer accepts.
///
/// Checked against the header before anything is allocated. A byte budget
/// alone does not cover this: a canvas can satisfy any total while still being
/// an absurd shape, and the layer that would reject the shape only looks once
/// the decode is done.
pub const maximum_dimension: u32 = 10000;

/// The most memory decodeAnimated will commit to composed frames.
///
/// A GIF states its canvas in four bytes and each frame costs
/// width * height * 4 once composed, so a file a few tens of kilobytes long
/// can ask for terabytes. Every limit the caller applies is checked against
/// frames this function has already allocated, so the bound has to live here.
///
/// Matched to the default kitty image storage limit, which is what animation
/// frames are charged against, rather than to the larger per-image ceiling:
/// budgeting past the storage limit only moves the truncation up a layer. The
/// caller stays the final authority even so, because that limit is
/// user-configurable and the store is shared with every other image, neither
/// of which is visible from here. A store configured smaller, or busy with
/// other images, can still refuse frames this decoded; a store configured
/// larger just gets fewer frames than it could have held.
pub const maximum_animation_bytes: usize = 320 * 1000 * 1000;

/// The largest canvas decodeAnimated will decode at all.
///
/// Separate from the frame budget because they answer different questions.
/// The frame budget says how many composed frames may be retained; this says
/// how large one frame may be. Every GIF comes through here, so testing the
/// frame budget alone would refuse a still image larger than it, which the
/// layer above is willing to hold and which decoded fine before frames were
/// budgeted at all.
///
/// Matched to the per-image ceiling that layer enforces, so this is never the
/// binding limit in practice; it is here so that lowering the frame budget
/// cannot silently narrow the still path with it.
pub const maximum_still_bytes: usize = 400 * 1024 * 1024;

comptime {
    // A still is allowed to be larger than the whole frame budget, by the
    // floor of one frame. The reverse would mean a canvas that fits the frame
    // budget but is refused outright, which is never what is wanted.
    std.debug.assert(maximum_still_bytes >= maximum_animation_bytes);
}

/// The bounds a decode runs within.
///
/// Separate from the constants above only so the tests can drive the
/// decimation and both decode ceilings with a fixture that costs bytes rather
/// than one that costs hundreds of megabytes.
const Limits = struct {
    /// The most memory committed to retained frames.
    animation_bytes: usize = maximum_animation_bytes,

    /// The most frames decoded, retained or not.
    decode_limit: usize = maximum_frames,

    /// The most bytes written across the canvas while decoding.
    decode_bytes: usize = maximum_decode_bytes,

    /// The largest canvas accepted at all, animated or not.
    still_bytes: usize = maximum_still_bytes,
};

/// A decoded GIF animation. Every frame is composed to the full canvas, so
/// any single frame can be displayed without replaying the ones before it.
pub const AnimatedImageData = struct {
    width: u32,
    height: u32,

    /// The frames in display order, at least one. Not necessarily every
    /// frame the file holds; see decodeAnimated.
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

        /// How long the frame is shown, in milliseconds. If the animation was
        /// decimated this covers the frames it stands in for, which is what
        /// keeps one loop the length the file asks for.
        ///
        /// A declared zero is reported as zero: what a zero delay ought to
        /// mean differs between the GIF and kitty models, so the answer
        /// belongs to the caller.
        delay_ms: u32,

        /// How many of the frames this one stands in for declared no delay,
        /// itself included.
        ///
        /// A caller that substitutes a gap for a zero delay needs the count,
        /// because zero delays fold into zero: without it a decimated
        /// animation gets one gap where the file asked for several.
        zero_delay_frames: u32,
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
/// the disposal the file asked for before moving on. Composing eagerly is what
/// lets the caller treat frames as independent images.
///
/// An animation whose frames do not all fit in maximum_animation_bytes is
/// decimated, not cut short: every kth frame is kept and the delays in between
/// are folded into it, so the caller gets the whole loop at a lower frame rate
/// rather than the opening of it repeating forever.
pub fn decodeAnimated(alloc: Allocator, data: []const u8) Error!AnimatedImageData {
    return decodeAnimatedLimited(alloc, data, .{});
}

fn decodeAnimatedLimited(
    alloc: Allocator,
    data: []const u8,
    limits: Limits,
) Error!AnimatedImageData {
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

    if (width > maximum_dimension or height > maximum_dimension) {
        log.warn(
            "gif canvas {d}x{d} exceeds the maximum dimension ({d})",
            .{ width, height, maximum_dimension },
        );
        return error.Overflow;
    }

    const size: usize = try mul(
        usize,
        try mul(usize, width, height),
        @sizeOf(c.wuffs_base__color_u32_argb_premul),
    );

    // Rejected against the still ceiling, not the frame budget. Every GIF comes
    // through here now, including single-frame ones, so testing the frame
    // budget would refuse a still image that the layer above is willing to hold
    // and that decoded fine before there was a frame budget at all.
    if (size > limits.still_bytes) {
        log.warn(
            "gif canvas {d} is larger than the maximum allowed ({d})",
            .{ size, limits.still_bytes },
        );
        return error.Overflow;
    }

    // How many composed frames the budget affords, computed before anything is
    // allocated because the canvas alone is already one frame. The floor of one
    // is what lets a still image larger than the frame budget through: that one
    // frame is the image itself, charged as an image rather than as animation
    // frames, and the layer above applies its own ceiling to it.
    const frame_capacity = @max(
        @as(usize, 1),
        limits.animation_bytes / @max(size, 1),
    );

    // How many frames may be decoded at all. Storage is met by lowering the
    // frame rate, which costs nothing in time; time can only be met by
    // decoding fewer frames, so this is the one ceiling that still truncates.
    const decode_ceiling = @min(
        limits.decode_limit,
        @max(
            @as(usize, 1),
            limits.decode_bytes / (@max(size, 1) * canvas_passes_per_frame),
        ),
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

    // Frames whose index is a multiple of this one are retained. It doubles
    // whenever the retained set fills the budget, so it cannot be chosen up
    // front: a GIF declares its frame count nowhere.
    var stride: usize = 1;

    // Whether the file ran out before the decode ceiling did, which is now
    // the only case where frames are lost off the end.
    var complete = false;

    var index: usize = 0;
    while (index < decode_ceiling) : (index += 1) {
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

        const keep = keep: {
            if (index % stride != 0) break :keep false;
            if (frames.items.len < frame_capacity) break :keep true;

            // The budget is full and the file is not done, so halve what is
            // retained and carry on at twice the stride. The frame in hand
            // was on the old stride but may not be on the new one, so the
            // test has to run again rather than being assumed.
            decimate(alloc, &frames);
            stride *= 2;
            break :keep index % stride == 0 and frames.items.len < frame_capacity;
        };

        // The retained delays always sum to the delays of every frame decoded
        // so far, so one loop still takes the time the file asks for. A frame
        // that is not retained shows as the retained frame before it staying
        // up for longer, which is where its delay goes. Folding into the next
        // retained frame instead would hold the same total but start every
        // retained frame early by the delays it absorbed.
        const delay_ms = flicksToMs(duration);
        if (keep) {
            const snapshot = try alloc.dupe(u8, canvas);
            errdefer alloc.free(snapshot);
            try frames.append(alloc, .{
                .data = snapshot,
                .delay_ms = delay_ms,
                .zero_delay_frames = @intFromBool(delay_ms == 0),
            });
        } else {
            // Index zero is a multiple of every stride and arrives to an empty
            // list, so it is always retained and there is always a frame here
            // to fold into. Saturating, because a total past 49 days does not
            // fit in the u32 a frame carries.
            const last = &frames.items[frames.items.len - 1];
            last.delay_ms +|= delay_ms;
            if (delay_ms == 0) last.zero_delay_frames +|= 1;
        }

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
            "gif stopped at the {d} frame decode ceiling; the rest are dropped",
            .{decode_ceiling},
        );
    }
    if (stride > 1) {
        log.info(
            "gif animation reduced to one frame in {d} ({d} of {d}) to fit {d} bytes",
            .{ stride, frames.items.len, index, limits.animation_bytes },
        );
    }

    return .{
        .width = width,
        .height = height,
        .frames = try frames.toOwnedSlice(alloc),
        .loop_count = c.wuffs_gif__decoder__num_animation_loops(decoder),
    };
}

/// Halve the retained frames in place, folding each dropped frame's delay
/// into the frame that now stands in for it.
///
/// Every odd position is freed and its delay added to the even position
/// before it. An odd count leaves its last frame alone: that frame sits at an
/// even position, so the retained set stays exactly the frames whose index is
/// a multiple of the doubled stride, with no gap in the middle.
///
/// The write cursor never passes the read cursor, so no buffer is ever owned
/// by two entries, and shrinking at the end keeps the caller's errdefer off
/// freed pointers.
fn decimate(
    alloc: Allocator,
    frames: *std.ArrayListUnmanaged(AnimatedImageData.Frame),
) void {
    var kept: usize = 0;
    var i: usize = 0;
    while (i < frames.items.len) : (i += 2) {
        var frame = frames.items[i];
        if (i + 1 < frames.items.len) {
            frame.delay_ms +|= frames.items[i + 1].delay_ms;
            frame.zero_delay_frames +|= frames.items[i + 1].zero_delay_frames;
            alloc.free(frames.items[i + 1].data);
        }
        frames.items[kept] = frame;
        kept += 1;
    }
    frames.shrinkRetainingCapacity(kept);
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
    // Flicks are signed, and clamping a negative one costs nothing.
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
        try std.testing.expectEqual(@as(u32, 0), frame.zero_delay_frames);
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
    // the caller's decision, because zero means gapless in the kitty animation
    // model, which a GIF frame never intends.
    const alloc = std.testing.allocator;
    var anim = try decodeAnimated(alloc, @embedFile("anim-nodelay.gif"));
    defer anim.deinit(alloc);

    try std.testing.expectEqual(@as(usize, 2), anim.frames.len);
    try std.testing.expectEqual(@as(u32, 0), anim.frames[0].delay_ms);
    try std.testing.expectEqual(@as(u32, 0), anim.frames[1].delay_ms);
    try std.testing.expectEqual(@as(u32, 1), anim.frames[0].zero_delay_frames);
    try std.testing.expectEqual(@as(u32, 1), anim.frames[1].zero_delay_frames);
}

test "gif animated: a file from a real encoder decodes to distinct frames" {
    // Every frame of this fixture overwrites the whole canvas opaquely, so it
    // says nothing about composition. What it covers is bytes a real encoder
    // produced rather than the ones testGif writes.
    const alloc = std.testing.allocator;
    var anim = try decodeAnimated(alloc, @embedFile("anim-dispose.gif"));
    defer anim.deinit(alloc);

    try std.testing.expectEqual(@as(u32, 4), anim.width);
    try std.testing.expectEqual(@as(u32, 4), anim.height);
    try std.testing.expectEqual(@as(usize, 3), anim.frames.len);
    for (anim.frames) |frame| {
        try std.testing.expectEqual(@as(usize, 4 * 4 * 4), frame.data.len);
    }

    try std.testing.expect(!std.mem.eql(u8, anim.frames[0].data, anim.frames[1].data));
    try std.testing.expect(!std.mem.eql(u8, anim.frames[1].data, anim.frames[2].data));
}

test "gif animated: an absurd canvas shape is rejected from the header" {
    // A GIF89a header declaring a 20000x1 logical screen, then nothing. That
    // is 80KB composed, inside every byte budget, so only the dimension check
    // can refuse it.
    const header = [_]u8{
        'G', 'I', 'F', '8', '9', 'a',
        0x20, 0x4e, // width 20000
        0x01, 0x00, // height 1
        0x00, 0x00,
        0x00,
        0x3b, // trailer
    };

    try std.testing.expectError(
        error.Overflow,
        decodeAnimated(std.testing.allocator, &header),
    );
}

test "gif animated: a canvas past the still ceiling is rejected from the header" {
    // The canvas is four bytes of the file, so an oversized one has to be
    // refused before anything is allocated. Driven through the limit rather
    // than through a large header because the dimension check binds first for
    // every canvas the production ceiling would reject: 10000x10000, the
    // largest shape that check allows, composes to just under it.
    const header = [_]u8{
        'G', 'I', 'F', '8', '9', 'a',
        0x10, 0x27, // width 10000
        0x10, 0x27, // height 10000
        0x00, 0x00,
        0x00,
        0x3b, // trailer
    };

    try std.testing.expectError(error.Overflow, decodeAnimatedLimited(
        std.testing.allocator,
        &header,
        .{ .still_bytes = 400 * 1000 * 1000 - 1 },
    ));
}

test "gif animated: a still larger than the frame budget still decodes" {
    // Every GIF routes through this decoder, so the frame budget must not be
    // what decides whether a single-frame image is accepted. One frame past
    // that budget is the image itself, and the layer above charges it as an
    // image and applies its own ceiling.
    const alloc = std.testing.allocator;
    const data = try testAnimation(alloc, &[_]u16{10});
    defer alloc.free(data);

    // A 2x2 canvas is 16 bytes; a budget of 1 cannot hold a single frame.
    var anim = try decodeAnimatedLimited(alloc, data, .{ .animation_bytes = 1 });
    defer anim.deinit(alloc);

    try std.testing.expectEqual(@as(usize, 1), anim.frames.len);
}

test "gif animated: a single-frame gif yields one frame" {
    const alloc = std.testing.allocator;
    var anim = try decodeAnimated(alloc, @embedFile("1x1#000000.gif"));
    defer anim.deinit(alloc);

    try std.testing.expectEqual(@as(usize, 1), anim.frames.len);
    try std.testing.expectEqualSlices(u8, &.{ 0, 0, 0, 255 }, anim.frames[0].data);
}

/// The colour table the generated fixtures use. Eight entries, so a frame can
/// name a colour and still have a spare index to declare transparent.
const test_palette = [_]u8{
    0xff, 0x00, 0x00, // 0 red
    0x00, 0xff, 0x00, // 1 green
    0x00, 0x00, 0xff, // 2 blue
    0xff, 0xff, 0xff, // 3 white
    0xff, 0xff, 0x00, // 4 yellow
    0x00, 0xff, 0xff, // 5 cyan
    0xff, 0x00, 0xff, // 6 magenta
    0x00, 0x00, 0x00, // 7 the entry frames declare transparent
};

/// One frame of a generated fixture, in colour table indices.
const TestFrame = struct {
    /// The delay in centiseconds, which is the unit GIF stores.
    delay_cs: u16 = 0,

    /// GIF disposal method: 1 leaves the canvas alone, 2 restores the
    /// background, 3 restores what was under the frame before it drew.
    disposal: u3 = 1,

    /// The colour table entry that is transparent, or null for an opaque
    /// frame. An opaque frame overwrites its rectangle; a transparent one
    /// blends over whatever is under it.
    transparent: ?u8 = null,

    left: u16 = 0,
    top: u16 = 0,
    width: u16,
    height: u16,

    /// One colour table index per pixel, row major, width * height long.
    pixels: []const u8,

    /// A local colour table shaped like test_palette, or null to use the
    /// global one.
    palette: ?[]const u8 = null,
};

fn appendU16(
    alloc: Allocator,
    out: *std.ArrayListUnmanaged(u8),
    value: u16,
) Allocator.Error!void {
    try out.append(alloc, @as(u8, @truncate(value)));
    try out.append(alloc, @as(u8, @intCast(value >> 8)));
}

/// Append a frame's image data, coded as literals so no compressor is needed:
/// a clear code, one nine bit code per pixel, then end of information. A
/// minimum code size of eight keeps every code nine bits wide until 254 more
/// have been added to the table, which no fixture here comes near.
fn appendImageData(
    alloc: Allocator,
    out: *std.ArrayListUnmanaged(u8),
    pixels: []const u8,
) Allocator.Error!void {
    try out.append(alloc, 8);

    var data: std.ArrayListUnmanaged(u8) = .empty;
    defer data.deinit(alloc);

    const Packer = struct {
        alloc: Allocator,
        data: *std.ArrayListUnmanaged(u8),
        acc: u32 = 0,
        bits: u5 = 0,

        fn push(self: *@This(), code: u16) Allocator.Error!void {
            self.acc |= @as(u32, code) << self.bits;
            self.bits += 9;
            while (self.bits >= 8) {
                try self.data.append(self.alloc, @as(u8, @truncate(self.acc)));
                self.acc >>= 8;
                self.bits -= 8;
            }
        }

        fn flush(self: *@This()) Allocator.Error!void {
            if (self.bits > 0) {
                try self.data.append(self.alloc, @as(u8, @truncate(self.acc)));
            }
        }
    };

    var packer: Packer = .{ .alloc = alloc, .data = &data };
    try packer.push(256);
    for (pixels) |pixel| try packer.push(pixel);
    try packer.push(257);
    try packer.flush();

    var i: usize = 0;
    while (i < data.items.len) {
        const len = @min(@as(usize, 255), data.items.len - i);
        try out.append(alloc, @as(u8, @intCast(len)));
        try out.appendSlice(alloc, data.items[i..][0..len]);
        i += len;
    }
    try out.append(alloc, 0x00);
}

/// Build a GIF from frames described in colour table indices.
///
/// The checked-in fixtures are three frames that each overwrite the whole
/// canvas, which is neither long enough to watch the retained set halve nor
/// dependent enough to tell a composed canvas from a fresh one.
fn testGif(
    alloc: Allocator,
    width: u16,
    height: u16,
    frames: []const TestFrame,
) ![]u8 {
    var out: std.ArrayListUnmanaged(u8) = .empty;
    errdefer out.deinit(alloc);

    // Header and logical screen, with an eight entry global colour table,
    // background index zero and square pixels.
    try out.appendSlice(alloc, "GIF89a");
    try appendU16(alloc, &out, width);
    try appendU16(alloc, &out, height);
    try out.appendSlice(alloc, "\x82\x00\x00");
    try out.appendSlice(alloc, &test_palette);

    // NETSCAPE, zero repeats, which means loop forever.
    try out.appendSlice(alloc, "\x21\xff\x0bNETSCAPE2.0\x03\x01\x00\x00\x00");

    for (frames) |frame| {
        // Graphic control: disposal, transparency and delay.
        try out.appendSlice(alloc, "\x21\xf9\x04");
        try out.append(alloc, (@as(u8, frame.disposal) << 2) |
            @as(u8, @intFromBool(frame.transparent != null)));
        try appendU16(alloc, &out, frame.delay_cs);
        try out.append(alloc, frame.transparent orelse 0);
        try out.append(alloc, 0x00);

        // Image descriptor, with a local colour table only where the frame
        // asked for one.
        try out.append(alloc, 0x2c);
        try appendU16(alloc, &out, frame.left);
        try appendU16(alloc, &out, frame.top);
        try appendU16(alloc, &out, frame.width);
        try appendU16(alloc, &out, frame.height);
        if (frame.palette) |palette| {
            try out.append(alloc, 0x82);
            try out.appendSlice(alloc, palette);
        } else {
            try out.append(alloc, 0x00);
        }

        try appendImageData(alloc, &out, frame.pixels);
    }

    try out.append(alloc, 0x3b);
    return out.toOwnedSlice(alloc);
}

/// Write the RGBA bytes a canvas of these colour table indices decodes to.
/// Null is a pixel no frame ever painted, which stays transparent.
fn testCanvas(indices: []const ?u8, out: []u8) void {
    for (indices, 0..) |index, i| {
        const pixel = out[i * 4 ..][0..4];
        if (index) |entry| {
            @memcpy(pixel[0..3], test_palette[@as(usize, entry) * 3 ..][0..3]);
            pixel[3] = 0xff;
        } else {
            @memset(pixel, 0);
        }
    }
}

/// Build a GIF of one 2x2 frame per entry in `delays_cs`, each frame a flat
/// shade of its own so a test can name the frames that were kept.
fn testAnimation(alloc: Allocator, delays_cs: []const u16) ![]u8 {
    const flat = [_]u8{ 0, 0, 0, 0 };

    const palettes = try alloc.alloc([test_palette.len]u8, delays_cs.len);
    defer alloc.free(palettes);

    const frames = try alloc.alloc(TestFrame, delays_cs.len);
    defer alloc.free(frames);

    for (delays_cs, palettes, frames, 0..) |delay, *palette, *frame, i| {
        // All four pixels use entry zero, so the frame is a flat shade the
        // tests can name: the shade is the frame's own number, one based.
        palette.* = test_palette;
        const shade: u8 = @intCast(i + 1);
        palette[0] = shade;
        palette[1] = shade;
        palette[2] = shade;

        frame.* = .{
            .width = 2,
            .height = 2,
            .pixels = &flat,
            .delay_cs = delay,
            .palette = palette,
        };
    }

    return testGif(alloc, 2, 2, frames);
}

/// Sixteen frame delays a centisecond apart, which GIF stores exactly and
/// which decode to 10, 20 ... 160ms, or 1360ms for a whole loop. Distinct
/// values, so a fold that dropped or double counted one would show up in the
/// total.
fn testRampDelays() [16]u16 {
    var delays: [16]u16 = undefined;
    for (&delays, 0..) |*delay, i| delay.* = @intCast(i + 1);
    return delays;
}

/// Five frames of a 4x4 canvas, two of which restore what was under them.
/// Every frame's correct pixels depend on the frames before it, so a decode
/// that skipped one frame's composition, its snapshot or its restore comes out
/// different from that point on.
fn testRestorePreviousGif(alloc: Allocator) ![]u8 {
    const red_canvas = [_]u8{0} ** 16;
    const blue_square = [_]u8{2} ** 4;
    const green_row = [_]u8{1} ** 4;
    const yellow_square = [_]u8{4} ** 4;
    const cyan_square = [_]u8{5} ** 4;

    return testGif(alloc, 4, 4, &.{
        .{ .width = 4, .height = 4, .pixels = &red_canvas, .delay_cs = 10 },
        .{
            .width = 2,
            .height = 2,
            .pixels = &blue_square,
            .disposal = 3,
            .delay_cs = 20,
        },
        .{ .width = 4, .height = 1, .pixels = &green_row, .delay_cs = 30 },
        .{
            .left = 1,
            .top = 1,
            .width = 2,
            .height = 2,
            .pixels = &yellow_square,
            .disposal = 3,
            .delay_cs = 40,
        },
        .{
            .left = 2,
            .top = 2,
            .width = 2,
            .height = 2,
            .pixels = &cyan_square,
            .delay_cs = 50,
        },
    });
}

test "gif animated: frames compose onto one canvas" {
    // Partial rectangles, a transparent index and a restore-to-background
    // disposal, so a decode that started each frame from a fresh canvas, that
    // overwrote where it should have blended, or that skipped the disposal
    // produces different pixels here.
    const alloc = std.testing.allocator;

    const red_canvas = [_]u8{0} ** 16;
    const green_square = [_]u8{1} ** 4;
    const blue_checker = [_]u8{ 2, 7, 7, 2 };
    const yellow_square = [_]u8{4} ** 4;
    const cyan_pixel = [_]u8{ 7, 7, 7, 5 } ++ ([_]u8{7} ** 12);

    const data = try testGif(alloc, 4, 4, &.{
        .{ .width = 4, .height = 4, .pixels = &red_canvas, .delay_cs = 10 },
        .{
            .left = 1,
            .top = 1,
            .width = 2,
            .height = 2,
            .pixels = &green_square,
            .delay_cs = 20,
        },
        .{
            .width = 2,
            .height = 2,
            .pixels = &blue_checker,
            .transparent = 7,
            .disposal = 2,
            .delay_cs = 30,
        },
        .{
            .left = 2,
            .top = 2,
            .width = 2,
            .height = 2,
            .pixels = &yellow_square,
            .delay_cs = 40,
        },
        .{
            .width = 4,
            .height = 4,
            .pixels = &cyan_pixel,
            .transparent = 7,
            .delay_cs = 50,
        },
    });
    defer alloc.free(data);

    var anim = try decodeAnimated(alloc, data);
    defer anim.deinit(alloc);

    try std.testing.expectEqual(@as(usize, 5), anim.frames.len);

    var expected: [4 * 4 * 4]u8 = undefined;

    // Green over the middle of the red canvas, so the ring of red around it
    // can only have come from the frame before.
    const frame_two = [_]?u8{ 0, 0, 0, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 0 };
    testCanvas(&frame_two, &expected);
    try std.testing.expectEqualSlices(u8, &expected, anim.frames[1].data);

    // Two blue pixels and two transparent ones over the top left corner: the
    // transparent pair leaves the red and green underneath showing.
    const frame_three = [_]?u8{ 2, 0, 0, 0, 0, 2, 1, 0, 0, 1, 1, 0, 0, 0, 0, 0 };
    testCanvas(&frame_three, &expected);
    try std.testing.expectEqualSlices(u8, &expected, anim.frames[2].data);

    // That frame asked for its rectangle to go back to the background, which
    // leaves those four pixels transparent under the yellow square.
    const frame_four = [_]?u8{ null, null, 0, 0, null, null, 1, 0, 0, 1, 4, 4, 0, 0, 4, 4 };
    testCanvas(&frame_four, &expected);
    try std.testing.expectEqualSlices(u8, &expected, anim.frames[3].data);

    // The last frame is transparent but for one pixel, so it is the canvas as
    // it stood with a single cyan pixel painted into it.
    const frame_five = [_]?u8{ null, null, 0, 5, null, null, 1, 0, 0, 1, 4, 4, 0, 0, 4, 4 };
    testCanvas(&frame_five, &expected);
    try std.testing.expectEqualSlices(u8, &expected, anim.frames[4].data);
}

test "gif animated: restore to previous is undone before the next frame" {
    // The snapshot has to be taken before the frame draws and put back after
    // it, or the frames that follow inherit pixels the file never meant to
    // keep. No checked-in fixture asks for this disposal at all.
    const alloc = std.testing.allocator;
    const data = try testRestorePreviousGif(alloc);
    defer alloc.free(data);

    var anim = try decodeAnimated(alloc, data);
    defer anim.deinit(alloc);

    try std.testing.expectEqual(@as(usize, 5), anim.frames.len);

    var expected: [4 * 4 * 4]u8 = undefined;

    // The frame itself is still composed onto the canvas.
    const frame_two = [_]?u8{ 2, 2, 0, 0, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    testCanvas(&frame_two, &expected);
    try std.testing.expectEqualSlices(u8, &expected, anim.frames[1].data);

    // And gone by the next one, which paints its row onto the canvas as it
    // stood before the blue square drew.
    const frame_three = [_]?u8{ 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    testCanvas(&frame_three, &expected);
    try std.testing.expectEqualSlices(u8, &expected, anim.frames[2].data);

    const frame_four = [_]?u8{ 1, 1, 1, 1, 0, 4, 4, 0, 0, 4, 4, 0, 0, 0, 0, 0 };
    testCanvas(&frame_four, &expected);
    try std.testing.expectEqualSlices(u8, &expected, anim.frames[3].data);

    // The second restore has to put back the green row, which is what was
    // under that frame, and not the all red canvas the first restore saved.
    const frame_five = [_]?u8{ 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 5, 5, 0, 0, 5, 5 };
    testCanvas(&frame_five, &expected);
    try std.testing.expectEqualSlices(u8, &expected, anim.frames[4].data);
}

test "gif animated: decimation preserves the total duration" {
    const alloc = std.testing.allocator;
    const delays = testRampDelays();
    const data = try testAnimation(alloc, &delays);
    defer alloc.free(data);

    // Room for four frames against sixteen, so the retained set halves
    // twice on the way through.
    var anim = try decodeAnimatedLimited(alloc, data, .{
        .animation_bytes = 4 * 2 * 2 * 4,
    });
    defer anim.deinit(alloc);

    var total: u32 = 0;
    for (anim.frames) |frame| total += frame.delay_ms;

    // One loop takes as long as the file asks for. Truncating to the first
    // four frames would leave 100ms of the 1360.
    try std.testing.expectEqual(@as(u32, 1360), total);

    // And the memory is inside the budget it was decimated to fit.
    try std.testing.expect(anim.frames.len * 2 * 2 * 4 <= 4 * 2 * 2 * 4);
}

test "gif animated: decimation covers the animation end to end" {
    const alloc = std.testing.allocator;
    const delays = testRampDelays();
    const data = try testAnimation(alloc, &delays);
    defer alloc.free(data);

    var anim = try decodeAnimatedLimited(alloc, data, .{
        .animation_bytes = 4 * 2 * 2 * 4,
    });
    defer anim.deinit(alloc);

    try std.testing.expectEqual(@as(usize, 4), anim.frames.len);

    // Frames 1, 5, 9 and 13 of the sixteen, by the shade each carries. The
    // first frame is still the first frame, which the kitty layer relies on
    // when it peels it off to use as the image; the last is within one
    // stride of the end rather than a quarter of the way in.
    const shades = [_]u8{ 1, 5, 9, 13 };
    for (anim.frames, &shades) |frame, shade| {
        try std.testing.expectEqualSlices(
            u8,
            &[_]u8{ shade, shade, shade, 255 },
            frame.data[0..4],
        );
    }

    // Each retained frame carries the delays of the three it stands in for,
    // so it comes up at the moment it would have without decimation.
    const expected = [_]u32{ 100, 260, 420, 580 };
    for (anim.frames, &expected) |frame, delay_ms| {
        try std.testing.expectEqual(delay_ms, frame.delay_ms);
    }
}

test "gif animated: halving an odd count carries the last frame forward" {
    // A retained set of three halves to the first and the third, which is the
    // only branch of decimate no other test reaches: every other one starts
    // from an even count.
    const alloc = std.testing.allocator;
    const delays = testRampDelays();
    const data = try testAnimation(alloc, &delays);
    defer alloc.free(data);

    // Room for three frames against sixteen, so every halving runs on an odd
    // count.
    var anim = try decodeAnimatedLimited(alloc, data, .{
        .animation_bytes = 3 * 2 * 2 * 4,
    });
    defer anim.deinit(alloc);

    // Frames 1 and 9 of the sixteen, holding the delays of frames 1 to 8 and 9
    // to 16. A halving that folded the odd last frame into the one before it
    // instead would retain frames 1, 9 and 13, at the same total duration.
    try std.testing.expectEqual(@as(usize, 2), anim.frames.len);
    const shades = [_]u8{ 1, 9 };
    for (anim.frames, &shades) |frame, shade| {
        try std.testing.expectEqualSlices(
            u8,
            &[_]u8{ shade, shade, shade, 255 },
            frame.data[0..4],
        );
    }
    try std.testing.expectEqual(@as(u32, 360), anim.frames[0].delay_ms);
    try std.testing.expectEqual(@as(u32, 1000), anim.frames[1].delay_ms);
}

test "gif animated: a budget of one frame still spans the whole loop" {
    // A single frame fills the budget on its own, so nothing but the first
    // frame survives and it holds the whole duration: a still image shown for
    // the length of the animation rather than a fragment looping fast.
    const alloc = std.testing.allocator;
    const delays = testRampDelays();
    const data = try testAnimation(alloc, &delays);
    defer alloc.free(data);

    var anim = try decodeAnimatedLimited(alloc, data, .{
        .animation_bytes = 2 * 2 * 4,
    });
    defer anim.deinit(alloc);

    try std.testing.expectEqual(@as(usize, 1), anim.frames.len);
    try std.testing.expectEqual(@as(u32, 1360), anim.frames[0].delay_ms);
    try std.testing.expectEqualSlices(
        u8,
        &.{ 1, 1, 1, 255 },
        anim.frames[0].data[0..4],
    );
}

test "gif animated: an animation inside the budget is untouched" {
    const alloc = std.testing.allocator;
    const data = @embedFile("anim3.gif");

    var full = try decodeAnimated(alloc, data);
    defer full.deinit(alloc);

    // Exactly enough room for all three frames. Decimating one step early
    // would halve an animation that fits.
    var fitted = try decodeAnimatedLimited(alloc, data, .{
        .animation_bytes = 3 * 2 * 2 * 4,
    });
    defer fitted.deinit(alloc);

    try std.testing.expectEqual(full.frames.len, fitted.frames.len);
    for (full.frames, fitted.frames) |expected, actual| {
        try std.testing.expectEqual(expected.delay_ms, actual.delay_ms);
        try std.testing.expectEqualSlices(u8, expected.data, actual.data);
    }
}

test "gif animated: a skipped frame is still composed and disposed" {
    // Decimation skips the snapshot and nothing else: the frame is still
    // decoded and disposed, so a retained frame has to come out byte for byte
    // the same as it does when nothing is dropped.
    const alloc = std.testing.allocator;
    const data = try testRestorePreviousGif(alloc);
    defer alloc.free(data);

    var full = try decodeAnimated(alloc, data);
    defer full.deinit(alloc);

    // Room for two of the five frames. The set halves at index two and again
    // at index four, so index three arrives at a stride of two: skipped
    // outright rather than retained and dropped later.
    var fitted = try decodeAnimatedLimited(alloc, data, .{
        .animation_bytes = 2 * 4 * 4 * 4,
    });
    defer fitted.deinit(alloc);

    try std.testing.expectEqual(@as(usize, 5), full.frames.len);
    try std.testing.expectEqual(@as(usize, 2), fitted.frames.len);
    try std.testing.expectEqualSlices(
        u8,
        full.frames[0].data,
        fitted.frames[0].data,
    );
    try std.testing.expectEqualSlices(
        u8,
        full.frames[4].data,
        fitted.frames[1].data,
    );

    // The dropped delays went to the frame left on screen in their place.
    try std.testing.expectEqual(
        @as(u32, 100 + 200 + 300 + 400),
        fitted.frames[0].delay_ms,
    );
    try std.testing.expectEqual(@as(u32, 500), fitted.frames[1].delay_ms);
}

test "gif animated: decimating zero delays keeps the loop the same length" {
    // Zero delays fold into zero, so the count is the only thing stopping a
    // caller substituting a single gap where the file asked for four.
    const alloc = std.testing.allocator;
    const delays = [_]u16{0} ** 16;
    const data = try testAnimation(alloc, &delays);
    defer alloc.free(data);

    var anim = try decodeAnimatedLimited(alloc, data, .{
        .animation_bytes = 4 * 2 * 2 * 4,
    });
    defer anim.deinit(alloc);

    try std.testing.expectEqual(@as(usize, 4), anim.frames.len);
    for (anim.frames) |frame| {
        try std.testing.expectEqual(@as(u32, 0), frame.delay_ms);
        try std.testing.expectEqual(@as(u32, 4), frame.zero_delay_frames);
    }
}

test "gif animated: the decode ceiling still truncates" {
    // Decimation answers the byte budget, so the decode ceiling is the one
    // bound that can still cost frames off the end. It also logs, which this
    // cannot observe.
    const alloc = std.testing.allocator;
    const delays = testRampDelays();
    const data = try testAnimation(alloc, &delays);
    defer alloc.free(data);

    var anim = try decodeAnimatedLimited(alloc, data, .{ .decode_limit = 3 });
    defer anim.deinit(alloc);

    // The frames that were decoded are untouched: the byte budget was never
    // the constraint, so nothing was folded into anything.
    try std.testing.expectEqual(@as(usize, 3), anim.frames.len);
    try std.testing.expectEqual(@as(u32, 10), anim.frames[0].delay_ms);
    try std.testing.expectEqual(@as(u32, 20), anim.frames[1].delay_ms);
    try std.testing.expectEqual(@as(u32, 30), anim.frames[2].delay_ms);
}

test "gif animated: the decode ceiling counts every pass over the canvas" {
    // The budget below is four frames of a 2x2 canvas at four passes each, so
    // a ceiling counting one pass per frame would decode sixteen and one
    // counting three would decode five.
    const alloc = std.testing.allocator;
    const delays = testRampDelays();
    const data = try testAnimation(alloc, &delays);
    defer alloc.free(data);

    var anim = try decodeAnimatedLimited(alloc, data, .{
        .decode_bytes = 4 * 2 * 2 * 4 * canvas_passes_per_frame,
    });
    defer anim.deinit(alloc);

    try std.testing.expectEqual(@as(usize, 4), anim.frames.len);
    try std.testing.expectEqual(@as(u32, 10), anim.frames[0].delay_ms);
    try std.testing.expectEqual(@as(u32, 40), anim.frames[3].delay_ms);

    // A budget too small for a single frame still decodes one. A file with one
    // frame has to decode that frame, so the floor sits outside this budget and
    // is bounded by the still ceiling instead.
    var one = try decodeAnimatedLimited(alloc, data, .{ .decode_bytes = 1 });
    defer one.deinit(alloc);
    try std.testing.expectEqual(@as(usize, 1), one.frames.len);
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
