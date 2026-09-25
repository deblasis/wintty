//! Command launches sub-processes. This is an alternate implementation to the
//! Zig std.process.Child since at the time of authoring this, std.process.Child
//! didn't support the options necessary to spawn a shell attached to a pty.
//!
//! Consequently, I didn't implement a lot of features that std.process.Child
//! supports because we didn't need them. Cross-platform subprocessing is not
//! a trivial thing to implement (I've done it in three separate languages now)
//! so if we want to replatform onto std.process.Child I'd love to do that.
//! This was just the fastest way to get something built.
//!
//! Issues with std.process.Child:
//!
//!   * No pre_exec callback for logic after fork but before exec.
//!   * posix_spawn is used for Mac, but doesn't support the necessary
//!     features for tty setup.
//!
//!
//! TODO: This may have changed a lot now with the new I/O implementations in
//! >= 0.16.0, so this might warrant a recheck.
const Command = @This();

const std = @import("std");
const log = std.log.scoped(.command);
const builtin = @import("builtin");
const configpkg = @import("config.zig");
const global = @import("global.zig");
const internal_os = @import("os/main.zig");
const windows = internal_os.windows;
const TempDir = internal_os.TempDir;
const mem = std.mem;
const linux = std.os.linux;
const posix = std.posix;
const debug = std.debug;
const testing = std.testing;
const Allocator = std.mem.Allocator;
const File = std.Io.File;
const EnvMap = std.process.Environ.Map;
const apprt = @import("apprt.zig");

/// Function prototype for a function executed /in the child process/ after the
/// fork, but before exec'ing the command. If the function returns a u8, the
/// child process will be exited with that error code.
const PreExecFn = fn (*Command) ?u8;

/// Allowable set of errors that can be returned by a post fork function. Any
/// errors will result in the failure to create the surface.
pub const PostForkError = error{PostForkError};

/// Function prototype for a function executed /in the parent process/
/// after the fork.
const PostForkFn = fn (*Command) PostForkError!void;

/// Path to the command to run. This doesn't have to be an absolute path,
/// because use exec functions that search the PATH, if necessary.
///
/// This field is null-terminated to avoid a copy for the sake of
/// adding a null terminator since POSIX systems are so common.
path: [:0]const u8,

/// Command-line arguments. It is the responsibility of the caller to set
/// args[0] to the command. If args is empty then args[0] will automatically
/// be set to equal path.
args: []const [:0]const u8,

/// Environment variables for the child process. If this is null, inherits
/// the environment variables from this process. These are the exact
/// environment variables to set; these are /not/ merged.
env: ?*const EnvMap = null,

/// Working directory to change to in the child process. If not set, the
/// working directory of the calling process is preserved.
cwd: ?[:0]const u8 = null,

/// The file handle to set for stdin/out/err. If this isn't set, we do
/// nothing explicitly so it is up to the behavior of the operating system.
stdin: ?File = null,
stdout: ?File = null,
stderr: ?File = null,

/// If set, this will be executed /in the child process/ after fork but
/// before exec. This is useful to setup some state in the child before the
/// exec process takes over, such as signal handlers, setsid, setuid, etc.
os_pre_exec: ?*const PreExecFn,

/// If set, this will be executed /in the child process/ after fork but
/// before exec. This is useful to setup some state in the child before the
/// exec process takes over, such as signal handlers, setsid, setuid, etc.
rt_pre_exec: ?*const PreExecFn,

/// Configuration information needed by the apprt pre exec function. Note
/// that this should be a trivially copyable struct and not require any
/// allocation/deallocation.
rt_pre_exec_info: RtPreExecInfo,

/// If set, this will be executed in the /in the parent process/ after the fork.
rt_post_fork: ?*const PostForkFn,

/// Configuration information needed by the apprt post fork function. Note
/// that this should be a trivially copyable struct and not require any
/// allocation/deallocation.
rt_post_fork_info: RtPostForkInfo,

/// If set, then the process will be created attached to this pseudo console.
/// `stdin`, `stdout`, and `stderr` will be ignored if set.
pseudo_console: if (builtin.os.tag == .windows) ?windows.HPCON else void =
    if (builtin.os.tag == .windows) null else {},

/// User data that is sent to the callback. Set with setData and getData
/// for a more user-friendly API.
data: ?*anyopaque = null,

/// Process ID is set after start is called.
pid: ?posix.system.pid_t = null,

/// The various methods a process may exit.
pub const Exit = if (builtin.os.tag == .windows) union(enum) {
    Exited: u32,
} else union(enum) {
    /// Exited by normal exit call, value is exit status
    Exited: u8,

    /// Exited by a signal, value is the signal
    Signal: u32,

    /// Exited by a stop signal, value is signal
    Stopped: u32,

    /// Unknown exit reason, value is the status from waitpid
    Unknown: u32,

    pub fn init(status: u32) Exit {
        return if (posix.W.IFEXITED(status))
            Exit{ .Exited = posix.W.EXITSTATUS(status) }
        else if (posix.W.IFSIGNALED(status))
            Exit{ .Signal = @intFromEnum(posix.W.TERMSIG(status)) }
        else if (posix.W.IFSTOPPED(status))
            Exit{ .Stopped = @intFromEnum(posix.W.STOPSIG(status)) }
        else
            Exit{ .Unknown = status };
    }
};

/// Configuration information needed by the apprt pre exec function. Note
/// that this should be a trivially copyable struct and not require any
/// allocation/deallocation.
pub const RtPreExecInfo = if (@hasDecl(apprt.runtime, "pre_exec")) apprt.runtime.pre_exec.PreExecInfo else struct {
    pub inline fn init(_: *const configpkg.Config) @This() {
        return .{};
    }
};

/// Configuration information needed by the apprt post fork function. Note
/// that this should be a trivially copyable struct and not require any
/// allocation/deallocation.
pub const RtPostForkInfo = if (@hasDecl(apprt.runtime, "post_fork")) apprt.runtime.post_fork.PostForkInfo else struct {
    pub inline fn init(_: *const configpkg.Config) @This() {
        return .{};
    }
};

/// Start the subprocess. This returns immediately once the child is started.
///
/// After this is successful, self.pid is available.
pub fn start(self: *Command, alloc: Allocator) !void {
    // Use an arena allocator for the temporary allocations we need in this func.
    // IMPORTANT: do all allocation prior to the fork(). I believe it is undefined
    // behavior if you malloc between fork and exec. The source of the Zig
    // stdlib seems to verify this as well as Go.
    var arena_allocator = std.heap.ArenaAllocator.init(alloc);
    defer arena_allocator.deinit();
    const arena = arena_allocator.allocator();

    switch (builtin.os.tag) {
        .windows => try self.startWindows(arena),
        else => try self.startPosix(arena),
    }
}

fn startPosix(self: *Command, arena: Allocator) !void {
    // Null-terminate all our arguments
    const argsZ = try arena.allocSentinel(?[*:0]const u8, self.args.len, null);
    for (self.args, 0..) |arg, i| argsZ[i] = arg.ptr;

    // Determine our env vars
    const envp = if (self.env) |env_map|
        (try createNullDelimitedEnvMap(arena, env_map)).ptr
    else if (builtin.link_libc)
        std.c.environ
    else
        @compileError("missing env vars");

    // Fork.
    const pid = try fork();

    if (pid != 0) {
        // Parent, return immediately.
        self.pid = @intCast(pid);
        if (self.rt_post_fork) |f| try f(self);
        return;
    }

    // We are the child.

    // Setup our file descriptors for std streams.
    if (self.stdin) |f| setupFd(f.handle, posix.STDIN_FILENO) catch
        return error.ExecFailedInChild;
    if (self.stdout) |f| setupFd(f.handle, posix.STDOUT_FILENO) catch
        return error.ExecFailedInChild;
    if (self.stderr) |f| setupFd(f.handle, posix.STDERR_FILENO) catch
        return error.ExecFailedInChild;

    // Setup our working directory.
    //
    // NOTE: this can fail if we don't have permission to go to this directory
    // or if due to race conditions it doesn't exist or any various other
    // reasons. We don't want to crash the entire process if this fails so we
    // ignore it. We don't log because that'll show up in the output.
    if (self.cwd) |cwd| _ = posix.system.chdir(cwd);

    // Restore any rlimits that were set by Ghostty. This might fail but
    // any failures are ignored (its best effort).
    global.rlimits().restore();

    // If there are pre exec callbacks, call them now.
    if (self.os_pre_exec) |f| if (f(self)) |exitcode| posix.system.exit(exitcode);
    if (self.rt_pre_exec) |f| if (f(self)) |exitcode| posix.system.exit(exitcode);

    const err: posix.E = execve: {
        // This functionality has been taken from Zig stdlib, a simplified
        // version of the exec bits with PATH search so that we can just
        // offload to execve below.
        const file_slice = std.mem.sliceTo(self.path, 0);
        if (std.mem.findScalar(u8, file_slice, '/') != null) {
            break :execve posix.errno(posix.system.execve(self.path, argsZ, envp));
        }

        var path_expanded_buf: [std.fs.max_path_bytes]u8 = undefined;
        const PATH = global.environ().getPosix("PATH") orelse "/usr/local/bin:/bin/:/usr/bin";
        var it = std.mem.tokenizeScalar(u8, PATH, ':');
        var err: posix.system.E = .NOENT;
        var seen_eacces = false;

        while (it.next()) |search_path| {
            const path_len = search_path.len + file_slice.len + 1;
            if (path_expanded_buf.len < path_len + 1) break :execve .NAMETOOLONG;
            @memcpy(path_expanded_buf[0..search_path.len], search_path);
            path_expanded_buf[search_path.len] = '/';
            @memcpy(path_expanded_buf[search_path.len + 1 ..][0..file_slice.len], file_slice);
            path_expanded_buf[path_len] = 0;
            const full_path = path_expanded_buf[0..path_len :0].ptr;
            // Replace here, switch on error (any error means that replace
            // failed, but we might need to retry).
            err = posix.errno(posix.system.execve(full_path, argsZ, envp));
            switch (err) {
                .ACCES => seen_eacces = true,
                .NOENT, .NOTDIR => {},
                else => break :execve err,
            }
        }

        if (seen_eacces) break :execve .ACCES;
        break :execve err;
    };

    // If we are executing this code, the exec failed. We're in the
    // child process so there isn't much we can do. We try to output
    // something reasonable. Its important to note we MUST NOT return
    // any other error condition from here on out.
    var stderr_buf: [1024]u8 = undefined;
    var stderr_writer = std.Io.File.stderr().writer(global.io(), &stderr_buf);
    const stderr = &stderr_writer.interface;
    switch (err) {
        posix.system.E.NOENT => stderr.print(
            \\Requested executable not found. Please verify the command is on
            \\the PATH and try again.
            \\
        ,
            .{},
        ) catch {},

        else => |e| stderr.print(
            \\exec syscall failed with unexpected error: E{s}
            \\
        ,
            .{@tagName(e)},
        ) catch {},
    }
    stderr.flush() catch {};

    // We return a very specific error that can be detected to determine
    // we're in the child.
    return error.ExecFailedInChild;
}

/// Wrapper for the raw fork syscall. This preserves the error handling from
/// the std.posix wrapper that was removed in Zig 0.16.
fn fork() !posix.pid_t {
    const rc = posix.system.fork();
    switch (posix.errno(rc)) {
        .SUCCESS => return @intCast(rc),
        .AGAIN, .NOMEM => return error.SystemResources,
        else => |err| return posix.unexpectedErrno(err),
    }
}

/// Resolve a bare program name (no directory component) to an absolute
/// path so CreateProcessW gets an explicit lpApplicationName. Returned
/// as UTF-16 straight through; the caller hands it to CreateProcessW
/// without re-encoding.
///
/// With lpApplicationName null, CreateProcessW searches using THIS
/// process's PATH -- inherited verbatim from whoever launched us. A GUI
/// app started from a terminal whose session predates a PATH edit (or
/// that sanitizes the environment) fails to find shells that plainly
/// exist, surfacing as error.FileNotFound mapped up to an opaque
/// error.Unexpected on the "error starting IO thread" screen (live
/// incident 2026-08-23: `command = pwsh.exe` from a Warp session whose
/// PATH lacked PowerShell 7).
///
/// Search order: SearchPathW first (the exact search CreateProcessW
/// would have done, honoring the caller's PATH ordering), then a few
/// well-known shell homes PATH misses when stale. Returns null when
/// nothing resolves; the caller then falls back to the legacy
/// CreateProcessW search unchanged.
fn resolveWindowsProgram(arena: Allocator, path: [:0]const u8) !?[:0]u16 {
    if (std.fs.path.dirname(path) != null) return null;

    // Probe name: the program as written plus .exe when it carries no
    // extension. SearchPathW would append the extension itself, but
    // GetFileAttributesW below appends nothing, so a bare `pwsh` only
    // resolves with the suffix baked in.
    const probe_u8 = if (std.fs.path.extension(path).len != 0)
        path
    else
        try std.fmt.allocPrintSentinel(arena, "{s}.exe", .{path}, 0);
    const probe_w = try std.unicode.utf8ToUtf16LeAllocZ(arena, probe_u8);
    var buf: [32768]u16 = undefined;

    // 1) The PATH-aware search CreateProcessW itself would run.
    const len = windows.exp.kernel32.SearchPathW(null, probe_w.ptr, null, buf.len, @ptrCast(&buf), null);
    if (len > 0 and len < buf.len) {
        return try arena.dupeZ(u16, buf[0..len]);
    }

    // 2) Well-known homes. Cheap existence probes; covers the default
    // shells when the caller's PATH is stale.
    const sys32 = std.unicode.utf8ToUtf16LeStringLiteral("\\System32\\");
    const pwsh7 = std.unicode.utf8ToUtf16LeStringLiteral("\\PowerShell\\7\\");
    const ps7_user = std.unicode.utf8ToUtf16LeStringLiteral("\\Programs\\PowerShell\\7\\");
    const winps = std.unicode.utf8ToUtf16LeStringLiteral("\\System32\\WindowsPowerShell\\v1.0\\");
    // A missing env var kills only its own probe, never the rest.
    const dirs = [_][]const u16{
        winJoinEnv(arena, "SystemRoot", sys32) catch &.{},
        winJoinEnv(arena, "ProgramFiles", pwsh7) catch &.{},
        winJoinEnv(arena, "LOCALAPPDATA", ps7_user) catch &.{},
        winJoinEnv(arena, "SystemRoot", winps) catch &.{},
    };
    for (dirs) |dir| {
        var candidate: [32768 + 16]u16 = undefined;
        if (dir.len + probe_w.len > candidate.len) continue;
        @memcpy(candidate[0..dir.len], dir);
        @memcpy(candidate[dir.len..][0..probe_w.len], probe_w[0..probe_w.len :0]);
        candidate[dir.len + probe_w.len] = 0;
        const full = candidate[0 .. dir.len + probe_w.len :0];
        const attrs = windows.exp.kernel32.GetFileAttributesW(full.ptr);
        if (attrs != windows.INVALID_FILE_ATTRIBUTES) {
            return try arena.dupeZ(u16, full);
        }
    }

    return null;
}

/// dir-from-env helper: `<env var value><literal suffix>` as UTF-16, or
/// null when the variable is unset. Errors are swallowed by the caller
/// (the resolver treats a missing env as a dead probe, never a spawn
/// failure).
fn winJoinEnv(arena: Allocator, name: []const u8, suffix: []const u16) ![]u16 {
    const name_w = try std.unicode.utf8ToUtf16LeAllocZ(arena, name);
    var vbuf: [32768]u16 = undefined;
    const vlen = windows.exp.kernel32.GetEnvironmentVariableW(name_w.ptr, @ptrCast(&vbuf), vbuf.len);
    if (vlen == 0 or vlen >= vbuf.len) return error.EnvironmentVariableMissing;
    const out = try arena.alloc(u16, vlen + suffix.len);
    @memcpy(out[0..vlen], vbuf[0..vlen]);
    @memcpy(out[vlen..], suffix);
    return out;
}

/// How a Windows spawn hands standard I/O to the child.
const WindowsStdioMode = struct {
    /// Whether `STARTF_USESTDHANDLES` belongs in `STARTUPINFO.dwFlags`.
    use_std_handles: bool,

    /// The `bInheritHandles` argument to `CreateProcessW`.
    inherit_handles: bool,
};

/// A pseudoconsole child takes its standard handles from the console it
/// is attached to. `STARTF_USESTDHANDLES` would override those with the
/// three NULL handles that path has, and nothing in it is passed by
/// inheritance either, so inheriting is only a way to leak whatever
/// inheritable handles this process happens to hold. The explicit-handle
/// path needs both: the child sees the three handles we name, and only
/// because it inherits them (bounded by the handle list attribute).
fn windowsStdioMode(has_pseudo_console: bool) WindowsStdioMode {
    return if (has_pseudo_console) .{
        .use_std_handles = false,
        .inherit_handles = false,
    } else .{
        .use_std_handles = true,
        .inherit_handles = true,
    };
}

fn startWindows(self: *Command, arena: Allocator) !void {
    const cwd_w = if (self.cwd) |cwd| try std.unicode.utf8ToUtf16LeAllocZ(arena, cwd) else null;

    // Pass null for lpApplicationName and put the program as the first
    // token of lpCommandLine. This lets CreateProcessW perform the
    // standard program search (parent-app dir, CWD, system dirs, PATH)
    // and append ".exe" when the name has no extension, which is what
    // users expect for bare commands like `wsl ~` or `pwsh.exe`.
    // It also preserves the child's argv[0] as written by the caller
    // rather than replacing it with the resolved absolute path.
    const command_line = if (self.args.len > 0)
        try windowsCreateCommandLine(arena, self.args)
    else
        try windowsCreateCommandLine(arena, &.{self.path});
    const command_line_w = try std.unicode.utf8ToUtf16LeAllocZ(arena, command_line);

    // Bare program names get an explicit lpApplicationName so the spawn
    // does not depend on this process's (possibly stale) PATH. argv[0]
    // in the command line stays as the caller wrote it; only the module
    // to load comes from the resolved absolute path.
    const app_name_w: ?[:0]u16 = try resolveWindowsProgram(arena, self.path);
    const env_w = if (self.env) |env_map| try createWindowsEnvBlock(arena, env_map) else null;

    const any_null_fd = self.stdin == null or self.stdout == null or self.stderr == null;
    const null_fd = if (any_null_fd) null_fd: {
        // path = "\Device\Null"
        const path = [_]u16{ '\\', 'D', 'e', 'v', 'i', 'c', 'e', '\\', 'N', 'u', 'l', 'l' };
        var path_unicode_string: windows.UNICODE_STRING = .init(&path);
        var attrs: windows.OBJECT_ATTRIBUTES = .{ .ObjectName = &path_unicode_string };

        var fd: windows.HANDLE = undefined;
        var io_status: windows.IO_STATUS_BLOCK = undefined; // unused
        const result = windows.exp.ntdll.NtCreateFile(
            &fd,
            .{ .GENERIC = .{ .READ = true }, .STANDARD = .{ .SYNCHRONIZE = true } },
            &attrs,
            &io_status,
            null,
            windows.FILE_ATTRIBUTE_NORMAL,
            windows.FILE_SHARE_READ,
            windows.OPEN_EXISTING,
            windows.FILE_NON_DIRECTORY_FILE,
            null,
            0,
        );

        if (result != .SUCCESS) {
            return windows.unexpectedStatus(result);
        }

        break :null_fd fd;
    } else null;
    defer {
        if (null_fd) |fd| _ = windows.exp.kernel32.CloseHandle(fd);
    }

    // TODO: In the case of having FDs instead of pty, need to set up
    // attributes such that the child process only inherits these handles,
    // then set bInheritsHandles below.

    const attribute_list, const stdin, const stdout, const stderr = if (self.pseudo_console) |pseudo_console| b: {
        var attribute_list_size: usize = undefined;
        _ = windows.exp.kernel32.InitializeProcThreadAttributeList(
            null,
            1,
            0,
            &attribute_list_size,
        );

        const attribute_list_buf = try arena.alloc(u8, attribute_list_size);
        if (windows.exp.kernel32.InitializeProcThreadAttributeList(
            attribute_list_buf.ptr,
            1,
            0,
            &attribute_list_size,
        ) == windows.FALSE) return windows.unexpectedError(windows.GetLastError());

        if (windows.exp.kernel32.UpdateProcThreadAttribute(
            attribute_list_buf.ptr,
            0,
            windows.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            pseudo_console,
            @sizeOf(windows.HPCON),
            null,
            null,
        ) == windows.FALSE) return windows.unexpectedError(windows.GetLastError());

        break :b .{ attribute_list_buf.ptr, null, null, null };
    } else b: {
        const stdin = if (self.stdin) |f| f.handle else null_fd.?;
        const stdout = if (self.stdout) |f| f.handle else null_fd.?;
        const stderr = if (self.stderr) |f| f.handle else null_fd.?;

        // All three handles must be HANDLE_FLAG_INHERIT for
        // PROC_THREAD_ATTRIBUTE_HANDLE_LIST. The PTY path flips _pty
        // ends inheritable in pty.zig; null_fd and caller-provided
        // files may or may not be. Flip them unconditionally here -
        // it's idempotent, and handles our code owns are closed after
        // spawn anyway.
        try windows.SetHandleInformation(stdin, windows.HANDLE_FLAG_INHERIT, windows.HANDLE_FLAG_INHERIT);
        try windows.SetHandleInformation(stdout, windows.HANDLE_FLAG_INHERIT, windows.HANDLE_FLAG_INHERIT);
        try windows.SetHandleInformation(stderr, windows.HANDLE_FLAG_INHERIT, windows.HANDLE_FLAG_INHERIT);

        // Non-pseudoconsole spawn (no HPCON given, e.g. helper/test
        // processes with explicit std handles): restrict inheritance to
        // just these three handles via PROC_THREAD_ATTRIBUTE_HANDLE_LIST.
        // Without this, bInheritHandles = TRUE leaks every inheritable
        // parent handle to the child - a real security bug.
        var attribute_list_size: usize = undefined;
        _ = windows.exp.kernel32.InitializeProcThreadAttributeList(
            null,
            1,
            0,
            &attribute_list_size,
        );

        const attribute_list_buf = try arena.alloc(u8, attribute_list_size);
        if (windows.exp.kernel32.InitializeProcThreadAttributeList(
            attribute_list_buf.ptr,
            1,
            0,
            &attribute_list_size,
        ) == .FALSE) return windows.unexpectedError(windows.GetLastError());

        // Allocate the handle list from the arena so its lifetime
        // outlives the attribute list until after CreateProcessW.
        // PROC_THREAD_ATTRIBUTE_HANDLE_LIST rejects duplicate handles,
        // which is easy to hit: a caller (or a test) can point multiple
        // std handles at the same file (e.g. null_fd for both stdin and
        // stderr). Build a list of unique handles only.
        var unique: [3]windows.HANDLE = .{ stdin, stdout, stderr };
        var unique_len: usize = 1;
        for (unique[1..]) |h| {
            var seen = false;
            for (unique[0..unique_len]) |u| {
                if (u == h) {
                    seen = true;
                    break;
                }
            }
            if (!seen) {
                unique[unique_len] = h;
                unique_len += 1;
            }
        }
        const handles = try arena.alloc(windows.HANDLE, unique_len);
        @memcpy(handles, unique[0..unique_len]);

        // lpValue for PROC_THREAD_ATTRIBUTE_HANDLE_LIST is the handles
        // array pointer passed BY VALUE (not by ref), matching how
        // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE above passes `pseudo_console`.
        if (windows.exp.kernel32.UpdateProcThreadAttribute(
            attribute_list_buf.ptr,
            0,
            windows.PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
            @ptrCast(handles.ptr),
            @sizeOf(windows.HANDLE) * handles.len,
            null,
            null,
        ) == .FALSE) return windows.unexpectedError(windows.GetLastError());

        break :b .{ attribute_list_buf.ptr, stdin, stdout, stderr };
    };

    const stdio_mode = windowsStdioMode(self.pseudo_console != null);

    var startup_info_ex = windows.STARTUPINFOEX{
        .StartupInfo = .{
            .cb = @sizeOf(windows.STARTUPINFOEX),
            .hStdError = stderr,
            .hStdOutput = stdout,
            .hStdInput = stdin,
            .dwFlags = if (stdio_mode.use_std_handles)
                windows.STARTF_USESTDHANDLES
            else
                0,
            .lpReserved = null,
            .lpDesktop = null,
            .lpTitle = null,
            .dwX = 0,
            .dwY = 0,
            .dwXSize = 0,
            .dwYSize = 0,
            .dwXCountChars = 0,
            .dwYCountChars = 0,
            .dwFillAttribute = 0,
            .wShowWindow = 0,
            .cbReserved2 = 0,
            .lpReserved2 = null,
        },
        .lpAttributeList = attribute_list,
    };

    var flags: windows.DWORD = windows.CREATE_UNICODE_ENVIRONMENT;
    flags |= windows.EXTENDED_STARTUPINFO_PRESENT;

    // Suppress the console window for non-pseudoconsole spawns (helper /
    // test processes spawned with explicit std handles). ConPTY attaches
    // a pseudo-console which already suppresses the window; handle-list
    // spawns need the flag explicitly.
    if (self.pseudo_console == null) {
        flags |= windows.CREATE_NO_WINDOW;
    }

    var process_information: windows.PROCESS_INFORMATION = undefined;
    if (windows.exp.kernel32.CreateProcessW(
        if (app_name_w) |w| w.ptr else null,
        command_line_w.ptr,
        null,
        null,
        if (stdio_mode.inherit_handles) windows.TRUE else windows.FALSE,
        flags,
        if (env_w) |w| w.ptr else null,
        if (cwd_w) |w| w.ptr else null,
        @ptrCast(&startup_info_ex.StartupInfo),
        &process_information,
    ) == windows.FALSE) {
        const failed_rc = windows.GetLastError();
        log.warn("CreateProcessW failed rc=0x{x} app={s} cmd={s}", .{
            failed_rc,
            if (app_name_w) |w| std.unicode.utf16LeToUtf8Alloc(arena, w) catch "<utf16>" else "<path-search>",
            command_line,
        });
        return windows.unexpectedError(failed_rc);
    }

    self.pid = process_information.hProcess;
}

fn setupFd(src: File.Handle, target: i32) !void {
    const PosixCall = struct {
        fn f(func: anytype, args: anytype) !usize {
            while (true) {
                const rc = @call(.auto, func, args);
                switch (posix.errno(rc)) {
                    .SUCCESS => return @intCast(rc),
                    .INTR => continue,
                    .AGAIN, .ACCES => return error.Locked,
                    .BADF => unreachable,
                    .BUSY => return error.FileBusy,
                    .INVAL => unreachable, // invalid parameters
                    .PERM => return error.PermissionDenied,
                    .MFILE => return error.ProcessFdQuotaExceeded,
                    .NOTDIR => unreachable, // invalid parameter
                    .DEADLK => return error.DeadLock,
                    .NOLCK => return error.LockedRegionLimitExceeded,
                    else => |err| return posix.unexpectedErrno(err),
                }
            }
        }
    };

    switch (builtin.os.tag) {
        .linux => {
            // We use dup3 so that we can clear CLO_ON_EXEC. We do NOT want this
            // file descriptor to be closed on exec since we're exactly exec-ing after
            // this.
            _ = try PosixCall.f(linux.dup3, .{ src, target, 0 });
        },
        .freebsd, .ios, .macos => {
            // Mac doesn't support dup3 so we use dup2. We purposely clear
            // CLO_ON_EXEC for this fd.
            const flags = try PosixCall.f(posix.system.fcntl, .{ src, posix.F.GETFD });
            if (flags & posix.FD_CLOEXEC != 0) {
                _ = try PosixCall.f(
                    posix.system.fcntl,
                    .{ src, posix.F.SETFD, flags & ~@as(u32, posix.FD_CLOEXEC) },
                );
            }

            _ = try PosixCall.f(posix.system.dup2, .{ src, target });
        },
        else => @compileError("unsupported platform"),
    }
}

/// Wait for the command to exit and return information about how it exited.
pub fn wait(self: Command, block: bool) !Exit {
    if (comptime builtin.os.tag == .windows) {
        // Block until the process exits. This returns immediately if the
        // process already exited.
        //
        // NOTE: We can use the pid directly as posix.system.pid_t is still an
        // alias for a handle under Windows. We might want to keep an eye on if
        // this changes, though.
        const result = windows.exp.kernel32.WaitForSingleObject(self.pid.?, windows.INFINITE);
        if (result == windows.WAIT_FAILED) {
            return windows.unexpectedError(windows.GetLastError());
        }

        var exit_code: windows.DWORD = undefined;
        const has_code = windows.exp.kernel32.GetExitCodeProcess(self.pid.?, &exit_code) != windows.FALSE;
        if (!has_code) {
            return windows.unexpectedError(windows.GetLastError());
        }

        return .{ .Exited = exit_code };
    }

    const status: u32 = if (block) wait_block: {
        var status: if (builtin.link_libc) c_int else u32 = undefined;
        _ = try waitPid(self.pid.?, &status, 0);
        break :wait_block @bitCast(status);
    } else wait_nohang: {
        // We specify NOHANG because its not our fault if the process we launch
        // for the tty doesn't properly waitpid its children. We don't want
        // to hang the terminal over it.
        // When NOHANG is specified, waitpid will return a pid of 0 if the process
        // doesn't have a status to report. When that happens, it is as though the
        // wait call has not been performed, so we need to keep trying until we get
        // a non-zero pid back, otherwise we end up with zombie processes.
        while (true) {
            var status: if (builtin.link_libc) c_int else u32 = undefined;
            const pid = try waitPid(self.pid.?, &status, posix.system.W.NOHANG);
            if (pid != 0) break :wait_nohang @bitCast(status);
        }
    };

    return .init(status);
}

/// Wrapper for the raw waitpid syscall. Status is only initialized on success;
/// interrupted waits are retried and all other errors are propagated.
fn waitPid(
    pid: posix.pid_t,
    status: *if (builtin.link_libc) c_int else u32,
    flags: u32,
) !posix.pid_t {
    while (true) {
        const rc = posix.system.waitpid(pid, status, @intCast(flags));
        switch (posix.errno(rc)) {
            .SUCCESS => return @intCast(rc),
            .INTR => continue,
            .CHILD => return error.NoChildProcess,
            .INVAL => return error.InvalidWaitOptions,
            else => |err| return posix.unexpectedErrno(err),
        }
    }
}

/// Sets command->data to data.
pub fn setData(self: *Command, pointer: ?*anyopaque) void {
    self.data = pointer;
}

/// Returns command->data.
pub fn getData(self: Command, comptime DT: type) ?*DT {
    return if (self.data) |ptr| @ptrCast(@alignCast(ptr)) else null;
}

// Copied from Zig. This is a publicly exported function but there is no
// way to get it from the std package.
fn createNullDelimitedEnvMap(arena: mem.Allocator, env_map: *const EnvMap) ![:null]?[*:0]u8 {
    const envp_count = env_map.count();
    const envp_buf = try arena.allocSentinel(?[*:0]u8, envp_count, null);

    var it = env_map.iterator();
    var i: usize = 0;
    while (it.next()) |pair| : (i += 1) {
        const env_buf = try arena.allocSentinel(u8, pair.key_ptr.len + pair.value_ptr.len + 1, 0);
        @memcpy(env_buf[0..pair.key_ptr.len], pair.key_ptr.*);
        env_buf[pair.key_ptr.len] = '=';
        @memcpy(env_buf[pair.key_ptr.len + 1 ..], pair.value_ptr.*);
        envp_buf[i] = env_buf.ptr;
    }
    std.debug.assert(i == envp_count);

    return envp_buf;
}

// Copied from Zig. This is a publicly exported function but there is no
// way to get it from the std package.
fn createWindowsEnvBlock(allocator: mem.Allocator, env_map: *const EnvMap) ![]u16 {
    // count bytes needed
    const max_chars_needed = x: {
        var max_chars_needed: usize = 4; // 4 for the final 4 null bytes
        var it = env_map.iterator();
        while (it.next()) |pair| {
            // +1 for '='
            // +1 for null byte
            max_chars_needed += pair.key_ptr.len + pair.value_ptr.len + 2;
        }
        break :x max_chars_needed;
    };
    const result = try allocator.alloc(u16, max_chars_needed);
    errdefer allocator.free(result);

    var it = env_map.iterator();
    var i: usize = 0;
    while (it.next()) |pair| {
        i += try std.unicode.utf8ToUtf16Le(result[i..], pair.key_ptr.*);
        result[i] = '=';
        i += 1;
        i += try std.unicode.utf8ToUtf16Le(result[i..], pair.value_ptr.*);
        result[i] = 0;
        i += 1;
    }
    result[i] = 0;
    i += 1;
    result[i] = 0;
    i += 1;
    result[i] = 0;
    i += 1;
    result[i] = 0;
    i += 1;
    return try allocator.realloc(result, i);
}

/// Index of the `/C` or `/K` switch when `argv` is a cmd.exe invocation
/// with a single script after it, which is the shape a wrapped shell
/// command has. Returns null for anything else.
///
/// Two deliberate narrownesses, both of which fail closed to the CRT
/// quoting that was here before:
///
///   - The program is recognized by basename, not by the module
///     `resolveWindowsProgram` actually loads. A user program named
///     `cmd.exe` would get its last argument written verbatim, which
///     `CommandLineToArgvW` would then mis-parse.
///   - A `/C` with more than one argument after it does not match, so
///     `["cmd.exe", "/c", "prog", "arg one"]` is CRT-quoted and cmd
///     mis-parses it the way this function exists to avoid. Nothing in
///     the tree produces that shape: the wrap always joins the tail
///     into one script.
fn windowsCmdScriptSwitch(argv: []const []const u8) ?usize {
    if (argv.len < 3) return null;

    const exe = std.fs.path.basenameWindows(argv[0]);
    if (!std.ascii.eqlIgnoreCase(exe, "cmd.exe") and
        !std.ascii.eqlIgnoreCase(exe, "cmd")) return null;

    // The script is the last argument, so the switch is the one before.
    const i = argv.len - 2;
    const sw = argv[i];
    if (sw.len != 2 or sw[0] != '/') return null;
    switch (std.ascii.toLower(sw[1])) {
        'c', 'k' => {},
        else => return null,
    }

    // Everything between the program and the switch must be another
    // plain cmd.exe switch for this to be the shape we recognize.
    for (argv[1..i]) |arg| {
        if (arg.len != 2 or arg[0] != '/') return null;
    }

    return i;
}

/// Windows batch files are run by cmd.exe: CreateProcessW hands a
/// .bat/.cmd target to `%COMSPEC% /c`, and the command line this module
/// composed is parsed a second time, by a tokenizer with different
/// rules. It splits commands on unquoted `& | < > ( )`, and `^` escapes
/// the byte that follows; it splits parameters on ` \t,;=`; and it
/// expands `%VAR%` (and `!VAR!` under delayed expansion) even inside
/// quotes. An argument body like `r.txt&echo.X` written bare therefore
/// runs a second command (issue #1172). A batch invocation is serialized
/// by the rules below instead of the C runtime's.
fn windowsBatchTarget(argv0: []const u8) bool {
    const trimmed = mem.trim(u8, argv0, "\"' \t\r\n");
    const base = std.fs.path.basenameWindows(trimmed);

    // Detection errs toward detecting a batch; the other direction is the
    // original injection. The odd spellings behave differently from each
    // other and each class is treated for what it does:
    //
    //   - Trailing dots and spaces: the loader's batch special-case keys
    //     on the file it resolves, so a `x.cmd.` spelling still takes the
    //     cmd.exe path, but cmd itself cannot resolve the spelled name
    //     and the batch does not run; what made the spelling dangerous
    //     was the pre-fix bare compose, in which the hostile tail
    //     following it ran as a second command (probe round 3d, case
    //     B1, measured live).
    //   - An alternate-data-stream suffix: the loader actually rejects
    //     the spelling (CreateProcessW fails, probe round 3a R9), so
    //     nothing runs at all. Cutting the suffix anyway is deliberate
    //     over-approximation: if a Windows release ever runs one, the
    //     arguments are already serialized for cmd.
    //
    // The ADS cut runs FIRST so a dot before the colon (`x.cmd.:stream`)
    // is not mistaken for a trailing dot. The end-strip is end-only.
    const colon_from: usize = if (base.len > 1 and base[1] == ':') 2 else 0;
    const no_ads = if (mem.indexOfScalarPos(u8, base, colon_from, ':')) |i|
        base[0..i]
    else
        base;
    var end = no_ads.len;
    while (end > 0 and (no_ads[end - 1] == '.' or no_ads[end - 1] == ' ')) end -= 1;

    const stem = no_ads[0..end];
    const ext = std.fs.path.extension(stem);
    if (std.ascii.eqlIgnoreCase(ext, ".bat") or
        std.ascii.eqlIgnoreCase(ext, ".cmd"))
    {
        return true;
    }
    // Dotfile spellings: the extension reader sees no extension in `.cmd`
    // (a leading dot reads as a dotfile marker). If the loader's own
    // extension check disagrees, the miss is the original injection, so
    // over-detect by testing the stem itself: a target named exactly
    // `.cmd` or `.bat` gets its arguments serialized either way.
    return std.ascii.eqlIgnoreCase(stem, ".bat") or
        std.ascii.eqlIgnoreCase(stem, ".cmd");
}

/// The bytes cmd treats specially in an invocation tail: parameter
/// delimiters, metacharacters, and the quote. An element carrying any
/// of them needs quoting; anything else stays verbatim.
const windows_batch_quote_bytes = " \t\r\n,;=&<>|()^\"";

/// Serialize one element of a batch-file invocation for cmd.exe. Every
/// element of the invocation goes through this; the target path is
/// checked first and refused when it would need quoting (see
/// `windowsBatchPathNeedsQuoting`), so a path that reaches here is clean
/// and comes out byte-identical. Quoting keeps everything cmd splits,
/// redirects or chains on inside the pair, and an embedded quote doubles
/// (`""`), which cmd reads as one literal quote and which leaves the
/// quote state balanced, so no metacharacter ever sits outside a pair.
/// Backslashes and carets are literal inside quotes, so neither needs
/// escaping.
fn windowsBatchQuoteArg(writer: *std.Io.Writer, arg: []const u8) !void {
    if (arg.len == 0) {
        try writer.writeAll("\"\"");
        return;
    }
    if (mem.indexOfAny(u8, arg, windows_batch_quote_bytes) == null) {
        try writer.writeAll(arg);
        return;
    }
    try writer.writeByte('"');
    for (arg) |byte| {
        if (byte == '"') {
            try writer.writeAll("\"\"");
        } else {
            try writer.writeByte(byte);
        }
    }
    try writer.writeByte('"');
}

/// Whether the element carries a byte with no safe serialization for
/// cmd.exe: `%VAR%` expands even inside quotes and has no command-line
/// escape, `!VAR!` joins in whenever delayed expansion is on, and a raw
/// `\r` or `\n` acts as a line boundary in several cmd contexts
/// regardless of quote state. The compose refuses the spawn rather than
/// guess: an expandable body is a live injection and a quoted one
/// silently corrupts the argument.
fn windowsBatchArgUnsafe(arg: []const u8) bool {
    return mem.indexOfAny(u8, arg, "%!\r\n") != null;
}

/// Whether the batch target's own path would need quoting: it carries a
/// byte cmd treats specially. (An empty path is unreachable here:
/// `windowsBatchTarget` needs an extension to see a batch at all.) Such
/// a path is refused outright, because CreateProcessW wraps a quoted
/// batch line in an extra quote pair when it composes `%COMSPEC% /c`
/// (measured with a COMSPEC spy that prints its own GetCommandLineW,
/// probe round 3d), and cmd then strips that pair and drops the
/// invocation: every quoted batch-path spawn is a silent no-op on
/// current Windows, with or without arguments, hostile or benign
/// (rounds 3c-3e). There is no compose that runs such a target, so the
/// compose refuses instead of writing a line the OS swallows.
fn windowsBatchPathNeedsQuoting(argv0: []const u8) bool {
    return mem.indexOfAny(u8, argv0, windows_batch_quote_bytes) != null;
}

/// Serialize `arg` with the MS C runtime quoting rules, which
/// CommandLineToArgvW (and so almost every Windows program) reverses.
fn windowsQuoteArg(writer: *std.Io.Writer, arg: []const u8) !void {
    if (mem.indexOfAny(u8, arg, " \t\n\"") == null) {
        try writer.writeAll(arg);
        return;
    }
    try writer.writeByte('"');
    var backslash_count: usize = 0;
    for (arg) |byte| {
        switch (byte) {
            '\\' => backslash_count += 1,
            '"' => {
                try writer.splatByteAll('\\', backslash_count * 2 + 1);
                try writer.writeByte('"');
                backslash_count = 0;
            },
            else => {
                try writer.splatByteAll('\\', backslash_count);
                try writer.writeByte(byte);
                backslash_count = 0;
            },
        }
    }
    try writer.splatByteAll('\\', backslash_count * 2);
    try writer.writeByte('"');
}

/// Build the `lpCommandLine` for `argv`. `argv` must be non-empty; the
/// only caller guarantees it by falling back to `path`.
///
/// Arguments are quoted with the C runtime rules, except the script of a
/// cmd.exe `/C` (or `/K`) invocation. cmd.exe parses that tail with its
/// own rules and has no `\"` escape, so C runtime quoting reaches the
/// child as literal backslashes and truncates the command at the last
/// escaped quote. Such a script is written verbatim inside a single
/// quote pair instead.
///
/// One pair is all cmd needs. It keeps a line's quoting untouched only
/// when the line holds exactly two quotes, no `&<>()@^|` between them,
/// one or more whitespace characters between them, and the text between
/// them names an executable; otherwise it strips the outer pair and runs
/// the rest as written. A script that carries its own quotes or a
/// metacharacter fails that test and is stripped back to what the user
/// wrote, and a script that passes it is a bare quoted program path,
/// which is meant to stay quoted.
///
/// A batch-file target (argv[0] spelling a `.bat` or `.cmd` file, see
/// `windowsBatchTarget`) never reaches that branch: CreateProcessW runs
/// it through cmd.exe, which re-parses the whole line. Every element of
/// such an invocation is serialized with cmd's rules instead (see
/// `windowsBatchQuoteArg`), with three fail-closed exceptions refused as
/// `error.BatchArgumentUnsafe`: a target path that would need quoting
/// (CreateProcessW wraps such a line in an extra quote pair and cmd
/// strips that pair and drops the invocation outright, probe rounds
/// 3c-3e), and any element carrying `%`/`!` (quote-blind expansion, no
/// command-line escape) or a raw `\r`/`\n` (line boundary regardless of
/// quote state).
///
/// `pub` for the Exec wrap tests, which pin the composed line at the
/// seam (wrap output fed straight through this function); the only
/// production caller remains `startWindows` below.
pub fn windowsCreateCommandLine(allocator: mem.Allocator, argv: []const []const u8) ![:0]u8 {
    var buf: std.Io.Writer.Allocating = .init(allocator);
    defer buf.deinit();
    const writer = &buf.writer;

    const cmd_switch = windowsCmdScriptSwitch(argv);
    const batch_target = windowsBatchTarget(argv[0]);

    for (argv, 0..) |arg, arg_i| {
        if (arg_i != 0) try writer.writeByte(' ');

        if (cmd_switch) |i| if (arg_i == i) {
            try writer.writeAll(arg);
            try writer.writeAll(" \"");
            try writer.writeAll(argv[i + 1]);
            try writer.writeByte('"');
            break;
        };

        if (batch_target) {
            // No error-level log at either refusal site: the termio io
            // thread already reports the failed spawn, and error-level
            // logs fail any test run that exercises these branches. The
            // debug lines name the argument index, never its content:
            // arguments can carry secrets.
            if (windowsBatchArgUnsafe(arg)) {
                log.debug(
                    "refusing batch-file spawn: argument {d} carries a byte cmd.exe expands or splits even quoted",
                    .{arg_i},
                );
                return error.BatchArgumentUnsafe;
            }
            if (arg_i == 0 and windowsBatchPathNeedsQuoting(arg)) {
                log.debug(
                    "refusing batch-file spawn: the target path needs quoting, which cmd.exe silently drops",
                    .{},
                );
                return error.BatchArgumentUnsafe;
            }
            try windowsBatchQuoteArg(writer, arg);
            continue;
        }

        try windowsQuoteArg(writer, arg);
    }

    return buf.toOwnedSliceSentinel(0);
}

test "windowsStdioMode: a pseudoconsole child takes neither std handles nor inheritance" {
    // A ConPTY child gets its standard handles from the pseudoconsole it
    // is attached to, and nothing in that spawn is passed by inheritance.
    const conpty = windowsStdioMode(true);
    try testing.expect(!conpty.use_std_handles);
    try testing.expect(!conpty.inherit_handles);

    // The explicit-handle spawn is the opposite: the child only sees the
    // three handles we name, and only if it inherits them.
    const explicit = windowsStdioMode(false);
    try testing.expect(explicit.use_std_handles);
    try testing.expect(explicit.inherit_handles);
}

test "windowsCreateCommandLine: a cmd.exe script keeps its own quoting" {
    const alloc = testing.allocator;

    const line = try windowsCreateCommandLine(alloc, &.{
        "C:\\Windows\\System32\\cmd.exe",
        "/c",
        "chcp 65001 >nul && dir \"C:\\Program Files\"",
    });
    defer alloc.free(line);

    // Four quotes, so cmd strips the outer pair and hands the rest to
    // its own tokenizer exactly as the user wrote it.
    try testing.expectEqualStrings(
        "C:\\Windows\\System32\\cmd.exe /c " ++
            "\"chcp 65001 >nul && dir \"C:\\Program Files\"\"",
        line,
    );
}

test "windowsCreateCommandLine: a cmd.exe script without quotes gets one pair" {
    const alloc = testing.allocator;

    // Two quotes, but the pipe between them is a cmd metacharacter, so
    // the pair is stripped and the script runs as written.
    //
    // A script with no quotes of its own is byte identical under either
    // rule, so this case pins the shape and nothing else. The quoted
    // case below is the one that tells the two rules apart.
    const plain = try windowsCreateCommandLine(alloc, &.{
        "cmd.exe",
        "/C",
        "echo hi | more",
    });
    defer alloc.free(plain);
    try testing.expectEqualStrings("cmd.exe /C \"echo hi | more\"", plain);

    // The same pipeline with a quoted argument. C runtime quoting would
    // write the inner quotes as \", which cmd has no escape for and
    // would hand to `echo` as literal backslashes.
    const quoted = try windowsCreateCommandLine(alloc, &.{
        "cmd.exe",
        "/C",
        "echo \"hi there\" | more",
    });
    defer alloc.free(quoted);
    try testing.expectEqualStrings(
        "cmd.exe /C \"echo \"hi there\" | more\"",
        quoted,
    );
}

test "windowsCreateCommandLine: a bare quoted program path stays quoted" {
    const alloc = testing.allocator;

    // The one case where cmd preserves the quoting instead of stripping
    // it, which is what a program path with a space needs.
    //
    // Like the plain pipeline above, a script carrying no quotes is byte
    // identical under either rule, so the pre-quoted case below is what
    // discriminates.
    const bare = try windowsCreateCommandLine(alloc, &.{
        "cmd.exe",
        "/c",
        "C:\\Program Files\\app.exe",
    });
    defer alloc.free(bare);
    try testing.expectEqualStrings("cmd.exe /c \"C:\\Program Files\\app.exe\"", bare);

    // A user who quoted the path themselves and added an argument gets
    // exactly the quotes they wrote. Four quotes, so cmd strips the
    // outer pair and the inner pair is what keeps the path together.
    const prequoted = try windowsCreateCommandLine(alloc, &.{
        "cmd.exe",
        "/c",
        "\"C:\\Program Files\\app.exe\" --flag",
    });
    defer alloc.free(prequoted);
    try testing.expectEqualStrings(
        "cmd.exe /c \"\"C:\\Program Files\\app.exe\" --flag\"",
        prequoted,
    );
}

test "windowsCreateCommandLine: a caller supplied /S still gets the verbatim script" {
    const alloc = testing.allocator;

    const line = try windowsCreateCommandLine(alloc, &.{
        "cmd.exe",
        "/s",
        "/c",
        "dir \"C:\\Program Files\"",
    });
    defer alloc.free(line);

    // `/S` only makes the outer-pair strip unconditional, which is what
    // the verbatim script already relies on.
    try testing.expectEqualStrings(
        "cmd.exe /s /c \"dir \"C:\\Program Files\"\"",
        line,
    );
}

test "windowsCreateCommandLine: everything else keeps the C runtime quoting" {
    const alloc = testing.allocator;

    // pwsh parses its command line with CommandLineToArgvW, so a quote
    // inside an argument must arrive backslash escaped.
    const pwsh = try windowsCreateCommandLine(alloc, &.{
        "pwsh.exe",
        "-Command",
        "Get-ChildItem \"C:\\Program Files\"",
    });
    defer alloc.free(pwsh);
    try testing.expectEqualStrings(
        "pwsh.exe -Command \"Get-ChildItem \\\"C:\\Program Files\\\"\"",
        pwsh,
    );

    // A bare cmd.exe with no script is not a `/C` invocation.
    const bare = try windowsCreateCommandLine(alloc, &.{"cmd.exe"});
    defer alloc.free(bare);
    try testing.expectEqualStrings("cmd.exe", bare);

    // Neither is one whose trailing switch is not /C or /K.
    const query = try windowsCreateCommandLine(alloc, &.{ "cmd.exe", "/q", "arg one" });
    defer alloc.free(query);
    try testing.expectEqualStrings("cmd.exe /q \"arg one\"", query);
}

test "windowsCreateCommandLine: a batch target's hostile argument stays one argument" {
    const alloc = testing.allocator;

    // CreateProcessW hands a .cmd target to `%COMSPEC% /c`, and cmd
    // splits commands on unquoted `&`. C runtime quoting leaves this
    // body bare, which runs `echo.X>injected.txt` as a second command
    // (issue #1172); the batch rules quote it instead.
    const line = try windowsCreateCommandLine(alloc, &.{
        "C:\\Temp\\a.cmd",
        "r.txt&echo.X>injected.txt",
    });
    defer alloc.free(line);
    try testing.expectEqualStrings(
        "C:\\Temp\\a.cmd \"r.txt&echo.X>injected.txt\"",
        line,
    );
}

test "windowsCreateCommandLine: a batch target keeps benign arguments bare" {
    const alloc = testing.allocator;

    // An argument with nothing cmd treats specially stays byte
    // identical, so existing invocations do not grow quotes. `=` is a
    // cmd parameter delimiter, so it must quote; a plain name must not.
    const line = try windowsCreateCommandLine(alloc, &.{
        "C:\\Temp\\a.cmd",
        "r.txt",
        "--flag=value",
    });
    defer alloc.free(line);
    try testing.expectEqualStrings(
        "C:\\Temp\\a.cmd r.txt \"--flag=value\"",
        line,
    );
}

test "windowsCreateCommandLine: a batch target whose path needs quoting is refused" {
    const alloc = testing.allocator;

    // CreateProcessW wraps a quoted batch line in an extra quote pair when
    // it composes `%COMSPEC% /c`, and cmd then strips that pair and drops
    // the invocation: every quoted batch path is a silent no-op on current
    // Windows (probe rounds 3c-3e), with or without arguments, hostile or
    // benign. The compose refuses instead of writing a line the OS
    // swallows.
    try testing.expectError(
        error.BatchArgumentUnsafe,
        windowsCreateCommandLine(alloc, &.{ "C:\\a&b\\x.cmd", "arg" }),
    );
    try testing.expectError(
        error.BatchArgumentUnsafe,
        windowsCreateCommandLine(alloc, &.{ "C:\\p a\\x.cmd", "y.txt" }),
    );
    try testing.expectError(
        error.BatchArgumentUnsafe,
        windowsCreateCommandLine(alloc, &.{"C:\\p a\\x.cmd"}),
    );
    try testing.expectError(
        error.BatchArgumentUnsafe,
        windowsCreateCommandLine(alloc, &.{"\"C:\\p a\\x.cmd\""}),
    );
}

test "windowsCreateCommandLine: a batch target is detected in every spelling Windows resolves" {
    const alloc = testing.allocator;

    // Detection errs toward detecting a batch; the other direction is the
    // original injection. A trailing-dot spelling still takes the cmd.exe
    // path (the loader's batch special-case keys on the file it resolves),
    // but cmd cannot resolve the spelled name and the batch does not run:
    // what made the spelling dangerous was the pre-fix bare compose, in
    // which the hostile tail after it ran as a second command (probe
    // round 3d, B1, measured live). An ADS spelling is actually rejected
    // by the loader
    // (round 3a, R9: CreateProcessW fails), and is cut anyway on purpose:
    // over-detection only quotes arguments, never bare. The cases pin the
    // shapes: plain trailing dot, ADS, dot before the ADS colon, a
    // leading-dot name (over-detection), a trailing separator
    // (basenameWindows trims it), and case-insensitive classification.
    const cases = [_]struct { path: []const u8 }{
        .{ .path = "C:\\Temp\\x.cmd." },
        .{ .path = "C:\\Temp\\x.cmd:stream" },
        .{ .path = "C:\\Temp\\x.cmd.:stream" },
        .{ .path = "C:\\Temp\\.cmd" },
        .{ .path = "C:\\Temp\\x.cmd\\" },
        .{ .path = "C:\\Temp\\A.BAT" },
    };
    for (cases) |c| {
        const actual = try windowsCreateCommandLine(alloc, &.{
            c.path,
            "r.txt&echo.X>injected.txt",
        });
        defer alloc.free(actual);
        const expected = try std.fmt.allocPrintSentinel(
            alloc,
            "{s} \"r.txt&echo.X>injected.txt\"",
            .{c.path},
            0,
        );
        defer alloc.free(expected);
        try testing.expectEqualStrings(expected, actual);
    }
}

test "windowsCreateCommandLine: a batch target quotes every cmd-special argument" {
    const alloc = testing.allocator;

    // The quote set's remaining command-separating members, a delimiter
    // byte, and an empty argument. The first argument carries ONLY the
    // pipe: every other member of the quote set also forces quoting
    // somewhere else in the suite, so this is the case that fails if the
    // pipe is ever dropped from the set. The last argument pairs a space
    // with a trailing backslash: quoting is forced by the space, and the
    // backslash then stays literal inside the pair (cmd has no `\"` rule,
    // so one trailing backslash stays one backslash). A backslash-only
    // argument carries nothing cmd treats specially and stays bare.
    const line = try windowsCreateCommandLine(alloc, &.{
        "a.cmd",
        "x|y",
        "a(b)",
        "--flag=v;w",
        "",
        "C:\\di r\\",
    });
    defer alloc.free(line);
    try testing.expectEqualStrings(
        "a.cmd \"x|y\" \"a(b)\" \"--flag=v;w\" \"\" \"C:\\di r\\\"",
        line,
    );
}

test "windowsCreateCommandLine: a cmd.exe /c script is untouched by the batch rules" {
    const alloc = testing.allocator;

    // The verbatim-script branch matches cmd.exe as argv[0]; a batch target
    // matches a .bat/.cmd extension. The two never overlap, and the script
    // tail keeps its own quoting either way.
    const line = try windowsCreateCommandLine(alloc, &.{
        "cmd.exe",
        "/c",
        "x.cmd",
    });
    defer alloc.free(line);
    try testing.expectEqualStrings("cmd.exe /c \"x.cmd\"", line);
}

test "windowsCreateCommandLine: a batch target argument's embedded quotes double" {
    const alloc = testing.allocator;

    // cmd has no `\"` escape: a quote toggles its parse state wherever
    // it sits, so doubling is the only way to keep an embedded quote
    // literal while the metacharacters around it stay inside the pair.
    const line = try windowsCreateCommandLine(alloc, &.{
        "a.cmd",
        "say \"hi\" & dir",
    });
    defer alloc.free(line);
    try testing.expectEqualStrings("a.cmd \"say \"\"hi\"\" & dir\"", line);
}

test "windowsCreateCommandLine: a batch target refuses % and ! arguments" {
    const alloc = testing.allocator;

    // %VAR% expands even inside quotes, and `!VAR!` joins in whenever
    // delayed expansion is on; neither has a command-line escape, so
    // the compose refuses the spawn instead of guessing.
    try testing.expectError(
        error.BatchArgumentUnsafe,
        windowsCreateCommandLine(alloc, &.{ "a.cmd", "100%" }),
    );
    try testing.expectError(
        error.BatchArgumentUnsafe,
        windowsCreateCommandLine(alloc, &.{ "a.cmd", "a!b" }),
    );
    try testing.expectError(
        error.BatchArgumentUnsafe,
        windowsCreateCommandLine(alloc, &.{ "a%b.cmd", "x" }),
    );
    try testing.expectError(
        error.BatchArgumentUnsafe,
        windowsCreateCommandLine(alloc, &.{ "a.cmd", "x", "%PATH%" }),
    );
    try testing.expectError(
        error.BatchArgumentUnsafe,
        windowsCreateCommandLine(alloc, &.{ "a.cmd", "x\r\ny&calc" }),
    );
}

test "windowsCreateCommandLine: a non-batch target keeps the C runtime rules" {
    const alloc = testing.allocator;

    // Only batch targets are re-parsed by cmd.exe. Everywhere else an
    // argument body like this is one argv element to the child, and
    // stays written under the C runtime rules.
    const line = try windowsCreateCommandLine(alloc, &.{
        "tool.exe",
        "r.txt&echo.X",
    });
    defer alloc.free(line);
    try testing.expectEqualStrings("tool.exe r.txt&echo.X", line);
}

test "Command: a batch target whose path needs quoting refuses to spawn" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    const alloc = testing.allocator;

    var td = try TempDir.init();
    defer td.deinit();

    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const dir_len = try td.dir.realPath(testing.io, &path_buf);
    const dir_path = try std.fmt.allocPrintSentinel(
        alloc,
        "{s}",
        .{path_buf[0..dir_len]},
        0,
    );
    defer alloc.free(dir_path);
    // The space in the name is the point: CreateProcessW wraps such a line
    // in an extra quote pair and cmd drops the whole invocation, so the
    // compose must refuse before any spawn. The file never has to exist;
    // the refusal happens in the compose, before CreateProcessW.
    const bat_path = try std.fmt.allocPrintSentinel(
        alloc,
        "{s}\\s p.cmd",
        .{dir_path},
        0,
    );
    defer alloc.free(bat_path);

    var cmd: Command = .{
        .path = bat_path,
        .args = &.{ bat_path, "r.txt" },
        .os_pre_exec = null,
        .rt_pre_exec = null,
        .rt_post_fork = null,
        .rt_pre_exec_info = undefined,
        .rt_post_fork_info = undefined,
    };

    try testing.expectError(error.BatchArgumentUnsafe, cmd.testingStart());
}

test "Command: a batch file target's argument cannot run a second command" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    const alloc = testing.allocator;

    var td = try TempDir.init();
    defer td.deinit();

    // The batch records its %* view. If the composed command line lets
    // cmd re-parse the argument body, `echo.X>injected.txt` runs as a
    // second command and injected.txt appears (issue #1172).
    try td.dir.writeFile(testing.io, .{
        .sub_path = "hostile.cmd",
        .data = "@echo off\r\necho args: %*>batch.marker\r\n",
    });

    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const dir_len = try td.dir.realPath(testing.io, &path_buf);
    const dir_path = try std.fmt.allocPrintSentinel(
        alloc,
        "{s}",
        .{path_buf[0..dir_len]},
        0,
    );
    defer alloc.free(dir_path);

    // A temp path carrying a byte cmd treats specially (a space in the
    // user name, for one) cannot reach the bare-path shape this test
    // exercises; the refusal tests own that case.
    for (dir_path) |byte| {
        if (mem.indexOfScalar(u8, windows_batch_quote_bytes, byte) != null) {
            return error.SkipZigTest;
        }
    }

    const bat_path = try std.fmt.allocPrintSentinel(
        alloc,
        "{s}\\hostile.cmd",
        .{dir_path},
        0,
    );
    defer alloc.free(bat_path);

    var stdout = try createTestStdout(testing.io, td.dir);
    defer stdout.close(testing.io);

    var cmd: Command = .{
        .path = bat_path,
        .args = &.{ bat_path, "r.txt&echo.X>injected.txt" },
        .stdout = stdout,
        .cwd = dir_path,
        .os_pre_exec = null,
        .rt_pre_exec = null,
        .rt_post_fork = null,
        .rt_pre_exec_info = undefined,
        .rt_post_fork_info = undefined,
    };

    try cmd.testingStart();
    try testing.expect(cmd.pid != null);
    const exit = try cmd.wait(true);
    try testing.expect(exit == .Exited);
    try testing.expectEqual(@as(u32, 0), @as(u32, exit.Exited));

    // The second command must not have run.
    if (td.dir.openFile(testing.io, "injected.txt", .{})) |file| {
        file.close(testing.io);
        log.err("batch injection: the argument body ran as a second command", .{});
        return error.SecondCommandRan;
    } else |err| {
        try testing.expectEqual(error.FileNotFound, err);
    }

    // The batch itself ran and received the body as ONE argument.
    const marker = try td.dir.readFileAlloc(testing.io, "batch.marker", alloc, .limited(4096));
    defer alloc.free(marker);
    try testing.expect(
        mem.indexOf(u8, marker, "args: \"r.txt&echo.X>injected.txt\"") != null,
    );
}

test "createNullDelimitedEnvMap" {
    const allocator = testing.allocator;
    var envmap = EnvMap.init(allocator);
    defer envmap.deinit();

    try envmap.put("HOME", "/home/ifreund");
    try envmap.put("WAYLAND_DISPLAY", "wayland-1");
    try envmap.put("DISPLAY", ":1");
    try envmap.put("DEBUGINFOD_URLS", " ");
    try envmap.put("XCURSOR_SIZE", "24");

    var arena = std.heap.ArenaAllocator.init(allocator);
    defer arena.deinit();
    const environ = try createNullDelimitedEnvMap(arena.allocator(), &envmap);

    try testing.expectEqual(@as(usize, 5), environ.len);

    inline for (.{
        "HOME=/home/ifreund",
        "WAYLAND_DISPLAY=wayland-1",
        "DISPLAY=:1",
        "DEBUGINFOD_URLS= ",
        "XCURSOR_SIZE=24",
    }) |target| {
        for (environ) |variable| {
            if (mem.eql(u8, mem.span(variable orelse continue), target)) break;
        } else {
            try testing.expect(false); // Environment variable not found
        }
    }
}

test "Command: os pre exec 1" {
    if (builtin.os.tag == .windows) return error.SkipZigTest;
    var cmd: Command = .{
        .path = "/bin/sh",
        .args = &.{ "/bin/sh", "-v" },
        .os_pre_exec = (struct {
            fn do(_: *Command) ?u8 {
                // This runs in the child, so we can exit and it won't
                // kill the test runner.
                posix.system.exit(42);
            }
        }).do,
        .rt_pre_exec = null,
        .rt_post_fork = null,
        .rt_pre_exec_info = undefined,
        .rt_post_fork_info = undefined,
    };

    try cmd.testingStart();
    try testing.expect(cmd.pid != null);
    const exit = try cmd.wait(true);
    try testing.expect(exit == .Exited);
    try testing.expect(exit.Exited == 42);
}

test "Command: os pre exec 2" {
    if (builtin.os.tag == .windows) return error.SkipZigTest;
    var cmd: Command = .{
        .path = "/bin/sh",
        .args = &.{ "/bin/sh", "-v" },
        .os_pre_exec = (struct {
            fn do(_: *Command) ?u8 {
                // This runs in the child, so we can exit and it won't
                // kill the test runner.
                return 42;
            }
        }).do,
        .rt_pre_exec = null,
        .rt_post_fork = null,
        .rt_pre_exec_info = undefined,
        .rt_post_fork_info = undefined,
    };

    try cmd.testingStart();
    try testing.expect(cmd.pid != null);
    const exit = try cmd.wait(true);
    try testing.expect(exit == .Exited);
    try testing.expect(exit.Exited == 42);
}

test "Command: rt pre exec 1" {
    if (builtin.os.tag == .windows) return error.SkipZigTest;
    var cmd: Command = .{
        .path = "/bin/sh",
        .args = &.{ "/bin/sh", "-v" },
        .os_pre_exec = null,
        .rt_pre_exec = (struct {
            fn do(_: *Command) ?u8 {
                // This runs in the child, so we can exit and it won't
                // kill the test runner.
                posix.system.exit(42);
            }
        }).do,
        .rt_post_fork = null,
        .rt_pre_exec_info = undefined,
        .rt_post_fork_info = undefined,
    };

    try cmd.testingStart();
    try testing.expect(cmd.pid != null);
    const exit = try cmd.wait(true);
    try testing.expect(exit == .Exited);
    try testing.expect(exit.Exited == 42);
}

test "Command: rt pre exec 2" {
    if (builtin.os.tag == .windows) return error.SkipZigTest;
    var cmd: Command = .{
        .path = "/bin/sh",
        .args = &.{ "/bin/sh", "-v" },
        .os_pre_exec = null,
        .rt_pre_exec = (struct {
            fn do(_: *Command) ?u8 {
                // This runs in the child, so we can exit and it won't
                // kill the test runner.
                return 42;
            }
        }).do,
        .rt_post_fork = null,
        .rt_pre_exec_info = undefined,
        .rt_post_fork_info = undefined,
    };

    try cmd.testingStart();
    try testing.expect(cmd.pid != null);
    const exit = try cmd.wait(true);
    try testing.expect(exit == .Exited);
    try testing.expect(exit.Exited == 42);
}

test "Command: rt post fork 1" {
    if (builtin.os.tag == .windows) return error.SkipZigTest;
    var cmd: Command = .{
        .path = "/bin/sh",
        .args = &.{ "/bin/sh", "-c", "sleep 1" },
        .os_pre_exec = null,
        .rt_pre_exec = null,
        .rt_post_fork = (struct {
            fn do(_: *Command) PostForkError!void {
                return error.PostForkError;
            }
        }).do,
        .rt_pre_exec_info = undefined,
        .rt_post_fork_info = undefined,
    };

    try testing.expectError(error.PostForkError, cmd.testingStart());
}

fn createTestStdout(io: std.Io, dir: std.Io.Dir) !File {
    const file = try dir.createFile(io, "stdout.txt", .{ .read = true });
    if (builtin.os.tag == .windows) {
        if (windows.exp.kernel32.SetHandleInformation(
            file.handle,
            windows.HANDLE_FLAG_INHERIT,
            windows.HANDLE_FLAG_INHERIT,
        ) == windows.FALSE) {
            return windows.unexpectedError(windows.GetLastError());
        }
    }

    return file;
}

fn createTestStderr(io: std.Io, dir: std.Io.Dir) !File {
    const file = try dir.createFile(io, "stderr.txt", .{ .read = true });
    if (builtin.os.tag == .windows) {
        if (windows.exp.kernel32.SetHandleInformation(
            file.handle,
            windows.HANDLE_FLAG_INHERIT,
            windows.HANDLE_FLAG_INHERIT,
        ) == windows.FALSE) {
            return windows.unexpectedError(windows.GetLastError());
        }
    }

    return file;
}

test "Command: redirect stdout to file" {
    var td = try TempDir.init();
    defer td.deinit();
    var stdout = try createTestStdout(testing.io, td.dir);
    defer stdout.close(testing.io);

    var cmd: Command = if (builtin.os.tag == .windows) .{
        .path = "C:\\Windows\\System32\\whoami.exe",
        .args = &.{"C:\\Windows\\System32\\whoami.exe"},
        .stdout = stdout,
        .os_pre_exec = null,
        .rt_pre_exec = null,
        .rt_post_fork = null,
        .rt_pre_exec_info = undefined,
        .rt_post_fork_info = undefined,
    } else .{
        .path = "/bin/sh",
        .args = &.{ "/bin/sh", "-c", "echo hello" },
        .stdout = stdout,
        .os_pre_exec = null,
        .rt_pre_exec = null,
        .rt_post_fork = null,
        .rt_pre_exec_info = undefined,
        .rt_post_fork_info = undefined,
    };

    try cmd.testingStart();
    try testing.expect(cmd.pid != null);
    const exit = try cmd.wait(true);
    try testing.expect(exit == .Exited);
    try testing.expectEqual(@as(u32, 0), @as(u32, exit.Exited));

    // Read our stdout
    const contents = contents: {
        const size = (try stdout.stat(testing.io)).size;
        const data = try testing.allocator.alloc(u8, size);
        errdefer testing.allocator.free(data);
        try testing.expectEqual(size, try stdout.readPositionalAll(testing.io, data, 0));
        break :contents data;
    };
    defer testing.allocator.free(contents);
    try testing.expect(contents.len > 0);
}

test "Command: custom env vars" {
    var td = try TempDir.init();
    defer td.deinit();
    var stdout = try createTestStdout(testing.io, td.dir);
    defer stdout.close(testing.io);

    var env = EnvMap.init(testing.allocator);
    defer env.deinit();
    try env.put("VALUE", "hello");

    var cmd: Command = if (builtin.os.tag == .windows) .{
        .path = "C:\\Windows\\System32\\cmd.exe",
        .args = &.{ "C:\\Windows\\System32\\cmd.exe", "/C", "echo %VALUE%" },
        .stdout = stdout,
        .env = &env,
        .os_pre_exec = null,
        .rt_pre_exec = null,
        .rt_post_fork = null,
        .rt_pre_exec_info = undefined,
        .rt_post_fork_info = undefined,
    } else .{
        .path = "/bin/sh",
        .args = &.{ "/bin/sh", "-c", "echo $VALUE" },
        .stdout = stdout,
        .env = &env,
        .os_pre_exec = null,
        .rt_pre_exec = null,
        .rt_post_fork = null,
        .rt_pre_exec_info = undefined,
        .rt_post_fork_info = undefined,
    };

    try cmd.testingStart();
    try testing.expect(cmd.pid != null);
    const exit = try cmd.wait(true);
    try testing.expect(exit == .Exited);
    try testing.expect(exit.Exited == 0);

    // Read our stdout
    const contents = contents: {
        const size = (try stdout.stat(testing.io)).size;
        const data = try testing.allocator.alloc(u8, size);
        errdefer testing.allocator.free(data);
        try testing.expectEqual(size, try stdout.readPositionalAll(testing.io, data, 0));
        break :contents data;
    };
    defer testing.allocator.free(contents);

    if (builtin.os.tag == .windows) {
        try testing.expectEqualStrings("hello\r\n", contents);
    } else {
        try testing.expectEqualStrings("hello\n", contents);
    }
}

test "Command: custom working directory" {
    var td = try TempDir.init();
    defer td.deinit();
    var stdout = try createTestStdout(testing.io, td.dir);
    defer stdout.close(testing.io);

    var cmd: Command = if (builtin.os.tag == .windows) .{
        .path = "C:\\Windows\\System32\\cmd.exe",
        .args = &.{ "C:\\Windows\\System32\\cmd.exe", "/C", "cd" },
        .stdout = stdout,
        .cwd = "C:\\Windows\\System32",
        .os_pre_exec = null,
        .rt_pre_exec = null,
        .rt_post_fork = null,
        .rt_pre_exec_info = undefined,
        .rt_post_fork_info = undefined,
    } else .{
        .path = "/bin/sh",
        .args = &.{ "/bin/sh", "-c", "pwd" },
        .stdout = stdout,
        .cwd = "/tmp",
        .os_pre_exec = null,
        .rt_pre_exec = null,
        .rt_post_fork = null,
        .rt_pre_exec_info = undefined,
        .rt_post_fork_info = undefined,
    };

    try cmd.testingStart();
    try testing.expect(cmd.pid != null);
    const exit = try cmd.wait(true);
    try testing.expect(exit == .Exited);
    try testing.expect(exit.Exited == 0);

    // Read our stdout
    const contents = contents: {
        const size = (try stdout.stat(testing.io)).size;
        const data = try testing.allocator.alloc(u8, size);
        errdefer testing.allocator.free(data);
        try testing.expectEqual(size, try stdout.readPositionalAll(testing.io, data, 0));
        break :contents data;
    };
    defer testing.allocator.free(contents);

    if (builtin.os.tag == .windows) {
        try testing.expectEqualStrings("C:\\Windows\\System32\r\n", contents);
    } else if (builtin.os.tag == .macos) {
        try testing.expectEqualStrings("/private/tmp\n", contents);
    } else {
        try testing.expectEqualStrings("/tmp\n", contents);
    }
}

// Test validate an execveZ failure correctly terminates when error.ExecFailedInChild is correctly handled
//
// Incorrectly handling an error.ExecFailedInChild results in a second copy of the test process running.
// Duplicating the test process leads to weird behavior
// zig build test will hang
// test binary created via -Demit-test-exe will run 2 copies of the test suite
test "Command: posix fork handles execveZ failure" {
    if (builtin.os.tag == .windows) {
        return error.SkipZigTest;
    }
    var td = try TempDir.init();
    defer td.deinit();
    var stdout = try createTestStdout(testing.io, td.dir);
    defer stdout.close(testing.io);
    var stderr = try createTestStderr(testing.io, td.dir);
    defer stderr.close(testing.io);

    var cmd: Command = .{
        .path = "/not/a/binary",
        .args = &.{ "/not/a/binary", "" },
        .stdout = stdout,
        .stderr = stderr,
        .cwd = "/bin",
        .os_pre_exec = null,
        .rt_pre_exec = null,
        .rt_post_fork = null,
        .rt_pre_exec_info = undefined,
        .rt_post_fork_info = undefined,
    };

    try cmd.testingStart();
    try testing.expect(cmd.pid != null);
    const exit = try cmd.wait(true);
    try testing.expect(exit == .Exited);
    try testing.expect(exit.Exited == 1);
}

// If cmd.start fails with error.ExecFailedInChild it's the _child_ process that is running. If it does not
// terminate in response to that error both the parent and child will continue as if they _are_ the test suite
// process.
pub fn testingStart(self: *Command) !void {
    self.start(testing.allocator) catch |err| {
        if (err == error.ExecFailedInChild) {
            // I am a child process, I must not get confused and continue running the rest of the test suite.
            posix.system.exit(1);
        }
        return err;
    };
}
