//! Texture sampler wrapper for DX11.
//! TODO: Implement with ID3D11SamplerState.

/// Options for initializing a sampler.
pub const Options = struct {};

pub const Error = error{
    /// A DirectX 11 API call failed.
    DirectXFailed,
};

pub fn init(opts: Options) Error!@This() {
    _ = opts;
    @panic("TODO: DX11 Sampler.init");
}

pub fn deinit(self: @This()) void {
    _ = self;
    @panic("TODO: DX11 Sampler.deinit");
}
