//! Generic render pipeline wrapper for DX11.
//!
//! Wraps an optional vertex shader + pixel shader + input layout triple,
//! analogous to Metal's Pipeline.zig which wraps MTLRenderPipelineState.
//!
//! When all fields are null, the pipeline is "empty" (no shaders loaded).
//! GenericRenderer skips draw calls for empty pipelines via the has_shaders
//! check. This lets initShaders return default-initialized pipelines before
//! HLSL shaders are written.
const d3d11 = @import("d3d11.zig");

/// Options for initializing a render pipeline.
pub const Options = struct {
    device: *d3d11.ID3D11Device,
    vs_bytecode: ?[]const u8 = null,
    ps_bytecode: ?[]const u8 = null,
};

vertex_shader: ?*d3d11.ID3D11VertexShader = null,
pixel_shader: ?*d3d11.ID3D11PixelShader = null,
input_layout: ?*d3d11.ID3D11InputLayout = null,

pub fn deinit(self: *const @This()) void {
    if (self.input_layout) |layout| _ = layout.Release();
    if (self.pixel_shader) |ps| _ = ps.Release();
    if (self.vertex_shader) |vs| _ = vs.Release();
}
