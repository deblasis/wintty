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
const global = @import("../global.zig");
const xev = global.xev;
const apprt = @import("../apprt.zig");
const build_config = @import("../build_config.zig");
const configpkg = @import("../config.zig");
const crash = @import("../crash/main.zig");
const fastmem = @import("../fastmem.zig");
const internal_os = @import("../os/main.zig");
const renderer = @import("../renderer.zig");
const shell_integration = @import("shell_integration.zig");
const terminal = @import("../terminal/main.zig");
const termio = @import("../termio.zig");
const Command = @import("../Command.zig");
const SegmentedPool = @import("../datastruct/main.zig").SegmentedPool;
const ptypkg = @import("../pty.zig");
const Pty = ptypkg.Pty;
const EnvMap = std.process.Environ.Map;
const PasswdEntry = internal_os.passwd.Entry;
const windows = internal_os.windows;
const ProcessInfo = @import("../pty.zig").ProcessInfo;
const compat_fd = @import("../lib/compat/fd.zig");

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
        std.process.exit(1);
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
    const process_start: std.Io.Timestamp = .now(global.io(), .awake);

    // Create our pipe that we'll use to kill our read thread.
    // pipe[0] is the read end, pipe[1] is the write end.
    const pipe = try internal_os.pipe();
    errdefer _ = posix.system.close(pipe[0]);
    errdefer _ = posix.system.close(pipe[1]);

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
    read_thread.setName(global.io(), "io-reader") catch {};

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
    switch (posix.errno(posix.system.write(exec.read_thread_pipe, "x", 1))) {
        .SUCCESS => {},

        // EPIPE means that our read thread is closed already, which is
        // completely fine since that is what we were trying to achieve.
        .PIPE => {},

        else => |e| log.warn(
            "error writing to read thread quit pipe err=E{s}",
            .{@tagName(e)},
        ),
    }

    if (comptime builtin.os.tag == .windows) {
        // Interrupt the blocking read so the thread can see the quit message
        if (windows.exp.kernel32.CancelIoEx(exec.read_thread_fd, null) == windows.FALSE) {
            switch (windows.GetLastError()) {
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
    const runtime_ms: u64 = @max(
        0,
        execdata.start.untilNow(global.io(), .awake).toMilliseconds(),
    );
    log.debug("child process exited status={} runtime={}ms", .{ exit_code, runtime_ms });

    // We always notify the surface immediately that the child has
    // exited and some metadata about the exit.
    _ = td.surface_mailbox.push(.{
        .child_exited = .{
            .exit_code = exit_code,
            .runtime_ms = runtime_ms,
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
            td.renderer_state.mutex.lockUncancelable(global.io());
            defer td.renderer_state.mutex.unlock(global.io());
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
    start: std.Io.Timestamp,
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
        _ = posix.system.close(self.read_thread_pipe);

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

    /// Windows ConPTY transport mode. Resolved at spawn time against the
    /// shell classifier (see `resolveConptyMode`). Ignored on POSIX.
    conpty_mode: if (builtin.os.tag == .windows)
        configpkg.Config.ConptyMode
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

    /// Captured from Config.conpty_mode at init time; resolved against
    /// the shell classifier at spawn time. Ignored on POSIX.
    conpty_mode: if (builtin.os.tag == .windows)
        configpkg.Config.ConptyMode
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
            const exe_bin_path = exe_buf[0 .. std.process.executablePath(
                global.io(),
                &exe_buf,
            ) catch |err| {
                log.warn("failed to get ghostty exe path err={}", .{err});
                break :ghostty_path;
            }];
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
                    try appendEnv(alloc, path, exe_dir),
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
                    try appendEnv(
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
                    try appendEnvAlways(
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
        try env.put("TERM_PROGRAM", "ghostty");
        try env.put("TERM_PROGRAM_VERSION", build_config.version_string);

        // VTE_VERSION is set by gnome-terminal and other VTE-based terminals.
        // We don't want our child processes to think we're running under VTE.
        // This is not apprt-specific, so we do it here.
        _ = env.orderedRemove("VTE_VERSION");

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
                .elvish => .elvish,
                .fish => .fish,
                .nushell => .nushell,
                .zsh => .zsh,
            };

            const dir = cfg.resources_dir orelse {
                log.warn("no resources dir set, shell integration disabled", .{});
                break :shell default_shell_command;
            };

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

        // Build our args list
        const args: []const [:0]const u8 = execCommand(
            alloc,
            shell_command,
            internal_os.passwd,
            cfg.conpty_mode,
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

            .conpty_mode = cfg.conpty_mode,

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

        // This function is funny because on POSIX systems it can
        // fail in the forked process. This is flipped to true if
        // we're in an error state in the forked process (child
        // process).
        var in_child: bool = false;

        // Resolve the transport mode from config + shell classification.
        // Windows-only; POSIX ignores opts.mode.
        // args[0] is the shell executable path (bare basename or full path),
        // never a joined command line. resolveConptyMode relies on this -
        // windows_shell.classify does not split tokens on spaces.
        const mode: ptypkg.Mode = if (comptime builtin.os.tag == .windows)
            resolveConptyMode(self.conpty_mode, self.args[0])
        else
            .conpty;

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
                _ = posix.system.close(pty.slave);
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
                _ = posix.system.close(pty.slave);
            } else {
                // In bypass mode the child holds its own inherited dup of
                // `in_pipe_pty` / `out_pipe_pty`. Close our parent copies
                // so EOF propagates on `out_pipe` when the child exits.
                // In ConPTY mode the pseudoconsole owns those handles
                // internally and we keep them alive until `Pty.deinit`.
                if (mode == .bypass) {
                    _ = windows.exp.kernel32.CloseHandle(pty.in_pipe_pty);
                    _ = windows.exp.kernel32.CloseHandle(pty.out_pipe_pty);
                    pty.in_pipe_pty = windows.INVALID_HANDLE_VALUE;
                    pty.out_pipe_pty = windows.INVALID_HANDLE_VALUE;
                    // Keep `self.pty` in sync; `Pty.deinit` tolerates
                    // `INVALID_HANDLE_VALUE` via `CloseHandle` returning 0.
                    if (self.pty) |*sp| {
                        sp.in_pipe_pty = windows.INVALID_HANDLE_VALUE;
                        sp.out_pipe_pty = windows.INVALID_HANDLE_VALUE;
                    }
                }
            }

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
                const dev_null = try std.Io.Dir.cwd().openFile(
                    global.io(),
                    "/dev/null",
                    .{ .mode = .read_write },
                );
                defer dev_null.close(global.io());
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

            if (std.Io.Dir.cwd().access(global.io(), proposed, .{})) {
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

        // Build our subcommand. On Windows, stdio and `pseudo_console` are
        // wired differently per transport mode: ConPTY owns stdio via the
        // pseudoconsole handle; bypass mode feeds the child our raw pipe
        // ends and leaves `pseudo_console` null.
        var cmd: Command = .{
            .path = self.args[0],
            .args = self.args,
            .env = if (self.env) |*env| env else null,
            .cwd = cwd,
            .stdin = if (comptime builtin.os.tag == .windows)
                switch (mode) {
                    .bypass => .{ .handle = pty.in_pipe_pty },
                    .conpty => null,
                }
            else
                .{
                    .handle = pty.slave,
                    .flags = .{ .nonblocking = false },
                },
            .stdout = if (comptime builtin.os.tag == .windows)
                switch (mode) {
                    .bypass => .{ .handle = pty.out_pipe_pty },
                    .conpty => null,
                }
            else
                .{
                    .handle = pty.slave,
                    .flags = .{ .nonblocking = false },
                },
            .stderr = if (comptime builtin.os.tag == .windows)
                switch (mode) {
                    .bypass => .{ .handle = pty.out_pipe_pty },
                    .conpty => null,
                }
            else
                .{
                    .handle = pty.slave,
                    .flags = .{ .nonblocking = false },
                },
            .pseudo_console = if (comptime builtin.os.tag == .windows)
                switch (mode) {
                    .conpty => pty.pseudo_console,
                    .bypass => null,
                }
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
                    if (windows.exp.kernel32.TerminateProcess(pid, 0) == windows.FALSE) {
                        return windows.unexpectedError(windows.GetLastError());
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
            const res_pid = while (true) {
                const rc = posix.system.waitpid(pid, null, std.c.W.NOHANG);
                if (posix.errno(rc) == .INTR) continue;
                break rc;
            };
            log.debug("waitpid result={}", .{res_pid});
            if (res_pid != 0) break;
            try std.Io.sleep(global.io(), .fromMilliseconds(10), .awake);
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
                std.Io.sleep(global.io(), .fromMilliseconds(10), .awake) catch {};
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
        mutex: std.Io.Mutex = .init,

        /// Signaled when a batch is published or the gather stage is
        /// done. Waited on by the parse stage.
        batch_ready: std.Io.Condition = .init,

        /// Signaled when a batch has been consumed. Waited on by the
        /// gather stage when all buffers are in flight (backpressure).
        slot_free: std.Io.Condition = .init,

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
        defer _ = posix.system.close(quit);

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
        if (compat_fd.pipe2(.{
            .CLOEXEC = true,
            .NONBLOCK = true,
        })) |fds| {
            pipeline.idle_read_fd = fds[0];
            pipeline.idle_write_fd = fds[1];
        } else |err| {
            log.warn("read thread failed to create idle pipe err={}", .{err});
        }
        defer if (pipeline.idle_read_fd >= 0) {
            compat_fd.close(pipeline.idle_read_fd);
            compat_fd.close(pipeline.idle_write_fd);
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
            gather_thread.setName(global.io(), "io-gather") catch {};
        }

        // This thread is the parse stage. We consume batches in ring
        // order until the gather stage reports the stream is over and
        // the ring is drained.
        while (true) {
            const batch: []const u8 = batch: {
                pipeline.mutex.lockUncancelable(global.io());
                defer pipeline.mutex.unlock(global.io());
                while (pipeline.count == 0) {
                    if (pipeline.done) return;
                    pipeline.batch_ready.waitUncancelable(global.io(), &pipeline.mutex);
                }
                const slot = pipeline.tail;
                break :batch pipeline.bufs[slot][0..pipeline.lens[slot]];
            };

            // The batch buffer is owned by this stage until we advance
            // the tail below, so it is safe to read outside the lock.
            io.processOutput(batch);

            {
                pipeline.mutex.lockUncancelable(global.io());
                pipeline.tail = (pipeline.tail + 1) % buffer_count;
                pipeline.count -= 1;
                const wake = pipeline.count == 0 and
                    pipeline.bridging and
                    pipeline.idle_write_fd >= 0;
                pipeline.mutex.unlock(global.io());
                pipeline.slot_free.signal(global.io());

                // We ran out of batches while the gather stage is
                // bridging a refill gap: interrupt its poll so it
                // delivers what it has instead of sleeping out the
                // timeout while we sit idle.
                if (wake) {
                    _ = posix.system.write(pipeline.idle_write_fd, "i", 1);
                }
            }

            // Batch boundary: hand the renderer state mutex off if
            // the renderer is waiting. See renderer.State.lockDemand.
            io.renderer_state.yieldToDemand(global.io());
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
            pipeline.mutex.lockUncancelable(global.io());
            pipeline.done = true;
            pipeline.mutex.unlock(global.io());
            pipeline.batch_ready.signal(global.io());
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
                pipeline.mutex.lockUncancelable(global.io());
                defer pipeline.mutex.unlock(global.io());
                while (pipeline.count == buffer_count) {
                    pipeline.slot_free.waitUncancelable(global.io(), &pipeline.mutex);
                }
                break :buf &pipeline.bufs[pipeline.head];
            };

            var total: usize = 0;
            var bridge_start: ?std.Io.Timestamp = null;
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
                        const now = std.Io.Timestamp.now(global.io(), .awake);
                        if (bridge_start) |start| {
                            if (start.durationTo(now).toNanoseconds() >= gather_budget_ns)
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
                            pipeline.mutex.lockUncancelable(global.io());
                            defer pipeline.mutex.unlock(global.io());
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
                pipeline.mutex.lockUncancelable(global.io());
                pipeline.lens[pipeline.head] = total;
                pipeline.head = (pipeline.head + 1) % buffer_count;
                pipeline.count += 1;
                pipeline.mutex.unlock(global.io());
                pipeline.batch_ready.signal(global.io());
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
        pipeline.mutex.lockUncancelable(global.io());
        defer pipeline.mutex.unlock(global.io());
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
        const flags = posix.system.fcntl(fd, posix.F.GETFL);
        if (flags == -1) {
            log.warn("read thread failed to get flags err=E{s}", .{@tagName(posix.errno(-1))});
            return false;
        }

        switch (posix.errno(posix.system.fcntl(
            fd,
            posix.F.SETFL,
            flags | @as(u32, @bitCast(posix.O{ .NONBLOCK = true })),
        ))) {
            .SUCCESS => {},
            else => |e| {
                log.warn("read thread failed to set flags err=E{s}", .{@tagName(e)});
                return false;
            },
        }
        return true;
    }

    fn threadMainWindows(fd: posix.fd_t, io: *termio.Termio, quit: posix.fd_t) void {
        // Always close our end of the pipe when we exit.
        defer _ = posix.system.close(quit);

        // Setup our crash metadata
        crash.sentry.thread_state = .{
            .type = .io,
            .surface = io.surface_mailbox.surface,
        };
        defer crash.sentry.thread_state = null;

        var buf: [1024]u8 = undefined;
        while (true) {
            while (true) {
                var n: windows.DWORD = 0;
                if (windows.exp.kernel32.ReadFile(fd, &buf, buf.len, &n, null) == windows.FALSE) {
                    const err = windows.GetLastError();
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
                io.renderer_state.yieldToDemand(global.io());
            }

            var quit_bytes: windows.DWORD = 0;
            if (windows.exp.kernel32.PeekNamedPipe(quit, null, 0, null, &quit_bytes, null) == windows.FALSE) {
                const err = windows.GetLastError();
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
    /// Configured transport mode; used only on Windows to decide whether
    /// to inject the ConPTY UTF-8 preamble (# 302). Ignored on other
    /// platforms.
    conpty_mode: configpkg.Config.ConptyMode,
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
            var dir = std.Io.Dir.openDirAbsolute(global.io(), home, .{}) catch |err| {
                log.warn(
                    "failed to open home dir, not checking for hushlogin err={}",
                    .{err},
                );
                break :hush false;
            };
            defer dir.close(global.io());

            break :hush if (dir.access(global.io(), ".hushlogin", .{})) true else |_| false;
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
        .direct => direct: {
            const cloned = (try command.clone(alloc)).direct;
            if (comptime builtin.os.tag == .windows) {
                break :direct try maybeInjectUtf8Preamble(
                    alloc,
                    cloned,
                    conpty_mode,
                );
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
                // classification that looks at args[0] (e.g. the
                // ConPTY-bypass detector for # 112) sees the wrapper
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
                        if (global.environ().getAlloc(alloc, "COMSPEC")) |comspec| {
                            args.items[0] = try alloc.dupeZ(u8, comspec);
                        } else |_| {}
                    }

                    const direct_args = try args.toOwnedSlice(alloc);
                    break :shell try maybeInjectUtf8Preamble(
                        alloc,
                        direct_args,
                        conpty_mode,
                    );
                }

                // Command contains cmd.exe metacharacters (or parsing
                // produced no tokens). Let cmd.exe handle it.
                var args: std.ArrayList([:0]const u8) = try .initCapacity(alloc, 4);
                defer args.deinit(alloc);

                // Note we don't free any of the memory below since it is
                // allocated in the arena.
                const windir = global.environ().getAlloc(
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

                // cmd.exe is always `console_api` so `auto` + `never`
                // both pick ConPTY here, and `always` picks the raw-pipe
                // bypass (which already gets UTF-8 from PR # 301).
                // Prepend `chcp 65001 >nul && ` to the /C script in the
                // ConPTY cases so the whole pipeline runs UTF-8.
                const mode = resolveConptyMode(conpty_mode, cmd);
                const script: [:0]const u8 = if (mode == .conpty)
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

/// Append a value to an environment variable such as PATH.
/// The returned value is always allocated so it must be freed.
fn appendEnv(
    alloc: Allocator,
    current: []const u8,
    value: []const u8,
) Allocator.Error![]u8 {
    // If there is no prior value, we return it as-is
    if (current.len == 0) return try alloc.dupe(u8, value);

    // Otherwise we must prefix.
    return try appendEnvAlways(alloc, current, value);
}

/// Always append value to environment, even when it is empty.
/// This is useful because some env vars (like MANPATH) want there
/// to be an empty prefix to preserve existing values.
///
/// The returned value is always allocated so it must be freed.
fn appendEnvAlways(
    alloc: Allocator,
    current: []const u8,
    value: []const u8,
) Allocator.Error![]u8 {
    return try std.fmt.allocPrint(alloc, "{s}{c}{s}", .{
        current,
        std.fs.path.delimiter,
        value,
    });
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

/// Windows-only. If the configured transport will resolve to ConPTY
/// for this argv and the shell is known to benefit from a UTF-8
/// preamble (# 302), return a new argv with the preamble suffix
/// appended. Returns `args` unchanged otherwise.
///
/// Why the injection happens here and not later: we need to emit the
/// preamble *argv-level*, before the shell has read any input. The
/// raw-pipe bypass already gets UTF-8 from PR # 301 (parent console
/// inheritance), but CreatePseudoConsole spawns its own conhost that
/// starts at the system OEM CP regardless of what the parent has set.
///
/// Callers own both the input `args` and the returned slice; when no
/// injection is needed the returned slice aliases `args` (no copy).
fn maybeInjectUtf8Preamble(
    alloc: Allocator,
    args: []const [:0]const u8,
    conpty_mode: configpkg.Config.ConptyMode,
) Allocator.Error![]const [:0]const u8 {
    if (comptime builtin.os.tag != .windows) return args;
    if (args.len == 0) return args;

    const mode = resolveConptyMode(conpty_mode, args[0]);
    if (mode != .conpty) return args;

    const preamble = internal_os.windows_shell.utf8Preamble(args[0]);
    if (preamble == .none) return args;
    if (preambleArgsConflict(args, preamble)) return args;

    const suffix = preamble.suffix();
    const out = try alloc.alloc([:0]const u8, args.len + suffix.len);
    @memcpy(out[0..args.len], args);
    // The suffix elements are `.rodata` string literals; they outlive
    // any arena and spawning reads them during CreateProcess. No dupe
    // needed, matching how `"/C"` is appended inline elsewhere here.
    @memcpy(out[args.len..], suffix);
    return out;
}

/// Returns true if the existing argv already contains a flag that
/// would conflict with our preamble suffix. We bail out in that case
/// rather than silently duplicating `-Command` / `/C` and breaking
/// whatever the user configured.
fn preambleArgsConflict(
    args: []const [:0]const u8,
    preamble: internal_os.windows_shell.Preamble,
) bool {
    if (args.len <= 1) return false;
    return switch (preamble) {
        .none => false,
        .cmd => for (args[1..]) |arg| {
            // Only `/C` and `/K` consume "the rest of the command line"
            // and would swallow our preamble. Single-dash `-c` is not
            // recognized by cmd.exe.
            if (arg.len != 2 or arg[0] != '/') continue;
            const c = std.ascii.toLower(arg[1]);
            if (c == 'c' or c == 'k') break true;
        } else false,
        .pwsh => for (args[1..]) |arg| {
            if (pwshFlagConflicts(arg)) break true;
        } else false,
    };
}

/// Returns true if `arg` is a PowerShell flag whose presence would
/// conflict with appending `-NoExit -Command <setup>`. We detect the
/// flags by prefix because PowerShell accepts any unambiguous prefix
/// of a flag name (e.g. `-Com` for `-Command`).
fn pwshFlagConflicts(arg: []const u8) bool {
    if (arg.len < 2 or arg[0] != '-') return false;

    var buf: [32]u8 = undefined;
    const tail_len = arg.len - 1;
    // Anything longer than a real PS flag name cannot be a conflict.
    if (tail_len > buf.len) return false;
    const lower = std.ascii.lowerString(buf[0..tail_len], arg[1..]);

    // Exact matches for short forms and unambiguous 2-letter abbreviations.
    if (std.mem.eql(u8, lower, "c")) return true;
    if (std.mem.eql(u8, lower, "f")) return true;
    if (std.mem.eql(u8, lower, "ec")) return true;
    if (std.mem.eql(u8, lower, "enc")) return true;

    // Prefix matches (length >= 3 avoids colliding with -ConfigurationName
    // which also starts with -C, or -Format* which starts with -F).
    if (lower.len >= 3 and std.mem.startsWith(u8, "command", lower)) return true;
    if (lower.len >= 3 and std.mem.startsWith(u8, "file", lower)) return true;
    if (lower.len >= 4 and std.mem.startsWith(u8, "encodedcommand", lower)) return true;

    return false;
}

/// Get information about the process(es) running within the backend. Returns
/// `null` if there was an error getting the information or the information is
/// not available on a particular platform.
pub fn getProcessInfo(self: *Exec, comptime info: ProcessInfo) ?ProcessInfo.Type(info) {
    return self.subprocess.getProcessInfo(info);
}

/// Resolve the requested transport mode at spawn time. Windows-only
/// in effect; POSIX callers should pass `.conpty` and the value is
/// ignored by PosixPty.
///
/// - `.never` always picks ConPTY (classic behavior).
/// - `.always` always picks the raw-pipe bypass.
/// - `.auto` defers to the shell classifier: VT-aware shells use the
///   bypass, console-API shells and anything unrecognized fall back
///   to ConPTY so unknown programs keep the safe default.
///
/// `exe_path` must be a single executable path (basename or full path),
/// not a joined argv string. Callers holding argv should pass argv[0].
fn resolveConptyMode(
    cfg: configpkg.Config.ConptyMode,
    exe_path: []const u8,
) ptypkg.Mode {
    const resolved: ptypkg.Mode = switch (cfg) {
        .never => .conpty,
        .always => .bypass,
        .auto => switch (internal_os.windows_shell.classify(exe_path)) {
            .vt_aware => .bypass,
            .console_api, .unknown => .conpty,
        },
    };
    log_validate.info(
        "transport resolved: shell=\"{s}\" config_mode={s} resolved={s}",
        .{ exe_path, @tagName(cfg), @tagName(resolved) },
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

    const result = try execCommand(alloc, .{ .shell = "cmd.exe" }, struct {
        fn get(_: Allocator) !PasswdEntry {
            return .{};
        }
    });

    try testing.expectEqual(1, result.len);

    // Expect COMSPEC if available, otherwise the documented fallback.
    const expected = testing.environ.getAlloc(alloc, "COMSPEC") catch
        try alloc.dupe(u8, "C:\\Windows\\System32\\cmd.exe");
    try testing.expectEqualStrings(expected, result[0]);
}

test "execCommand windows: shell command, single token spawns directly" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    // always mode forces the bypass path so we get the bare argv without
    // UTF-8 preamble injection - the original behavior the test covers.
    const result = try execCommand(
        alloc,
        .{ .shell = "pwsh.exe" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .always,
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
        .always,
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
        .always,
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

    // always mode forces bypass so the cmd-wrap path does not pick up
    // the ConPTY UTF-8 preamble injection covered in its own test.
    const result = try execCommand(
        alloc,
        .{ .shell = "dir | findstr foo" },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        .always,
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
        .always,
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

    const result = try execCommand(alloc, .{ .direct = &.{
        "C:\\tools\\foo.exe",
        "arg with spaces",
    } }, struct {
        fn get(_: Allocator) !PasswdEntry {
            return .{};
        }
    });

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

test "resolveConptyMode: never forces conpty" {
    try std.testing.expectEqual(ptypkg.Mode.conpty, resolveConptyMode(.never, "pwsh.exe"));
    try std.testing.expectEqual(ptypkg.Mode.conpty, resolveConptyMode(.never, "cmd.exe"));
    try std.testing.expectEqual(ptypkg.Mode.conpty, resolveConptyMode(.never, "unknown.exe"));
}

test "resolveConptyMode: always forces bypass" {
    try std.testing.expectEqual(ptypkg.Mode.bypass, resolveConptyMode(.always, "pwsh.exe"));
    try std.testing.expectEqual(ptypkg.Mode.bypass, resolveConptyMode(.always, "cmd.exe"));
    try std.testing.expectEqual(ptypkg.Mode.bypass, resolveConptyMode(.always, "unknown.exe"));
}

test "resolveConptyMode: auto picks bypass for vt_aware" {
    try std.testing.expectEqual(ptypkg.Mode.bypass, resolveConptyMode(.auto, "pwsh.exe"));
    try std.testing.expectEqual(ptypkg.Mode.bypass, resolveConptyMode(.auto, "wsl.exe"));
    try std.testing.expectEqual(ptypkg.Mode.bypass, resolveConptyMode(.auto, "bash"));
}

test "resolveConptyMode: auto picks conpty for console_api" {
    try std.testing.expectEqual(ptypkg.Mode.conpty, resolveConptyMode(.auto, "cmd.exe"));
    try std.testing.expectEqual(ptypkg.Mode.conpty, resolveConptyMode(.auto, "powershell.exe"));
}

test "resolveConptyMode: auto picks conpty for unknown (safe default)" {
    try std.testing.expectEqual(ptypkg.Mode.conpty, resolveConptyMode(.auto, "my-custom.exe"));
    try std.testing.expectEqual(ptypkg.Mode.conpty, resolveConptyMode(.auto, ""));
}

test "resolveConptyMode: auto handles path-prefixed shell" {
    try std.testing.expectEqual(ptypkg.Mode.bypass, resolveConptyMode(.auto, "C:\\Program Files\\PowerShell\\7\\pwsh.exe"));
    try std.testing.expectEqual(ptypkg.Mode.conpty, resolveConptyMode(.auto, "C:\\Windows\\System32\\cmd.exe"));
    try std.testing.expectEqual(ptypkg.Mode.bypass, resolveConptyMode(.auto, "C:/Program Files/PowerShell/7/pwsh.exe"));
}

test "resolveConptyMode: auto handles quoted shell" {
    try std.testing.expectEqual(ptypkg.Mode.bypass, resolveConptyMode(.auto, "\"pwsh.exe\""));
    try std.testing.expectEqual(ptypkg.Mode.conpty, resolveConptyMode(.auto, "'cmd.exe'"));
}

// --- # 302 UTF-8 preamble injection tests -----------------------------------
//
// These check the argv that execCommand hands back when the resolved
// transport is ConPTY. The preamble's *content* is covered by
// `utf8Preamble` tests in os/windows_shell.zig; here we only assert
// the injection decision (when do we inject? on what shells?) and the
// argv shape (length + flag markers).

fn testExecWindowsShell(
    alloc: Allocator,
    shell: [:0]const u8,
    conpty_mode: configpkg.Config.ConptyMode,
) ![]const [:0]const u8 {
    return try execCommand(
        alloc,
        .{ .shell = shell },
        struct {
            fn get(_: Allocator) !PasswdEntry {
                return .{};
            }
        },
        conpty_mode,
    );
}

test "execCommand windows: cmd.exe under auto mode gets cmd preamble" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(arena.allocator(), "cmd.exe", .auto);

    // auto + cmd (console_api) → ConPTY → cmd preamble appended.
    try testing.expectEqual(@as(usize, 3), result.len);
    try testing.expectEqualStrings("cmd.exe", result[0]);
    try testing.expectEqualStrings("/K", result[1]);
    try testing.expectEqualStrings("chcp 65001 >nul", result[2]);
}

test "execCommand windows: pwsh under auto mode (bypass) has no preamble" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(arena.allocator(), "pwsh.exe", .auto);

    // auto + pwsh (vt_aware) → bypass → PR # 301's parent console CP
    // does the work. No preamble.
    try testing.expectEqual(@as(usize, 1), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
}

test "execCommand windows: pwsh under forced never mode gets pwsh preamble" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(arena.allocator(), "pwsh.exe", .never);

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
    const result = try testExecWindowsShell(arena.allocator(), "powershell.exe", .auto);

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
    const result = try testExecWindowsShell(arena.allocator(), "my-custom.exe", .never);

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
    const result = try testExecWindowsShell(arena.allocator(), "bash.exe", .never);

    // bash et al. don't care about the Windows console CP; they decode
    // their own output.
    try testing.expectEqual(@as(usize, 1), result.len);
    try testing.expectEqualStrings("bash.exe", result[0]);
}

test "execCommand windows: always mode (bypass) never injects preamble" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();

    const cmd_result = try testExecWindowsShell(arena.allocator(), "cmd.exe", .always);
    try testing.expectEqual(@as(usize, 1), cmd_result.len);
    try testing.expectEqualStrings("cmd.exe", cmd_result[0]);

    const pwsh_result = try testExecWindowsShell(arena.allocator(), "pwsh.exe", .always);
    try testing.expectEqual(@as(usize, 1), pwsh_result.len);
    try testing.expectEqualStrings("pwsh.exe", pwsh_result[0]);
}

test "execCommand windows: cmd with existing /c arg skips preamble" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(arena.allocator(), "cmd.exe /c echo hi", .auto);

    // User's `/c` already consumes "the rest of the command line";
    // appending `/K chcp ...` after it would either be ignored by cmd
    // or parsed as part of the user's echo, so we skip.
    try testing.expectEqual(@as(usize, 4), result.len);
    try testing.expectEqualStrings("cmd.exe", result[0]);
    try testing.expectEqualStrings("/c", result[1]);
    try testing.expectEqualStrings("echo", result[2]);
    try testing.expectEqualStrings("hi", result[3]);
}

test "execCommand windows: pwsh with existing -Command skips preamble" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(
        arena.allocator(),
        "pwsh.exe -NoProfile -Command Write-Host",
        .never,
    );

    // Duplicating -Command confuses pwsh's arg parser; let the user's
    // explicit script run as configured.
    try testing.expectEqual(@as(usize, 4), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-NoProfile", result[1]);
    try testing.expectEqualStrings("-Command", result[2]);
    try testing.expectEqualStrings("Write-Host", result[3]);
}

test "execCommand windows: pwsh with benign args gets appended preamble" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const result = try testExecWindowsShell(arena.allocator(), "pwsh.exe -NoProfile", .never);

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
    const result = try testExecWindowsShell(arena.allocator(), "dir | findstr foo", .auto);

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
        .never,
    );

    try testing.expectEqual(@as(usize, 5), result.len);
    try testing.expectEqualStrings("pwsh.exe", result[0]);
    try testing.expectEqualStrings("-NoLogo", result[1]);
    try testing.expectEqualStrings("-NoExit", result[2]);
    try testing.expectEqualStrings("-Command", result[3]);
}
