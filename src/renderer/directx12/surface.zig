//! Surface types for DX12 renderer.
//!
//! The renderer supports three surface modes at the library level:
//! - HWND: for standalone windows and test harnesses
//! - SwapChainPanel: for WinUI 3 / XAML composition hosts
//! - SharedTexture: for game engines and offscreen rendering
const std = @import("std");
const dxgi = @import("dxgi.zig");

pub const HWND = dxgi.HWND;

pub const Surface = union(enum) {
    hwnd: HWND,
    swap_chain_panel: *dxgi.ISwapChainPanelNative,
    shared_texture: SharedTextureConfig,
};

pub const SharedTextureConfig = struct {
    /// Output parameter: the shared handle will be written here after
    /// device creation. The caller owns the storage and must keep it
    /// alive until init returns.
    handle_out: *?std.os.windows.HANDLE,
    width: u32,
    height: u32,
};
