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
    const ctx = context orelse return .{ .context = null, .device = device };

    // Bind the first attachment's render target and optionally clear it.
    // GenericRenderer always passes exactly one attachment per render pass.
    if (opts.attachments.len == 0) return .{ .context = context, .device = device };

    const att = opts.attachments[0];
    const target, const rtv = switch (att.target) {
        .target => |t| .{ t, t.rtv orelse {
            log.warn("render pass attachment has no RTV, skipping bind", .{});
            return .{ .context = context, .device = device };
        } },
        // Texture-as-RTV not yet supported (needs CreateRenderTargetView
        // from the texture's ID3D11Texture2D).
        .texture => {
            log.warn("texture attachments not yet supported in DX11 render pass", .{});
            return .{ .context = context, .device = device };
        },
    };

    // Set viewport to target dimensions.
    const viewport = d3d11.D3D11_VIEWPORT{
        .TopLeftX = 0.0,
        .TopLeftY = 0.0,
        .Width = @floatFromInt(target.width),
        .Height = @floatFromInt(target.height),
        .MinDepth = 0.0,
        .MaxDepth = 1.0,
    };
    ctx.RSSetViewports(&.{viewport});

    // Bind the render target.
    ctx.OMSetRenderTargets(&.{rtv}, null);

    // Clear if requested.
    if (att.clear_color) |color| {
        ctx.ClearRenderTargetView(rtv, &.{
            @floatCast(color[0]),
            @floatCast(color[1]),
            @floatCast(color[2]),
            @floatCast(color[3]),
        });
    }

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

    // Bind buffers[0] as vertex buffer, buffers[1..] as SRVs.
    // Mirrors OpenGL which binds buffers[0] as vertex data and
    // buffers[1..] as storage buffers (SSBOs). In DX11, the SSBO
    // equivalent is a StructuredBuffer bound via an SRV.
    if (s.buffers.len > 0) {
        if (s.buffers[0]) |buf| {
            ctx.IASetVertexBuffers(
                0,
                &.{@as(?*d3d11.ID3D11Buffer, buf.ptr)},
                &.{s.pipeline.instance_stride},
                &.{@as(u32, 0)},
            );
        }
    }

    // Bind uniforms as constant buffer at slot 0 for both VS and PS.
    // Why: DX11 constant buffers are a separate namespace from vertex
    // buffers, so slot 0 here doesn't conflict with IASetVertexBuffers
    // slot 0 above. Metal uses buffer index 1 for uniforms instead.
    if (s.uniforms) |buf| {
        ctx.VSSetConstantBuffers(0, &.{@as(?*d3d11.ID3D11Buffer, buf.ptr)});
        ctx.PSSetConstantBuffers(0, &.{@as(?*d3d11.ID3D11Buffer, buf.ptr)});
    }

    // Bind textures as shader resource views for both VS and PS.
    // Textures occupy t-register slots 0..N-1.
    for (s.textures, 0..) |tex_opt, i| {
        if (tex_opt) |tex| {
            const srv = @as(?*d3d11.ID3D11ShaderResourceView, tex.srv);
            ctx.VSSetShaderResources(@intCast(i), &.{srv});
            ctx.PSSetShaderResources(@intCast(i), &.{srv});
        }
    }

    // Bind structured buffers (buffers[1..]) as SRVs, continuing
    // after the texture slots. HLSL registers are:
    //   bg_color/cell_bg: cell_bg_colors at t0 (no textures)
    //   cell_text: atlases at t0,t1, cell_bg_colors at t2
    if (s.buffers.len > 1) {
        for (s.buffers[1..], 0..) |buf_opt, i| {
            if (buf_opt) |buf| {
                if (buf.srv) |srv| {
                    const slot: u32 = @intCast(s.textures.len + i);
                    ctx.VSSetShaderResources(slot, &.{@as(?*d3d11.ID3D11ShaderResourceView, srv)});
                    ctx.PSSetShaderResources(slot, &.{@as(?*d3d11.ID3D11ShaderResourceView, srv)});
                }
            }
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
