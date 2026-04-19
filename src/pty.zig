const std = @import("std");
const builtin = @import("builtin");
const windows = @import("os/main.zig").windows;
const posix = std.posix;
const assert = @import("quirks.zig").inlineAssert;

const log = std.log.scoped(.pty);

/// Redeclare this winsize struct so we can just use a Zig struct. This
/// layout should be correct on all tested platforms. The defaults on this
/// are some reasonable screen size but you should probably not use them.
pub const winsize = extern struct {
    ws_row: u16 = 100,
    ws_col: u16 = 80,
    ws_xpixel: u16 = 800,
    ws_ypixel: u16 = 600,
};

pub const Pty = switch (builtin.os.tag) {
    .windows => WindowsPty,
    .ios => NullPty,
    else => PosixPty,
};

/// The terminal modes of a pty. Not all of these modes are supported
/// on all platforms but all platforms share the same mode struct.
///
/// The default values of fields in this struct are set to the most
/// typical values for a pty. This makes it easier for cross-platform
/// code which doesn't support all of the modes to work correctly.
pub const TerminalMode = packed struct {
    /// ICANON on POSIX
    canonical: bool = true,

    /// ECHO on POSIX
    echo: bool = true,
};

/// Transport mode. Only meaningful on Windows - POSIX always uses
/// the kernel PTY. See `Options.mode`.
pub const Mode = enum { conpty, bypass };

/// Arguments to `Pty.open`.
pub const Options = struct {
    size: winsize,
    /// Windows-only. Ignored on POSIX.
    mode: Mode = .conpty,
};

pub const ProcessInfo = enum {
    /// The PID of the process that controls the PTY.
    foreground_pid,
    /// Gets the name of the slave PTY. Returned name points to an internal buffer
    /// so it should not be modified or freed.
    tty_name,

    pub fn Type(comptime info: ProcessInfo) type {
        return switch (info) {
            .foreground_pid => u64,
            .tty_name => [:0]const u8,
        };
    }
};

// A pty implementation that does nothing.
//
// TODO: This should be removed. This is only temporary until we have
// a termio that doesn't use a pty. This isn't used in any user-facing
// artifacts, this is just a stopgap to get compilation to work on iOS.
const NullPty = struct {
    pub const Error = OpenError || GetModeError || SetSizeError || ChildPreExecError;

    pub const Fd = posix.fd_t;

    master: Fd,
    slave: Fd,

    pub const OpenError = error{};

    pub fn open(opts: Options) OpenError!Pty {
        const size = opts.size;
        _ = size;
        _ = opts.mode;
        return .{ .master = 0, .slave = 0 };
    }

    pub fn deinit(self: *Pty) void {
        _ = self;
    }

    pub const GetModeError = error{GetModeFailed};

    pub fn getMode(self: Pty) GetModeError!TerminalMode {
        _ = self;
        return .{};
    }

    pub const SetSizeError = error{};

    pub fn setSize(self: *Pty, size: winsize) SetSizeError!void {
        _ = self;
        _ = size;
    }

    pub const ChildPreExecError = error{};

    pub fn childPreExec(self: Pty) ChildPreExecError!void {
        _ = self;
    }

    /// Get information about the process(es) attached to the PTY. Returns
    /// `null` if there was an error getting the information or the information
    /// is not available on a particular platform.
    pub fn getProcessInfo(_: *Pty, comptime info: ProcessInfo) ?ProcessInfo.Type(info) {
        return null;
    }
};

/// Posix PTY creation and management. This is just a thin layer on top
/// of Posix syscalls. The caller is responsible for detail-oriented handling
/// of the returned file handles.
const PosixPty = struct {
    pub const Error = OpenError || GetModeError || GetSizeError || SetSizeError || ChildPreExecError;

    pub const Fd = posix.fd_t;

    const c = @import("pty-c");

    /// The file descriptors for the master and slave side of the pty.
    /// The slave side is never closed automatically by this struct
    /// so the caller is responsible for closing it if things
    /// go wrong.
    master: Fd,
    slave: Fd,

    /// Buffer for storage of slave tty name so that we don't have to recompute
    /// it every time we need it.
    tty_name_buf: [std.fs.max_path_bytes:0]u8 = undefined,
    /// The name of slave tty. If `null` it has not yet been computed or
    /// may not be available. Should not be accessed directly, but through
    /// `self.getProcessInfo(.tty_name)`
    tty_name: ?[:0]const u8 = null,

    pub const OpenError = error{OpenptyFailed};

    /// Open a new PTY with the given initial size.
    pub fn open(opts: Options) OpenError!Pty {
        const size = opts.size;
        _ = opts.mode;
        // Need to copy so that it becomes non-const.
        var sizeCopy = size;

        var master_fd: Fd = undefined;
        var slave_fd: Fd = undefined;
        if (c.openpty(
            &master_fd,
            &slave_fd,
            null,
            null,
            @ptrCast(&sizeCopy),
        ) < 0)
            return error.OpenptyFailed;
        errdefer {
            _ = posix.system.close(master_fd);
            _ = posix.system.close(slave_fd);
        }

        // Set CLOEXEC on the master fd, only the slave fd should be inherited
        // by the child process (shell/command).
        cloexec: {
            const flags = posix.fcntl(master_fd, posix.F.GETFD, 0) catch |err| {
                log.warn("error getting flags for master fd err={}", .{err});
                break :cloexec;
            };

            _ = posix.fcntl(
                master_fd,
                posix.F.SETFD,
                flags | posix.FD_CLOEXEC,
            ) catch |err| {
                log.warn("error setting CLOEXEC on master fd err={}", .{err});
                break :cloexec;
            };
        }

        // Enable UTF-8 mode. I think this is on by default on Linux but it
        // is NOT on by default on macOS so we ensure that it is always set.
        var attrs: c.termios = undefined;
        if (c.tcgetattr(master_fd, &attrs) != 0)
            return error.OpenptyFailed;
        attrs.c_iflag |= c.IUTF8;
        if (c.tcsetattr(master_fd, c.TCSANOW, &attrs) != 0)
            return error.OpenptyFailed;

        return .{
            .master = master_fd,
            .slave = slave_fd,
            .tty_name_buf = undefined,
            .tty_name = null,
        };
    }

    pub fn deinit(self: *Pty) void {
        _ = posix.system.close(self.master);
        self.* = undefined;
    }

    pub const GetModeError = error{GetModeFailed};

    pub fn getMode(self: Pty) GetModeError!TerminalMode {
        var attrs: c.termios = undefined;
        if (c.tcgetattr(self.master, &attrs) != 0)
            return error.GetModeFailed;

        return .{
            .canonical = (attrs.c_lflag & c.ICANON) != 0,
            .echo = (attrs.c_lflag & c.ECHO) != 0,
        };
    }

    pub const GetSizeError = error{IoctlFailed};

    /// Return the size of the pty.
    pub fn getSize(self: Pty) GetSizeError!winsize {
        var ws: winsize = undefined;
        if (c.ioctl(self.master, c.TIOCGWINSZ, @intFromPtr(&ws)) < 0)
            return error.IoctlFailed;

        return ws;
    }

    pub const SetSizeError = error{IoctlFailed};

    /// Set the size of the pty.
    pub fn setSize(self: *Pty, size: winsize) SetSizeError!void {
        if (c.ioctl(self.master, c.TIOCSWINSZ, @intFromPtr(&size)) < 0)
            return error.IoctlFailed;
    }

    pub const ChildPreExecError = error{ OperationNotSupported, ProcessGroupFailed, SetControllingTerminalFailed };

    /// This should be called prior to exec in the forked child process
    /// in order to setup the tty properly.
    pub fn childPreExec(self: Pty) ChildPreExecError!void {
        // Reset our signals
        var sa: posix.Sigaction = .{
            .handler = .{ .handler = posix.SIG.DFL },
            .mask = posix.sigemptyset(),
            .flags = 0,
        };
        posix.sigaction(posix.SIG.ABRT, &sa, null);
        posix.sigaction(posix.SIG.ALRM, &sa, null);
        posix.sigaction(posix.SIG.BUS, &sa, null);
        posix.sigaction(posix.SIG.CHLD, &sa, null);
        posix.sigaction(posix.SIG.FPE, &sa, null);
        posix.sigaction(posix.SIG.HUP, &sa, null);
        posix.sigaction(posix.SIG.ILL, &sa, null);
        posix.sigaction(posix.SIG.INT, &sa, null);
        posix.sigaction(posix.SIG.PIPE, &sa, null);
        posix.sigaction(posix.SIG.SEGV, &sa, null);
        posix.sigaction(posix.SIG.TRAP, &sa, null);
        posix.sigaction(posix.SIG.TERM, &sa, null);
        posix.sigaction(posix.SIG.QUIT, &sa, null);

        // Create a new process group
        if (c.setsid() < 0) return error.ProcessGroupFailed;

        // Set controlling terminal
        switch (posix.errno(c.ioctl(self.slave, c.TIOCSCTTY, @as(c_ulong, 0)))) {
            .SUCCESS => {},
            else => |err| {
                log.err("error setting controlling terminal errno={}", .{err});
                return error.SetControllingTerminalFailed;
            },
        }

        // Can close master/slave pair now
        posix.close(self.slave);
        posix.close(self.master);
    }

    /// Get information about the process(es) attached to the PTY. Returns
    /// `null` if there was an error getting the information or the information
    /// is not available on a particular platform.
    pub fn getProcessInfo(self: *PosixPty, comptime info: ProcessInfo) ?ProcessInfo.Type(info) {
        return switch (info) {
            .foreground_pid => {
                switch (builtin.os.tag) {
                    .linux => {
                        const linux = std.os.linux;
                        var pgrp: i32 = undefined;
                        const rc = linux.tcgetpgrp(self.master, &pgrp);
                        switch (linux.E.init(rc)) {
                            .SUCCESS => return @intCast(pgrp),
                            else => return null,
                        }
                    },
                    else => {
                        const rc = c.tcgetpgrp(self.master);
                        if (rc < 0) return null;
                        return @intCast(rc);
                    },
                }
            },
            .tty_name => {
                if (self.tty_name) |tty_name| return tty_name;

                switch (builtin.os.tag) {
                    .macos => {
                        // The macOS TIOCPTYGNAME ioctl does not allow us to
                        // specify the length of the buffer passed to it, but
                        // expects it to be at least 128 bytes long.
                        assert(self.tty_name_buf.len >= 128);
                        switch (posix.errno(c.ioctl(self.master, c.TIOCPTYGNAME, @intFromPtr(&self.tty_name_buf)))) {
                            .SUCCESS => {
                                const tty_name: [:0]const u8 = std.mem.sliceTo(&self.tty_name_buf, 0);
                                self.tty_name = tty_name;
                                return tty_name;
                            },
                            else => |err| {
                                log.err("error getting name of slave PTY errno={t}", .{err});
                                return null;
                            },
                        }
                    },
                    .linux => {
                        if (c.ptsname_r(self.master, &self.tty_name_buf, self.tty_name_buf.len) != 0) return null;
                        const tty_name: [:0]const u8 = std.mem.sliceTo(&self.tty_name_buf, 0);
                        self.tty_name = tty_name;
                        return tty_name;
                    },
                    else => return null,
                }
            },
        };
    }
};

/// Windows PTY creation and management.
const WindowsPty = struct {
    pub const Error = OpenError || GetSizeError || SetSizeError;

    pub const Fd = windows.HANDLE;

    // Process-wide counter for pipe names
    var pipe_name_counter = std.atomic.Value(u32).init(1);

    out_pipe: windows.HANDLE,
    in_pipe: windows.HANDLE,
    out_pipe_pty: windows.HANDLE,
    in_pipe_pty: windows.HANDLE,
    /// null when mode == .bypass. In ConPTY mode this holds the HPCON
    /// that owns the pipe pair internally.
    pseudo_console: ?windows.exp.HPCON,
    size: winsize,
    mode: Mode,

    pub const OpenError = error{Unexpected};

    /// Open a new PTY with the given initial size.
    pub fn open(opts: Options) OpenError!Pty {
        const size = opts.size;
        var pty: Pty = undefined;

        var pipe_path_buf: [128]u8 = undefined;
        var pipe_path_buf_w: [128]u16 = undefined;
        const pipe_path = std.fmt.bufPrintZ(
            &pipe_path_buf,
            "\\\\.\\pipe\\LOCAL\\ghostty-pty-{d}-{d}",
            .{
                windows.GetCurrentProcessId(),
                pipe_name_counter.fetchAdd(1, .monotonic),
            },
        ) catch unreachable;

        const pipe_path_w_len = std.unicode.utf8ToUtf16Le(
            &pipe_path_buf_w,
            pipe_path,
        ) catch unreachable;
        pipe_path_buf_w[pipe_path_w_len] = 0;
        const pipe_path_w = pipe_path_buf_w[0..pipe_path_w_len :0];

        const security_attributes = windows.SECURITY_ATTRIBUTES{
            .nLength = @sizeOf(windows.SECURITY_ATTRIBUTES),
            .bInheritHandle = windows.FALSE,
            .lpSecurityDescriptor = null,
        };

        pty.in_pipe = windows.kernel32.CreateNamedPipeW(
            pipe_path_w.ptr,
            windows.PIPE_ACCESS_OUTBOUND |
                windows.exp.FILE_FLAG_FIRST_PIPE_INSTANCE |
                windows.FILE_FLAG_OVERLAPPED,
            windows.PIPE_TYPE_BYTE,
            1,
            4096,
            4096,
            0,
            &security_attributes,
        );
        if (pty.in_pipe == windows.INVALID_HANDLE_VALUE) {
            return windows.unexpectedError(windows.kernel32.GetLastError());
        }
        errdefer _ = windows.CloseHandle(pty.in_pipe);

        var security_attributes_read = security_attributes;
        pty.in_pipe_pty = windows.kernel32.CreateFileW(
            pipe_path_w.ptr,
            windows.GENERIC_READ,
            0,
            &security_attributes_read,
            windows.OPEN_EXISTING,
            windows.FILE_ATTRIBUTE_NORMAL,
            null,
        );
        if (pty.in_pipe_pty == windows.INVALID_HANDLE_VALUE) {
            return windows.unexpectedError(windows.kernel32.GetLastError());
        }
        errdefer _ = windows.CloseHandle(pty.in_pipe_pty);

        // The in_pipe needs to be created as a named pipe, since anonymous
        // pipes created with CreatePipe do not support overlapped operations,
        // and the IOCP backend of libxev only uses overlapped operations on files.
        //
        // It would be ideal to use CreatePipe here, so that our pipe isn't
        // visible to any other processes.

        // if (windows.exp.kernel32.CreatePipe(&pty.in_pipe_pty, &pty.in_pipe, null, 0) == 0) {
        //     return windows.unexpectedError(windows.kernel32.GetLastError());
        // }
        // errdefer {
        //     _ = windows.CloseHandle(pty.in_pipe_pty);
        //     _ = windows.CloseHandle(pty.in_pipe);
        // }

        if (windows.exp.kernel32.CreatePipe(&pty.out_pipe, &pty.out_pipe_pty, null, 0) == 0) {
            return windows.unexpectedError(windows.kernel32.GetLastError());
        }
        errdefer {
            _ = windows.CloseHandle(pty.out_pipe);
            _ = windows.CloseHandle(pty.out_pipe_pty);
        }

        try windows.SetHandleInformation(pty.in_pipe, windows.HANDLE_FLAG_INHERIT, 0);
        try windows.SetHandleInformation(pty.in_pipe_pty, windows.HANDLE_FLAG_INHERIT, 0);
        try windows.SetHandleInformation(pty.out_pipe, windows.HANDLE_FLAG_INHERIT, 0);
        try windows.SetHandleInformation(pty.out_pipe_pty, windows.HANDLE_FLAG_INHERIT, 0);

        pty.size = size;
        pty.mode = opts.mode;
        pty.pseudo_console = null;

        switch (opts.mode) {
            .conpty => {
                var hpcon: windows.exp.HPCON = undefined;
                const result = windows.exp.kernel32.CreatePseudoConsole(
                    .{ .X = @intCast(size.ws_col), .Y = @intCast(size.ws_row) },
                    pty.in_pipe_pty,
                    pty.out_pipe_pty,
                    0,
                    &hpcon,
                );
                if (result != windows.S_OK) return error.Unexpected;
                pty.pseudo_console = hpcon;
            },
            .bypass => {
                // Bypass: no pseudoconsole. The child inherits in_pipe_pty as
                // stdin and out_pipe_pty as stdout/stderr via
                // STARTF_USESTDHANDLES. To make that inheritance work, the
                // _pty ends must be HANDLE_FLAG_INHERIT. Parent ends stay
                // non-inheritable so they don't leak.
                try windows.SetHandleInformation(
                    pty.in_pipe_pty,
                    windows.HANDLE_FLAG_INHERIT,
                    windows.HANDLE_FLAG_INHERIT,
                );
                try windows.SetHandleInformation(
                    pty.out_pipe_pty,
                    windows.HANDLE_FLAG_INHERIT,
                    windows.HANDLE_FLAG_INHERIT,
                );
            },
        }

        return pty;
    }

    pub fn deinit(self: *Pty) void {
        _ = windows.CloseHandle(self.in_pipe_pty);
        _ = windows.CloseHandle(self.in_pipe);
        _ = windows.CloseHandle(self.out_pipe_pty);
        _ = windows.CloseHandle(self.out_pipe);
        if (self.pseudo_console) |hpcon| {
            _ = windows.exp.kernel32.ClosePseudoConsole(hpcon);
        }
        self.* = undefined;
    }

    /// Bypass-mode resize signal: write `CSI 8 ; rows ; cols t` (XTWINOPS)
    /// to the parent-side input pipe so that a VT-aware child parses it
    /// as a size change.
    ///
    /// Best-effort by design: if the child isn't reading we don't want
    /// to stall the UI thread, so the overlapped WriteFile is bounded
    /// by a 100 ms wait and any failure is logged, not returned. Full
    /// resize parity (SIGWINCH via WSL interop, per-shell signalling)
    /// is a deferred follow-up.
    fn writeResizeSequence(self: *Pty, size: winsize) void {
        var buf: [32]u8 = undefined;
        // 32 bytes fits any "\x1b[8;rows;cols t" for u16 dimensions,
        // so bufPrint cannot actually fail. Treat as unreachable.
        const seq = std.fmt.bufPrint(
            &buf,
            "\x1b[8;{d};{d}t",
            .{ size.ws_row, size.ws_col },
        ) catch unreachable;

        var overlapped = std.mem.zeroes(windows.OVERLAPPED);
        overlapped.hEvent = windows.exp.kernel32.CreateEventW(
            null,
            windows.TRUE,
            windows.FALSE,
            null,
        );
        if (overlapped.hEvent == null) {
            log.warn("bypass resize signal: CreateEventW failed", .{});
            return;
        }
        defer _ = windows.CloseHandle(overlapped.hEvent.?);

        var written: windows.DWORD = 0;
        const write_ok = windows.kernel32.WriteFile(
            self.in_pipe,
            seq.ptr,
            @intCast(seq.len),
            &written,
            &overlapped,
        );
        if (write_ok == 0) {
            const err = windows.kernel32.GetLastError();
            if (err == .IO_PENDING) {
                // Wait up to 100 ms. Best-effort: if the child isn't
                // reading we don't want to block the UI thread.
                const wait = windows.kernel32.WaitForSingleObject(
                    overlapped.hEvent.?,
                    100,
                );
                if (wait != windows.WAIT_OBJECT_0) {
                    _ = windows.kernel32.CancelIoEx(self.in_pipe, &overlapped);
                    log.warn("bypass resize signal timed out", .{});
                }
            } else {
                log.warn("bypass resize signal write failed: {}", .{err});
            }
        }
    }

    pub const GetSizeError = error{};

    /// Return the size of the pty.
    pub fn getSize(self: Pty) GetSizeError!winsize {
        return self.size;
    }

    pub const SetSizeError = error{ResizeFailed};

    /// Set the size of the pty.
    pub fn setSize(self: *Pty, size: winsize) SetSizeError!void {
        switch (self.mode) {
            .conpty => {
                const hpcon = self.pseudo_console orelse return error.ResizeFailed;
                const result = windows.exp.kernel32.ResizePseudoConsole(
                    hpcon,
                    .{ .X = @intCast(size.ws_col), .Y = @intCast(size.ws_row) },
                );
                if (result != windows.S_OK) return error.ResizeFailed;
            },
            // Best-effort: any transport failure is logged inside
            // writeResizeSequence; we still record `self.size` below.
            .bypass => self.writeResizeSequence(size),
        }
        self.size = size;
    }

    /// Get information about the process(es) attached to the PTY. Returns
    /// `null` if there was an error getting the information or the information
    /// is not available on a particular platform.
    pub fn getProcessInfo(_: *WindowsPty, comptime info: ProcessInfo) ?ProcessInfo.Type(info) {
        return null;
    }
};

test {
    const testing = std.testing;
    var ws: winsize = .{
        .ws_row = 50,
        .ws_col = 80,
        .ws_xpixel = 1,
        .ws_ypixel = 1,
    };

    var pty = try Pty.open(.{ .size = ws });
    defer pty.deinit();

    // Initialize size should match what we gave it
    try testing.expectEqual(ws, try pty.getSize());

    // Can set and read new sizes
    ws.ws_row *= 2;
    try pty.setSize(ws);
    try testing.expectEqual(ws, try pty.getSize());

    switch (builtin.os.tag) {
        .freebsd => try testing.expect(std.mem.startsWith(u8, pty.getProcessInfo(.tty_name).?, "/dev/")),
        .linux => try testing.expect(std.mem.startsWith(u8, pty.getProcessInfo(.tty_name).?, "/dev/pts/")),
        .macos => try testing.expect(std.mem.startsWith(u8, pty.getProcessInfo(.tty_name).?, "/dev/")),
        else => try testing.expect(pty.getProcessInfo(.tty_name) == null),
    }
}

test "WindowsPty: conpty mode populates pseudo_console" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    var pty = try Pty.open(.{
        .size = .{ .ws_row = 24, .ws_col = 80, .ws_xpixel = 0, .ws_ypixel = 0 },
        .mode = .conpty,
    });
    defer pty.deinit();
    try std.testing.expect(pty.pseudo_console != null);
    try std.testing.expectEqual(Mode.conpty, pty.mode);
}

test "WindowsPty: bypass mode skips pseudo_console and flips _pty handles inheritable" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    var pty = try Pty.open(.{
        .size = .{ .ws_row = 24, .ws_col = 80, .ws_xpixel = 0, .ws_ypixel = 0 },
        .mode = .bypass,
    });
    defer pty.deinit();
    try std.testing.expect(pty.pseudo_console == null);
    try std.testing.expectEqual(Mode.bypass, pty.mode);
    try std.testing.expect(pty.in_pipe != windows.INVALID_HANDLE_VALUE);
    try std.testing.expect(pty.out_pipe != windows.INVALID_HANDLE_VALUE);

    // _pty ends must be inheritable so CreateProcessW with
    // bInheritHandles = TRUE + HANDLE_LIST attribute passes them to
    // the child's stdio.
    var flags: windows.DWORD = 0;
    try windows.GetHandleInformation(pty.in_pipe_pty, &flags);
    try std.testing.expect((flags & windows.HANDLE_FLAG_INHERIT) != 0);
    try windows.GetHandleInformation(pty.out_pipe_pty, &flags);
    try std.testing.expect((flags & windows.HANDLE_FLAG_INHERIT) != 0);
}

test "WindowsPty: bypass setSize writes CSI 8;r;c;t to in_pipe" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    var pty = try Pty.open(.{
        .size = .{ .ws_row = 24, .ws_col = 80, .ws_xpixel = 0, .ws_ypixel = 0 },
        .mode = .bypass,
    });
    defer pty.deinit();

    try pty.setSize(.{ .ws_row = 40, .ws_col = 120, .ws_xpixel = 0, .ws_ypixel = 0 });

    // Read from the child-side pipe end. The resize escape was
    // written to in_pipe (parent) and must be readable from
    // in_pipe_pty (child).
    var buf: [32]u8 = undefined;
    var read: windows.DWORD = 0;
    const ok = windows.kernel32.ReadFile(
        pty.in_pipe_pty,
        &buf,
        @intCast(buf.len),
        &read,
        null,
    );
    try std.testing.expect(ok != 0);
    try std.testing.expectEqualStrings("\x1b[8;40;120t", buf[0..read]);

    // Size accounting still correct.
    try std.testing.expectEqual(@as(u16, 40), pty.size.ws_row);
    try std.testing.expectEqual(@as(u16, 120), pty.size.ws_col);
}

test "WindowsPty: bypass end-to-end with cmd.exe /c echo" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    const Command = @import("Command.zig");
    const testing = std.testing;

    var pty = try Pty.open(.{
        .size = .{ .ws_row = 24, .ws_col = 80, .ws_xpixel = 0, .ws_ypixel = 0 },
        .mode = .bypass,
    });
    defer pty.deinit();

    var cmd: Command = .{
        .path = "C:\\Windows\\System32\\cmd.exe",
        .args = &.{ "cmd.exe", "/c", "echo hi" },
        .stdin = .{ .handle = pty.in_pipe_pty },
        .stdout = .{ .handle = pty.out_pipe_pty },
        .stderr = .{ .handle = pty.out_pipe_pty },
        .pseudo_console = null,
        .os_pre_exec = null,
        .rt_pre_exec = null,
        .rt_post_fork = null,
        .rt_pre_exec_info = undefined,
        .rt_post_fork_info = undefined,
    };
    try cmd.testingStart();
    defer _ = cmd.wait(true) catch {};

    // Close our parent copies of _pty ends so EOF reaches us when
    // the child exits. Matches the termio/Exec.zig post-spawn
    // cleanup for bypass mode.
    _ = windows.CloseHandle(pty.in_pipe_pty);
    _ = windows.CloseHandle(pty.out_pipe_pty);
    pty.in_pipe_pty = windows.INVALID_HANDLE_VALUE;
    pty.out_pipe_pty = windows.INVALID_HANDLE_VALUE;

    // Drain stdout until EOF or buffer full.
    var buf: [256]u8 = undefined;
    var total: usize = 0;
    while (total < buf.len) {
        var read: windows.DWORD = 0;
        const ok = windows.kernel32.ReadFile(
            pty.out_pipe,
            buf[total..].ptr,
            @intCast(buf.len - total),
            &read,
            null,
        );
        if (ok == 0 or read == 0) break;
        total += read;
    }

    try testing.expect(std.mem.indexOf(u8, buf[0..total], "hi") != null);
}
