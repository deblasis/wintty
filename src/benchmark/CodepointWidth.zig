//! This benchmark tests the throughput of codepoint width calculation.
//! This is a common operation in terminal character printing and the
//! motivating factor to write this benchmark was discovering that our
//! codepoint width function was 30% of the runtime of every character
//! print.
const CodepointWidth = @This();

const std = @import("std");
const builtin = @import("builtin");
const assert = std.debug.assert;
const Allocator = std.mem.Allocator;
const Benchmark = @import("Benchmark.zig");
const options = @import("options.zig");
const compat_file = @import("../lib/compat/file.zig");
const UTF8Decoder = @import("../terminal/UTF8Decoder.zig");
const simd = @import("../simd/main.zig");
const table = @import("../unicode/main.zig").table;
const global = @import("../global.zig");

const log = std.log.scoped(.@"terminal-stream-bench");

/// Prevent a malformed or accidentally enormous corpus from consuming
/// unbounded memory during benchmark setup.
const max_data_size = 64 * 1024 * 1024;

opts: Options,
alloc: Allocator,

/// The file, opened in the setup function. Used only by the `simd`
/// mode's step, which reads directly from disk on every call. That
/// mode measures a dead code path scheduled for removal separately, so
/// it's left untouched here rather than folded into the preload below.
data_f: ?std.Io.File = null,

/// Complete contents of the input corpus for the `noop`, `wcwidth` and
/// `table` modes, read once in `setup` so the timed step measures
/// codepoint-width throughput rather than file IO.
data: []u8 = &.{},

pub const Options = struct {
    /// The type of codepoint width calculation to use.
    mode: Mode = .noop,

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

pub const Mode = enum {
    /// The baseline mode copies the data from the fd into a buffer. This
    /// is used to show the minimal overhead of reading the fd into memory
    /// and establishes a baseline for the other modes.
    noop,

    /// libc wcwidth
    wcwidth,

    /// Our SIMD implementation.
    simd,

    /// Test our lookup table implementation.
    table,
};

/// Create a new terminal stream handler for the given arguments.
pub fn create(
    alloc: Allocator,
    opts: Options,
) !*CodepointWidth {
    const ptr = try alloc.create(CodepointWidth);
    errdefer alloc.destroy(ptr);
    ptr.* = .{ .opts = opts, .alloc = alloc };
    return ptr;
}

pub fn destroy(self: *CodepointWidth, alloc: Allocator) void {
    alloc.destroy(self);
}

pub fn benchmark(self: *CodepointWidth) Benchmark {
    return .init(self, .{
        .stepFn = switch (self.opts.mode) {
            .noop => stepNoop,
            .wcwidth => stepWcwidth,
            .table => stepTable,
            .simd => stepSimd,
        },
        .setupFn = setup,
        .teardownFn = teardown,
    });
}

fn setup(ptr: *anyopaque) Benchmark.Error!void {
    const self: *CodepointWidth = @ptrCast(@alignCast(ptr));

    switch (self.opts.mode) {
        // Unchanged: this mode reads from `data_f` directly inside its
        // step. See the field doc comment for why.
        .simd => {
            assert(self.data_f == null);
            self.data_f = options.dataFile(self.opts.data) catch |err| {
                log.warn("error opening data file err={}", .{err});
                return error.BenchmarkFailed;
            };
        },
        .noop, .wcwidth, .table => {
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
        },
    }
}

fn teardown(ptr: *anyopaque) void {
    const self: *CodepointWidth = @ptrCast(@alignCast(ptr));
    if (self.data_f) |f| {
        f.close(global.io());
        self.data_f = null;
    }
    // `Allocator.free` is a no-op on a zero-length slice, so this is
    // safe even for the `simd` mode, which never populates `data`.
    self.alloc.free(self.data);
    self.data = &.{};
}

fn stepNoop(ptr: *anyopaque) Benchmark.Error!void {
    _ = ptr;
}

extern "c" fn wcwidth(c: u32) c_int;

fn stepWcwidth(ptr: *anyopaque) Benchmark.Error!void {
    if (comptime builtin.os.tag == .windows) {
        log.warn("wcwidth is not available on Windows", .{});
        return;
    }

    const self: *CodepointWidth = @ptrCast(@alignCast(ptr));

    var d: UTF8Decoder = .{};
    for (self.data) |c| {
        const cp_, const consumed = d.next(c);
        assert(consumed);
        if (cp_) |cp| {
            std.mem.doNotOptimizeAway(wcwidth(cp));
        }
    }
}

fn stepTable(ptr: *anyopaque) Benchmark.Error!void {
    const self: *CodepointWidth = @ptrCast(@alignCast(ptr));

    var d: UTF8Decoder = .{};
    for (self.data) |c| {
        const cp_, const consumed = d.next(c);
        assert(consumed);
        if (cp_) |cp| {
            // This is the same trick we do in terminal.zig so we
            // keep it here.
            std.mem.doNotOptimizeAway(if (cp <= 0xFF)
                1
            else
                table.get(@intCast(cp)).width);
        }
    }
}

fn stepSimd(ptr: *anyopaque) Benchmark.Error!void {
    const self: *CodepointWidth = @ptrCast(@alignCast(ptr));

    const f = self.data_f orelse return;
    var read_buf: [4096]u8 align(std.atomic.cache_line) = undefined;
    var f_reader = f.reader(global.io(), &read_buf);
    var r = &f_reader.interface;

    var d: UTF8Decoder = .{};
    var buf: [4096]u8 align(std.atomic.cache_line) = undefined;
    while (true) {
        const n = r.readSliceShort(&buf) catch {
            log.warn("error reading data file err={?}", .{f_reader.err});
            return error.BenchmarkFailed;
        };
        if (n == 0) break; // EOF reached

        for (buf[0..n]) |c| {
            const cp_, const consumed = d.next(c);
            assert(consumed);
            if (cp_) |cp| {
                std.mem.doNotOptimizeAway(simd.codepointWidth(cp));
            }
        }
    }
}

test CodepointWidth {
    const testing = std.testing;
    const alloc = testing.allocator;

    const impl: *CodepointWidth = try .create(alloc, .{});
    defer impl.destroy(alloc);

    const bench = impl.benchmark();
    _ = try bench.run(.once);
}

test "CodepointWidth stepTable consumes preloaded data without a file" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const impl: *CodepointWidth = try .create(alloc, .{ .mode = .table });
    defer impl.destroy(alloc);

    // `stepTable` must work entirely off `self.data`; `data_f` stays
    // null for this mode. This only passes if `setup`'s preload
    // contract holds for non-simd modes.
    impl.data = try alloc.dupe(u8, "hello");
    try stepTable(impl);
    teardown(impl);
}
