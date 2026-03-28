//! GPU texture wrapper for DX11.
//! TODO: Implement with ID3D11Texture2D and ID3D11ShaderResourceView.

/// Options for initializing a texture.
pub const Options = struct {};

pub const Error = error{
    /// A DirectX 11 API call failed.
    DirectXFailed,
};

/// The width of this texture.
width: usize = 0,
/// The height of this texture.
height: usize = 0,
/// Bytes per pixel for this texture.
bpp: usize = 0,

pub fn init(
    opts: Options,
    width: usize,
    height: usize,
    data: ?[]const u8,
) Error!@This() {
    _ = opts;
    _ = width;
    _ = height;
    _ = data;
    @panic("TODO: DX11 Texture.init");
}

pub fn deinit(self: @This()) void {
    _ = self;
    @panic("TODO: DX11 Texture.deinit");
}

/// Replace a region of the texture with new data.
pub fn replaceRegion(
    self: @This(),
    x: usize,
    y: usize,
    width: usize,
    height: usize,
    data: []const u8,
) error{}!void {
    _ = self;
    _ = x;
    _ = y;
    _ = width;
    _ = height;
    _ = data;
    @panic("TODO: DX11 Texture.replaceRegion");
}
