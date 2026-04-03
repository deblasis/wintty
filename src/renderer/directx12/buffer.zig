//! DX12 GPU buffer stub.
//!
//! Will be replaced with a real implementation using upload heaps
//! (ring buffer or per-frame allocators) for CPU-to-GPU data transfer.
const std = @import("std");

/// Type-erased buffer handle for passing to RenderPass.Step.
pub const RawBuffer = struct {};

/// Options for initializing a buffer.
pub const Options = struct {};

/// DX12 GPU data buffer for a set of equal-typed elements.
pub fn Buffer(comptime T: type) type {
    return struct {
        const Self = @This();

        /// The type-erased handle passed into RenderPass steps.
        buffer: RawBuffer = .{},

        pub fn init(opts: Options, len: usize) !Self {
            _ = opts;
            _ = len;
            return .{};
        }

        /// Init the buffer filled with the given data.
        pub fn initFill(opts: Options, data: []const T) !Self {
            _ = opts;
            _ = data;
            return .{};
        }

        pub fn deinit(self: *const Self) void {
            _ = self;
        }

        /// Sync the buffer contents with the given data slice.
        pub fn sync(self: *Self, data: []const T) !void {
            _ = self;
            _ = data;
        }

        /// Sync from multiple ArrayListUnmanaged(T), returning total count.
        pub fn syncFromArrayLists(self: *Self, lists: []const std.ArrayListUnmanaged(T)) !usize {
            _ = self;
            var total: usize = 0;
            for (lists) |list| {
                total += list.items.len;
            }
            return total;
        }
    };
}
