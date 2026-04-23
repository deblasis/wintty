//! DX12 per-frame command recording context.
//!
//! Each in-flight frame (triple buffered) owns a command allocator and
//! a graphics command list. The allocator backs the command list's memory
//! and must not be reset until the GPU has finished executing its commands.
//!
//! Lifecycle per frame:
//!   1. Wait for this frame's fence value (GPU done with previous use)
//!   2. reset() -- resets allocator + command list for new recording
//!   3. renderPass() -- returns a RenderPass for draw command recording
//!   4. complete() -- closes the command list, reports health
const Frame = @This();

const std = @import("std");

const com = @import("com.zig");
const d3d12 = @import("d3d12.zig");

const DirectX12 = @import("../DirectX12.zig");
const Renderer = @import("../generic.zig").Renderer(DirectX12);
const RenderPass = @import("RenderPass.zig");
const Target = @import("Target.zig");
const Health = @import("../../renderer.zig").Health;

const HRESULT = com.HRESULT;
const FAILED = com.FAILED;

const log = std.log.scoped(.directx12);

// --- Helpers ---

/// Format an HRESULT as a u32 for hex logging.
fn hrFmt(hr: HRESULT) u32 {
    return @as(u32, @bitCast(hr));
}

// --- State ---

command_allocator: ?*d3d12.ID3D12CommandAllocator,
command_list: ?*d3d12.ID3D12GraphicsCommandList,
/// Fence value for GPU synchronization.
/// Written by drawFrameEnd after submitting this frame's command list.
/// Read by beginFrame to wait for the GPU before reusing the frame.
fence_value: u64,

renderer: *Renderer,
target: *Target,

// --- Creation / teardown ---

pub fn init(device: *d3d12.ID3D12Device) !Frame {
    // -- Command allocator --
    var allocator: ?*d3d12.ID3D12CommandAllocator = null;
    const alloc_hr = device.CreateCommandAllocator(
        .DIRECT,
        &d3d12.ID3D12CommandAllocator.IID,
        @ptrCast(&allocator),
    );
    if (FAILED(alloc_hr)) {
        log.err("CreateCommandAllocator failed: 0x{x}", .{hrFmt(alloc_hr)});
        return error.CommandAllocatorCreationFailed;
    }
    errdefer _ = allocator.?.Release();

    // -- Graphics command list --
    // Created in a closed state so the first reset() opens it cleanly.
    var command_list: ?*d3d12.ID3D12GraphicsCommandList = null;
    const list_hr = device.CreateCommandList(
        0,
        .DIRECT,
        allocator.?,
        null,
        &d3d12.ID3D12GraphicsCommandList.IID,
        @ptrCast(&command_list),
    );
    if (FAILED(list_hr)) {
        log.err("CreateCommandList failed: 0x{x}", .{hrFmt(list_hr)});
        return error.CommandListCreationFailed;
    }
    errdefer _ = command_list.?.Release();

    // Close immediately -- reset() will reopen when the frame is first used.
    const close_hr = command_list.?.Close();
    if (FAILED(close_hr)) {
        log.err("initial command list Close failed: 0x{x}", .{hrFmt(close_hr)});
        return error.CommandListCloseFailed;
    }

    return .{
        .command_allocator = allocator.?,
        .command_list = command_list.?,
        .fence_value = 0,
        .renderer = undefined,
        .target = undefined,
    };
}

pub fn deinit(self: *Frame) void {
    // Best-effort close in case the command list is still open.
    if (self.command_list) |cl| {
        _ = cl.Close();
        _ = cl.Release();
    }
    if (self.command_allocator) |ca| {
        _ = ca.Release();
    }

    self.* = undefined;
}

// --- Per-frame operations ---

/// Reset the allocator and command list for a new frame.
/// The caller must ensure the GPU has finished with this frame's
/// previous commands (by waiting on fence_value) before calling.
pub fn reset(self: *Frame) !void {
    const allocator = self.command_allocator orelse return error.FrameNotInitialized;
    const command_list = self.command_list orelse return error.FrameNotInitialized;

    const alloc_hr = allocator.Reset();
    if (FAILED(alloc_hr)) {
        log.err("ID3D12CommandAllocator.Reset failed: 0x{x}", .{hrFmt(alloc_hr)});
        return error.CommandAllocatorResetFailed;
    }

    const list_hr = command_list.Reset(allocator, null);
    if (FAILED(list_hr)) {
        log.err("ID3D12GraphicsCommandList.Reset failed: 0x{x}", .{hrFmt(list_hr)});
        return error.CommandListResetFailed;
    }
}

/// Begin a render pass for this frame. The returned RenderPass is used
/// by GenericRenderer to issue draw commands.
pub fn renderPass(
    self: *Frame,
    attachments: []const RenderPass.Options.Attachment,
) RenderPass {
    const cl = self.command_list orelse {
        // Frame not initialized (stub path) -- return a no-op RenderPass.
        // begin/step/complete will be no-ops without a command list.
        return .{
            .command_list = null,
            .srv_heap = null,
            .sampler_heap = null,
            .attachments = attachments,
            .step_number = 0,
        };
    };
    // Pass GPU-visible descriptor heaps from the DirectX12 API so
    // RenderPass.begin() can bind them via SetDescriptorHeaps before
    // any root descriptor table calls.
    const api = &self.renderer.api;
    return RenderPass.begin(.{
        .command_list = cl,
        .srv_heap = api.srv_heap,
        .sampler_heap = api.sampler_heap,
        .attachments = attachments,
    });
}

/// Close the command list and report frame health.
/// If sync is true the caller will block until the GPU finishes.
pub fn complete(self: *Frame, sync: bool) void {
    _ = sync;

    // If the frame was never initialized (stub path), report healthy
    // and let the generic renderer continue its lifecycle.
    const command_list = self.command_list orelse {
        // Defer frameCompleted to drawFrameEnd after the fence signal.
        const api: *DirectX12 = &self.renderer.api;
        api.pending_complete = .{
            .renderer = self.renderer,
            .health = .healthy,
        };
        return;
    };

    const hr = command_list.Close();
    const health: Health = if (FAILED(hr)) blk: {
        log.err("command list Close failed: 0x{x}", .{hrFmt(hr)});
        break :blk .unhealthy;
    } else .healthy;

    // Don't call frameCompleted here. The semaphore release must happen
    // after the GPU fence is signaled in drawFrameEnd, because
    // frame.resize() reuses descriptor slots that the GPU may still read.
    const api: *DirectX12 = &self.renderer.api;
    api.pending_complete = .{
        .renderer = self.renderer,
        .health = health,
    };
}

// --- Tests ---

test "Frame init error set includes expected errors" {
    // Compile-time check that init can return the documented error variants.
    const fn_info = @typeInfo(@TypeOf(Frame.init)).@"fn";
    const Errors = @typeInfo(fn_info.return_type.?).error_union.error_set;
    const err_fields = @typeInfo(Errors).error_set.?;
    inline for (.{ "CommandAllocatorCreationFailed", "CommandListCreationFailed", "CommandListCloseFailed" }) |name| {
        comptime var found = false;
        inline for (err_fields) |e| {
            if (comptime std.mem.eql(u8, e.name, name)) found = true;
        }
        try std.testing.expect(found);
    }
}

test "Frame reset error set includes expected errors" {
    const fn_info = @typeInfo(@TypeOf(Frame.reset)).@"fn";
    const Errors = @typeInfo(fn_info.return_type.?).error_union.error_set;
    const err_fields = @typeInfo(Errors).error_set.?;
    inline for (.{ "FrameNotInitialized", "CommandAllocatorResetFailed", "CommandListResetFailed" }) |name| {
        comptime var found = false;
        inline for (err_fields) |e| {
            if (comptime std.mem.eql(u8, e.name, name)) found = true;
        }
        try std.testing.expect(found);
    }
}

test "Frame has expected fields" {
    try std.testing.expect(@hasField(Frame, "command_list"));
    try std.testing.expect(@hasField(Frame, "command_allocator"));
    try std.testing.expect(@hasField(Frame, "fence_value"));
}
