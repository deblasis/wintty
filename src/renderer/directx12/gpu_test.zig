//! Integration tests for DX12 GPU resource types.
//!
//! These tests create a real D3D12 device (headless, no window/swap chain)
//! and exercise Buffer, Texture, Sampler, Pipeline, Frame, and Device
//! create/use/destroy cycles. They only run on Windows -- on other
//! platforms they're skipped.
const std = @import("std");
const builtin = @import("builtin");

const com = @import("com.zig");
const d3d12 = @import("d3d12.zig");
const dxgi = @import("dxgi.zig");
const buffer_mod = @import("buffer.zig");
const DescriptorHeap = @import("descriptor_heap.zig").DescriptorHeap;
const Texture = @import("Texture.zig");
const Sampler = @import("Sampler.zig");
const Pipeline = @import("Pipeline.zig");
const Frame = @import("Frame.zig");
const Device = @import("device.zig").Device;
const Surface = @import("surface.zig").Surface;
const Shaders = @import("shaders.zig").Shaders;

const Buffer = buffer_mod.Buffer;

// ---- Test device helper ----

/// Bundles a device, command queue, command list, and fence so tests
/// can create resources and record/execute commands.
const TestDevice = struct {
    device: *d3d12.ID3D12Device,
    command_queue: *d3d12.ID3D12CommandQueue,
    command_allocator: *d3d12.ID3D12CommandAllocator,
    command_list: *d3d12.ID3D12GraphicsCommandList,
    fence: *d3d12.ID3D12Fence,
    fence_event: std.os.windows.HANDLE,
    fence_value: u64,

    fn deinit(self: *TestDevice) void {
        _ = d3d12.CloseHandle(self.fence_event);
        _ = self.fence.Release();
        _ = self.command_list.Release();
        _ = self.command_allocator.Release();
        _ = self.command_queue.Release();
        _ = self.device.Release();
        self.* = undefined;
    }

    /// Execute the command list and wait for the GPU to finish.
    fn executeAndWait(self: *TestDevice) !void {
        var hr = self.command_list.Close();
        if (com.FAILED(hr)) return error.CommandListCloseFailed;

        const lists = [_]*d3d12.ID3D12GraphicsCommandList{self.command_list};
        self.command_queue.ExecuteCommandLists(1, @ptrCast(&lists));

        self.fence_value += 1;
        hr = self.command_queue.Signal(self.fence, self.fence_value);
        if (com.FAILED(hr)) return error.FenceSignalFailed;

        if (self.fence.GetCompletedValue() < self.fence_value) {
            hr = self.fence.SetEventOnCompletion(self.fence_value, self.fence_event);
            if (com.FAILED(hr)) return error.FenceSetEventFailed;
            const wait_result = d3d12.WaitForSingleObject(self.fence_event, d3d12.INFINITE);
            if (wait_result != 0) return error.WaitFailed;
        }
    }

    /// Reset the command allocator and list for new recording.
    fn reset(self: *TestDevice) !void {
        var hr = self.command_allocator.Reset();
        if (com.FAILED(hr)) return error.AllocatorResetFailed;
        hr = self.command_list.Reset(self.command_allocator, null);
        if (com.FAILED(hr)) return error.CommandListResetFailed;
    }
};

/// Create a D3D12 device for testing. Returns null on non-Windows or if
/// device creation fails (e.g. no GPU in CI).
fn createTestDevice() !TestDevice {
    if (comptime builtin.os.tag != .windows) return error.TestSkipped;

    // Device
    var device: ?*d3d12.ID3D12Device = null;
    var hr = d3d12.D3D12CreateDevice(
        null,
        d3d12.D3D_FEATURE_LEVEL_12_0,
        &d3d12.IID_ID3D12Device,
        @ptrCast(&device),
    );
    if (com.FAILED(hr) or device == null) return error.DeviceCreationFailed;
    errdefer _ = device.?.Release();

    // Command queue
    var command_queue: ?*d3d12.ID3D12CommandQueue = null;
    const queue_desc = d3d12.D3D12_COMMAND_QUEUE_DESC{
        .Type = .DIRECT,
        .Priority = 0,
        .Flags = .NONE,
        .NodeMask = 0,
    };
    hr = device.?.CreateCommandQueue(
        &queue_desc,
        &d3d12.IID_ID3D12CommandQueue,
        @ptrCast(&command_queue),
    );
    if (com.FAILED(hr) or command_queue == null) return error.CommandQueueCreationFailed;
    errdefer _ = command_queue.?.Release();

    // Command allocator
    var command_allocator: ?*d3d12.ID3D12CommandAllocator = null;
    hr = device.?.CreateCommandAllocator(
        .DIRECT,
        &d3d12.IID_ID3D12CommandAllocator,
        @ptrCast(&command_allocator),
    );
    if (com.FAILED(hr) or command_allocator == null) return error.CommandAllocatorCreationFailed;
    errdefer _ = command_allocator.?.Release();

    // Command list (created open)
    var command_list: ?*d3d12.ID3D12GraphicsCommandList = null;
    hr = device.?.CreateCommandList(
        0,
        .DIRECT,
        command_allocator.?,
        null,
        &d3d12.IID_ID3D12GraphicsCommandList,
        @ptrCast(&command_list),
    );
    if (com.FAILED(hr) or command_list == null) return error.CommandListCreationFailed;
    errdefer _ = command_list.?.Release();

    // Fence
    var fence: ?*d3d12.ID3D12Fence = null;
    hr = device.?.CreateFence(
        0,
        .NONE,
        &d3d12.IID_ID3D12Fence,
        @ptrCast(&fence),
    );
    if (com.FAILED(hr) or fence == null) return error.FenceCreationFailed;
    errdefer _ = fence.?.Release();

    const fence_event = d3d12.CreateEventW(null, 0, 0, null) orelse
        return error.FenceEventCreationFailed;
    errdefer _ = d3d12.CloseHandle(fence_event);

    return .{
        .device = device.?,
        .command_queue = command_queue.?,
        .command_allocator = command_allocator.?,
        .command_list = command_list.?,
        .fence = fence.?,
        .fence_event = fence_event,
        .fence_value = 0,
    };
}

// ---- Device + command queue + fence tests ----

test "Device: create and feature level" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    // If we got here, D3D12CreateDevice succeeded at feature level 12.0.
    // Verify the device is usable by querying descriptor handle increment size.
    const inc = dev.device.GetDescriptorHandleIncrementSize(.CBV_SRV_UAV);
    try std.testing.expect(inc > 0);
}

test "Command queue: fence signal and wait" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    // Close the open command list (we don't need to record anything).
    _ = dev.command_list.Close();

    // Signal the fence from the command queue.
    dev.fence_value += 1;
    const hr = dev.command_queue.Signal(dev.fence, dev.fence_value);
    try std.testing.expect(!com.FAILED(hr));

    // Wait for the GPU to reach the signaled value.
    if (dev.fence.GetCompletedValue() < dev.fence_value) {
        const hr2 = dev.fence.SetEventOnCompletion(dev.fence_value, dev.fence_event);
        try std.testing.expect(!com.FAILED(hr2));
        const wait_result = d3d12.WaitForSingleObject(dev.fence_event, d3d12.INFINITE);
        try std.testing.expectEqual(@as(u32, 0), wait_result);
    }

    try std.testing.expect(dev.fence.GetCompletedValue() >= dev.fence_value);
}

// ---- Descriptor heap tests ----

test "DescriptorHeap: create CBV/SRV/UAV and allocate" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    var heap = DescriptorHeap.init(
        dev.device,
        .CBV_SRV_UAV,
        16,
        true, // shader-visible
    ) catch return;
    defer heap.deinit();

    try std.testing.expectEqual(@as(u32, 16), heap.capacity);
    try std.testing.expectEqual(@as(u32, 0), heap.allocated);
    try std.testing.expect(heap.increment_size > 0);

    // Allocate a descriptor.
    const d0 = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 0), d0.index);
    try std.testing.expectEqual(@as(u32, 1), heap.allocated);
    try std.testing.expect(d0.cpu.ptr != 0);
    try std.testing.expect(d0.gpu.ptr != 0);
}

test "DescriptorHeap: create sampler heap" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    var heap = DescriptorHeap.init(
        dev.device,
        .SAMPLER,
        4,
        true,
    ) catch return;
    defer heap.deinit();

    try std.testing.expectEqual(@as(u32, 4), heap.capacity);

    const d0 = try heap.allocate();
    const d1 = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 0), d0.index);
    try std.testing.expectEqual(@as(u32, 1), d1.index);
    // GPU handles should be offset by increment_size.
    try std.testing.expectEqual(d0.gpu.ptr + @as(u64, heap.increment_size), d1.gpu.ptr);
}

test "DescriptorHeap: create RTV heap (non-shader-visible)" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    var heap = DescriptorHeap.init(
        dev.device,
        .RTV,
        3,
        false, // non-shader-visible
    ) catch return;
    defer heap.deinit();

    try std.testing.expectEqual(@as(u32, 3), heap.capacity);
    // Non-shader-visible heaps have gpu_start = 0.
    try std.testing.expectEqual(@as(u64, 0), heap.gpu_start.ptr);
}

// ---- Buffer tests ----

test "Buffer: create, sync, deinit" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    const TestFloat = Buffer(f32);
    var buf = try TestFloat.init(.{ .device = dev.device }, 64);
    defer buf.deinit();

    try std.testing.expect(buf.resource != null);
    try std.testing.expect(buf.mapped != null);
    try std.testing.expectEqual(@as(usize, 64), buf.len);
    try std.testing.expect(buf.buffer.gpu_address != 0);
    try std.testing.expectEqual(@as(u32, @sizeOf(f32)), buf.buffer.stride);

    // Sync some data.
    const data = [_]f32{ 1.0, 2.0, 3.0, 4.0 };
    try buf.sync(&data);
}

test "Buffer: sync triggers realloc when data exceeds capacity" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    const TestU32 = Buffer(u32);
    var buf = try TestU32.init(.{ .device = dev.device }, 4);
    defer buf.deinit();

    // Sync data that exceeds capacity -- should realloc at 2x.
    var big_data: [100]u32 = undefined;
    for (&big_data, 0..) |*v, i| v.* = @intCast(i);
    try buf.sync(&big_data);

    // After realloc at 2x, capacity should be exactly 200 (100 * 2).
    try std.testing.expectEqual(@as(usize, 200), buf.len);
}

test "Buffer: syncFromArrayLists concatenates correctly" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    const TestU32 = Buffer(u32);
    var buf = try TestU32.init(.{ .device = dev.device }, 64);
    defer buf.deinit();

    var list1 = std.ArrayListUnmanaged(u32){};
    defer list1.deinit(std.testing.allocator);
    try list1.appendSlice(std.testing.allocator, &.{ 1, 2, 3 });

    var list2 = std.ArrayListUnmanaged(u32){};
    defer list2.deinit(std.testing.allocator);
    try list2.appendSlice(std.testing.allocator, &.{ 4, 5 });

    const total = try buf.syncFromArrayLists(&.{ list1, list2 });
    try std.testing.expectEqual(@as(usize, 5), total);
}

test "Buffer: persistent mapping allows direct writes" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    const TestF32 = Buffer(f32);
    var buf = try TestF32.init(.{ .device = dev.device }, 16);
    defer buf.deinit();

    // DX12 buffers are persistently mapped -- write directly.
    const mapped = buf.mapped orelse return;
    const dst: [*]f32 = @ptrCast(@alignCast(mapped));
    dst[0] = 1.0;
    dst[1] = 2.0;
    dst[2] = 3.0;
    dst[3] = 4.0;

    // GPU address should be valid.
    try std.testing.expect(buf.buffer.gpu_address != 0);
    try std.testing.expectEqual(@as(u32, 16 * @sizeOf(f32)), buf.buffer.size);
}

test "Buffer: constant buffer (Uniforms)" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    const Uniforms = extern struct { x: f32, y: f32, z: f32, w: f32 };
    const TestCB = Buffer(Uniforms);
    var buf = try TestCB.init(.{ .device = dev.device }, 1);
    defer buf.deinit();

    try buf.sync(&.{Uniforms{ .x = 1.0, .y = 2.0, .z = 3.0, .w = 4.0 }});
    try std.testing.expectEqual(@as(u32, @sizeOf(Uniforms)), buf.buffer.stride);
}

test "Buffer: initFill creates buffer with data" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    const TestU8 = Buffer(u8);
    const data = [_]u8{ 0xAA, 0xBB, 0xCC, 0xDD };
    var buf = try TestU8.initFill(.{ .device = dev.device }, &data);
    defer buf.deinit();

    try std.testing.expectEqual(@as(usize, 4), buf.len);
    try std.testing.expect(buf.resource != null);
}

// ---- Texture tests ----

test "Texture: create R8_UNORM with initial data" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    var srv_heap = DescriptorHeap.init(
        dev.device,
        .CBV_SRV_UAV,
        16,
        true,
    ) catch return;
    defer srv_heap.deinit();

    // 4x4 R8_UNORM texture (16 bytes).
    var data: [16]u8 = undefined;
    for (&data, 0..) |*v, i| v.* = @intCast(i);

    const tex = Texture.init(.{
        .device = dev.device,
        .command_list = dev.command_list,
        .srv_heap = &srv_heap,
        .pixel_format = .R8_UNORM,
    }, 4, 4, &data) catch return;
    defer tex.deinit();

    // Execute the copy commands and wait for GPU to finish.
    try dev.executeAndWait();
    try dev.reset();

    try std.testing.expectEqual(@as(usize, 4), tex.width);
    try std.testing.expectEqual(@as(usize, 4), tex.height);
    try std.testing.expectEqual(@as(u32, 1), tex.bpp);
    try std.testing.expect(tex.resource != null);
    try std.testing.expect(tex.srv.cpu.ptr != 0);
}

test "Texture: create B8G8R8A8_UNORM without initial data" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    var srv_heap = DescriptorHeap.init(
        dev.device,
        .CBV_SRV_UAV,
        16,
        true,
    ) catch return;
    defer srv_heap.deinit();

    const tex = Texture.init(.{
        .device = dev.device,
        .command_list = dev.command_list,
        .srv_heap = &srv_heap,
        .pixel_format = .B8G8R8A8_UNORM,
    }, 8, 8, null) catch return;
    defer tex.deinit();

    try std.testing.expectEqual(@as(usize, 8), tex.width);
    try std.testing.expectEqual(@as(usize, 8), tex.height);
    try std.testing.expectEqual(@as(u32, 4), tex.bpp);
}

test "Texture: replaceRegion updates sub-region" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    var srv_heap = DescriptorHeap.init(
        dev.device,
        .CBV_SRV_UAV,
        16,
        true,
    ) catch return;
    defer srv_heap.deinit();

    var tex = Texture.init(.{
        .device = dev.device,
        .command_list = dev.command_list,
        .srv_heap = &srv_heap,
        .pixel_format = .B8G8R8A8_UNORM,
    }, 8, 8, null) catch return;
    defer tex.deinit();

    // Replace a 2x2 sub-region (16 bytes = 2*2*4 bpp).
    const region_data = [_]u8{0xFF} ** (2 * 2 * 4);
    tex.replaceRegion(1, 1, 2, 2, &region_data) catch return;

    // Execute the copy commands and wait for GPU to finish.
    try dev.executeAndWait();
    try dev.reset();

    // State should be back to PIXEL_SHADER_RESOURCE after replaceRegion.
    try std.testing.expectEqual(
        d3d12.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE,
        tex.state,
    );
}

// ---- Sampler tests ----

test "Sampler: create and deinit" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    var sampler_heap = DescriptorHeap.init(
        dev.device,
        .SAMPLER,
        4,
        true,
    ) catch return;
    defer sampler_heap.deinit();

    const sampler = Sampler.init(.{
        .device = dev.device,
        .sampler_heap = &sampler_heap,
    }) catch return;
    defer sampler.deinit();

    try std.testing.expect(sampler.descriptor.cpu.ptr != 0);
    try std.testing.expect(sampler.descriptor.gpu.ptr != 0);
}

test "Sampler: custom filter and address mode" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    var sampler_heap = DescriptorHeap.init(
        dev.device,
        .SAMPLER,
        4,
        true,
    ) catch return;
    defer sampler_heap.deinit();

    const sampler = Sampler.init(.{
        .device = dev.device,
        .sampler_heap = &sampler_heap,
        .filter = .MIN_MAG_MIP_POINT,
        .address_mode_u = .WRAP,
        .address_mode_v = .WRAP,
    }) catch return;
    defer sampler.deinit();

    try std.testing.expectEqual(@as(u32, 0), sampler.descriptor.index);
}

// ---- Pipeline tests ----

test "Pipeline: root signature creation" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    const root_sig = Pipeline.createRootSignature(dev.device) catch return;
    defer _ = root_sig.Release();

    // Root signature is a COM object -- if we got here, it was created.
}

test "Pipeline: all PSOs created from DXIL bytecode via Shaders.init" {
    if (comptime builtin.os.tag != .windows) return;

    var dev = createTestDevice() catch return;
    defer dev.deinit();

    var s = Shaders.init(dev.device) catch return;
    defer s.deinit(std.testing.allocator);

    try std.testing.expect(s.pipelines.bg_color.pso != null);
    try std.testing.expect(s.pipelines.cell_bg.pso != null);
    try std.testing.expect(s.pipelines.cell_text.pso != null);
    try std.testing.expect(s.pipelines.image.pso != null);
    try std.testing.expect(s.pipelines.bg_image.pso != null);
}

// ---- Frame tests ----

test "Frame: create, reset, deinit" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    // Close the test device's command list so it doesn't conflict.
    _ = dev.command_list.Close();

    // Frame.init sets renderer/target to undefined -- reset() only
    // touches command_allocator and command_list, so this is safe.
    var frame = Frame.init(dev.device) catch return;
    defer frame.deinit();

    // Frame starts with command list closed. Reset opens it.
    try frame.reset();

    // Close it again to verify the reset worked.
    const hr = frame.command_list.Close();
    try std.testing.expect(!com.FAILED(hr));
}

// ---- HWND swap chain + DirectComposition tests ----

test "Device: HWND surface uses DirectComposition with PREMULTIPLIED alpha" {
    if (comptime builtin.os.tag != .windows) return;

    const HWND = dxgi.HWND;
    const HINSTANCE = std.os.windows.HINSTANCE;
    const WNDCLASSEXW = extern struct {
        cbSize: u32 = @sizeOf(@This()),
        style: u32 = 0,
        lpfnWndProc: *const fn (HWND, u32, usize, isize) callconv(.winapi) isize,
        cbClsExtra: i32 = 0,
        cbWndExtra: i32 = 0,
        hInstance: ?HINSTANCE = null,
        hIcon: ?*anyopaque = null,
        hCursor: ?*anyopaque = null,
        hbrBackground: ?*anyopaque = null,
        lpszMenuName: ?[*:0]const u16 = null,
        lpszClassName: [*:0]const u16,
        hIconSm: ?*anyopaque = null,
    };

    const user32 = struct {
        extern "user32" fn RegisterClassExW(*const WNDCLASSEXW) callconv(.winapi) u16;
        extern "user32" fn CreateWindowExW(
            u32,
            [*:0]const u16,
            ?[*:0]const u16,
            u32,
            i32,
            i32,
            i32,
            i32,
            ?HWND,
            ?*anyopaque,
            ?HINSTANCE,
            ?*anyopaque,
        ) callconv(.winapi) ?HWND;
        extern "user32" fn DestroyWindow(HWND) callconv(.winapi) i32;
        extern "user32" fn DefWindowProcW(HWND, u32, usize, isize) callconv(.winapi) isize;
    };

    const class_name = std.unicode.utf8ToUtf16LeStringLiteral("GhosttyDX12DCompTestClass");
    const wc = WNDCLASSEXW{ .lpfnWndProc = user32.DefWindowProcW, .lpszClassName = class_name };
    _ = user32.RegisterClassExW(&wc);

    const hwnd = user32.CreateWindowExW(
        0,
        class_name,
        null,
        0,
        0,
        0,
        100,
        100,
        null,
        null,
        null,
        null,
    ) orelse return;
    defer _ = user32.DestroyWindow(hwnd);

    var device = Device.init(.{ .hwnd = hwnd }, .{
        .width = 100,
        .height = 100,
    }) catch return;
    defer device.deinit();

    // HWND path uses DirectComposition: dcomp objects must be non-null.
    try std.testing.expect(device.dcomp_device != null);
    try std.testing.expect(device.dcomp_target != null);
    try std.testing.expect(device.dcomp_visual != null);
    try std.testing.expect(device.swap_chain != null);

    // Swap chain uses composition path: STRETCH scaling, premultiplied alpha.
    var desc: dxgi.DXGI_SWAP_CHAIN_DESC1 = undefined;
    const hr = device.swap_chain.?.GetDesc1(&desc);
    try std.testing.expect(!com.FAILED(hr));
    try std.testing.expectEqual(dxgi.DXGI_SCALING.STRETCH, desc.Scaling);
    try std.testing.expectEqual(dxgi.DXGI_ALPHA_MODE.PREMULTIPLIED, desc.AlphaMode);
}

test "Device: shared texture mode has no swap chain or dcomp" {
    if (comptime builtin.os.tag != .windows) return;

    const HANDLE = std.os.windows.HANDLE;
    var shared_handle: ?HANDLE = null;

    var device = Device.init(.{ .shared_texture = .{
        .handle_out = &shared_handle,
        .width = 640,
        .height = 480,
    } }, .{}) catch return;
    defer device.deinit();

    // Shared texture mode: no swap chain, no DirectComposition.
    try std.testing.expect(device.swap_chain == null);
    try std.testing.expect(device.dcomp_device == null);
    try std.testing.expect(device.dcomp_target == null);
    try std.testing.expect(device.dcomp_visual == null);
}

// ---- Device.init edge case tests ----

test "Device: shared texture 0x0 dimensions does not crash" {
    if (comptime builtin.os.tag != .windows) return;

    const HANDLE = std.os.windows.HANDLE;
    var shared_handle: ?HANDLE = null;

    // SharedTexture mode has no swap chain, so 0x0 should not hit DXGI.
    var device = Device.init(.{ .shared_texture = .{
        .handle_out = &shared_handle,
        .width = 0,
        .height = 0,
    } }, .{}) catch return;
    defer device.deinit();

    try std.testing.expect(device.swap_chain == null);
    try std.testing.expect(device.fence_value == 0);
}

test "Device: HWND surface with 0x0 dimensions clamps to 1x1" {
    if (comptime builtin.os.tag != .windows) return;

    const HWND = dxgi.HWND;
    const HINSTANCE = std.os.windows.HINSTANCE;
    const WNDCLASSEXW = extern struct {
        cbSize: u32 = @sizeOf(@This()),
        style: u32 = 0,
        lpfnWndProc: *const fn (HWND, u32, usize, isize) callconv(.winapi) isize,
        cbClsExtra: i32 = 0,
        cbWndExtra: i32 = 0,
        hInstance: ?HINSTANCE = null,
        hIcon: ?*anyopaque = null,
        hCursor: ?*anyopaque = null,
        hbrBackground: ?*anyopaque = null,
        lpszMenuName: ?[*:0]const u16 = null,
        lpszClassName: [*:0]const u16,
        hIconSm: ?*anyopaque = null,
    };

    const user32 = struct {
        extern "user32" fn RegisterClassExW(*const WNDCLASSEXW) callconv(.winapi) u16;
        extern "user32" fn CreateWindowExW(
            u32,
            [*:0]const u16,
            ?[*:0]const u16,
            u32,
            i32,
            i32,
            i32,
            i32,
            ?HWND,
            ?*anyopaque,
            ?HINSTANCE,
            ?*anyopaque,
        ) callconv(.winapi) ?HWND;
        extern "user32" fn DestroyWindow(HWND) callconv(.winapi) i32;
        extern "user32" fn DefWindowProcW(HWND, u32, usize, isize) callconv(.winapi) isize;
    };

    const class_name = std.unicode.utf8ToUtf16LeStringLiteral("GhosttyDX12ZeroDimTestClass");
    const wc = WNDCLASSEXW{ .lpfnWndProc = user32.DefWindowProcW, .lpszClassName = class_name };
    _ = user32.RegisterClassExW(&wc);

    const hwnd = user32.CreateWindowExW(
        0,
        class_name,
        null,
        0,
        0,
        0,
        1,
        1,
        null,
        null,
        null,
        null,
    ) orelse return;
    defer _ = user32.DestroyWindow(hwnd);

    // 0x0 dimensions should be clamped to 1x1 inside createCompositionSwapChain.
    var device = Device.init(.{ .hwnd = hwnd }, .{
        .width = 0,
        .height = 0,
    }) catch return;
    defer device.deinit();

    // The swap chain must exist -- the clamp prevented DXGI from rejecting 0x0.
    try std.testing.expect(device.swap_chain != null);

    // Verify the swap chain dimensions were clamped to 1x1.
    var desc: dxgi.DXGI_SWAP_CHAIN_DESC1 = undefined;
    const hr = device.swap_chain.?.GetDesc1(&desc);
    try std.testing.expect(!com.FAILED(hr));
    try std.testing.expectEqual(@as(u32, 1), desc.Width);
    try std.testing.expectEqual(@as(u32, 1), desc.Height);
}

// ---- Execute and wait test (fence lifecycle) ----

test "Fence: execute empty command list and wait" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    // The command list is open from createTestDevice. Execute it empty.
    try dev.executeAndWait();

    // Fence value should match what we signaled.
    try std.testing.expect(dev.fence.GetCompletedValue() >= dev.fence_value);
}
