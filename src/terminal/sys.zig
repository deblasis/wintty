//! System interface for the terminal package.
//!
//! This provides runtime-swappable function pointers for operations that
//! depend on external implementations (e.g. image decoding). Each function
//! pointer is initialized with a default implementation if available.
//!
//! This exists so that the terminal package doesn't have hard dependencies
//! on specific libraries and enables embedders of the terminal package to
//! swap out implementations as needed at startup to provide their own
//! implementations.
const std = @import("std");
const Allocator = std.mem.Allocator;
const build_options = @import("terminal_options");

/// Decode PNG data into RGBA pixels. If null, PNG decoding is unsupported
/// and the exact semantics are up to callers. For example, the Kitty Graphics
/// Protocol will work but cannot accept PNG images.
pub var decode_png: ?DecodePngFn = png: {
    if (build_options.artifact == .lib) break :png null;
    break :png &decodePngWuffs;
};

/// Decode JPEG data into RGBA pixels. Same semantics as decode_png. Used by
/// the iTerm2 OSC 1337 File= synth path which sniffs the magic bytes and
/// routes JPEG payloads through the Kitty graphics decoder.
pub var decode_jpeg: ?DecodeJpegFn = jpeg: {
    if (build_options.artifact == .lib) break :jpeg null;
    break :jpeg &decodeJpegWuffs;
};

/// Decode the first frame of a GIF into RGBA pixels. Same semantics as
/// decode_png. Used by the iTerm2 OSC 1337 File= synth path alongside PNG and
/// JPEG, and for still GIFs when decode_gif_frames is unavailable.
pub var decode_gif: ?DecodeGifFn = gif: {
    if (build_options.artifact == .lib) break :gif null;
    break :gif &decodeGifWuffs;
};

/// Decode every frame of a GIF. When this is available a multi-frame GIF
/// becomes an animation; when it is null the caller falls back to decode_gif
/// and shows the first frame only.
///
/// This is separate from decode_gif rather than replacing it because the two
/// answer different questions, and because decode_gif is what the C API
/// exposes to embedders. An embedder that only supplies a still decoder keeps
/// working unchanged.
pub var decode_gif_frames: ?DecodeGifFramesFn = gif: {
    if (build_options.artifact == .lib) break :gif null;
    break :gif &decodeGifFramesWuffs;
};

pub const DecodeError = Allocator.Error || error{InvalidData};
pub const DecodePngFn = *const fn (Allocator, []const u8) DecodeError!Image;
pub const DecodeJpegFn = *const fn (Allocator, []const u8) DecodeError!Image;
pub const DecodeGifFn = *const fn (Allocator, []const u8) DecodeError!Image;
pub const DecodeGifFramesFn = *const fn (Allocator, []const u8) DecodeError!Animation;

/// The result of decoding an image. The caller owns the returned data
/// and must free it with the same allocator that was passed to the
/// decode function.
pub const Image = struct {
    width: u32,
    height: u32,
    data: []u8,
};

/// The result of decoding an animated image. Every frame is composed to the
/// full canvas, so frames are independent of one another. The caller owns
/// every buffer and the slice, and must free them with the same allocator
/// that was passed to the decode function.
pub const Animation = struct {
    width: u32,
    height: u32,
    frames: []Frame,

    /// Total plays, zero meaning forever.
    loop_count: u32,

    /// The delay of the frame that preceded `frames`, in milliseconds.
    ///
    /// Decoders leave this zero because they return every frame. It is filled
    /// in by a caller that peels the first frame off to use as the image
    /// itself, which would otherwise lose that frame's timing.
    root_delay_ms: u32 = 0,

    /// zero_delay_frames of the frame that preceded `frames`, filled in by
    /// the same caller and for the same reason.
    root_zero_delay_frames: u32 = 0,

    pub const Frame = struct {
        data: []u8,

        /// The delay the file declares, in milliseconds. A declared zero is
        /// passed through as zero; substituting a default is the caller's
        /// policy decision.
        delay_ms: u32,

        /// How many source frames this one stands in for declared no delay.
        ///
        /// A decoder that drops frames folds their delays into the frame that
        /// replaces them, and zeros fold into zero, so a caller substituting
        /// a default gap needs a count rather than a flag. Decoders that
        /// return every frame may leave this zero.
        zero_delay_frames: u32 = 0,
    };

    pub fn deinit(self: *Animation, alloc: Allocator) void {
        for (self.frames) |frame| alloc.free(frame.data);
        alloc.free(self.frames);
        self.* = undefined;
    }
};

fn decodePngWuffs(
    alloc: Allocator,
    data: []const u8,
) DecodeError!Image {
    const wuffs = @import("wuffs");
    const result = wuffs.png.decode(
        alloc,
        data,
    ) catch |err| switch (err) {
        error.WuffsError => return error.InvalidData,
        error.OutOfMemory => return error.OutOfMemory,
        error.Overflow => return error.InvalidData,
    };

    return .{
        .width = result.width,
        .height = result.height,
        .data = result.data,
    };
}

fn decodeJpegWuffs(
    alloc: Allocator,
    data: []const u8,
) DecodeError!Image {
    const wuffs = @import("wuffs");
    const result = wuffs.jpeg.decode(
        alloc,
        data,
    ) catch |err| switch (err) {
        error.WuffsError => return error.InvalidData,
        error.OutOfMemory => return error.OutOfMemory,
        error.Overflow => return error.InvalidData,
    };

    return .{
        .width = result.width,
        .height = result.height,
        .data = result.data,
    };
}

fn decodeGifWuffs(
    alloc: Allocator,
    data: []const u8,
) DecodeError!Image {
    const wuffs = @import("wuffs");
    const result = wuffs.gif.decode(
        alloc,
        data,
    ) catch |err| switch (err) {
        error.WuffsError => return error.InvalidData,
        error.OutOfMemory => return error.OutOfMemory,
        error.Overflow => return error.InvalidData,
    };

    return .{
        .width = result.width,
        .height = result.height,
        .data = result.data,
    };
}

fn decodeGifFramesWuffs(
    alloc: Allocator,
    data: []const u8,
) DecodeError!Animation {
    const wuffs = @import("wuffs");
    var result = wuffs.gif.decodeAnimated(
        alloc,
        data,
    ) catch |err| switch (err) {
        error.WuffsError => return error.InvalidData,
        error.OutOfMemory => return error.OutOfMemory,
        error.Overflow => return error.InvalidData,
    };

    // The two Frame types are identical in layout but belong to different
    // packages, and the terminal package does not depend on wuffs. Move the
    // buffers across rather than copying them.
    //
    // Freeing explicitly rather than with errdefer: this allocation runs
    // right after the decoder committed every frame, so it is the likeliest
    // point to fail and the most expensive one to leak.
    const frames = alloc.alloc(Animation.Frame, result.frames.len) catch |err| {
        result.deinit(alloc);
        return err;
    };
    for (result.frames, frames) |src, *dst| dst.* = .{
        .data = src.data,
        .delay_ms = src.delay_ms,
        .zero_delay_frames = src.zero_delay_frames,
    };

    // The buffers now belong to `frames`, so release only the outer slice.
    alloc.free(result.frames);

    return .{
        .width = result.width,
        .height = result.height,
        .frames = frames,
        .loop_count = result.loop_count,
    };
}
