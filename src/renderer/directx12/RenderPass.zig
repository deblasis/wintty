//! DX12 render pass -- records draw commands into an
//! ID3D12GraphicsCommandList for a single render pass.
//!
//! Follows the same begin/step/complete pattern as Metal and OpenGL.
//! begin() transitions the target to RENDER_TARGET, sets viewport and
//! scissor, and optionally clears. step() binds pipeline state and
//! issues draw calls. complete() transitions the target back to PRESENT.
const RenderPass = @This();

const d3d12 = @import("d3d12.zig");
const ResourceStates = d3d12.D3D12_RESOURCE_STATES;

const Pipeline = @import("Pipeline.zig");
const Sampler = @import("Sampler.zig");
const Target = @import("Target.zig");
const Texture = @import("Texture.zig");
const bufferpkg = @import("buffer.zig");
const RawBuffer = bufferpkg.RawBuffer;

/// Options for beginning a render pass.
pub const Options = struct {
    /// The command list to record into.
    command_list: *d3d12.ID3D12GraphicsCommandList,
    /// Color attachments for this render pass.
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
    pipeline: Pipeline = .{},
    uniforms: ?RawBuffer = null,
    buffers: []const ?RawBuffer = &.{},
    textures: []const ?Texture = &.{},
    samplers: []const ?Sampler = &.{},
    draw: Draw = .{},

    pub const Draw = struct {
        type: DrawType = .triangle,
        vertex_count: usize = 0,
        instance_count: usize = 1,
    };

    pub const DrawType = enum {
        triangle,
        triangle_strip,
    };
};

command_list: ?*d3d12.ID3D12GraphicsCommandList,
attachments: []const Options.Attachment,
step_number: usize,

pub fn begin(opts: Options) RenderPass {
    const cl = opts.command_list;

    // Collect all RTV handles so we can set them with a single
    // OMSetRenderTargets call (per-attachment calls would silently
    // overwrite, leaving only the last target bound).
    const max_rtvs = 8; // D3D12_SIMULTANEOUS_RENDER_TARGET_COUNT
    var rtv_handles: [max_rtvs]d3d12.D3D12_CPU_DESCRIPTOR_HANDLE = undefined;
    var rtv_count: u32 = 0;

    // Track viewport dimensions from the first valid target.
    var vp_width: usize = 0;
    var vp_height: usize = 0;

    for (opts.attachments) |*at| {
        switch (at.target) {
            .target => |*t| {
                // Skip if this target has no GPU resource yet (stub).
                if (t.resource == null) continue;

                // Transition PRESENT -> RENDER_TARGET.
                t.transitionBarrier(
                    cl,
                    ResourceStates.PRESENT,
                    ResourceStates.RENDER_TARGET,
                );

                // Collect RTV handle.
                if (rtv_count < rtv_handles.len) {
                    rtv_handles[rtv_count] = t.rtv_handle;
                    rtv_count += 1;
                }

                // Use the first valid target for viewport dimensions.
                if (rtv_count == 1) {
                    vp_width = t.width;
                    vp_height = t.height;
                }

                // Clear if requested.
                if (at.clear_color) |c| {
                    const color = [4]f32{
                        @floatCast(c[0]),
                        @floatCast(c[1]),
                        @floatCast(c[2]),
                        @floatCast(c[3]),
                    };
                    cl.ClearRenderTargetView(t.rtv_handle, &color, 0, null);
                }
            },
            .texture => {
                // Texture targets will be handled when Texture.zig gets
                // a real GPU resource implementation.
            },
        }
    }

    if (rtv_count > 0) {
        // Bind all render targets at once.
        cl.OMSetRenderTargets(
            rtv_count,
            &rtv_handles,
            0, // FALSE -- handles are individual, not contiguous
            null,
        );

        // Set viewport and scissor once from the first target.
        const viewport = d3d12.D3D12_VIEWPORT{
            .TopLeftX = 0,
            .TopLeftY = 0,
            .Width = @floatFromInt(vp_width),
            .Height = @floatFromInt(vp_height),
            .MinDepth = 0.0,
            .MaxDepth = 1.0,
        };
        cl.RSSetViewports(1, @ptrCast(&viewport));

        const scissor = d3d12.D3D12_RECT{
            .left = 0,
            .top = 0,
            .right = @intCast(vp_width),
            .bottom = @intCast(vp_height),
        };
        cl.RSSetScissorRects(1, @ptrCast(&scissor));
    }

    return .{
        .command_list = cl,
        .attachments = opts.attachments,
        .step_number = 0,
    };
}

/// Add a step to this render pass.
/// No-op if the render pass has no command list (stub path).
pub fn step(self: *RenderPass, s: Step) void {
    if (self.command_list == null) return;
    if (s.draw.instance_count == 0) return;

    // Pipeline, buffer bindings, texture/sampler bindings will be
    // wired when Pipeline.zig, buffer.zig, Texture.zig, and
    // Sampler.zig get their real implementations.

    self.step_number += 1;
}

/// Complete the render pass. Transitions targets back to PRESENT.
/// No-op if the render pass has no command list (stub path).
pub fn complete(self: *const RenderPass) void {
    const cl = self.command_list orelse return;
    for (self.attachments) |*at| {
        switch (at.target) {
            .target => |*t| {
                if (t.resource == null) continue;
                t.transitionBarrier(
                    cl,
                    ResourceStates.RENDER_TARGET,
                    ResourceStates.PRESENT,
                );
            },
            .texture => {},
        }
    }
}

// --- Tests ---

const std = @import("std");

test "RenderPass struct fields" {
    try std.testing.expect(@hasField(RenderPass, "command_list"));
    try std.testing.expect(@hasField(RenderPass, "attachments"));
    try std.testing.expect(@hasField(RenderPass, "step_number"));
}

test "RenderPass has required methods" {
    try std.testing.expect(@TypeOf(RenderPass.begin) != void);
    try std.testing.expect(@TypeOf(RenderPass.step) != void);
    try std.testing.expect(@TypeOf(RenderPass.complete) != void);
}

test "Step DrawType values" {
    try std.testing.expectEqual(@as(u1, 0), @intFromEnum(Step.DrawType.triangle));
    try std.testing.expectEqual(@as(u1, 1), @intFromEnum(Step.DrawType.triangle_strip));
}
