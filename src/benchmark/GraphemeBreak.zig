//! This benchmark tests the throughput of grapheme break calculation.
//! This is a common operation in terminal character printing for terminals
//! that support grapheme clustering.
const GraphemeBreak = @This();

const std = @import("std");
const assert = std.debug.assert;
const Allocator = std.mem.Allocator;
const Benchmark = @import("Benchmark.zig");
const options = @import("options.zig");
const compat_file = @import("../lib/compat/file.zig");
const UTF8Decoder = @import("../terminal/UTF8Decoder.zig");
const unicode = @import("../unicode/main.zig");
const uucode = @import("uucode");
const global = @import("../global.zig");

const log = std.log.scoped(.@"terminal-stream-bench");

/// Prevent a malformed or accidentally enormous corpus from consuming
/// unbounded memory during benchmark setup.
const max_data_size = 64 * 1024 * 1024;

opts: Options,
alloc: Allocator,

/// Complete contents of the input corpus, read once in `setup` so the
/// timed step measures grapheme-break throughput rather than file IO.
data: []u8 = &.{},

pub const Options = struct {
    /// The type of codepoint width calculation to use.
    mode: Mode = .table,

    /// The data to read as a filepath. If this is "-" then
    /// we will read stdin. If this is unset, then we will
    /// do nothing (benchmark is a noop). It'd be more unixy to
    /// use stdin by default but I find that a hanging CLI command
    /// with no interaction is a bit annoying.
    data: ?[]const u8 = null,
};

pub const Mode = enum {
    /// The baseline mode copies the data from the fd into a buffer. This
    /// is used to show the minimal overhead of reading the fd into memory
    /// and establishes a baseline for the other modes.
    noop,

    /// Ghostty's table-based approach.
    table,
};

/// Create a new terminal stream handler for the given arguments.
pub fn create(
    alloc: Allocator,
    opts: Options,
) !*GraphemeBreak {
    const ptr = try alloc.create(GraphemeBreak);
    errdefer alloc.destroy(ptr);
    ptr.* = .{ .opts = opts, .alloc = alloc };
    return ptr;
}

pub fn destroy(self: *GraphemeBreak, alloc: Allocator) void {
    alloc.destroy(self);
}

pub fn benchmark(self: *GraphemeBreak) Benchmark {
    return .init(self, .{
        .stepFn = switch (self.opts.mode) {
            .noop => stepNoop,
            .table => stepTable,
        },
        .setupFn = setup,
        .teardownFn = teardown,
    });
}

fn setup(ptr: *anyopaque) Benchmark.Error!void {
    const self: *GraphemeBreak = @ptrCast(@alignCast(ptr));

    // Preload the entire data file into memory so the timed step
    // below measures grapheme-break throughput, not file IO.
    assert(self.data.len == 0);
    const f = (options.dataFile(self.opts.data) catch |err| {
        log.warn("error opening data file err={}", .{err});
        return error.BenchmarkFailed;
    }) orelse return;
    defer f.close(global.io());

    self.data = compat_file.readToEndAlloc(
        f,
        self.alloc,
        max_data_size,
    ) catch |err| {
        log.warn("error reading data file err={}", .{err});
        return error.BenchmarkFailed;
    };
}

fn teardown(ptr: *anyopaque) void {
    const self: *GraphemeBreak = @ptrCast(@alignCast(ptr));
    if (self.data.len > 0) self.alloc.free(self.data);
    self.data = &.{};
}

fn stepNoop(ptr: *anyopaque) Benchmark.Error!void {
    const self: *GraphemeBreak = @ptrCast(@alignCast(ptr));

    var d: UTF8Decoder = .{};
    for (self.data) |c| {
        _ = d.next(c);
    }
}

fn stepTable(ptr: *anyopaque) Benchmark.Error!void {
    const self: *GraphemeBreak = @ptrCast(@alignCast(ptr));

    var d: UTF8Decoder = .{};
    var state: uucode.grapheme.BreakState = .default;
    var cp1: u21 = 0;
    for (self.data) |c| {
        const cp_, const consumed = d.next(c);
        assert(consumed);
        if (cp_) |cp2| {
            std.mem.doNotOptimizeAway(unicode.graphemeBreak(cp1, @intCast(cp2), &state));
            cp1 = cp2;
        }
    }
}

test GraphemeBreak {
    const testing = std.testing;
    const alloc = testing.allocator;

    const impl: *GraphemeBreak = try .create(alloc, .{});
    defer impl.destroy(alloc);

    const bench = impl.benchmark();
    _ = try bench.run(.once);
}

test "GraphemeBreak stepTable consumes preloaded data without a file" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const impl: *GraphemeBreak = try .create(alloc, .{ .mode = .table });
    defer impl.destroy(alloc);

    // `stepTable` must work entirely off `self.data`. There's no
    // `data_f` to read from anymore, so this only passes if `setup`'s
    // preload contract holds: the corpus is fully in memory before the
    // timed step runs.
    impl.data = try alloc.dupe(u8, "e\u{301}"); // e + combining acute accent
    try stepTable(impl);
    teardown(impl);
}
