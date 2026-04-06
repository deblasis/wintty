//! DX12 render target -- wraps a swap chain back buffer (or offscreen
//! resource) with its RTV descriptor handle.
//!
//! Barrier transitions are handled by RenderPass, which knows the
//! expected state cycle (PRESENT -> RENDER_TARGET -> PRESENT).
const Target = @This();

const d3d12 = @import("d3d12.zig");

/// The underlying GPU resource. This is a BORROWED reference -- it points
/// at one of the API's `back_buffers[*]` slots and is rebound by every
/// `beginFrame`. Target does NOT AddRef on assign and MUST NOT Release on
/// deinit, otherwise after a swap chain ResizeBuffers (which releases the
/// back buffers and re-acquires them) the old pointer here is dangling and
/// Release on it is a use-after-free.
///
/// Null until `beginFrame` wires it up.
resource: ?*d3d12.ID3D12Resource = null,

/// CPU descriptor handle for the render target view.
/// Zero-initialized until device wiring is done.
rtv_handle: d3d12.D3D12_CPU_DESCRIPTOR_HANDLE = .{ .ptr = 0 },

/// Width of this target in pixels.
width: usize = 0,

/// Height of this target in pixels.
height: usize = 0,

pub fn deinit(self: *Target) void {
    // resource is a borrowed reference (see field doc) -- do NOT Release.
    self.* = undefined;
}

/// Record a transition barrier on the given command list.
/// No-op if resource is null (stub target without a GPU resource).
pub fn transitionBarrier(
    self: *const Target,
    command_list: *d3d12.ID3D12GraphicsCommandList,
    state_before: d3d12.D3D12_RESOURCE_STATES,
    state_after: d3d12.D3D12_RESOURCE_STATES,
) void {
    const res = self.resource orelse return;
    const barrier = d3d12.D3D12_RESOURCE_BARRIER{
        .Type = .TRANSITION,
        .Flags = .NONE,
        .u = .{
            .Transition = .{
                .pResource = res,
                .Subresource = 0xFFFFFFFF, // D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES
                .StateBefore = state_before,
                .StateAfter = state_after,
            },
        },
    };
    command_list.ResourceBarrier(1, @ptrCast(&barrier));
}

// --- Tests ---

const std = @import("std");

test "Target struct fields" {
    try std.testing.expect(@hasField(Target, "resource"));
    try std.testing.expect(@hasField(Target, "rtv_handle"));
    try std.testing.expect(@hasField(Target, "width"));
    try std.testing.expect(@hasField(Target, "height"));
}

test "Target has required methods" {
    try std.testing.expect(@TypeOf(Target.deinit) != void);
    try std.testing.expect(@TypeOf(Target.transitionBarrier) != void);
}
