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
pub const DescriptorHeap = @This();

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
/// Device the heap was created on, kept so release() can rewrite freed
/// slots of a shader-visible CBV/SRV/UAV heap as null SRVs. Null in the
/// hand-built heaps of unit tests, where release() skips the rewrite.
device: ?*d3d12.ID3D12Device = null,
/// True for the shader-visible CBV/SRV/UAV heaps whose slots GPU tables
/// can cover. Frees on such heaps rewrite the null SRV; sampler and RTV
/// heaps have no stale-descriptor hazard (samplers hold plain state, RTV
/// heaps are CPU-side only).
shader_visible_srv: bool = false,

/// Occupancy mask for slots below `allocated`, one bit per slot. A set
/// bit means the slot is live; a clear bit below `allocated` is a freed
/// slot `allocate` will hand out again before touching fresh ones. Slots
/// at or above `allocated` are implicitly free (and hold null SRVs from
/// init, so binding past the frontier is defined).
///
/// u128 bounds the recyclable capacity at 128 slots. Every heap this
/// renderer creates is far below that (64 SRV / 16 sampler / 9 RTV);
/// init asserts rather than silently not recycling.
free_mask: u128 = 0,

/// Slot reserved for the second half of an atlas SRV pair, between the
/// two initAtlasTexture calls that consume it. The cell pass binds
/// grayscale+color as ONE descriptor-table range, so the pair must occupy
/// adjacent slots; the first call allocates them together and parks the
/// partner index here because the shared initAtlasTexture signature is
/// *const self (the heap is already behind a pointer, so it is the
/// mutable home). A stale value from an aborted pair is released before
/// reuse -- it was never bound, so immediate release is safe. Only
/// touched under the renderer's draw mutex, like every other heap
/// mutation. SRV heap only.
atlas_partner: ?u32 = null,

pub const Descriptor = struct {
    cpu: d3d12.D3D12_CPU_DESCRIPTOR_HANDLE,
    gpu: d3d12.D3D12_GPU_DESCRIPTOR_HANDLE,
    index: u32,
};

/// Create a descriptor heap with a linear allocator over its slots.
///
/// A shader-visible CBV/SRV/UAV heap comes back with every slot already
/// holding a null SRV, so binding a descriptor table that reaches past the
/// slots handed out so far is defined rather than a validation error. See
/// the null_srv block below.
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

    const self: DescriptorHeap = .{
        .heap = h,
        .cpu_start = cpu_start,
        .gpu_start = gpu_start,
        .increment_size = increment_size,
        .capacity = count,
        .allocated = 0,
        .device = device,
        .shader_visible_srv = shader_visible and heap_type == .CBV_SRV_UAV,
    };

    // D3D12 requires every descriptor in a range that is not
    // DESCRIPTORS_VOLATILE to be initialized before the table is set, not
    // just the ones a shader samples. RenderPass binds the srv_table_size
    // table from a single texture's slot, so the slots behind it hold
    // whatever was allocated next, which is nothing at all when that texture
    // is the most recent one created, and every such bind failed validation
    // with INVALID_DESCRIPTOR_HANDLE.
    //
    // Filling here rather than in allocate() is the point: the slots that
    // were failing are the ones no texture ever claims. A null SRV reads as
    // zeros, so an unused slot is defined rather than merely quiet.
    if (shader_visible and heap_type == .CBV_SRV_UAV) {
        const null_srv = d3d12.D3D12_SHADER_RESOURCE_VIEW_DESC{
            .Format = .R8G8B8A8_UNORM,
            .ViewDimension = .TEXTURE2D,
            .Shader4ComponentMapping = d3d12.D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING,
            .u = .{ .Texture2D = .{
                .MostDetailedMip = 0,
                .MipLevels = 1,
                .PlaneSlice = 0,
                .ResourceMinLODClamp = 0.0,
            } },
        };
        for (0..count) |i| {
            device.CreateShaderResourceView(null, &null_srv, self.cpuHandle(@intCast(i)));
        }
    }

    return self;
}

pub fn deinit(self: *DescriptorHeap) void {
    _ = self.heap.Release();

    self.* = undefined;
}

/// Reset the allocator so all slots can be reused. Does not invalidate
/// existing descriptors -- the caller must ensure the GPU is done with
/// them before calling this.
pub fn reset(self: *DescriptorHeap) void {
    self.allocated = 0;
    self.free_mask = 0;
}

/// Claim the first `n` slots outright, for callers that write descriptors
/// by handle rather than through allocate() (swap chain back buffers are
/// the case: their RTVs land in slots 0..n by construction). Writing
/// `allocated` directly is no longer valid bookkeeping -- the free mask
/// would disagree and allocate() would hand a live slot to someone else.
pub fn claimFirst(self: *DescriptorHeap, n: u32) void {
    std.debug.assert(n <= self.capacity);
    self.allocated = n;
    var i: u32 = 0;
    while (i < n) : (i += 1) {
        self.free_mask |= @as(u128, 1) << @intCast(i);
    }
}

/// Allocate the next descriptor slot, preferring a recycled slot over a
/// fresh one. Returns the CPU/GPU handles and index.
pub fn allocate(self: *DescriptorHeap) !Descriptor {
    // Recycle the lowest freed slot below the high-water mark first.
    const below_high_water: u128 = if (self.allocated >= 128)
        std.math.maxInt(u128)
    else
        (@as(u128, 1) << @intCast(self.allocated)) - 1;
    const free_below = ~self.free_mask & below_high_water;
    if (free_below != 0) {
        const index: u32 = @ctz(free_below);
        self.free_mask |= @as(u128, 1) << @intCast(index);
        return .{
            .cpu = self.cpuHandle(index),
            .gpu = self.gpuHandle(index),
            .index = index,
        };
    }

    if (self.allocated >= self.capacity) {
        return error.DescriptorHeapFull;
    }
    const index = self.allocated;
    self.allocated += 1;
    self.free_mask |= @as(u128, 1) << @intCast(index);
    return .{
        .cpu = self.cpuHandle(index),
        .gpu = self.gpuHandle(index),
        .index = index,
    };
}

/// Return a slot for reuse. The caller must guarantee the GPU no longer
/// reads a binding that covers this slot (route the release through the
/// retirement queue alongside the resource it described).
pub fn release(self: *DescriptorHeap, index: u32) void {
    std.debug.assert(index < self.allocated);
    self.free_mask &= ~(@as(u128, 1) << @intCast(index));

    // Rewrite the freed slot as a null SRV, restoring the invariant init
    // established: every unclaimed slot of a shader-visible SRV heap
    // holds a defined descriptor. A recycled-but-not-yet-reallocated slot
    // otherwise still points at the resource whose release this release
    // accompanies, and any table that covers it would sample freed
    // memory. Sampler/RTV heaps have no such hazard and no device.
    if (self.shader_visible_srv) {
        if (self.device) |device| {
            const null_srv = d3d12.D3D12_SHADER_RESOURCE_VIEW_DESC{
                .Format = .R8G8B8A8_UNORM,
                .ViewDimension = .TEXTURE2D,
                .Shader4ComponentMapping = d3d12.D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING,
                .u = .{ .Texture2D = .{
                    .MostDetailedMip = 0,
                    .MipLevels = 1,
                    .PlaneSlice = 0,
                    .ResourceMinLODClamp = 0.0,
                } },
            };
            device.CreateShaderResourceView(null, &null_srv, self.cpuHandle(index));
        }
    }
}

/// Allocate `n` adjacent slots, returning the first. Descriptor tables
/// bind a range from one base handle, so textures that a single table
/// must cover (the atlas pair) have to occupy consecutive slots; per-slot
/// recycling makes that an explicit request rather than a bump-order
/// accident.
pub fn allocateContiguous(self: *DescriptorHeap, n: u32) !Descriptor {
    std.debug.assert(n > 0);
    const total: u32 = @min(self.allocated +| n, self.capacity);
    if (total <= self.allocated and self.allocated >= self.capacity) {
        return error.DescriptorHeapFull;
    }

    // Scan for a run of n free slots: recycled holes below the
    // high-water mark, or the frontier itself. Capacity is small (64
    // shader-visible slots at most), so the scan is cheaper than keeping
    // a second free-run index in sync.
    var run: u32 = 0;
    var i: u32 = 0;
    while (i < total) : (i += 1) {
        const occupied = (i < self.allocated) and
            (self.free_mask & (@as(u128, 1) << @intCast(i))) != 0;
        if (occupied) {
            run = 0;
            continue;
        }
        run += 1;
        if (run == n) {
            const start = i + 1 - n;
            var j = start;
            while (j <= i) : (j += 1) {
                self.free_mask |= @as(u128, 1) << @intCast(j);
            }
            if (i + 1 > self.allocated) self.allocated = i + 1;
            return .{
                .cpu = self.cpuHandle(start),
                .gpu = self.gpuHandle(start),
                .index = start,
            };
        }
    }
    return error.DescriptorHeapFull;
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

/// A heap with fake bases for the pure-allocation tests. init() needs a
/// real device; the allocator logic below only touches the fields set here.
fn testHeap(capacity: u32, increment: u32) DescriptorHeap {
    return .{
        .heap = undefined,
        .cpu_start = .{ .ptr = 0x1000 },
        .gpu_start = .{ .ptr = 0x2000 },
        .increment_size = increment,
        .capacity = capacity,
        .allocated = 0,
        .free_mask = 0,
    };
}

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
    var heap = testHeap(10, 32);

    const h0 = heap.cpuHandle(0);
    try std.testing.expectEqual(@as(usize, 0x1000), h0.ptr);

    const h3 = heap.cpuHandle(3);
    try std.testing.expectEqual(@as(usize, 0x1000 + 3 * 32), h3.ptr);

    const g5 = heap.gpuHandle(5);
    try std.testing.expectEqual(@as(u64, 0x2000 + 5 * 32), g5.ptr);
}

test "allocate increments and respects capacity" {
    var heap = testHeap(2, 64);

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
    var heap = testHeap(10, 32);
    heap.gpu_start = .{ .ptr = 0 };

    const g = heap.gpuHandle(3);
    try std.testing.expectEqual(@as(u64, 0), g.ptr);
}

test "reset allows reuse of descriptor slots" {
    var heap = testHeap(1, 64);

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

test "release recycles a slot before fresh ones" {
    var heap = testHeap(2, 64);

    const d0 = try heap.allocate();
    const d1 = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 0), d0.index);
    try std.testing.expectEqual(@as(u32, 1), d1.index);
    try std.testing.expectError(error.DescriptorHeapFull, heap.allocate());

    // Returning d0's slot makes exactly that slot available again; a
    // full heap without recycling stays exhausted forever, which is the
    // leak the free list exists to close.
    heap.release(d0.index);
    const d2 = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 0), d2.index);
    try std.testing.expectEqual(@as(usize, 0x1000), d2.cpu.ptr);
    try std.testing.expectError(error.DescriptorHeapFull, heap.allocate());
}

test "release then reset clears the recycled state" {
    var heap = testHeap(2, 64);

    _ = try heap.allocate();
    const d1 = try heap.allocate();
    heap.release(d1.index);
    heap.reset();

    // After reset the allocator starts over; slot 0 comes out first.
    const d = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 0), d.index);
}

test "allocateContiguous returns adjacent slots at the frontier" {
    var heap = testHeap(4, 64);

    const pair = try heap.allocateContiguous(2);
    try std.testing.expectEqual(@as(u32, 0), pair.index);
    try std.testing.expectEqual(@as(usize, 0x1000), pair.cpu.ptr);
    try std.testing.expectEqual(@as(u64, 0x2000), pair.gpu.ptr);

    // The pair's partner is the very next slot, and the next single
    // allocation lands after the pair.
    const single = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 2), single.index);
}

test "allocateContiguous fills an adjacent recycled pair" {
    var heap = testHeap(4, 64);

    // Occupy 0, 1, 2; then free 0 and 2, leaving holes at 0 and 2 with
    // the frontier at 3. Slot 1 stays live throughout.
    const d0 = try heap.allocate();
    _ = try heap.allocate(); // slot 1, never released
    const d2 = try heap.allocate();
    heap.release(d0.index);
    heap.release(d2.index);

    // Slot 2's hole plus the frontier slot 3 form an adjacent run, and
    // that is the pair the allocator takes: recycled hole first, only
    // one slot of fresh capacity spent.
    const pair = try heap.allocateContiguous(2);
    try std.testing.expectEqual(@as(u32, 2), pair.index);

    // The remaining hole 0 recycles for the next single allocation.
    const next = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 0), next.index);

    // With 0..3 all live again, freeing slots 0 and 2 leaves two
    // NON-adjacent holes and no frontier: the request must fail rather
    // than split a pair across them.
    heap.release(next.index);
    heap.release(pair.index);
    try std.testing.expectError(error.DescriptorHeapFull, heap.allocateContiguous(2));

    // Those two single holes still serve single allocations.
    const single = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 0), single.index);
}

test "allocateContiguous fails when only non-adjacent holes remain" {
    var heap = testHeap(2, 64);

    const d0 = try heap.allocate();
    const d1 = try heap.allocate();
    heap.release(d0.index);
    heap.release(d1.index);

    // Holes 0 and 1 are adjacent, so the pair fits.
    const pair = try heap.allocateContiguous(2);
    try std.testing.expectEqual(@as(u32, 0), pair.index);

    // Full again, no frontier left: single and pair both fail.
    try std.testing.expectError(error.DescriptorHeapFull, heap.allocateContiguous(2));
    try std.testing.expectError(error.DescriptorHeapFull, heap.allocate());

    // Free only one of the two: a single hole cannot satisfy a pair.
    heap.release(d1.index);
    try std.testing.expectError(error.DescriptorHeapFull, heap.allocateContiguous(2));
}

test "claimFirst marks handle-written slots as live" {
    var heap = testHeap(4, 64);

    // Swap-chain style: RTVs written by handle into slots 0 and 1.
    heap.claimFirst(2);

    // The next allocation must land past the claim, not recycle slot 0.
    const d = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 2), d.index);

    // Releasing a claimed slot returns it to circulation like any other.
    heap.release(1);
    const e = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 1), e.index);
}
