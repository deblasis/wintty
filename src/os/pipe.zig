const std = @import("std");
const builtin = @import("builtin");
const windows = @import("windows.zig");
const posix = std.posix;
const compat_fd = @import("../lib/compat/fd.zig");

/// pipe() that works on Windows and POSIX. For POSIX systems, this sets
/// CLOEXEC on the file descriptors.
pub fn pipe() ![2]posix.fd_t {
    switch (builtin.os.tag) {
        else => return compat_fd.pipe2(.{ .CLOEXEC = true }),
        .windows => {
            var read: windows.HANDLE = undefined;
            var write: windows.HANDLE = undefined;
            if (windows.exp.kernel32.CreatePipe(&read, &write, null, 0) == windows.FALSE) {
                return windows.unexpectedError(windows.GetLastError());
            }

            return .{ read, write };
        },
    }
}

/// Close one end of a pipe from `pipe()`. Mirrors the platform split
/// above: POSIX ends are fds and close with the syscall, Windows ends
/// are HANDLEs and need CloseHandle. `posix.system.close` does not
/// link at all against the MSVC CRT.
pub fn closeEnd(fd: posix.fd_t) void {
    switch (builtin.os.tag) {
        else => _ = posix.system.close(fd),
        .windows => _ = windows.exp.kernel32.CloseHandle(fd),
    }
}

/// What a `writeEnd` call did, collapsing the two platforms' very
/// different error reporting to the cases callers actually branch on.
pub const WriteResult = enum {
    ok,
    /// The reader closed its end. For the quit-signal pipes this is the
    /// intended outcome, not a failure.
    broken_pipe,
    failed,
};

/// Write to one end of a pipe from `pipe()`. See `closeEnd` for why
/// this can't just be `posix.system.write` everywhere.
pub fn writeEnd(fd: posix.fd_t, bytes: []const u8) WriteResult {
    switch (builtin.os.tag) {
        else => return switch (posix.errno(posix.system.write(
            fd,
            bytes.ptr,
            bytes.len,
        ))) {
            .SUCCESS => .ok,
            .PIPE => .broken_pipe,
            else => .failed,
        },

        .windows => {
            var written: windows.DWORD = 0;
            if (windows.exp.kernel32.WriteFile(
                fd,
                bytes.ptr,
                @intCast(bytes.len),
                &written,
                null,
            ) == windows.FALSE) {
                return switch (windows.GetLastError()) {
                    .BROKEN_PIPE, .NO_DATA => .broken_pipe,
                    else => .failed,
                };
            }
            return .ok;
        },
    }
}
