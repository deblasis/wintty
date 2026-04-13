//! Surface types for DX12 renderer.
//!
//! The renderer supports four surface modes at the library level:
//! - HWND: for standalone windows and test harnesses
//! - SwapChainPanel: for WinUI 3 / XAML composition hosts (opaque)
//! - Composition: swap chain created but not bound; embedder retrieves
//!   the pointer and binds it to a Windows.UI.Composition visual for
//!   per-pixel alpha transparency
//! - SharedTexture: for game engines and offscreen rendering
const dxgi = @import("dxgi.zig");

pub const HWND = dxgi.HWND;

pub const Surface = union(enum) {
    hwnd: HWND,
    swap_chain_panel: *dxgi.ISwapChainPanelNative,
    composition: void,
    shared_texture: SharedTextureConfig,
};

pub const SharedTextureConfig = struct {
    /// Initial pixel width of the shared render target.
    width: u32,
    /// Initial pixel height of the shared render target.
    height: u32,
};
