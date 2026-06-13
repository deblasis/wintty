//! DirectX12 backend for the terminal inspector's imgui overlay.
//!
//! This owns a shader-visible SRV descriptor heap and the alloc/free
//! callbacks that imgui 1.92's DX12 backend uses for its dynamic font atlas,
//! plus thin wrappers over ImGui_ImplDX12_{Init,NewFrame,RenderDrawData,
//! Shutdown}. The embedded apprt Inspector keeps the imgui frame
//! orchestration and delegates the DX12 specifics here.
//!
//! It lives in the directx12 renderer directory (rather than the apprt) so it
//! can sit next to the d3d12 bindings it needs and be exercised by the same
//! device-backed test harness as the rest of the renderer. It is only ever
//! compiled on Windows: the embedded apprt imports it behind a comptime
//! os.tag check, and the directx12 renderer itself is Windows-only.

const std = @import("std");
const builtin = @import("builtin");

const cimgui = @import("dcimgui");
const com = @import("com.zig");
const d3d12 = @import("d3d12.zig");
const dxgi = @import("dxgi.zig");
const DescriptorHeap = @import("descriptor_heap.zig").DescriptorHeap;

/// A shader-visible CBV/SRV heap with per-slot recycling. imgui 1.92's
/// dynamic font atlas allocates and frees texture descriptors as the atlas is
/// (re)built (e.g. on a DPI change), so a plain linear allocator would leak
/// slots over a long session of rebuilds. The free list lets freed slots be
/// reused.
pub const SrvHeap = struct {
    base: DescriptorHeap,
    free_buf: [capacity]u32 = undefined,
    free_len: usize = 0,

    /// Slot count. The inspector needs very few (a font atlas plus the
    /// occasional user texture), so this is generous headroom.
    pub const capacity = 64;

    pub fn deinit(self: *SrvHeap) void {
        self.base.deinit();
    }

    /// Allocate the next descriptor, reusing a freed slot if available.
    pub fn allocSlot(self: *SrvHeap) !DescriptorHeap.Descriptor {
        if (self.free_len > 0) {
            self.free_len -= 1;
            const idx = self.free_buf[self.free_len];
            return .{
                .cpu = self.base.cpuHandle(idx),
                .gpu = self.base.gpuHandle(idx),
                .index = idx,
            };
        }
        return self.base.allocate();
    }

    /// Return a slot to the free list, identified by its CPU handle (which is
    /// how imgui's free callback hands it back). The slot index is recovered
    /// from the offset into the heap.
    pub fn freeSlot(self: *SrvHeap, cpu_ptr: usize) void {
        const idx: u32 = @intCast((cpu_ptr - self.base.cpu_start.ptr) / self.base.increment_size);
        if (self.free_len < capacity) {
            self.free_buf[self.free_len] = idx;
            self.free_len += 1;
        }
    }
};

fn srvAlloc(
    info: *cimgui.ImGui_ImplDX12_InitInfo,
    out_cpu: *cimgui.ImGui_ImplDX12_CpuDescriptorHandle,
    out_gpu: *cimgui.ImGui_ImplDX12_GpuDescriptorHandle,
) callconv(.c) void {
    const heap: *SrvHeap = @ptrCast(@alignCast(info.UserData.?));
    const d = heap.allocSlot() catch {
        out_cpu.* = .{ .ptr = 0 };
        out_gpu.* = .{ .ptr = 0 };
        return;
    };
    out_cpu.* = .{ .ptr = d.cpu.ptr };
    out_gpu.* = .{ .ptr = d.gpu.ptr };
}

fn srvFree(
    info: *cimgui.ImGui_ImplDX12_InitInfo,
    cpu: cimgui.ImGui_ImplDX12_CpuDescriptorHandle,
    gpu: cimgui.ImGui_ImplDX12_GpuDescriptorHandle,
) callconv(.c) void {
    _ = gpu;
    const heap: *SrvHeap = @ptrCast(@alignCast(info.UserData.?));
    heap.freeSlot(cpu.ptr);
}

/// Create the SRV heap that backs an inspector's imgui textures.
pub fn createHeap(device: *d3d12.ID3D12Device) !SrvHeap {
    return .{ .base = try DescriptorHeap.init(device, .CBV_SRV_UAV, SrvHeap.capacity, true) };
}

/// Initialize the imgui DX12 backend. `heap` must outlive the backend; its
/// address is stored as the imgui UserData and recovered by the callbacks.
pub fn init(
    device: *d3d12.ID3D12Device,
    command_queue: *d3d12.ID3D12CommandQueue,
    num_frames: u32,
    rtv_format: u32,
    heap: *SrvHeap,
) bool {
    var info: cimgui.ImGui_ImplDX12_InitInfo = .{};
    info.Device = @ptrCast(device);
    info.CommandQueue = @ptrCast(command_queue);
    info.NumFramesInFlight = @intCast(num_frames);
    info.RTVFormat = rtv_format;
    info.SrvDescriptorHeap = @ptrCast(heap.base.heap);
    info.UserData = @ptrCast(heap);
    info.SrvDescriptorAllocFn = srvAlloc;
    info.SrvDescriptorFreeFn = srvFree;
    return cimgui.ImGui_ImplDX12_Init(&info);
}

pub fn newFrame() void {
    cimgui.ImGui_ImplDX12_NewFrame();
}

/// Record the imgui draw data into `command_list`. The caller must already
/// have bound its render target and viewport; this binds the inspector's SRV
/// heap (required before imgui's RenderDrawData issues its draws).
pub fn renderDrawData(
    draw_data: *cimgui.c.ImDrawData,
    command_list: *d3d12.ID3D12GraphicsCommandList,
    heap: *SrvHeap,
) void {
    const heaps = [_]*d3d12.ID3D12DescriptorHeap{heap.base.heap};
    command_list.SetDescriptorHeaps(1, &heaps);
    cimgui.ImGui_ImplDX12_RenderDrawData(draw_data, @ptrCast(command_list));
}

pub fn shutdown() void {
    cimgui.ImGui_ImplDX12_Shutdown();
}

// --- Tests ---

test "SrvHeap recycles freed slots" {
    // No device: drive the base heap's pure arithmetic via known fields,
    // following the descriptor_heap.zig test idiom.
    var h: SrvHeap = .{ .base = undefined };
    h.base.cpu_start = .{ .ptr = 0x1000 };
    h.base.gpu_start = .{ .ptr = 0x2000 };
    h.base.increment_size = 32;
    h.base.capacity = 4;
    h.base.allocated = 0;

    const a = try h.allocSlot();
    const b = try h.allocSlot();
    try std.testing.expectEqual(@as(u32, 0), a.index);
    try std.testing.expectEqual(@as(u32, 1), b.index);

    // Free the first slot; the next allocation should reuse it rather than
    // grow the linear allocator.
    h.freeSlot(a.cpu.ptr);
    const c = try h.allocSlot();
    try std.testing.expectEqual(@as(u32, 0), c.index);
    try std.testing.expectEqual(@as(usize, 0x1000), c.cpu.ptr);
}

test "SrvHeap freeSlot recovers index from cpu handle" {
    var h: SrvHeap = .{ .base = undefined };
    h.base.cpu_start = .{ .ptr = 0x4000 };
    h.base.increment_size = 64;
    h.base.capacity = 8;
    h.base.allocated = 5;

    h.freeSlot(0x4000 + 3 * 64);
    try std.testing.expectEqual(@as(usize, 1), h.free_len);
    try std.testing.expectEqual(@as(u32, 3), h.free_buf[0]);
}

test "imgui dx12 backend produces and records draw data on a real device" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    // Device + command queue/allocator/list + fence. Skip if no GPU.
    var device: ?*d3d12.ID3D12Device = null;
    if (com.FAILED(d3d12.D3D12CreateDevice(
        null,
        d3d12.D3D_FEATURE_LEVEL_12_0,
        &d3d12.ID3D12Device.IID,
        @ptrCast(&device),
    )) or device == null) return error.SkipZigTest;
    const dev = device.?;
    defer _ = dev.Release();

    var queue: ?*d3d12.ID3D12CommandQueue = null;
    const queue_desc = d3d12.D3D12_COMMAND_QUEUE_DESC{ .Type = .DIRECT, .Priority = 0, .Flags = .NONE, .NodeMask = 0 };
    if (com.FAILED(dev.CreateCommandQueue(&queue_desc, &d3d12.ID3D12CommandQueue.IID, @ptrCast(&queue)))) return error.SkipZigTest;
    defer _ = queue.?.Release();

    var allocator: ?*d3d12.ID3D12CommandAllocator = null;
    if (com.FAILED(dev.CreateCommandAllocator(.DIRECT, &d3d12.ID3D12CommandAllocator.IID, @ptrCast(&allocator)))) return error.SkipZigTest;
    defer _ = allocator.?.Release();

    var cmd_list: ?*d3d12.ID3D12GraphicsCommandList = null;
    if (com.FAILED(dev.CreateCommandList(0, .DIRECT, allocator.?, null, &d3d12.ID3D12GraphicsCommandList.IID, @ptrCast(&cmd_list)))) return error.SkipZigTest;
    const cl = cmd_list.?;
    defer _ = cl.Release();

    var fence: ?*d3d12.ID3D12Fence = null;
    if (com.FAILED(dev.CreateFence(0, .NONE, &d3d12.ID3D12Fence.IID, @ptrCast(&fence)))) return error.SkipZigTest;
    defer _ = fence.?.Release();
    const fence_event = d3d12.CreateEventW(null, 0, 0, null) orelse return error.SkipZigTest;
    defer _ = d3d12.CloseHandle(fence_event);

    // Offscreen render target (256x256 B8G8R8A8) + RTV.
    const rtv_format = dxgi.DXGI_FORMAT.B8G8R8A8_UNORM;
    const heap_props = d3d12.D3D12_HEAP_PROPERTIES{ .Type = .DEFAULT, .CPUPageProperty = 0, .MemoryPoolPreference = 0, .CreationNodeMask = 0, .VisibleNodeMask = 0 };
    const rt_desc = d3d12.D3D12_RESOURCE_DESC{
        .Dimension = .TEXTURE2D,
        .Alignment = 0,
        .Width = 256,
        .Height = 256,
        .DepthOrArraySize = 1,
        .MipLevels = 1,
        .Format = rtv_format,
        .SampleDesc = .{ .Count = 1, .Quality = 0 },
        .Layout = .UNKNOWN,
        .Flags = .ALLOW_RENDER_TARGET,
    };
    var rt: ?*d3d12.ID3D12Resource = null;
    if (com.FAILED(dev.CreateCommittedResource(&heap_props, 0, &rt_desc, .RENDER_TARGET, null, &d3d12.ID3D12Resource.IID, @ptrCast(&rt)))) return error.SkipZigTest;
    defer _ = rt.?.Release();

    var rtv_heap = DescriptorHeap.init(dev, .RTV, 1, false) catch return error.SkipZigTest;
    defer rtv_heap.deinit();
    const rtv = try rtv_heap.allocate();
    dev.CreateRenderTargetView(rt.?, null, rtv.cpu);

    // imgui context + our backend.
    cimgui.c.ImGui_SetCurrentContext(cimgui.c.ImGui_CreateContext(null));
    defer cimgui.c.ImGui_DestroyContext(cimgui.c.ImGui_GetCurrentContext());
    const io: *cimgui.c.ImGuiIO = cimgui.c.ImGui_GetIO();
    io.DisplaySize = .{ .x = 256, .y = 256 };
    io.DeltaTime = 1.0 / 60.0;
    io.IniFilename = null; // don't write imgui.ini from the test

    var srv_heap = createHeap(dev) catch return error.SkipZigTest;
    defer srv_heap.deinit();
    try std.testing.expect(init(dev, queue.?, 1, @intFromEnum(rtv_format), &srv_heap));
    defer shutdown();

    // Build a frame that generates geometry. imgui needs a couple of frames
    // to settle its state (same reason the Metal backend renders twice).
    for (0..2) |_| {
        newFrame();
        cimgui.c.ImGui_NewFrame();
        cimgui.c.ImGui_ShowDemoWindow(null);
        cimgui.c.ImGui_Render();
    }

    const draw_data = cimgui.c.ImGui_GetDrawData();
    try std.testing.expect(draw_data != null);
    try std.testing.expect(draw_data.*.TotalVtxCount > 0);

    // Record the draw data against the offscreen RTV and execute it. This
    // exercises the real RenderDrawData path (descriptor heap binding, PSO,
    // draws) rather than just the draw-data generation.
    const rtvs = [_]d3d12.D3D12_CPU_DESCRIPTOR_HANDLE{rtv.cpu};
    cl.OMSetRenderTargets(1, &rtvs, 0, null);
    const viewport = d3d12.D3D12_VIEWPORT{ .TopLeftX = 0, .TopLeftY = 0, .Width = 256, .Height = 256, .MinDepth = 0, .MaxDepth = 1 };
    cl.RSSetViewports(1, @ptrCast(&viewport));
    renderDrawData(draw_data, cl, &srv_heap);

    // Execute and wait.
    if (com.FAILED(cl.Close())) return error.CommandListCloseFailed;
    const lists = [_]*d3d12.ID3D12GraphicsCommandList{cl};
    queue.?.ExecuteCommandLists(1, @ptrCast(&lists));
    if (com.FAILED(queue.?.Signal(fence.?, 1))) return error.FenceSignalFailed;
    if (fence.?.GetCompletedValue() < 1) {
        if (com.FAILED(fence.?.SetEventOnCompletion(1, fence_event))) return error.FenceSetEventFailed;
        if (d3d12.WaitForSingleObject(fence_event, d3d12.INFINITE) != 0) return error.WaitFailed;
    }
}
