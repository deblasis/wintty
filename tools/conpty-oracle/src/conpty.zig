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
};

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

    // Reproduce console LF->newline processing: insert a CR before any LF
    // not already preceded by one. (A doubled CR is idempotent, so this is
    // safe even for streams that already use CRLF.)
    defer alloc.free(raw);
    var xl: std.ArrayList(u8) = .empty;
    errdefer xl.deinit(alloc);
    for (raw) |b| {
        if (b == '\n' and (xl.items.len == 0 or xl.items[xl.items.len - 1] != '\r'))
            try xl.append(alloc, '\r');
        try xl.append(alloc, b);
    }
    return xl.toOwnedSlice(alloc);
}
