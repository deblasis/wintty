//! Deferred release of D3D12 resources that the GPU may still be reading.
//!
//! D3D12, unlike Metal, does not retain the resources a command list
//! references. Once `ExecuteCommandLists` has been called, every resource
//! bound or copied by that list must stay alive until the fence value the
//! submission was signaled with has been reached. Calling the final
//! `Release` before then is a GPU use-after-free: the debug layer reports
//! it as
//!
//!     ID3D12Resource::<final-release>: CORRUPTION: ... is referenced by
//!     GPU operations in-flight on Command Queue ...
//!
//! and raises a native SEH from `D3D12SDKLayers!NDebug::ReportCorruption`,
//! which no managed handler catches. In Release builds the same release is
//! silent and the GPU reads freed memory.
//!
//! `Retirement` is the queue that makes those releases safe. Instead of
//! releasing, an owner *retires* its resource here. Retirements accumulate
//! in `staged` until the next queue submission seals them against that
//! submission's fence value; `collect` then releases everything whose fence
//! value the GPU has reached.
//!
//! Sealing at submission time rather than tagging at retire time is what
//! makes this conservative by construction: whatever is staged when a
//! submission is signaled can only have been referenced by work at or
//! before that submission, so that one fence value covers it. A retirement
//! that happens while a command list is open is sealed by that same list's
//! own signal, which is exactly when its recorded reads finish.
//!
//! NOT single-threaded, and the reason it is safe is one layer up. Zig's
//! `drawFrame` runs on the renderer thread AND on the WinUI UI thread --
//! `Surface.draw` is explicitly required to support the latter, which is
//! how `RequestRepaint` gets a frame out. What serialises every call into
//! this queue is `Renderer.draw_mutex`, held for the whole body of
//! `drawFrame` and by the other GPU-touching entry points (changeConfig,
//! setScreenSize, displayRealized/Unrealized). Teardown is covered
//! separately: the renderer thread is joined before `renderer.deinit()`.
//!
//! So do not add a `retire()` call from a path that does not hold that
//! mutex -- `setTargetSize` runs on the apprt thread, for instance.
//! `staged` is a plain ArrayListUnmanaged: two threads appending races
//! `items.len`, which loses a resource or double-releases one, and the
//! second is a GPU use-after-free that is silent in Release.
//!
//! One more coupling worth stating: `collect` trusts
//! `ID3D12Fence::GetCompletedValue`, so this device's fence must stay
//! producer-signalled -- only this queue advances it. In shared-texture
//! mode the fence is created SHARED and handed out through
//! `ghostty_surface_shared_texture`; no consumer signals it today, and if
//! one ever did, the value would jump past work still in flight and
//! `collect` would free resources the GPU is reading.
const std = @import("std");

const com = @import("com.zig");
const d3d12 = @import("d3d12.zig");
const DescriptorHeap = @import("descriptor_heap.zig").DescriptorHeap;

const log = std.log.scoped(.directx12);

pub const Retirement = struct {
    const Self = @This();

    /// Backed by the C allocator so retiring stays callable from the
    /// value-receiver `deinit` signatures that Buffer and Texture share
    /// with the Metal/OpenGL backends (same reason Texture.pending_staging
    /// uses it).
    alloc: std.mem.Allocator = std.heap.c_allocator,

    /// Retired since the last `seal`. These have no fence value yet
    /// because the submission that will cover them has not happened.
    staged: std.ArrayListUnmanaged(Item) = .empty,

    /// Sealed retirements, each paired with the fence value that must be
    /// reached before it is safe to release. Kept in insertion order,
    /// which is also fence order, so `collect` can stop at the first
    /// entry the GPU has not reached.
    pending: std.ArrayListUnmanaged(Entry) = .empty,

    /// Something awaiting release: either a COM reference or a
    /// descriptor-heap slot. A slot "release" means returning it to its
    /// heap's free list, which lets a later allocation overwrite the
    /// descriptor -- exactly as unsafe before the covering fence as an
    /// early resource Release, hence the same queue.
    pub const Item = union(enum) {
        resource: *d3d12.ID3D12Resource,
        slot: struct { heap: *DescriptorHeap, index: u32 },
    };

    pub const Entry = struct {
        item: Item,
        fence_value: u64,
    };

    /// How many entries to reserve up front. A frame retires at most a
    /// handful of resources (buffer grows are 2x, so logarithmic; image
    /// vertex buffers are per-placement), and growing is cheap, so this
    /// only exists to keep the steady state allocation-free.
    const initial_capacity: usize = 32;

    pub fn init(alloc: std.mem.Allocator) Self {
        var self = Self{ .alloc = alloc };
        self.staged.ensureTotalCapacity(alloc, initial_capacity) catch {};
        self.pending.ensureTotalCapacity(alloc, initial_capacity) catch {};
        return self;
    }

    /// Hand a resource over to the queue. The caller must consider its
    /// reference given away -- it must not release or use the resource
    /// afterwards.
    ///
    /// On allocation failure the reference is intentionally leaked rather
    /// than released: leaking costs memory, releasing early corrupts the
    /// GPU. The next `collect` cannot see it, so it is leaked for the
    /// process lifetime.
    pub fn retire(self: *Self, resource: *d3d12.ID3D12Resource) void {
        self.staged.append(self.alloc, .{ .resource = resource }) catch {
            log.err("deferred-release queue is out of memory; leaking a GPU resource rather than freeing one the GPU may still read", .{});
        };
    }

    /// Return a descriptor-heap slot for reuse, once the GPU has finished
    /// every submission that could still bind a table covering it. Same
    /// fence discipline as `retire`: overwriting a slot's descriptor while
    /// an in-flight command list reads it is as much a use-after-free as
    /// releasing the resource itself.
    pub fn retireSlot(self: *Self, heap: *DescriptorHeap, index: u32) void {
        self.staged.append(
            self.alloc,
            .{ .slot = .{ .heap = heap, .index = index } },
        ) catch {
            log.err("deferred-release queue is out of memory; leaking a descriptor slot rather than recycling one the GPU may still read", .{});
        };
    }

    /// Bind everything staged so far to `fence_value`. Call immediately
    /// after the command queue has been signaled with that value: work
    /// referencing any staged resource was submitted at or before the
    /// signal, so reaching it proves those reads are done.
    pub fn seal(self: *Self, fence_value: u64) void {
        if (self.staged.items.len == 0) return;
        for (self.staged.items) |item| {
            self.pending.append(self.alloc, .{
                .item = item,
                .fence_value = fence_value,
            }) catch {
                log.err("deferred-release queue is out of memory; leaking a GPU resource rather than freeing one the GPU may still read", .{});
            };
        }
        self.staged.clearRetainingCapacity();
    }

    /// Release every sealed retirement the GPU has reached. `completed` is
    /// `ID3D12Fence::GetCompletedValue()`.
    pub fn collect(self: *Self, completed: u64) void {
        var released: usize = 0;
        for (self.pending.items) |entry| {
            if (entry.fence_value > completed) break;
            releaseItem(entry.item);
            released += 1;
        }
        if (released == 0) return;
        const remaining = self.pending.items.len - released;
        std.mem.copyForwards(
            Entry,
            self.pending.items[0..remaining],
            self.pending.items[released..],
        );
        self.pending.shrinkRetainingCapacity(remaining);
    }

    /// Release everything, sealed or not. Only valid once the GPU is known
    /// to be idle -- i.e. straight after a successful full drain.
    pub fn drainAll(self: *Self) void {
        for (self.pending.items) |entry| releaseItem(entry.item);
        self.pending.clearRetainingCapacity();
        for (self.staged.items) |item| releaseItem(item);
        self.staged.clearRetainingCapacity();
    }

    /// Drop every descriptor-slot retirement without recycling it. For
    /// the moment right before the heaps themselves are destroyed: a
    /// slot entry is a pointer into a heap, so once the heaps go it can
    /// only ever be released into freed memory. Resources are kept; they
    /// still follow the drain-or-leak rule.
    pub fn forgetSlots(self: *Self) void {
        const before_pending = self.pending.items.len;
        var kept: usize = 0;
        for (self.pending.items) |entry| {
            if (entry.item == .slot) continue;
            self.pending.items[kept] = entry;
            kept += 1;
        }
        self.pending.shrinkRetainingCapacity(kept);

        const before_staged = self.staged.items.len;
        kept = 0;
        for (self.staged.items) |item| {
            if (item == .slot) continue;
            self.staged.items[kept] = item;
            kept += 1;
        }
        self.staged.shrinkRetainingCapacity(kept);

        // Normally nothing: a drain ahead of this empties the queue. A
        // count here is a fence wait that failed on a live device.
        const dropped = (before_pending - self.pending.items.len) + (before_staged - kept);
        if (dropped != 0) {
            log.warn("dropped {d} descriptor slot retirement(s) ahead of heap teardown", .{dropped});
        }
    }

    fn releaseItem(item: Item) void {
        switch (item) {
            .resource => |resource| _ = resource.Release(),
            .slot => |slot| slot.heap.release(slot.index),
        }
    }

    /// Number of resources the queue is still holding, staged or sealed.
    pub fn count(self: *const Self) usize {
        return self.staged.items.len + self.pending.items.len;
    }

    /// Frees the queue's own memory. Any resource still held is leaked, so
    /// callers must `drainAll` after a GPU drain first.
    pub fn deinit(self: *Self) void {
        if (self.count() != 0) {
            log.warn(
                "deferred-release queue deinited with {d} resource(s) still held",
                .{self.count()},
            );
        }
        self.staged.deinit(self.alloc);
        self.pending.deinit(self.alloc);
        self.* = undefined;
    }
};

// --- Tests ---
//
// The bookkeeping is exercised here without a GPU by pointing the entries
// at stand-in COM objects whose Release only decrements a counter. Nothing
// here reaches D3D12; the GPU-side half of the fix is covered by the
// deferred-release test in gpu_test.zig.

var test_live: usize = 0;

fn testRelease(_: *d3d12.ID3D12Resource) callconv(.winapi) u32 {
    test_live -= 1;
    return @intCast(test_live);
}

fn testUnused() callconv(.winapi) void {
    unreachable;
}

fn testQueryInterface(
    _: *d3d12.ID3D12Resource,
    _: *const com.GUID,
    _: *?*anyopaque,
) callconv(.winapi) com.HRESULT {
    unreachable;
}

fn testAddRef(_: *d3d12.ID3D12Resource) callconv(.winapi) u32 {
    unreachable;
}

fn testMap(
    _: *d3d12.ID3D12Resource,
    _: u32,
    _: ?*const d3d12.D3D12_RANGE,
    _: *?*anyopaque,
) callconv(.winapi) com.HRESULT {
    unreachable;
}

fn testUnmap(_: *d3d12.ID3D12Resource, _: u32, _: ?*const d3d12.D3D12_RANGE) callconv(.winapi) void {
    unreachable;
}

fn testGpuAddress(_: *d3d12.ID3D12Resource) callconv(.winapi) u64 {
    unreachable;
}

const test_vtable: d3d12.ID3D12Resource.VTable = .{
    .QueryInterface = &testQueryInterface,
    .AddRef = &testAddRef,
    .Release = &testRelease,
    .GetPrivateData = &testUnused,
    .SetPrivateData = &testUnused,
    .SetPrivateDataInterface = &testUnused,
    .SetName = &testUnused,
    .GetDevice = &testUnused,
    .Map = &testMap,
    .Unmap = &testUnmap,
    .GetDesc = &testUnused,
    .GetGPUVirtualAddress = &testGpuAddress,
    .WriteToSubresource = &testUnused,
    .ReadFromSubresource = &testUnused,
    .GetHeapProperties = &testUnused,
};

/// `n` stand-in resources, each a bare vtable pointer whose Release only
/// decrements `test_live`.
fn fakeResources(comptime n: usize) [n]d3d12.ID3D12Resource {
    return [_]d3d12.ID3D12Resource{.{ .vtable = &test_vtable }} ** n;
}

test "Retirement: a retire alone releases nothing" {
    var q = Retirement.init(std.testing.allocator);
    defer q.deinit();
    defer q.drainAll();

    var res = fakeResources(1);
    test_live = 1;
    q.retire(&res[0]);

    // Not sealed, so not even a fully-completed fence can free it: the
    // submission that will reference it has not happened yet.
    q.collect(std.math.maxInt(u64));
    try std.testing.expectEqual(@as(usize, 1), test_live);
    try std.testing.expectEqual(@as(usize, 1), q.count());
}

test "Retirement: collect frees only what the fence has reached" {
    var q = Retirement.init(std.testing.allocator);
    defer q.deinit();
    defer q.drainAll();

    var res = fakeResources(2);
    test_live = 2;

    q.retire(&res[0]);
    q.seal(5);
    q.retire(&res[1]);
    q.seal(9);
    try std.testing.expectEqual(@as(usize, 2), q.count());

    // The GPU has not reached either value.
    q.collect(4);
    try std.testing.expectEqual(@as(usize, 2), test_live);

    // Reaching 5 frees the first and only the first.
    q.collect(5);
    try std.testing.expectEqual(@as(usize, 1), test_live);
    try std.testing.expectEqual(@as(usize, 1), q.count());

    q.collect(9);
    try std.testing.expectEqual(@as(usize, 0), test_live);
    try std.testing.expectEqual(@as(usize, 0), q.count());
}

test "Retirement: seal covers everything staged since the last seal" {
    var q = Retirement.init(std.testing.allocator);
    defer q.deinit();
    defer q.drainAll();

    var res = fakeResources(3);
    test_live = 3;

    for (&res) |*r| q.retire(r);
    q.seal(7);
    q.collect(6);
    try std.testing.expectEqual(@as(usize, 3), test_live);
    q.collect(7);
    try std.testing.expectEqual(@as(usize, 0), test_live);
}

test "Retirement: drainAll frees staged and sealed alike" {
    var q = Retirement.init(std.testing.allocator);
    defer q.deinit();

    var res = fakeResources(2);
    test_live = 2;

    q.retire(&res[0]);
    q.seal(11);
    q.retire(&res[1]);

    q.drainAll();
    try std.testing.expectEqual(@as(usize, 0), test_live);
    try std.testing.expectEqual(@as(usize, 0), q.count());
}

/// A heap for the slot tests. release()/allocate() only touch the
/// bookkeeping fields, so a fake base is enough; nothing here reaches D3D12.
fn testHeap(capacity: u32) DescriptorHeap {
    return .{
        .heap = undefined,
        .cpu_start = .{ .ptr = 0x1000 },
        .gpu_start = .{ .ptr = 0x2000 },
        .increment_size = 32,
        .capacity = capacity,
        .allocated = 0,
        .free_mask = 0,
    };
}

test "Retirement: slot release waits for the fence like a resource" {
    var q = Retirement.init(std.testing.allocator);
    defer q.deinit();
    defer q.drainAll();

    var heap = testHeap(2);
    const d0 = try heap.allocate();
    _ = try heap.allocate();
    try std.testing.expectError(error.DescriptorHeapFull, heap.allocate());

    q.retireSlot(&heap, d0.index);
    q.seal(3);

    // Fence not reached: the slot is still owned by in-flight work.
    q.collect(2);
    try std.testing.expectError(error.DescriptorHeapFull, heap.allocate());

    // Fence reached: the slot recycles.
    q.collect(3);
    const d2 = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 0), d2.index);
}

test "Retirement: drainAll frees staged slots without a fence" {
    var q = Retirement.init(std.testing.allocator);
    defer q.deinit();

    var heap = testHeap(1);
    const d0 = try heap.allocate();
    try std.testing.expectError(error.DescriptorHeapFull, heap.allocate());

    // Staged but never sealed.
    q.retireSlot(&heap, d0.index);
    q.drainAll();

    const d1 = try heap.allocate();
    try std.testing.expectEqual(@as(u32, 0), d1.index);
}

test "Retirement: forgetSlots drops slots, staged and sealed, and keeps nothing pointing at a heap" {
    var q = Retirement.init(std.testing.allocator);
    defer q.deinit();
    defer q.drainAll();

    var heap = testHeap(2);
    const d0 = try heap.allocate();
    const d1 = try heap.allocate();

    // One sealed, one staged: both kinds have to go.
    q.retireSlot(&heap, d0.index);
    q.seal(1);
    q.retireSlot(&heap, d1.index);
    try std.testing.expectEqual(@as(usize, 2), q.count());

    q.forgetSlots();
    try std.testing.expectEqual(@as(usize, 0), q.count());

    // Forgotten means not recycled: the heap still thinks both are taken,
    // which is the point when the heap is about to be destroyed anyway.
    try std.testing.expectError(error.DescriptorHeapFull, heap.allocate());
}
