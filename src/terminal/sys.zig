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

/// Decode the first frame of a GIF into RGBA pixels. Multi-frame GIFs are
/// rendered as a single still image; animation requires a separate design
/// pass. Used by the iTerm2 OSC 1337 File= synth path alongside PNG and JPEG.
pub var decode_gif: ?DecodeGifFn = gif: {
    if (build_options.artifact == .lib) break :gif null;
    break :gif &decodeGifWuffs;
};

pub const DecodeError = Allocator.Error || error{InvalidData};
pub const DecodePngFn = *const fn (Allocator, []const u8) DecodeError!Image;
pub const DecodeJpegFn = *const fn (Allocator, []const u8) DecodeError!Image;
pub const DecodeGifFn = *const fn (Allocator, []const u8) DecodeError!Image;

/// The result of decoding an image. The caller owns the returned data
/// and must free it with the same allocator that was passed to the
/// decode function.
pub const Image = struct {
    width: u32,
    height: u32,
    data: []u8,
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
