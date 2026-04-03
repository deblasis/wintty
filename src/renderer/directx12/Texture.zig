//! DX12 GPU texture stub.
//!
//! Will be replaced with a real implementation wrapping an
//! ID3D12Resource (committed texture) and SRV descriptor.

pub const Options = struct {};

pub const Error = error{
    TextureCreateFailed,
};

/// Width of this texture in pixels.
width: usize = 0,

pub fn init(opts: Options, width: usize, height: usize, data: ?[]const u8) Error!@This() {
    _ = opts;
    _ = height;
    _ = data;
    return .{ .width = width };
}

pub fn deinit(self: @This()) void {
    _ = self;
}

/// Upload pixel data to a sub-region of this texture.
pub fn replaceRegion(self: *@This(), x: usize, y: usize, width: usize, height: usize, data: []const u8) error{}!void {
    _ = self;
    _ = x;
    _ = y;
    _ = width;
    _ = height;
    _ = data;
}
