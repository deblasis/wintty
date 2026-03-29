//! Render target for DX11.
//!
//! Wraps an ID3D11RenderTargetView that draw commands render into.
//! For swap chain targets the RTV is owned by Device -- Target borrows
//! the pointer. For future off-screen targets, Target will own the
//! RTV and its backing ID3D11Texture2D.
const d3d11 = @import("d3d11.zig");

/// The render target view to draw into.
/// Null when running without a device (non-Windows builds).
rtv: ?*d3d11.ID3D11RenderTargetView = null,

/// Current width of this target in pixels.
width: usize = 0,
/// Current height of this target in pixels.
height: usize = 0,

pub fn deinit(self: *@This()) void {
    // Swap chain RTV is owned by Device, not by Target.
    // When we add off-screen targets we will Release() here.
    self.rtv = null;
    self.width = 0;
    self.height = 0;
}
