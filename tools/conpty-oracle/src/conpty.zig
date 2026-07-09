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
const DETACHED_PROCESS = 0x00000008;
const CREATE_NEW_PROCESS_GROUP = 0x00000200;
const CTRL_BREAK_EVENT: windows.DWORD = 1;
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
    extern "kernel32" fn GenerateConsoleCtrlEvent(
        dwCtrlEvent: windows.DWORD,
        dwProcessGroupId: windows.DWORD,
    ) callconv(.winapi) windows.BOOL;
    extern "kernel32" fn SetConsoleCtrlHandler(
        HandlerRoutine: ?*anyopaque,
        Add: windows.BOOL,
    ) callconv(.winapi) windows.BOOL;
    extern "kernel32" fn CreateJobObjectW(
        lpJobAttributes: ?*windows.SECURITY_ATTRIBUTES,
        lpName: ?windows.LPCWSTR,
    ) callconv(.winapi) ?windows.HANDLE;
    extern "kernel32" fn AssignProcessToJobObject(
        hJob: windows.HANDLE,
        hProcess: windows.HANDLE,
    ) callconv(.winapi) windows.BOOL;
    extern "kernel32" fn SetInformationJobObject(
        hJob: windows.HANDLE,
        JobObjectInformationClass: windows.DWORD,
        lpJobObjectInformation: *anyopaque,
        cbJobObjectInformationLength: windows.DWORD,
    ) callconv(.winapi) windows.BOOL;
    extern "kernel32" fn ResumeThread(hThread: windows.HANDLE) callconv(.winapi) windows.DWORD;
    extern "kernel32" fn OpenProcess(
        dwDesiredAccess: windows.DWORD,
        bInheritHandle: windows.BOOL,
        dwProcessId: windows.DWORD,
    ) callconv(.winapi) ?windows.HANDLE;
};

const CREATE_SUSPENDED = 0x00000004;
const JobObjectExtendedLimitInformation: windows.DWORD = 9;
const JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: u32 = 0x2000;
const STILL_ACTIVE: windows.DWORD = 259;
const PROCESS_QUERY_SYNC: windows.DWORD = 0x00100000 | 0x0400; // SYNCHRONIZE|QUERY_INFORMATION

const JOBOBJECT_BASIC_LIMIT_INFORMATION = extern struct {
    PerProcessUserTimeLimit: i64,
    PerJobUserTimeLimit: i64,
    LimitFlags: u32,
    MinimumWorkingSetSize: usize,
    MaximumWorkingSetSize: usize,
    ActiveProcessLimit: u32,
    Affinity: usize,
    PriorityClass: u32,
    SchedulingClass: u32,
};
const IO_COUNTERS = extern struct {
    ReadOperationCount: u64,
    WriteOperationCount: u64,
    OtherOperationCount: u64,
    ReadTransferCount: u64,
    WriteTransferCount: u64,
    OtherTransferCount: u64,
};
const JOBOBJECT_EXTENDED_LIMIT_INFORMATION = extern struct {
    BasicLimitInformation: JOBOBJECT_BASIC_LIMIT_INFORMATION,
    IoInfo: IO_COUNTERS,
    ProcessMemoryLimit: usize,
    JobMemoryLimit: usize,
    PeakProcessMemoryUsed: usize,
    PeakJobMemoryUsed: usize,
};

fn processActive(h: windows.HANDLE) bool {
    var code: windows.DWORD = 0;
    if (windows.kernel32.GetExitCodeProcess(h, &code) == 0) return false;
    return code == STILL_ACTIVE;
}

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
    // DETACHED_PROCESS: the courier starts with no console of its own, so its
    // FreeConsole() is a no-op and AttachConsole(target) runs from a clean
    // state (avoids ERROR_ACCESS_DENIED from being already-attached).
    if (k32.CreateProcessW(
        null,
        cmd_w.ptr,
        null,
        null,
        windows.FALSE,
        DETACHED_PROCESS,
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

/// Raw-pipe candidate, console-process-group variant. Instead of the child
/// owning a separate console (which a detached courier can't AttachConsole to
/// on a headless/service session — ERROR_INVALID_HANDLE), the child inherits
/// THIS process's console but as its own process GROUP
/// (CREATE_NEW_PROCESS_GROUP), stdout still on our pipe. We then target only
/// the child's group with GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT,
/// child_pid) — the mechanism a helper-owned-console transport uses — after
/// masking the event in ourselves. This proves injection-free console-signal
/// delivery to a pipe-output child without depending on cross-station
/// AttachConsole. (CTRL_C_EVENT is group-0-only by Windows design, so the
/// targeted test uses CTRL_BREAK; the delivery path is identical.)
///
/// helper_rc: 0 = event generated, 0x9999 = GenerateConsoleCtrlEvent failed.
pub fn signalProbeProcessGroup(
    alloc: Allocator,
    child_exe: []const u8,
) !SignalResult {
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

    // No new console: the child inherits ours, but as its own process group
    // so we can target it alone. Output still goes to the pipe.
    if (k32.CreateProcessW(
        null,
        cmd_w.ptr,
        null,
        null,
        windows.TRUE,
        CREATE_NEW_PROCESS_GROUP,
        null,
        null,
        &si,
        &pi,
    ) == 0) return error.CreateProcess;
    windows.CloseHandle(out_write);

    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(alloc);

    try drainUntilQuiet(alloc, &out, out_read, 300, 4000);

    // Mask the event in ourselves (belt-and-suspenders — we target the
    // child's group, not group 0), fire it, then unmask.
    _ = k32.SetConsoleCtrlHandler(null, windows.TRUE);
    const ok = k32.GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pi.dwProcessId);
    const rc: u32 = if (ok != 0) 0 else 0x9999;

    try drainUntilQuiet(alloc, &out, out_read, 400, 4000);
    _ = k32.SetConsoleCtrlHandler(null, windows.FALSE);

    windows.TerminateProcess(pi.hProcess, 0) catch {};
    _ = k32.WaitForSingleObject(pi.hProcess, 2000);
    windows.CloseHandle(pi.hProcess);
    windows.CloseHandle(pi.hThread);
    windows.CloseHandle(out_read);

    return .{ .output = try out.toOwnedSlice(alloc), .helper_rc = rc };
}

/// Drain `read_h` into `out` until the pipe closes (EOF) or `max_ms` elapses.
/// Returns true if EOF was reached (all writers gone), false on timeout —
/// which, when the writer is a process tree, means a leaked descendant is
/// still holding the write end (a read-loop wedge).
fn drainToEof(
    alloc: Allocator,
    out: *std.ArrayList(u8),
    read_h: windows.HANDLE,
    max_ms: u64,
) !bool {
    var buf: [64 * 1024]u8 = undefined;
    const step_ms: u64 = 20;
    var elapsed: u64 = 0;
    while (elapsed < max_ms) {
        var avail: windows.DWORD = 0;
        const ok = k32.PeekNamedPipe(read_h, null, 0, null, &avail, null);
        if (ok == 0) return true; // EOF: every writer closed
        if (avail > 0) {
            var n: windows.DWORD = 0;
            const want: windows.DWORD = @min(avail, @as(windows.DWORD, buf.len));
            if (windows.kernel32.ReadFile(read_h, &buf, want, &n, null) != 0 and n > 0) {
                try out.appendSlice(alloc, buf[0..n]);
                continue;
            }
        }
        std.Thread.sleep(step_ms * std.time.ns_per_ms);
        elapsed += step_ms;
    }
    return false; // timed out: a descendant still holds the pipe (wedge)
}

/// Parse the decimal that follows `tag` (e.g. "CHILD:") in `buf`.
fn parseTaggedPid(buf: []const u8, tag: []const u8) u32 {
    const at = std.mem.indexOf(u8, buf, tag) orelse return 0;
    var i = at + tag.len;
    var v: u32 = 0;
    while (i < buf.len and buf[i] >= '0' and buf[i] <= '9') : (i += 1) {
        v = v * 10 + (buf[i] - '0');
    }
    return v;
}

pub const TeardownResult = struct {
    assign_ok: bool, // AssignProcessToJobObject succeeded (no ConPTY job conflict)
    assign_err: u32, // GetLastError when assign failed (0 on success)
    child_pid: u32,
    grand_pid: u32,
    alive_before: bool, // both processes STILL_ACTIVE before the job closes
    dead_after: bool, // both exited within timeout after the job closes
    no_wedge: bool, // the output pipe reached EOF after kill (no leaked writer)
    output: []u8,
};

/// The teardown spike: spawn `child_exe` (which forks a grandchild that
/// inherits the stdout pipe) over a raw pipe with NO ConPTY, place it in a
/// job with KILL_ON_JOB_CLOSE, verify the whole tree is alive, then close the
/// job handle and verify (a) both child AND grandchild die — no leak, and (b)
/// the pipe reaches EOF — no read-loop wedge. This is the POSIX-equivalent
/// tree kill a raw-pipe transport gets precisely because there is no ConPTY
/// job object to conflict with.
pub fn teardownProbe(alloc: Allocator, child_exe: []const u8) !TeardownResult {
    var out_read: windows.HANDLE = undefined;
    var out_write: windows.HANDLE = undefined;
    var sa = windows.SECURITY_ATTRIBUTES{
        .nLength = @sizeOf(windows.SECURITY_ATTRIBUTES),
        .bInheritHandle = windows.TRUE,
        .lpSecurityDescriptor = null,
    };
    if (k32.CreatePipe(&out_read, &out_write, &sa, 0) == 0) return error.CreatePipe;
    try windows.SetHandleInformation(out_read, windows.HANDLE_FLAG_INHERIT, 0);

    const job = k32.CreateJobObjectW(null, null) orelse return error.CreateJobObject;
    var eli = std.mem.zeroes(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
    eli.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (k32.SetInformationJobObject(
        job,
        JobObjectExtendedLimitInformation,
        &eli,
        @sizeOf(JOBOBJECT_EXTENDED_LIMIT_INFORMATION),
    ) == 0) return error.SetJobInfo;

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

    // CREATE_SUSPENDED so we can assign to the job before the child forks —
    // guaranteeing the grandchild is born inside the job too.
    if (k32.CreateProcessW(
        null,
        cmd_w.ptr,
        null,
        null,
        windows.TRUE,
        CREATE_SUSPENDED,
        null,
        null,
        &si,
        &pi,
    ) == 0) return error.CreateProcess;
    windows.CloseHandle(out_write);

    const assign_ok = k32.AssignProcessToJobObject(job, pi.hProcess) != 0;
    const assign_err: u32 = if (assign_ok) 0 else @intFromEnum(windows.kernel32.GetLastError());
    _ = k32.ResumeThread(pi.hThread);

    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(alloc);

    // Let the tree announce CHILD:/GRAND: (both then sleep, holding the pipe).
    try drainUntilQuiet(alloc, &out, out_read, 400, 6000);
    const child_pid = parseTaggedPid(out.items, "CHILD:");
    const grand_pid = parseTaggedPid(out.items, "GRAND:");

    const grand_h = k32.OpenProcess(PROCESS_QUERY_SYNC, windows.FALSE, grand_pid);
    const alive_before = processActive(pi.hProcess) and
        (grand_h != null and processActive(grand_h.?));

    // KILL: dropping the last job handle terminates the whole tree.
    windows.CloseHandle(job);

    const dead_child = k32.WaitForSingleObject(pi.hProcess, 5000) == 0; // WAIT_OBJECT_0
    const dead_grand = grand_h != null and
        k32.WaitForSingleObject(grand_h.?, 5000) == 0;
    const dead_after = dead_child and dead_grand;

    // With every writer dead, the reader must now hit EOF (no wedge).
    const no_wedge = try drainToEof(alloc, &out, out_read, 5000);

    if (grand_h) |h| windows.CloseHandle(h);
    windows.CloseHandle(pi.hProcess);
    windows.CloseHandle(pi.hThread);
    windows.CloseHandle(out_read);

    return .{
        .assign_ok = assign_ok,
        .assign_err = assign_err,
        .child_pid = child_pid,
        .grand_pid = grand_pid,
        .alive_before = alive_before,
        .dead_after = dead_after,
        .no_wedge = no_wedge,
        .output = try out.toOwnedSlice(alloc),
    };
}

/// ConPTY contrast: spawn `child_exe` under a pseudoconsole and try to place
/// it in our own job. This is expected to FAIL (the child is already in
/// ConPTY's job object) — the reason wintty can't job-kill a ConPTY child and
/// uses a manual wait thread. Returns .{ ok, err }.
pub fn assignUnderConpty(alloc: Allocator, child_exe: []const u8) !struct { ok: bool, err: u32 } {
    const cmd_utf8 = try std.fmt.allocPrint(alloc, "\"{s}\"", .{child_exe});
    defer alloc.free(cmd_utf8);
    const cmd_w = try std.unicode.utf8ToUtf16LeAllocZ(alloc, cmd_utf8);
    defer alloc.free(cmd_w);

    var s = try Session.spawn(cmd_w, 80, 25);

    const job = k32.CreateJobObjectW(null, null) orelse return error.CreateJobObject;
    var eli = std.mem.zeroes(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
    eli.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    _ = k32.SetInformationJobObject(
        job,
        JobObjectExtendedLimitInformation,
        &eli,
        @sizeOf(JOBOBJECT_EXTENDED_LIMIT_INFORMATION),
    );

    const ok = k32.AssignProcessToJobObject(job, s.child) != 0;
    const err: u32 = if (ok) 0 else @intFromEnum(windows.kernel32.GetLastError());

    windows.CloseHandle(job);
    windows.TerminateProcess(s.child, 0) catch {};
    _ = k32.WaitForSingleObject(s.child, 2000);
    k32.ClosePseudoConsole(s.hpcon);
    windows.CloseHandle(s.out_write);
    s.deinit();

    return .{ .ok = ok, .err = err };
}

pub const RawPtyResult = struct {
    assign_ok: bool, // AssignProcessToJobObject succeeded
    got_ready: bool, // child announced READY
    got_resize: bool, // child reflowed from the in-band 2048 report
    got_signal: bool, // child received the console-group CTRL_BREAK
    composed: bool, // child saw BOTH resize and signal in one run
    alive_before: bool, // child + grandchild alive before teardown
    dead_after: bool, // both terminated by closing the job
    no_wedge: bool, // pipe reached EOF after kill (no leaked writer)
    output: []u8,
};

/// P1.2 integrated raw-pipe transport prototype. Composes all three proven
/// transport realities into ONE lifecycle over a real pipe pair, with NO
/// ConPTY: spawn the child (suspended) with its stdout/stdin on pipes and a
/// shared console (inherited, own process group) inside a KILL_ON_JOB_CLOSE
/// job; resume; read its READY; resize it in-band (DECSET 2048 on the stdin
/// pipe); signal it (GenerateConsoleCtrlEvent CTRL_BREAK to its group); then
/// tear the whole tree down by closing the job and confirm no leak / no
/// wedge. Proves the mechanisms compose, not just work in isolation. The
/// transport owns the console here (as a winpty-style agent/host would); in
/// production wintty that role is a small console-owning helper.
pub fn rawPtyLifecycle(
    alloc: Allocator,
    child_exe: []const u8,
    cols1: u16,
    rows1: u16,
) !RawPtyResult {
    // Pipe pair: child stdout -> us, us -> child stdin.
    var out_read: windows.HANDLE = undefined;
    var out_write: windows.HANDLE = undefined;
    var in_read: windows.HANDLE = undefined;
    var in_write: windows.HANDLE = undefined;
    var sa = windows.SECURITY_ATTRIBUTES{
        .nLength = @sizeOf(windows.SECURITY_ATTRIBUTES),
        .bInheritHandle = windows.TRUE,
        .lpSecurityDescriptor = null,
    };
    if (k32.CreatePipe(&out_read, &out_write, &sa, 0) == 0) return error.CreatePipe;
    if (k32.CreatePipe(&in_read, &in_write, &sa, 0) == 0) return error.CreatePipe;
    try windows.SetHandleInformation(out_read, windows.HANDLE_FLAG_INHERIT, 0);
    try windows.SetHandleInformation(in_write, windows.HANDLE_FLAG_INHERIT, 0);

    // Teardown job.
    const job = k32.CreateJobObjectW(null, null) orelse return error.CreateJobObject;
    var eli = std.mem.zeroes(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
    eli.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (k32.SetInformationJobObject(
        job,
        JobObjectExtendedLimitInformation,
        &eli,
        @sizeOf(JOBOBJECT_EXTENDED_LIMIT_INFORMATION),
    ) == 0) return error.SetJobInfo;

    const cmd_utf8 = try std.fmt.allocPrint(alloc, "\"{s}\"", .{child_exe});
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

    // CREATE_SUSPENDED so we assign to the job before the child forks;
    // CREATE_NEW_PROCESS_GROUP so we can target the child's group with a
    // console control event. No new console: the child inherits ours (the
    // agent/host console), while its stdio stays on the pipes.
    if (k32.CreateProcessW(
        null,
        cmd_w.ptr,
        null,
        null,
        windows.TRUE,
        CREATE_SUSPENDED | CREATE_NEW_PROCESS_GROUP,
        null,
        null,
        &si,
        &pi,
    ) == 0) return error.CreateProcess;
    windows.CloseHandle(out_write);
    windows.CloseHandle(in_read);

    const assign_ok = k32.AssignProcessToJobObject(job, pi.hProcess) != 0;
    _ = k32.ResumeThread(pi.hThread);

    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(alloc);

    // 1) READY.
    try drainUntilQuiet(alloc, &out, out_read, 400, 6000);
    const got_ready = std.mem.indexOf(u8, out.items, "READY") != null;
    const grand_pid = parseTaggedPid(out.items, "grand=");

    // 2) RESIZE in-band (DECSET 2048 report on the stdin pipe).
    var rep_buf: [64]u8 = undefined;
    const report = std.fmt.bufPrint(
        &rep_buf,
        "\x1b[48;{d};{d};{d};{d}t",
        .{ rows1, cols1, @as(u32, rows1) * 18, @as(u32, cols1) * 9 },
    ) catch unreachable;
    var w: windows.DWORD = 0;
    _ = windows.kernel32.WriteFile(in_write, report.ptr, @intCast(report.len), &w, null);
    try drainUntilQuiet(alloc, &out, out_read, 400, 5000);
    const got_resize = std.mem.indexOf(u8, out.items, "RESIZE:") != null;

    // 3) SIGNAL (console-group CTRL_BREAK to the child's group).
    _ = k32.SetConsoleCtrlHandler(null, windows.TRUE);
    _ = k32.GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pi.dwProcessId);
    try drainUntilQuiet(alloc, &out, out_read, 400, 5000);
    _ = k32.SetConsoleCtrlHandler(null, windows.FALSE);
    const got_signal = std.mem.indexOf(u8, out.items, "SIGNAL:") != null;
    const composed = std.mem.indexOf(u8, out.items, "COMPOSED") != null;

    // Liveness before teardown.
    const grand_h = k32.OpenProcess(PROCESS_QUERY_SYNC, windows.FALSE, grand_pid);
    const alive_before = processActive(pi.hProcess) and
        (grand_h != null and processActive(grand_h.?));

    // 4) TEARDOWN: closing the last job handle kills the whole tree.
    windows.CloseHandle(job);
    const dead_child = k32.WaitForSingleObject(pi.hProcess, 5000) == 0;
    const dead_grand = grand_h != null and
        k32.WaitForSingleObject(grand_h.?, 5000) == 0;
    const dead_after = dead_child and dead_grand;
    const no_wedge = try drainToEof(alloc, &out, out_read, 5000);

    if (grand_h) |h| windows.CloseHandle(h);
    windows.CloseHandle(pi.hProcess);
    windows.CloseHandle(pi.hThread);
    windows.CloseHandle(in_write);
    windows.CloseHandle(out_read);

    return .{
        .assign_ok = assign_ok,
        .got_ready = got_ready,
        .got_resize = got_resize,
        .got_signal = got_signal,
        .composed = composed,
        .alive_before = alive_before,
        .dead_after = dead_after,
        .no_wedge = no_wedge,
        .output = try out.toOwnedSlice(alloc),
    };
}
