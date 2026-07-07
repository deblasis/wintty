//! ConPTY read-path probe.
//!
//! Measures, on a real Windows machine, the questions raised by upstream
//! Ghostty's io-gather thread work (#13209) before porting it to the
//! Windows/ConPTY transport:
//!
//!   1. What read sizes does ConPTY actually deliver? (POSIX ptys cap
//!      reads at ~1 KiB kernel-side; Windows has no such cap, but the
//!      current read loop uses a 1024-byte buffer anyway.)
//!   2. Does bumping the read buffer (1 KiB -> 64 KiB) improve
//!      throughput when the reader also pays a simulated VT-parse cost?
//!   3. Does a gather thread (2-stage pipeline, ring of 4x64KiB buffers,
//!      mirroring upstream's Pipeline) beat the serial loop on Windows?
//!   4. Does enlarging the ConPTY output pipe buffer (CreatePipe nSize)
//!      matter?
//!
//! Self-contained: builds with `zig build-exe -target x86_64-windows-gnu`.
//! Usage:
//!   conpty_probe.exe               run the experiment matrix, report
//!   conpty_probe.exe blast <mib>   child mode: write <mib> MiB of text

const std = @import("std");
const windows = std.os.windows;

const MiB = 1024 * 1024;

/// MiB each child writes. ConPTY re-renders so the parent reads a
/// different (usually similar) byte count; we always report read-side
/// bytes.
const blast_mib = 32;

// ---------------------------------------------------------------------
// Win32 declarations not in std.os.windows (signatures copied from
// ghostty src/os/windows.zig `exp`).
// ---------------------------------------------------------------------

const HPCON = windows.LPVOID;
const LPPROC_THREAD_ATTRIBUTE_LIST = ?*anyopaque;
const EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
// ProcThreadAttributeValue(22, false, true, false)
const PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE: usize = 22 | 0x00020000;

const STARTUPINFOEX = extern struct {
    StartupInfo: windows.STARTUPINFOW,
    lpAttributeList: LPPROC_THREAD_ATTRIBUTE_LIST,
};

const k32 = struct {
    extern "kernel32" fn CreatePipe(
        hReadPipe: *windows.HANDLE,
        hWritePipe: *windows.HANDLE,
        lpPipeAttributes: ?*const windows.SECURITY_ATTRIBUTES,
        nSize: windows.DWORD,
    ) callconv(.winapi) windows.BOOL;
    extern "kernel32" fn CreatePseudoConsole(
        size: windows.COORD,
        hInput: windows.HANDLE,
        hOutput: windows.HANDLE,
        dwFlags: windows.DWORD,
        phPC: *HPCON,
    ) callconv(.winapi) windows.HRESULT;
    extern "kernel32" fn ClosePseudoConsole(hPC: HPCON) callconv(.winapi) void;
    extern "kernel32" fn InitializeProcThreadAttributeList(
        lpAttributeList: LPPROC_THREAD_ATTRIBUTE_LIST,
        dwAttributeCount: windows.DWORD,
        dwFlags: windows.DWORD,
        lpSize: *windows.SIZE_T,
    ) callconv(.winapi) windows.BOOL;
    extern "kernel32" fn UpdateProcThreadAttribute(
        lpAttributeList: LPPROC_THREAD_ATTRIBUTE_LIST,
        dwFlags: windows.DWORD,
        Attribute: windows.DWORD_PTR,
        lpValue: windows.PVOID,
        cbSize: windows.SIZE_T,
        lpPreviousValue: ?windows.PVOID,
        lpReturnSize: ?*windows.SIZE_T,
    ) callconv(.winapi) windows.BOOL;
    extern "kernel32" fn DeleteProcThreadAttributeList(
        lpAttributeList: LPPROC_THREAD_ATTRIBUTE_LIST,
    ) callconv(.winapi) void;
    extern "kernel32" fn CreateProcessW(
        lpApplicationName: ?windows.LPWSTR,
        lpCommandLine: ?windows.LPWSTR,
        lpProcessAttributes: ?*windows.SECURITY_ATTRIBUTES,
        lpThreadAttributes: ?*windows.SECURITY_ATTRIBUTES,
        bInheritHandles: windows.BOOL,
        dwCreationFlags: windows.DWORD,
        lpEnvironment: ?*anyopaque,
        lpCurrentDirectory: ?windows.LPWSTR,
        lpStartupInfo: *windows.STARTUPINFOW,
        lpProcessInformation: *windows.PROCESS_INFORMATION,
    ) callconv(.winapi) windows.BOOL;
    extern "kernel32" fn WaitForSingleObject(
        hHandle: windows.HANDLE,
        dwMilliseconds: windows.DWORD,
    ) callconv(.winapi) windows.DWORD;
};

// ---------------------------------------------------------------------
// Child mode: blast plain text to stdout.
// ---------------------------------------------------------------------

fn childBlast(mib: usize) void {
    const stdout = windows.GetStdHandle(windows.STD_OUTPUT_HANDLE) catch return;

    // 64 KiB block of 80-column lines: 78 visible chars + \r\n.
    var block: [64 * 1024]u8 = undefined;
    var i: usize = 0;
    while (i + 80 <= block.len) : (i += 80) {
        for (block[i..][0..78], 0..) |*ch, j| ch.* = 'a' + @as(u8, @intCast(j % 26));
        block[i + 78] = '\r';
        block[i + 79] = '\n';
    }
    const payload = block[0..i];

    var remaining: usize = mib * MiB;
    while (remaining > 0) {
        const want: windows.DWORD = @intCast(@min(remaining, payload.len));
        var written: windows.DWORD = 0;
        if (windows.kernel32.WriteFile(stdout, payload.ptr, want, &written, null) == 0) return;
        if (written == 0) return;
        remaining -= written;
    }
}

// ---------------------------------------------------------------------
// ConPTY session: pipes + pseudoconsole + child, mirroring
// ghostty src/pty.zig WindowsPty.open (anonymous output pipe).
// ---------------------------------------------------------------------

const Session = struct {
    in_read: windows.HANDLE, // conhost reads child input here (unused)
    in_write: windows.HANDLE,
    out_read: windows.HANDLE, // we read rendered output here
    out_write: windows.HANDLE, // conhost writes here
    hpcon: HPCON,
    child: windows.HANDLE,
    child_thread: windows.HANDLE,

    fn spawn(exe_w: [:0]const u16, pipe_buf: windows.DWORD) !Session {
        var s: Session = undefined;

        if (k32.CreatePipe(&s.in_read, &s.in_write, null, 0) == 0)
            return error.CreatePipe;
        if (k32.CreatePipe(&s.out_read, &s.out_write, null, pipe_buf) == 0)
            return error.CreatePipe;

        if (k32.CreatePseudoConsole(
            .{ .X = 120, .Y = 30 },
            s.in_read,
            s.out_write,
            0,
            &s.hpcon,
        ) != windows.S_OK) return error.CreatePseudoConsole;

        // Attribute list carrying the pseudoconsole.
        var attr_size: windows.SIZE_T = 0;
        _ = k32.InitializeProcThreadAttributeList(null, 1, 0, &attr_size);
        var attr_buf: [128]u8 align(16) = undefined;
        if (attr_size > attr_buf.len) return error.AttrListTooBig;
        const attr_list: LPPROC_THREAD_ATTRIBUTE_LIST = &attr_buf;
        if (k32.InitializeProcThreadAttributeList(attr_list, 1, 0, &attr_size) == 0)
            return error.AttrListInit;
        defer k32.DeleteProcThreadAttributeList(attr_list);
        if (k32.UpdateProcThreadAttribute(
            attr_list,
            0,
            PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            s.hpcon,
            @sizeOf(HPCON),
            null,
            null,
        ) == 0) return error.AttrListUpdate;

        // Command line: "<exe>" blast <mib>
        var cmd_utf8_buf: [std.fs.max_path_bytes + 32]u8 = undefined;
        var exe_utf8_buf: [std.fs.max_path_bytes]u8 = undefined;
        const exe_utf8_len = std.unicode.utf16LeToUtf8(&exe_utf8_buf, exe_w) catch
            return error.BadExePath;
        const cmd_utf8 = std.fmt.bufPrint(
            &cmd_utf8_buf,
            "\"{s}\" blast {d}",
            .{ exe_utf8_buf[0..exe_utf8_len], blast_mib },
        ) catch return error.CmdTooLong;
        var cmd_w: [cmd_utf8_buf.len + 1]u16 = undefined;
        const cmd_w_len = std.unicode.utf8ToUtf16Le(&cmd_w, cmd_utf8) catch
            return error.BadCmd;
        cmd_w[cmd_w_len] = 0;

        var siex = std.mem.zeroes(STARTUPINFOEX);
        siex.StartupInfo.cb = @sizeOf(STARTUPINFOEX);
        siex.lpAttributeList = attr_list;
        var pi = std.mem.zeroes(windows.PROCESS_INFORMATION);

        if (k32.CreateProcessW(
            null,
            @ptrCast(&cmd_w),
            null,
            null,
            windows.FALSE,
            EXTENDED_STARTUPINFO_PRESENT,
            null,
            null,
            &siex.StartupInfo,
            &pi,
        ) == 0) return error.CreateProcess;

        s.child = pi.hProcess;
        s.child_thread = pi.hThread;
        return s;
    }

    /// Waiter-thread body: once the child exits, close the
    /// pseudoconsole and our copy of the conhost-side write handle so
    /// the reader observes pipe EOF after the final flush.
    fn waitAndClose(s: *Session) void {
        _ = k32.WaitForSingleObject(s.child, windows.INFINITE);
        k32.ClosePseudoConsole(s.hpcon);
        _ = windows.CloseHandle(s.out_write);
    }

    fn deinit(s: *Session) void {
        _ = windows.CloseHandle(s.child_thread);
        _ = windows.CloseHandle(s.child);
        _ = windows.CloseHandle(s.in_read);
        _ = windows.CloseHandle(s.in_write);
        _ = windows.CloseHandle(s.out_read);
        // out_write and hpcon are closed by waitAndClose.
    }
};

// ---------------------------------------------------------------------
// Measurement
// ---------------------------------------------------------------------

const Stats = struct {
    reads: u64 = 0,
    bytes: u64 = 0,
    min: u64 = std.math.maxInt(u64),
    max: u64 = 0,
    full: u64 = 0, // reads that filled the buffer exactly
    eq_1024: u64 = 0,
    lt_1024: u64 = 0,
    first_byte_ns: u64 = 0, // timer value at first byte
    last_byte_ns: u64 = 0,

    fn record(st: *Stats, n: u64, buf_size: u64, now_ns: u64) void {
        if (st.reads == 0) st.first_byte_ns = now_ns;
        st.last_byte_ns = now_ns;
        st.reads += 1;
        st.bytes += n;
        st.min = @min(st.min, n);
        st.max = @max(st.max, n);
        if (n == buf_size) st.full += 1;
        if (n == 1024) st.eq_1024 += 1;
        if (n < 1024) st.lt_1024 += 1;
    }

    fn mbps(st: *const Stats) f64 {
        const dur = st.last_byte_ns -| st.first_byte_ns;
        if (dur == 0) return 0;
        return @as(f64, @floatFromInt(st.bytes)) / @as(f64, @floatFromInt(dur)) * 1000.0;
    }
};

/// Busy-wait to simulate VT parse cost for `n` bytes.
fn simulateParse(timer: *std.time.Timer, n: u64, ns_per_byte: u64) void {
    if (ns_per_byte == 0) return;
    const deadline = timer.read() + n * ns_per_byte;
    while (timer.read() < deadline) {}
}

fn readEof(err: windows.Win32Error) bool {
    return switch (err) {
        .BROKEN_PIPE, .HANDLE_EOF, .NO_DATA => true,
        else => false,
    };
}

/// Serial loop: read(); simulate_parse(); repeat. This is the shape of
/// ghostty's current threadMainWindows.
fn runSerial(
    exe_w: [:0]const u16,
    buf: []u8,
    pipe_buf: windows.DWORD,
    parse_ns_per_byte: u64,
) !Stats {
    var s = try Session.spawn(exe_w, pipe_buf);
    defer s.deinit();
    const waiter = try std.Thread.spawn(.{}, Session.waitAndClose, .{&s});
    defer waiter.join();

    var timer = try std.time.Timer.start();
    var st: Stats = .{};
    while (true) {
        var n: windows.DWORD = 0;
        if (windows.kernel32.ReadFile(s.out_read, buf.ptr, @intCast(buf.len), &n, null) == 0) {
            if (readEof(windows.kernel32.GetLastError())) break;
            return error.ReadFailed;
        }
        if (n == 0) break;
        st.record(n, buf.len, timer.read());
        simulateParse(&timer, n, parse_ns_per_byte);
    }
    return st;
}

/// Two-stage pipeline mirroring upstream's gather thread: a ring of 4
/// x 64 KiB buffers; this (gather) thread drains the pipe while the
/// main thread simulates parsing the previous batch.
const Ring = struct {
    const buffer_count = 4;
    const buffer_capacity = 64 * 1024;

    mutex: std.Thread.Mutex = .{},
    published: std.Thread.Condition = .{},
    freed: std.Thread.Condition = .{},
    bufs: [buffer_count][buffer_capacity]u8 = undefined,
    lens: [buffer_count]usize = @splat(0),
    head: usize = 0, // next slot the consumer takes
    count: usize = 0, // published, unconsumed slots
    done: bool = false,

    read_stats: Stats = .{},

    fn gatherMain(r: *Ring, s: *Session, timer: *std.time.Timer) void {
        var slot_owned = false;
        var slot: usize = 0;
        while (true) {
            if (!slot_owned) {
                r.mutex.lock();
                while (r.count == Ring.buffer_count) r.freed.wait(&r.mutex);
                slot = (r.head + r.count) % Ring.buffer_count;
                r.mutex.unlock();
                slot_owned = true;
            }

            var n: windows.DWORD = 0;
            const ok = windows.kernel32.ReadFile(
                s.out_read,
                &r.bufs[slot],
                Ring.buffer_capacity,
                &n,
                null,
            );
            if (ok == 0 or n == 0) {
                r.mutex.lock();
                r.done = true;
                r.mutex.unlock();
                r.published.signal();
                return;
            }

            r.read_stats.record(n, Ring.buffer_capacity, timer.read());
            r.lens[slot] = n;
            r.mutex.lock();
            r.count += 1;
            r.mutex.unlock();
            r.published.signal();
            slot_owned = false;
        }
    }
};

fn runGather(
    exe_w: [:0]const u16,
    pipe_buf: windows.DWORD,
    parse_ns_per_byte: u64,
) !Stats {
    var s = try Session.spawn(exe_w, pipe_buf);
    defer s.deinit();
    const waiter = try std.Thread.spawn(.{}, Session.waitAndClose, .{&s});
    defer waiter.join();

    var timer = try std.time.Timer.start();
    const ring = try std.heap.page_allocator.create(Ring);
    defer std.heap.page_allocator.destroy(ring);
    ring.* = .{};

    const gatherer = try std.Thread.spawn(.{}, Ring.gatherMain, .{ ring, &s, &timer });
    defer gatherer.join();

    // Consumer: simulate parsing each published batch.
    while (true) {
        ring.mutex.lock();
        while (ring.count == 0 and !ring.done) ring.published.wait(&ring.mutex);
        if (ring.count == 0 and ring.done) {
            ring.mutex.unlock();
            break;
        }
        const slot = ring.head;
        ring.mutex.unlock();

        simulateParse(&timer, ring.lens[slot], parse_ns_per_byte);

        ring.mutex.lock();
        ring.head = (ring.head + 1) % Ring.buffer_count;
        ring.count -= 1;
        ring.mutex.unlock();
        ring.freed.signal();
    }

    return ring.read_stats;
}

// ---------------------------------------------------------------------
// Experiment matrix
// ---------------------------------------------------------------------

const Experiment = struct {
    name: []const u8,
    mode: enum { serial, gather },
    buf_size: usize, // serial only
    pipe_buf: windows.DWORD, // CreatePipe nSize (0 = system default)
    parse_ns_per_byte: u64,
};

const experiments = [_]Experiment{
    // Question 1+2: current shape vs read-buffer bump, drain-only.
    .{ .name = "serial buf=1KiB  pipe=def parse=0ns/B (current ghostty)", .mode = .serial, .buf_size = 1024, .pipe_buf = 0, .parse_ns_per_byte = 0 },
    .{ .name = "serial buf=64KiB pipe=def parse=0ns/B (buffer bump)", .mode = .serial, .buf_size = 64 * 1024, .pipe_buf = 0, .parse_ns_per_byte = 0 },
    // Question 2 under parse load (1 ns/B ~ 1 GB/s parser).
    .{ .name = "serial buf=1KiB  pipe=def parse=1ns/B", .mode = .serial, .buf_size = 1024, .pipe_buf = 0, .parse_ns_per_byte = 1 },
    .{ .name = "serial buf=64KiB pipe=def parse=1ns/B", .mode = .serial, .buf_size = 64 * 1024, .pipe_buf = 0, .parse_ns_per_byte = 1 },
    // Heavy parser (4 ns/B ~ 250 MB/s).
    .{ .name = "serial buf=1KiB  pipe=def parse=4ns/B", .mode = .serial, .buf_size = 1024, .pipe_buf = 0, .parse_ns_per_byte = 4 },
    .{ .name = "serial buf=64KiB pipe=def parse=4ns/B", .mode = .serial, .buf_size = 64 * 1024, .pipe_buf = 0, .parse_ns_per_byte = 4 },
    // Question 4: bigger ConPTY output pipe buffer.
    .{ .name = "serial buf=64KiB pipe=1MiB parse=1ns/B", .mode = .serial, .buf_size = 64 * 1024, .pipe_buf = 1 * MiB, .parse_ns_per_byte = 1 },
    // Question 3: gather thread (ring of 4 x 64KiB).
    .{ .name = "gather 4x64KiB   pipe=def parse=0ns/B", .mode = .gather, .buf_size = 0, .pipe_buf = 0, .parse_ns_per_byte = 0 },
    .{ .name = "gather 4x64KiB   pipe=def parse=1ns/B", .mode = .gather, .buf_size = 0, .pipe_buf = 0, .parse_ns_per_byte = 1 },
    .{ .name = "gather 4x64KiB   pipe=def parse=4ns/B", .mode = .gather, .buf_size = 0, .pipe_buf = 0, .parse_ns_per_byte = 4 },
    .{ .name = "gather 4x64KiB   pipe=1MiB parse=1ns/B", .mode = .gather, .buf_size = 0, .pipe_buf = 1 * MiB, .parse_ns_per_byte = 1 },
};

pub fn main() !void {
    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const args = try std.process.argsAlloc(alloc);
    if (args.len >= 3 and std.mem.eql(u8, args[1], "blast")) {
        childBlast(try std.fmt.parseInt(usize, args[2], 10));
        return;
    }

    // Self path in UTF-16 for CreateProcessW.
    var exe_buf: [std.fs.max_path_bytes]u8 = undefined;
    const exe_path = try std.fs.selfExePath(&exe_buf);
    var exe_w_buf: [std.fs.max_path_bytes]u16 = undefined;
    const exe_w_len = try std.unicode.utf8ToUtf16Le(&exe_w_buf, exe_path);
    exe_w_buf[exe_w_len] = 0;
    const exe_w = exe_w_buf[0..exe_w_len :0];

    std.debug.print(
        "conpty_probe: child blasts {d} MiB plain text per experiment\n" ++
            "(read-side byte counts; ConPTY re-renders so they differ from child-written bytes)\n\n",
        .{blast_mib},
    );
    std.debug.print(
        "{s:<55} {s:>9} {s:>12} {s:>9} {s:>7} {s:>7} {s:>7} {s:>7} {s:>8}\n",
        .{ "experiment", "reads", "bytes", "MB/s", "min", "avg", "max", "=1024", "full%" },
    );

    for (experiments) |ex| {
        // Warm-up run then measured run, to stabilize file cache /
        // process spawn effects.
        var st: Stats = undefined;
        for (0..2) |round| {
            st = switch (ex.mode) {
                .serial => blk: {
                    const buf = try alloc.alloc(u8, ex.buf_size);
                    defer alloc.free(buf);
                    break :blk try runSerial(exe_w, buf, ex.pipe_buf, ex.parse_ns_per_byte);
                },
                .gather => try runGather(exe_w, ex.pipe_buf, ex.parse_ns_per_byte),
            };
            _ = round;
        }

        const avg: u64 = if (st.reads == 0) 0 else st.bytes / st.reads;
        const full_pct: f64 = if (st.reads == 0) 0 else @as(f64, @floatFromInt(st.full)) * 100.0 / @as(f64, @floatFromInt(st.reads));
        std.debug.print(
            "{s:<55} {d:>9} {d:>12} {d:>9.1} {d:>7} {d:>7} {d:>7} {d:>7} {d:>7.1}%\n",
            .{ ex.name, st.reads, st.bytes, st.mbps(), st.min, avg, st.max, st.eq_1024, full_pct },
        );
    }
}
