//! DX12 GPU buffer backed by an upload heap with persistent mapping.
//!
//! Each Buffer(T) owns a single ID3D12Resource in D3D12_HEAP_TYPE.UPLOAD,
//! mapped once at creation and never unmapped until deinit. Writes go
//! through the mapped pointer via @memcpy -- zero-copy from the CPU side.
//!
//! Per-frame isolation is handled one layer up by GenericRenderer's
//! FrameState, which maintains separate Buffer instances per in-flight
//! frame. This keeps the buffer implementation simple: no ring buffer
//! segmentation needed.
const std = @import("std");

const d3d12 = @import("d3d12.zig");
const com = @import("com.zig");

const log = std.log.scoped(.directx12);

/// Options for creating a DX12 buffer. Passed from DirectX12.zig's
/// bufferOptions() / uniformBufferOptions() / bgBufferOptions().
pub const Options = struct {
    device: ?*d3d12.ID3D12Device = null,
};

/// Type-erased buffer handle for passing to RenderPass.Step.
/// Holds the GPU virtual address, total size, and per-element stride
/// needed for vertex buffer view binding.
/// Both size and stride are u32 to match D3D12_VERTEX_BUFFER_VIEW's
/// SizeInBytes/StrideInBytes fields (UINT). Terminal buffers are well
/// under the 4GB limit so @intCast from usize is always safe here.
pub const RawBuffer = struct {
    gpu_address: u64 = 0,
    size: u32 = 0,
    stride: u32 = 0,
};

/// DX12 GPU data buffer for a set of equal-typed elements.
///
/// Wraps an upload heap resource with persistent CPU mapping.
/// Regrows automatically (2x) when sync data exceeds capacity.
pub fn Buffer(comptime T: type) type {
    return struct {
        const Self = @This();

        opts: Options,

        /// The underlying upload heap resource, null if zero-length.
        resource: ?*d3d12.ID3D12Resource = null,

        /// Persistently mapped pointer to the upload heap. Valid for
        /// the lifetime of the resource (Map at creation, no Unmap).
        mapped: ?[*]u8 = null,

        /// Allocated capacity in number of T elements.
        len: usize = 0,

        /// Type-erased handle for RenderPass binding.
        buffer: RawBuffer = .{},

        pub fn init(opts: Options, len: usize) !Self {
            var self = Self{ .opts = opts };
            errdefer self.release();
            if (len > 0) {
                try self.allocate(len);
            }
            return self;
        }

        /// Init the buffer filled with the given data.
        pub fn initFill(opts: Options, data: []const T) !Self {
            var self = Self{ .opts = opts };
            errdefer self.release();
            if (data.len > 0) {
                try self.allocate(data.len);
                try self.copy(data);
            }
            return self;
        }

        pub fn deinit(self: *const Self) void {
            // Delegate to release() for full cleanup. @constCast is safe
            // because deinit is the owner and this matches Metal's pattern
            // of calling release through a *const Self.
            const mutable = @constCast(self);
            mutable.release();
        }

        /// Sync the buffer contents with the given data slice.
        /// If the data exceeds capacity, the buffer is reallocated at 2x.
        pub fn sync(self: *Self, data: []const T) !void {
            if (data.len == 0) return;

            if (data.len > self.len) {
                self.release();
                try self.allocate(data.len * 2);
            }

            try self.copy(data);
        }

        /// Sync from multiple ArrayListUnmanaged(T), returning total count.
        pub fn syncFromArrayLists(self: *Self, lists: []const std.ArrayListUnmanaged(T)) !usize {
            var total: usize = 0;
            for (lists) |list| {
                total += list.items.len;
            }

            if (total == 0) return 0;

            if (total > self.len) {
                self.release();
                try self.allocate(total * 2);
            }

            const dst = self.mapped orelse {
                log.warn("buffer mapped pointer is null", .{});
                return error.BufferMapFailed;
            };
            var offset: usize = 0;
            for (lists) |list| {
                const bytes = list.items.len * @sizeOf(T);
                const src: [*]const u8 = @ptrCast(list.items.ptr);
                @memcpy(dst[offset..][0..bytes], src[0..bytes]);
                offset += bytes;
            }

            return total;
        }

        // -- internal helpers --

        fn allocate(self: *Self, len: usize) !void {
            const device = self.opts.device orelse return error.NoDevice;
            const byte_size = len * @sizeOf(T);

            // CPUPageProperty, MemoryPoolPreference, and node masks are
            // ignored by the runtime when Type is not CUSTOM.
            const heap_props = d3d12.D3D12_HEAP_PROPERTIES{
                .Type = .UPLOAD,
                .CPUPageProperty = 0,
                .MemoryPoolPreference = 0,
                .CreationNodeMask = 0,
                .VisibleNodeMask = 0,
            };

            const desc = d3d12.D3D12_RESOURCE_DESC{
                .Dimension = .BUFFER,
                .Alignment = 0,
                .Width = byte_size,
                .Height = 1,
                .DepthOrArraySize = 1,
                .MipLevels = 1,
                .Format = .UNKNOWN,
                .SampleDesc = .{ .Count = 1, .Quality = 0 },
                .Layout = .ROW_MAJOR,
                .Flags = .NONE,
            };

            var resource: ?*d3d12.ID3D12Resource = null;
            const hr = device.CreateCommittedResource(
                &heap_props,
                0, // no heap flags
                &desc,
                d3d12.D3D12_RESOURCE_STATES.GENERIC_READ,
                null, // no optimized clear
                &d3d12.ID3D12Resource.IID,
                @ptrCast(&resource),
            );
            if (com.FAILED(hr)) {
                log.err("CreateCommittedResource for upload buffer failed: 0x{x}", .{@as(u32, @bitCast(hr))});
                return error.BufferCreationFailed;
            }

            const res = resource orelse return error.BufferCreationFailed;

            // Persistently map the upload heap. Read range is empty because
            // the CPU never reads back from this buffer.
            var mapped: ?*anyopaque = null;
            const read_range = d3d12.D3D12_RANGE{ .Begin = 0, .End = 0 };
            const map_hr = res.Map(0, &read_range, &mapped);
            if (com.FAILED(map_hr) or mapped == null) {
                log.err("Map for upload buffer failed: 0x{x}", .{@as(u32, @bitCast(map_hr))});
                _ = res.Release();
                return error.BufferMapFailed;
            }

            self.resource = res;
            self.mapped = @ptrCast(mapped.?);
            self.len = len;
            self.buffer = .{
                .gpu_address = res.GetGPUVirtualAddress(),
                .size = @intCast(byte_size),
                .stride = @sizeOf(T),
            };
        }

        fn release(self: *Self) void {
            if (self.resource) |res| {
                _ = res.Release();
            }
            self.resource = null;
            self.mapped = null;
            self.len = 0;
            self.buffer = .{};
        }

        fn copy(self: *Self, data: []const T) !void {
            const dst = self.mapped orelse {
                log.warn("buffer mapped pointer is null", .{});
                return error.BufferMapFailed;
            };
            const bytes = data.len * @sizeOf(T);
            const src: [*]const u8 = @ptrCast(data.ptr);
            @memcpy(dst[0..bytes], src[0..bytes]);
        }
    };
}
