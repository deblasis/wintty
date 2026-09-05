//! This benchmark tests the throughput of the OSC parser.
const OscParser = @This();

const std = @import("std");
const builtin = @import("builtin");
const assert = std.debug.assert;
const Allocator = std.mem.Allocator;
const Benchmark = @import("Benchmark.zig");
const options = @import("options.zig");
const compat_file = @import("../lib/compat/file.zig");
const Parser = @import("../terminal/osc.zig").Parser;
const log = std.log.scoped(.@"osc-parser-bench");
const global = @import("../global.zig");

/// Prevent a malformed or accidentally enormous corpus from consuming
/// unbounded memory during benchmark setup.
const max_data_size = 64 * 1024 * 1024;

/// Byte width of the little-endian length prefix preceding each record
/// in the corpus. Matches `takeInt(usize, ...)`'s prior on-disk format.
const record_len_size = @sizeOf(usize);

opts: Options,
alloc: Allocator,

/// Complete contents of the input corpus, read once in `setup` so the
/// timed step measures OSC parser throughput rather than file IO. The
/// corpus is a sequence of `record_len_size`-byte little-endian length
/// prefixes each followed by that many bytes of OSC payload.
data: []u8 = &.{},

parser: Parser,

pub const Options = struct {
    /// The data to read as a filepath. If this is "-" then
    /// we will read stdin. If this is unset, then we will
    /// do nothing (benchmark is a noop). It'd be more unixy to
    /// use stdin by default but I find that a hanging CLI command
    /// with no interaction is a bit annoying.
    data: ?[]const u8 = null,

    /// `cli.args.parse` allocates `[]const u8` fields (like `data`
    /// above) out of this arena when present; without it, allocations
    /// go through an internal allocator that's never freed. See
    /// `deinit`.
    _arena: ?std.heap.ArenaAllocator = null,

    pub fn deinit(self: *Options) void {
        if (self._arena) |arena| arena.deinit();
        self.* = undefined;
    }
};

/// Create a new terminal stream handler for the given arguments.
pub fn create(
    alloc: Allocator,
    opts: Options,
) !*OscParser {
    const ptr = try alloc.create(OscParser);
    errdefer alloc.destroy(ptr);
    ptr.* = .{
        .opts = opts,
        .alloc = alloc,
        .parser = .init(alloc),
    };
    return ptr;
}

pub fn destroy(self: *OscParser, alloc: Allocator) void {
    self.parser.deinit();
    alloc.destroy(self);
}

pub fn benchmark(self: *OscParser) Benchmark {
    return .init(self, .{
        .stepFn = step,
        .setupFn = setup,
        .teardownFn = teardown,
    });
}

fn setup(ptr: *anyopaque) Benchmark.Error!void {
    const self: *OscParser = @ptrCast(@alignCast(ptr));

    // Preload the entire data file into memory so the timed step below
    // measures OSC parser throughput, not file IO.
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
    self.parser.reset();
}

fn teardown(ptr: *anyopaque) void {
    const self: *OscParser = @ptrCast(@alignCast(ptr));
    // `Allocator.free` is a no-op on a zero-length slice, so this is
    // safe even when `setup` never populated `data`.
    self.alloc.free(self.data);
    self.data = &.{};
}

fn step(ptr: *anyopaque) Benchmark.Error!void {
    const self: *OscParser = @ptrCast(@alignCast(ptr));

    var offset: usize = 0;
    while (offset + record_len_size <= self.data.len) {
        const len = std.mem.readInt(
            usize,
            self.data[offset..][0..record_len_size],
            .little,
        );
        offset += record_len_size;

        // Before the corpus was preloaded into `data`, this bounds
        // check guarded a fixed on-stack read buffer, rejecting any
        // record that claimed to be larger than that buffer. Now that
        // the whole corpus lives in memory, the same check instead
        // validates the length prefix against what's actually left in
        // the corpus -- a different guarantee (a well-formed record
        // stream) rather than a fixed per-record size cap.
        if (len > self.data.len - offset) return error.BenchmarkFailed;
        const record = self.data[offset .. offset + len];
        offset += len;

        for (record) |c| @call(.always_inline, Parser.next, .{ &self.parser, c });
        std.mem.doNotOptimizeAway(self.parser.end(std.ascii.control_code.bel));
        self.parser.reset();
    }
}

test OscParser {
    const testing = std.testing;
    const alloc = testing.allocator;

    const impl: *OscParser = try .create(alloc, .{});
    defer impl.destroy(alloc);

    const bench = impl.benchmark();
    _ = try bench.run(.once);
}

test "OscParser step consumes preloaded records without a file" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const impl: *OscParser = try .create(alloc, .{});
    defer impl.destroy(alloc);

    // Build one length-prefixed OSC record in memory, matching the
    // corpus format `step` expects. There's no file involved, so this
    // only passes if `setup`'s preload contract holds: the corpus is
    // fully in memory before the timed step runs.
    const payload = "0;hello";
    var buf: [record_len_size + payload.len]u8 = undefined;
    std.mem.writeInt(usize, buf[0..record_len_size], payload.len, .little);
    @memcpy(buf[record_len_size..], payload);

    impl.data = try alloc.dupe(u8, &buf);
    try step(impl);
    teardown(impl);
}

test "OscParser step rejects a record length past the end of the corpus" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const impl: *OscParser = try .create(alloc, .{});
    defer impl.destroy(alloc);

    var buf: [record_len_size]u8 = undefined;
    std.mem.writeInt(usize, &buf, 1, .little); // claims 1 byte, but none follow

    impl.data = try alloc.dupe(u8, &buf);
    try testing.expectError(error.BenchmarkFailed, step(impl));
    teardown(impl);
}
