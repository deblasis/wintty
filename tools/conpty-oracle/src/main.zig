//! conpty-oracle: differential cell-identity oracle for ConPTY.
//!
//! Runs a Windows console program under ConPTY, captures the rendered VT
//! output, feeds it into the ghostty-vt terminal model, and produces a
//! deterministic full-fidelity grid dump. Comparing dumps is the
//! acceptance test for "cell-identical to ConPTY".
//!
//! Usage:
//!   conpty-oracle dump <exe> <cols> <rows>
//!       Print the canonical grid dump to stdout.
//!   conpty-oracle selfcheck <exe> <cols> <rows> [runs=2]
//!       Run <exe> under ConPTY <runs> times; exit 0 if every dump is
//!       byte-identical, else print the first diff and exit 1.
//!   conpty-oracle diff <exeA> <exeB> <cols> <rows>
//!       Dump both, print a unified-ish diff, exit 1 on mismatch.

const std = @import("std");
const builtin = @import("builtin");
const conpty = @import("conpty.zig");
const dump_mod = @import("dump.zig");

comptime {
    if (builtin.os.tag != .windows)
        @compileError("conpty-oracle is Windows-only; build with -Dtarget=x86_64-windows-gnu");
}

fn usage() noreturn {
    std.debug.print(
        \\usage:
        \\  conpty-oracle dump <exe> <cols> <rows>
        \\  conpty-oracle selfcheck <exe> <cols> <rows> [runs=2]
        \\  conpty-oracle diff <exeA> <exeB> <cols> <rows>
        \\  conpty-oracle compare-transports <exe> <cols> <rows>
        \\
    , .{});
    std.process.exit(2);
}

fn parseSize(s: []const u8) u16 {
    const v = std.fmt.parseInt(u16, s, 10) catch usage();
    if (v == 0) usage();
    return v;
}

/// Capture <exe> under ConPTY and return its canonical dump.
fn captureAndDump(
    alloc: std.mem.Allocator,
    exe: []const u8,
    cols: u16,
    rows: u16,
) ![]u8 {
    const bytes = try conpty.capture(alloc, exe, cols, rows);
    defer alloc.free(bytes);
    return dump_mod.dump(alloc, bytes, cols, rows);
}

/// Write `line` with non-printable bytes escaped as \xNN (and '\' as
/// '\\') so dumps full of escape sequences diff legibly on a terminal.
fn writeEscaped(w: *std.Io.Writer, line: []const u8) !void {
    for (line) |byte| switch (byte) {
        '\\' => try w.writeAll("\\\\"),
        0x20...0x5b, 0x5d...0x7e => try w.writeByte(byte),
        else => try w.print("\\x{x:0>2}", .{byte}),
    };
}

/// Print a unified-ish line diff of two dumps. Returns true if they
/// differ. `label_a`/`label_b` name the two sides in the header.
fn printDiff(
    w: *std.Io.Writer,
    label_a: []const u8,
    a: []const u8,
    label_b: []const u8,
    b: []const u8,
) !bool {
    if (std.mem.eql(u8, a, b)) return false;

    try w.print("--- {s}\n+++ {s}\n", .{ label_a, label_b });

    var it_a = std.mem.splitScalar(u8, a, '\n');
    var it_b = std.mem.splitScalar(u8, b, '\n');
    var line_no: usize = 1;
    while (true) : (line_no += 1) {
        const la = it_a.next();
        const lb = it_b.next();
        if (la == null and lb == null) break;

        const sa = la orelse "";
        const sb = lb orelse "";
        if (la != null and lb != null and std.mem.eql(u8, sa, sb)) continue;

        try w.print("@@ line {d} @@\n", .{line_no});
        if (la != null) {
            try w.writeAll("-");
            try writeEscaped(w, sa);
            try w.writeAll("\n");
        }
        if (lb != null) {
            try w.writeAll("+");
            try writeEscaped(w, sb);
            try w.writeAll("\n");
        }
    }

    return true;
}

pub fn main() !void {
    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var stdout_buf: [4096]u8 = undefined;
    var stdout_writer = std.fs.File.stdout().writer(&stdout_buf);
    const stdout = &stdout_writer.interface;

    const args = try std.process.argsAlloc(alloc);
    if (args.len < 2) usage();
    const mode = args[1];

    if (std.mem.eql(u8, mode, "dump")) {
        if (args.len != 5) usage();
        const d = try captureAndDump(alloc, args[2], parseSize(args[3]), parseSize(args[4]));
        try stdout.writeAll(d);
        try stdout.flush();
        return;
    }

    if (std.mem.eql(u8, mode, "selfcheck")) {
        if (args.len != 5 and args.len != 6) usage();
        const exe = args[2];
        const cols = parseSize(args[3]);
        const rows = parseSize(args[4]);
        const runs: usize = if (args.len == 6)
            std.fmt.parseInt(usize, args[5], 10) catch usage()
        else
            2;
        if (runs < 2) usage();

        const first = try captureAndDump(alloc, exe, cols, rows);
        for (1..runs) |i| {
            const other = try captureAndDump(alloc, exe, cols, rows);
            var label_buf: [32]u8 = undefined;
            const label = std.fmt.bufPrint(&label_buf, "run {d}", .{i + 1}) catch unreachable;
            if (try printDiff(stdout, "run 1", first, label, other)) {
                try stdout.print(
                    "selfcheck FAIL: {s} at {d}x{d} is non-deterministic (run {d} differs from run 1)\n",
                    .{ exe, cols, rows, i + 1 },
                );
                try stdout.flush();
                std.process.exit(1);
            }
            alloc.free(other);
        }

        try stdout.print(
            "selfcheck OK: {s} at {d}x{d} produced identical dumps across {d} runs ({d} bytes)\n",
            .{ exe, cols, rows, runs, first.len },
        );
        try stdout.flush();
        return;
    }

    if (std.mem.eql(u8, mode, "compare-transports")) {
        if (args.len != 5) usage();
        const exe = args[2];
        const cols = parseSize(args[3]);
        const rows = parseSize(args[4]);

        // A: the program under ConPTY (conhost renders its output to VT).
        const bytes_c = try conpty.capture(alloc, exe, cols, rows);
        defer alloc.free(bytes_c);
        const dump_c = try dump_mod.dumpCells(alloc, bytes_c, cols, rows);

        // B: the program over a raw pipe (no conhost in the data path).
        const bytes_r = try conpty.captureRawPipe(alloc, exe);
        defer alloc.free(bytes_r);

        // No raw-pipe output => the program is Console-API-driven, not
        // VT-native. That's the transport boundary, a distinct outcome.
        if (bytes_r.len < 8) {
            try stdout.print(
                "NO-OUTPUT: {s} produced no raw-pipe output ({d} bytes) - Console-API program, not VT-native\n",
                .{ exe, bytes_r.len },
            );
            try stdout.flush();
            std.process.exit(2);
        }

        const dump_r = try dump_mod.dumpCells(alloc, bytes_r, cols, rows);
        if (try printDiff(stdout, "conpty", dump_c, "rawpipe", dump_r)) {
            try stdout.print(
                "NOT-IDENTICAL: {s} conpty != rawpipe at {d}x{d}\n",
                .{ exe, cols, rows },
            );
            try stdout.flush();
            std.process.exit(1);
        }

        try stdout.print(
            "CELL-IDENTICAL: {s} conpty == rawpipe at {d}x{d} ({d} bytes)\n",
            .{ exe, cols, rows, dump_c.len },
        );
        try stdout.flush();
        return;
    }

    if (std.mem.eql(u8, mode, "diff")) {
        if (args.len != 6) usage();
        const cols = parseSize(args[4]);
        const rows = parseSize(args[5]);
        const dump_a = try captureAndDump(alloc, args[2], cols, rows);
        const dump_b = try captureAndDump(alloc, args[3], cols, rows);

        if (try printDiff(stdout, args[2], dump_a, args[3], dump_b)) {
            try stdout.print(
                "diff FAIL: {s} and {s} are not cell-identical at {d}x{d}\n",
                .{ args[2], args[3], cols, rows },
            );
            try stdout.flush();
            std.process.exit(1);
        }

        try stdout.print(
            "diff OK: {s} and {s} are cell-identical at {d}x{d} ({d} bytes)\n",
            .{ args[2], args[3], cols, rows, dump_a.len },
        );
        try stdout.flush();
        return;
    }

    usage();
}
