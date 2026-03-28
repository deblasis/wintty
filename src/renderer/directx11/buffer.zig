const std = @import("std");

/// Options for initializing a buffer.
pub const Options = struct {};

/// DX11 GPU data buffer for a set of equal-typed elements.
/// TODO: Implement with ID3D11Buffer (DYNAMIC usage, Map/Unmap for CPU writes).
pub fn Buffer(comptime T: type) type {
    return struct {
        const Self = @This();

        opts: Options,
        len: usize,

        pub fn init(opts: Options, len: usize) !Self {
            _ = opts;
            _ = len;
            @panic("TODO: DX11 Buffer.init");
        }

        pub fn initFill(opts: Options, data: []const T) !Self {
            _ = opts;
            _ = data;
            @panic("TODO: DX11 Buffer.initFill");
        }

        pub fn deinit(self: *const Self) void {
            _ = self;
            @panic("TODO: DX11 Buffer.deinit");
        }

        pub fn map(self: *Self, len: usize) ![]T {
            _ = self;
            _ = len;
            @panic("TODO: DX11 Buffer.map");
        }

        pub fn unmap(self: *Self) void {
            _ = self;
            @panic("TODO: DX11 Buffer.unmap");
        }

        pub fn resize(self: *Self, new_len: usize) !void {
            _ = self;
            _ = new_len;
            @panic("TODO: DX11 Buffer.resize");
        }

        pub fn sync(self: *Self, data: []const T) !void {
            _ = self;
            _ = data;
            @panic("TODO: DX11 Buffer.sync");
        }

        pub fn syncFromArrayLists(self: *Self, lists: []const std.ArrayListUnmanaged(T)) !usize {
            _ = self;
            _ = lists;
            @panic("TODO: DX11 Buffer.syncFromArrayLists");
        }
    };
}
