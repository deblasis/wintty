//! This benchmark tests the performance of the terminal stream
//! handler from input to terminal state update. This is useful to
//! test general throughput of VT parsing and handling.
//!
//! This uses the full readonly terminal stream handler
//! (terminal.TerminalStream) so every escape sequence updates real
//! terminal state (styles, cursor movement, erases, modes, etc.).
//! This closely mirrors the work done by the real IO thread.
//!
//! For more isolated measurements see the terminal-parser and
//! osc-parser benchmarks.
const TerminalStream = @This();

const std = @import("std");
const assert = std.debug.assert;
const Allocator = std.mem.Allocator;
const terminalpkg = @import("../terminal/main.zig");
const Benchmark = @import("Benchmark.zig");
const options = @import("options.zig");
const compat_file = @import("../lib/compat/file.zig");
const Terminal = terminalpkg.Terminal;
const Stream = terminalpkg.TerminalStream;
const global = @import("../global.zig");

const log = std.log.scoped(.@"terminal-stream-bench");

/// Prevent a malformed or accidentally enormous corpus from consuming
/// unbounded memory during benchmark setup.
const max_data_size = 64 * 1024 * 1024;

/// Chunk size used to feed the stream in `step`. This matches the read
/// buffer size used by the real IO thread (see termio Exec.zig
/// buffer_capacity) so that the benchmark exercises the stream with
/// realistic chunk sizes, even though the data itself is preloaded (see
/// `setup`) instead of read from disk during the timed step.
const step_chunk_size = 64 * 1024;

opts: Options,
terminal: Terminal,
stream: Stream,
alloc: Allocator,

/// Complete contents of the input corpus, read once in `setup` so the
/// timed step measures stream throughput rather than file IO.
data: []u8 = &.{},

pub const Options = struct {
    /// The size of the terminal. This affects benchmarking when
    /// dealing with soft line wrapping and the memory impact
    /// of page sizes.
    @"terminal-rows": u16 = 80,
    @"terminal-cols": u16 = 120,

    /// Enable opt-in continuation tracking on the stream.
    @"continuation-enabled": bool = false,

    /// Maximum continuation suffix retained when tracking is enabled.
    @"continuation-max-bytes": usize = 1024 * 1024,

    /// Pre-generated data from ghostty-gen. If this is "-" then
    /// we will read stdin. If this is unset, then we will
    /// do nothing (benchmark is a noop). It'd be more unixy to
    /// use stdin by default but I find that a hanging CLI command
    /// with no interaction is a bit annoying.
    data: ?[]const u8 = null,
};

/// Create a new terminal stream handler for the given arguments.
pub fn create(
    alloc: Allocator,
    opts: Options,
) !*TerminalStream {
    const ptr = try alloc.create(TerminalStream);
    errdefer alloc.destroy(ptr);

    ptr.* = .{
        .opts = opts,
        .alloc = alloc,
        .terminal = try .init(global.io(), alloc, .{
            .rows = opts.@"terminal-rows",
            .cols = opts.@"terminal-cols",
        }),
        .stream = undefined,
    };
    errdefer ptr.terminal.deinit(alloc);
    ptr.stream = .init(.{
        .allocator = alloc,
        .handler = .init(&ptr.terminal),
        .continuation_max_bytes = if (opts.@"continuation-enabled")
            opts.@"continuation-max-bytes"
        else
            null,
    });

    return ptr;
}

pub fn destroy(self: *TerminalStream, alloc: Allocator) void {
    self.stream.deinit();
    self.terminal.deinit(alloc);
    alloc.destroy(self);
}

pub fn benchmark(self: *TerminalStream) Benchmark {
    return .init(self, .{
        .stepFn = step,
        .setupFn = setup,
        .teardownFn = teardown,
    });
}

fn setup(ptr: *anyopaque) Benchmark.Error!void {
    const self: *TerminalStream = @ptrCast(@alignCast(ptr));

    // Always reset our terminal state
    self.terminal.fullReset();

    // Preload the entire data file into memory so the timed step below
    // measures stream throughput, not file IO.
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
    const self: *TerminalStream = @ptrCast(@alignCast(ptr));
    if (self.data.len > 0) self.alloc.free(self.data);
    self.data = &.{};
}

fn step(ptr: *anyopaque) Benchmark.Error!void {
    const self: *TerminalStream = @ptrCast(@alignCast(ptr));

    var offset: usize = 0;
    while (offset < self.data.len) {
        const end = @min(offset + step_chunk_size, self.data.len);
        self.stream.nextSlice(self.data[offset..end]);
        offset = end;
    }
}

test TerminalStream {
    const testing = std.testing;
    const alloc = testing.allocator;

    const impl: *TerminalStream = try .create(alloc, .{});
    defer impl.destroy(alloc);

    const bench = impl.benchmark();
    _ = try bench.run(.once);

    const tracked: *TerminalStream = try .create(alloc, .{
        .@"continuation-enabled" = true,
    });
    defer tracked.destroy(alloc);

    const tracked_bench = tracked.benchmark();
    _ = try tracked_bench.run(.once);
}

test "TerminalStream step consumes preloaded data without touching the file" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const impl: *TerminalStream = try .create(alloc, .{});
    defer impl.destroy(alloc);

    // `step` must work entirely off `self.data`. There's no `data_f`
    // field to read from anymore, so this only compiles and passes if
    // `setup`'s preload contract holds: the corpus is fully in memory
    // before the timed step runs.
    impl.data = try alloc.dupe(u8, "hello\r\nworld");
    try step(impl);

    // The stream moved the cursor to the second row, proving the
    // in-memory data was actually parsed.
    try testing.expect(impl.terminal.screens.active.cursor.y > 0);

    // teardown is what actually frees `self.data` normally; call it
    // directly here since we bypassed `setup`.
    teardown(impl);
}
