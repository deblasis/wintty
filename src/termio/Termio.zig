//! Primary terminal IO ("termio") state. This maintains the terminal state,
//! pty, subprocess, etc. This is flexible enough to be used in environments
//! that don't have a pty and simply provides the input/output using raw
//! bytes.
pub const Termio = @This();

const std = @import("std");
const assert = @import("../quirks.zig").inlineAssert;
const Allocator = std.mem.Allocator;
const ArenaAllocator = std.heap.ArenaAllocator;
const EnvMap = std.process.Environ.Map;
const posix = std.posix;
const termio = @import("../termio.zig");
const StreamHandler = @import("stream_handler.zig").StreamHandler;
const terminalpkg = @import("../terminal/main.zig");
const snapshotpkg = @import("../terminal/snapshot/snapshot.zig");
const lz4 = @import("../terminal/compress/lz4.zig");
const global = @import("../global.zig");
const xev = global.xev;
const renderer = @import("../renderer.zig");
const apprt = @import("../apprt.zig");
const internal_os = @import("../os/main.zig");
const windows = internal_os.windows;
const configpkg = @import("../config.zig");
const ProcessInfo = @import("../pty.zig").ProcessInfo;
const compat_file = @import("../lib/compat/file.zig");

const log = std.log.scoped(.io_exec);

/// Mutex state argument for queueMessage.
pub const MutexState = enum { locked, unlocked };

/// Cap on the replayable parse state the stream's continuation tracker
/// may retain. Sized to the allocating OSC class's own ceiling class --
/// a truncated OSC 52-family sequence is the realistic worst case a
/// dormant tab would otherwise have to drop.
const continuation_max_bytes = 1024 * 1024;

/// Tier C dormancy: the terminal torn down into snapshot bytes. The
/// snapshot is the in-tree GHOSTSNP stream wrapped in one raw LZ4 block
/// (the v1 stream is an uncompressed logical encoding -- unwrapped, a
/// dormant heavy session would carry more than its live #1039-compressed
/// pages ever did). plain_len rides along because the LZ4 decoder needs
/// an exactly-sized destination. Guarded by renderer_state.mutex.
const DormantSnapshot = struct {
    compressed: []u8,
    plain_len: usize,

    /// Presentation flags the v1 snapshot does not carry (they are
    /// caller-supplied by design). Captured at teardown so the wake can
    /// put them back: without this, a surface that went dormant hidden
    /// rebuilds with the default (visible), and every later dormancy is
    /// refused for "surface visible" by a fact nobody observes.
    visible: bool,
    focused: bool,
};

/// Allocator
alloc: Allocator,

/// This is the implementation responsible for io.
backend: termio.Backend,

/// The derived configuration for this termio implementation.
config: DerivedConfig,

/// The terminal emulator internal state. This is the abstract "terminal"
/// that manages input, grid updating, etc. and is renderer-agnostic. It
/// just stores internal state about a grid.
terminal: terminalpkg.Terminal,

/// The shared render state
renderer_state: *renderer.State,

/// A handle to wake up the renderer. This hints to the renderer that
/// a repaint should happen. See termio.Options for why this is a pointer.
renderer_wakeup: *xev.Async,

/// The renderer's published visibility, if the embedder shared it. See
/// termio.Options for the semantics; null means never drop a wake.
renderer_visible: ?*const std.atomic.Value(bool),

/// The mailbox for notifying the renderer of things.
renderer_mailbox: *renderer.Thread.Mailbox,

/// The mailbox for communicating with the surface.
surface_mailbox: apprt.surface.Mailbox,

/// The cached size info
size: renderer.Size,

/// The mailbox implementation to use.
mailbox: termio.Mailbox,

/// Bounds the data queued to the pty. The backend accounts for its
/// writes here and the stream handler consults it before queueing a
/// terminal reply.
write_limit: termio.WriteLimit = .{},

/// The stream parser. This parses the stream of escape codes and so on
/// from the child process and calls callbacks in the stream handler.
terminal_stream: StreamHandler.Stream,

/// Non-null while the terminal field is torn down into its dormant
/// snapshot; see DormantSnapshot. Everything that can reach the
/// terminal takes renderer_state.mutex first; the shared atomic on
/// renderer_state is what lock-free readers check instead.
dormant_snapshot: ?DormantSnapshot = null,

/// Last time the cursor was reset. This is used to prevent message
/// flooding with cursor resets.
last_cursor_reset: ?std.Io.Timestamp = null,

/// State we have for thread enter. This may be null if we don't need
/// to keep track of any state or if its already been freed.
thread_enter_state: ?*ThreadEnterState = null,

/// The state we need to keep around only until we enter the IO
/// thread. Then we can throw it all away.
const ThreadEnterState = struct {
    arena: ArenaAllocator,

    /// Initial input to send to the subprocess after starting. This
    /// memory is freed once the subprocess start is attempted, even
    /// if it fails, because Exec only starts once.
    input: configpkg.io.RepeatableReadableIO,

    pub fn create(
        alloc: Allocator,
        config: *const configpkg.Config,
    ) !?*ThreadEnterState {
        // If we have no input then we have no thread enter state
        if (config.input.list.items.len == 0) return null;

        // Create our arena allocator
        var arena = ArenaAllocator.init(alloc);
        errdefer arena.deinit();
        const arena_alloc = arena.allocator();

        // Allocate our ThreadEnterState
        const ptr = try arena_alloc.create(ThreadEnterState);

        // Copy the input from the config
        const input = try config.input.cloneParsed(arena_alloc);

        // Return the initialized state
        ptr.* = .{
            .arena = arena,
            .input = input,
        };
        return ptr;
    }

    pub fn destroy(self: *ThreadEnterState) void {
        self.arena.deinit();
    }

    /// Prepare the inputs for use. Allocations happen on the arena.
    pub fn prepareInput(
        self: *ThreadEnterState,
    ) (Allocator.Error || error{InputNotFound})![]const Input {
        const alloc = self.arena.allocator();

        var inputs: std.ArrayList(Input) = try .initCapacity(
            alloc,
            self.input.list.items.len,
        );
        errdefer for (inputs.items) |item| item.deinit();

        for (self.input.list.items) |item| {
            inputs.appendAssumeCapacity(switch (item) {
                .raw => |v| .{ .string = v },
                .path => |path| file: {
                    const f = std.Io.Dir.cwd().openFile(
                        global.io(),
                        path,
                        .{},
                    ) catch |err| {
                        log.warn("failed to open input file={s} err={}", .{
                            path,
                            err,
                        });
                        return error.InputNotFound;
                    };

                    break :file .{ .file = f };
                },
            });
        }

        return inputs.items;
    }

    const Input = union(enum) {
        string: []const u8,
        file: std.Io.File,

        fn deinit(self: Input) void {
            switch (self) {
                .string => {},
                .file => |f| f.close(global.io()),
            }
        }
    };
};

/// The configuration for this IO that is derived from the main
/// configuration. This must be exported so that we don't need to
/// pass around Config pointers which makes memory management a pain.
pub const DerivedConfig = struct {
    arena: ArenaAllocator,

    palette: terminalpkg.color.Palette,
    image_storage_limit: usize,
    cursor_style: terminalpkg.CursorStyle,
    cursor_blink: ?bool,
    cursor_color: ?configpkg.Config.TerminalColor,
    foreground: configpkg.Config.Color,
    background: configpkg.Config.Color,
    osc_color_report_format: configpkg.Config.OSCColorReportFormat,
    clipboard_write: configpkg.ClipboardAccess,
    clipboard_write_limit: usize,
    enquiry_response: []const u8,
    conditional_state: configpkg.ConditionalState,

    pub fn init(
        alloc_gpa: Allocator,
        config: *const configpkg.Config,
    ) !DerivedConfig {
        var arena = ArenaAllocator.init(alloc_gpa);
        errdefer arena.deinit();
        const alloc = arena.allocator();

        const palette: terminalpkg.color.Palette = palette: {
            if (config.@"palette-generate") generate: {
                if (config.palette.mask.findFirstSet() == null) {
                    // If the user didn't set any values manually, then
                    // we're using the default palette and we don't need
                    // to apply the generation code to it.
                    break :generate;
                }

                break :palette terminalpkg.color.generate256Color(config.palette.value, config.palette.mask, config.background.toTerminalRGB(), config.foreground.toTerminalRGB(), config.@"palette-harmonious");
            }

            break :palette config.palette.value;
        };

        return .{
            .palette = palette,
            .image_storage_limit = config.@"image-storage-limit",
            .cursor_style = config.@"cursor-style",
            .cursor_blink = config.@"cursor-style-blink",
            .cursor_color = config.@"cursor-color",
            .foreground = config.foreground,
            .background = config.background,
            .osc_color_report_format = config.@"osc-color-report-format",
            .clipboard_write = config.@"clipboard-write",
            .clipboard_write_limit = config.@"clipboard-write-limit-bytes".value,
            .enquiry_response = try alloc.dupe(u8, config.@"enquiry-response"),
            .conditional_state = config._conditional_state,

            // This has to be last so that we copy AFTER the arena allocations
            // above happen (Zig assigns in order).
            .arena = arena,
        };
    }

    pub fn deinit(self: *DerivedConfig) void {
        self.arena.deinit();
    }
};

/// Initialize the termio state.
///
/// This will also start the child process if the termio is configured
/// to run a child process.
pub fn init(self: *Termio, alloc: Allocator, opts: termio.Options) !void {
    // The default terminal modes based on our config.
    const default_modes: terminalpkg.ModePacked = modes: {
        var modes: terminalpkg.ModePacked = .{};

        // Setup our initial grapheme cluster support if enabled. We use a
        // switch to ensure we get a compiler error if more cases are added.
        switch (opts.full_config.@"grapheme-width-method") {
            .unicode => modes.grapheme_cluster = true,
            .legacy => {},
        }

        // Set default cursor blink settings
        modes.cursor_blinking = opts.config.cursor_blink orelse true;

        break :modes modes;
    };

    // Create our terminal
    var term = try terminalpkg.Terminal.init(global.io(), alloc, opts: {
        const grid_size = opts.size.grid();
        break :opts .{
            .cols = grid_size.columns,
            .rows = grid_size.rows,
            .max_scrollback_bytes = opts.full_config.@"scrollback-limit-bytes".optional(),
            .max_scrollback_lines = opts.full_config.@"scrollback-limit-lines".optional(),
            .default_modes = default_modes,
            .default_cursor_style = opts.config.cursor_style,
            .default_cursor_blink = opts.config.cursor_blink,
            .colors = .{
                .background = .init(opts.config.background.toTerminalRGB()),
                .foreground = .init(opts.config.foreground.toTerminalRGB()),
                .cursor = cursor: {
                    const color = opts.config.cursor_color orelse break :cursor .unset;
                    const rgb = color.toTerminalRGB() orelse break :cursor .unset;
                    break :cursor .init(rgb);
                },
                .palette = .init(opts.config.palette),
            },
            .kitty_image_storage_limit = opts.config.image_storage_limit,
            .kitty_image_loading_limits = .allWithTempDir(global.tmpDirPath()),
        };
    });
    errdefer term.deinit(alloc);

    // Setup our terminal size in pixels for certain requests.
    term.width_px = term.cols * opts.size.cell.width;
    term.height_px = term.rows * opts.size.cell.height;

    // Setup our backend.
    var backend = opts.backend;
    backend.initTerminal(&term);

    // Derive the OSC 7 path-translation context (WSL UNC, or MSYS2/Git/Cygwin
    // install root). Returns null on non-Windows and for non-POSIX surfaces.
    // Keys on argv[0]; a shell-wrapped spawn silently disables translation
    // (falls back to the historical no-op).
    var osc7 = internal_os.windows_shell.osc7ContextFromArgs(
        alloc,
        switch (backend) {
            .exec => |*exec| exec.subprocess.args,
        },
    );
    // Bare `wsl.exe` (no -d) leaves the distro name unknown; resolve the default
    // distro's real name from the registry so in-distro OSC 7 paths (e.g.
    // /home/<user>) translate to \\wsl.localhost\<distro>\... rather than being
    // dropped. Best-effort: null on failure (then /mnt still works).
    if (osc7) |*c| switch (c.*) {
        .wsl => |*w| if (w.distro == null) {
            w.distro = internal_os.windows_shell.defaultDistroName(alloc);
        },
        .rooted => {},
    };
    // Until ownership transfers to the StreamHandler (via terminal_stream
    // below), we own the duped string; free it if init fails first.
    errdefer if (osc7) |c| switch (c) {
        .wsl => |w| if (w.distro) |d| alloc.free(d),
        .rooted => |r| if (r.install_root) |s| alloc.free(s),
    };

    // Create our stream handler. This points to memory in self so it
    // isn't safe to use until self.* is set.
    const handler: StreamHandler = .{
        .alloc = alloc,
        .osc7 = osc7,
        .termio_mailbox = &self.mailbox,
        .write_limit = &self.write_limit,
        .surface_mailbox = opts.surface_mailbox,
        .renderer_state = opts.renderer_state,
        .renderer_wakeup = opts.renderer_wakeup,
        .renderer_visible = opts.renderer_visible,
        .size = &self.size,
        .terminal = &self.terminal,
        .osc_color_report_format = opts.config.osc_color_report_format,
        .clipboard_write = opts.config.clipboard_write,
        .clipboard_write_limit = opts.config.clipboard_write_limit,
        .enquiry_response = opts.config.enquiry_response,
    };

    const thread_enter_state = try ThreadEnterState.create(
        alloc,
        opts.full_config,
    );

    self.* = .{
        .alloc = alloc,
        .terminal = term,
        .config = opts.config,
        .renderer_state = opts.renderer_state,
        .renderer_wakeup = opts.renderer_wakeup,
        .renderer_visible = opts.renderer_visible,
        .renderer_mailbox = opts.renderer_mailbox,
        .surface_mailbox = opts.surface_mailbox,
        .size = opts.size,
        .backend = backend,
        .mailbox = opts.mailbox,
        .terminal_stream = .init(.{
            .allocator = alloc,
            .handler = handler,
            // Tier C dormancy snapshots mid-sequence parse state as a
            // replayable continuation suffix; without tracking enabled
            // here, a tab that goes dormant inside an OSC cannot be
            // faithfully woken. The cap bounds what a runaway sequence
            // can pin; an empty tracker at ground costs a fixed 4 KiB.
            .continuation_max_bytes = continuation_max_bytes,
        }),
        .thread_enter_state = thread_enter_state,
    };
}

pub fn deinit(self: *Termio) void {
    self.backend.deinit();
    // A dormant terminal is already torn down -- deiniting it again
    // would double-free the screens -- and its snapshot must be scrubbed
    // and freed rather than leaked with the scrollback it holds.
    if (self.dormant_snapshot) |snapshot| {
        @memset(snapshot.compressed, 0);
        self.alloc.free(snapshot.compressed);
        self.dormant_snapshot = null;
        self.renderer_state.dormant.store(false, .release);
    } else {
        self.terminal.deinit(self.alloc);
    }
    self.config.deinit();
    self.mailbox.deinit(self.alloc);

    // Clear any StreamHandler state
    self.terminal_stream.deinit();

    // Clear any initial state if we have it
    if (self.thread_enter_state) |v| v.destroy();
}

pub fn threadEnter(
    self: *Termio,
    thread: *termio.Thread,
    data: *ThreadData,
) !void {
    // Always free our thread enter state when we're done.
    defer if (self.thread_enter_state) |v| {
        v.destroy();
        self.thread_enter_state = null;
    };

    // If we have thread enter state then we're going to validate
    // and set that all up now so that we can error before we actually
    // start the command and pty.
    const inputs: ?[]const ThreadEnterState.Input = if (self.thread_enter_state) |v|
        try v.prepareInput()
    else
        null;
    defer if (inputs) |items| {
        for (items) |input| input.deinit();
    };

    data.* = .{
        .alloc = self.alloc,
        .loop = &thread.loop,
        .renderer_state = self.renderer_state,
        .surface_mailbox = self.surface_mailbox,
        .mailbox = &self.mailbox,
        .backend = undefined, // Backend must replace this on threadEnter
    };

    // Setup our backend
    try self.backend.threadEnter(self.alloc, self, data);
    errdefer self.backend.threadExit(data);

    // If we have inputs, then queue them all up.
    for (inputs orelse &.{}) |input| switch (input) {
        .string => |v| self.queueWrite(data, v, false) catch |err| {
            log.warn("failed to queue input string err={}", .{err});
            return error.InputFailed;
        },
        .file => |f| {
            const contents = compat_file.readToEndAlloc(
                f,
                self.alloc,
                10 * 1024 * 1024, // 10 MiB max
            ) catch |err| {
                log.warn("failed to read input file err={}", .{err});
                return error.InputFailed;
            };
            defer self.alloc.free(contents);

            self.queueWrite(data, contents, false) catch |err| {
                log.warn("failed to queue input file err={}", .{err});
                return error.InputFailed;
            };
        },
    };
}

pub fn threadExit(self: *Termio, data: *ThreadData) void {
    self.backend.threadExit(data);
}

/// Send a message to the mailbox. Depending on the mailbox type in use
/// this may process now or it may just enqueue and process later.
///
/// This will also notify the mailbox thread to process the message. If
/// you're sending a lot of messages, it may be more efficient to use
/// the mailbox directly and then call notify separately.
///
/// Every message through here takes `termio.Mailbox.Budget.droppable`,
/// because this is `Surface.queueIo`'s tail and that is a UI-thread
/// producer. This function is a shared tail and hands its policy to its
/// callers without their asking: read `Budget.droppable`'s comment before
/// adding a call site, because three of the ones already here are state
/// or user data rather than events, and a fourth would be missed the same
/// way.
pub fn queueMessage(
    self: *Termio,
    msg: termio.Message,
    mutex: MutexState,
) void {
    self.mailbox.send(msg, switch (mutex) {
        .locked => self.renderer_state.mutex,
        .unlocked => null,
    });
    self.mailbox.notify();
}

/// Queue a write directly to the pty.
///
/// If you're using termio.Thread, this must ONLY be called from the
/// mailbox thread. If you're not on the thread, use queueMessage with
/// mailbox messages instead.
///
/// If you're not using termio.Thread, this is not threadsafe.
pub inline fn queueWrite(
    self: *Termio,
    td: *ThreadData,
    data: []const u8,
    linefeed: bool,
) !void {
    try self.backend.queueWrite(self.alloc, td, data, linefeed);
}

/// Update the configuration.
pub fn changeConfig(self: *Termio, td: *ThreadData, config: *DerivedConfig) !void {
    // The remainder of this function is modifying terminal state or
    // the read thread data, all of which requires holding the renderer
    // state lock.
    self.renderer_state.mutex.lockUncancelable(global.io());
    defer self.renderer_state.mutex.unlock(global.io());

    // Deinit our old config. We do this in the lock because the
    // stream handler may be referencing the old config (i.e. enquiry resp)
    self.config.deinit();
    self.config = config.*;

    // Update our stream handler. The stream handler uses the same
    // renderer mutex so this is safe to do despite being executed
    // from another thread.
    self.terminal_stream.handler.changeConfig(&self.config);
    td.backend.changeConfig(&self.config);

    // Update the configuration that we know about.
    //
    // Specific things we don't update:
    //   - command, working-directory: we never restart the underlying
    //   process so we don't care or need to know about these.

    // Update the default palette.
    self.terminal.colors.palette.changeDefault(config.palette);
    self.terminal.flags.dirty.palette = true;

    // Update all our other colors
    self.terminal.colors.background.default = config.background.toTerminalRGB();
    self.terminal.colors.foreground.default = config.foreground.toTerminalRGB();
    self.terminal.colors.cursor.default = cursor: {
        const color = config.cursor_color orelse break :cursor null;
        break :cursor color.toTerminalRGB() orelse break :cursor null;
    };

    // Set the image limits
    self.terminal.setKittyGraphicsSizeLimit(self.alloc, config.image_storage_limit);
    self.terminal.setKittyGraphicsLoadingLimits(.allWithTempDir(global.tmpDirPath()));
}

/// Resize the terminal.
pub fn resize(
    self: *Termio,
    td: *ThreadData,
    size: renderer.Size,
) !void {
    self.size = size;
    const grid_size = size.grid();

    // Update the size of our pty.
    try self.backend.resize(grid_size, size.terminal());

    // Enter the critical area that we want to keep small
    {
        self.renderer_state.mutex.lockUncancelable(global.io());
        defer self.renderer_state.mutex.unlock(global.io());

        // A coalesced resize timer can fire into a surface that went
        // dormant inside its window; resizing the torn-down terminal
        // would walk and reallocate freed pages. Wake first -- arriving
        // after a failed wake, skip: the next resize retries.
        if (!self.wakeIfDormantLocked()) return error.OutOfMemory;

        // Update the size of our terminal state
        try self.terminal.resize(
            self.alloc,
            .{
                .cols = grid_size.columns,
                .rows = grid_size.rows,
                .cell_size_px = .{
                    .width = self.size.cell.width,
                    .height = self.size.cell.height,
                },
            },
        );

        // If we have size reporting enabled we need to send a report.
        if (self.terminal.modes.get(.in_band_size_reports)) {
            try self.sizeReportLocked(td, .mode_2048);
        }
    }

    // Mail the renderer so that it can update the GPU and re-render.
    // The wake on the next line is what makes the renderer drain, so
    // this push must not be able to park: waiting in the queue would
    // withhold the very notify the renderer needs to free a slot.
    if (renderer.Thread.pushMailbox(
        self.renderer_mailbox,
        self.renderer_wakeup,
        .{ .resize = size },
    ) == 0) log.warn("renderer mailbox full, resize not delivered", .{});
    self.renderer_wakeup.notify() catch {};
}

/// Make a size report.
pub fn sizeReport(self: *Termio, td: *ThreadData, style: termio.Message.SizeReport) !void {
    self.renderer_state.mutex.lockUncancelable(global.io());
    defer self.renderer_state.mutex.unlock(global.io());
    try self.sizeReportLocked(td, style);
}

fn sizeReportLocked(self: *Termio, td: *ThreadData, style: termio.Message.SizeReport) !void {
    const grid_size = self.size.grid();
    const report_size: terminalpkg.size_report.Size = .{
        .rows = grid_size.rows,
        .columns = grid_size.columns,
        .cell_width = self.size.cell.width,
        .cell_height = self.size.cell.height,
    };

    // 1024 bytes should be enough for size report since report
    // in columns and pixels.
    var buf: [1024]u8 = undefined;
    var writer: std.Io.Writer = .fixed(&buf);
    try terminalpkg.size_report.encode(
        &writer,
        style,
        report_size,
    );

    try self.queueWrite(td, writer.buffered(), false);
}

/// Reset the synchronized output mode. This is usually called by timer
/// expiration from the termio thread.
pub fn resetSynchronizedOutput(self: *Termio) void {
    self.renderer_state.mutex.lockUncancelable(global.io());
    defer self.renderer_state.mutex.unlock(global.io());
    // The synchronized-output timeout is a timer, so it can land on a
    // surface that went dormant while the mode was armed. The write is
    // meaningless against a torn-down terminal (the wake's decode
    // carries the mode state forward), so skip it rather than write
    // into the undefined field.
    if (self.renderer_state.dormant.load(.monotonic)) return;
    self.terminal.modes.set(.synchronized_output, false);
    self.renderer_wakeup.notify() catch {};
}

/// Clear the screen.
pub fn clearScreen(self: *Termio, td: *ThreadData, history: bool) !void {
    {
        self.renderer_state.mutex.lockUncancelable(global.io());
        defer self.renderer_state.mutex.unlock(global.io());

        // If we're on the alternate screen, we do not clear. Since this is an
        // emulator-level screen clear, this messes up the running programs
        // knowledge of where the cursor is and causes rendering issues. So,
        // for alt screen, we do nothing.
        if (self.terminal.screens.active_key == .alternate) return;

        // Clear our selection
        self.terminal.screens.active.clearSelection();

        // Clear our scrollback
        if (history) self.terminal.eraseDisplay(.scrollback, false);

        // If we're not at a prompt, we just delete above the cursor.
        if (!self.terminal.cursorIsAtPrompt()) {
            if (self.terminal.screens.active.cursor.y > 0) {
                self.terminal.screens.active.eraseActive(
                    self.terminal.screens.active.cursor.y - 1,
                );
            }

            // Clear all Kitty graphics state for this screen. This copies
            // Kitty's behavior when Cmd+K deletes all Kitty graphics. I
            // didn't spend time researching whether it only deletes Kitty
            // graphics that are placed above the cursor or if it deletes
            // all of them. We delete all of them for now but if this behavior
            // isn't fully correct we should fix this later.
            self.terminal.screens.active.kitty_images.delete(
                self.terminal.io(),
                self.terminal.screens.active.alloc,
                &self.terminal,
                .{ .all = true },
            );

            return;
        }

        // At a prompt, we want to first fully clear the screen, and then after
        // send a FF (0x0C) to the shell so that it can repaint the screen.
        // Mark the current row as a not a prompt so we can properly
        // clear the full screen in the next eraseDisplay call.
        // TODO: fix this
        // self.terminal.markSemanticPrompt(.command);
        // assert(!self.terminal.cursorIsAtPrompt());
        self.terminal.eraseDisplay(.complete, false);
    }

    // If we reached here it means we're at a prompt, so we send a form-feed.
    try self.queueWrite(td, &[_]u8{0x0C}, false);
}

/// Deep-idle trim: drop the parse state a sequence cut mid-flight is
/// pinning. A truncated OSC can hold up to its allocating cap until the
/// sequence's next byte arrives; DCS, APC, an iTerm2 multipart transfer,
/// and an in-flight kitty clipboard write each pin their own capture.
/// These buffers self-clear per completed sequence, so a surface at
/// ground holds nothing to trim -- the reset is simply cheap when it
/// has nothing to do. Runs on the IO thread (the stream's owner) under
/// the same lock the read path parses under, so it cannot race a parse.
///
/// Kept: kitty clipboard grants (a session permission, not parse state;
/// dropping one costs the user a re-prompt) and every terminal screen
/// (user data -- scrollback shedding is the compression scheduler's
/// job, not this one).
///
/// The one visible behavior change: if the writer resumes a sequence
/// the trim cut (a stalled peer waking after minutes mid-OSC), its tail
/// parses as plain text from ground rather than completing the
/// sequence. That takes a minute-plus stall inside a single sequence,
/// by which point the peer is almost certainly dead or the payload
/// garbage either way.
pub fn deepIdleTrim(self: *Termio) void {
    self.renderer_state.mutex.lockUncancelable(global.io());
    defer self.renderer_state.mutex.unlock(global.io());

    self.terminal_stream.resetToGround();
    // The continuation tracker must go with the state it describes: the
    // stream runs with tracking enabled (dormancy needs it), and a
    // tracker left holding the trimmed sequence's suffix would
    // faithfully resurrect it in the next snapshot -- inverting this
    // trim exactly when dormancy later consumes it.
    if (self.terminal_stream.continuation) |*tracker| tracker.reset();

    const h = &self.terminal_stream.handler;
    h.apc.deinit();
    h.apc = .{};
    h.dcs.deinit();
    h.dcs = .{};
    h.multipart_iterm2.deinit(h.alloc);
    h.multipart_iterm2 = .{};
    h.kittyClipboardWriteAbort();
}

/// Tier C dormancy: tear the terminal down into snapshot bytes. The
/// eligibility gates here are the ones only the terminal in hand can
/// answer; the surface-side gates (an active search) are checked before
/// the message is ever queued. Everything runs under the renderer state
/// mutex on the IO thread, which is the lock every other terminal
/// reader takes, so no reader can observe the half-torn-down window.
///
/// What snapshot v1 cannot carry is refused rather than silently lost:
/// kitty image state, glyph glossary registrations, and kitty drag and
/// drop state are all user-visible, and a dormant wake that dropped
/// them would read as corruption. The viewport's scroll offset is the
/// one accepted loss (v1 wakes at the active position).
pub fn goDormant(self: *Termio) void {
    self.renderer_state.mutex.lockUncancelable(global.io());
    defer self.renderer_state.mutex.unlock(global.io());

    if (self.dormant_snapshot != null) return;
    if (self.dormantBlocker()) |reason| {
        log.info("dormancy refused: {s}", .{reason});
        return;
    }

    // The replayable parse state rides inside the snapshot itself.
    var cont: std.Io.Writer.Allocating = .init(self.alloc);
    defer cont.deinit();
    self.terminal_stream.writeContinuation(&cont.writer) catch |err| {
        log.warn("dormancy refused: continuation unavailable err={}", .{err});
        return;
    };
    const cont_value: snapshotpkg.Continuation = if (cont.written().len == 0)
        .ground
    else
        .{ .bytes = cont.written() };

    // The plain snapshot. One transient allocation the size of the
    // logical stream; the dormancy this serves only ever runs on a
    // surface that has been quiet for the embedder's whole patience.
    // The defer scrubs on EVERY exit -- refusal paths below leave
    // through it too, and the plain buffer holds the full scrollback,
    // secrets and all.
    var plain: std.Io.Writer.Allocating = .init(self.alloc);
    defer {
        @memset(plain.written(), 0);
        plain.deinit();
    }
    snapshotpkg.encode(
        self.alloc,
        &plain.writer,
        &self.terminal,
        .{ .continuation = cont_value },
    ) catch |err| {
        log.warn("dormancy refused: encode err={}", .{err});
        return;
    };

    // Wrap it in one raw LZ4 block, same codec the page compression
    // uses. RAM-only and producer/consumer-identical, so there is no
    // stability requirement to honor -- only the bytes' size.
    const bound = lz4.compressBound(plain.written().len) catch |err| {
        log.warn("dormancy refused: input too large for LZ4 err={}", .{err});
        return;
    };
    var compressed = self.alloc.alloc(u8, bound) catch |err| {
        log.warn("dormancy refused: out of memory err={}", .{err});
        return;
    };
    var table: lz4.HashTable = undefined;
    const compressed_len = lz4.compress(plain.written(), compressed, &table) catch |err| {
        self.alloc.free(compressed);
        log.warn("dormancy refused: compress err={}", .{err});
        return;
    };
    if (self.alloc.realloc(compressed, compressed_len)) |shrunk| {
        compressed = shrunk;
    } else |_| {
        // Shrinking failed; the larger allocation is still correct.
    }

    // From here the transition is committed. The plain buffer's scrub
    // rides the defer above; the same hygiene applies to the compressed
    // block on wake. Capture the presentation flags BEFORE the deinit:
    // deinit leaves the field undefined (poisoned in safe builds), and
    // these are facts the snapshot does not carry.
    const flags_visible = self.terminal.flags.visible;
    const flags_focused = self.terminal.flags.focused;

    // Tear the terminal down; the field stays undefined until wake
    // rebuilds it AT THIS ADDRESS, which is the address
    // renderer_state.terminal and the handler both point at.
    self.terminal.deinit(self.alloc);

    // The stream's parse state is already inside the snapshot as the
    // continuation; leaving it live would double-apply on wake, which
    // resets before replaying.
    self.terminal_stream.resetToGround();

    self.dormant_snapshot = .{
        .compressed = compressed,
        .plain_len = plain.written().len,
        .visible = flags_visible,
        .focused = flags_focused,
    };
    self.renderer_state.dormant.store(true, .release);
    log.info("dormant: {d} plain bytes as {d} compressed", .{
        plain.written().len,
        compressed_len,
    });
}

/// Why this terminal cannot go dormant right now, or null if it can.
/// Runs under the renderer state mutex.
fn dormantBlocker(self: *Termio) ?[]const u8 {
    if (self.renderer_state.inspector != null) return "inspector active";
    // Visible surfaces are refused: the frames a visible surface draws
    // read the terminal constantly, and the win is aimed at tabs nobody
    // is looking at.
    if (self.terminal.flags.visible) return "surface visible";
    for ([_]terminalpkg.ScreenSet.Key{ .primary, .alternate }) |key| {
        const screen = self.terminal.screens.get(key) orelse continue;
        if (screen.kitty_images.images.count() > 0)
            return "kitty images present";
    }
    if (self.terminal.glyph_glossary.entries.count() > 0)
        return "glyph glossary registrations present";
    if (self.terminal.kitty_dnd != null) return "kitty dnd state present";
    // v1 restores neither selections nor the viewport offset; a tab
    // whose selection silently evaporated on wake reads as corruption
    // for the same reason a dropped kitty image would.
    for ([_]terminalpkg.ScreenSet.Key{ .primary, .alternate }) |key| {
        const screen = self.terminal.screens.get(key) orelse continue;
        if (screen.selection != null) return "selection active";
    }
    if (self.renderer_state.search_active) return "search active";
    return null;
}

/// Wake a dormant terminal if one is present, taking the renderer state
/// mutex. The funnel for callers that do not already hold it. Returns
/// whether a live terminal is now in place -- a surface that was never
/// dormant counts as woken.
pub fn wakeIfDormant(self: *Termio) bool {
    if (!self.renderer_state.dormant.load(.acquire)) return true;
    self.renderer_state.mutex.lockUncancelable(global.io());
    defer self.renderer_state.mutex.unlock(global.io());
    return self.wakeIfDormantLocked();
}

/// Rebuild the terminal from its snapshot. CALLER HOLDS the renderer
/// state mutex, and the terminal field is undefined until this returns
/// successfully -- a failed wake leaves the snapshot intact and the
/// surface dormant, retried by the next input, because a terminal that
/// can never wake must not be torn down into bytes it can never read
/// back. The bool return is the contract callers must honor: FALSE
/// means the terminal is undefined and the caller must not touch it.
fn wakeIfDormantLocked(self: *Termio) bool {
    const snapshot = self.dormant_snapshot orelse return true;

    const plain = self.alloc.alloc(u8, snapshot.plain_len) catch |err| {
        log.warn("wake deferred: out of memory err={}", .{err});
        return false;
    };
    defer self.alloc.free(plain);
    const plain_len = lz4.decompress(snapshot.compressed, plain) catch |err| {
        log.err("wake failed: snapshot corrupt err={}", .{err});
        return false;
    };
    if (plain_len != snapshot.plain_len) {
        log.err("wake failed: snapshot truncated", .{});
        return false;
    }

    var source: std.Io.Reader = .fixed(plain);
    var decoded = snapshotpkg.decodeExact(
        self.alloc,
        global.io(),
        &source,
        .{ .max_continuation_bytes = continuation_max_bytes },
    ) catch |err| {
        log.err("wake failed: decode err={}", .{err});
        return false;
    };

    // Store at the terminal's final address -- this address -- before
    // anything can observe it, then rebuild the stream's parse state by
    // replaying the continuation exactly once. The stream was reset at
    // dormancy; replaying without that reset would apply the suffix to
    // whatever parse state accumulated since, which is not the state
    // the snapshot describes.
    self.terminal = decoded.toOwned();
    // The snapshot does not carry presentation flags (caller-supplied
    // by design); restore what teardown captured so dormancy is
    // transparent to visibility and focus bookkeeping.
    self.terminal.flags.visible = snapshot.visible;
    self.terminal.flags.focused = snapshot.focused;
    self.terminal_stream.resetToGround();
    switch (decoded.continuation) {
        .ground => {},
        .bytes => |bytes| for (bytes) |ch| {
            _ = self.terminal_stream.next(ch);
        },
    }
    decoded.deinit(self.alloc);

    // The snapshot's bytes are the same secrets the live scrollback
    // holds; scrub both copies on the way out the door.
    @memset(plain, 0);
    @memset(snapshot.compressed, 0);
    self.alloc.free(snapshot.compressed);
    self.dormant_snapshot = null;
    self.renderer_state.dormant.store(false, .release);
    log.info("dormant surface woke: {d} bytes restored", .{snapshot.plain_len});
    return true;
}

/// Scroll the viewport
pub fn scrollViewport(
    self: *Termio,
    scroll: terminalpkg.Terminal.ScrollViewport,
) void {
    self.renderer_state.mutex.lockUncancelable(global.io());
    defer self.renderer_state.mutex.unlock(global.io());
    self.terminal.scrollViewport(scroll);
}

/// Jump the viewport to the prompt.
pub fn jumpToPrompt(self: *Termio, delta: isize) !void {
    {
        self.renderer_state.mutex.lockUncancelable(global.io());
        defer self.renderer_state.mutex.unlock(global.io());
        self.terminal.screens.active.scroll(.{ .delta_prompt = delta });
    }

    try self.renderer_wakeup.notify();
}

/// Called when focus is gained or lost (when focus events are enabled)
pub fn focusGained(self: *Termio, td: *ThreadData, focused: bool) !void {
    self.renderer_state.mutex.lockUncancelable(global.io());
    const focus_event = self.renderer_state.terminal.modes.get(.focus_event);
    self.renderer_state.mutex.unlock(global.io());

    // If we have focus events enabled, we send the focus event.
    if (focus_event) {
        var buf: [terminalpkg.focus.max_encode_size]u8 = undefined;
        var writer: std.Io.Writer = .fixed(&buf);
        terminalpkg.focus.encode(&writer, if (focused) .gained else .lost) catch |err| {
            log.err("error encoding focus event err={}", .{err});
            return;
        };
        try self.queueWrite(td, writer.buffered(), false);
    }

    // We always notify our backend of focus changes.
    try self.backend.focusGained(td, focused);
}

/// Process output from the pty. This is the manual API that users can
/// call with pty data but it is also called by the read thread when using
/// an exec subprocess.
pub fn processOutput(self: *Termio, buf: []const u8) void {
    // We are modifying terminal state from here on out and we need
    // the lock to grab our read data.
    self.renderer_state.mutex.lockUncancelable(global.io());
    defer self.renderer_state.mutex.unlock(global.io());
    self.processOutputLocked(buf);
}

/// Process output from readdata but the lock is already held.
fn processOutputLocked(self: *Termio, buf: []const u8) void {
    // Arriving data wakes a dormant surface before it is parsed: the
    // terminal field is undefined until the wake rebuilds it. A FAILED
    // wake drops this buffer rather than parsing into the undefined
    // terminal -- the surface stays dormant and the next arrival
    // retries, which in the OOM regime this is costs one chunk of
    // output instead of the process.
    if (self.renderer_state.dormant.load(.monotonic)) {
        if (!self.wakeIfDormantLocked()) {
            log.warn("dropping {d} bytes: dormant wake failed", .{buf.len});
            return;
        }
    }

    // Schedule a render. We can call this first because we have the lock.
    self.terminal_stream.handler.queueRender() catch unreachable;

    // Whenever a character is typed, we ensure the cursor is in the
    // non-blink state so it is rendered if visible. If we're under
    // HEAVY read load, we don't want to send a ton of these so we
    // use a timer under the covers
    const now = std.Io.Timestamp.now(global.io(), .awake);
    cursor_reset: {
        if (self.last_cursor_reset) |last| {
            if (last.durationTo(now).toMilliseconds() <= 500) {
                break :cursor_reset;
            }
        }

        self.last_cursor_reset = now;
        _ = self.renderer_mailbox.push(global.io(), .{
            .reset_cursor_blink = {},
        }, .{ .instant = {} });
    }

    // If we have an inspector, we enter SLOW MODE because we need to
    // process a byte at a time alternating between the inspector handler
    // and the termio handler. This is very slow compared to our optimizations
    // below but at least users only pay for it if they're using the inspector.
    if (self.renderer_state.inspector) |insp| {
        for (buf, 0..) |byte, i| {
            insp.recordPtyRead(
                self.alloc,
                &self.terminal,
                buf[i .. i + 1],
            ) catch |err| {
                log.err("error recording pty read in inspector err={}", .{err});
            };

            self.terminal_stream.next(byte);
        }
    } else {
        self.terminal_stream.nextSlice(buf);
    }

    // If our stream handling caused messages to be sent to the mailbox
    // thread, then we need to wake it up so that it processes them.
    if (self.terminal_stream.handler.termio_messaged) {
        self.terminal_stream.handler.termio_messaged = false;
        self.mailbox.notify();
    }
}

/// Sends a DSR response for the current color scheme to the pty.
/// Record a Kitty clipboard protocol session grant so future requests
/// carrying the password skip the permission prompt.
pub fn kittyClipboardGrant(
    self: *Termio,
    pw: []const u8,
    dir: terminalpkg.kitty.clipboard.Grants.Direction,
) error{OutOfMemory}!void {
    self.renderer_state.mutex.lockUncancelable(global.io());
    defer self.renderer_state.mutex.unlock(global.io());

    try self.terminal_stream.handler.kittyClipboardGrant(pw, dir);
}

pub fn colorSchemeReport(self: *Termio, td: *ThreadData, force: bool) !void {
    self.renderer_state.mutex.lockUncancelable(global.io());
    defer self.renderer_state.mutex.unlock(global.io());

    try self.colorSchemeReportLocked(td, force);
}

pub fn colorSchemeReportLocked(self: *Termio, td: *ThreadData, force: bool) !void {
    if (!force and !self.renderer_state.terminal.modes.get(.report_color_scheme)) {
        return;
    }
    const scheme: terminalpkg.device_status.ColorScheme = switch (self.config.conditional_state.theme) {
        .light => .light,
        .dark => .dark,
    };

    var buf: [terminalpkg.device_status.max_color_scheme_report_encode_size]u8 = undefined;
    var writer: std.Io.Writer = .fixed(&buf);
    try terminalpkg.device_status.encodeColorSchemeReport(&writer, scheme);
    try self.queueWrite(td, writer.buffered(), false);
}

/// Sends a visibility report to the pty. Unforced reports are only sent while
/// DEC mode 2033 is enabled.
pub fn visibilityReport(
    self: *Termio,
    td: *ThreadData,
    visible: bool,
    force: bool,
) !void {
    self.renderer_state.mutex.lockUncancelable(global.io());
    defer self.renderer_state.mutex.unlock(global.io());

    if (!force and !self.renderer_state.terminal.modes.get(.report_visibility)) {
        return;
    }

    var buf: [terminalpkg.device_status.max_visibility_report_encode_size]u8 = undefined;
    var writer: std.Io.Writer = .fixed(&buf);
    try terminalpkg.device_status.encodeVisibilityReport(
        &writer,
        if (visible) .potentially_visible else .not_visible,
    );
    try self.queueWrite(td, writer.buffered(), false);
}

/// ThreadData is the data created and stored in the termio thread
/// when the thread is started and destroyed when the thread is
/// stopped.
///
/// All of the fields in this struct should only be read/written by
/// the termio thread. As such, a lock is not necessary.
pub const ThreadData = struct {
    /// Allocator used for the event data
    alloc: Allocator,

    /// The event loop associated with this thread. This is owned by
    /// the Thread but we have a pointer so we can queue new work to it.
    loop: *xev.Loop,

    /// The shared render state
    renderer_state: *renderer.State,

    /// Mailboxes for different threads
    surface_mailbox: apprt.surface.Mailbox,

    /// Data associated with the backend implementation (i.e. pty/exec state)
    backend: termio.backend.ThreadData,
    mailbox: *termio.Mailbox,

    pub fn deinit(self: *ThreadData) void {
        self.backend.deinit(self.alloc);
        self.* = undefined;
    }
};

/// Get information about the process(es) attached to the backend. Returns
/// `null` if there was an error getting the information or the information is
/// not available on a particular platform.
pub fn getProcessInfo(self: *Termio, comptime info: ProcessInfo) ?ProcessInfo.Type(info) {
    return self.backend.getProcessInfo(info);
}
