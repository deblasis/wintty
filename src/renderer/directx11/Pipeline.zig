//! Generic render pipeline wrapper for DX11.
//!
//! Wraps an optional vertex shader + pixel shader + input layout triple,
//! analogous to Metal's Pipeline.zig which wraps MTLRenderPipelineState.
//!
//! When all fields are null, the pipeline is "empty" (no shaders loaded).
//! GenericRenderer skips draw calls for empty pipelines via the has_shaders
//! check. This lets initShaders return default-initialized pipelines before
//! HLSL shaders are written.
const std = @import("std");
const com = @import("com.zig");
const d3d11 = @import("d3d11.zig");

const log = std.log.scoped(.directx11);

/// Options for initializing a render pipeline.
pub const Options = struct {
    device: *d3d11.ID3D11Device,
    vs_bytecode: ?[]const u8 = null,
    ps_bytecode: ?[]const u8 = null,
    input_elements: ?[]const d3d11.D3D11_INPUT_ELEMENT_DESC = null,
    /// Stride in bytes for the per-instance vertex buffer.
    /// 0 for pipelines with no vertex/instance data (full-screen triangle).
    instance_stride: u32 = 0,
};

pub const InitError = error{
    VertexShaderFailed,
    PixelShaderFailed,
    InputLayoutFailed,
};

vertex_shader: ?*d3d11.ID3D11VertexShader = null,
pixel_shader: ?*d3d11.ID3D11PixelShader = null,
input_layout: ?*d3d11.ID3D11InputLayout = null,
/// Stride for IASetVertexBuffers, set at init from the input layout.
instance_stride: u32 = 0,

pub fn init(opts: Options) InitError!@This() {
    var result: @This() = .{
        .instance_stride = opts.instance_stride,
    };
    errdefer result.deinit();

    // Create vertex shader.
    if (opts.vs_bytecode) |vs_bc| {
        var vs: ?*d3d11.ID3D11VertexShader = null;
        const hr = opts.device.CreateVertexShader(vs_bc.ptr, vs_bc.len, null, &vs);
        if (com.FAILED(hr) or vs == null) {
            log.err("CreateVertexShader failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
            return InitError.VertexShaderFailed;
        }
        result.vertex_shader = vs.?;
    }

    // Create pixel shader.
    if (opts.ps_bytecode) |ps_bc| {
        var ps: ?*d3d11.ID3D11PixelShader = null;
        const hr = opts.device.CreatePixelShader(ps_bc.ptr, ps_bc.len, null, &ps);
        if (com.FAILED(hr) or ps == null) {
            log.err("CreatePixelShader failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
            return InitError.PixelShaderFailed;
        }
        result.pixel_shader = ps.?;
    }

    // Create input layout (only if input elements and VS bytecode are both provided).
    if (opts.input_elements) |elements| {
        if (opts.vs_bytecode) |vs_bc| {
            var layout: ?*d3d11.ID3D11InputLayout = null;
            const hr = opts.device.CreateInputLayout(
                elements.ptr,
                @intCast(elements.len),
                vs_bc.ptr,
                vs_bc.len,
                &layout,
            );
            if (com.FAILED(hr) or layout == null) {
                log.err("CreateInputLayout failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
                return InitError.InputLayoutFailed;
            }
            result.input_layout = layout.?;
        }
    }

    return result;
}

pub fn deinit(self: *const @This()) void {
    if (self.input_layout) |layout| _ = layout.Release();
    if (self.pixel_shader) |ps| _ = ps.Release();
    if (self.vertex_shader) |vs| _ = vs.Release();
}
