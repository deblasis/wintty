//! Exec implements the logic for starting and stopping a subprocess with a
//! pty as well as spinning up the necessary read thread to read from the
//! pty and forward it to the Termio instance.
const Exec = @This();

const std = @import("std");
const builtin = @import("builtin");
const assert = @import("../quirks.zig").inlineAssert;
const Allocator = std.mem.Allocator;
const ArenaAllocator = std.heap.ArenaAllocator;
const posix = std.posix;
const xev = @import("../global.zig").xev;
const apprt = @import("../apprt.zig");
const build_config = @import("../build_config.zig");
const configpkg = @import("../config.zig");
const crash = @import("../crash/main.zig");
const fastmem = @import("../fastmem.zig");
const internal_os = @import("../os/main.zig");
const renderer = @import("../renderer.zig");
const shell_integration = @import("shell_integration.zig");
const wsl_shell_integration = @import("wsl_shell_integration.zig");
const terminfo_install = @import("../terminfo/install.zig");
const DiskCache = @import("../cli/ssh-cache/DiskCache.zig");
const terminal = @import("../terminal/main.zig");
const termio = @import("../termio.zig");
const Command = @import("../Command.zig");
const SegmentedPool = @import("../datastruct/main.zig").SegmentedPool;
const ptypkg = @import("../pty.zig");
const Pty = ptypkg.Pty;
const EnvMap = std.process.EnvMap;
const PasswdEntry = internal_os.passwd.Entry;
const windows = internal_os.windows;
const ProcessInfo = @import("../pty.zig").ProcessInfo;

const log = std.log.scoped(.io_exec);
const log_validate = std.log.scoped(.validate_transport);

/// The termios poll rate in milliseconds.
const TERMIOS_POLL_MS = 200;

/// If we build with flatpak support then we have to keep track of
/// a potential execution on the host.
const FlatpakHostCommand = if (!build_config.flatpak) struct {
    pub const Completion = struct {};
} else internal_os.FlatpakHostCommand;

/// The subprocess state for our exec backend.
subprocess: Subprocess,

/// Initialize the exec state. This will NOT start it, this only sets
/// up the internal state necessary to start it later.
pub fn init(
    alloc: Allocator,
    cfg: Config,
) !Exec {
    var subprocess = try Subprocess.init(alloc, cfg);
    errdefer subprocess.deinit();

    return .{ .subprocess = subprocess };
}

pub fn deinit(self: *Exec) void {
    self.subprocess.deinit();
}

/// Call to initialize the terminal state as necessary for this backend.
/// This is called before any termio begins. This should not be called
/// after termio begins because it may put the internal terminal state
/// into a bad state.
pub fn initTerminal(self: *Exec, term: *terminal.Terminal) void {
    // If we have an initial pwd requested by the subprocess, then we
    // set that on the terminal now. This allows rapidly initializing
    // new surfaces to use the proper pwd.
    if (self.subprocess.cwd) |cwd| term.setPwd(cwd) catch |err| {
        log.warn("error setting initial pwd err={}", .{err});
    };

    // Setup our initial grid/screen size from the terminal. This
    // can't fail because the pty should not exist at this point.
    self.resize(.{
        .columns = term.cols,
        .rows = term.rows,
    }, .{
        .width = term.width_px,
        .height = term.height_px,
    }) catch unreachable;
}

pub fn threadEnter(
    self: *Exec,
    alloc: Allocator,
    io: *termio.Termio,
    td: *termio.Termio.ThreadData,
) !void {
    // Start our subprocess
    const pty_fds = self.subprocess.start(alloc) catch |err| {
        // If we specifically got this error then we are in the forked
        // process and our child failed to execute. If we DIDN'T
        // get this specific error then we're in the parent and
        // we need to bubble it up.
        if (err != error.ExecFailedInChild) return err;

        // We're in the child. Nothing more we can do but abnormal exit.
        // The Command will output some additional information.
        posix.exit(1);
    };
    errdefer self.subprocess.stop();

    // Watcher to detect subprocess exit.
    //
    // On Windows we never use xev.Process because under ConPTY the child
    // process is already assigned to ConPTY's internal job object before we
    // get here. xev.Process.init calls CreateJobObject + AssignProcessToJobObject
    // which silently no-ops (the process cannot be moved to a new job), so the
    // IOCP completion never fires and processExitCommon is never called. We
    // replace the whole mechanism with a dedicated WaitForSingleObject thread
    // (see winProcessWaitThread below) that works regardless of job assignment.
    var process: ?xev.Process = if (comptime builtin.os.tag == .windows)
        null
    else if (self.subprocess.process) |v| switch (v) {
        .fork_exec => |cmd| try xev.Process.init(
            cmd.pid orelse return error.ProcessNoPid,
        ),

        // If we're executing via Flatpak then we can't do
        // traditional process watching (its implemented
        // as a special case in os/flatpak.zig) since the
        // command is on the host.
        .flatpak => null,
    } else return error.ProcessNotStarted;
    errdefer if (process) |*p| p.deinit();

    // On Windows, get a duplicated process handle for the wait thread.
    // We duplicate so that the wait thread owns its copy and can close it
    // independently of the Command's handle.
    const win_proc_handle: if (builtin.os.tag == .windows) windows.HANDLE else void =
        if (comptime builtin.os.tag == .windows) blk: {
        const cmd = switch (self.subprocess.process orelse return error.ProcessNotStarted) {
            .fork_exec => |c| c,
            .flatpak => unreachable, // Flatpak is Linux-only
        };
        const src = cmd.pid orelse return error.ProcessNoPid;
        var dup: windows.HANDLE = undefined;
        const self_proc = windows.kernel32.GetCurrentProcess();
        if (windows.kernel32.DuplicateHandle(
            self_proc,
            src,
            self_proc,
            &dup,
            0,
            windows.FALSE,
            windows.DUPLICATE_SAME_ACCESS,
        ) == 0) {
            return windows.unexpectedError(windows.kernel32.GetLastError());
        }
        break :blk dup;
    } else {};

    // Track our process start time for abnormal exits
    const process_start = try std.time.Instant.now();

    // Create our pipe that we'll use to kill our read thread.
    // pipe[0] is the read end, pipe[1] is the write end.
    const pipe = try internal_os.pipe();
    errdefer posix.close(pipe[0]);
    errdefer posix.close(pipe[1]);

    // Setup our stream so that we can write.
    var stream = xev.Stream.initFd(pty_fds.write);
    errdefer stream.deinit();

    // Start our timer to read termios state changes. This is used
    // to detect things such as when password input is being done
    // so we can render the terminal in a different way.
    var termios_timer = try xev.Timer.init();
    errdefer termios_timer.deinit();

    // Start our read thread
    const read_thread = try std.Thread.spawn(
        .{},
        if (builtin.os.tag == .windows) ReadThread.threadMainWindows else ReadThread.threadMainPosix,
        .{ pty_fds.read, io, pipe[0] },
    );
    read_thread.setName("io-reader") catch {};

    // Setup our threadata backend state to be our own
    td.backend = .{ .exec = .{
        .start = process_start,
        .write_stream = stream,
        .process = process,
        .read_thread = read_thread,
        .read_thread_pipe = pipe[1],
        .read_thread_fd = pty_fds.read,
        .termios_timer = termios_timer,
    } };

    // On Windows, spawn a dedicated thread that blocks on WaitForSingleObject
    // for the child process. This replaces the xev.Process watcher which does
    // not work under ConPTY because the child is already in ConPTY's job object
    // before we can assign it to our own.
    if (comptime builtin.os.tag == .windows) {
        const ctx = try io.alloc.create(WinProcessWaitCtx);
        ctx.* = .{ .handle = win_proc_handle, .td = td };
        // On error: close the dup handle and free ctx. The thread takes
        // ownership of both once it starts.
        errdefer {
            windows.CloseHandle(ctx.handle);
            io.alloc.destroy(ctx);
        }
        const wt = try std.Thread.spawn(.{}, winProcessWaitThread, .{ io.alloc, ctx });
        wt.setName("proc-wait") catch {};
        td.backend.exec.process_wait_thread = wt;
    }

    // Start our process watcher. If we have an xev.Process use it.
    if (process) |*p| p.wait(
        td.loop,
        &td.backend.exec.process_wait_c,
        termio.Termio.ThreadData,
        td,
        processExit,
    ) else if (comptime build_config.flatpak) flatpak: {
        switch (self.subprocess.process orelse break :flatpak) {
            // If we're in flatpak and we have a flatpak command
            // then we can run the special flatpak logic for watching.
            .flatpak => |*c| c.waitXev(
                td.loop,
                &td.backend.exec.flatpak_wait_c,
                termio.Termio.ThreadData,
                td,
                flatpakExit,
            ),

            .fork_exec => {},
        }
    }

    // Start our termios timer. We don't support this on Windows.
    // Fundamentally, we could support this on Windows so we're just
    // waiting for someone to implement it.
    if (comptime builtin.os.tag != .windows) {
        termios_timer.run(
            td.loop,
            &td.backend.exec.termios_timer_c,
            TERMIOS_POLL_MS,
            termio.Termio.ThreadData,
            td,
            termiosTimer,
        );
    }
}

pub fn threadExit(self: *Exec, td: *termio.Termio.ThreadData) void {
    assert(td.backend == .exec);
    const exec = &td.backend.exec;

    if (exec.exited) self.subprocess.externalExit();
    self.subprocess.stop();

    // Quit our read thread after exiting the subprocess so that
    // we don't get stuck waiting for data to stop flowing if it is
    // a particularly noisy process.
    _ = posix.write(exec.read_thread_pipe, "x") catch |err| switch (err) {
        // BrokenPipe means that our read thread is closed already,
        // which is completely fine since that is what we were trying
        // to achieve.
        error.BrokenPipe => {},

        else => log.warn(
            "error writing to read thread quit pipe err={}",
            .{err},
        ),
    };

    if (comptime builtin.os.tag == .windows) {
        // Interrupt the blocking read so the thread can see the quit message
        if (windows.kernel32.CancelIoEx(exec.read_thread_fd, null) == 0) {
            switch (windows.kernel32.GetLastError()) {
                .NOT_FOUND => {},
                else => |err| log.warn("error interrupting read thread err={}", .{err}),
            }
        }
    }

    exec.read_thread.join();

    // Join the Windows process-exit watcher thread. If the child already
    // exited naturally, winProcessWaitThread has already called
    // processExitCommon and returned. If we killed it via subprocess.stop()
    // above, it will call processExitCommon now - the extra child_exited
    // push is harmless because td is still valid until join() returns and
    // surface_mailbox is thread-safe.
    if (comptime builtin.os.tag == .windows) {
        if (exec.process_wait_thread) |wt| wt.join();
    }
}

pub fn focusGained(
    self: *Exec,
    td: *termio.Termio.ThreadData,
    focused: bool,
) !void {
    _ = self;

    assert(td.backend == .exec);
    const execdata = &td.backend.exec;

    // Windows has no termios, so there is nothing to poll.
    if (comptime builtin.os.tag == .windows) return;

    if (!focused) {
        // Flag the timer to end on the next iteration. This is
        // a lot cheaper than doing full timer cancellation.
        execdata.termios_timer_running = false;
    } else {
        // Always set this to true. There is a race condition if we lose
        // focus and regain focus before the termios timer ticks where
        // if we don't set this unconditionally the timer will end on
        // the next iteration.
        execdata.termios_timer_running = true;

        // If we're focused, we want to start our termios timer. We
        // only do this if it isn't already running. We use the termios
        // callback because that'll trigger an immediate state check AND
        // start the timer.
        // Skip on Windows where termios polling is not yet implemented.
        if (comptime builtin.os.tag != .windows) {
            if (execdata.termios_timer_c.state() != .active) {
                _ = termiosTimer(td, undefined, undefined, {});
            }
        }
    }
}

pub fn resize(
    self: *Exec,
    grid_size: renderer.GridSize,
    screen_size: renderer.ScreenSize,
) !void {
    return try self.subprocess.resize(grid_size, screen_size);
}

fn processExitCommon(td: *termio.Termio.ThreadData, exit_code: u32) void {
    assert(td.backend == .exec);
    const execdata = &td.backend.exec;
    // Non-atomic write is intentional and matches the POSIX xev-callback-thread
    // pattern. Single-byte writes are architecturally atomic on x86/ARM and the
    // read in queueWrite tolerates a stale value for one cycle.
    execdata.exited = true;

    // Determine how long the process was running for.
    const runtime_ms: ?u64 = runtime: {
        const process_end = std.time.Instant.now() catch break :runtime null;
        const runtime_ns = process_end.since(execdata.start);
        const runtime_ms = runtime_ns / std.time.ns_per_ms;
        break :runtime runtime_ms;
    };
    log.debug(
        "child process exited status={} runtime={}ms",
        .{ exit_code, runtime_ms orelse 0 },
    );

    // We always notify the surface immediately that the child has
    // exited and some metadata about the exit.
    _ = td.surface_mailbox.push(.{
        .child_exited = .{
            .exit_code = exit_code,
            .runtime_ms = runtime_ms orelse 0,
        },
    }, .{ .forever = {} });
}

fn processExit(
    td_: ?*termio.Termio.ThreadData,
    _: *xev.Loop,
    _: *xev.Completion,
    r: xev.Process.WaitError!u32,
) xev.CallbackAction {
    const exit_code = r catch unreachable;
    processExitCommon(td_.?, exit_code);
    return .disarm;
}

fn flatpakExit(
    td_: ?*termio.Termio.ThreadData,
    _: *xev.Loop,
    _: *FlatpakHostCommand.Completion,
    r: FlatpakHostCommand.WaitError!u8,
) void {
    const exit_code = r catch unreachable;
    processExitCommon(td_.?, exit_code);
}

fn termiosTimer(
    td_: ?*termio.Termio.ThreadData,
    _: *xev.Loop,
    _: *xev.Completion,
    r: xev.Timer.RunError!void,
) xev.CallbackAction {
    // log.debug("termios timer fired", .{});

    // This should never happen because we guard starting our
    // timer on windows but we want this assertion to fire if
    // we ever do start the timer on windows.
    // TODO: support on windows
    if (comptime builtin.os.tag == .windows) {
        @panic("termios timer not implemented on Windows");
    }

    _ = r catch |err| switch (err) {
        // This is sent when our timer is canceled. That's fine.
        error.Canceled => return .disarm,

        else => {
            log.warn("error in termios timer callback err={}", .{err});
            @panic("crash in termios timer callback");
        },
    };

    const td = td_.?;
    assert(td.backend == .exec);
    const exec = &td.backend.exec;

    // This is kind of hacky but we rebuild a Pty struct to get the
    // termios data.
    const mode: ptypkg.TerminalMode = (Pty{
        .master = exec.read_thread_fd,
        .slave = undefined,
    }).getMode() catch |err| err: {
        log.warn("error getting termios mode err={}", .{err});

        // If we have an error we return the default mode values
        // which are the likely values.
        break :err .{};
    };

    // If the mode changed, then we process it.
    if (!std.meta.eql(mode, exec.termios_mode)) mode_change: {
        log.debug("termios change mode={}", .{mode});
        exec.termios_mode = mode;

        // We assume we're in some sort of password input if we're
        // in canonical mode and not echoing. This is a heuristic.
        const password_input = mode.canonical and !mode.echo;

        // If our password input state changed on the terminal then
        // we notify the surface.
        {
            td.renderer_state.mutex.lock();
            defer td.renderer_state.mutex.unlock();
            const t = td.renderer_state.terminal;
            if (t.flags.password_input == password_input) {
                break :mode_change;
            }
        }

        // We have to notify the surface that we're in password input.
        // We must block on this because the balanced true/false state
        // of this is critical to apprt behavior.
        _ = td.surface_mailbox.push(.{
            .password_input = password_input,
        }, .{ .forever = {} });
    }

    // Repeat the timer
    if (exec.termios_timer_running) {
        exec.termios_timer.run(
            td.loop,
            &exec.termios_timer_c,
            TERMIOS_POLL_MS,
            termio.Termio.ThreadData,
            td,
            termiosTimer,
        );
    }

    return .disarm;
}

pub fn queueWrite(
    self: *Exec,
    alloc: Allocator,
    td: *termio.Termio.ThreadData,
    data: []const u8,
    linefeed: bool,
) !void {
    _ = self;
    const exec = &td.backend.exec;

    // If our process is exited then we don't send any more writes.
    if (exec.exited) return;

    // We go through and chunk the data if necessary to fit into
    // our cached buffers that we can queue to the stream.
    var i: usize = 0;
    while (i < data.len) {
        const req = try exec.write_req_pool.getGrow(alloc);
        const buf = try exec.write_buf_pool.getGrow(alloc);
        const slice = slice: {
            // The maximum end index is either the end of our data or
            // the end of our buffer, whichever is smaller.
            const max = @min(data.len, i + buf.len);

            // Fast
            if (!linefeed) {
                fastmem.copy(u8, buf, data[i..max]);
                const len = max - i;
                i = max;
                break :slice buf[0..len];
            }

            // Slow, have to replace \r with \r\n
            var buf_i: usize = 0;
            while (i < data.len and buf_i < buf.len - 1) {
                const ch = data[i];
                i += 1;

                if (ch != '\r') {
                    buf[buf_i] = ch;
                    buf_i += 1;
                    continue;
                }

                // CRLF
                buf[buf_i] = '\r';
                buf[buf_i + 1] = '\n';
                buf_i += 2;
            }

            break :slice buf[0..buf_i];
        };

        //for (slice) |b| log.warn("write: {x}", .{b});

        exec.write_stream.queueWrite(
            td.loop,
            &exec.write_queue,
            req,
            .{ .slice = slice },
            termio.Exec.ThreadData,
            exec,
            ttyWrite,
        );
    }
}

fn ttyWrite(
    td_: ?*ThreadData,
    _: *xev.Loop,
    _: *xev.Completion,
    _: xev.Stream,
    _: xev.WriteBuffer,
    r: xev.WriteError!usize,
) xev.CallbackAction {
    const td = td_.?;
    td.write_req_pool.put();
    td.write_buf_pool.put();

    const d = r catch |err| {
        log.err("write error: {}", .{err});
        return .disarm;
    };
    _ = d;
    //log.info("WROTE: {d}", .{d});

    return .disarm;
}

/// The thread local data for the exec implementation.
pub const ThreadData = struct {
    // The preallocation size for the write request pool. This should be big
    // enough to satisfy most write requests. It must be a power of 2.
    const WRITE_REQ_PREALLOC = std.math.pow(usize, 2, 5);

    /// Process start time and boolean of whether its already exited.
    start: std.time.Instant,
    exited: bool = false,

    /// The data stream is the main IO for the pty.
    write_stream: xev.Stream,

    /// The process watcher
    process: ?xev.Process,

    /// This is the pool of available (unused) write requests. If you grab
    /// one from the pool, you must put it back when you're done!
    write_req_pool: SegmentedPool(xev.WriteRequest, WRITE_REQ_PREALLOC) = .{},

    /// The pool of available buffers for writing to the pty.
    write_buf_pool: SegmentedPool([64]u8, WRITE_REQ_PREALLOC) = .{},

    /// The write queue for the data stream.
    write_queue: xev.WriteQueue = .{},

    /// This is used for both waiting for the process to exit and then
    /// subsequently to wait for the data_stream to close.
    process_wait_c: xev.Completion = .{},

    // The completion specific to Flatpak process waiting. If
    // we aren't compiling with Flatpak support this is zero-sized.
    flatpak_wait_c: FlatpakHostCommand.Completion = .{},

    /// Reader thread state
    read_thread: std.Thread,
    read_thread_pipe: posix.fd_t,
    read_thread_fd: posix.fd_t,

    /// Dedicated Windows process-exit watcher thread. Null on POSIX.
    /// Must be joined in threadExit before td is freed. Only valid when
    /// builtin.os.tag == .windows; always null on other platforms.
    process_wait_thread: if (builtin.os.tag == .windows) ?std.Thread else void =
        if (builtin.os.tag == .windows) null else {},

    /// The timer to detect termios state changes.
    termios_timer: xev.Timer,
    termios_timer_c: xev.Completion = .{},
    termios_timer_running: bool = true,

    /// The last known termios mode. Used for change detection
    /// to prevent unnecessary locking of expensive mutexes.
    termios_mode: ptypkg.TerminalMode = .{},

    pub fn deinit(self: *ThreadData, alloc: Allocator) void {
        posix.close(self.read_thread_pipe);

        // Clear our write pools. We know we aren't ever going to do
        // any more IO since we stop our data stream below so we can just
        // drop this.
        self.write_req_pool.deinit(alloc);
        self.write_buf_pool.deinit(alloc);

        // Stop our process watcher
        if (self.process) |*p| p.deinit();

        // Stop our write stream
        self.write_stream.deinit();

        // Stop our termios timer
        self.termios_timer.deinit();
    }
};

pub const Config = struct {
    command: ?configpkg.Command = null,
    env: EnvMap,
    env_override: configpkg.RepeatableStringMap = .{},
    shell_integration: configpkg.Config.ShellIntegration = .detect,
    shell_integration_features: configpkg.Config.ShellIntegrationFeatures = .{},
    cursor_blink: ?bool = null,
    working_directory: ?[]const u8 = null,
    resources_dir: ?[]const u8,
    term: []const u8,

    /// Windows UTF-8 console preamble policy. Resolved at spawn time
    /// against the system ANSI codepage (see `resolveUtf8Console`).
    /// Ignored on POSIX.
    utf8_console: if (builtin.os.tag == .windows)
        configpkg.Config.Utf8Console
    else
        void = if (builtin.os.tag == .windows) .auto else {},

    rt_pre_exec_info: Command.RtPreExecInfo,
    rt_post_fork_info: Command.RtPostForkInfo,
};

const Subprocess = struct {
    const c = @cImport({
        @cInclude("errno.h");
        @cInclude("signal.h");
        @cInclude("unistd.h");
    });

    arena: std.heap.ArenaAllocator,
    cwd: ?[:0]const u8,
    env: ?EnvMap,
    args: []const [:0]const u8,
    grid_size: renderer.GridSize,
    screen_size: renderer.ScreenSize,
    pty: ?Pty = null,
    process: ?Process = null,

    /// Captured from Config.utf8_console at init time; resolved against
    /// the system ACP per-spawn inside maybeInjectUtf8Preamble.
    utf8_console: if (builtin.os.tag == .windows)
        configpkg.Config.Utf8Console
    else
        void = if (builtin.os.tag == .windows) .auto else {},

    rt_pre_exec_info: Command.RtPreExecInfo,
    rt_post_fork_info: Command.RtPostForkInfo,

    /// Union that represents the running process type.
    const Process = union(enum) {
        /// Standard POSIX fork/exec
        fork_exec: Command,

        /// Flatpak DBus command
        flatpak: FlatpakHostCommand,
    };

    const ArgsFormatter = struct {
        args: []const [:0]const u8,

        pub fn format(this: @This(), writer: *std.Io.Writer) std.Io.Writer.Error!void {
            for (this.args, 0..) |a, i| {
                if (i > 0) try writer.writeAll(", ");
                try writer.print("`{s}`", .{a});
            }
        }
    };

    /// Initialize the subprocess. This will NOT start it, this only sets
    /// up the internal state necessary to start it later.
    pub fn init(gpa: Allocator, cfg: Config) !Subprocess {
        // We have a lot of maybe-allocations that all share the same lifetime
        // so use an arena so we don't end up in an accounting nightmare.
        var arena = std.heap.ArenaAllocator.init(gpa);
        errdefer arena.deinit();
        const alloc = arena.allocator();

        // Get our env. If a default env isn't provided by the caller
        // then we get it ourselves.
        var env = cfg.env;

        // If we have a resources dir then set our env var
        if (cfg.resources_dir) |dir| {
            log.info("found Ghostty resources dir: {s}", .{dir});
            try env.put("GHOSTTY_RESOURCES_DIR", dir);
        }

        // Set our TERM var. This is a bit complicated because we want to use
        // the ghostty TERM value but we want to only do that if we have
        // ghostty in the TERMINFO database.
        //
        // For now, we just look up a bundled dir but in the future we should
        // also load the terminfo database and look for it.
        if (cfg.resources_dir) |base| {
            try env.put("TERM", cfg.term);
            try env.put("COLORTERM", "truecolor");

            // Assume that the resources directory is adjacent to the terminfo
            // database
            var buf: [std.fs.max_path_bytes]u8 = undefined;
            const dir = try std.fmt.bufPrint(&buf, "{s}/terminfo", .{
                std.fs.path.dirname(base) orelse unreachable,
            });
            try env.put("TERMINFO", dir);
        } else {
            if (comptime builtin.target.os.tag.isDarwin()) {
                log.warn("ghostty terminfo not found, using xterm-256color", .{});
                log.warn("the terminfo SHOULD exist on macos, please ensure", .{});
                log.warn("you're using a valid app bundle.", .{});
            }

            try env.put("TERM", "xterm-256color");
            try env.put("COLORTERM", "truecolor");
        }

        // Add our binary to the path if we can find it.
        ghostty_path: {
            // Skip this for flatpak since host cannot reach them
            if ((comptime build_config.flatpak) and
                internal_os.isFlatpak())
            {
                break :ghostty_path;
            }

            var exe_buf: [std.fs.max_path_bytes]u8 = undefined;
            const exe_bin_path = std.fs.selfExePath(&exe_buf) catch |err| {
                log.warn("failed to get ghostty exe path err={}", .{err});
                break :ghostty_path;
            };
            const exe_dir = std.fs.path.dirname(exe_bin_path) orelse break :ghostty_path;
            log.debug("appending ghostty bin to path dir={s}", .{exe_dir});

            // We always set this so that if the shell overwrites the path
            // scripts still have a way to find the Ghostty binary when
            // running in Ghostty.
            try env.put("GHOSTTY_BIN_DIR", exe_dir);

            // Append if we have a path. We want to append so that ghostty is
            // the last priority in the path. If we don't have a path set
            // then we just set it to the directory of the binary.
            if (env.get("PATH")) |path| {
                // Verify that our path doesn't already contain this entry
                var it = std.mem.tokenizeScalar(u8, path, std.fs.path.delimiter);
                while (it.next()) |entry| {
                    if (std.mem.eql(u8, entry, exe_dir)) break :ghostty_path;
                }

                try env.put(
                    "PATH",
                    try internal_os.appendEnv(alloc, path, exe_dir),
                );
            } else {
                try env.put("PATH", exe_dir);
            }
        }

        // On macOS, export additional data directories from our
        // application bundle.
        if (comptime builtin.target.os.tag.isDarwin()) darwin: {
            const resources_dir = cfg.resources_dir orelse break :darwin;

            var buf: [std.fs.max_path_bytes]u8 = undefined;

            const xdg_data_dir_key = "XDG_DATA_DIRS";
            if (std.fmt.bufPrint(&buf, "{s}/..", .{resources_dir})) |data_dir| {
                try env.put(
                    xdg_data_dir_key,
                    try internal_os.appendEnv(
                        alloc,
                        env.get(xdg_data_dir_key) orelse "/usr/local/share:/usr/share",
                        data_dir,
                    ),
                );
            } else |err| {
                log.warn("error building {s}; err={}", .{ xdg_data_dir_key, err });
            }

            const manpath_key = "MANPATH";
            if (std.fmt.bufPrint(&buf, "{s}/../man", .{resources_dir})) |man_dir| {
                // Always append with colon in front, as it mean that if
                // `MANPATH` is empty, then it should be treated as an extra
                // path instead of overriding all paths set by OS.
                try env.put(
                    manpath_key,
                    try internal_os.appendEnvAlways(
                        alloc,
                        env.get(manpath_key) orelse "",
                        man_dir,
                    ),
                );
            } else |err| {
                log.warn("error building {s}; man pages may not be available; err={}", .{ manpath_key, err });
            }
        }

        // Set environment variables used by some programs (such as neovim) to detect
        // which terminal emulator and version they're running under.
        try env.put("TERM_PROGRAM", build_config.term_program);
        try env.put("TERM_PROGRAM_VERSION", build_config.version_string);

        // VTE_VERSION is set by gnome-terminal and other VTE-based terminals.
        // We don't want our child processes to think we're running under VTE.
        // This is not apprt-specific, so we do it here.
        env.remove("VTE_VERSION");

        // Setup our shell integration, if we can.
        const shell_command: configpkg.Command = shell: {
            const default_shell_command: configpkg.Command =
                cfg.command orelse .{ .shell = switch (builtin.os.tag) {
                    .windows => "cmd.exe",
                    else => "sh",
                } };

            // Always set up shell features (GHOSTTY_SHELL_FEATURES). These are
            // used by both automatic and manual shell integrations.
            try shell_integration.setupFeatures(
                &env,
                cfg.shell_integration_features,
                cfg.cursor_blink orelse true,
            );

            const force: ?shell_integration.Shell = switch (cfg.shell_integration) {
                .none => {
                    // This is a source of confusion for users despite being
                    // opt-in since it results in some Ghostty features not
                    // working. We always want to log it.
                    log.info("shell integration disabled by configuration", .{});
                    break :shell default_shell_command;
                },

                .detect => null,
                .bash => .bash,
                .cmd => .cmd,
                .elvish => .elvish,
                .fish => .fish,
                .nushell => .nushell,
                .powershell => .powershell,
                .zsh => .zsh,
            };

            const dir = cfg.resources_dir orelse {
                log.warn("no resources dir set, shell integration disabled", .{});
                break :shell default_shell_command;
            };

            // On Windows, a `wsl.exe` launch can't be integrated through
            // detectShell (it sees the launcher, not the distro's shell).
            // Inject zsh/fish integration into the distro via WSLENV instead.
            // Login bash is unreachable (documented no-op).
            if (comptime builtin.os.tag == .windows) wsl: {
                var it = default_shell_command.argIterator(alloc) catch break :wsl;
                defer it.deinit();
                const arg0 = it.next() orelse break :wsl;
                if (!internal_os.windows_shell.isWslExe(arg0)) break :wsl;
                // WSL integration is best-effort, so unlike the native
                // shell_integration.setup below (which `try`s and treats OOM as
                // fatal) we catch and downgrade to a warning: a failed WSL
                // injection should never abort the spawn.
                wsl_shell_integration.setup(alloc, dir, &env) catch |err| {
                    log.warn("WSL shell integration failed: {}", .{err});
                };
                log.info("WSL shell integration injected via WSLENV", .{});
                break :shell default_shell_command;
            }

            const integration = try shell_integration.setup(
                alloc,
                dir,
                default_shell_command,
                &env,
                force,
            ) orelse {
                log.warn("shell could not be detected, no automatic shell integration will be injected", .{});
                break :shell default_shell_command;
            };

            log.info(
                "shell integration automatically injected shell={}",
                .{integration.shell},
            );

            break :shell integration.command;
        };

        // Add the environment variables that override any others.
        {
            var it = cfg.env_override.iterator();
            while (it.next()) |entry| try env.put(
                entry.key_ptr.*,
                entry.value_ptr.*,
            );
        }

        // Build our args list. cfg.utf8_console is `void` on
        // non-Windows; pass `.auto` as a no-op concrete value so the
        // shape of the call site stays cross-platform (execCommand's
        // body reads utf8_console only inside Windows-gated blocks).
        const args: []const [:0]const u8 = execCommand(
            alloc,
            shell_command,
            internal_os.passwd,
            if (comptime builtin.os.tag == .windows)
                cfg.utf8_console
            else
                .auto,
        ) catch |err| switch (err) {
            // If we fail to allocate space for the command we want to
            // execute, we'd still like to try to run something so
            // Ghostty can launch (and maybe the user can debug this further).
            // Realistically, if you're getting OOM, I think other stuff is
            // about to crash, but we can try.
            error.OutOfMemory => oom: {
                log.warn("failed to allocate space for command args, falling back to basic shell", .{});

                // The comptime here is important to ensure the full slice
                // is put into the binary data and not the stack.
                break :oom comptime switch (builtin.os.tag) {
                    .windows => &.{"cmd.exe"},
                    else => &.{"/bin/sh"},
                };
            },

            // This logs on its own, this is a bad error.
            error.SystemError => return err,
        };

        // We have to copy the cwd because there is no guarantee that
        // pointers in full_config remain valid.
        const cwd: ?[:0]u8 = if (cfg.working_directory) |cwd|
            try alloc.dupeZ(u8, cwd)
        else
            null;

        // Propagate the current working directory (CWD) to the shell, enabling
        // the shell to display the current directory name rather than the
        // resolved path for symbolic links. This is important and based
        // on the same behavior in Konsole and Kitty (see the linked issues):
        // https://bugs.kde.org/show_bug.cgi?id=242114
        // https://github.com/kovidgoyal/kitty/issues/1595
        // https://github.com/ghostty-org/ghostty/discussions/7769
        if (cwd) |pwd| try env.put("PWD", pwd);

        return .{
            .arena = arena,
            .env = env,
            .cwd = cwd,
            .args = args,

            .utf8_console = cfg.utf8_console,

            .rt_pre_exec_info = cfg.rt_pre_exec_info,
            .rt_post_fork_info = cfg.rt_post_fork_info,

            // Should be initialized with initTerminal call.
            .grid_size = .{},
            .screen_size = .{ .width = 1, .height = 1 },
        };
    }

    /// Clean up the subprocess. This will stop the subprocess if it is started.
    pub fn deinit(self: *Subprocess) void {
        self.stop();
        if (self.pty) |*pty| pty.deinit();
        if (self.env) |*env| env.deinit();
        self.arena.deinit();
        self.* = undefined;
    }

    /// Start the subprocess. If the subprocess is already started this
    /// will crash.
    pub fn start(self: *Subprocess, alloc: Allocator) !struct {
        read: Pty.Fd,
        write: Pty.Fd,
    } {
        assert(self.pty == null and self.process == null);

        // Windows: provision Ghostty's xterm-ghostty terminfo into a target
        // WSL distro before we spawn the shell, so the first session resolves
        // it. Best-effort and self-gating to `wsl` commands; never fails the
        // spawn. Runs here (the IO-thread spawn site) rather than at argv-build
        // time, which is on the UI thread.
        if (comptime builtin.os.tag == .windows) {
            maybeProvisionWslTerminfo(alloc, self.args);
        }

        // This function is funny because on POSIX systems it can
        // fail in the forked process. This is flipped to true if
        // we're in an error state in the forked process (child
        // process).
        var in_child: bool = false;

        // ConPTY is the sole Windows transport; POSIX ignores opts.mode.
        const mode: ptypkg.Mode = .conpty;

        // Create our pty
        var pty = try Pty.open(.{
            .size = .{
                .ws_row = @intCast(self.grid_size.rows),
                .ws_col = @intCast(self.grid_size.columns),
                .ws_xpixel = @intCast(self.screen_size.width),
                .ws_ypixel = @intCast(self.screen_size.height),
            },
            .mode = mode,
        });
        self.pty = pty;
        errdefer if (!in_child) {
            if (comptime builtin.os.tag != .windows) {
                _ = posix.close(pty.slave);
            }

            pty.deinit();
            self.pty = null;
        };

        // Cleanup we only run in our parent when we successfully start
        // the process.
        defer if (!in_child and self.process != null) {
            if (comptime builtin.os.tag != .windows) {
                // Once our subcommand is started we can close the slave
                // side. This prevents the slave fd from being leaked to
                // future children.
                _ = posix.close(pty.slave);
            }
            // On Windows ConPTY owns the pipe pair internally; we keep
            // those handles alive until `Pty.deinit` and do nothing here.

            // Successful start we can clear out some memory.
            if (self.env) |*env| {
                env.deinit();
                self.env = null;
            }
        };

        log.debug("starting command command={f}", .{ArgsFormatter{ .args = self.args }});

        // If we can't access the cwd, then don't set any cwd and inherit.
        // This is important because our cwd can be set by the shell (OSC 7)
        // and we don't want to break new windows.
        const cwd: ?[:0]const u8 = if (self.cwd) |proposed| cwd: {
            if ((comptime build_config.flatpak) and internal_os.isFlatpak()) {
                // Flatpak sandboxing prevents access to certain reserved paths
                // regardless of configured permissions. Perform a test spawn
                // to get around this problem
                //
                // https://docs.flatpak.org/en/latest/sandbox-permissions.html#reserved-paths
                log.info("flatpak detected, will use host command to verify cwd access", .{});
                const dev_null = try std.fs.cwd().openFile("/dev/null", .{ .mode = .read_write });
                defer dev_null.close();
                var cmd: internal_os.FlatpakHostCommand = .{
                    .argv = &[_][]const u8{
                        "/bin/sh",
                        "-c",
                        ":",
                    },
                    .cwd = proposed,
                    .stdin = dev_null.handle,
                    .stdout = dev_null.handle,
                    .stderr = dev_null.handle,
                };
                _ = cmd.spawn(alloc) catch |err| {
                    log.warn("cannot spawn command at cwd, ignoring: {}", .{err});
                    break :cwd null;
                };
                _ = try cmd.wait();

                break :cwd proposed;
            }

            if (std.fs.cwd().access(proposed, .{})) {
                break :cwd proposed;
            } else |err| {
                log.warn("cannot access cwd, ignoring: {}", .{err});
                break :cwd null;
            }
        } else null;

        // In flatpak, we use the HostCommand to execute our shell.
        if (internal_os.isFlatpak()) flatpak: {
            if (comptime !build_config.flatpak) {
                log.warn("flatpak detected, but flatpak support not built-in", .{});
                break :flatpak;
            }

            // Flatpak command must have a stable pointer.
            self.process = .{ .flatpak = .{
                .argv = self.args,
                .cwd = cwd,
                .env = if (self.env) |*env| env else null,
                .stdin = pty.slave,
                .stdout = pty.slave,
                .stderr = pty.slave,
            } };
            var cmd = &self.process.?.flatpak;
            const pid = try cmd.spawn(alloc);
            errdefer killCommandFlatpak(cmd);

            log.info("started subcommand on host via flatpak API path={s} pid={}", .{
                self.args[0],
                pid,
            });

            return .{
                .read = pty.master,
                .write = pty.master,
            };
        }

        // Build our subcommand. On Windows ConPTY owns stdio via the
        // pseudoconsole handle, so the std handles passed to Command are
        // null and the pseudoconsole is attached instead.
        var cmd: Command = .{
            .path = self.args[0],
            .args = self.args,
            .env = if (self.env) |*env| env else null,
            .cwd = cwd,
            .stdin = if (comptime builtin.os.tag == .windows)
                null
            else
                .{ .handle = pty.slave },
            .stdout = if (comptime builtin.os.tag == .windows)
                null
            else
                .{ .handle = pty.slave },
            .stderr = if (comptime builtin.os.tag == .windows)
                null
            else
                .{ .handle = pty.slave },
            .pseudo_console = if (comptime builtin.os.tag == .windows)
                pty.pseudo_console
            else {},
            .os_pre_exec = switch (comptime builtin.os.tag) {
                .windows => null,
                else => f: {
                    const f = struct {
                        fn callback(cmd: *Command) ?u8 {
                            const sp = cmd.getData(Subprocess) orelse unreachable;
                            sp.childPreExec() catch |err| log.err(
                                "error initializing child: {}",
                                .{err},
                            );
                            return null;
                        }
                    };
                    break :f f.callback;
                },
            },
            .rt_pre_exec = if (comptime @hasDecl(apprt.runtime, "pre_exec")) apprt.runtime.pre_exec.preExec else null,
            .rt_pre_exec_info = self.rt_pre_exec_info,
            .rt_post_fork = if (comptime @hasDecl(apprt.runtime, "post_fork")) apprt.runtime.post_fork.postFork else null,
            .rt_post_fork_info = self.rt_post_fork_info,
            .data = self,
        };

        cmd.start(alloc) catch |err| {
            // We have to do this because start on Windows can't
            // ever return ExecFailedInChild
            const StartError = error{ExecFailedInChild} || @TypeOf(err);
            switch (@as(StartError, err)) {
                // If we fail in our child we need to flag it so our
                // errdefers don't run.
                error.ExecFailedInChild => {
                    in_child = true;
                    return err;
                },

                else => return err,
            }
        };
        errdefer killCommand(&cmd) catch |err| {
            log.warn("error killing command during cleanup err={}", .{err});
        };
        log.info("started subcommand path={s} pid={?}", .{ self.args[0], cmd.pid });

        self.process = .{ .fork_exec = cmd };
        return switch (builtin.os.tag) {
            .windows => .{
                .read = pty.out_pipe,
                .write = pty.in_pipe,
            },

            else => .{
                .read = pty.master,
                .write = pty.master,
            },
        };
    }

    /// This should be called after fork but before exec in the child process.
    /// To repeat: this function RUNS IN THE FORKED CHILD PROCESS before
    /// exec is called; it does NOT run in the main Ghostty process.
    fn childPreExec(self: *Subprocess) !void {
        // Setup our pty
        try self.pty.?.childPreExec();
    }

    /// Called to notify that we exited externally so we can unset our
    /// running state.
    pub fn externalExit(self: *Subprocess) void {
        self.process = null;
    }

    /// Stop the subprocess. This is safe to call anytime. This will wait
    /// for the subprocess to register that it has been signalled, but not
    /// for it to terminate, so it will not block.
    /// This does not close the pty.
    pub fn stop(self: *Subprocess) void {
        switch (self.process orelse return) {
            .fork_exec => |*cmd| {
                // Note: this will also wait for the command to exit, so
                // DO NOT call cmd.wait
                killCommand(cmd) catch |err|
                    log.err("error sending SIGHUP to command, may hang: {}", .{err});
            },

            .flatpak => |*cmd| if (comptime build_config.flatpak) {
                killCommandFlatpak(cmd) catch |err|
                    log.err("error sending SIGHUP to command, may hang: {}", .{err});
                _ = cmd.wait() catch |err|
                    log.err("error waiting for command to exit: {}", .{err});
            },
        }

        self.process = null;
    }

    /// Resize the pty subprocess. This is safe to call anytime.
    pub fn resize(
        self: *Subprocess,
        grid_size: renderer.GridSize,
        screen_size: renderer.ScreenSize,
    ) !void {
        self.grid_size = grid_size;
        self.screen_size = screen_size;

        if (self.pty) |*pty| {
            // It is theoretically possible for the grid or screen size to
            // exceed u16, although the terminal in that case isn't very
            // usable. This should be protected upstream but we still clamp
            // in case there is a bad caller which has happened before.
            try pty.setSize(.{
                .ws_row = std.math.cast(u16, grid_size.rows) orelse std.math.maxInt(u16),
                .ws_col = std.math.cast(u16, grid_size.columns) orelse std.math.maxInt(u16),
                .ws_xpixel = std.math.cast(u16, screen_size.width) orelse std.math.maxInt(u16),
                .ws_ypixel = std.math.cast(u16, screen_size.height) orelse std.math.maxInt(u16),
            });
        }
    }

    /// Kill the underlying subprocess. This sends a SIGHUP to the child
    /// process. This also waits for the command to exit and will return the
    /// exit code.
    fn killCommand(command: *Command) !void {
        if (command.pid) |pid| {
            switch (builtin.os.tag) {
                .windows => {
                    if (windows.kernel32.TerminateProcess(pid, 0) == 0) {
                        return windows.unexpectedError(windows.kernel32.GetLastError());
                    }

                    _ = try command.wait(false);
                },

                else => try killPid(pid),
            }
        }
    }

    fn killPid(pid: c.pid_t) !void {
        const pgid = getpgid(pid) orelse return;

        // It is possible to send a killpg between the time that
        // our child process calls setsid but before or simultaneous
        // to calling execve. In this case, the direct child dies
        // but grandchildren survive. To work around this, we loop
        // and repeatedly kill the process group until all
        // descendents are well and truly dead. We will not rest
        // until the entire family tree is obliterated.
        while (true) {
            switch (posix.errno(c.killpg(pgid, c.SIGHUP))) {
                .SUCCESS => log.debug("process group killed pgid={}", .{pgid}),
                else => |err| killpg: {
                    if ((comptime builtin.target.os.tag.isDarwin()) and
                        err == .PERM)
                    {
                        log.debug("killpg failed with EPERM, expected on Darwin and ignoring", .{});
                        break :killpg;
                    }

                    log.warn("error killing process group pgid={} err={}", .{ pgid, err });
                    return error.KillFailed;
                },
            }

            // See Command.zig wait for why we specify WNOHANG.
            // The gist is that it lets us detect when children
            // are still alive without blocking so that we can
            // kill them again.
            const res = posix.waitpid(pid, std.c.W.NOHANG);
            log.debug("waitpid result={}", .{res.pid});
            if (res.pid != 0) break;
            std.Thread.sleep(10 * std.time.ns_per_ms);
        }
    }

    fn getpgid(pid: c.pid_t) ?c.pid_t {
        // Get our process group ID. Before the child pid calls setsid
        // the pgid will be ours because we forked it. Its possible that
        // we may be calling this before setsid if we are killing a surface
        // VERY quickly after starting it.
        const my_pgid = c.getpgid(0);

        // We loop while pgid == my_pgid. The expectation if we have a valid
        // pid is that setsid will eventually be called because it is the
        // FIRST thing the child process does and as far as I can tell,
        // setsid cannot fail. I'm sure that's not true, but I'd rather
        // have a bug reported than defensively program against it now.
        while (true) {
            const pgid = c.getpgid(pid);
            if (pgid == my_pgid) {
                log.warn("pgid is our own, retrying", .{});
                std.Thread.sleep(10 * std.time.ns_per_ms);
                continue;
            }

            // Don't know why it would be zero but its not a valid pid
            if (pgid == 0) return null;

            // If the pid doesn't exist then... we're done!
            if (pgid == c.ESRCH) return null;

            // If we have an error we're done.
            if (pgid < 0) {
                log.warn("error getting pgid for kill", .{});
                return null;
            }

            return pgid;
        }
    }

    /// Kill the underlying process started via Flatpak host command.
    /// This sends a signal via the Flatpak API.
    fn killCommandFlatpak(command: *FlatpakHostCommand) !void {
        try command.signal(c.SIGHUP, true);
    }

    /// Get information about the process(es) running within the subprocess.
    /// Returns `null` if there was an error getting the information or the
    /// information is not available on a particular platform.
    pub fn getProcessInfo(self: *Subprocess, comptime info: ProcessInfo) ?ProcessInfo.Type(info) {
        // On Windows the pty layer does not (and cannot) track a foreground
        // process group the way POSIX does. The semantically-useful PID for
        // our callers (the apprt active-process tracker / tab-icon picker)
        // is the spawned shell itself; descendants are walked client-side
        // via Toolhelp32. Resolve that here from the Command HANDLE rather
        // than going through Pty, which returns null on Windows.
        if (comptime builtin.os.tag == .windows) {
            if (info == .foreground_pid) {
                // Local capture intentionally named differently from the
                // file-level `c = @cImport(...)` to avoid a shadow error
                // under Zig 0.15.
                const cmd = switch (self.process orelse return null) {
                    .fork_exec => |*sub| sub,
                    // Flatpak path is POSIX-only; unreachable on Windows
                    // but keep the switch exhaustive.
                    .flatpak => return null,
                };
                const handle = cmd.pid orelse return null;
                const pid = windows.exp.kernel32.GetProcessId(handle);
                if (pid == 0) return null;
                return @intCast(pid);
            }
        }

        const pty = &(self.pty orelse return null);
        return pty.getProcessInfo(info);
    }
};

/// Context passed to winProcessWaitThread. Heap-allocated and owned by the
/// thread; the thread frees it before returning.
const WinProcessWaitCtx = struct {
    /// Duplicated process HANDLE. The thread owns this copy and must close it.
    handle: windows.HANDLE,
    /// Pointer to the io thread's ThreadData. Valid for the lifetime of the
    /// io thread (see threadExit which joins process_wait_thread before td
    /// is freed).
    td: *termio.Termio.ThreadData,
};

/// Dedicated Windows process-exit watcher. Blocks on WaitForSingleObject and
/// calls processExitCommon when the child exits. Used instead of xev.Process
/// on Windows because xev.Process uses CreateJobObject + AssignProcessToJobObject,
/// which silently no-ops when the child is already in ConPTY's job object.
fn winProcessWaitThread(alloc: Allocator, ctx: *WinProcessWaitCtx) void {
    defer alloc.destroy(ctx);
    defer windows.CloseHandle(ctx.handle);

    switch (windows.kernel32.WaitForSingleObject(ctx.handle, windows.INFINITE)) {
        windows.WAIT_OBJECT_0 => {},
        windows.WAIT_FAILED => {
            log.err("proc-wait: WaitForSingleObject failed err={}", .{windows.kernel32.GetLastError()});
            return;
        },
        else => |r| {
            log.err("proc-wait: WaitForSingleObject unexpected result={x}", .{r});
            return;
        },
    }

    var exit_code: windows.DWORD = 1;
    if (windows.kernel32.GetExitCodeProcess(ctx.handle, &exit_code) == 0) {
        log.err("proc-wait: GetExitCodeProcess failed err={}", .{windows.kernel32.GetLastError()});
        // processExitCommon with a best-effort code of 1
    }

    log.debug("proc-wait: child exited code={}", .{exit_code});
    processExitCommon(ctx.td, exit_code);
}

/// The read thread works with a companion gather thread to form a two-stage
/// pipeline that moves pty output into the terminal:
///
///   io-gather:  read()/poll() the pty into one of a few rotating
///               buffers, batching bulk output.
///   io-reader:  hand each filled buffer to processOutput (terminal
///               lock, VT parse, state update, render scheduling).
///
/// This used to be a single serial loop (and still is on Windows):
///
///   while (true) { blocking_read(); exit_if_eof(); process(); }
///
/// I found on macOS that the kernel tty output queue caps every read
/// on the master at 1 KB no matter how large the read buffer is. This means
/// that producers (e.g. `cat`) stall with the above architecture because
/// there are windows in the `process()` part where we aren't draining
/// the kernel pty fd.
///
/// Instead, having a separate thread gather and drain the kernel pty
/// into a rotating set of preallocated buffers minimizes this stall
/// period to effectively zero: while the io-reader thread parses
/// one batch, the gather thread is draining the kernel queue. There is
/// still stalling (our VT parse is a bottleneck now), but we don't stall
/// between them.
///
/// Interactive latency is preserved: a batch is delivered on the
/// first EAGAIN unless the stream is saturated (>= 1 KiB gathered
/// means the writer filled the kernel queue), in which case we bridge
/// the producer's microsecond refill gaps with a short poll, bounded
/// by a small total budget per batch that is well under a display
/// frame. This means that small outputs that are more typical continue
/// to be interactive.
///
/// We use basic poll/read syscalls here because we are only
/// monitoring two fds and this is still much faster and lower
/// overhead than any async mechanism.
pub const ReadThread = struct {
    /// The number of buffers rotated between the gather and parse
    /// stages. The gather stage can run at most this many batches
    /// ahead of the parse stage before it blocks, which (via the
    /// kernel pty queue) is also what preserves flow control to the
    /// child. Empirically chosen through measurements on an M4 Max.
    /// Less than 4 there are minor slowdowns, above 4 there are no
    /// improvements.
    const buffer_count = 4;

    /// The capacity of each gather buffer. One batch is also the unit
    /// of work the parse stage does per terminal lock acquisition, so
    /// this bounds both gather latency and lock hold time.
    const buffer_capacity = 64 * 1024;

    /// Read buffer size for the Windows serial loop
    /// (threadMainWindows). ConPTY has no analogue of the ~1 KiB pty
    /// read cap on macOS/Linux: measured against both the in-box
    /// conhost and the bundled OpenConsole pair, a read this size
    /// returns 46-65 KiB per call under bulk output, so a 1 KiB
    /// buffer only multiplied ReadFile calls, terminal lock
    /// acquisitions, and render wakeups 45-63x for the same stream.
    /// Throughput doesn't change either way; ConPTY's own VT
    /// rendering caps it well below what either buffer size can
    /// drain.
    ///
    /// Deliberately equal to buffer_capacity so one batch bounds
    /// terminal lock hold time the same on every platform. If the
    /// posix pipeline ever retunes that const for gather-specific
    /// reasons, revisit this value independently.
    const windows_read_capacity = buffer_capacity;

    /// How many gathered bytes mark a stream as saturated. The macOS
    /// kernel tty output queue hands the master at most about 1 KiB
    /// per read, so gathering a full 1 KiB means the writer filled
    /// the queue (a bulk stream worth briefly waiting on), while
    /// anything smaller is an interactive trickle that must be
    /// delivered with no added latency.
    const bridge_threshold = 1024;

    /// How many times an EAGAIN on a saturated stream is retried
    /// with an immediate read before we're willing to sleep in poll.
    /// Basically, a spin retry.
    ///
    /// The writer refills the drained kernel queue within a few
    /// microseconds, while a sleep and wakeup through poll costs
    /// several more. If the gather stage sleeps on every refill gap,
    /// the whole pipeline degenerates to lockstep with the writer at
    /// about 1 KiB per wakeup. A short burst of nonblocking reads
    /// bridges nearly all refill gaps without sleeping. Measured, 8
    /// to 16 retries catches over 90% of the gaps and nearly doubles
    /// the saturated drain rate, and larger values helped little.
    /// The cost is bounded to at most this many extra ~0.5us read
    /// syscalls per gap, and we only spin on streams that already
    /// gathered a full kernel queue, so an idle or interactive
    /// terminal never spins.
    const bridge_spin_max = 16;

    /// How long one bridge poll waits for the writer's next refill
    /// once the spin retries above have failed. If the stream is
    /// quiet for this long the burst is over and we deliver what we
    /// have.
    const bridge_poll_timeout_ms = 1;

    /// The longest one batch may spend bridging refill gaps before it
    /// is delivered regardless. This bounds output latency for
    /// streams that produce just enough to keep bridging. Three
    /// milliseconds is well under one display frame, so batching is
    /// invisible on screen.
    const gather_budget_ns = 3 * std.time.ns_per_ms;

    /// The state shared between the gather and parse stages. This is
    /// a fixed ring of buffers plus the metadata to rotate ownership
    /// between the two threads. A buffer is owned by exactly one
    /// stage at a time, so buffer contents need no locking. Only the
    /// ring metadata is guarded by the mutex.
    const Pipeline = struct {
        mutex: std.Thread.Mutex = .{},

        /// Signaled when a batch is published or the gather stage is
        /// done. Waited on by the parse stage.
        batch_ready: std.Thread.Condition = .{},

        /// Signaled when a batch has been consumed. Waited on by the
        /// gather stage when all buffers are in flight (backpressure).
        slot_free: std.Thread.Condition = .{},

        /// The number of valid bytes in each buffer. Set at publish
        /// time by the gather stage, read by the parse stage.
        lens: [buffer_count]usize = @splat(0),

        /// Ring state: head is the next slot the gather stage fills,
        /// tail is the next slot the parse stage consumes, count is
        /// the number of published, unconsumed batches.
        head: usize = 0,
        tail: usize = 0,
        count: usize = 0,

        /// Set by the gather stage (under the mutex) while it sleeps
        /// in a bridge poll. The parse stage only writes to the idle
        /// pipe when this is set, so an interactive terminal never
        /// pays the pipe syscalls.
        bridging: bool = false,

        /// A self-pipe the parse stage uses to interrupt the gather
        /// stage's bridge poll the moment it runs out of batches.
        /// Bridging a refill gap is only free while the parse stage
        /// is busy; once it goes idle, every additional microsecond
        /// spent bridging is added straight to output latency. The
        /// write end is used by the parse stage, the read end is
        /// polled by the gather stage. -1 when unavailable, in which
        /// case bridge polls are bounded by their timeout only.
        idle_read_fd: posix.fd_t = -1,
        idle_write_fd: posix.fd_t = -1,

        /// Set by the gather stage when the stream is over (quit
        /// signal, EOF, or pty error). The parse stage drains any
        /// remaining batches and then exits.
        done: bool = false,

        /// The buffer storage itself.
        bufs: [buffer_count][buffer_capacity]u8 = undefined,
    };

    fn threadMainPosix(fd: posix.fd_t, io: *termio.Termio, quit: posix.fd_t) void {
        // Always close our end of the pipe when we exit.
        defer posix.close(quit);

        // Right now, on Darwin, `std.Thread.setName` can only name the current
        // thread, and we have no way to get the current thread from within it,
        // so instead we use this code to name the thread instead.
        if (builtin.os.tag.isDarwin()) {
            internal_os.macos.pthread_setname_np(&"io-reader".*);
            setQosClass();
        }

        // Setup our crash metadata
        crash.sentry.thread_state = .{
            .type = .io,
            .surface = io.surface_mailbox.surface,
        };
        defer crash.sentry.thread_state = null;

        // Set the fd to non-blocking so the gather stage can drain it
        // in a tight loop and fall back to poll for readiness. The
        // pipeline can't run with a blocking fd (a blocking read
        // would hang the gather stage on a quiet pty), but this also
        // can't realistically fail on a valid pty master.
        if (!setNonblock(fd)) {
            log.err("read thread exiting, pty fd must be non-blocking", .{});
            return;
        }

        // Shared pipeline
        var pipeline: Pipeline = .{};

        // The idle self-pipe (see the Pipeline field docs). If we
        // can't create it we still run correctly, bridge polls are
        // just bounded by their timeout instead of being interrupted
        // when the parse stage goes idle.
        if (posix.pipe2(.{
            .CLOEXEC = true,
            .NONBLOCK = true,
        })) |fds| {
            pipeline.idle_read_fd = fds[0];
            pipeline.idle_write_fd = fds[1];
        } else |err| {
            log.warn("read thread failed to create idle pipe err={}", .{err});
        }
        defer if (pipeline.idle_read_fd >= 0) {
            posix.close(pipeline.idle_read_fd);
            posix.close(pipeline.idle_write_fd);
        };

        const gather_thread = std.Thread.spawn(
            .{},
            gatherMainPosix,
            .{ fd, quit, &pipeline },
        ) catch |err| {
            // If we can't spawn a thread the process is already
            // doomed (every surface spawns several), so don't try
            // to limp along.
            log.err("read thread exiting, failed to spawn gather thread err={}", .{err});
            return;
        };
        defer gather_thread.join();
        if (comptime !builtin.os.tag.isDarwin()) {
            gather_thread.setName("io-gather") catch {};
        }

        // This thread is the parse stage. We consume batches in ring
        // order until the gather stage reports the stream is over and
        // the ring is drained.
        while (true) {
            const batch: []const u8 = batch: {
                pipeline.mutex.lock();
                defer pipeline.mutex.unlock();
                while (pipeline.count == 0) {
                    if (pipeline.done) return;
                    pipeline.batch_ready.wait(&pipeline.mutex);
                }
                const slot = pipeline.tail;
                break :batch pipeline.bufs[slot][0..pipeline.lens[slot]];
            };

            // The batch buffer is owned by this stage until we advance
            // the tail below, so it is safe to read outside the lock.
            io.processOutput(batch);

            {
                pipeline.mutex.lock();
                pipeline.tail = (pipeline.tail + 1) % buffer_count;
                pipeline.count -= 1;
                const wake = pipeline.count == 0 and
                    pipeline.bridging and
                    pipeline.idle_write_fd >= 0;
                pipeline.mutex.unlock();
                pipeline.slot_free.signal();

                // We ran out of batches while the gather stage is
                // bridging a refill gap: interrupt its poll so it
                // delivers what it has instead of sleeping out the
                // timeout while we sit idle.
                if (wake) {
                    _ = posix.write(pipeline.idle_write_fd, "i") catch {};
                }
            }

            // Batch boundary: hand the renderer state mutex off if
            // the renderer is waiting. See renderer.State.lockDemand.
            io.renderer_state.yieldToDemand();
        }
    }

    /// The gather stage. This drains the pty into rotating buffers,
    /// bridging the kernel queue's refill gaps for saturated streams,
    /// and publishes each batch to the parse stage. This thread owns
    /// all fd monitoring, including the quit fd.
    fn gatherMainPosix(fd: posix.fd_t, quit: posix.fd_t, pipeline: *Pipeline) void {
        if (builtin.os.tag.isDarwin()) {
            internal_os.macos.pthread_setname_np(&"io-gather".*);
            setQosClass();
        }

        // However we exit, tell the parse stage the stream is over so
        // it drains the ring and joins us.
        defer {
            pipeline.mutex.lock();
            pipeline.done = true;
            pipeline.mutex.unlock();
            pipeline.batch_ready.signal();
        }

        // The fds we poll: data on the pty, our quit notification,
        // and the parse stage's idle wake. The idle fd only
        // participates in bridge polls (the parse stage only writes
        // while we're bridging), so the outer poll slices it off.
        var pollfds: [3]posix.pollfd = .{
            .{ .fd = fd, .events = posix.POLL.IN, .revents = undefined },
            .{ .fd = quit, .events = posix.POLL.IN, .revents = undefined },
            .{ .fd = pipeline.idle_read_fd, .events = posix.POLL.IN, .revents = undefined },
        };

        while (true) {
            // Claim the next free buffer. This blocks only when the
            // parse stage is a full ring behind, which is exactly when
            // we should stop reading and let the kernel queue exert
            // backpressure on the child.
            const buf: *[buffer_capacity]u8 = buf: {
                pipeline.mutex.lock();
                defer pipeline.mutex.unlock();
                while (pipeline.count == buffer_count) {
                    pipeline.slot_free.wait(&pipeline.mutex);
                }
                break :buf &pipeline.bufs[pipeline.head];
            };

            var total: usize = 0;
            var bridge_start: ?std.time.Instant = null;
            var spins: usize = 0;
            var fatal = false;

            // Fill the buffer from the pty. For a saturated stream the
            // kernel queue momentarily runs dry while the writer
            // refills it in parallel, so we bridge those gaps with
            // spin retries and a short poll instead of delivering a
            // tiny batch.
            gather: while (total < buffer_capacity) {
                const n = posix.read(
                    fd,
                    buf[total..],
                ) catch |err| switch (err) {
                    error.WouldBlock => {
                        // Anything below the threshold is interactive.
                        if (total < bridge_threshold) break :gather;

                        // The stream is saturated, so we bridge the
                        // gap. First retry the read directly a bounded
                        // number of times, since the refill usually
                        // lands within microseconds.
                        if (spins < bridge_spin_max) {
                            spins += 1;
                            continue :gather;
                        }

                        // Still dry, so we want to sleep in poll for
                        // the next refill, within our latency budget.
                        const now = std.time.Instant.now() catch
                            break :gather;
                        if (bridge_start) |start| {
                            if (now.since(start) >= gather_budget_ns)
                                break :gather;
                        } else bridge_start = now;

                        // Bridging a refill gap is only free while the
                        // parse stage is busy, since the wait hides
                        // behind parse time. Once the parser is idle,
                        // every microsecond we hold this batch is
                        // added straight to output latency, and for a
                        // request/response producer (a burst ending
                        // in a query, e.g. frame + cursor position
                        // report) the writer is blocked on a reply to
                        // data sitting in this buffer, so a poll here
                        // would always sleep its full timeout. Deliver
                        // now if the parser is idle, and otherwise arm
                        // the idle wake so it can interrupt our poll
                        // the moment that changes.
                        {
                            pipeline.mutex.lock();
                            defer pipeline.mutex.unlock();
                            if (pipeline.count == 0) break :gather;
                            pipeline.bridging = true;
                        }

                        const r = posix.poll(
                            &pollfds,
                            bridge_poll_timeout_ms,
                        ) catch |poll_err| {
                            clearBridging(pipeline);
                            log.warn("bridge poll failed err={}", .{poll_err});
                            break :gather;
                        };
                        clearBridging(pipeline);

                        // Quiet for a full timeout means the burst
                        // ended.
                        if (r == 0) break :gather;

                        // On a quit signal we deliver what we have
                        // and stop.
                        if (pollfds[1].revents & posix.POLL.IN != 0) {
                            log.info("read thread got quit signal", .{});
                            fatal = true;
                            break :gather;
                        }

                        // The parse stage went idle: drain the wake
                        // and deliver what we have. The pty may have
                        // data as well, but the next batch can pick
                        // that up; an idle parser means delivery must
                        // not wait.
                        if (pollfds[2].revents & posix.POLL.IN != 0) {
                            var trash: [16]u8 = undefined;
                            while (true) {
                                const drained = posix.read(
                                    pipeline.idle_read_fd,
                                    &trash,
                                ) catch break;
                                if (drained < trash.len) break;
                            }
                            break :gather;
                        }

                        // HUP without IN means no more data is
                        // coming. Deliver and let the outer poll
                        // decide what to do.
                        if (pollfds[0].revents & posix.POLL.IN == 0)
                            break :gather;

                        continue :gather;
                    },

                    // The pty is closed. We're probably gracefully
                    // shutting down.
                    error.NotOpenForReading,
                    error.InputOutput,
                    => {
                        log.info("io gather exiting", .{});
                        fatal = true;
                        break :gather;
                    },

                    else => {
                        log.err("io gather error err={}", .{err});
                        unreachable;
                    },
                };

                // This happens on macOS instead of WouldBlock when the
                // child process dies. Deliver what we have and let the
                // outer poll detect HUP.
                if (n == 0) break :gather;

                total += n;

                // Each refill gap gets a fresh spin budget.
                spins = 0;
            }

            // Publish the batch (if any) to the parse stage and rotate
            // to the next buffer.
            if (total > 0) {
                pipeline.mutex.lock();
                pipeline.lens[pipeline.head] = total;
                pipeline.head = (pipeline.head + 1) % buffer_count;
                pipeline.count += 1;
                pipeline.mutex.unlock();
                pipeline.batch_ready.signal();
            }

            if (fatal) return;

            // A full buffer means the stream is still hot, so go
            // claim the next buffer without an intervening poll.
            if (total == buffer_capacity) continue;

            // Wait for data. The idle fd is sliced off: the parse
            // stage only writes to it while we're bridging.
            _ = posix.poll(pollfds[0..2], -1) catch |err| {
                log.warn("poll failed on read thread, exiting early err={}", .{err});
                return;
            };

            // If our quit fd is set, we're done.
            if (pollfds[1].revents & posix.POLL.IN != 0) {
                log.info("read thread got quit signal", .{});
                return;
            }

            // If our pty fd is closed, then we're also done with our
            // read thread.
            if (pollfds[0].revents & posix.POLL.HUP != 0) {
                log.info("pty fd closed, read thread exiting", .{});
                return;
            }
        }
    }

    /// Clears the bridging flag armed before a bridge poll, closing
    /// the window in which the parse stage writes idle wakes.
    fn clearBridging(pipeline: *Pipeline) void {
        pipeline.mutex.lock();
        defer pipeline.mutex.unlock();
        pipeline.bridging = false;
    }

    /// Sets the QoS class of the calling thread for the read pipeline
    /// (macOS only). Both pipeline threads feed content the user is
    /// actively watching, and at default QoS the scheduler may place
    /// them on efficiency cores with wakeup latencies that are large
    /// compared to the ~10us cadence of the pty producer/consumer
    /// dance. Measured on an M4 Max, this results in a 15% throughput
    /// difference (on the change, not 15% total).
    fn setQosClass() void {
        internal_os.macos.setQosClass(.user_initiated) catch |err| {
            log.warn("error setting QoS class err={}", .{err});
        };
    }

    /// Sets the fd to non-blocking mode. Returns false on failure.
    fn setNonblock(fd: posix.fd_t) bool {
        const flags = posix.fcntl(
            fd,
            posix.F.GETFL,
            0,
        ) catch |err| {
            log.warn("read thread failed to get flags err={}", .{err});
            return false;
        };

        _ = posix.fcntl(
            fd,
            posix.F.SETFL,
            flags | @as(u32, @bitCast(posix.O{ .NONBLOCK = true })),
        ) catch |err| {
            log.warn("read thread failed to set flags err={}", .{err});
            return false;
        };

        return true;
    }

    fn threadMainWindows(fd: posix.fd_t, io: *termio.Termio, quit: posix.fd_t) void {
        // Always close our end of the pipe when we exit.
        defer posix.close(quit);

        // Setup our crash metadata
        crash.sentry.thread_state = .{
            .type = .io,
            .surface = io.surface_mailbox.surface,
        };
        defer crash.sentry.thread_state = null;

        var buf: [windows_read_capacity]u8 = undefined;
        while (true) {
            while (true) {
                var n: windows.DWORD = 0;
                if (windows.kernel32.ReadFile(fd, &buf, buf.len, &n, null) == 0) {
                    const err = windows.kernel32.GetLastError();
                    switch (err) {
                        // CancelIoEx was called (threadExit signaling shutdown)
                        .OPERATION_ABORTED => break,

                        // All writers closed the write end of the pipe.
                        // The child has exited and the PTY output pipe is done.
                        .BROKEN_PIPE => {
                            log.info("io reader: pipe EOF (BROKEN_PIPE), child exited", .{});
                            return;
                        },

                        else => {
                            // Any other error is unexpected. Log it and return
                            // rather than hitting unreachable (which is UB in
                            // ReleaseFast and a panic in Debug).
                            log.err("io reader error err={}", .{err});
                            return;
                        },
                    }
                }

                if (n == 0) {
                    // ReadFile succeeded with zero bytes: all writers have closed.
                    log.info("io reader: zero-byte read, pipe EOF", .{});
                    return;
                }

                @call(.always_inline, termio.Termio.processOutput, .{ io, buf[0..n] });

                // See threadMainPosix: hand the renderer state mutex
                // off if the renderer is waiting, since this loop
                // would otherwise starve it under heavy output.
                io.renderer_state.yieldToDemand();
            }

            var quit_bytes: windows.DWORD = 0;
            if (windows.exp.kernel32.PeekNamedPipe(quit, null, 0, null, &quit_bytes, null) == 0) {
                const err = windows.kernel32.GetLastError();
                log.err("quit pipe reader error err={}", .{err});
                // Return rather than crash; the loop will clean up via defer.
                return;
            }

            if (quit_bytes > 0) {
                log.info("read thread got quit signal", .{});
                return;
            }
        }
    }
};

/// Builds the argv array for the process we should exec for the
/// configured command. This isn't as straightforward as it seems since
/// we deal with shell-wrapping, macOS login shells, etc.
///
/// The passwdpkg comptime argument is expected to have a single function
/// `get(Allocator)` that returns a passwd entry. This is used by macOS
/// to determine the username and home directory for the login shell.
/// It is unused on other platforms.
///
/// Memory ownership:
///
/// The allocator should be an arena, since the returned value may or
/// may not be allocated and args may or may not be allocated (or copied).
/// Pointers in the return value may point to pointers in the command
/// struct.
fn execCommand(
    alloc: Allocator,
    command: configpkg.Command,
    comptime passwdpkg: type,
    /// Configured UTF-8 console preamble policy; used only on Windows
    /// to gate UTF-8 preamble injection under ConPTY (the sole Windows
    /// transport). Ignored on other platforms. We keep the parameter
    /// concrete (rather than `void` off-Windows) so darwin/posix unit
    /// tests can keep passing `.auto` literally as a "don't care"
    /// argument; the function never reads it outside Windows-gated
    /// blocks.
    utf8_console: configpkg.Config.Utf8Console,
) (Allocator.Error || error{SystemError})![]const [:0]const u8 {
    // If we're on macOS, we have to use `login(1)` to get all of
    // the proper environment variables set, a login shell, and proper
    // hushlogin behavior.
    if (comptime builtin.target.os.tag.isDarwin()) darwin: {
        const passwd = passwdpkg.get(alloc) catch |err| {
            log.warn("failed to read passwd, not using a login shell err={}", .{err});
            break :darwin;
        };

        const username = passwd.name orelse {
            log.warn("failed to get username, not using a login shell", .{});
            break :darwin;
        };

        const hush = if (passwd.home) |home| hush: {
            var dir = std.fs.openDirAbsolute(home, .{}) catch |err| {
                log.warn(
                    "failed to open home dir, not checking for hushlogin err={}",
                    .{err},
                );
                break :hush false;
            };
            defer dir.close();

            break :hush if (dir.access(".hushlogin", .{})) true else |_| false;
        } else false;

        // If we made it this far we're going to start building
        // the actual command.
        var args: std.ArrayList([:0]const u8) = try .initCapacity(
            alloc,

            // This capacity is chosen based on what we'd need to
            // execute a shell command (very common). We can/will
            // grow if necessary for a longer command (uncommon).
            9,
        );
        defer args.deinit(alloc);

        // The reason for executing login this way is unclear. This
        // comment will attempt to explain but prepare for a truly
        // unhinged reality.
        //
        // The first major issue is that on macOS, a lot of users
        // put shell configurations in ~/.bash_profile instead of
        // ~/.bashrc (or equivalent for another shell). This file is only
        // loaded for a login shell so macOS users expect all their terminals
        // to be login shells. No other platform behaves this way and its
        // totally braindead but somehow the entire dev community on
        // macOS has cargo culted their way to this reality so we have to
        // do it...
        //
        // To get a login shell, you COULD just prepend argv0 with a `-`
        // but that doesn't fully work because `getlogin()` C API will
        // return the wrong value, SHELL won't be set, and various
        // other login behaviors that macOS users expect.
        //
        // The proper way is to use `login(1)`. But login(1) forces
        // the working directory to change to the home directory,
        // which we may not want. If we specify "-l" then we can avoid
        // this behavior but now the shell isn't a login shell.
        //
        // There is another issue: `login(1)` on macOS 14.3 and earlier
        // checked for ".hushlogin" in the working directory. This means
        // that if we specify "-l" then we won't get hushlogin honored
        // if its in the home directory (which is standard). To get
        // around this, we check for hushlogin ourselves and if present
        // specify the "-q" flag to login(1).
        //
        // So to get all the behaviors we want, we specify "-l" but
        // execute "bash" (which is built-in to macOS). We then use
        // the bash builtin "exec" to replace the process with a login
        // shell ("-l" on exec) with the command we really want.
        //
        // We use "bash" instead of other shells that ship with macOS
        // because as of macOS Sonoma, we found with a microbenchmark
        // that bash can `exec` into the desired command ~2x faster
        // than zsh.
        //
        // To figure out a lot of this logic I read the login.c
        // source code in the OSS distribution Apple provides for
        // macOS.
        //
        // Awesome.
        try args.append(alloc, "/usr/bin/login");
        if (hush) try args.append(alloc, "-q");
        try args.append(alloc, "-flp");
        try args.append(alloc, username);

        switch (command) {
            // Direct args can be passed directly to login, since
            // login uses execvp we don't need to worry about PATH
            // searching.
            .direct => |v| try args.appendSlice(alloc, v),

            .shell => |v| {
                // Use "exec" to replace the bash process with
                // our intended command so we don't have a parent
                // process hanging around.
                const cmd = try std.fmt.allocPrintSentinel(
                    alloc,
                    "exec -l {s}",
                    .{v},
                    0,
                );

                // We execute bash with "--noprofile --norc" so that it doesn't
                // load startup files so that (1) our shell integration doesn't
                // break and (2) user configuration doesn't mess this process
                // up.
                try args.append(alloc, "/bin/bash");
                try args.append(alloc, "--noprofile");
                try args.append(alloc, "--norc");
                try args.append(alloc, "-c");
                try args.append(alloc, cmd);
            },
        }

        return try args.toOwnedSlice(alloc);
    }

    return switch (command) {
        // We need to clone the command since there's no guarantee the config remains valid.
        .direct => |_| direct: {
            const cloned = (try command.clone(alloc)).direct;
            if (comptime builtin.os.tag == .windows) {
                const with_preamble = try maybeInjectUtf8Preamble(
                    alloc,
                    cloned,
                    utf8_console,
                );
                break :direct try maybeWrapGitBashWithWinpty(alloc, with_preamble);
            }
            break :direct cloned;
        },

        .shell => |v| shell: {
            if (comptime builtin.os.tag == .windows) {
                // On Windows we only fall back to `cmd.exe /C <cmd>` when
                // the command actually needs cmd.exe features (pipes,
                // redirects, chaining, env expansion). Otherwise we parse
                // the string into argv and spawn directly.
                //
                // Why: `cmd.exe /C <cmd>` does NOT exec-replace itself
                // with <cmd>, unlike `/bin/sh -c <cmd>` on POSIX where
                // sh often optimizes into an exec of the final command.
                // Wrapping therefore makes cmd.exe the spawned process
                // and the parent of the user's shell. That means
                // `command = pwsh.exe` ends up with args[0] = cmd.exe,
                // ConPTY attaches to cmd.exe, and any process
                // classification that looks at args[0] (e.g. the shell
                // identity used to pick a UTF-8 preamble) sees the wrapper
                // instead of the configured shell. Spawning directly
                // fixes all three.
                if (!windowsShellNeedsCmdWrapping(v)) windows_direct: {
                    var args: std.ArrayList([:0]const u8) = .empty;
                    errdefer args.deinit(alloc);

                    var iter = try std.process.ArgIteratorGeneral(.{}).init(
                        alloc,
                        v,
                    );
                    defer iter.deinit();

                    while (iter.next()) |arg| {
                        const copy = try alloc.dupeZ(u8, arg);
                        try args.append(alloc, copy);
                    }

                    // Parser produced nothing usable (empty or
                    // whitespace-only command): fall through to cmd.exe
                    // wrapping so the error surfaces from cmd.exe rather
                    // than from an empty argv here.
                    if (args.items.len == 0) {
                        args.deinit(alloc);
                        break :windows_direct;
                    }

                    // If the user explicitly configured shell = "cmd.exe"
                    // (the default) with no further args, resolve via
                    // %COMSPEC% which is the documented path to the
                    // current command processor. Other values are
                    // passed as-is and resolved by Command.startWindows.
                    if (args.items.len == 1 and
                        std.ascii.eqlIgnoreCase(args.items[0], "cmd.exe"))
                    {
                        if (std.process.getEnvVarOwned(alloc, "COMSPEC")) |comspec| {
                            args.items[0] = try alloc.dupeZ(u8, comspec);
                        } else |_| {}
                    }

                    const direct_args = try args.toOwnedSlice(alloc);
                    const with_preamble = try maybeInjectUtf8Preamble(
                        alloc,
                        direct_args,
                        utf8_console,
                    );
                    break :shell try maybeWrapGitBashWithWinpty(alloc, with_preamble);
                }

                // Command contains cmd.exe metacharacters (or parsing
                // produced no tokens). Let cmd.exe handle it.
                var args: std.ArrayList([:0]const u8) = try .initCapacity(alloc, 4);
                defer args.deinit(alloc);

                // Note we don't free any of the memory below since it is
                // allocated in the arena.
                const windir = std.process.getEnvVarOwned(
                    alloc,
                    "WINDIR",
                ) catch |err| {
                    log.warn("failed to get WINDIR, cannot run shell command err={}", .{err});
                    return error.SystemError;
                };
                const cmd = try std.fs.path.joinZ(alloc, &[_][]const u8{
                    windir,
                    "System32",
                    "cmd.exe",
                });

                // Gate on the encoding policy directly. The chcp
                // prepend runs the cmd under ConPTY; utf8-console = never
                // is a real kill switch, and `auto` (resolved to
                // `.always` on non-CJK Windows) still forces UTF-8
                // across the whole pipeline.
                const script: [:0]const u8 = if (resolveUtf8Console(utf8_console) == .always)
                    try std.fmt.allocPrintSentinel(
                        alloc,
                        "chcp 65001 >nul && {s}",
                        .{v},
                        0,
                    )
                else
                    try alloc.dupeZ(u8, v);

                try args.append(alloc, cmd);
                try args.append(alloc, "/C");
                try args.append(alloc, script);
                break :shell try args.toOwnedSlice(alloc);
            }

            // POSIX: wrap with `/bin/sh -c` so the shell handles argument
            // splitting, quoting, and expansion. On POSIX the extra sh
            // is usually optimized away via tail exec.
            var args: std.ArrayList([:0]const u8) = try .initCapacity(alloc, 4);
            defer args.deinit(alloc);
            try args.append(alloc, "/bin/sh");
            if (internal_os.isFlatpak()) try args.append(alloc, "-l");
            try args.append(alloc, "-c");
            try args.append(alloc, v);
            break :shell try args.toOwnedSlice(alloc);
        },
    };
}

/// Returns true if `s` contains any cmd.exe metacharacter that would
/// actually need cmd.exe to interpret (pipes, redirects, chaining,
/// grouping, escape, env expansion, delayed expansion). If it returns
/// false we can safely tokenize `s` ourselves and spawn the first
/// token directly. Quotes are intentionally not in the list: they're
/// handled by the argv tokenizer.
fn windowsShellNeedsCmdWrapping(s: []const u8) bool {
    for (s) |c| switch (c) {
        '&', '|', '<', '>', '(', ')', '^', '%', '!' => return true,
        else => {},
    };
    return false;
}

/// Prepend winpty to a manually-configured Git-for-Windows / MSYS2
/// `bash.exe` so its job-control init sees a MinTTY-compatible PTY under
/// ConPTY instead of emitting "cannot set terminal process group (-1):
/// Inappropriate ioctl for device" + "no job control in this shell".
///
/// This gives a manual `command = "C:\Program Files\Git\bin\bash.exe"`
/// the same winpty/ConPTY job-control bridge that the profile-discovery
/// probes (GitBashProbe, Msys2Probe) build into their command strings.
/// (The probes additionally append `--login -i`; here the user owns
/// their own flags, so we only prepend winpty.)
///
/// Gated narrowly so we never wrap the wrong bash:
///   - args[0] must identify as bash (basename `bash`/`bash.exe`).
///   - args[0] must be an absolute path. Bare `bash` is left alone: on a
///     normal PATH it resolves to C:\Windows\System32\bash.exe (the WSL
///     launcher, which must NOT be winpty-wrapped), and PATH-resolving
///     here would duplicate Command.startWindows. A relative path is also
///     left alone: it can't be validated by accessAbsolute below (which
///     requires an absolute path). Both degrade gracefully to the
///     pre-existing behavior.
///   - a winpty.exe must exist adjacent to the bash (see
///     windows_shell.winptyCandidatePaths). System32 has none, so WSL is
///     excluded; a discovered profile's args[0] is winpty (not bash), so
///     there is no double-wrap.
///
/// Build the WSL launcher prefix that runs the terminfo-install script in the
/// target distro: `wsl.exe [-d <distro>] -- sh -c`. terminfo_install.install
/// appends the script as the final arg.
fn wslTerminfoLauncherPrefix(
    alloc: Allocator,
    distro: []const u8,
) Allocator.Error![]const []const u8 {
    if (distro.len == 0) {
        return try alloc.dupe([]const u8, &.{ "wsl.exe", "--", "sh", "-c" });
    }
    return try alloc.dupe([]const u8, &.{ "wsl.exe", "-d", distro, "--", "sh", "-c" });
}

/// Turn a WSL distro name into a single hostname label that always passes
/// `DiskCache.isValidCacheKey` (which validates keys as RFC-1123 hostnames).
/// Keeps `[A-Za-z0-9]`, collapses every other run to a single `-`, never
/// produces a leading/trailing `-`, caps the label at 63 bytes, and falls back
/// to `"wsl"` if nothing valid remains. A non-readable but bulletproof hash
/// would also work; a readable slug keeps the cache file inspectable.
fn wslDistroSlug(buf: []u8, name: []const u8) []const u8 {
    const max_label = 63;
    var n: usize = 0;
    var prev_dash = false;
    for (name) |c| {
        if (n >= buf.len or n >= max_label) break;
        switch (c) {
            'A'...'Z', 'a'...'z', '0'...'9' => {
                buf[n] = c;
                n += 1;
                prev_dash = false;
            },
            // Collapse runs of non-alnum to one `-`, and never lead with one.
            else => if (!prev_dash and n > 0) {
                buf[n] = '-';
                n += 1;
                prev_dash = true;
            },
        }
    }
    // Strip a trailing `-` (a label may not end with one).
    while (n > 0 and buf[n - 1] == '-') n -= 1;
    if (n == 0) {
        const fallback = "wsl";
        @memcpy(buf[0..fallback.len], fallback);
        return buf[0..fallback.len];
    }
    return buf[0..n];
}

/// Windows-only: if `argv` launches WSL, ensure Ghostty's `xterm-ghostty`
/// terminfo is installed in the target distro before the shell spawns, so the
/// first session resolves it. Installs the real terminfo the same way the
/// `ssh-terminfo` feature provisions remote hosts (pipe the encoded source to
/// `tic` over the launcher). Cached per distro so steady-state launches skip
/// the extra `wsl` spawn. Side-effecting and entirely best-effort: any failure
/// degrades to a warning, and the install script itself skips when terminfo is
/// already present and bails without `tic`. Never affects the spawned argv.
fn maybeProvisionWslTerminfo(
    alloc: Allocator,
    argv: []const [:0]const u8,
) void {
    if (comptime builtin.os.tag != .windows) return;

    // Cheap gate first: most spawns are not WSL, so avoid the view alloc below.
    if (argv.len == 0 or !internal_os.windows_shell.isWslExe(argv[0])) return;

    // wslDistro takes []const []const u8; build a thin non-sentinel view.
    const view = alloc.alloc([]const u8, argv.len) catch return;
    defer alloc.free(view);
    for (argv, 0..) |a, i| view[i] = a;

    const distro = internal_os.windows_shell.wslDistro(view) orelse return;

    // The default distro (empty) is keyed "default". A real distro literally
    // named "default" would share that marker, which is harmless (both want
    // the same terminfo).
    var slug_buf: [256]u8 = undefined;
    const key = wslDistroSlug(&slug_buf, if (distro.len == 0) "default" else distro);

    // Per-distro cache (reuses the ssh terminfo DiskCache mechanism, separate
    // file). Optional: any lookup failure just means we run the (idempotent,
    // self-skipping) install script.
    const cache: ?DiskCache = cache: {
        const state_dir = internal_os.xdg.state(
            alloc,
            .{ .subdir = "ghostty" },
        ) catch break :cache null;
        defer alloc.free(state_dir);
        const path = std.fs.path.join(
            alloc,
            &.{ state_dir, "wsl_terminfo_cache" },
        ) catch break :cache null;
        break :cache .{ .path = path };
    };
    defer if (cache) |c| alloc.free(c.path);

    if (cache) |c| {
        if (c.contains(alloc, key) catch false) return;
    }

    const prefix = wslTerminfoLauncherPrefix(alloc, distro) catch return;
    defer alloc.free(prefix);
    terminfo_install.install(alloc, prefix, false) catch |err| {
        log.warn("wsl terminfo install failed (distro={s}): {}", .{ key, err });
        return;
    };

    // Best-effort record; a write failure just re-runs the self-skipping
    // script next launch.
    if (cache) |c| c.add(alloc, key, std.time.timestamp()) catch {};
}

test "wslTerminfoLauncherPrefix builds wsl sh -c prefix" {
    const testing = std.testing;
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const prefix = try wslTerminfoLauncherPrefix(alloc, "Ubuntu");
    try testing.expectEqual(@as(usize, 6), prefix.len);
    try testing.expectEqualStrings("wsl.exe", prefix[0]);
    try testing.expectEqualStrings("-d", prefix[1]);
    try testing.expectEqualStrings("Ubuntu", prefix[2]);
    try testing.expectEqualStrings("--", prefix[3]);
    try testing.expectEqualStrings("sh", prefix[4]);
    try testing.expectEqualStrings("-c", prefix[5]);

    // Default distro (empty) omits -d.
    const prefix_def = try wslTerminfoLauncherPrefix(alloc, "");
    try testing.expectEqual(@as(usize, 4), prefix_def.len);
    try testing.expectEqualStrings("wsl.exe", prefix_def[0]);
    try testing.expectEqualStrings("--", prefix_def[1]);
    try testing.expectEqualStrings("sh", prefix_def[2]);
    try testing.expectEqualStrings("-c", prefix_def[3]);
}

test "wslDistroSlug always yields a valid DiskCache key" {
    const testing = std.testing;
    var buf: [256]u8 = undefined;

    // Adversarial names that previously slugged to invalid hostname keys
    // (leading/trailing '-', empty labels, over-long) and silently disabled
    // the cache. All must now pass DiskCache.isValidCacheKey.
    const names = [_][]const u8{
        "Ubuntu",
        "Ubuntu-24.04",
        "Ubuntu 22.04 LTS",
        "default",
        "Ubuntu ", // trailing space -> would have been "Ubuntu-"
        " -weird- ", // leading/trailing junk
        "My_Distro_",
        "....", // all non-alnum
        "" ++ ("x" ** 100), // over-long label
        "",
    };
    inline for (names) |name| {
        const slug = wslDistroSlug(&buf, name);
        try testing.expect(DiskCache.isValidCacheKey(slug));
    }

    // Concrete spot-checks.
    try testing.expectEqualStrings("Ubuntu-24-04", wslDistroSlug(&buf, "Ubuntu-24.04"));
    try testing.expectEqualStrings("Ubuntu", wslDistroSlug(&buf, "Ubuntu "));
    try testing.expectEqualStrings("wsl", wslDistroSlug(&buf, "...."));
    try testing.expect(wslDistroSlug(&buf, "" ++ ("x" ** 100)).len == 63);
}

/// When no wrap applies the input `args` slice is returned unchanged.
/// Both input and output are owned by the caller's arena.
fn maybeWrapGitBashWithWinpty(
    alloc: Allocator,
    args: []const [:0]const u8,
) Allocator.Error![]const [:0]const u8 {
    if (comptime builtin.os.tag != .windows) return args;
    if (args.len == 0) return args;
    if (internal_os.windows_shell.identify(args[0]) != .bash) return args;

    // Absolute path only (see doc comment): bare/relative bash is skipped,
    // and an absolute path satisfies accessAbsolute's precondition below.
    if (!std.fs.path.isAbsoluteWindows(std.mem.trim(u8, args[0], "\"' \t\r\n")))
        return args;

    // Best effort: an allocation failure here just skips the wrap rather
    // than failing the spawn.
    const candidates = internal_os.windows_shell.winptyCandidatePaths(alloc, args[0]) catch return args;

    const winpty: []const u8 = found: {
        for (candidates) |c| {
            std.fs.accessAbsolute(c, .{}) catch continue;
            break :found c;
        }
        return args;
    };

    const out = try alloc.alloc([:0]const u8, args.len + 1);
    out[0] = try alloc.dupeZ(u8, winpty);
    @memcpy(out[1..], args);
    return out;
}

/// Inject a shell-specific UTF-8 codepage setup into argv when the
/// resolved utf8-console policy is `always`. Skips when the user
/// configured `never` or when `auto` resolved to `never` (CJK ACP).
///
/// Why pwsh needs this under ConPTY (the sole Windows transport):
/// CreatePseudoConsole spawns conhost at the system OEM CP regardless
/// of parent state, so the child needs an explicit
/// `[Console]::OutputEncoding = ...` to talk UTF-8.
///
/// Other VT-aware shells (bash, wsl, ssh, nu, fish, zsh, elvish,
/// xonsh) short-circuit on the Preamble.none guard.
///
/// Callers own both the input `args` and the returned slice; when no
/// injection is needed the returned slice aliases `args` (no copy).
fn maybeInjectUtf8Preamble(
    alloc: Allocator,
    args: []const [:0]const u8,
    utf8_console: configpkg.Config.Utf8Console,
) Allocator.Error![]const [:0]const u8 {
    if (comptime builtin.os.tag != .windows) return args;
    if (args.len == 0) return args;

    // Honor the user kill switch first. `.auto` resolves against the
    // system ACP and collapses to `.always` on Western Windows (the
    // common case) or `.never` on default-locale CJK Windows where
    // forcing UTF-8 would mojibake legacy double-byte .bat scripts.
    if (resolveUtf8Console(utf8_console) == .never) return args;

    const preamble = internal_os.windows_shell.utf8Preamble(args[0]);
    if (preamble == .none) return args;

    // If the user passed a flag that already consumes "the rest of the
    // command line" (cmd `/C`/`/K`, pwsh `-Command`), we can still get
    // UTF-8 by *wrapping* their script instead of appending to argv.
    // `-EncodedCommand` is handled similarly by decoding the base64
    // UTF-16LE, prepending the setup, and re-encoding (see
    // `wrapEncodedCommand`). `-File` is the one case we still bail on:
    // its script lives in a file we must not modify. For pwsh scripts
    // whose first non-whitespace token must stay first-statement
    // (`param(...)`, `#requires`, `{ ... }`) we also bail, because
    // prepending demotes those constructs silently.
    switch (findPreambleConflict(args, preamble)) {
        .none => return appendSuffix(alloc, args, preamble),
        // cmd re-parses its `/C`/`/K` command line, so the tail must be
        // re-serialized with MS-C-runtime argv quoting to round-trip.
        .cmd_script => |idx| return wrapScript(alloc, args, idx, preamble.prefix(), true),
        .pwsh_command => |idx| {
            if (pwshTailRequiresFirstStatement(args[idx + 1 ..])) |reason| {
                log.debug(
                    "UTF-8 preamble skipped: pwsh -Command script starts with {s} " ++
                        "which must remain the first statement",
                    .{reason},
                );
                return args;
            }
            // pwsh `-Command` takes a SCRIPT string, not an argv array.
            // Re-quoting the tail with argv rules would turn a dot-source
            // like `. 'C:/x/ghostty.ps1'` into the string literal
            // `". 'C:/x/ghostty.ps1'"`, which pwsh prints instead of
            // executing (the integration script then never loads). Join
            // the tail verbatim, matching how pwsh itself concatenates
            // post-`-Command` args into the script.
            return wrapScript(alloc, args, idx, preamble.prefix(), false);
        },
        .pwsh_encoded_command => |idx| {
            if (try wrapEncodedCommand(alloc, args, idx, preamble)) |new_args|
                return new_args;
            // wrapEncodedCommand logs the specific skip reason.
            return args;
        },
        .pwsh_file => |idx| {
            log.debug(
                "UTF-8 preamble skipped: arg[{d}]=\"{s}\" is a -File path we must not modify",
                .{ idx, args[idx] },
            );
            return args;
        },
    }
}

/// `firstStatementReason` applied to the first `-Command` tail arg.
/// Returns null when the tail is empty or prepending our setup is safe.
fn pwshTailRequiresFirstStatement(tail: []const [:0]const u8) ?[]const u8 {
    if (tail.len == 0) return null;
    return firstStatementReason(tail[0]);
}

/// If `script`'s leading token is a pwsh construct that must sit at the
/// top of the script (`param(...)`, `#requires`, or a bare `{ ... }`
/// scriptblock literal), return a short description for the skip log.
/// Returns null when prepending our setup is safe. Shared by the
/// `-Command` (tail) and `-EncodedCommand` (decoded script) paths.
///
/// Rationale:
/// - `param(...)` must be the first statement in a script; moving it
///   lower causes `The param statement can only be used as the first
///   statement in the body of a script`.
/// - `#requires -Version X` directives only take effect when they sit
///   on the first non-comment line; otherwise they are parsed as
///   comments and silently no-op.
/// - A leading `{ ... }` at the top of a `-Command` script is
///   interpreted as a scriptblock *literal* expression (its value is
///   produced and discarded). Our prepend would not change the meaning
///   here but would suppress whatever the user was hoping to observe;
///   the safer choice is to leave their argv as-is.
fn firstStatementReason(script: []const u8) ?[]const u8 {
    const first = std.mem.trimLeft(u8, script, " \t\r\n");
    if (first.len == 0) return null;
    if (first[0] == '{') return "a scriptblock literal";
    if (asciiStartsWithIgnoreCase(first, "#requires")) return "a #requires directive";
    if (asciiStartsWithIgnoreCase(first, "param(") or
        asciiStartsWithIgnoreCase(first, "param ("))
        return "a param() block";
    return null;
}

fn asciiStartsWithIgnoreCase(haystack: []const u8, needle: []const u8) bool {
    if (haystack.len < needle.len) return false;
    return std.ascii.eqlIgnoreCase(haystack[0..needle.len], needle);
}

/// Append the preamble's argv suffix to `args`. Caller owns the result.
fn appendSuffix(
    alloc: Allocator,
    args: []const [:0]const u8,
    preamble: internal_os.windows_shell.Preamble,
) Allocator.Error![]const [:0]const u8 {
    const suffix = preamble.suffix();
    const out = try alloc.alloc([:0]const u8, args.len + suffix.len);
    @memcpy(out[0..args.len], args);
    // The suffix elements are `.rodata` string literals; they outlive
    // any arena and spawning reads them during CreateProcess. No dupe
    // needed, matching how `"/C"` is appended inline elsewhere here.
    @memcpy(out[args.len..], suffix);
    return out;
}

/// Wrap the user-supplied script that sits at `args[flag_idx+1..]` by
/// prepending `prefix_text` and collapsing the tail into a single argv
/// element. Produces `args[0..flag_idx+1] ++ [prefix_text ++ tail]`.
///
/// `quote_tail` selects how the tail is serialized (see
/// `buildWrappedScript` for the full rationale): `true` re-quotes each
/// arg with MS-C-runtime rules for cmd `/C`/`/K`; `false` writes the
/// tail verbatim for pwsh `-Command` (whose value is script text, not
/// an argv array). The cmd `/C` interaction described below applies
/// only to the quoted (`true`) path.
///
/// Quoted path: the tail is re-serialized with MS-C-runtime quoting
/// rules (matching `windowsCreateCommandLine` in Command.zig) so args
/// that originally contained spaces or quotes round-trip correctly:
/// tokens like `C:\Program Files` are re-wrapped in quotes when joined
/// back. The resulting command line survives cmd's two-rule `/C`
/// interaction documented in `cmd /?`: when our wrapped arg contains
/// any embedded quotes (because we re-quoted a path with spaces),
/// `lpCommandLine` has inner `"` characters and cmd falls into rule 1
/// ("preserve quoting as seen"). When it contains none, the outermost
/// quoting `windowsCreateCommandLine` adds is trivially symmetric and
/// rule 2 ("strip outer quotes") is safe to apply. Either way cmd sees
/// the same tokens the user originally wrote.
///
/// When `flag_idx+1 == args.len` (flag is the last arg, so there is
/// nothing to wrap) we leave argv alone rather than fabricate a bare
/// preamble that would change how the shell interprets the flag.
fn wrapScript(
    alloc: Allocator,
    args: []const [:0]const u8,
    flag_idx: usize,
    prefix_text: []const u8,
    quote_tail: bool,
) Allocator.Error![]const [:0]const u8 {
    const head = args[0 .. flag_idx + 1];
    const tail = args[flag_idx + 1 ..];
    if (tail.len == 0) return args;

    // `std.Io.Writer.Allocating`'s drain can only fail with
    // `error.WriteFailed`, and the backing is our own allocator, so
    // the only realistic cause is OOM. Fold the single inner function
    // into one `catch` to keep the hot path readable.
    const wrapped = buildWrappedScript(alloc, prefix_text, tail, quote_tail) catch
        return error.OutOfMemory;

    const out = try alloc.alloc([:0]const u8, head.len + 1);
    @memcpy(out[0..head.len], head);
    out[head.len] = wrapped;
    return out;
}

/// Inject the pwsh UTF-8 preamble into a `-EncodedCommand` value
/// (`args[idx+1]`), which is base64 of a UTF-16LE script. Returns a
/// fresh argv whose value is re-encoded as `prefix ++ original`, or
/// `null` to fall back to the original argv (each path logs its reason)
/// when the value is missing, not padded/valid base64, not even-length
/// UTF-16LE, not valid UTF-16, or begins with a construct that must
/// remain the first statement (`param(`, `#requires`, `{`).
///
/// A leading UTF-16 BOM (U+FEFF) is preserved at the very front of the
/// re-encoded script (ahead of the preamble) and skipped when locating
/// the first statement, so a BOM-prefixed script is neither corrupted
/// nor able to hide a `param(`/`#requires`/`{` from the guard.
///
/// The base64 must be canonically padded (as produced by PowerShell's
/// `[Convert]::ToBase64String(...)`). Unpadded values that PowerShell
/// would still accept fall back rather than risk a mis-decode.
///
/// SECURITY: the prepended text is `preamble.prefix()`, a compile-time
/// constant. The user's decoded script bytes are preserved verbatim
/// after it (same model as `wrapScript`); no user input is interpolated
/// into a new shell string.
fn wrapEncodedCommand(
    alloc: Allocator,
    args: []const [:0]const u8,
    idx: usize,
    preamble: internal_os.windows_shell.Preamble,
) Allocator.Error!?[]const [:0]const u8 {
    if (idx + 1 >= args.len) return null;
    const encoded = args[idx + 1];

    const dec = std.base64.standard.Decoder;
    const decoded_len = dec.calcSizeForSlice(encoded) catch {
        log.debug("UTF-8 preamble skipped: -EncodedCommand value is not valid base64", .{});
        return null;
    };
    const raw = try alloc.alloc(u8, decoded_len);
    defer alloc.free(raw);
    dec.decode(raw, encoded) catch {
        log.debug("UTF-8 preamble skipped: -EncodedCommand value failed base64 decode", .{});
        return null;
    };
    if (raw.len % 2 != 0) {
        log.debug("UTF-8 preamble skipped: -EncodedCommand decoded to odd byte length (not UTF-16LE)", .{});
        return null;
    }

    const u16s = try alloc.alloc(u16, raw.len / 2);
    defer alloc.free(u16s);
    for (u16s, 0..) |*u, i| u.* = std.mem.readInt(u16, raw[i * 2 ..][0..2], .little);

    // A leading UTF-16 BOM (U+FEFF) is kept verbatim at the front of the
    // output (ahead of the preamble) and excluded from the first-statement
    // scan, so it neither lands mid-script nor masks a leading `param(`.
    const has_bom = u16s.len > 0 and u16s[0] == 0xFEFF;
    const script_units = if (has_bom) u16s[1..] else u16s;

    const script_utf8 = std.unicode.utf16LeToUtf8Alloc(alloc, script_units) catch {
        log.debug("UTF-8 preamble skipped: -EncodedCommand is not valid UTF-16LE", .{});
        return null;
    };
    defer alloc.free(script_utf8);

    if (firstStatementReason(script_utf8)) |reason| {
        log.debug(
            "UTF-8 preamble skipped: -EncodedCommand script starts with {s} " ++
                "which must remain the first statement",
            .{reason},
        );
        return null;
    }

    // Build [BOM?] ++ prefix ++ script as UTF-16LE bytes, then
    // base64-encode. preamble.prefix() is ASCII, so each byte maps to
    // exactly one UTF-16 code unit; the user's script units are appended
    // verbatim. Building UTF-16 directly avoids a UTF-8 round-trip (and
    // its InvalidUtf8 error) on text we already hold as UTF-16.
    const prefix = preamble.prefix();
    const bom_len: usize = @intFromBool(has_bom);
    const new_bytes = try alloc.alloc(u8, (bom_len + prefix.len + script_units.len) * 2);
    defer alloc.free(new_bytes);
    if (has_bom) std.mem.writeInt(u16, new_bytes[0..2], 0xFEFF, .little);
    for (prefix, 0..) |c, i|
        std.mem.writeInt(u16, new_bytes[(bom_len + i) * 2 ..][0..2], c, .little);
    for (script_units, 0..) |u, i|
        std.mem.writeInt(u16, new_bytes[(bom_len + prefix.len + i) * 2 ..][0..2], u, .little);

    const enc = std.base64.standard.Encoder;
    const new_b64 = try alloc.allocSentinel(u8, enc.calcSize(new_bytes.len), 0);
    _ = enc.encode(new_b64, new_bytes);

    const out = try alloc.alloc([:0]const u8, args.len);
    @memcpy(out, args);
    out[idx + 1] = new_b64;
    return out;
}

/// Join `tail` after `prefix_text` into a single script element.
///
/// `quote_tail` selects how the tail is serialized:
/// - `true` (cmd `/C`/`/K`): re-quote each arg with MS-C-runtime rules
///   so cmd, which re-parses its command line, sees the same tokens.
/// - `false` (pwsh `-Command`): write each arg verbatim, space-joined.
///   A pwsh `-Command` value is SCRIPT text, not an argv array; quoting
///   a token that contains spaces (e.g. `. 'C:/x/ghostty.ps1'`) would
///   turn it into a string literal that pwsh prints instead of running.
///   Verbatim space-join matches how pwsh concatenates post-`-Command`
///   args into the script.
fn buildWrappedScript(
    alloc: Allocator,
    prefix_text: []const u8,
    tail: []const [:0]const u8,
    quote_tail: bool,
) ![:0]u8 {
    var buf: std.Io.Writer.Allocating = .init(alloc);
    errdefer buf.deinit();
    const writer = &buf.writer;

    try writer.writeAll(prefix_text);
    for (tail, 0..) |arg, i| {
        if (i > 0) try writer.writeByte(' ');
        if (quote_tail) try writeQuotedArg(writer, arg) else try writer.writeAll(arg);
    }
    return try buf.toOwnedSliceSentinel(0);
}

/// Serialize `arg` into `writer` using the MS C runtime quoting rules
/// (CommandLineToArgvW inverse). Matches `windowsCreateCommandLine` in
/// Command.zig byte-for-byte; we duplicate here rather than cross-
/// module to avoid leaking an internal helper, and the per-arg surface
/// area is small enough that drift is easy to audit.
///
/// Note cmd.exe uses its OWN parser for `/C` tokenization (special
/// chars `& | < > ^ ( )`, not CRT-style backslash escaping). The round
/// trip works here because each individual arg survives both parsers
/// identically: our quoted output has no unescaped cmd metacharacters,
/// and cmd's rules don't consume backslashes. Do not extend this to
/// emit cmd-specific escape sequences without adding tests.
fn writeQuotedArg(writer: *std.Io.Writer, arg: []const u8) !void {
    if (std.mem.indexOfAny(u8, arg, " \t\n\"") == null) {
        try writer.writeAll(arg);
        return;
    }
    try writer.writeByte('"');
    var backslash_count: usize = 0;
    for (arg) |byte| switch (byte) {
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
    };
    try writer.splatByteAll('\\', backslash_count * 2);
    try writer.writeByte('"');
}

/// Which preamble-conflicting flag the user supplied, and where it
/// sits in argv. The payload index points at the flag itself; the
/// script argument (if any) sits at `idx + 1`.
const PreambleConflict = union(enum) {
    none,
    /// cmd.exe `/C` or `/K`: everything after is a command string we
    /// can safely prepend `chcp 65001 >nul && ` to.
    cmd_script: usize,
    /// pwsh `-Command`: the tail is a script we can prepend the
    /// `[Console]::*Encoding = ...` setup to.
    pwsh_command: usize,
    /// pwsh `-File`: the tail is a path to a script we must not
    /// modify. Skip and log.
    pwsh_file: usize,
    /// pwsh `-EncodedCommand`: the value is base64-encoded UTF-16LE.
    /// We decode it, prepend the UTF-8 setup, and re-encode (see
    /// `wrapEncodedCommand`), falling back to a skip on decode failure
    /// or a leading first-statement construct.
    pwsh_encoded_command: usize,
};

fn findPreambleConflict(
    args: []const [:0]const u8,
    preamble: internal_os.windows_shell.Preamble,
) PreambleConflict {
    if (args.len <= 1) return .none;
    return switch (preamble) {
        .none => .none,
        .cmd => blk: {
            for (args[1..], 1..) |arg, i| {
                // Only `/C` and `/K` consume "the rest of the command
                // line" and would swallow our preamble. Single-dash
                // `-c` is not recognized by cmd.exe.
                if (arg.len != 2 or arg[0] != '/') continue;
                const c = std.ascii.toLower(arg[1]);
                if (c == 'c' or c == 'k') break :blk .{ .cmd_script = i };
            }
            break :blk .none;
        },
        .pwsh => blk: {
            for (args[1..], 1..) |arg, i| switch (pwshConflictKind(arg)) {
                .none => {},
                .command => break :blk .{ .pwsh_command = i },
                .file => break :blk .{ .pwsh_file = i },
                .encoded_command => break :blk .{ .pwsh_encoded_command = i },
            };
            break :blk .none;
        },
    };
}

const PwshConflictKind = enum {
    none,
    /// `-Command`, `-c`, or any unambiguous prefix like `-Com`.
    command,
    /// `-File`, `-f`, or any unambiguous prefix like `-Fi`.
    file,
    /// `-EncodedCommand`, `-ec`, `-enc`, or any unambiguous prefix.
    encoded_command,
};

/// Classify a single pwsh/powershell argv element as a preamble
/// conflict. We match by unambiguous prefix because PowerShell accepts
/// any prefix of a flag name (e.g. `-Com` resolves to `-Command`).
fn pwshConflictKind(arg: []const u8) PwshConflictKind {
    if (arg.len < 2 or arg[0] != '-') return .none;

    var buf: [32]u8 = undefined;
    const tail_len = arg.len - 1;
    // Anything longer than a real PS flag name cannot be a conflict.
    if (tail_len > buf.len) return .none;
    const lower = std.ascii.lowerString(buf[0..tail_len], arg[1..]);

    // Exact matches for short forms and unambiguous 2-letter
    // abbreviations. These take precedence over prefix matches.
    if (std.mem.eql(u8, lower, "c")) return .command;
    if (std.mem.eql(u8, lower, "f")) return .file;
    if (std.mem.eql(u8, lower, "ec")) return .encoded_command;
    if (std.mem.eql(u8, lower, "enc")) return .encoded_command;

    // Prefix matches. We require length >= 3 for command/file to avoid
    // colliding with `-ConfigurationName` (starts with -C) or
    // `-Format*` (starts with -F). `encodedcommand` has `ec` covered
    // above, so the prefix path starts at length 4 (`-enco`).
    if (lower.len >= 3 and std.mem.startsWith(u8, "command", lower)) return .command;
    if (lower.len >= 3 and std.mem.startsWith(u8, "file", lower)) return .file;
    if (lower.len >= 4 and std.mem.startsWith(u8, "encodedcommand", lower)) return .encoded_command;

    return .none;
}

/// Get information about the process(es) running within the backend. Returns
/// `null` if there was an error getting the information or the information is
/// not available on a particular platform.
pub fn getProcessInfo(self: *Exec, comptime info: ProcessInfo) ?ProcessInfo.Type(info) {
    return self.subprocess.getProcessInfo(info);
}

/// Binary resolution of `Config.Utf8Console`. `auto` collapses to
/// `.never` on default-locale CJK Windows (Shift-JIS, GB2312, EUC-KR,
/// Big5, Johab) and to `.always` everywhere else. The runtime never
/// sees `.auto`; only its resolved form.
const ResolvedUtf8Console = enum { always, never };

fn resolveUtf8Console(cfg: configpkg.Config.Utf8Console) ResolvedUtf8Console {
    const resolved: ResolvedUtf8Console = switch (cfg) {
        .always => .always,
        .never => .never,
        .auto => if (internal_os.windows_shell.isCjkAnsiCodePage())
            .never
        else
            .always,
    };
    log_validate.info(
        "utf8-console resolved: config={s} resolved={s}",
        .{ @tagName(cfg), @tagName(resolved) },
    );
    return resolved;
}

test "execCommand darwin: shell command" {
    if (comptime !builtin.os.tag.isDarwin()) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const result = try execCommand(alloc, .{ .shell = "foo bar baz" }, struct {
        fn get(_: Allocator) !PasswdEntry {
            return .{
                .name = "testuser",
            };
        }
    }, .auto);

    try testing.expectEqual(8, result.len);
    try testing.expectEqualStrings(result[0], "/usr/bin/login");
    try testing.expectEqualStrings(result[1], "-flp");
    try testing.expectEqualStrings(result[2], "testuser");
    try testing.expectEqualStrings(result[3], "/bin/bash");
    try testing.expectEqualStrings(result[4], "--noprofile");
    try testing.expectEqualStrings(result[5], "--norc");
    try testing.expectEqualStrings(result[6], "-c");
    try testing.expectEqualStrings(result[7], "exec -l foo bar baz");
}

test "execCommand darwin: direct command" {
    if (comptime !builtin.os.tag.isDarwin()) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const result = try execCommand(alloc, .{ .direct = &.{
        "foo",
        "bar baz",
    } }, struct {
        fn get(_: Allocator) !PasswdEntry {
            return .{
                .name = "testuser",
            };
        }
    }, .auto);

    try testing.expectEqual(5, result.len);
    try testing.expectEqualStrings(result[0], "/usr/bin/login");
    try testing.expectEqualStrings(result[1], "-flp");
    try testing.expectEqualStrings(result[2], "testuser");
    try testing.expectEqualStrings(result[3], "foo");
    try testing.expectEqualStrings(result[4], "bar baz");
}

test "execCommand: shell command, empty passwd" {
    if (comptime builtin.os.tag == .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const result = try execCommand(
        alloc,
        .{ .shell = "foo bar baz" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                // Empty passwd entry means we can't construct a macOS
                // login command and falls back to POSIX behavior.
                return .{};
            }
        },
        .auto,
    );

    try testing.expectEqual(3, result.len);
    try testing.expectEqualStrings(result[0], "/bin/sh");
    try testing.expectEqualStrings(result[1], "-c");
    try testing.expectEqualStrings(result[2], "foo bar baz");
}

test "execCommand: shell command, error passwd" {
    if (comptime builtin.os.tag == .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const result = try execCommand(
        alloc,
        .{ .shell = "foo bar baz" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                // Failed passwd entry means we can't construct a macOS
                // login command and falls back to POSIX behavior.
                return error.Fail;
            }
        },
        .auto,
    );

    try testing.expectEqual(3, result.len);
    try testing.expectEqualStrings(result[0], "/bin/sh");
    try testing.expectEqualStrings(result[1], "-c");
    try testing.expectEqualStrings(result[2], "foo bar baz");
}

test "execCommand: direct command, error passwd" {
    if (comptime builtin.os.tag == .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const result = try execCommand(alloc, .{
        .direct = &.{
            "foo",
            "bar baz",
        },
    }, struct {
        fn get(_: Allocator) !PasswdEntry {
            // Failed passwd entry means we can't construct a macOS
            // login command and falls back to POSIX behavior.
            return error.Fail;
        }
    }, .auto);

    try testing.expectEqual(2, result.len);
    try testing.expectEqualStrings(result[0], "foo");
    try testing.expectEqualStrings(result[1], "bar baz");
}

test "execCommand: direct command, config freed" {
    if (comptime builtin.os.tag == .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var command_arena = ArenaAllocator.init(testing.allocator);
    const command_alloc = command_arena.allocator();
    const command = try (configpkg.Command{
        .direct = &.{
            "foo",
            "bar baz",
        },
    }).clone(command_alloc);

    const result = try execCommand(alloc, command, struct {
        fn get(_: Allocator) !PasswdEntry {
            // Failed passwd entry means we can't construct a macOS
            // login command and falls back to POSIX behavior.
            return error.Fail;
        }
    }, .auto);

    command_arena.deinit();

    try testing.expectEqual(2, result.len);
    try testing.expectEqualStrings(result[0], "foo");
    try testing.expectEqualStrings(result[1], "bar baz");
}

test "execCommand windows: bare cmd.exe resolves via COMSPEC" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const result = try execCommand(
        alloc,
        .{ .shell = "cmd.exe" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        // utf8-console=never to disable the chcp preamble; this test
        // verifies COMSPEC resolution, not preamble injection.
        .never,
    );

    try testing.expectEqual(1, result.len);
    try testing.expectEqualStrings(try expectedCmdExeArg0(alloc), result[0]);
}

/// Helper for the Windows execCommand tests: what we expect `args[0]`
/// to be when the configured shell is the bare token "cmd.exe". The
/// production code at execCommand's shell-handling block resolves it
/// via %COMSPEC% when set, otherwise leaves the input unchanged -- so
/// the unset case returns "cmd.exe" literally, not a hard-coded path.
fn expectedCmdExeArg0(alloc: Allocator) Allocator.Error![]const u8 {
    return std.process.getEnvVarOwned(alloc, "COMSPEC") catch
        try alloc.dupe(u8, "cmd.exe");
}

test "execCommand windows: shell command, single token spawns directly" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    // utf8-console=never disables the preamble injection (# 341) so this
    // test stays focused on what it's actually checking: shell-string
    // parsing.
    const result = try execCommand(
        alloc,
        .{ .shell = "pwsh.exe" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .never,
    );

    // No cmd.exe /C wrapper: args[0] is the configured shell itself.
    try testing.expectEqual(1, result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
}

test "execCommand windows: shell command, args split without cmd wrap" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const result = try execCommand(
        alloc,
        .{ .shell = "pwsh.exe -NoLogo -NoProfile" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .never,
    );

    try testing.expectEqual(3, result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-NoLogo", result[1]);
    try testing.expectEqualStrings("-NoProfile", result[2]);
}

test "execCommand windows: shell command, quoted path kept as one arg" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const result = try execCommand(
        alloc,
        .{ .shell = "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .never,
    );

    try testing.expectEqual(2, result.len);
    try testing.expectEqualStrings(
        "C:\\Program Files\\PowerShell\\7\\pwsh.exe",
        result[0],
    );
    try testing.expectEqualStrings("-NoLogo", result[1]);
}

test "execCommand windows: shell command with pipe falls back to cmd.exe" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    // utf8-console=never disables the chcp prepend so this test stays
    // focused on cmd-wrap routing, not preamble injection (covered by
    // its own test).
    const result = try execCommand(
        alloc,
        .{ .shell = "dir | findstr foo" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .never,
    );

    // Metachar present: wrap with cmd.exe /C so cmd handles the pipe.
    try testing.expectEqual(3, result.len);
    try testing.expect(std.mem.endsWith(u8, result[0], "cmd.exe"));
    try testing.expectEqualStrings("/C", result[1]);
    try testing.expectEqualStrings("dir | findstr foo", result[2]);
}

test "execCommand windows: shell command with redirect falls back to cmd.exe" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const result = try execCommand(
        alloc,
        .{ .shell = "echo hi > out.txt" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .never,
    );

    try testing.expectEqual(3, result.len);
    try testing.expect(std.mem.endsWith(u8, result[0], "cmd.exe"));
    try testing.expectEqualStrings("/C", result[1]);
    try testing.expectEqualStrings("echo hi > out.txt", result[2]);
}

test "execCommand windows: direct command is passed through unchanged" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const result = try execCommand(
        alloc,
        .{ .direct = &.{
            "C:\\tools\\foo.exe",
            "arg with spaces",
        } },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .auto,
    );

    try testing.expectEqual(2, result.len);
    try testing.expectEqualStrings("C:\\tools\\foo.exe", result[0]);
    try testing.expectEqualStrings("arg with spaces", result[1]);
}

test "windowsShellNeedsCmdWrapping" {
    const testing = std.testing;

    // Simple commands don't need cmd.exe.
    try testing.expect(!windowsShellNeedsCmdWrapping("pwsh.exe"));
    try testing.expect(!windowsShellNeedsCmdWrapping("pwsh.exe -NoLogo"));
    try testing.expect(!windowsShellNeedsCmdWrapping("cmd.exe"));
    try testing.expect(!windowsShellNeedsCmdWrapping(
        "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\"",
    ));
    try testing.expect(!windowsShellNeedsCmdWrapping(""));

    // Metachars trigger the wrapper.
    try testing.expect(windowsShellNeedsCmdWrapping("a | b"));
    try testing.expect(windowsShellNeedsCmdWrapping("a && b"));
    try testing.expect(windowsShellNeedsCmdWrapping("a > b"));
    try testing.expect(windowsShellNeedsCmdWrapping("a < b"));
    try testing.expect(windowsShellNeedsCmdWrapping("(a) & (b)"));
    try testing.expect(windowsShellNeedsCmdWrapping("echo %USERNAME%"));
    try testing.expect(windowsShellNeedsCmdWrapping("echo !var!"));
    try testing.expect(windowsShellNeedsCmdWrapping("a^b"));
}

test "resolveUtf8Console: always returns always" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    try std.testing.expectEqual(
        @as(ResolvedUtf8Console, .always),
        resolveUtf8Console(.always),
    );
}

test "resolveUtf8Console: never returns never" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    try std.testing.expectEqual(
        @as(ResolvedUtf8Console, .never),
        resolveUtf8Console(.never),
    );
}

test "resolveUtf8Console: auto agrees with isCjkAnsiCodePage" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    // Cross-check the resolved value against the linked CJK helper for
    // whatever ACP the test host actually has. Catches a regression
    // where the auto branch silently stops calling isCjkAnsiCodePage.
    const expected: ResolvedUtf8Console = if (internal_os.windows_shell.isCjkAnsiCodePage())
        .never
    else
        .always;
    try std.testing.expectEqual(expected, resolveUtf8Console(.auto));
}

// UTF-8 preamble injection tests: assert the injection decision (when
// do we inject? on what shells?) and the argv shape (length + flag
// markers). The preamble's *content* is covered by `utf8Preamble` tests
// in os/windows_shell.zig.

fn testExecWindowsShell(
    alloc: Allocator,
    shell: [:0]const u8,
) ![]const [:0]const u8 {
    return try execCommand(
        alloc,
        .{ .shell = shell },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .auto,
    );
}

test "execCommand windows: cmd.exe under auto mode gets cmd preamble" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();
    const result = try testExecWindowsShell(alloc, "cmd.exe");

    // auto + cmd (console_api) → ConPTY → cmd preamble appended.
    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings(try expectedCmdExeArg0(alloc), result[0]);
    try testing.expectEqualStrings("/K", result[1]);
    try testing.expectEqualStrings("chcp 65001 >nul", result[2]);
}

test "execCommand windows: pwsh under forced never mode gets pwsh preamble" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(arena.allocator(), "pwsh.exe");

    // never → ConPTY even for vt_aware shells. pwsh is identified as
    // the powershell family, so we inject the Console encoding setup.
    try testing.expectEqual(@as(usize, 4), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-NoExit", result[1]);
    try testing.expectEqualStrings("-Command", result[2]);
    try testing.expect(std.mem.indexOf(u8, result[3], "[Console]::OutputEncoding") != null);
}

test "execCommand windows: powershell 5.1 under auto mode gets pwsh preamble" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(arena.allocator(), "powershell.exe");

    // Windows PowerShell 5.1 is console_api → auto picks ConPTY → pwsh
    // preamble (same [Console]::*Encoding API as pwsh 7).
    try testing.expectEqual(@as(usize, 4), result.len);
    try testing.expectEqualStrings("powershell.exe", result[0]);
    try testing.expectEqualStrings("-NoExit", result[1]);
    try testing.expectEqualStrings("-Command", result[2]);
}

test "execCommand windows: unknown shell under ConPTY has no preamble" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(arena.allocator(), "my-custom.exe");

    // Unknown → no preamble even under forced ConPTY; we don't know
    // what syntax to inject.
    try testing.expectEqual(@as(usize, 1), result.len);
    try testing.expectEqualStrings("my-custom.exe", result[0]);
}

test "execCommand windows: vt-aware non-powershell (bash) under ConPTY has no preamble" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(arena.allocator(), "bash.exe");

    // bash et al. don't care about the Windows console CP; they decode
    // their own output.
    try testing.expectEqual(@as(usize, 1), result.len);
    try testing.expectEqualStrings("bash.exe", result[0]);
}

test "execCommand windows: utf8-console=never is a kill switch across transports" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    // The preamble decision is owned by the utf8-console policy, not
    // the transport. `never` must mean "no preamble anywhere",
    // regardless of how transport resolves.
    const cmd_result = try execCommand(
        alloc,
        .{ .shell = "cmd.exe" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .never,
    );
    try testing.expectEqual(@as(usize, 1), cmd_result.len);
    // %COMSPEC% resolution still happens here (transport-independent
    // shell-name handling); .never only suppresses preamble injection.
    try testing.expectEqualStrings(try expectedCmdExeArg0(alloc), cmd_result[0]);

    const pwsh_result = try execCommand(
        alloc,
        .{ .shell = "pwsh.exe" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .never,
    );
    try testing.expectEqual(@as(usize, 1), pwsh_result.len);
    try testing.expectEqualStrings("pwsh.exe", pwsh_result[0]);
}

test "execCommand windows: cmd with existing /c arg wraps user script" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(arena.allocator(), "cmd.exe /c echo hi");

    // User's `/c` consumes "the rest of the command line", so we wrap
    // instead of appending: collapse the tail tokens back into one
    // script and prepend `chcp 65001 >nul && ` (see issue # 299).
    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings("cmd.exe", result[0]);
    try testing.expectEqualStrings("/c", result[1]);
    try testing.expectEqualStrings("chcp 65001 >nul && echo hi", result[2]);
}

test "execCommand windows: cmd with existing /K arg wraps user script" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(
        arena.allocator(),
        "cmd.exe /K title UTF8",
    );

    // /K keeps the shell interactive after running the script; the
    // wrap still applies because cmd re-reads everything after /K as
    // one command line.
    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings("cmd.exe", result[0]);
    try testing.expectEqualStrings("/K", result[1]);
    try testing.expectEqualStrings("chcp 65001 >nul && title UTF8", result[2]);
}

test "execCommand windows: pwsh with existing -Command wraps user script" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(
        arena.allocator(),
        "pwsh.exe -NoProfile -Command Write-Host",
    );

    // -Command consumes "the rest of the command line". We collapse
    // the user's tail tokens back into one script and prepend the
    // chcp + [Console]::*Encoding setup so their script runs UTF-8.
    try testing.expectEqual(@as(usize, 4), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-NoProfile", result[1]);
    try testing.expectEqualStrings("-Command", result[2]);
    try testing.expect(std.mem.startsWith(u8, result[3], "chcp 65001"));
    try testing.expect(std.mem.indexOf(u8, result[3], "[Console]::OutputEncoding") != null);
    try testing.expect(std.mem.indexOf(u8, result[3], "[Console]::InputEncoding") != null);
    try testing.expect(std.mem.endsWith(u8, result[3], "; Write-Host"));
}

test "execCommand windows: pwsh with multi-token -Command script is joined" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(
        arena.allocator(),
        "pwsh.exe -Command Write-Host hello world",
    );

    // pwsh joins remaining positional args after `-Command` into the
    // script; our wrap must match that behavior by space-joining the
    // tail before prepending the setup.
    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-Command", result[1]);
    try testing.expect(std.mem.endsWith(u8, result[2], "; Write-Host hello world"));
}

test "execCommand windows: pwsh -Command script with spaces stays executable" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    // A quoted -Command script collapses to a single tail token that
    // contains spaces (here a dot-source, the shape the PowerShell
    // shell-integration auto-inject produces). The wrap must keep it as
    // executable script text, NOT re-quote it into a string literal that
    // pwsh would print instead of run.
    const result = try testExecWindowsShell(
        arena.allocator(),
        "pwsh.exe -Command \". 'C:/x/ghostty.ps1'\"",
    );

    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-Command", result[1]);
    // Tail kept verbatim after the UTF-8 setup; no wrapping double quotes.
    try testing.expect(std.mem.endsWith(u8, result[2], "; . 'C:/x/ghostty.ps1'"));
    try testing.expect(std.mem.indexOf(u8, result[2], "\". '") == null);
}

test "execCommand windows: pwsh -Command quoted path among tokens is not re-quoted" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    // A quoted path among positional -Command tokens: the tokenizer strips
    // the quotes (as CommandLineToArgvW would before pwsh concatenates), so
    // the verbatim join must reproduce the unquoted form pwsh sees natively.
    // This is the one shape where pwsh (no re-quote) deliberately diverges
    // from the cmd /c path (which DOES re-quote).
    const result = try testExecWindowsShell(
        arena.allocator(),
        "pwsh.exe -Command Get-ChildItem \"C:\\Program Files\"",
    );

    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings("-Command", result[1]);
    try testing.expect(std.mem.endsWith(u8, result[2], "; Get-ChildItem C:\\Program Files"));
    // No re-introduced double quotes anywhere in the pwsh script.
    try testing.expect(std.mem.indexOfScalar(u8, result[2], '"') == null);
}

test "execCommand windows: cmd /c with quoted path preserves quoting on wrap" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    // The config tokenizer strips the outer quotes, giving us
    // ["cmd.exe", "/c", "dir", "C:\\Program Files"]. If we space-join
    // blindly, cmd would see `dir C:\Program Files` and break path
    // resolution. The wrap must re-quote args containing spaces so
    // cmd re-tokenizes into `dir "C:\Program Files"` on the inside.
    const result = try testExecWindowsShell(
        arena.allocator(),
        "cmd.exe /c dir \"C:\\Program Files\"",
    );

    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings("cmd.exe", result[0]);
    try testing.expectEqualStrings("/c", result[1]);
    try testing.expectEqualStrings(
        "chcp 65001 >nul && dir \"C:\\Program Files\"",
        result[2],
    );
}

test "execCommand windows: pwsh with -c short form wraps user script" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(
        arena.allocator(),
        "pwsh.exe -c Write-Host",
    );

    // `-c` is the unambiguous 2-letter short form of -Command; treat
    // it the same as the long form.
    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-c", result[1]);
    try testing.expect(std.mem.endsWith(u8, result[2], "; Write-Host"));
}

test "execCommand windows: pwsh with -File leaves args untouched" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(
        arena.allocator(),
        "pwsh.exe -File C:\\scripts\\my.ps1",
    );

    // We can't safely rewrite a user-supplied script file. Leave argv
    // intact; a log.debug tells operators why the preamble was
    // skipped. Users who need UTF-8 in -File scripts set
    // [Console]::OutputEncoding themselves (documented in issue # 299).
    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-File", result[1]);
    try testing.expectEqualStrings("C:\\scripts\\my.ps1", result[2]);
}

test "execCommand windows: pwsh -EncodedCommand gets preamble injected end-to-end" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    // The value is base64 UTF-16LE of "Write-Host hi" (no first-statement
    // construct). Use an explicit .always policy so this is deterministic
    // regardless of the test host's ANSI codepage.
    const result = try execCommand(
        alloc,
        .{ .shell = "pwsh.exe -EncodedCommand VwByAGkAdABlAC0ASABvAHMAdAAgAGgAaQA=" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .always,
    );

    // The .shell path tokenizes, spawns pwsh directly, and routes through
    // maybeInjectUtf8Preamble -> wrapEncodedCommand: the encoded value is
    // re-encoded as prefix ++ original script.
    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-EncodedCommand", result[1]);
    const decoded = try utf8FromUtf16LeBase64(alloc, result[2]);
    const prefix = internal_os.windows_shell.Preamble.pwsh.prefix();
    try testing.expect(std.mem.startsWith(u8, decoded, prefix));
    try testing.expect(std.mem.endsWith(u8, decoded, "Write-Host hi"));
}

test "execCommand windows: pwsh with trailing -Command (no script) leaves args untouched" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(
        arena.allocator(),
        "pwsh.exe -Command",
    );

    // No tail after `-Command`: fabricating one would change whatever
    // pwsh does by default with a bare flag. Keep argv intact.
    try testing.expectEqual(@as(usize, 2), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-Command", result[1]);
}

test "writeQuotedArg: MS C runtime quoting edge cases" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const Case = struct { in: []const u8, out: []const u8 };
    const cases: []const Case = &.{
        // No whitespace or quotes: pass through untouched.
        .{ .in = "hello", .out = "hello" },
        // Embedded space: wrap whole arg in quotes.
        .{ .in = "C:\\Program Files", .out = "\"C:\\Program Files\"" },
        // Embedded tab/newline: still quotes.
        .{ .in = "a\tb", .out = "\"a\tb\"" },
        // Embedded quote: escape as \", no outer backslash needed.
        .{ .in = "a\"b", .out = "\"a\\\"b\"" },
        // Trailing backslash in a quoted arg: double the backslash run
        // so the closing quote isn't escaped.
        .{ .in = "foo bar\\", .out = "\"foo bar\\\\\"" },
        // Backslash-quote sequence: 2n+1 backslashes before the quote.
        .{ .in = "a\\\"b c", .out = "\"a\\\\\\\"b c\"" },
    };

    for (cases) |c| {
        var buf: std.Io.Writer.Allocating = .init(alloc);
        defer buf.deinit();
        try writeQuotedArg(&buf.writer, c.in);
        try testing.expectEqualStrings(c.out, buf.writer.buffered());
    }
}

test "execCommand windows: cmd with trailing /c (no script) leaves args untouched" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(arena.allocator(), "cmd.exe /c");

    try testing.expectEqual(@as(usize, 2), result.len);
    try testing.expectEqualStrings("cmd.exe", result[0]);
    try testing.expectEqualStrings("/c", result[1]);
}

test "execCommand windows: pwsh -Command with param() is left untouched" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    // Prepending `[Console]::...; ` would demote the param() block out
    // of first-statement position and break pwsh's parse. The skip
    // branch is preferred: user keeps their script, we log why the
    // preamble was not injected.
    //
    // We build argv directly because the string-shell path triggers
    // `windowsShellNeedsCmdWrapping` on `(` / `)` and would re-route
    // through cmd.exe, which is a separate code path with its own
    // chcp prepend (covered by other tests).
    const result = try execCommand(
        arena.allocator(),
        .{ .direct = &.{
            "pwsh.exe",
            "-Command",
            "param($x) Write-Host $x",
        } },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .auto,
    );

    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-Command", result[1]);
    try testing.expectEqualStrings("param($x) Write-Host $x", result[2]);
}

test "execCommand windows: pwsh -Command with #requires is left untouched" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    // `#requires -Version 7` must be the first non-comment/non-blank
    // line; prepending `[Console]::...; ` silently demotes it to a
    // plain comment.
    const result = try testExecWindowsShell(
        arena.allocator(),
        "pwsh.exe -Command \"#requires -Version 7\"",
    );

    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-Command", result[1]);
    try testing.expectEqualStrings("#requires -Version 7", result[2]);
}

test "execCommand windows: pwsh -Command with leading scriptblock is left untouched" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    // `-Command "{ Get-Date }"` is unusual but legal; prepending our
    // setup would turn the scriptblock into a discarded literal value
    // rather than something that executes.
    const result = try testExecWindowsShell(
        arena.allocator(),
        "pwsh.exe -Command \"{ Get-Date }\"",
    );

    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-Command", result[1]);
    try testing.expectEqualStrings("{ Get-Date }", result[2]);
}

test "execCommand windows: pwsh -Command with leading whitespace + param is left untouched" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    // Detection must trim leading whitespace, otherwise a formatted
    // `-Command "  param(...)"` silently regresses. Use direct argv
    // so `(`/`)` don't route through the cmd-wrap path.
    const result = try execCommand(
        arena.allocator(),
        .{ .direct = &.{
            "pwsh.exe",
            "-Command",
            "  param($x) 'ok'",
        } },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .auto,
    );

    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-Command", result[1]);
    try testing.expectEqualStrings("  param($x) 'ok'", result[2]);
}

test "execCommand windows: pwsh with benign args gets appended preamble" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(arena.allocator(), "pwsh.exe -NoProfile");

    // -NoProfile doesn't conflict with -Command, so we append the
    // preamble after the user's flags.
    try testing.expectEqual(@as(usize, 5), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-NoProfile", result[1]);
    try testing.expectEqualStrings("-NoExit", result[2]);
    try testing.expectEqualStrings("-Command", result[3]);
}

test "execCommand windows: cmd-wrap path (pipes) gets chcp prepended to /C script" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(arena.allocator(), "dir | findstr foo");

    // Metachars force a cmd.exe /C wrap (existing behavior). The
    // preamble goes *inside* the /C string so the whole pipeline runs
    // in the same UTF-8 codepage.
    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expect(std.mem.endsWith(u8, result[0], "cmd.exe"));
    try testing.expectEqualStrings("/C", result[1]);
    try testing.expectEqualStrings("chcp 65001 >nul && dir | findstr foo", result[2]);
}

test "execCommand windows: direct command for cmd.exe gets cmd preamble" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();

    const result = try execCommand(
        arena.allocator(),
        .{ .direct = &.{"cmd.exe"} },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .auto,
    );

    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings("cmd.exe", result[0]);
    try testing.expectEqualStrings("/K", result[1]);
    try testing.expectEqualStrings("chcp 65001 >nul", result[2]);
}

test "execCommand windows: direct command for pwsh under never gets pwsh preamble" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();

    const result = try execCommand(
        arena.allocator(),
        .{ .direct = &.{ "pwsh.exe", "-NoLogo" } },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .auto,
    );

    try testing.expectEqual(@as(usize, 5), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-NoLogo", result[1]);
    try testing.expectEqualStrings("-NoExit", result[2]);
    try testing.expectEqualStrings("-Command", result[3]);
}

test "execCommand windows: pwsh.exe under auto/auto gets pwsh preamble (regression for #341)" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    // Under ConPTY (the sole Windows transport) pwsh still needs the
    // preamble (see maybeInjectUtf8Preamble), so the user sees
    // `chcp 65001` + `[Console]::OutputEncoding = UTF8` applied at
    // startup.
    const result = try execCommand(
        alloc,
        .{ .shell = "pwsh.exe" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .auto, // utf8_console - non-CJK ACP resolves to .always
    );

    // Expect: ["pwsh.exe", "-NoExit", "-Command", "<UTF-8 setup script>"]
    // The exact suffix string is owned by windows_shell.suffix(.pwsh);
    // assert structural shape, not the literal script text (which may
    // evolve).
    try testing.expect(result.len >= 4);
    try testing.expectEqualStrings(result[0], "pwsh.exe");
    try testing.expectEqualStrings(result[1], "-NoExit");
    try testing.expectEqualStrings(result[2], "-Command");
    // Script text contains both chcp and OutputEncoding setup.
    try testing.expect(std.mem.indexOf(u8, result[3], "chcp") != null);
    try testing.expect(std.mem.indexOf(u8, result[3], "OutputEncoding") != null);
}

test "execCommand windows: pwsh.exe under utf8-console=never returns args unchanged" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const result = try execCommand(
        alloc,
        .{ .shell = "pwsh.exe" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .never, // utf8_console - kill switch
    );

    try testing.expectEqual(@as(usize, 1), result.len);
    try testing.expectEqualStrings(result[0], "pwsh.exe");
}

test "execCommand windows: bash.exe never gets a preamble (Preamble.none guard)" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const result = try execCommand(
        alloc,
        .{ .shell = "bash.exe" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .always, // even with always, bash should be untouched
    );

    try testing.expectEqual(@as(usize, 1), result.len);
    try testing.expectEqualStrings(result[0], "bash.exe");
}

// --- test helpers: UTF-16LE <-> base64, mirroring PowerShell's
// [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(...)) ---

fn utf16LeBase64FromUtf8(alloc: Allocator, s: []const u8) ![:0]u8 {
    const u16s = try std.unicode.utf8ToUtf16LeAlloc(alloc, s);
    defer alloc.free(u16s);
    const bytes = try alloc.alloc(u8, u16s.len * 2);
    defer alloc.free(bytes);
    for (u16s, 0..) |u, i| std.mem.writeInt(u16, bytes[i * 2 ..][0..2], u, .little);
    const enc = std.base64.standard.Encoder;
    const out = try alloc.allocSentinel(u8, enc.calcSize(bytes.len), 0);
    _ = enc.encode(out, bytes);
    return out;
}

fn utf8FromUtf16LeBase64(alloc: Allocator, b64: []const u8) ![]u8 {
    const dec = std.base64.standard.Decoder;
    const n = try dec.calcSizeForSlice(b64);
    const bytes = try alloc.alloc(u8, n);
    defer alloc.free(bytes);
    try dec.decode(bytes, b64);
    const u16s = try alloc.alloc(u16, bytes.len / 2);
    defer alloc.free(u16s);
    for (u16s, 0..) |*u, i| u.* = std.mem.readInt(u16, bytes[i * 2 ..][0..2], .little);
    return std.unicode.utf16LeToUtf8Alloc(alloc, u16s);
}

test "maybeInjectUtf8Preamble windows: -EncodedCommand gets preamble injected" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const script_utf8 = "Write-Output 'hi'";
    const b64 = try utf16LeBase64FromUtf8(alloc, script_utf8);
    const args: []const [:0]const u8 = &.{ "pwsh", "-EncodedCommand", b64 };
    const out = try maybeInjectUtf8Preamble(alloc, args, .always);

    try testing.expectEqual(@as(usize, 3), out.len);
    try testing.expectEqualStrings("-EncodedCommand", out[1]);

    // The re-encoded value must decode to prefix ++ original script.
    const decoded = try utf8FromUtf16LeBase64(alloc, out[2]);
    const prefix = internal_os.windows_shell.Preamble.pwsh.prefix();
    try testing.expect(std.mem.startsWith(u8, decoded, prefix));
    try testing.expect(std.mem.endsWith(u8, decoded, script_utf8));
}

test "maybeInjectUtf8Preamble windows: -EncodedCommand with param() is skipped" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const b64 = try utf16LeBase64FromUtf8(alloc, "param($x)\nWrite-Output $x");
    const args: []const [:0]const u8 = &.{ "pwsh", "-EncodedCommand", b64 };
    const out = try maybeInjectUtf8Preamble(alloc, args, .always);

    // First-statement construct: skipped, value unchanged.
    try testing.expectEqualStrings(b64, out[2]);
}

test "maybeInjectUtf8Preamble windows: invalid base64 -EncodedCommand is skipped" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const args: []const [:0]const u8 = &.{ "pwsh", "-EncodedCommand", "not valid base64!!!" };
    const out = try maybeInjectUtf8Preamble(alloc, args, .always);
    try testing.expectEqualStrings("not valid base64!!!", out[2]);
}

test "maybeInjectUtf8Preamble windows: -EncodedCommand skipped when policy never" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const b64 = try utf16LeBase64FromUtf8(alloc, "Write-Output 'hi'");
    const args: []const [:0]const u8 = &.{ "pwsh", "-EncodedCommand", b64 };
    const out = try maybeInjectUtf8Preamble(alloc, args, .never);
    try testing.expectEqualStrings(b64, out[2]);
}

/// Like `utf16LeBase64FromUtf8` but emits a leading UTF-16 BOM (U+FEFF),
/// matching encoders that prepend one to `-EncodedCommand` scripts.
fn utf16LeBase64FromUtf8WithBom(alloc: Allocator, s: []const u8) ![:0]u8 {
    const u16s = try std.unicode.utf8ToUtf16LeAlloc(alloc, s);
    defer alloc.free(u16s);
    const bytes = try alloc.alloc(u8, (1 + u16s.len) * 2);
    defer alloc.free(bytes);
    std.mem.writeInt(u16, bytes[0..2], 0xFEFF, .little);
    for (u16s, 0..) |u, i| std.mem.writeInt(u16, bytes[(1 + i) * 2 ..][0..2], u, .little);
    const enc = std.base64.standard.Encoder;
    const out = try alloc.allocSentinel(u8, enc.calcSize(bytes.len), 0);
    _ = enc.encode(out, bytes);
    return out;
}

test "maybeInjectUtf8Preamble windows: -EncodedCommand with leading BOM preserves BOM and injects" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const b64 = try utf16LeBase64FromUtf8WithBom(alloc, "Write-Output 'hi'");
    const args: []const [:0]const u8 = &.{ "pwsh", "-EncodedCommand", b64 };
    const out = try maybeInjectUtf8Preamble(alloc, args, .always);

    // Result keeps exactly one leading BOM, then the preamble, then the
    // original script (the script's own BOM is not duplicated mid-stream).
    const decoded = try utf8FromUtf16LeBase64(alloc, out[2]);
    const prefix = internal_os.windows_shell.Preamble.pwsh.prefix();
    const expected_head = try std.fmt.allocPrint(alloc, "\u{FEFF}{s}", .{prefix});
    try testing.expect(std.mem.startsWith(u8, decoded, expected_head));
    try testing.expect(std.mem.endsWith(u8, decoded, "Write-Output 'hi'"));
    try testing.expectEqual(@as(usize, 1), std.mem.count(u8, decoded, "\u{FEFF}"));
}

test "maybeInjectUtf8Preamble windows: -EncodedCommand with BOM + param() is skipped" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    // The BOM must not hide the leading param() from the first-statement
    // guard, so this is skipped (value unchanged) rather than demoted.
    const b64 = try utf16LeBase64FromUtf8WithBom(alloc, "param($x)\nWrite-Output $x");
    const args: []const [:0]const u8 = &.{ "pwsh", "-EncodedCommand", b64 };
    const out = try maybeInjectUtf8Preamble(alloc, args, .always);
    try testing.expectEqualStrings(b64, out[2]);
}

test "maybeWrapGitBashWithWinpty windows: non-bash is left untouched" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    const args: []const [:0]const u8 = &.{"C:\\Windows\\System32\\cmd.exe"};
    const out = try maybeWrapGitBashWithWinpty(alloc, args);
    try testing.expectEqual(args.ptr, out.ptr); // unchanged slice
}

test "maybeWrapGitBashWithWinpty windows: relative bash is skipped (no accessAbsolute panic)" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    // A relative path-qualified bash passes the basename check but is not
    // absolute; it must short-circuit BEFORE accessAbsolute (which asserts
    // an absolute path) and return the args unchanged.
    for ([_][:0]const u8{ "bin\\bash.exe", ".\\bash.exe", "bash.exe" }) |arg| {
        const args: []const [:0]const u8 = &.{arg};
        const out = try maybeWrapGitBashWithWinpty(alloc, args);
        try testing.expectEqual(args.ptr, out.ptr);
    }
}

test "maybeWrapGitBashWithWinpty windows: absolute bash with adjacent winpty is wrapped" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    // Build a real bash.exe + adjacent winpty.exe under a tmp dir so the
    // accessAbsolute existence check passes, then assert winpty is prepended.
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.writeFile(.{ .sub_path = "bash.exe", .data = "" });
    try tmp.dir.writeFile(.{ .sub_path = "winpty.exe", .data = "" });
    const real = try tmp.dir.realpathAlloc(alloc, ".");
    const bash = try std.fmt.allocPrintSentinel(alloc, "{s}\\bash.exe", .{real}, 0);
    const winpty = try std.fmt.allocPrint(alloc, "{s}\\winpty.exe", .{real});

    const args: []const [:0]const u8 = &.{ bash, "--login" };
    const out = try maybeWrapGitBashWithWinpty(alloc, args);
    try testing.expectEqual(@as(usize, 3), out.len);
    try testing.expectEqualStrings(winpty, out[0]);
    try testing.expectEqualStrings(bash, out[1]);
    try testing.expectEqualStrings("--login", out[2]);
}
