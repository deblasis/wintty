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
        \\  conpty-oracle compare-resize <exe> <cols0> <rows0> <cols1> <rows1>
        \\  conpty-oracle signal-probe <child_exe> <helper_exe> <C|B>
        \\  conpty-oracle teardown-probe <tree_child_exe>
        \\  conpty-oracle rawpty <rawpty_child_exe> <cols> <rows>
        \\
    , .{});
    std.process.exit(2);
}

/// Name the handful of Win32 error codes the Ctrl-C courier can hit so the
/// diagnosis is legible without a lookup.
fn win32Err(code: u32) []const u8 {
    return switch (code) {
        5 => "ERROR_ACCESS_DENIED - caller already attached to a console",
        6 => "ERROR_INVALID_HANDLE - target process has no console",
        87 => "ERROR_INVALID_PARAMETER",
        1811 => "ERROR_NO_PROC_SLOTS",
        else => "?",
    };
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

    if (std.mem.eql(u8, mode, "compare-resize")) {
        if (args.len != 7) usage();
        const exe = args[2];
        const cols0 = parseSize(args[3]);
        const rows0 = parseSize(args[4]);
        const cols1 = parseSize(args[5]);
        const rows1 = parseSize(args[6]);

        // A: under ConPTY, resized via ResizePseudoConsole (the child learns
        // the new size from a WINDOW_BUFFER_SIZE_EVENT).
        const bytes_c = try conpty.captureResize(alloc, exe, cols0, rows0, cols1, rows1);
        defer alloc.free(bytes_c);
        const dump_c = try dump_mod.dumpCells(alloc, bytes_c, cols1, rows1);

        // B: over a raw pipe, resized via an in-band 2048 report on stdin
        // (the injection-free substitute a raw-pipe transport would use).
        const bytes_r = try conpty.captureRawPipeResize(alloc, exe, cols1, rows1);
        defer alloc.free(bytes_r);

        // No raw-pipe output at all => the child never spoke VT (wrong
        // program for this mode). Distinct from a resize that didn't land.
        if (bytes_r.len < 8) {
            try stdout.print(
                "NO-OUTPUT: {s} produced no raw-pipe output ({d} bytes)\n",
                .{ exe, bytes_r.len },
            );
            try stdout.flush();
            std.process.exit(2);
        }

        const dump_r = try dump_mod.dumpCells(alloc, bytes_r, cols1, rows1);
        if (try printDiff(stdout, "conpty(resize)", dump_c, "rawpipe(2048)", dump_r)) {
            try stdout.print(
                "NOT-IDENTICAL: {s} conpty(resize {d}x{d}->{d}x{d}) != rawpipe(2048)\n",
                .{ exe, cols0, rows0, cols1, rows1 },
            );
            try stdout.flush();
            std.process.exit(1);
        }

        try stdout.print(
            "CELL-IDENTICAL: {s} conpty(resize)==rawpipe(2048) at {d}x{d} ({d} bytes)\n",
            .{ exe, cols1, rows1, dump_c.len },
        );
        try stdout.flush();
        return;
    }

    if (std.mem.eql(u8, mode, "signal-probe")) {
        if (args.len != 5) usage();
        const child_exe = args[2];
        const helper_exe = args[3];
        if (args[4].len != 1) usage();
        const kind = args[4][0];
        if (kind != 'C' and kind != 'c' and kind != 'B' and kind != 'b') usage();

        // Escape non-printable bytes so the child's VT-framed output (ConPTY
        // side) reads legibly, then trim to the informative markers.
        const marker = if (kind == 'B' or kind == 'b') "GOT-SIGNAL:1" else "GOT-SIGNAL:0";

        // ConPTY baseline: Ctrl-Break isn't a single input byte, so the
        // baseline only covers Ctrl-C; the raw-pipe arm covers both.
        var conpty_ok = false;
        if (kind == 'C' or kind == 'c') {
            const res_c = try conpty.signalProbeConpty(alloc, child_exe);
            defer alloc.free(res_c.output);
            conpty_ok = res_c.gotSignal();
            try stdout.print(
                "conpty  (write 0x03)      : got_signal={} (looking for '{s}')\n",
                .{ conpty_ok, marker },
            );
        } else {
            try stdout.print("conpty  (write 0x03)      : n/a (Ctrl-Break not a single input byte)\n", .{});
        }

        const res_r = try conpty.signalProbeRawPipe(alloc, child_exe, helper_exe, kind);
        defer alloc.free(res_r.output);
        const raw_ok = res_r.gotSignal();
        try stdout.print(
            "rawpipe (AttachConsole)   : got_signal={} helper_rc={d} (looking for '{s}')\n",
            .{ raw_ok, res_r.helper_rc, marker },
        );
        // Show what the raw-pipe child actually printed (incl. its CON: line,
        // which distinguishes "no console" from "attach failed").
        try stdout.writeAll("  child said: ");
        try writeEscaped(stdout, std.mem.trim(u8, res_r.output, "\r\n"));
        try stdout.writeAll("\n");

        // Diagnose the courier if it didn't land. Exit codes 1000+errno /
        // 2000+errno carry the Win32 GetLastError from the courier.
        if (!raw_ok) {
            const rc = res_r.helper_rc;
            if (rc >= 1000 and rc < 2000) {
                try stdout.print(
                    "  diagnosis: AttachConsole(childpid) failed, GetLastError={d} ({s})\n",
                    .{ rc - 1000, win32Err(rc - 1000) },
                );
            } else if (rc >= 2000 and rc < 3000) {
                try stdout.print(
                    "  diagnosis: GenerateConsoleCtrlEvent failed, GetLastError={d} ({s})\n",
                    .{ rc - 2000, win32Err(rc - 2000) },
                );
            } else if (rc == 0) {
                try stdout.print("  diagnosis: courier ran clean but child saw no signal - group/timing\n", .{});
            } else {
                try stdout.print("  diagnosis: courier error rc={d}\n", .{rc});
            }
        }

        // Third arm: targeted console-process-group delivery. The child
        // inherits our console but as its own group; we fire CTRL_BREAK at
        // just that group. This is the injection-free mechanism a
        // helper-owned-console transport uses, and unlike the courier it does
        // not depend on cross-station AttachConsole, so it is provable under
        // headless CI. Always delivers Ctrl-Break (type 1).
        const res_g = try conpty.signalProbeProcessGroup(alloc, child_exe);
        defer alloc.free(res_g.output);
        const grp_ok = std.mem.indexOf(u8, res_g.output, "GOT-SIGNAL:1") != null;
        try stdout.print(
            "rawpipe (proc-group brk)  : got_signal={} rc={d} (looking for 'GOT-SIGNAL:1')\n",
            .{ grp_ok, res_g.helper_rc },
        );

        // Verdict. The process-group arm is the CI-provable mechanism; the
        // AttachConsole courier is the same idea via the child's own console
        // and is expected to work on a real desktop but hits a window-station
        // wall (ERROR_INVALID_HANDLE) on headless CI.
        if (grp_ok) {
            try stdout.print(
                "RESULT: injection-free console-group signal delivery WORKS to a pipe-output child" ++
                    " (Ctrl-Break; ConPTY 0x03 baseline={}; AttachConsole courier got_signal={})\n",
                .{ conpty_ok, raw_ok },
            );
            try stdout.flush();
            return;
        }

        try stdout.print(
            "RESULT: injection-free signal delivery UNCONFIRMED (proc-group={} courier={} conpty={})\n",
            .{ grp_ok, raw_ok, conpty_ok },
        );
        try stdout.flush();
        std.process.exit(1);
    }

    if (std.mem.eql(u8, mode, "teardown-probe")) {
        if (args.len != 3) usage();
        const child_exe = args[2];

        const t = try conpty.teardownProbe(alloc, child_exe);
        defer alloc.free(t.output);
        try stdout.print(
            "rawpipe (job KILL_ON_JOB_CLOSE):\n" ++
                "  assign_to_job = {} (err={d})\n" ++
                "  tree          = child:{d} grand:{d}\n" ++
                "  alive_before  = {}\n" ++
                "  dead_after    = {}   (both child AND grandchild terminated by job close)\n" ++
                "  no_wedge      = {}   (pipe hit EOF after kill -> no leaked writer)\n",
            .{ t.assign_ok, t.assign_err, t.child_pid, t.grand_pid, t.alive_before, t.dead_after, t.no_wedge },
        );

        // ConPTY contrast: the same job assignment is expected to fail because
        // the child already belongs to ConPTY's job object.
        const c = try conpty.assignUnderConpty(alloc, child_exe);
        try stdout.print(
            "conpty  (contrast):\n" ++
                "  assign_to_job = {} (err={d}) {s}\n",
            .{ c.ok, c.err, if (c.ok) "" else "(expected: child already in ConPTY's job)" },
        );

        const pass = t.assign_ok and t.alive_before and t.dead_after and t.no_wedge;
        if (pass) {
            try stdout.print(
                "RESULT: raw-pipe job-object teardown kills the whole tree with no leak and no wedge" ++
                    " (ConPTY job-assign contrast: ok={})\n",
                .{c.ok},
            );
            try stdout.flush();
            return;
        }

        try stdout.print(
            "RESULT: raw-pipe teardown INCOMPLETE (assign={} alive_before={} dead_after={} no_wedge={})\n",
            .{ t.assign_ok, t.alive_before, t.dead_after, t.no_wedge },
        );
        try stdout.flush();
        std.process.exit(1);
    }

    if (std.mem.eql(u8, mode, "rawpty")) {
        if (args.len != 5) usage();
        const child_exe = args[2];
        const cols = parseSize(args[3]);
        const rows = parseSize(args[4]);

        const r = try conpty.rawPtyLifecycle(alloc, child_exe, cols, rows);
        defer alloc.free(r.output);

        try stdout.print(
            "raw-pipe transport lifecycle (no ConPTY):\n" ++
                "  spawn+job     assign_to_job = {}\n" ++
                "  READY         got_ready     = {}\n" ++
                "  RESIZE (2048) got_resize    = {}  (in-band size report on stdin pipe)\n" ++
                "  SIGNAL (brk)  got_signal    = {}  (console-group ctrl event)\n" ++
                "  COMPOSED      both_in_1_run = {}\n" ++
                "  TEARDOWN      alive_before  = {}  dead_after = {}  no_wedge = {}\n",
            .{
                r.assign_ok, r.got_ready,    r.got_resize, r.got_signal,
                r.composed,  r.alive_before, r.dead_after, r.no_wedge,
            },
        );
        try stdout.writeAll("  child said: ");
        try writeEscaped(stdout, std.mem.trim(u8, r.output, "\r\n"));
        try stdout.writeAll("\n");

        const pass = r.assign_ok and r.got_ready and r.got_resize and
            r.got_signal and r.composed and r.alive_before and
            r.dead_after and r.no_wedge;
        if (pass) {
            try stdout.print(
                "RESULT: raw-pipe transport composes resize + signals + teardown end-to-end (no ConPTY)\n",
                .{},
            );
            try stdout.flush();
            return;
        }

        try stdout.print("RESULT: raw-pipe transport lifecycle INCOMPLETE - see flags above\n", .{});
        try stdout.flush();
        std.process.exit(1);
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
