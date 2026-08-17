//! DX12 swap-chain surface for the embedded inspector window.
//!
//! Owns a minimal D3D12 device + composition-surface-handle swap chain,
//! binds it to a WinUI SwapChainPanel, and presents imgui frames on
//! demand from the host's timer loop.

pub const State = @This();

const std = @import("std");
const builtin = @import("builtin");

const com = @import("com.zig");
const d3d12 = @import("d3d12.zig");
const dxgi = @import("dxgi.zig");
const device = @import("device.zig");
const DescriptorHeap = @import("descriptor_heap.zig").DescriptorHeap;
const Target = @import("Target.zig");

const HRESULT = com.HRESULT;
const FAILED = com.FAILED;

const log = std.log.scoped(.inspector_dx12);

const GpuFrame = struct {
    command_allocator: *d3d12.ID3D12CommandAllocator,
    command_list: *d3d12.ID3D12GraphicsCommandList,
    fence_value: u64 = 0,

    fn init(dev: *d3d12.ID3D12Device) !GpuFrame {
        var allocator: ?*d3d12.ID3D12CommandAllocator = null;
        const alloc_hr = dev.CreateCommandAllocator(
            .DIRECT,
            &d3d12.ID3D12CommandAllocator.IID,
            @ptrCast(&allocator),
        );
        if (FAILED(alloc_hr)) return error.CommandAllocatorCreationFailed;
        errdefer _ = allocator.?.Release();

        var command_list: ?*d3d12.ID3D12GraphicsCommandList = null;
        const list_hr = dev.CreateCommandList(
            0,
            .DIRECT,
            allocator.?,
            null,
            &d3d12.ID3D12GraphicsCommandList.IID,
            @ptrCast(&command_list),
        );
        if (FAILED(list_hr)) return error.CommandListCreationFailed;
        errdefer _ = command_list.?.Release();

        const close_hr = command_list.?.Close();
        if (FAILED(close_hr)) return error.CommandListCloseFailed;

        return .{
            .command_allocator = allocator.?,
            .command_list = command_list.?,
        };
    }

    fn deinit(self: *GpuFrame) void {
        _ = self.command_list.Close();
        _ = self.command_list.Release();
        _ = self.command_allocator.Release();
    }

    fn reset(self: *GpuFrame) !void {
        const alloc_hr = self.command_allocator.Reset();
        if (FAILED(alloc_hr)) return error.CommandAllocatorResetFailed;

        const list_hr = self.command_list.Reset(self.command_allocator, null);
        if (FAILED(list_hr)) return error.CommandListResetFailed;
    }
};

dev: device.Device,
swap_chain3: *dxgi.IDXGISwapChain3,
width: u32,
height: u32,
rtv_heap: DescriptorHeap,
back_buffers: [device.frame_count]?*d3d12.ID3D12Resource = .{null} ** device.frame_count,
rtv_handles: [device.frame_count]d3d12.D3D12_CPU_DESCRIPTOR_HANDLE = undefined,
frames: [device.frame_count]GpuFrame = undefined,
frames_initialized: bool = false,

pub fn init(
    panel_native: *dxgi.ISwapChainPanelNative,
    width: u32,
    height: u32,
) !State {
    var result: State = undefined;

    result.dev = try device.Device.init(.swap_chain_panel, .{
        .width = width,
        .height = height,
    });
    errdefer result.dev.deinit();

    const handle = result.dev.swap_chain_surface_handle orelse return error.NoSurfaceHandle;
    const sc = result.dev.swap_chain orelse return error.NoSwapChain;

    try bindSwapChainHandle(panel_native, handle);

    var sc3: ?*dxgi.IDXGISwapChain3 = null;
    const hr = sc.vtable.QueryInterface(
        @ptrCast(sc),
        &dxgi.IDXGISwapChain3.IID,
        @ptrCast(&sc3),
    );
    if (FAILED(hr)) return error.SwapChain3QueryFailed;
    result.swap_chain3 = sc3.?;
    errdefer _ = result.swap_chain3.Release();

    result.width = @max(width, 1);
    result.height = @max(height, 1);

    result.rtv_heap = try DescriptorHeap.init(
        result.dev.device,
        .RTV,
        device.frame_count,
        false,
    );
    errdefer result.rtv_heap.deinit();

    try result.acquireBackBuffers();
    errdefer result.releaseBackBuffers();

    var frames_initialized: u32 = 0;
    errdefer for (0..frames_initialized) |i| result.frames[i].deinit();
    for (0..device.frame_count) |i| {
        result.frames[i] = try GpuFrame.init(result.dev.device);
        frames_initialized += 1;
    }
    result.frames_initialized = true;

    return result;
}

pub fn deinit(self: *State) void {
    self.dev.waitForGpu() catch {};

    if (self.frames_initialized) self.deinitFrames();

    self.releaseBackBuffers();
    self.rtv_heap.deinit();

    _ = self.swap_chain3.Release();
    self.dev.deinit();
    self.* = undefined;
}

fn deinitFrames(self: *State) void {
    for (&self.frames) |*f| f.deinit();
    self.frames_initialized = false;
}

fn acquireBackBuffers(self: *State) !void {
    for (0..device.frame_count) |i| {
        var resource: ?*d3d12.ID3D12Resource = null;
        const hr = self.swap_chain3.GetBuffer(
            @intCast(i),
            &d3d12.ID3D12Resource.IID,
            @ptrCast(&resource),
        );
        if (FAILED(hr)) return error.GetBufferFailed;
        self.back_buffers[i] = resource;

        const rtv_handle = self.rtv_heap.cpuHandle(@intCast(i));
        self.dev.device.CreateRenderTargetView(resource, null, rtv_handle);
        self.rtv_handles[i] = rtv_handle;
    }
    self.rtv_heap.allocated = device.frame_count;
}

fn releaseBackBuffers(self: *State) void {
    for (&self.back_buffers) |*bb| {
        if (bb.*) |r| {
            _ = r.Release();
            bb.* = null;
        }
    }
}

fn bindSwapChainHandle(
    panel_native: *dxgi.ISwapChainPanelNative,
    handle: std.os.windows.HANDLE,
) !void {
    var native2: ?*dxgi.ISwapChainPanelNative2 = null;
    const hr = panel_native.vtable.QueryInterface(
        panel_native,
        &dxgi.ISwapChainPanelNative2.IID,
        @ptrCast(&native2),
    );
    if (FAILED(hr)) {
        log.err("QueryInterface for ISwapChainPanelNative2 failed: 0x{x}", .{
            @as(u32, @bitCast(hr)),
        });
        return error.PanelQueryFailed;
    }
    defer _ = native2.?.Release();

    const shr = native2.?.SetSwapChainHandle(handle);
    if (FAILED(shr)) {
        log.err("SetSwapChainHandle failed: 0x{x}", .{@as(u32, @bitCast(shr))});
        return error.SetSwapChainHandleFailed;
    }
}

/// Begin a present frame: wait for the frame slot, reset its command list,
/// transition the back buffer to RENDER_TARGET, clear, and bind viewport.
pub fn beginPresentFrame(self: *State) !struct {
    frame_idx: u32,
    command_list: *d3d12.ID3D12GraphicsCommandList,
    target: Target,
} {
    const frame_idx = self.swap_chain3.GetCurrentBackBufferIndex();
    var frame = &self.frames[frame_idx];

    const wait_value = frame.fence_value;
    if (self.dev.fence.GetCompletedValue() < wait_value) {
        const hr = self.dev.fence.SetEventOnCompletion(wait_value, self.dev.fence_event);
        if (FAILED(hr)) return error.FrameSyncFailed;
        _ = d3d12.WaitForSingleObject(self.dev.fence_event, d3d12.INFINITE);
    }

    try frame.reset();

    const resource = self.back_buffers[frame_idx] orelse return error.NoBackBuffer;
    const rtv_handle = self.rtv_handles[frame_idx];
    var target: Target = .{
        .resource = resource,
        .rtv_handle = rtv_handle,
        .width = self.width,
        .height = self.height,
    };

    const cl = frame.command_list;
    target.transitionBarrier(
        cl,
        d3d12.D3D12_RESOURCE_STATES.PRESENT,
        d3d12.D3D12_RESOURCE_STATES.RENDER_TARGET,
    );

    const clear = [4]f32{ 0.1, 0.1, 0.1, 1.0 };
    cl.ClearRenderTargetView(rtv_handle, &clear, 0, null);

    cl.OMSetRenderTargets(1, @ptrCast(&rtv_handle), .FALSE, null);

    const viewport = d3d12.D3D12_VIEWPORT{
        .TopLeftX = 0,
        .TopLeftY = 0,
        .Width = @floatFromInt(self.width),
        .Height = @floatFromInt(self.height),
        .MinDepth = 0.0,
        .MaxDepth = 1.0,
    };
    cl.RSSetViewports(1, @ptrCast(&viewport));

    const scissor = d3d12.D3D12_RECT{
        .left = 0,
        .top = 0,
        .right = @intCast(self.width),
        .bottom = @intCast(self.height),
    };
    cl.RSSetScissorRects(1, @ptrCast(&scissor));

    return .{
        .frame_idx = frame_idx,
        .command_list = cl,
        .target = target,
    };
}

/// Finish a present frame: transition back to PRESENT, submit, and present.
pub fn endPresentFrame(
    self: *State,
    frame_idx: u32,
    target: *Target,
    command_list: *d3d12.ID3D12GraphicsCommandList,
) !void {
    target.transitionBarrier(
        command_list,
        d3d12.D3D12_RESOURCE_STATES.RENDER_TARGET,
        d3d12.D3D12_RESOURCE_STATES.PRESENT,
    );

    const close_hr = command_list.Close();
    if (FAILED(close_hr)) return error.CommandListCloseFailed;

    const lists = [_]*d3d12.ID3D12GraphicsCommandList{command_list};
    self.dev.command_queue.ExecuteCommandLists(1, &lists);

    const present_hr = self.swap_chain3.Present(1, 0);
    if (present_hr == com.DXGI_ERROR_DEVICE_REMOVED or
        present_hr == com.DXGI_ERROR_DEVICE_HUNG or
        present_hr == com.DXGI_ERROR_DEVICE_RESET)
    {
        log.err("inspector Present device lost: 0x{x}", .{@as(u32, @bitCast(present_hr))});
        return error.PresentFailed;
    }
    if (FAILED(present_hr)) {
        log.err("inspector Present failed: 0x{x}", .{@as(u32, @bitCast(present_hr))});
    }

    const new_fence_value = self.dev.fence_value.fetchAdd(1, .release) + 1;
    self.frames[frame_idx].fence_value = new_fence_value;

    const signal_hr = self.dev.command_queue.Signal(self.dev.fence, new_fence_value);
    if (signal_hr == com.DXGI_ERROR_DEVICE_REMOVED or
        signal_hr == com.DXGI_ERROR_DEVICE_HUNG or
        signal_hr == com.DXGI_ERROR_DEVICE_RESET)
    {
        log.err("inspector fence Signal device lost: 0x{x}", .{@as(u32, @bitCast(signal_hr))});
        return error.FenceSignalFailed;
    }
    if (FAILED(signal_hr)) {
        log.err("inspector fence Signal failed: 0x{x}", .{@as(u32, @bitCast(signal_hr))});
    }
}

pub fn resize(self: *State, width: u32, height: u32) !void {
    const w = @max(width, 1);
    const h = @max(height, 1);
    if (w == self.width and h == self.height) return;

    self.dev.waitForGpu() catch {};

    const old_w = self.width;
    const old_h = self.height;

    self.releaseBackBuffers();

    const sc1: *dxgi.IDXGISwapChain1 = @ptrCast(self.swap_chain3);
    const hr = sc1.ResizeBuffers(
        device.frame_count,
        w,
        h,
        .UNKNOWN,
        0,
    );
    if (FAILED(hr)) {
        log.err("inspector ResizeBuffers failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        // Re-acquire at the previous size so presents do not hit NoBackBuffer.
        const rollback = sc1.ResizeBuffers(
            device.frame_count,
            old_w,
            old_h,
            .UNKNOWN,
            0,
        );
        if (!FAILED(rollback)) {
            self.acquireBackBuffers() catch |acq_err| {
                log.err("inspector acquireBackBuffers after resize rollback failed: {}", .{acq_err});
            };
        }
        return error.ResizeBuffersFailed;
    }

    try self.acquireBackBuffers();

    for (&self.frames) |*f| f.fence_value = 0;

    self.width = w;
    self.height = h;
}

test "State has expected fields" {
    try std.testing.expect(@hasField(State, "dev"));
    try std.testing.expect(@hasField(State, "swap_chain3"));
    try std.testing.expect(@hasField(State, "rtv_heap"));
}
