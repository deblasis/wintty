//! Render pass for DX11 draw steps.
//!
//! Stores the device context and issues immediate-mode draw commands.
//! Unlike Metal (which records into a command buffer), DX11 commands
//! execute immediately when called on the device context.
const std = @import("std");
const d3d11 = @import("d3d11.zig");
const Pipeline = @import("Pipeline.zig");
const Sampler = @import("Sampler.zig");
const Target = @import("Target.zig");
const Texture = @import("Texture.zig");
const bufferpkg = @import("buffer.zig");
const RawBuffer = bufferpkg.RawBuffer;

const log = std.log.scoped(.directx11);

/// Options for beginning a render pass.
pub const Options = struct {
    attachments: []const Attachment,

    pub const Attachment = struct {
        target: union(enum) {
            texture: Texture,
            target: Target,
        },
        clear_color: ?[4]f64 = null,
    };
};

/// A single step in a render pass.
pub const Step = struct {
    pipeline: Pipeline,
    uniforms: ?RawBuffer = null,
    buffers: []const ?RawBuffer = &.{},
    textures: []const ?Texture = &.{},
    samplers: []const ?Sampler = &.{},
    draw: Draw,

    pub const Draw = struct {
        type: DrawType,
        vertex_count: usize,
        instance_count: usize = 1,
    };

    pub const DrawType = enum {
        triangle,
        triangle_strip,
    };
};

/// The device context for issuing draw commands.
context: ?*d3d11.ID3D11DeviceContext,

/// The device for creating render target views from textures.
device: ?*d3d11.ID3D11Device,

pub fn begin(
    context: ?*d3d11.ID3D11DeviceContext,
    device: ?*d3d11.ID3D11Device,
    opts: Options,
) @This() {
    // Clearing and render target binding is handled by Frame.renderPass()
    // via device.clearRenderTarget(), which sets the viewport, binds the
    // swap chain's RTV, and clears it. Per-attachment RTV creation from
    // Texture/Target is a future task (Target doesn't hold an
    // ID3D11Texture2D yet).
    _ = opts;

    return .{ .context = context, .device = device };
}

pub fn step(self: *@This(), s: Step) void {
    const ctx = self.context orelse return;

    // Skip if the pipeline has no shaders loaded yet.
    if (s.pipeline.vertex_shader == null and s.pipeline.pixel_shader == null) return;

    // Skip zero-instance draws.
    if (s.draw.instance_count == 0) return;

    // Bind pipeline state: shaders and input layout.
    ctx.VSSetShader(s.pipeline.vertex_shader);
    ctx.PSSetShader(s.pipeline.pixel_shader);
    if (s.pipeline.input_layout) |layout| {
        ctx.IASetInputLayout(layout);
    }

    // Set primitive topology.
    ctx.IASetPrimitiveTopology(switch (s.draw.type) {
        .triangle => .TRIANGLELIST,
        .triangle_strip => .TRIANGLESTRIP,
    });

    // Bind vertex buffers.
    // Why: Metal reserves slot 0 for vertex data and slot 1 for uniforms,
    // starting additional buffers at slot 2. DX11 doesn't need that
    // workaround -- uniforms go through constant buffers (a separate
    // binding point), so vertex buffers bind at their natural index.
    for (s.buffers, 0..) |buf_opt, i| {
        if (buf_opt) |buf| {
            // TODO: stride must come from Pipeline's input layout once
            // HLSL shaders define their vertex formats. Stride 0 for now
            // lets shaders use SV_VertexID for procedural geometry.
            ctx.IASetVertexBuffers(
                @intCast(i),
                &.{@as(?*d3d11.ID3D11Buffer, buf)},
                &.{@as(u32, 0)},
                &.{@as(u32, 0)},
            );
        }
    }

    // Bind uniforms as constant buffer at slot 0 for both VS and PS.
    // Why: DX11 constant buffers are a separate namespace from vertex
    // buffers, so slot 0 here doesn't conflict with IASetVertexBuffers
    // slot 0 above. Metal uses buffer index 1 for uniforms instead.
    if (s.uniforms) |buf| {
        ctx.VSSetConstantBuffers(0, &.{@as(?*d3d11.ID3D11Buffer, buf)});
        ctx.PSSetConstantBuffers(0, &.{@as(?*d3d11.ID3D11Buffer, buf)});
    }

    // Bind textures as shader resource views for both VS and PS.
    for (s.textures, 0..) |tex_opt, i| {
        if (tex_opt) |tex| {
            const srv = @as(?*d3d11.ID3D11ShaderResourceView, tex.srv);
            ctx.VSSetShaderResources(@intCast(i), &.{srv});
            ctx.PSSetShaderResources(@intCast(i), &.{srv});
        }
    }

    // Bind samplers for the pixel shader.
    for (s.samplers, 0..) |samp_opt, i| {
        if (samp_opt) |samp| {
            ctx.PSSetSamplers(@intCast(i), &.{@as(?*d3d11.ID3D11SamplerState, samp.sampler)});
        }
    }

    // Draw.
    ctx.DrawInstanced(
        @intCast(s.draw.vertex_count),
        @intCast(s.draw.instance_count),
        0,
        0,
    );
}

pub fn complete(self: *const @This()) void {
    // DX11 immediate mode: commands already executed on the context.
    // Nothing to finalize (unlike Metal which calls endEncoding).
    _ = self;
}
