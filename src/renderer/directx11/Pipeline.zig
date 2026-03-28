//! Generic render pipeline wrapper for DX11.
//!
//! Wraps a vertex + pixel shader pair, analogous to Metal's Pipeline.zig
//! which wraps MTLRenderPipelineState. Distinct from cell_pipeline.zig (the
//! concrete cell pipeline from branch 025).
//!
//! TODO: Implement with ID3D11VertexShader + ID3D11PixelShader.

/// Options for initializing a render pipeline.
pub const Options = struct {};

pub fn deinit(self: *const @This()) void {
    _ = self;
    @panic("TODO: DX11 Pipeline.deinit");
}
