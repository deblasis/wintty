//! DX12 render target stub.
//!
//! Will be replaced with a real implementation wrapping D3D12 render
//! targets (RTV descriptors pointing at swap chain or offscreen buffers).

width: usize = 0,
height: usize = 0,

pub fn deinit(self: *@This()) void {
    _ = self;
}
