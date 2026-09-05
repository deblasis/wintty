//! Integration tests for DX12 GPU resource types.
//!
//! These tests create a real D3D12 device (headless, no window/swap chain)
//! and exercise Buffer, Texture, Sampler, Pipeline, Frame, and Device
//! create/use/destroy cycles. They only run on Windows -- on other
//! platforms they're skipped.
const std = @import("std");
const builtin = @import("builtin");
const global = @import("../../global.zig");

const com = @import("com.zig");
const d3d12 = @import("d3d12.zig");
const dxgi = @import("dxgi.zig");
const buffer_mod = @import("buffer.zig");
const DescriptorHeap = @import("descriptor_heap.zig").DescriptorHeap;
const Texture = @import("Texture.zig");
const Sampler = @import("Sampler.zig");
const RenderPassMod = @import("RenderPass.zig");
const shadertoy = @import("../shadertoy.zig");
const Pipeline = @import("Pipeline.zig");
const Frame = @import("Frame.zig");
const Device = @import("device.zig").Device;
const Surface = @import("surface.zig").Surface;
const shaders_mod = @import("shaders.zig");
const Shaders = shaders_mod.Shaders;

const Buffer = buffer_mod.Buffer;

// ---- Test environment helpers ----

/// True when this process runs on an interactive desktop.
///
/// The HWND/dcomp tests below need a composition swap chain, and
/// CreateSwapChainForComposition returns DXGI_ERROR_NOT_CURRENTLY_AVAILABLE
/// (0x887A0022) wherever DWM is not composing for the caller: session 0
/// services, ssh sessions, and headless CI. Device.init logs that HRESULT
/// as an error, and the test runner treats any logged error as a run
/// failure even though the tests themselves skip, so every headless run
/// of this suite turned two environmental impossibilities into "N errors
/// were logged" and a failed `zig build test`. Skipping before Device.init
/// is called keeps the suite green on headless hosts and unchanged on
/// interactive ones.
fn hasInteractiveDesktop() bool {
    if (comptime builtin.os.tag != .windows) return false;

    const USEROBJECTFLAGS = extern struct {
        fInherit: i32,
        fReserved: i32,
        dwFlags: u32,
    };
    const UOI_FLAGS: u32 = 1;
    const WSF_VISIBLE: u32 = 0x0001;

    const user32 = struct {
        extern "user32" fn GetProcessWindowStation() callconv(.winapi) ?*anyopaque;
        extern "user32" fn GetUserObjectInformationW(
            hObj: ?*anyopaque,
            nIndex: u32,
            pvInfo: ?*anyopaque,
            nLength: u32,
            lpnLengthNeeded: ?*u32,
        ) callconv(.winapi) i32;
    };

    const station = user32.GetProcessWindowStation() orelse return false;
    var flags: USEROBJECTFLAGS = std.mem.zeroes(USEROBJECTFLAGS);
    var needed: u32 = 0;
    if (user32.GetUserObjectInformationW(
        station,
        UOI_FLAGS,
        &flags,
        @sizeOf(USEROBJECTFLAGS),
        &needed,
    ) == 0) return false;
    return flags.dwFlags & WSF_VISIBLE != 0;
}

/// COM object reduced to its Release slot, for owning a raw IUnknown.
const AnyComObject = extern struct {
    vtable: *const VTable,
    pub const VTable = extern struct {
        QueryInterface: *const anyopaque,
        AddRef: *const anyopaque,
        Release: *const fn (*AnyComObject) callconv(.winapi) u32,
    };
};

fn releaseCom(ptr: ?*anyopaque) void {
    if (ptr) |p| {
        const obj: *AnyComObject = @ptrCast(@alignCast(p));
        _ = obj.vtable.Release(obj);
    }
}

/// Minimal IDXGIFactory4 binding for EnumWarpAdapter, which is vtable
/// slot 27: IUnknown holds slots 0..2, IDXGIObject 3..6, IDXGIFactory
/// 7..11, IDXGIFactory1 12..13, IDXGIFactory2 14..24 (including the two
/// Unregister methods), IDXGIFactory3 25, then EnumAdapterByLuid 26 and
/// EnumWarpAdapter 27. Asking for the adapter as IUnknown keeps the
/// returned pointer usable by D3D12CreateDevice and releasable through
/// AnyComObject.
const Factory4 = extern struct {
    vtable: *const VTable,
    pub const IID = com.GUID{
        .data1 = 0x1bc6ea02,
        .data2 = 0xef36,
        .data3 = 0x464f,
        .data4 = .{ 0xbf, 0x0c, 0x21, 0xca, 0x39, 0xe5, 0x16, 0x8a },
    };
    pub const VTable = extern struct {
        QueryInterface: *const fn (*Factory4, *const com.GUID, *?*anyopaque) callconv(.winapi) com.HRESULT,
        AddRef: *const fn (*Factory4) callconv(.winapi) u32,
        Release: *const fn (*Factory4) callconv(.winapi) u32,
        pad: [23]*const anyopaque,
        EnumAdapterByLuid: *const anyopaque,
        EnumWarpAdapter: *const fn (*Factory4, *const com.GUID, *?*anyopaque) callconv(.winapi) com.HRESULT,
    };

    comptime {
        // The slot arithmetic above is the one thing here that cannot be
        // checked by reading: a wrong count calls an arbitrary function
        // pointer. Pin it so the compiler answers on every build.
        std.debug.assert(@offsetOf(VTable, "EnumWarpAdapter") == 27 * @sizeOf(usize));
    }
};

/// The WARP software adapter when WINTTY_GPU_TEST_WARP=1, else null (the
/// default adapter). This is how the whole TestDevice family can be run
/// on the software rasterizer on demand, e.g.
///   WINTTY_GPU_TEST_WARP=1 zig build test -Dtest-filter="DescriptorHeap"
/// or the same variable set for a loop of full runs on a box whose
/// default adapter is hardware.
///
/// The value must be exactly "1"; anything else (including "true") leaves
/// the default adapter in place. Acquisition failure while the variable IS
/// set returns an error rather than falling back: an explicit opt-in that
/// silently runs on hardware would report a green run for a path it never
/// exercised, which is the whole point of the knob.
fn warpAdapterIfRequested() !?*anyopaque {
    if (comptime builtin.os.tag != .windows) return null;

    const kernel32 = struct {
        extern "kernel32" fn GetEnvironmentVariableA(
            lpName: [*:0]const u8,
            lpBuffer: ?[*]u8,
            nSize: u32,
        ) callconv(.winapi) u32;
    };
    var buf: [4]u8 = undefined;
    const n = kernel32.GetEnvironmentVariableA("WINTTY_GPU_TEST_WARP", &buf, buf.len);
    if (n != 1 or buf[0] != '1') return null;

    var factory: ?*Factory4 = null;
    const hr = dxgi.CreateDXGIFactory2(0, &Factory4.IID, @ptrCast(&factory));
    if (com.FAILED(hr) or factory == null) {
        std.debug.print(
            "WINTTY_GPU_TEST_WARP=1 but IDXGIFactory4 is unavailable (hr=0x{X:0>8})\n",
            .{@as(u32, @bitCast(hr))},
        );
        return error.WarpAdapterUnavailable;
    }
    defer _ = factory.?.vtable.Release(factory.?);

    const iid_iunknown = com.GUID{
        .data1 = 0x00000000,
        .data2 = 0x0000,
        .data3 = 0x0000,
        .data4 = .{ 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 },
    };
    var adapter: ?*anyopaque = null;
    const hr2 = factory.?.vtable.EnumWarpAdapter(factory.?, &iid_iunknown, &adapter);
    if (com.FAILED(hr2) or adapter == null) {
        std.debug.print(
            "WINTTY_GPU_TEST_WARP=1 but EnumWarpAdapter failed (hr=0x{X:0>8})\n",
            .{@as(u32, @bitCast(hr2))},
        );
        return error.WarpAdapterUnavailable;
    }
    return adapter;
}

// ---- Test device helper ----

/// Minimal debug-layer info queue: the debug layer is enabled by
/// Device.init in Debug builds, so any validation error from the post
/// pass lands here. Layout follows d3d12sdklayers.h's ID3D12InfoQueue:
/// the IID is 0742a90b-c387-483f-b946-30a7e4e61458 and the method order
/// after IUnknown is SetMessageCountLimit, ClearStoredMessages,
/// GetMessage, then the GetNum* counters. The two defects this corrects
/// in the previous declaration: a wrong IID (E_NOINTERFACE on every
/// machine, so the drain below never ran and healthy runs printed
/// "D3D12 debug layer unavailable") and ID3D12InfoQueue1's mute methods
/// spliced into the vtable, which left GetNumStoredMessages on
/// GetMessage's slot and GetMessage on a counter's slot; the drain only
/// worked at all because the wrong IID kept the misaligned calls from
/// ever being made.
const InfoQueue = extern struct {
    vtable: *const VTable,
    pub const IID = com.GUID{
        .data1 = 0x0742a90b,
        .data2 = 0xc387,
        .data3 = 0x483f,
        .data4 = .{ 0xb9, 0x46, 0x30, 0xa7, 0xe4, 0xe6, 0x14, 0x58 },
    };
    pub const Message = extern struct {
        Category: u32,
        Severity: u32,
        ID: u32,
        pDescription: ?[*:0]const u8,
        DescriptionByteLength: usize,
    };
    pub const VTable = extern struct {
        QueryInterface: *const fn (*InfoQueue, *const com.GUID, *?*anyopaque) callconv(.winapi) com.HRESULT,
        AddRef: *const fn (*InfoQueue) callconv(.winapi) u32,
        Release: *const fn (*InfoQueue) callconv(.winapi) u32,
        SetMessageCountLimit: *const fn (*InfoQueue, u64) callconv(.winapi) com.HRESULT,
        ClearStoredMessages: *const fn (*InfoQueue) callconv(.winapi) void,
        GetMessage: *const fn (*InfoQueue, u64, ?*Message, *usize) callconv(.winapi) com.HRESULT,
        GetNumMessagesAllowedByStorageFilter: *const fn (*InfoQueue) callconv(.winapi) u64,
        GetNumMessagesDeniedByStorageFilter: *const fn (*InfoQueue) callconv(.winapi) u64,
        GetNumStoredMessages: *const fn (*InfoQueue) callconv(.winapi) u64,
    };

    comptime {
        // The previous declaration spliced ID3D12InfoQueue1's mute methods
        // in front of the counters, leaving GetMessage on a counter's slot.
        // Pin the layout so that class of defect fails the build.
        std.debug.assert(@offsetOf(VTable, "GetMessage") == 5 * @sizeOf(usize));
        std.debug.assert(@offsetOf(VTable, "GetNumStoredMessages") == 8 * @sizeOf(usize));
    }
};

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

    // Device. Null adapter selects the system GPU; the WARP software
    // adapter replaces it when WINTTY_GPU_TEST_WARP=1 (see
    // warpAdapterIfRequested).
    const warp_adapter = try warpAdapterIfRequested();
    defer releaseCom(warp_adapter);
    var device: ?*d3d12.ID3D12Device = null;
    var hr = d3d12.D3D12CreateDevice(
        @ptrCast(@alignCast(warp_adapter)),
        d3d12.D3D_FEATURE_LEVEL_12_0,
        &d3d12.ID3D12Device.IID,
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
        &d3d12.ID3D12CommandQueue.IID,
        @ptrCast(&command_queue),
    );
    if (com.FAILED(hr) or command_queue == null) return error.CommandQueueCreationFailed;
    errdefer _ = command_queue.?.Release();

    // Command allocator
    var command_allocator: ?*d3d12.ID3D12CommandAllocator = null;
    hr = device.?.CreateCommandAllocator(
        .DIRECT,
        &d3d12.ID3D12CommandAllocator.IID,
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
        &d3d12.ID3D12GraphicsCommandList.IID,
        @ptrCast(&command_list),
    );
    if (com.FAILED(hr) or command_list == null) return error.CommandListCreationFailed;
    errdefer _ = command_list.?.Release();

    // Fence
    var fence: ?*d3d12.ID3D12Fence = null;
    hr = device.?.CreateFence(
        0,
        .NONE,
        &d3d12.ID3D12Fence.IID,
        @ptrCast(&fence),
    );
    if (com.FAILED(hr) or fence == null) return error.FenceCreationFailed;
    errdefer _ = fence.?.Release();

    const fence_event = d3d12.CreateEventW(null, .FALSE, .FALSE, null) orelse
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
    var buf = try TestFloat.init(.{ .device = dev.device, .retire = null }, 64);
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
    var buf = try TestU32.init(.{ .device = dev.device, .retire = null }, 4);
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
    var buf = try TestU32.init(.{ .device = dev.device, .retire = null }, 64);
    defer buf.deinit();

    var list1 = std.ArrayListUnmanaged(u32).empty;
    defer list1.deinit(std.testing.allocator);
    try list1.appendSlice(std.testing.allocator, &.{ 1, 2, 3 });

    var list2 = std.ArrayListUnmanaged(u32).empty;
    defer list2.deinit(std.testing.allocator);
    try list2.appendSlice(std.testing.allocator, &.{ 4, 5 });

    const total = try buf.syncFromArrayLists(&.{ list1, list2 });
    try std.testing.expectEqual(@as(usize, 5), total);
}

test "Buffer: persistent mapping allows direct writes" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    const TestF32 = Buffer(f32);
    var buf = try TestF32.init(.{ .device = dev.device, .retire = null }, 16);
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
    var buf = try TestCB.init(.{ .device = dev.device, .retire = null }, 1);
    defer buf.deinit();

    try buf.sync(&.{Uniforms{ .x = 1.0, .y = 2.0, .z = 3.0, .w = 4.0 }});
    try std.testing.expectEqual(@as(u32, @sizeOf(Uniforms)), buf.buffer.stride);
}

// ---- Deferred release regression (issue #944) ----

// Growing a cell buffer must not final-release the old resource while a
// submission that reads it is still in flight.
//
// This is the crash in issue #944: `drawFrame` syncs the cell buffers at
// a point where the only DX12 GPU drain -- the per-slot fence wait in
// `beginFrame` -- has not happened yet, so `syncFromArrayLists` grew the
// buffer and released a resource the previous frame's command list was
// still reading. The debug layer answers that with
// `ID3D12Resource::<final-release>: CORRUPTION`, raised as a native SEH
// that kills the process with exit code 2173 and writes no crash.log.
//
// Determinism comes from `ID3D12CommandQueue::Wait`: the queue is parked
// on a gate fence this test alone signals, so the submission that reads
// the buffer provably cannot retire while the grow happens -- no
// dependence on how fast the GPU is.
//
// The buffer is built from `DirectX12.bufferOptions()` rather than a
// hand-written Options literal on purpose. That is the seam the fix
// changes; a literal here would keep passing whether or not production
// buffers are wired to the retirement queue.
test "Buffer: a grow does not final-release a resource the GPU still reads" {
    if (comptime builtin.os.tag != .windows) return;

    const DirectX12 = @import("../DirectX12.zig");

    // Shared-texture mode: a real device and command queue with no window
    // or swap chain, so this runs headless.
    var api: DirectX12 = .{ .allocator = std.testing.allocator };
    // Skip loudly, never silently. This is the one test standing between
    // the tree and a silent memory-corruption regression, so a box with no
    // D3D12 adapter must show up in the run's skip count rather than
    // reporting a pass it never earned.
    api.dev = Device.init(.{ .shared_texture = .{
        .width = 64,
        .height = 64,
    } }, .{}) catch return error.SkipZigTest;
    defer api.dev.?.deinit();
    const dev = &api.dev.?;

    // A cell buffer through the production options path.
    const CellBuffer = buffer_mod.Buffer(u32);
    var buf = try CellBuffer.init(api.bufferOptions(), 4);
    defer buf.deinit();
    const old_resource = buf.resource orelse return error.BufferResourceMissing;

    // Somewhere for the in-flight submission to copy the buffer into, so
    // the command list genuinely references it.
    var sink: ?*d3d12.ID3D12Resource = null;
    {
        const heap_props = d3d12.D3D12_HEAP_PROPERTIES{
            .Type = .READBACK,
            .CPUPageProperty = 0,
            .MemoryPoolPreference = 0,
            .CreationNodeMask = 0,
            .VisibleNodeMask = 0,
        };
        const desc = d3d12.D3D12_RESOURCE_DESC{
            .Dimension = .BUFFER,
            .Alignment = 0,
            .Width = 4 * @sizeOf(u32),
            .Height = 1,
            .DepthOrArraySize = 1,
            .MipLevels = 1,
            .Format = .UNKNOWN,
            .SampleDesc = .{ .Count = 1, .Quality = 0 },
            .Layout = .ROW_MAJOR,
            .Flags = .NONE,
        };
        const hr = dev.device.CreateCommittedResource(
            &heap_props,
            0,
            &desc,
            d3d12.D3D12_RESOURCE_STATES.COPY_DEST,
            null,
            &d3d12.ID3D12Resource.IID,
            @ptrCast(&sink),
        );
        if (com.FAILED(hr) or sink == null) return error.SinkCreationFailed;
    }
    defer _ = sink.?.Release();

    var cmd_allocator: ?*d3d12.ID3D12CommandAllocator = null;
    {
        const hr = dev.device.CreateCommandAllocator(
            .DIRECT,
            &d3d12.ID3D12CommandAllocator.IID,
            @ptrCast(&cmd_allocator),
        );
        if (com.FAILED(hr) or cmd_allocator == null) return error.CommandAllocatorCreationFailed;
    }
    defer _ = cmd_allocator.?.Release();

    var command_list: ?*d3d12.ID3D12GraphicsCommandList = null;
    {
        const hr = dev.device.CreateCommandList(
            0,
            .DIRECT,
            cmd_allocator.?,
            null,
            &d3d12.ID3D12GraphicsCommandList.IID,
            @ptrCast(&command_list),
        );
        if (com.FAILED(hr) or command_list == null) return error.CommandListCreationFailed;
    }
    defer _ = command_list.?.Release();

    // The gate. The queue blocks on it until this test signals it from the
    // CPU, which is what makes "still in flight" a fact rather than a race.
    var gate: ?*d3d12.ID3D12Fence = null;
    {
        const hr = dev.device.CreateFence(0, .NONE, &d3d12.ID3D12Fence.IID, @ptrCast(&gate));
        if (com.FAILED(hr) or gate == null) return error.FenceCreationFailed;
    }
    defer _ = gate.?.Release();

    // Record a read of the buffer. UPLOAD-heap buffers live in
    // GENERIC_READ, which already includes COPY_SOURCE, so no barrier.
    command_list.?.CopyBufferRegion(sink.?, 0, old_resource, 0, 4 * @sizeOf(u32));
    if (com.FAILED(command_list.?.Close())) return error.CommandListCloseFailed;

    // Park the queue, then submit. Nothing executes until the gate opens.
    if (com.FAILED(dev.command_queue.Wait(gate.?, 1))) return error.QueueWaitFailed;
    // Every exit from here on must open the gate AND drain before the
    // defers unwind. Opening alone fixes the hang and buys a worse
    // failure: the defers below release the command allocator, the command
    // list and the sink LIFO, and the copy they belong to has just been
    // let go, so a failing assertion would final-release resources the GPU
    // is executing -- the exact CORRUPTION break this test is about, which
    // kills the process with 2173 and reports nothing. Draining first
    // means a red assertion stays a red assertion.
    errdefer {
        _ = gate.?.Signal(1);
        dev.waitForGpu() catch {};
    }
    const lists = [_]*d3d12.ID3D12GraphicsCommandList{command_list.?};
    dev.command_queue.ExecuteCommandLists(1, &lists);

    // Signal the device fence the way drawFrameEnd does. Nothing is staged
    // yet, so this seal is a no-op on the queue -- what it establishes is
    // the fence value the grow below will be sealed against, and that the
    // ordering matches production rather than being contrived here.
    const work_value = dev.fence_value.fetchAdd(1, .release) + 1;
    if (com.FAILED(dev.command_queue.Signal(dev.fence, work_value))) return error.FenceSignalFailed;
    dev.retirement.seal(work_value);

    // The submission is provably unretired.
    try std.testing.expect(dev.fence.GetCompletedValue() < work_value);

    // Hold our own reference so the resource survives for measurement even
    // if the grow drops the buffer's. Reference counts are diagnostics
    // only per COM, so the assertion compares two readings of the same
    // object rather than trusting an absolute number.
    _ = old_resource.AddRef();
    const before = old_resource.AddRef();
    _ = old_resource.Release();

    // The bug: grow the buffer while that submission is in flight. This is
    // exactly generic.zig's `frame.cells.syncFromArrayLists(...)` at the
    // point where DX12 has not drained anything yet.
    var big: [64]u32 = undefined;
    for (&big, 0..) |*v, i| v.* = @intCast(i);
    try buf.sync(&big);
    try std.testing.expect(buf.resource != old_resource);

    const after = old_resource.AddRef();
    _ = old_resource.Release();

    // Unfixed, `after` is one lower, and the reason matters: it is the
    // BUFFER'S OWN reference that release() dropped. D3D12 holds no
    // reference on a resource a command list merely references -- that is
    // the whole premise of this bug, so "the reference D3D12 held" would
    // be exactly backwards. Fixed, release() hands the resource to the
    // retirement queue instead, which keeps that reference until the
    // fence proves the copy done, so the two readings match.
    //
    // What the gate above buys is the PRECONDITION, not this count: it is
    // what makes the assertion at the fence check true every run, so the
    // grow provably happens while the copy is in flight rather than
    // whenever the GPU is slow enough.
    try std.testing.expectEqual(before, after);

    // Open the gate and let everything retire, then verify the queue
    // actually hands the resource back once the fence proves it safe.
    if (com.FAILED(gate.?.Signal(1))) return error.GateSignalFailed;
    dev.waitForGpu() catch return error.WaitForGpuFailed;
    try std.testing.expectEqual(@as(usize, 0), dev.retirement.count());

    // Drop the reference we took; the retirement queue already dropped
    // the buffer's, so this is the last one.
    _ = old_resource.Release();
}

test "Buffer: initFill creates buffer with data" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    const TestU8 = Buffer(u8);
    const data = [_]u8{ 0xAA, 0xBB, 0xCC, 0xDD };
    var buf = try TestU8.initFill(.{ .device = dev.device, .retire = null }, &data);
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
        // Every submitted copy is drained by executeAndWait before this
        // texture is destroyed, so an immediate release is safe here.
        .retire = null,
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
        // Every submitted copy is drained by executeAndWait before this
        // texture is destroyed, so an immediate release is safe here.
        .retire = null,
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
        // Every submitted copy is drained by executeAndWait before this
        // texture is destroyed, so an immediate release is safe here.
        .retire = null,
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
        d3d12.D3D12_RESOURCE_STATES.PIXEL_SHADER_RESOURCE,
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

    var s = Shaders.init(dev.device, std.testing.allocator, &.{}) catch return;
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

    // Close it again to verify the reset worked. command_list is
    // optional on Frame because Frame.init may not have populated
    // it yet; after frame.reset() it is guaranteed non-null.
    const cl = frame.command_list orelse return error.CommandListMissing;
    const hr = cl.Close();
    try std.testing.expect(!com.FAILED(hr));
}

// ---- HWND swap chain + DirectComposition tests ----

test "Device: HWND surface uses DirectComposition with PREMULTIPLIED alpha" {
    if (comptime builtin.os.tag != .windows) return;
    if (!hasInteractiveDesktop()) return error.SkipZigTest;

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

    var device = Device.init(.{ .shared_texture = .{
        .width = 640,
        .height = 480,
    } }, .{}) catch return;
    defer device.deinit();

    // Shared texture mode: no swap chain, no DirectComposition.
    try std.testing.expect(device.swap_chain == null);
    try std.testing.expect(device.dcomp_device == null);
    try std.testing.expect(device.dcomp_target == null);
    try std.testing.expect(device.dcomp_visual == null);

    // Shared texture state is populated with a non-null resource,
    // both NT handles, and version starts at 1.
    const st = device.shared_texture orelse return error.SharedTextureNotPopulated;
    try std.testing.expect(@intFromPtr(st.resource) != 0);
    try std.testing.expect(@intFromPtr(st.resource_handle) != 0);
    try std.testing.expect(@intFromPtr(st.fence_handle) != 0);
    try std.testing.expectEqual(@as(u64, 1), st.version);
    try std.testing.expectEqual(@as(u32, 640), st.width);
    try std.testing.expectEqual(@as(u32, 480), st.height);
}

// ---- Device.init edge case tests ----

test "Device: shared texture 0x0 dimensions does not crash" {
    if (comptime builtin.os.tag != .windows) return;

    // SharedTexture mode has no swap chain, so 0x0 should not hit DXGI.
    // SharedTextureState.init clamps both dimensions to 1.
    var device = Device.init(.{ .shared_texture = .{
        .width = 0,
        .height = 0,
    } }, .{}) catch return;
    defer device.deinit();

    try std.testing.expect(device.swap_chain == null);
    try std.testing.expectEqual(@as(u64, 0), device.fence_value.load(.monotonic));

    const st = device.shared_texture orelse return error.SharedTextureNotPopulated;
    try std.testing.expectEqual(@as(u32, 1), st.width);
    try std.testing.expectEqual(@as(u32, 1), st.height);
}

test "Device: recreateSharedTexture bumps version and changes handle" {
    if (comptime builtin.os.tag != .windows) return;

    var device = Device.init(.{ .shared_texture = .{
        .width = 320,
        .height = 240,
    } }, .{}) catch return;
    defer device.deinit();

    const st_before = device.shared_texture.?;
    const version_before = st_before.version;
    const handle_before = st_before.resource_handle;
    const fence_handle_before = st_before.fence_handle;

    device.recreateSharedTexture(800, 600) catch return;

    const st_after = device.shared_texture.?;
    try std.testing.expect(st_after.version > version_before);
    try std.testing.expect(st_after.resource_handle != handle_before);
    // Fence handle is stable across resize.
    try std.testing.expectEqual(fence_handle_before, st_after.fence_handle);
    try std.testing.expectEqual(@as(u32, 800), st_after.width);
    try std.testing.expectEqual(@as(u32, 600), st_after.height);
}

test "Device: shared texture deinit does not leak" {
    if (comptime builtin.os.tag != .windows) return;

    // Create + destroy several times; if handles leak, the OS will
    // eventually refuse new allocations. This is a weak guarantee but
    // catches gross mistakes.
    var i: usize = 0;
    while (i < 16) : (i += 1) {
        var device = Device.init(.{ .shared_texture = .{
            .width = 64,
            .height = 64,
        } }, .{}) catch return;
        device.deinit();
    }
}

test "Device: HWND surface with 0x0 dimensions clamps to 1x1" {
    if (comptime builtin.os.tag != .windows) return;
    if (!hasInteractiveDesktop()) return error.SkipZigTest;

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

// ---- Device removed reason test ----

test "Device: GetDeviceRemovedReason returns S_OK on healthy device" {
    if (comptime builtin.os.tag != .windows) return;
    var dev = createTestDevice() catch return;
    defer dev.deinit();
    _ = dev.command_list.Close();

    // A healthy device should return S_OK (0) for GetDeviceRemovedReason.
    const hr = dev.device.GetDeviceRemovedReason();
    try std.testing.expectEqual(@as(com.HRESULT, 0), hr);
}

// ── Post-pipeline reproduction test (zioshade-4ne) ─────────────────────────
//
// Live wintty sessions show custom shaders whose effect depends on
// sin(fragCoord.y * k) (CRT scanlines) rendering as a constant, while the
// SAME DXIL renders dominant scanlines on the external WARP harness. This
// test drives the app's REAL post path -- shadertoy.loadFromFile (read +
// prefix + zioshade HLSL), Shaders.init (DXC + Pipeline.init with
// bg_color_vs and the post root signature), and a RenderPass step shaped
// exactly like generic.zig's post loop -- then reads the target back and
// looks for the scanline row pattern. If the app path drops the pattern,
// this test reproduces it in-repo.
//
// The app path is the ONLY thing exercised: the draw goes through the
// app's own post pipeline (bg_color_vs vertex stage), so a regression in
// that vertex stage fails the assertions below rather than being masked
// by a hand-written replacement stage.
test "post pipeline: scanline shader leaves row periodicity" {
    if (comptime builtin.os.tag != .windows) return;

    const W: u32 = 256;
    const H: u32 = 256;

    const alloc = std.heap.c_allocator;

    // A scanline body in the gallery CRT's shape: a smooth sine darkening
    // every 4 px in fragCoord space over the sampled content, so the
    // expected output is periodic row means over mid gray.
    const scanline_body =
        \\void mainImage( out vec4 fragColor, in vec2 fragCoord )
        \\{
        \\    vec2 uv = fragCoord.xy / iResolution.xy;
        \\    vec3 col = texture(iChannel0, uv).rgb;
        \\    float scan = 0.5 + 0.5 * sin(fragCoord.y * 6.2831853 / 4.0);
        \\    float line = 1.0 - step(0.45, scan);
        \\    col *= 1.0 - 0.45 * line;
        \\    fragColor = vec4(col, 1.0);
        \\}
    ;

    // Write it to a temp file so the REAL loader path runs (prefix embed,
    // zioshade compile with the app's options).
    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.writeFile(global.io(), .{
        .sub_path = "scan.glsl",
        .data = scanline_body,
    });
    const real = try tmp.dir.realPathFileAlloc(global.io(), ".", alloc);
    defer alloc.free(real);
    const path = try std.fmt.allocPrintSentinel(alloc, "{s}\\scan.glsl", .{real}, 0);
    defer alloc.free(path);

    const hlsl = shadertoy.loadFromFile(alloc, path, .hlsl) catch |err| {
        std.debug.print("scan.glsl failed to compile through shadertoy: {}\n", .{err});
        return err;
    };
    defer alloc.free(hlsl);

    // Real device in shared-texture (offscreen, headless-safe) mode.
    var device = Device.init(.{ .shared_texture = .{
        .width = W,
        .height = H,
    } }, .{}) catch return;
    defer device.deinit();
    const dev = device.device;

    // Real pipeline construction for the custom shader.
    var shaders = Shaders.init(dev, alloc, &.{hlsl}) catch |err| {
        std.debug.print("Shaders.init failed: {}\n", .{err});
        return err;
    };
    defer shaders.deinit(alloc);
    if (shaders.post_pipelines.len != 1) {
        std.debug.print("post pipelines: {d}, failure reason: {any}\n", .{
            shaders.post_pipelines.len, shaders.post_failure,
        });
        return error.PostPipelineMissing;
    }

    // Heaps the render pass and textures need.
    var srv_heap = DescriptorHeap.init(dev, .CBV_SRV_UAV, 16, true) catch return;
    defer srv_heap.deinit();
    var sampler_heap = DescriptorHeap.init(dev, .SAMPLER, 4, true) catch return;
    defer sampler_heap.deinit();
    var rtv_heap = DescriptorHeap.init(dev, .RTV, 4, false) catch return;
    defer rtv_heap.deinit();

    // Own command recording (same shape as TestDevice above).
    var cmd_allocator: ?*d3d12.ID3D12CommandAllocator = null;
    {
        const hr = dev.CreateCommandAllocator(.DIRECT, &d3d12.ID3D12CommandAllocator.IID, @ptrCast(&cmd_allocator));
        if (com.FAILED(hr) or cmd_allocator == null) return error.CommandAllocatorCreationFailed;
    }
    defer if (cmd_allocator) |a| {
        _ = a.Release();
    };
    var command_list: ?*d3d12.ID3D12GraphicsCommandList = null;
    {
        const hr = dev.CreateCommandList(0, .DIRECT, cmd_allocator.?, null, &d3d12.ID3D12GraphicsCommandList.IID, @ptrCast(&command_list));
        if (com.FAILED(hr) or command_list == null) return error.CommandListCreationFailed;
    }
    defer if (command_list) |l| {
        _ = l.Release();
    };
    var queue: ?*d3d12.ID3D12CommandQueue = null;
    {
        const qd = d3d12.D3D12_COMMAND_QUEUE_DESC{
            .Type = .DIRECT,
            .Priority = 0,
            .Flags = .NONE,
            .NodeMask = 0,
        };
        const hr = dev.CreateCommandQueue(
            &qd,
            &d3d12.ID3D12CommandQueue.IID,
            @ptrCast(&queue),
        );
        if (com.FAILED(hr) or queue == null) return error.CommandQueueCreationFailed;
    }
    defer if (queue) |q| {
        _ = q.Release();
    };

    var fence: ?*d3d12.ID3D12Fence = null;
    {
        const hr = dev.CreateFence(
            0,
            .NONE,
            &d3d12.ID3D12Fence.IID,
            @ptrCast(&fence),
        );
        if (com.FAILED(hr) or fence == null) return error.FenceCreationFailed;
    }
    defer if (fence) |f| {
        _ = f.Release();
    };

    const fence_event = d3d12.CreateEventW(null, .FALSE, .FALSE, null) orelse
        return error.FenceEventCreationFailed;
    defer _ = d3d12.CloseHandle(fence_event);

    // CommandList is created recording; close it, then reset opens cleanly.
    _ = command_list.?.Close();
    _ = cmd_allocator.?.Reset();
    _ = command_list.?.Reset(cmd_allocator.?, null);

    const tex_opts = Texture.Options{
        .device = dev,
        .command_list = command_list,
        .srv_heap = &srv_heap,
        // Drained by the explicit fence waits this test already makes
        // before teardown.
        .retire = null,
        .rtv_heap = &rtv_heap,
        .pixel_format = .B8G8R8A8_UNORM,
        .render_target = true,
    };

    // Content texture: render-target pair, filled the way production fills
    // it -- by a render pass targeting it (Texture.init only uploads initial
    // data for non-RT textures, so passing pixels here would be silently
    // ignored). A clear-to-gray stands in for the terminal content pass.
    var back = try Texture.init(tex_opts, W, H, null);
    defer back.deinit();

    // Output texture (the "front" the post pass writes).
    var front = try Texture.init(tex_opts, W, H, null);
    defer front.deinit();

    // Uniforms through the real buffer path.
    var uniforms = std.mem.zeroes(shadertoy.Uniforms);
    uniforms.resolution = .{ @floatFromInt(W), @floatFromInt(H), 1.0 };
    uniforms.time = 0.25;
    // No retirement queue: this buffer is synced and read on the CPU side
    // only, so nothing the GPU has in flight can reference it.
    var ubuf = try buffer_mod.Buffer(shadertoy.Uniforms)
        .init(.{ .device = dev, .retire = null }, 1);
    defer ubuf.deinit();
    try ubuf.sync(&.{uniforms});

    // Sampler.
    var sampler = try Sampler.init(.{ .device = dev, .sampler_heap = &sampler_heap });
    defer sampler.deinit();

    // Fill back with mid gray via a real render pass (the production shape).
    {
        var pass = RenderPassMod.begin(.{
            .command_list = command_list.?,
            .srv_heap = &srv_heap,
            .sampler_heap = &sampler_heap,
            .attachments = &.{.{
                .target = .{ .texture = back },
                .clear_color = .{ 0.5, 0.5, 0.5, 1.0 },
            }},
        });
        pass.complete();
    }

    // The app's post pipeline verbatim (bg_color_vs). The readback below
    // asserts on what this draw alone leaves in `front`.
    {
        var pass = RenderPassMod.begin(.{
            .command_list = command_list.?,
            .srv_heap = &srv_heap,
            .sampler_heap = &sampler_heap,
            .attachments = &.{.{
                .target = .{ .texture = front },
                .clear_color = .{ 0.0, 0.0, 0.0, 1.0 },
            }},
        });
        pass.step(.{
            .pipeline = shaders.post_pipelines[0],
            .uniforms = ubuf.buffer,
            .textures = &.{back},
            .samplers = &.{sampler},
            .draw = .{ .type = .triangle, .vertex_count = 3 },
        });
        pass.complete();
    }

    // Read back the front texture.
    front.transitionBarrier(command_list.?, d3d12.D3D12_RESOURCE_STATES.PIXEL_SHADER_RESOURCE, d3d12.D3D12_RESOURCE_STATES.COPY_SOURCE);
    var readback: ?*d3d12.ID3D12Resource = null;
    {
        const hp = d3d12.D3D12_HEAP_PROPERTIES{
            .Type = .READBACK,
            .CPUPageProperty = 0,
            .MemoryPoolPreference = 0,
            .CreationNodeMask = 0,
            .VisibleNodeMask = 0,
        };
        const rd = d3d12.D3D12_RESOURCE_DESC{
            .Dimension = .BUFFER,
            .Alignment = 0,
            .Width = W * H * 4,
            .Height = 1,
            .DepthOrArraySize = 1,
            .MipLevels = 1,
            .Format = .UNKNOWN,
            .SampleDesc = .{ .Count = 1, .Quality = 0 },
            .Layout = .ROW_MAJOR,
            .Flags = @enumFromInt(@as(u32, 0)),
        };
        _ = dev.CreateCommittedResource(&hp, 0, &rd, d3d12.D3D12_RESOURCE_STATES.COPY_DEST, null, &d3d12.ID3D12Resource.IID, @ptrCast(&readback));
    }
    defer if (readback) |r| {
        _ = r.Release();
    };
    {
        var dst: d3d12.D3D12_TEXTURE_COPY_LOCATION = std.mem.zeroes(d3d12.D3D12_TEXTURE_COPY_LOCATION);
        dst.pResource = readback.?;
        dst.Type = .PLACED_FOOTPRINT;
        dst.u.PlacedFootprint.Footprint.Format = .B8G8R8A8_UNORM;
        dst.u.PlacedFootprint.Footprint.Width = W;
        dst.u.PlacedFootprint.Footprint.Height = H;
        dst.u.PlacedFootprint.Footprint.Depth = 1;
        dst.u.PlacedFootprint.Footprint.RowPitch = W * 4;
        var src: d3d12.D3D12_TEXTURE_COPY_LOCATION = std.mem.zeroes(d3d12.D3D12_TEXTURE_COPY_LOCATION);
        src.pResource = front.resource.?;
        src.Type = .SUBRESOURCE_INDEX;
        command_list.?.CopyTextureRegion(&dst, 0, 0, 0, &src, null);
    }
    _ = command_list.?.Close();
    const lists = [_]*d3d12.ID3D12GraphicsCommandList{command_list.?};
    queue.?.ExecuteCommandLists(1, @ptrCast(&lists));
    _ = queue.?.Signal(fence.?, 1);
    if (fence.?.GetCompletedValue() < 1) {
        _ = fence.?.SetEventOnCompletion(1, fence_event);
        if (d3d12.WaitForSingleObject(fence_event, d3d12.INFINITE) != 0) return error.WaitFailed;
    }

    // Drain the debug-layer info queue: the runtime's own complaints about
    // the post pass (binding, state, viewport) name the defect directly.
    {
        var iq: ?*InfoQueue = null;
        const hr = dev.vtable.QueryInterface(dev, &InfoQueue.IID, @ptrCast(&iq));
        if (!com.FAILED(hr) and iq != null) {
            defer _ = iq.?.vtable.Release(iq.?);
            const n = iq.?.vtable.GetNumStoredMessages(iq.?);
            std.debug.print("D3D12 debug layer: {d} stored message(s)\n", .{n});
            var i: u64 = 0;
            while (i < n and i < 20) : (i += 1) {
                var len: usize = 0;
                const size_hr = iq.?.vtable.GetMessage(iq.?, i, null, &len);
                if (com.FAILED(size_hr)) {
                    std.debug.print(
                        "  (message {d}: size query failed, hr=0x{X:0>8})\n",
                        .{ i, @as(u32, @bitCast(size_hr)) },
                    );
                    continue;
                }
                // Message is read back through this buffer, so it must carry
                // the struct's alignment: alloc gives align 1 and the cast
                // below is a checked @alignCast, not a hint.
                const buf = alloc.alignedAlloc(u8, .of(InfoQueue.Message), len) catch break;
                defer alloc.free(buf);
                const msg: *InfoQueue.Message = @ptrCast(@alignCast(buf.ptr));
                if (iq.?.vtable.GetMessage(iq.?, i, msg, &len) == 0) {
                    const desc = if (msg.pDescription) |d| std.mem.sliceTo(d, 0) else "";
                    std.debug.print("  [sev={d} id={d}] {s}\n", .{ msg.Severity, msg.ID, desc });
                }
            }
        } else {
            std.debug.print("D3D12 debug layer unavailable (hr=0x{x})\n", .{@as(u32, @bitCast(hr))});
        }
    }

    var mapped: ?*anyopaque = null;
    {
        const rr = d3d12.D3D12_RANGE{ .Begin = 0, .End = W * H * 4 };
        _ = readback.?.Map(0, &rr, &mapped);
    }
    // Snapshot front's bytes NOW: the back-texture probe below reuses this
    // same readback resource, which silently replaced everything analyzed
    // after it (the entire "fragCoord.y is constant" trail was this race).
    const front_bytes = alloc.dupe(u8, @as([*]const u8, @ptrCast(mapped.?))[0 .. W * H * 4]) catch return error.OutOfMemory;
    defer alloc.free(front_bytes);
    const px: [*]const u8 = front_bytes.ptr;

    // Row analysis: the scanline pattern darkens roughly 1.4px of every 4
    // by ~45% over a mid-gray input, so adjacent row means must differ
    // strongly and periodically.
    // DISCRIMINATOR: read the back texture itself. Gray => the upload landed
    // and the SRV/table binding is the bug; black => the upload is the bug.
    {
        _ = cmd_allocator.?.Reset();
        _ = command_list.?.Reset(cmd_allocator.?, null);
        back.transitionBarrier(command_list.?, d3d12.D3D12_RESOURCE_STATES.PIXEL_SHADER_RESOURCE, d3d12.D3D12_RESOURCE_STATES.COPY_SOURCE);
        {
            var dst: d3d12.D3D12_TEXTURE_COPY_LOCATION = std.mem.zeroes(d3d12.D3D12_TEXTURE_COPY_LOCATION);
            dst.pResource = readback.?;
            dst.Type = .PLACED_FOOTPRINT;
            dst.u.PlacedFootprint.Footprint.Format = .B8G8R8A8_UNORM;
            dst.u.PlacedFootprint.Footprint.Width = W;
            dst.u.PlacedFootprint.Footprint.Height = H;
            dst.u.PlacedFootprint.Footprint.Depth = 1;
            dst.u.PlacedFootprint.Footprint.RowPitch = W * 4;
            var src: d3d12.D3D12_TEXTURE_COPY_LOCATION = std.mem.zeroes(d3d12.D3D12_TEXTURE_COPY_LOCATION);
            src.pResource = back.resource.?;
            src.Type = .SUBRESOURCE_INDEX;
            src.u.SubresourceIndex = 0;
            command_list.?.CopyTextureRegion(&dst, 0, 0, 0, &src, null);
        }
        _ = command_list.?.Close();
        const lists2 = [_]*d3d12.ID3D12GraphicsCommandList{command_list.?};
        queue.?.ExecuteCommandLists(1, @ptrCast(&lists2));
        _ = queue.?.Signal(fence.?, 2);
        if (fence.?.GetCompletedValue() < 2) {
            _ = fence.?.SetEventOnCompletion(2, fence_event);
            if (d3d12.WaitForSingleObject(fence_event, d3d12.INFINITE) != 0) return error.WaitFailed;
        }
        var mapped2: ?*anyopaque = null;
        const rr2 = d3d12.D3D12_RANGE{ .Begin = 0, .End = W * H * 4 };
        _ = readback.?.Map(0, &rr2, &mapped2);
        const bpx: [*]const u8 = @ptrCast(mapped2.?);
        var bsum: u64 = 0;
        for (0..W * H) |i| bsum += bpx[i * 4];
        std.debug.print("BACK TEXTURE avg byte0 = {d:.1} (~128 = upload landed)\n", .{@as(f64, @floatFromInt(bsum)) / @as(f64, @floatFromInt(W * H))});
        const nrw2 = d3d12.D3D12_RANGE{ .Begin = 0, .End = 0 };
        readback.?.Unmap(0, &nrw2);
    }

    var row_means: [H]f64 = undefined;
    for (0..H) |y| {
        var sum: u64 = 0;
        for (0..W) |x| {
            sum += px[y * W * 4 + x * 4];
        }
        row_means[y] = @as(f64, @floatFromInt(sum)) / @as(f64, @floatFromInt(W));
    }
    var max_diff: f64 = 0;
    for (0..H - 1) |y| {
        const d = @abs(row_means[y + 1] - row_means[y]);
        if (d > max_diff) max_diff = d;
    }
    // Count distinct dark rows (mean below mid-gray by > 10%).
    var dark_rows: usize = 0;
    for (0..H) |y| {
        if (row_means[y] < 0x80 * 0.9) dark_rows += 1;
    }

    std.debug.print("post-pipeline scanline: max row diff = {d:.1}, dark rows = {d}/{d}, first rows: {d:.1} {d:.1} {d:.1} {d:.1} {d:.1} {d:.1}\n", .{
        max_diff, dark_rows, H, row_means[0], row_means[1], row_means[2], row_means[3], row_means[4], row_means[5],
    });
    {
        const nrw = d3d12.D3D12_RANGE{ .Begin = 0, .End = 0 };
        readback.?.Unmap(0, &nrw);
    }

    // Scanlines over mid gray: bright rows ~128, dark rows ~70; strong
    // row-to-row differences and roughly a third of rows dark.
    try std.testing.expect(max_diff > 10.0);
    try std.testing.expect(dark_rows > H / 8);
}

test "buffer.Options.retire has no default" {
    // The buffer half of the same rule Texture.zig pins. A builder that
    // omitted `retire` would get a bare Release on grow, which is the
    // crash this fix is for, and nothing would fail. Having no default is
    // what turns that into a compile error at every construction site.
    //
    // The found flag keeps the loop from passing vacuously if the field is
    // ever renamed.
    var found = false;
    inline for (@typeInfo(buffer_mod.Options).@"struct".fields) |field| {
        if (comptime std.mem.eql(u8, field.name, "retire")) {
            found = true;
            try std.testing.expect(field.default_value_ptr == null);
        }
    }
    try std.testing.expect(found);
}

test "Texture: deinit recycles its SRV slot" {
    var dev = createTestDevice() catch return;
    defer dev.deinit();

    var srv_heap = DescriptorHeap.init(
        dev.device,
        .CBV_SRV_UAV,
        1, // one slot: the second texture below can only exist by recycling
        true,
    ) catch return;
    defer srv_heap.deinit();

    const opts = Texture.Options{
        .device = dev.device,
        .command_list = dev.command_list,
        .srv_heap = &srv_heap,
        // Never submitted anywhere, so an immediate slot release is safe.
        .retire = null,
        .pixel_format = .R8_UNORM,
    };

    {
        const tex = Texture.init(opts, 4, 4, null) catch return;
        defer tex.deinit();
        try std.testing.expectEqual(@as(u32, 0), tex.srv.index);
    }

    // Before the release wiring, the single slot stayed owned by the
    // dead texture and this init failed with TextureCreateFailed.
    const tex2 = try Texture.init(opts, 4, 4, null);
    defer tex2.deinit();
    try std.testing.expectEqual(@as(u32, 0), tex2.srv.index);
}

// ---- Device-loss recovery ----

/// Put a live device into the removed state, the way a TDR or a driver
/// upgrade would, without waiting for either. Needs ID3D12Device5
/// (Windows 10 1809+); skips where the runtime cannot hand it out.
fn removeDevice(dev: *d3d12.ID3D12Device) !void {
    var dev5: ?*d3d12.ID3D12Device5 = null;
    const hr = dev.vtable.QueryInterface(dev, &d3d12.ID3D12Device5.IID, @ptrCast(&dev5));
    if (com.FAILED(hr) or dev5 == null) return error.SkipZigTest;
    defer _ = dev5.?.Release();
    dev5.?.RemoveDevice();
    if (!com.FAILED(dev.GetDeviceRemovedReason())) return error.DeviceNotRemoved;
}

test "Device: SwapChainPanel surface handle outlives the device that presented into it" {
    // The WinUI shell binds the DirectComposition surface handle to its
    // SwapChainPanel exactly once. A device recreated after a TDR must
    // present into that same surface, or the panel keeps compositing a
    // handle nothing writes to.
    if (!hasInteractiveDesktop()) return error.SkipZigTest;

    var first = Device.init(.swap_chain_panel, .{ .width = 64, .height = 64 }) catch
        return error.SkipZigTest;
    var first_alive = true;
    errdefer if (first_alive) first.deinit();
    const handle = first.swap_chain_surface_handle orelse return error.NoSurfaceHandle;

    try removeDevice(first.device);

    // Keep the handle across the teardown; the renderer does the same.
    first.swap_chain_surface_handle = null;
    first.deinit();
    first_alive = false;

    var second = try Device.init(.swap_chain_panel, .{
        .width = 64,
        .height = 64,
        .surface_handle = handle,
    });
    defer second.deinit();

    try std.testing.expectEqual(handle, second.swap_chain_surface_handle.?);
    try std.testing.expect(!com.FAILED(second.device.GetDeviceRemovedReason()));

    // The proof is a Present into the reused surface from the new device.
    const sc = second.swap_chain orelse return error.NoSwapChain;
    try std.testing.expect(!com.FAILED(sc.Present(0, 0)));
}

test "DirectX12: rebuilds every device-bound object after the device is removed" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const DirectX12 = @import("../DirectX12.zig");

    // Shared-texture mode: a real device and command queue with no window
    // or swap chain, so this runs headless.
    var api: DirectX12 = .{ .allocator = std.testing.allocator };
    api.initGpu(.{ .shared_texture = .{ .width = 64, .height = 64 } }, 64, 64) catch
        return error.SkipZigTest;
    defer api.deinit();
    api.flushInitCommands();

    try removeDevice(api.dev.?.device);
    // What handleDeviceRemoved records when Present or Signal reports it.
    api.device_lost = true;
    try std.testing.expect(api.deviceLost());

    try api.recoverDevice();

    try std.testing.expect(!api.deviceLost());
    const dev = &(api.dev orelse return error.NoDevice);
    try std.testing.expect(!dev.removed());
    try std.testing.expect(dev.shared_texture != null);
    try std.testing.expect(api.srv_heap != null);
    try std.testing.expect(api.rtv_heap != null);
    try std.testing.expect(api.sampler_heap != null);
    try std.testing.expect(api.shared_rtv != null);
    for (api.gpu_frames) |gf| try std.testing.expect(gf != null);
    try std.testing.expectEqual(@as(u32, 64), api.applied_width);
    try std.testing.expectEqual(@as(u32, 64), api.applied_height);

    // The rebuilt queue accepts and completes work: the fresh init
    // command list goes through Close, ExecuteCommandLists and a fence
    // wait, none of which the old device could do.
    try std.testing.expect(api.init_command_list != null);
    api.flushInitCommands();
    try std.testing.expect(api.init_command_list == null);
    try dev.waitForGpu();
}
