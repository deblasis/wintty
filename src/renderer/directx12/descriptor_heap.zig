//! Descriptor heap management for DX12.
//!
//! Wraps ID3D12DescriptorHeap with a linear allocator that hands out
//! the next free slot. Each heap tracks CPU and GPU base handles plus
//! the per-descriptor increment size so callers can index into the heap
//! without querying the device repeatedly.
//!
//! Callers typically create three heaps:
//! - CBV/SRV/UAV (shader-visible): constant buffers, textures
//! - Sampler (shader-visible): texture samplers
//! - RTV (non-shader-visible): render target views
const DescriptorHeap = @This();

const std = @import("std");
const builtin = @import("builtin");

const com = @import("com.zig");
const d3d12 = @import("d3d12.zig");

const HRESULT = com.HRESULT;
const FAILED = com.FAILED;

const log = std.log.scoped(.directx12);

heap: *d3d12.ID3D12DescriptorHeap,
cpu_start: d3d12.D3D12_CPU_DESCRIPTOR_HANDLE,
gpu_start: d3d12.D3D12_GPU_DESCRIPTOR_HANDLE,
increment_size: u32,
capacity: u32,
allocated: u32,

pub const Descriptor = struct {
    cpu: d3d12.D3D12_CPU_DESCRIPTOR_HANDLE,
    gpu: d3d12.D3D12_GPU_DESCRIPTOR_HANDLE,
    index: u32,
};

pub fn init(
    device: *d3d12.ID3D12Device,
    heap_type: d3d12.D3D12_DESCRIPTOR_HEAP_TYPE,
    count: u32,
    shader_visible: bool,
) !DescriptorHeap {
    const desc = d3d12.D3D12_DESCRIPTOR_HEAP_DESC{
        .Type = heap_type,
        .NumDescriptors = count,
        .Flags = if (shader_visible) .SHADER_VISIBLE else .NONE,
        .NodeMask = 0,
    };

    var heap: ?*d3d12.ID3D12DescriptorHeap = null;
    const hr = device.CreateDescriptorHeap(
        &desc,
        &d3d12.ID3D12DescriptorHeap.IID,
        @ptrCast(&heap),
    );
    if (FAILED(hr)) {
        log.err("CreateDescriptorHeap failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        return error.DescriptorHeapCreationFailed;
    }

    const h = heap.?;
    const cpu_start = h.GetCPUDescriptorHandleForHeapStart();
    const gpu_start = if (shader_visible)
        h.GetGPUDescriptorHandleForHeapStart()
    else
        d3d12.D3D12_GPU_DESCRIPTOR_HANDLE{ .ptr = 0 };

    const increment_size = device.GetDescriptorHandleIncrementSize(heap_type);

    return .{
        .heap = h,
        .cpu_start = cpu_start,
        .gpu_start = gpu_start,
        .increment_size = increment_size,
        .capacity = count,
        .allocated = 0,
    };
}

pub fn deinit(self: *DescriptorHeap) void {
    _ = self.heap.Release();
}

/// Reset the allocator so all slots can be reused. Does not invalidate
/// existing descriptors -- the caller must ensure the GPU is done with
/// them before calling this.
pub fn reset(self: *DescriptorHeap) void {
    self.allocated = 0;
}

/// Allocate the next descriptor slot. Returns the CPU/GPU handles and index.
pub fn allocate(self: *DescriptorHeap) !Descriptor {
    if (self.allocated >= self.capacity) {
        return error.DescriptorHeapFull;
    }
    const index = self.allocated;
    self.allocated += 1;
    return .{
        .cpu = self.cpuHandle(index),
        .gpu = self.gpuHandle(index),
        .index = index,
    };
}

/// CPU handle for a given slot index.
pub fn cpuHandle(self: *const DescriptorHeap, index: u32) d3d12.D3D12_CPU_DESCRIPTOR_HANDLE {
    std.debug.assert(index < self.capacity);
    return .{
        .ptr = self.cpu_start.ptr + @as(usize, index) * @as(usize, self.increment_size),
    };
}

/// GPU handle for a given slot index. Returns a zeroed handle for
/// non-shader-visible heaps (e.g. RTV) where GPU handles are meaningless.
pub fn gpuHandle(self: *const DescriptorHeap, index: u32) d3d12.D3D12_GPU_DESCRIPTOR_HANDLE {
    std.debug.assert(index < self.capacity);
    if (self.gpu_start.ptr == 0) return .{ .ptr = 0 };
    return .{
        .ptr = self.gpu_start.ptr + @as(u64, index) * @as(u64, self.increment_size),
    };
}

// --- Tests ---

test "DescriptorHeap struct fields" {
    try std.testing.expect(@hasField(DescriptorHeap, "heap"));
    try std.testing.expect(@hasField(DescriptorHeap, "cpu_start"));
    try std.testing.expect(@hasField(DescriptorHeap, "gpu_start"));
    try std.testing.expect(@hasField(DescriptorHeap, "increment_size"));
    try std.testing.expect(@hasField(DescriptorHeap, "capacity"));
    try std.testing.expect(@hasField(DescriptorHeap, "allocated"));
}

test "Descriptor struct fields" {
    try std.testing.expect(@hasField(Descriptor, "cpu"));
    try std.testing.expect(@hasField(Descriptor, "gpu"));
    try std.testing.expect(@hasField(Descriptor, "index"));
}

test "cpuHandle and gpuHandle offset correctly" {
    // Simulate a heap with known base handles and increment size.
    // We can't call init() without a real device, but the handle math
    // is pure arithmetic we can verify directly.
    var heap: DescriptorHeap = undefined;
    heap.cpu_start = .{ .ptr = 0x1000 };
    heap.gpu_start = .{ .ptr = 0x2000 };
    heap.increment_size = 32;
    heap.capacity = 10;
    heap.allocated = 0;

    const h0 = heap.cpuHandle(0);
    try std.testing.expectEqual(@as(usize, 0x1000), h0.ptr);

    const h3 = heap.cpuHandle(3);
    try std.testing.expectEqual(@as(usize, 0x1000 + 3 * 32), h3.ptr);

    const g5 = heap.gpuHandle(5);
    try std.testing.expectEqual(@as(u64, 0x2000 + 5 * 32), g5.ptr);
}

test "allocate increments and respects capacity" {
    var heap: DescriptorHeap = undefined;
    heap.cpu_start = .{ .ptr = 0x1000 };
    heap.gpu_start = .{ .ptr = 0x2000 };
    heap.increment_size = 64;
    heap.capacity = 2;
    heap.allocated = 0;

    const d0 = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 0), d0.index);
    try std.testing.expectEqual(@as(usize, 0x1000), d0.cpu.ptr);

    const d1 = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 1), d1.index);
    try std.testing.expectEqual(@as(usize, 0x1000 + 64), d1.cpu.ptr);

    // Heap is full -- next allocate should fail.
    try std.testing.expectError(error.DescriptorHeapFull, heap.allocate());
}

test "gpuHandle returns zero for non-shader-visible heap" {
    // RTV heaps have gpu_start zeroed since they're not shader-visible.
    var heap: DescriptorHeap = undefined;
    heap.cpu_start = .{ .ptr = 0x1000 };
    heap.gpu_start = .{ .ptr = 0 };
    heap.increment_size = 32;
    heap.capacity = 10;
    heap.allocated = 0;

    const g = heap.gpuHandle(3);
    try std.testing.expectEqual(@as(u64, 0), g.ptr);
}

test "reset allows reuse of descriptor slots" {
    var heap: DescriptorHeap = undefined;
    heap.cpu_start = .{ .ptr = 0x1000 };
    heap.gpu_start = .{ .ptr = 0x2000 };
    heap.increment_size = 64;
    heap.capacity = 1;
    heap.allocated = 0;

    // Exhaust the heap.
    const d0 = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 0), d0.index);
    try std.testing.expectError(error.DescriptorHeapFull, heap.allocate());

    // Reset and allocate again.
    heap.reset();
    try std.testing.expectEqual(@as(u32, 0), heap.allocated);
    const d1 = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 0), d1.index);
}
