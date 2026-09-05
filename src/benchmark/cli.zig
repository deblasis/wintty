const std = @import("std");
const Allocator = std.mem.Allocator;
const cli = @import("../cli.zig");
const global = @import("../global.zig");
const Benchmark = @import("Benchmark.zig");

/// The available actions for the CLI. This is the list of available
/// benchmarks. View docs for each individual one in the predictably
/// named files.
pub const Action = enum {
    @"apc-parser",
    @"codepoint-width",
    @"grapheme-break",
    @"hyperlink-map",
    @"page-compression",
    @"scrollback-compression",
    @"screen-clone",
    @"terminal-formatter",
    @"terminal-parser",
    @"terminal-resize",
    @"terminal-snapshot",
    @"terminal-stream",
    @"is-symbol",
    @"osc-parser",

    /// Returns the struct associated with the action. The struct
    /// should have a few decls:
    ///
    ///   - `const Options`: The CLI options for the action.
    ///   - `fn create`: Create a new instance of the action from options.
    ///   - `fn benchmark`: Returns a `Benchmark` instance for the action.
    ///
    /// See TerminalStream for an example.
    pub fn Struct(comptime action: Action) type {
        return switch (action) {
            .@"apc-parser" => @import("ApcParser.zig"),
            .@"hyperlink-map" => @import("HyperlinkMap.zig"),
            .@"screen-clone" => @import("ScreenClone.zig"),
            .@"page-compression" => @import("PageCompression.zig"),
            .@"scrollback-compression" => @import("ScrollbackCompression.zig"),
            .@"terminal-stream" => @import("TerminalStream.zig"),
            .@"codepoint-width" => @import("CodepointWidth.zig"),
            .@"grapheme-break" => @import("GraphemeBreak.zig"),
            .@"terminal-formatter" => @import("TerminalFormatter.zig"),
            .@"terminal-parser" => @import("TerminalParser.zig"),
            .@"terminal-resize" => @import("TerminalResize.zig"),
            .@"terminal-snapshot" => @import("TerminalSnapshot.zig"),
            .@"is-symbol" => @import("IsSymbol.zig"),
            .@"osc-parser" => @import("OscParser.zig"),
        };
    }
};

/// An entrypoint for the benchmark CLI.
pub fn main(minimal: std.process.Init.Minimal) !void {
    try global.init(.{ .tool = minimal });
    const alloc = std.heap.c_allocator;
    const action_ = try cli.action.detectArgs(Action, alloc, minimal.args);
    const action = action_ orelse return error.NoAction;
    try mainAction(alloc, action, .{ .cli = minimal.args });
}

/// Arguments that can be passed to the benchmark.
pub const Args = union(enum) {
    /// The arguments passed to the CLI via argc/argv.
    cli: std.process.Args,

    /// Simple string arguments, parsed via ArgIteratorGeneral.
    string: []const u8,
};

pub fn mainAction(
    alloc: Allocator,
    action: Action,
    args: Args,
) !void {
    switch (action) {
        inline else => |comptime_action| {
            const BenchmarkImpl = Action.Struct(comptime_action);
            try mainActionImpl(BenchmarkImpl, alloc, args);
        },
    }
}

fn mainActionImpl(
    comptime BenchmarkImpl: type,
    alloc: Allocator,
    args: Args,
) !void {
    // Collect every raw CLI argument once, as independent copies so
    // they outlive whichever source iterator produced them (the
    // process argv iterator frees its backing buffer on `deinit`, and
    // a string iterator similarly owns its buffer). We need two full
    // passes over this same argv below: one for the flags shared by
    // every benchmark action (currently just `--duration-ms`), and one
    // for the per-action `Options`, which must never see
    // `--duration-ms` since it has no field for it and (unlike
    // `RunOptions`) no `_diagnostics` list to tolerate an unrecognized
    // flag instead of erroring.
    var raw_args: std.ArrayList([]const u8) = .empty;
    defer {
        for (raw_args.items) |arg| alloc.free(arg);
        raw_args.deinit(alloc);
    }
    switch (args) {
        .cli => |process_args| {
            var iter = try cli.args.argsIterator(alloc, process_args);
            defer iter.deinit();
            while (iter.next()) |arg| {
                try raw_args.append(alloc, try alloc.dupe(u8, arg));
            }
        },
        .string => |str| {
            var iter = try std.process.Args.IteratorGeneral(.{}).init(
                alloc,
                str,
            );
            defer iter.deinit();
            while (iter.next()) |arg| {
                try raw_args.append(alloc, try alloc.dupe(u8, arg));
            }
        },
    }

    // Parse the flags shared by every benchmark action against a
    // permissive struct first. Its `_diagnostics` field means
    // action-specific flags it doesn't recognize just land there
    // instead of causing a parse error.
    var run_opts: RunOptions = .{};
    defer run_opts.deinit();
    {
        var iter: SliceArgIterator = .{ .items = raw_args.items };
        try cli.args.parse(RunOptions, alloc, &run_opts, &iter);
    }

    // Parse the action-specific options from the same argv with
    // `--duration-ms` filtered out first (see the comment above
    // `raw_args`).
    const Options = BenchmarkImpl.Options;
    var opts: Options = .{};
    defer if (@hasDecl(Options, "deinit")) opts.deinit();
    {
        var filtered: std.ArrayList([]const u8) = .empty;
        defer filtered.deinit(alloc);
        for (raw_args.items) |arg| {
            if (isDurationMsFlag(arg)) continue;
            try filtered.append(alloc, arg);
        }

        var iter: SliceArgIterator = .{ .items = filtered.items };
        try cli.args.parse(Options, alloc, &opts, &iter);
    }

    // Create our implementation
    const impl = try BenchmarkImpl.create(alloc, opts);
    defer impl.destroy(alloc);

    // Initialize our benchmark
    const b = impl.benchmark();
    _ = try b.run(runMode(run_opts.@"duration-ms"));
}

/// True if `arg` is the `--duration-ms` flag, with or without a value.
/// This is the one flag every benchmark action's own `Options` must
/// not see (it has no field for it, and no `_diagnostics` list to
/// tolerate an unknown one).
fn isDurationMsFlag(arg: []const u8) bool {
    const key = if (std.mem.indexOfScalar(u8, arg, '=')) |idx|
        arg[0..idx]
    else
        arg;
    return std.mem.eql(u8, key, "--duration-ms");
}

/// A `cli.args.parse`-compatible iterator (just needs a `next`
/// returning `?[]const u8`) over an already-collected list of argument
/// strings. Lets `mainActionImpl` parse the same argv twice (once per
/// struct below) without re-reading the process's real argv or
/// re-tokenizing a string each time.
const SliceArgIterator = struct {
    items: []const []const u8,
    index: usize = 0,

    pub fn next(self: *SliceArgIterator) ?[]const u8 {
        if (self.index >= self.items.len) return null;
        defer self.index += 1;
        return self.items[self.index];
    }
};

/// Flags accepted for every benchmark action, independent of the
/// per-action `Options` parsed above.
const RunOptions = struct {
    _arena: ?std.heap.ArenaAllocator = null,
    _diagnostics: cli.DiagnosticList = .{},

    /// Run the benchmark step repeatedly for this many milliseconds
    /// instead of running it exactly once. 0 (the default) runs the
    /// step exactly once.
    @"duration-ms": u64 = 0,

    pub fn deinit(self: *RunOptions) void {
        if (self._arena) |arena| arena.deinit();
        self.* = undefined;
    }
};

fn runMode(duration_ms: u64) Benchmark.RunMode {
    return if (duration_ms > 0)
        .{ .duration = duration_ms * std.time.ns_per_ms }
    else
        .once;
}

test "runMode defaults to running once" {
    try std.testing.expectEqual(Benchmark.RunMode.once, runMode(0));
}

test "runMode converts duration-ms to nanoseconds" {
    switch (runMode(5)) {
        .duration => |ns| try std.testing.expectEqual(
            @as(u64, 5 * std.time.ns_per_ms),
            ns,
        ),
        .once => return error.TestUnexpectedResult,
    }
}

test "RunOptions parses duration-ms and ignores unrelated action flags" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var opts: RunOptions = .{};
    defer opts.deinit();

    var iter = try std.process.Args.IteratorGeneral(.{}).init(
        alloc,
        "--terminal-rows=80 --duration-ms=5 --data=-",
    );
    defer iter.deinit();
    try cli.args.parse(RunOptions, alloc, &opts, &iter);

    try testing.expectEqual(@as(u64, 5), opts.@"duration-ms");
}

test "isDurationMsFlag matches with and without a value, not lookalikes" {
    const testing = std.testing;
    try testing.expect(isDurationMsFlag("--duration-ms"));
    try testing.expect(isDurationMsFlag("--duration-ms=5"));
    try testing.expect(!isDurationMsFlag("--duration-msx"));
    try testing.expect(!isDurationMsFlag("--duration"));
    try testing.expect(!isDurationMsFlag("--data=--duration-ms"));
}

test "mainActionImpl accepts --duration-ms alongside action-specific flags" {
    // Regression test: an action's `Options` (TerminalStream's, here)
    // has no `_diagnostics` field, so if `--duration-ms` ever reached
    // its parser unfiltered this would fail with error.InvalidField
    // instead of actually running the benchmark in duration mode.
    const testing = std.testing;
    try mainAction(
        testing.allocator,
        .@"terminal-stream",
        .{ .string = "--terminal-rows=4 --terminal-cols=4 --duration-ms=1" },
    );
}

// The tests below drive `mainAction` end to end against a real file on
// disk: argv collection, the `RunOptions`/`Options` split, `create`,
// `setup`, `step` and `teardown`. Every other test for the preload fix
// (in CodepointWidth.zig, GraphemeBreak.zig, OscParser.zig and
// TerminalStream.zig) sets `self.data` by hand and calls `step`
// directly, which proves `step` can consume preloaded data but never
// proves `setup` actually preloads it -- `options.dataFile` and
// `compat_file.readToEndAlloc` are never reached that way. These do
// reach them: a real temp file is opened by `setup`, so the preload
// path we fixed is what's under test, not assumed.

/// Writes `contents` to a file named "data" inside `tmp` and returns
/// its path, valid for the lifetime of `buf`. Used to hand `mainAction`
/// a `--data=<path>` argument that points at a real file.
fn writeTmpDataFile(
    tmp: *std.testing.TmpDir,
    contents: []const u8,
    buf: []u8,
) ![]const u8 {
    const io = std.testing.io;
    try tmp.dir.writeFile(io, .{ .sub_path = "data", .data = contents });
    const n = try tmp.dir.realPathFile(io, "data", buf);
    return buf[0..n];
}

test "mainAction preloads a real codepoint-width corpus through setup" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    var path_buf: [std.Io.Dir.max_path_bytes]u8 = undefined;
    const path = try writeTmpDataFile(&tmp, "hello world", &path_buf);

    const args = try std.fmt.allocPrint(alloc, "--mode=table --data={s}", .{path});
    defer alloc.free(args);

    try mainAction(alloc, .@"codepoint-width", .{ .string = args });
}

test "mainAction preloads a real grapheme-break corpus through setup" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    var path_buf: [std.Io.Dir.max_path_bytes]u8 = undefined;
    const path = try writeTmpDataFile(&tmp, "e\u{301} world", &path_buf);

    const args = try std.fmt.allocPrint(alloc, "--mode=table --data={s}", .{path});
    defer alloc.free(args);

    try mainAction(alloc, .@"grapheme-break", .{ .string = args });
}

test "mainAction preloads a real osc-parser corpus through setup" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();

    // Matches OscParser's corpus format: a `@sizeOf(usize)`-byte
    // little-endian length prefix followed by that many bytes of OSC
    // payload (see the `data` field doc comment in OscParser.zig).
    const payload = "0;hello";
    const record_len_size = @sizeOf(usize);
    var record: [record_len_size + payload.len]u8 = undefined;
    std.mem.writeInt(usize, record[0..record_len_size], payload.len, .little);
    @memcpy(record[record_len_size..], payload);

    var path_buf: [std.Io.Dir.max_path_bytes]u8 = undefined;
    const path = try writeTmpDataFile(&tmp, &record, &path_buf);

    const args = try std.fmt.allocPrint(alloc, "--data={s}", .{path});
    defer alloc.free(args);

    try mainAction(alloc, .@"osc-parser", .{ .string = args });
}

test "mainAction preloads a real terminal-stream corpus through setup" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    var path_buf: [std.Io.Dir.max_path_bytes]u8 = undefined;
    const path = try writeTmpDataFile(&tmp, "hello\r\nworld", &path_buf);

    const args = try std.fmt.allocPrint(
        alloc,
        "--terminal-rows=4 --terminal-cols=10 --data={s}",
        .{path},
    );
    defer alloc.free(args);

    try mainAction(alloc, .@"terminal-stream", .{ .string = args });
}

test "mainAction opens a real data file for screen-clone through setup" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    var path_buf: [std.Io.Dir.max_path_bytes]u8 = undefined;
    const path = try writeTmpDataFile(&tmp, "hello\r\nworld", &path_buf);

    const args = try std.fmt.allocPrint(
        alloc,
        "--terminal-rows=4 --terminal-cols=10 --data={s}",
        .{path},
    );
    defer alloc.free(args);

    // screen-clone's `setup` doesn't preload into a `data` field like
    // the others -- it streams the file straight into the terminal
    // before the timed clone loop -- but it still opens a real file via
    // `options.dataFile`, which is what this proves actually happens.
    try mainAction(alloc, .@"screen-clone", .{ .string = args });
}
