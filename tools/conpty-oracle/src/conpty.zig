//! ConPTY spawn + capture.
//!
//! Runs an arbitrary Windows console program under an in-box pseudoconsole
//! (`kernel32.CreatePseudoConsole`) of a fixed size and captures every byte
//! conhost renders to the output pipe until EOF.
//!
//! The spawn shape is deliberately identical to the debugged probe
//! (scratchpad conpty_probe.zig) and ghostty's own Command.zig:
//! `STARTF_USESTDHANDLES` with null std handles + `bInheritHandles = TRUE`
//! + `EXTENDED_STARTUPINFO_PRESENT` + the PSEUDOCONSOLE attribute list.
//! Getting this wrong makes the child bind to the wrong console.

const std = @import("std");
const windows = std.os.windows;
const Allocator = std.mem.Allocator;

const HPCON = windows.LPVOID;
const LPPROC_THREAD_ATTRIBUTE_LIST = ?*anyopaque;
const EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
const CREATE_NO_WINDOW = 0x08000000;
const CREATE_NEW_CONSOLE = 0x00000010;
// ProcThreadAttributeValue(22, false, true, false)
const PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE: usize = 22 | 0x00020000;

/// How long we let the child run before the watchdog terminates it so a
/// stuck program can't hang the oracle. Overridable via the
/// CONPTY_ORACLE_WATCHDOG_MS env var (CI lowers it so input-reading
/// programs don't each burn the full default).
const watchdog_ms_default: windows.DWORD = 60 * 1000;

fn watchdogMs() windows.DWORD {
    const v = std.process.getEnvVarOwned(std.heap.page_allocator, "CONPTY_ORACLE_WATCHDOG_MS") catch return watchdog_ms_default;
    defer std.heap.page_allocator.free(v);
    const trimmed = std.mem.trim(u8, v, " \t\r\n");
    return std.fmt.parseInt(windows.DWORD, trimmed, 10) catch watchdog_ms_default;
}

/// When CONPTY_ORACLE_RAW_LF_TO_CRLF is set (to anything but "0"), the
/// raw-pipe capture translates bare LF to CR+LF, reproducing the console's
/// ENABLE_PROCESSED_OUTPUT newline handling that a production raw-pipe
/// transport would have to provide. Used to validate that doing so makes a
/// bare-LF program cell-identical to ConPTY.
fn rawLfToCrlf() bool {
    const v = std.process.getEnvVarOwned(std.heap.page_allocator, "CONPTY_ORACLE_RAW_LF_TO_CRLF") catch return false;
    defer std.heap.page_allocator.free(v);
    return !std.mem.eql(u8, std.mem.trim(u8, v, " \t\r\n"), "0");
}

const STARTUPINFOEX = extern struct {
    StartupInfo: windows.STARTUPINFOW,
    lpAttributeList: LPPROC_THREAD_ATTRIBUTE_LIST,
};

// Win32 declarations not in std.os.windows (signatures copied from
// ghostty src/os/windows.zig `exp`).
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
    extern "kernel32" fn ResizePseudoConsole(
        hPC: HPCON,
        size: windows.COORD,
    ) callconv(.winapi) windows.HRESULT;
    extern "kernel32" fn PeekNamedPipe(
        hNamedPipe: windows.HANDLE,
        lpBuffer: ?windows.LPVOID,
        nBufferSize: windows.DWORD,
        lpBytesRead: ?*windows.DWORD,
        lpTotalBytesAvail: ?*windows.DWORD,
        lpBytesLeftThisMessage: ?*windows.DWORD,
    ) callconv(.winapi) windows.BOOL;
};

/// Drain `read_h` into `out` until no bytes arrive for `quiet_ms`, or the
/// hard `max_ms` cap elapses, or the pipe closes (EOF). Unlike the one-shot
/// captures, an interactive child stays alive across a resize, so there is
/// no EOF to stop on mid-run — we stop on quiescence. Polls availability
/// with PeekNamedPipe (works on anonymous pipes) so a blocking ReadFile
/// can't wedge on a live-but-idle child.
fn drainUntilQuiet(
    alloc: Allocator,
    out: *std.ArrayList(u8),
    read_h: windows.HANDLE,
    quiet_ms: u64,
    max_ms: u64,
) !void {
    var buf: [64 * 1024]u8 = undefined;
    const step_ms: u64 = 10;
    var idle: u64 = 0;
    var elapsed: u64 = 0;
    while (elapsed < max_ms) {
        var avail: windows.DWORD = 0;
        const ok = k32.PeekNamedPipe(read_h, null, 0, null, &avail, null);
        if (ok == 0) break; // pipe closed/broken => EOF
        if (avail > 0) {
            var n: windows.DWORD = 0;
            const want: windows.DWORD = @min(avail, @as(windows.DWORD, buf.len));
            if (windows.kernel32.ReadFile(read_h, &buf, want, &n, null) != 0 and n > 0) {
                try out.appendSlice(alloc, buf[0..n]);
                idle = 0;
                continue; // more may be waiting; drain greedily
            }
        }
        std.Thread.sleep(step_ms * std.time.ns_per_ms);
        idle += step_ms;
        elapsed += step_ms;
        if (idle >= quiet_ms) break;
    }
}

/// A ConPTY session: pipes + pseudoconsole + child, mirroring ghostty
/// src/pty.zig WindowsPty.open (anonymous output pipe, in-box API).
const Session = struct {
    in_read: windows.HANDLE, // conhost reads child input here (unused)
    in_write: windows.HANDLE,
    out_read: windows.HANDLE, // we read rendered output here
    out_write: windows.HANDLE, // conhost writes here
    hpcon: HPCON,
    child: windows.HANDLE,
    child_thread: windows.HANDLE,

    /// Spawn `cmd_w` (a full command line, exe path already quoted) under
    /// a fresh pseudoconsole of size cols x rows.
    fn spawn(cmd_w: [:0]u16, cols: u16, rows: u16) !Session {
        var s: Session = undefined;

        if (k32.CreatePipe(&s.in_read, &s.in_write, null, 0) == 0)
            return error.CreatePipe;
        if (k32.CreatePipe(&s.out_read, &s.out_write, null, 0) == 0)
            return error.CreatePipe;

        if (k32.CreatePseudoConsole(
            .{ .X = @intCast(cols), .Y = @intCast(rows) },
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

        // STARTF_USESTDHANDLES with null std handles prevents the child
        // from binding to the parent's console/stdio; the pseudoconsole
        // attribute is then the child's sole console connection.
        var siex = std.mem.zeroes(STARTUPINFOEX);
        siex.StartupInfo.cb = @sizeOf(STARTUPINFOEX);
        siex.StartupInfo.dwFlags = windows.STARTF_USESTDHANDLES;
        siex.lpAttributeList = attr_list;
        var pi = std.mem.zeroes(windows.PROCESS_INFORMATION);

        if (k32.CreateProcessW(
            null,
            cmd_w.ptr,
            null,
            null,
            windows.TRUE,
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

    /// Waiter-thread body: once the child exits, close the pseudoconsole
    /// and our copy of the conhost-side write handle so the reader
    /// observes pipe EOF after the final flush. The watchdog terminates
    /// a stuck child so a broken program can't hang the oracle.
    fn waitAndClose(s: *Session) void {
        const WAIT_TIMEOUT = 0x102;
        const ms = watchdogMs();
        if (k32.WaitForSingleObject(s.child, ms) == WAIT_TIMEOUT) {
            std.debug.print(
                "conpty-oracle: watchdog: child stuck after {d}ms; terminating\n",
                .{ms},
            );
            windows.TerminateProcess(s.child, 1) catch {};
            _ = k32.WaitForSingleObject(s.child, windows.INFINITE);
        }
        k32.ClosePseudoConsole(s.hpcon);
        windows.CloseHandle(s.out_write);
    }

    fn deinit(s: *Session) void {
        windows.CloseHandle(s.child_thread);
        windows.CloseHandle(s.child);
        windows.CloseHandle(s.in_read);
        windows.CloseHandle(s.in_write);
        windows.CloseHandle(s.out_read);
        // out_write and hpcon are closed by waitAndClose.
    }
};

fn readEof(err: windows.Win32Error) bool {
    return switch (err) {
        .BROKEN_PIPE, .HANDLE_EOF, .NO_DATA => true,
        else => false,
    };
}

/// Run `exe_path` under a cols x rows ConPTY and return every byte conhost
/// rendered to the output pipe, drained to EOF. Caller owns the result.
pub fn capture(
    alloc: Allocator,
    exe_path: []const u8,
    cols: u16,
    rows: u16,
) ![]u8 {
    // Build `"<exe>"` as a null-terminated UTF-16 command line. Quoting
    // the path is sufficient: '"' is not a legal filename character on
    // Windows, so no embedded-quote escaping is needed.
    const cmd_utf8 = try std.fmt.allocPrint(alloc, "\"{s}\"", .{exe_path});
    defer alloc.free(cmd_utf8);
    const cmd_w = try std.unicode.utf8ToUtf16LeAllocZ(alloc, cmd_utf8);
    defer alloc.free(cmd_w);

    var s = try Session.spawn(cmd_w, cols, rows);
    defer s.deinit();
    const waiter = try std.Thread.spawn(.{}, Session.waitAndClose, .{&s});
    defer waiter.join();

    // Drain the output pipe with blocking reads in 64 KiB chunks until
    // EOF (BROKEN_PIPE / zero-byte read after waitAndClose closes the
    // conhost-side write handle).
    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(alloc);
    var buf: [64 * 1024]u8 = undefined;
    while (true) {
        var n: windows.DWORD = 0;
        if (windows.kernel32.ReadFile(s.out_read, &buf, buf.len, &n, null) == 0) {
            if (readEof(windows.kernel32.GetLastError())) break;
            return error.ReadFailed;
        }
        if (n == 0) break;
        try out.appendSlice(alloc, buf[0..n]);
    }

    return out.toOwnedSlice(alloc);
}

/// Run `exe_path` with its stdout redirected straight to an anonymous
/// pipe — NO pseudoconsole, no conhost in the data path — and return
/// every byte it writes, drained to EOF. `CREATE_NO_WINDOW` gives the
/// child a hidden console, but since stdout is the pipe, a program that
/// writes VT via its std output handle reaches us verbatim. A program
/// that instead relies on the Console API (WriteConsoleOutput, etc.)
/// produces nothing here — that empty result is the VT-native boundary,
/// not an error. Caller owns the result.
pub fn captureRawPipe(alloc: Allocator, exe_path: []const u8) ![]u8 {
    var read_h: windows.HANDLE = undefined;
    var write_h: windows.HANDLE = undefined;
    var sa = windows.SECURITY_ATTRIBUTES{
        .nLength = @sizeOf(windows.SECURITY_ATTRIBUTES),
        .bInheritHandle = windows.TRUE, // child inherits the write end
        .lpSecurityDescriptor = null,
    };
    if (k32.CreatePipe(&read_h, &write_h, &sa, 0) == 0) return error.CreatePipe;
    defer windows.CloseHandle(read_h);
    // Our read end must not leak into the child.
    try windows.SetHandleInformation(read_h, windows.HANDLE_FLAG_INHERIT, 0);

    const cmd_utf8 = try std.fmt.allocPrint(alloc, "\"{s}\"", .{exe_path});
    defer alloc.free(cmd_utf8);
    const cmd_w = try std.unicode.utf8ToUtf16LeAllocZ(alloc, cmd_utf8);
    defer alloc.free(cmd_w);

    var si = std.mem.zeroes(windows.STARTUPINFOW);
    si.cb = @sizeOf(windows.STARTUPINFOW);
    si.dwFlags = windows.STARTF_USESTDHANDLES;
    si.hStdOutput = write_h;
    si.hStdError = write_h;
    si.hStdInput = null;
    var pi = std.mem.zeroes(windows.PROCESS_INFORMATION);

    if (k32.CreateProcessW(
        null,
        cmd_w.ptr,
        null,
        null,
        windows.TRUE,
        CREATE_NO_WINDOW,
        null,
        null,
        &si,
        &pi,
    ) == 0) return error.CreateProcess;
    defer windows.CloseHandle(pi.hProcess);
    defer windows.CloseHandle(pi.hThread);
    // Close our copy of the write end so the read sees EOF at child exit.
    windows.CloseHandle(write_h);

    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(alloc);
    var buf: [64 * 1024]u8 = undefined;
    while (true) {
        var n: windows.DWORD = 0;
        if (windows.kernel32.ReadFile(read_h, &buf, buf.len, &n, null) == 0) {
            if (readEof(windows.kernel32.GetLastError())) break;
            return error.ReadFailed;
        }
        if (n == 0) break;
        try out.appendSlice(alloc, buf[0..n]);
    }

    const raw = try out.toOwnedSlice(alloc);
    if (!rawLfToCrlf()) return raw;

    // Reproduce the console's ENABLE_PROCESSED_OUTPUT line-control handling:
    // conhost treats LF (0x0A), VT (0x0B) and FF (0x0C) all as a newline
    // (column 1 + down), where a raw VT parser treats them as index (down,
    // same column). Insert a CR before any of them not already preceded by
    // one, so `CR + <ctrl>` == newline. (A doubled CR is idempotent, so this
    // is safe even for streams that already use CRLF.)
    defer alloc.free(raw);
    var xl: std.ArrayList(u8) = .empty;
    errdefer xl.deinit(alloc);
    for (raw) |b| {
        const is_line_ctrl = b == '\n' or b == 0x0b or b == 0x0c;
        if (is_line_ctrl and (xl.items.len == 0 or xl.items[xl.items.len - 1] != '\r'))
            try xl.append(alloc, '\r');
        try xl.append(alloc, b);
    }
    return xl.toOwnedSlice(alloc);
}

/// Resize timing knobs. Quiescence window (how long output must be silent
/// before a drain is considered complete) and the hard per-drain cap.
const resize_quiet_ms: u64 = 300;
const resize_max_ms: u64 = 8000;

/// Run `exe_path` under a cols0 x rows0 ConPTY, let it settle, resize the
/// pseudoconsole to cols1 x rows1 (which conhost signals to the child as a
/// WINDOW_BUFFER_SIZE_EVENT), let it settle again, and return everything
/// conhost rendered across both phases. This is the ConPTY arm of the
/// resize-fidelity comparison: the classic console-API resize path a
/// raw-pipe transport has to substitute for.
pub fn captureResize(
    alloc: Allocator,
    exe_path: []const u8,
    cols0: u16,
    rows0: u16,
    cols1: u16,
    rows1: u16,
) ![]u8 {
    const cmd_utf8 = try std.fmt.allocPrint(alloc, "\"{s}\"", .{exe_path});
    defer alloc.free(cmd_utf8);
    const cmd_w = try std.unicode.utf8ToUtf16LeAllocZ(alloc, cmd_utf8);
    defer alloc.free(cmd_w);

    var s = try Session.spawn(cmd_w, cols0, rows0);

    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(alloc);

    // Phase 1: initial paint (the child emits its "READY" banner).
    try drainUntilQuiet(alloc, &out, s.out_read, resize_quiet_ms, resize_max_ms);

    // Trigger the resize. conhost delivers a WINDOW_BUFFER_SIZE_EVENT to the
    // child and repaints its buffer at the new size.
    if (k32.ResizePseudoConsole(
        s.hpcon,
        .{ .X = @intCast(cols1), .Y = @intCast(rows1) },
    ) != windows.S_OK) return error.ResizePseudoConsole;

    // Phase 2: the child's redraw at the new size.
    try drainUntilQuiet(alloc, &out, s.out_read, resize_quiet_ms, resize_max_ms);

    // Tear down: the child may still be blocked reading input, so terminate
    // it, then close the pseudoconsole and handles (no waiter thread here).
    windows.TerminateProcess(s.child, 0) catch {};
    _ = k32.WaitForSingleObject(s.child, 2000);
    k32.ClosePseudoConsole(s.hpcon);
    windows.CloseHandle(s.out_write);
    s.deinit();

    return out.toOwnedSlice(alloc);
}

/// Run `exe_path` over a raw pipe (no conhost) with a live stdin pipe, let
/// it settle, then emit an in-band size report (`CSI 48;rows;cols;hpix;wpix
/// t`, byte-for-byte what ghostty's `size_report.zig` writes) on its stdin —
/// exactly what a raw-pipe transport does on resize — let it settle, and
/// return everything the child wrote. This is the candidate arm: resize via
/// DECSET 2048 instead of ResizePseudoConsole. Pixel fields use 9x18 cells
/// (they don't affect the child's grid; the child parses only rows/cols).
pub fn captureRawPipeResize(
    alloc: Allocator,
    exe_path: []const u8,
    cols1: u16,
    rows1: u16,
) ![]u8 {
    // Output pipe: child stdout -> us.
    var out_read: windows.HANDLE = undefined;
    var out_write: windows.HANDLE = undefined;
    // Input pipe: us -> child stdin.
    var in_read: windows.HANDLE = undefined;
    var in_write: windows.HANDLE = undefined;
    var sa = windows.SECURITY_ATTRIBUTES{
        .nLength = @sizeOf(windows.SECURITY_ATTRIBUTES),
        .bInheritHandle = windows.TRUE,
        .lpSecurityDescriptor = null,
    };
    if (k32.CreatePipe(&out_read, &out_write, &sa, 0) == 0) return error.CreatePipe;
    if (k32.CreatePipe(&in_read, &in_write, &sa, 0) == 0) return error.CreatePipe;
    // Our ends must not leak into the child.
    try windows.SetHandleInformation(out_read, windows.HANDLE_FLAG_INHERIT, 0);
    try windows.SetHandleInformation(in_write, windows.HANDLE_FLAG_INHERIT, 0);

    const cmd_utf8 = try std.fmt.allocPrint(alloc, "\"{s}\"", .{exe_path});
    defer alloc.free(cmd_utf8);
    const cmd_w = try std.unicode.utf8ToUtf16LeAllocZ(alloc, cmd_utf8);
    defer alloc.free(cmd_w);

    var si = std.mem.zeroes(windows.STARTUPINFOW);
    si.cb = @sizeOf(windows.STARTUPINFOW);
    si.dwFlags = windows.STARTF_USESTDHANDLES;
    si.hStdOutput = out_write;
    si.hStdError = out_write;
    si.hStdInput = in_read;
    var pi = std.mem.zeroes(windows.PROCESS_INFORMATION);

    if (k32.CreateProcessW(
        null,
        cmd_w.ptr,
        null,
        null,
        windows.TRUE,
        CREATE_NO_WINDOW,
        null,
        null,
        &si,
        &pi,
    ) == 0) return error.CreateProcess;
    // Close the child-side ends we no longer need so EOF propagates at exit.
    windows.CloseHandle(out_write);
    windows.CloseHandle(in_read);

    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(alloc);

    // Phase 1: initial paint ("READY").
    try drainUntilQuiet(alloc, &out, out_read, resize_quiet_ms, resize_max_ms);

    // Emit the in-band size report on the child's stdin.
    var rep_buf: [64]u8 = undefined;
    const report = std.fmt.bufPrint(
        &rep_buf,
        "\x1b[48;{d};{d};{d};{d}t",
        .{ rows1, cols1, @as(u32, rows1) * 18, @as(u32, cols1) * 9 },
    ) catch unreachable;
    var written: windows.DWORD = 0;
    _ = windows.kernel32.WriteFile(in_write, report.ptr, @intCast(report.len), &written, null);

    // Phase 2: the child's redraw at the new size (it then exits -> EOF).
    try drainUntilQuiet(alloc, &out, out_read, resize_quiet_ms, resize_max_ms);

    windows.TerminateProcess(pi.hProcess, 0) catch {};
    _ = k32.WaitForSingleObject(pi.hProcess, 2000);
    windows.CloseHandle(pi.hProcess);
    windows.CloseHandle(pi.hThread);
    windows.CloseHandle(in_write);
    windows.CloseHandle(out_read);

    return out.toOwnedSlice(alloc);
}

/// Outcome of a signal-delivery probe: what the child printed (contains
/// GOT-SIGNAL / NO-SIGNAL) plus the courier's exit code (raw-pipe arm only;
/// 0xffff_ffff on the ConPTY arm, which delivers 0x03 with no courier).
pub const SignalResult = struct {
    output: []u8,
    helper_rc: u32,

    pub fn gotSignal(self: SignalResult) bool {
        return std.mem.indexOf(u8, self.output, "GOT-SIGNAL") != null;
    }
};

const no_helper: u32 = 0xffff_ffff;

/// Spawn a bare process (no redirected IO, no inherited handles), wait for
/// it, and return its exit code. Used to run the Ctrl-C courier.
fn spawnWait(alloc: Allocator, cmd_w: [:0]u16) !u32 {
    var si = std.mem.zeroes(windows.STARTUPINFOW);
    si.cb = @sizeOf(windows.STARTUPINFOW);
    var pi = std.mem.zeroes(windows.PROCESS_INFORMATION);
    _ = alloc;
    if (k32.CreateProcessW(
        null,
        cmd_w.ptr,
        null,
        null,
        windows.FALSE,
        CREATE_NO_WINDOW,
        null,
        null,
        &si,
        &pi,
    ) == 0) return error.CreateProcess;
    defer windows.CloseHandle(pi.hProcess);
    defer windows.CloseHandle(pi.hThread);
    _ = k32.WaitForSingleObject(pi.hProcess, 5000);
    var code: windows.DWORD = 0;
    _ = windows.kernel32.GetExitCodeProcess(pi.hProcess, &code);
    return code;
}

/// ConPTY baseline: run `child_exe` under a pseudoconsole, wait for its READY
/// banner, then write 0x03 (Ctrl-C) to the ConPTY input pipe — conhost, which
/// owns the child's console, raises CTRL_C_EVENT. Return what the child
/// printed. This is the behaviour a raw-pipe transport has to reproduce.
pub fn signalProbeConpty(
    alloc: Allocator,
    child_exe: []const u8,
) !SignalResult {
    const cmd_utf8 = try std.fmt.allocPrint(alloc, "\"{s}\"", .{child_exe});
    defer alloc.free(cmd_utf8);
    const cmd_w = try std.unicode.utf8ToUtf16LeAllocZ(alloc, cmd_utf8);
    defer alloc.free(cmd_w);

    var s = try Session.spawn(cmd_w, 80, 25);

    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(alloc);

    // Wait for READY (the child has registered its handler and is blocked).
    try drainUntilQuiet(alloc, &out, s.out_read, 300, 4000);

    // Deliver Ctrl-C as a byte on the console input pipe.
    const etx = [_]u8{0x03};
    var w: windows.DWORD = 0;
    _ = windows.kernel32.WriteFile(s.in_write, &etx, 1, &w, null);

    // Capture the child's reaction (GOT-SIGNAL) as it runs to exit.
    try drainUntilQuiet(alloc, &out, s.out_read, 400, 4000);

    windows.TerminateProcess(s.child, 0) catch {};
    _ = k32.WaitForSingleObject(s.child, 2000);
    k32.ClosePseudoConsole(s.hpcon);
    windows.CloseHandle(s.out_write);
    s.deinit();

    return .{ .output = try out.toOwnedSlice(alloc), .helper_rc = no_helper };
}

/// Raw-pipe candidate: run `child_exe` with its stdout on a pipe (no conhost
/// in the data path) but its OWN console (CREATE_NEW_CONSOLE) so it can
/// receive console control events. Wait for READY, then run `helper_exe`
/// (the AttachConsole courier) against the child's PID to deliver `kind`
/// ('C' = Ctrl-C, 'B' = Ctrl-Break) injection-free. Return what the child
/// printed plus the courier's exit code.
pub fn signalProbeRawPipe(
    alloc: Allocator,
    child_exe: []const u8,
    helper_exe: []const u8,
    kind: u8,
) !SignalResult {
    // Output pipe: child stdout -> us.
    var out_read: windows.HANDLE = undefined;
    var out_write: windows.HANDLE = undefined;
    var sa = windows.SECURITY_ATTRIBUTES{
        .nLength = @sizeOf(windows.SECURITY_ATTRIBUTES),
        .bInheritHandle = windows.TRUE,
        .lpSecurityDescriptor = null,
    };
    if (k32.CreatePipe(&out_read, &out_write, &sa, 0) == 0) return error.CreatePipe;
    try windows.SetHandleInformation(out_read, windows.HANDLE_FLAG_INHERIT, 0);

    const cmd_utf8 = try std.fmt.allocPrint(alloc, "\"{s}\"", .{child_exe});
    defer alloc.free(cmd_utf8);
    const cmd_w = try std.unicode.utf8ToUtf16LeAllocZ(alloc, cmd_utf8);
    defer alloc.free(cmd_w);

    var si = std.mem.zeroes(windows.STARTUPINFOW);
    si.cb = @sizeOf(windows.STARTUPINFOW);
    si.dwFlags = windows.STARTF_USESTDHANDLES;
    si.hStdOutput = out_write;
    si.hStdError = out_write;
    si.hStdInput = null;
    var pi = std.mem.zeroes(windows.PROCESS_INFORMATION);

    // CREATE_NEW_CONSOLE gives the child its own console (attachable by the
    // courier) while STARTF_USESTDHANDLES keeps its stdout on our pipe, so
    // output still bypasses conhost. This is the raw-pipe-with-signals model.
    if (k32.CreateProcessW(
        null,
        cmd_w.ptr,
        null,
        null,
        windows.TRUE,
        CREATE_NEW_CONSOLE,
        null,
        null,
        &si,
        &pi,
    ) == 0) return error.CreateProcess;
    windows.CloseHandle(out_write);

    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(alloc);

    // Wait for READY.
    try drainUntilQuiet(alloc, &out, out_read, 300, 4000);

    // Run the courier against the child's PID.
    const kind_str = if (kind == 'B' or kind == 'b') "B" else "C";
    const hc_utf8 = try std.fmt.allocPrint(
        alloc,
        "\"{s}\" {d} {s}",
        .{ helper_exe, pi.dwProcessId, kind_str },
    );
    defer alloc.free(hc_utf8);
    const hc_w = try std.unicode.utf8ToUtf16LeAllocZ(alloc, hc_utf8);
    defer alloc.free(hc_w);
    const helper_rc = spawnWait(alloc, hc_w) catch 0xffff_fffe;

    // Capture the child's reaction (GOT-SIGNAL) as it runs to exit (EOF).
    try drainUntilQuiet(alloc, &out, out_read, 400, 4000);

    windows.TerminateProcess(pi.hProcess, 0) catch {};
    _ = k32.WaitForSingleObject(pi.hProcess, 2000);
    windows.CloseHandle(pi.hProcess);
    windows.CloseHandle(pi.hThread);
    windows.CloseHandle(out_read);

    return .{ .output = try out.toOwnedSlice(alloc), .helper_rc = helper_rc };
}
