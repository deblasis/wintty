const std = @import("std");
const builtin = @import("builtin");
const build_config = @import("build_config.zig");
const build_options = @import("build_options");
const cli = @import("cli.zig");
const internal_os = @import("os/main.zig");
const fontconfig = @import("fontconfig");
const harfbuzz = @import("harfbuzz");
const oni = @import("oniguruma");
const crash = @import("crash/main.zig");
const renderer = @import("renderer.zig");
const apprt = @import("apprt.zig");
const assert = @import("quirks.zig").inlineAssert;
const allocTmpDir = @import("os/file.zig").allocTmpDir;
const freeTmpDir = @import("os/file.zig").freeTmpDir;

// This file should only be imported for certain platforms.
comptime {
    switch (@import("terminal_options").artifact) {
        .ghostty => {},
        // This file is not allowed to be included in libghostty-vt
        .lib => @compileError("global state cannot be used in libghostty-vt"),
    }
}

/// We export the xev backend we want to use so that the rest of
/// Ghostty can import this once and have access to the proper
/// backend.
pub const xev = @import("xev").Dynamic;

/// Global process state. This is initialized in main() for exe artifacts and
/// by ghostty_init() for lib artifacts. Most other methods in this file will
/// retrieve items stored in this state.
var state: State = .uninitialized;

/// Whether there is a usable `GlobalState`, and if not, why not.
///
/// Three tags rather than an optional because `init` needs to tell "never
/// ran" from "ran and is gone" to refuse a second call, and the accessors
/// need neither of those to be readable. A torn-down `GlobalState` left in
/// place would let them hand out a deinitialized allocator instead of
/// tripping, which is how `ghostty_config_new` ended up allocating from a
/// dead GPA after a failed `init`.
///
/// The trip is checked only in Debug and ReleaseSafe. ReleaseFast and
/// ReleaseSmall still depend on the caller honoring `init`'s error, since
/// nothing removes the payload bytes; only the tag changes.
const State = union(enum) {
    /// `init` has not run.
    uninitialized,

    /// `init` succeeded and `deinit` has not run. The only tag the accessors
    /// below will read.
    initialized: GlobalState,

    /// `init` failed, or `deinit` tore the state down. See `init` for why a
    /// failure is not retryable.
    unavailable,
};

pub const InitOpts = union(enum) {
    main: std.process.Init.Minimal,

    /// Same as `main` but for auxiliary tool binaries (e.g. ghostty-bench
    /// and ghostty-gen) that have their own CLI action namespace. This
    /// skips detection of ghostty app CLI actions, since tool actions
    /// (e.g. `+terminal-stream`) are not valid app actions and would
    /// otherwise cause init to fail with InvalidAction.
    tool: std.process.Init.Minimal,

    c: struct {
        argc: usize,
        argv: [*][*:0]u8,
        environ: std.process.Environ,
    },

    /// Windows embedders. The args vector on Windows is the raw WTF-16
    /// command line, which a C `char**` cannot represent, so it is passed
    /// through as-is instead of being reassembled from argv.
    ///
    /// The payload is `Args.Vector` rather than `[]const u16` so this prong
    /// still compiles off Windows, where the vector is a narrow `argv`. Only
    /// `ghostty_init_wide` constructs it, and that export is Windows-only.
    ///
    /// `cmdline` is borrowed, not copied: `std.process.Args.Iterator` keeps
    /// a reference to it, so it must stay valid for as long as the args are
    /// readable, which in practice is the life of the process.
    c_wide: struct {
        cmdline: std.process.Args.Vector,
        environ: std.process.Environ,
    },
};

/// Initialize the global state. This may only be called once per process,
/// including after `deinit` and after a failed `init`. A second call returns
/// `error.AlreadyInitialized` without touching whatever state is there.
pub fn init(opts: InitOpts) !void {
    // Re-initializing would leak the previous GPA, I/O instance and resources
    // dir, orphan the command line the args iterator borrows, and re-run
    // process-global setup that nothing undoes: `oni.init` is documented
    // once-per-process everywhere, and on POSIX `crash.init` asserts sentry's
    // init thread starts exactly once while `ResourceLimits.init` snapshots
    // the limit it later restores (both no-ops on Windows).
    //
    // An error rather than an assertion: `quirks.inlineAssert` is
    // `unreachable`, which ReleaseFast and ReleaseSmall do not check, so it
    // would fall through and do all of that in the builds where it is hardest
    // to diagnose. Returning here leaves any live state untouched.
    if (state != .uninitialized) return error.AlreadyInitialized;

    // Initialize ourself to nothing so we don't have any extra state.
    // Assign before any log output: `logging` falls back to defaults without
    // a state, so an earlier log ignores whatever `GHOSTTY_LOG` asks for.
    state = .{
        .initialized = .{
            // Not `undefined`: the errdefer below arms before the real
            // implementation is assigned, and `deinit` unconditionally calls
            // `io_impl.deinit()`, which locks its mutex.
            .io_impl = .init_single_threaded,
            .gpa = null,
            .alloc = undefined,
            .environ = switch (opts) {
                .main, .tool => |m| m.environ,
                .c => |c| c.environ,
                .c_wide => |c| c.environ,
            },
            .args = switch (opts) {
                .main, .tool => |m| m.args,
                // A C `char**` cannot carry WTF-16, so on Windows the narrow
                // entry point cannot express the args vector at all. Fall back to
                // the process command line from the PEB, as std's own Windows
                // start code does; `c_wide` is how a caller supplies its own.
                .c => |c| .{ .vector = if (comptime builtin.os.tag == .windows)
                    std.os.windows.peb().ProcessParameters.CommandLine.slice()
                else
                    c.argv[0..c.argc] },
                .c_wide => |c| .{ .vector = c.cmdline },
            },
            .tmp_dir_path = null,
            .action = null,
            .logging = .{},
            .rlimits = .{},
            .resources_dir = .{},
        },
    };
    const self = &state.initialized;

    // `deinit` leaves the state `unavailable`, which is what an embedder that
    // ignored our error then trips on. See `State`.
    errdefer deinit();

    // Don't let the narrow-entry-point fallback above be silent: a caller
    // that passed argv on Windows is not getting the args it asked for.
    if (comptime builtin.os.tag == .windows) switch (opts) {
        .c => |c| if (c.argc > 0) std.log.warn(
            "ghostty_init cannot carry WTF-16 args on Windows so argv was " ++
                "ignored and the process command line used instead; call " ++
                "ghostty_init_wide to supply args explicitly",
            .{},
        ),
        else => {},
    };

    self.gpa = gpa: {
        // Use the libc allocator if it is available because it is WAY
        // faster than GPA. We only do this in release modes so that we
        // can get easy memory leak detection in debug modes.
        if (builtin.link_libc) {
            if (switch (builtin.mode) {
                .ReleaseSafe, .ReleaseFast => true,

                // We also use it if we can detect we're running under
                // Valgrind since Valgrind only instruments the C allocator
                else => std.valgrind.runningOnValgrind() > 0,
            }) break :gpa null;
        }

        break :gpa .init;
    };

    self.alloc = if (self.gpa) |*value|
        value.allocator()
    else if (builtin.link_libc)
        std.heap.c_allocator
    else
        unreachable;

    // Set up our main I/O implementation (fully threaded w/allocator). Note
    // that we cannot use any implementation supplied from main at this point,
    // because there are some later initialization steps that depend on us
    // mutating the environment, and thus it needs to be re-synced farther
    // down. For that, we need a stable implementation that allows us to do so.
    self.io_impl = .init(self.alloc, .{
        .argv0 = .init(self.args),
        .environ = self.environ,
    });

    // Discover and save the temporary directory path
    self.tmp_dir_path = try allocTmpDir(self.alloc, self.environ);

    // We first try to parse any action that we may be executing.
    // Tool binaries (ghostty-bench, ghostty-gen) have their own action
    // namespace and detect their own actions, so we skip detection here.
    self.action = switch (opts) {
        .main, .c, .c_wide => try cli.action.detectArgs(
            cli.ghostty.Action,
            self.alloc,
            self.args,
        ),
        .tool => null,
    };

    // If we have an action executing, we disable logging by default
    // since we write to stderr we don't want logs messing up our
    // output.
    if (self.action != null) self.logging.stderr = false;

    // I don't love the env var name but I don't have it in my heart
    // to parse CLI args 3 times (once for actions, once for config,
    // maybe once for logging) so for now this is an easy way to do
    // this. Env vars are useful for logging too because they are
    // easy to set.
    logging: {
        // Any read failure leaves the defaults, rather than aborting startup
        // over a logging knob. The parse below is already tolerant, so a
        // malformed value could not stop init while an unreadable one could.
        const v = self.environ.getAlloc(self.alloc, "GHOSTTY_LOG") catch break :logging;
        defer self.alloc.free(v);
        self.logging = cli.args.parsePackedStruct(GlobalState.Logging, v) catch .{};
    }

    // Setup our signal handlers before logging
    GlobalState.initSignals();

    // Setup our Xev backend if we're dynamic
    if (comptime xev.dynamic) xev.detect() catch |err| {
        std.log.warn("failed to detect xev backend, falling back to " ++
            "most compatible backend err={}", .{err});
    };

    // Output some debug information right away
    std.log.info("ghostty version={s}", .{build_config.version_string});
    std.log.info("ghostty build optimize={s}", .{build_config.mode_string});
    std.log.info("runtime={}", .{build_config.app_runtime});
    std.log.info("font_backend={}", .{build_config.font_backend});
    if (comptime build_config.font_backend.hasHarfbuzz()) {
        std.log.info("dependency harfbuzz={s}", .{harfbuzz.versionString()});
    }
    if (comptime build_config.font_backend.hasFontconfig()) {
        std.log.info("dependency fontconfig={d}", .{fontconfig.version()});
    }
    std.log.info("renderer={}", .{renderer.Renderer});
    std.log.info("libxev default backend={t}", .{xev.backend});

    // As early as possible, initialize our resource limits.
    self.rlimits = .init();

    if (build_options.sentry) {
        // Initialize our crash reporting. The environ map snapshot is
        // owned by crash.init (it is freed by the init thread).
        const environ_map = try self.environ.createMap(self.alloc);
        crash.init(self.alloc, environ_map) catch |err| {
            std.log.warn(
                "sentry init failed, no crash capture available err={}",
                .{err},
            );
        };
    }

    // const sentrylib = @import("sentry");
    // if (sentrylib.captureEvent(sentrylib.Value.initMessageEvent(
    //     .info,
    //     null,
    //     "hello, world",
    // ))) |uuid| {
    //     std.log.warn("uuid={s}", .{uuid.string()});
    // } else std.log.warn("failed to capture event", .{});

    // We need to make sure the process locale is set properly. Locale
    // affects a lot of behaviors in a shell.
    //
    // We need to re-sync the environment after this completes.
    try internal_os.ensureLocale();
    syncEnviron();

    // No shader compiler init: zioshade is pure Zig and needs none, where
    // upstream calls `glslang.init()` here.

    // Initialize oniguruma for regex
    try oni.init(&.{oni.Encoding.utf8});

    // Find our resources directory once for the app so every launch
    // hereafter can use this cached value.
    self.resources_dir = try apprt.runtime.resourcesDir(self.alloc);

    // Setup i18n
    if (self.resources_dir.app()) |v| internal_os.i18n.init(v) catch |err| {
        std.log.warn("failed to init i18n, translations will not be available err={}", .{err});
    };
}

/// Cleans up the global state. This doesn't _need_ to be called but
/// doing so in dev modes will check for memory leaks.
///
/// This leaves the state `unavailable`, so the accessors below trip rather
/// than handing out what was just released, and `init` refuses to run again.
/// A failed `init` lands in the same place, through its errdefer.
///
/// Asserts that the state is initialized.
pub fn deinit() void {
    const self = &state.initialized;

    // Deferred rather than written at the end: `self` points into the payload
    // this tag replaces, so anything appended below has to run first. The GPA
    // leak report is one of those - it logs, and `logging` reads the tag.
    defer state = .unavailable;

    self.resources_dir.deinit(self.alloc);

    // Flush our crash logs
    crash.deinit();

    // Release our tmp_dir_path if needed
    if (self.tmp_dir_path) |td| freeTmpDir(self.alloc, td);

    // Release our I/O instance
    self.io_impl.deinit();

    if (self.gpa) |*value| {
        // We want to ensure that we deinit the GPA because this is
        // the point at which it will output if there were safety violations.
        _ = value.deinit();
    }
}

/// Helper to return either the state's I/O instance, or one from testing.
///
/// Asserts that the global state is initialized when not running as as test.
pub fn io() std.Io {
    if (builtin.is_test) return std.testing.io;

    return state.initialized.io();
}

/// Helper to return either the state's I/O instance, or one from testing.
///
/// Asserts that the global state is initialized when not running as as test.
pub fn alloc() std.mem.Allocator {
    if (builtin.is_test) return std.testing.allocator;

    return state.initialized.alloc;
}

/// Helper to return either the state's environment, or one from testing.
///
/// Asserts that the global state is initialized when not running as a test.
pub fn environ() std.process.Environ {
    if (builtin.is_test) return std.testing.environ;

    return state.initialized.environ;
}

/// Helper to create an environment map off of the state's environment, or one
/// from testing. The map is created off of the state allocator.
///
/// Asserts that the global state is initialized when not running as a test.
pub fn environMap() !std.process.Environ.Map {
    if (builtin.is_test) return std.testing.environ.createMap(std.testing.allocator);

    return state.initialized.environ.createMap(state.initialized.alloc);
}

/// Re-synchronizes the global Environ (both the higher-level and I/O versions)
/// from the process. No-op on Windows, asserts libc and an initialized global
/// state on everything else.
///
/// It is not valid to run this within any code that needs to be run through
/// tests. For any of these, re-factor the code to take an environment map
/// instead, where you can modify the environment as needed.
///
/// NOTE: Be cognizant of where you are calling this! While the only real
/// difference between the POSIX environment and higher-level Zig `PosixBlock`
/// struct is that the latter has a length versus the former's many-item
/// pointer state, there is no concurrency control on this function (or for
/// that matter, `environ` or `environMap`. Direct modification of the system
/// environment is becoming more discouraged in the standard library as well,
/// and this should be kept in mind when resorting to lower-level `setenv` or
/// `unsetenv` - as a rule, beyond initialization, favor
/// `std.process.Environ.Map` whenever possible.
pub fn syncEnviron() void {
    switch (builtin.os.tag) {
        .windows => {},
        else => {
            assert(builtin.link_libc);
            assert(!builtin.is_test);
            const new_environ: std.process.Environ = .{ .block = .{ .slice = std.c.environ[0..env_len: {
                var len: usize = 0;
                while (std.c.environ[len]) |_| : (len += 1) {}
                break :env_len len;
            } :null] } };
            state.initialized.environ = new_environ;
            state.initialized.io_impl.environ = .{ .process_environ = new_environ };
        },
    }
}

/// Helper to return either the state's args, or one from testing.
///
/// Asserts that the global state is initialized when not running as a test.
pub fn args() std.process.Args {
    if (builtin.is_test) return .{ .vector = &.{} };

    return state.initialized.args;
}

/// Returns the temporary directory discovered from the global environment
/// (saves allocation of the temporary directory where you can get at global
/// state).
///
/// Asserts that the global state is initialized.
pub fn tmpDirPath() []const u8 {
    return state.initialized.tmp_dir_path.?;
}

/// Returns the global state resources_dir, or an empty one when testing.
///
/// Asserts that the global state is initialized when not running as a test.
pub fn resourcesDir() internal_os.ResourcesDir {
    if (builtin.is_test) return .{};

    return state.initialized.resources_dir;
}

/// Returns the global state rlimits, or an empty one when testing.
///
/// Asserts that the global state is initialized when not running as a test.
pub fn rlimits() ResourceLimits {
    if (builtin.is_test) return .{};

    return state.initialized.rlimits;
}

/// Returns the global state logging configuration, or the defaults when there
/// is no usable state.
///
/// Unlike the accessors above this one tolerates an absent state rather than
/// trapping. `logFn` reads it on every log call, including the `std.log.err`
/// that `ghostty_init` writes to report an `init` failure, which runs after
/// `init`'s errdefer has already marked the state unavailable. Trapping here
/// would panic inside the code trying to explain the failure.
///
/// The defaults are the same values `init` starts from, so a log emitted with
/// no state goes wherever one emitted at the very start of `init` would have.
pub fn logging() GlobalState.Logging {
    return switch (state) {
        // Pointer capture: a by-value capture copies all of GlobalState.
        .initialized => |*s| s.logging,
        .uninitialized, .unavailable => .{},
    };
}

/// Returns the global state action.
///
/// Asserts that the global state is initialized.
pub fn action() ?cli.ghostty.Action {
    return state.initialized.action;
}

/// Write a human-readable explanation of an `init` failure to stderr.
///
/// `init` reports a failure by returning an error, leaving `std.log` as the
/// only other sink, and libghostty silences that one: `Logging.stderr` above
/// defaults to false whenever `app_runtime` is `none`, before the args are
/// even parsed. Writing to the file directly is what gets a message to an
/// embedder that would otherwise have nothing but an exit code.
///
/// Every failure gets text, not just the argument-parsing ones. The rest
/// reach only an embedder that registered a `ghostty_log_set_callback`, and
/// that registration typically comes after the init it is trying to get
/// through.
///
/// The exe routes through here too. It touches no global state by design:
/// `init` can fail before `io_impl` exists, so the caller may have nothing
/// initialized to consult.
pub fn reportInitError(err: anyerror) void {
    var buf: [256]u8 = undefined;
    const message = initErrorText(err, &buf);

    // Streaming, not the seekable `writer`: an embedder may have pointed
    // stderr at a file it is also writing to itself, and a positional write
    // would start at offset 0 and overwrite what is already there. Streaming
    // writes go wherever the shared handle's file pointer sits.
    //
    // The global Io rather than `io()`: `init` can fail before it reaches
    // `io_impl`, which `io()` asserts has been initialized.
    std.Io.File.stderr().writeStreamingAll(
        std.Io.Threaded.global_single_threaded.io(),
        message,
    ) catch return;
}

/// The exact text `reportInitError` writes for `err`: the bespoke wording
/// where there is one, and otherwise a generic line naming the error, so no
/// failure leaves a caller with a status and nothing to print.
///
/// `buf` backs the generic line; the returned slice may point into it.
///
/// Split from the write, for the same reason as `initErrorMessage`.
fn initErrorText(err: anyerror, buf: []u8) []const u8 {
    return initErrorMessage(err) orelse std.fmt.bufPrint(
        buf,
        "Error: ghostty failed to initialize err={t}\n",
        .{err},
    ) catch "Error: ghostty failed to initialize.\n";
}

/// The bespoke text for `err`, or null for errors with no wording of their
/// own, which `initErrorText` covers with a generic line.
///
/// Split from the write so the mapping can be tested without capturing
/// stderr.
pub fn initErrorMessage(err: anyerror) ?[]const u8 {
    return switch (err) {
        error.MultipleActions => "Error: multiple CLI actions specified. You must specify only one\n" ++
            "action starting with the `+` character.\n",

        // Deliberately silent about the `+` convention. The only path to
        // this error is a `+` argument whose name is not in the action
        // enum, so explaining the prefix sends the reader hunting for a
        // mistake they did not make.
        error.InvalidAction => "Error: unknown CLI action specified.\n\n" ++
            "All valid CLI actions can be listed with the `+help` action.\n",

        else => null,
    };
}

test "initErrorMessage explains the CLI argument errors" {
    const testing = std.testing;

    // The exe and the C API both print these, so a reword lands in every
    // surface at once. Pinning the text keeps that deliberate.
    try testing.expectEqualStrings(
        "Error: unknown CLI action specified.\n\n" ++
            "All valid CLI actions can be listed with the `+help` action.\n",
        initErrorMessage(cli.action.DetectError.InvalidAction).?,
    );
    try testing.expectEqualStrings(
        "Error: multiple CLI actions specified. You must specify only one\n" ++
            "action starting with the `+` character.\n",
        initErrorMessage(cli.action.DetectError.MultipleActions).?,
    );
}

test "initErrorMessage has no bespoke wording for the rest" {
    try std.testing.expect(initErrorMessage(error.OutOfMemory) == null);
    try std.testing.expect(initErrorMessage(error.AlreadyInitialized) == null);
}

test "initErrorText still explains an error with no wording of its own" {
    // The generic line is what most failures get: `OutOfMemory` alone can
    // come from `allocTmpDir`, `detectArgs`, `ensureLocale`, `oni.init` and
    // `resourcesDir`.
    var buf: [256]u8 = undefined;
    try std.testing.expectEqualStrings(
        "Error: ghostty failed to initialize err=OutOfMemory\n",
        initErrorText(error.OutOfMemory, &buf),
    );

    // A bespoke message still wins, and does not touch the buffer.
    try std.testing.expectEqualStrings(
        initErrorMessage(error.InvalidAction).?,
        initErrorText(error.InvalidAction, &buf),
    );

    // Degrades rather than truncating into nonsense when the buffer cannot
    // hold the formatted line.
    var tiny: [4]u8 = undefined;
    try std.testing.expectEqualStrings(
        "Error: ghostty failed to initialize.\n",
        initErrorText(error.OutOfMemory, &tiny),
    );
}

test "a failed init leaves no usable state and is not retryable" {
    // This drives the real `init`, so establish the preconditions rather than
    // inheriting them, and put the state back afterwards. The `deinit` in the
    // defer matters for the case this test is not expecting: if `init` ever
    // succeeds here, bailing out without it would strand the GPA, the tmp
    // dir, the I/O impl and the resources dir on the rest of the suite.
    const prev = state;
    defer {
        if (state == .initialized) deinit();
        state = prev;
    }
    state = .uninitialized;

    // An unknown `+action` fails in `detectArgs`, the earliest error `init`
    // can return, so this never stands up signal handlers, oniguruma or the
    // resources dir. The args vector is platform-typed, so build it under a
    // comptime `if` that leaves the other branch unanalyzed.
    const vector: std.process.Args.Vector = if (comptime builtin.os.tag == .windows)
        std.unicode.utf8ToUtf16LeStringLiteral("ghostty +definitely-not-an-action")
    else
        &.{ "ghostty", "+definitely-not-an-action" };

    try std.testing.expectError(error.InvalidAction, init(.{ .main = .{
        .environ = std.testing.environ,
        .args = .{ .vector = vector },
    } }));

    // The whole point. Every accessor reaches through this tag, and a failed
    // init that left an `initialized` payload would hand out a dead allocator
    // and a deinitialized I/O impl instead of tripping.
    try std.testing.expect(state == .unavailable);

    // And that tag is also what makes the failure stick. `init` re-runs
    // one-shot process setup that nothing undoes.
    try std.testing.expectError(error.AlreadyInitialized, init(.{ .main = .{
        .environ = std.testing.environ,
        .args = .{ .vector = vector },
    } }));
}

test "logging falls back to defaults with no usable state" {
    const prev = state;
    defer state = prev;

    // `logFn` hits this after `init`'s errdefer has marked the state
    // unavailable, so it has to survive what the other accessors trap on. The
    // uninitialized tag covers a log emitted before `init` is ever called.
    state = .uninitialized;
    try std.testing.expectEqual(GlobalState.Logging{}, logging());
    state = .unavailable;
    try std.testing.expectEqual(GlobalState.Logging{}, logging());

    // The other branch. Flipped off the defaults so a regression that always
    // returned them - silently discarding a GHOSTTY_LOG override - fails here
    // whatever the defaults happen to be on this target.
    const defaults: GlobalState.Logging = .{};
    const flipped: GlobalState.Logging = .{
        .stderr = !defaults.stderr,
        .macos = !defaults.macos,
    };
    var live: GlobalState = undefined;
    live.logging = flipped;
    state = .{ .initialized = live };
    try std.testing.expectEqual(flipped, logging());
}

/// This represents the global process state. There should only
/// be one of these at any given moment. This is extracted into a dedicated
/// struct because it is reused by main and the static C lib.
pub const GlobalState = struct {
    const GPA = std.heap.DebugAllocator(.{});

    io_impl: std.Io.Threaded,
    gpa: ?GPA,
    alloc: std.mem.Allocator,
    environ: std.process.Environ,
    args: std.process.Args,
    tmp_dir_path: ?[]const u8,
    action: ?cli.ghostty.Action,
    logging: Logging,
    rlimits: ResourceLimits = .{},

    /// The app resources directory, equivalent to zig-out/share when we build
    /// from source. This is null if we can't detect it.
    resources_dir: internal_os.ResourcesDir,

    /// Where logging should go
    pub const Logging = packed struct {
        /// Whether to log to stderr. For lib mode we always disable stderr
        /// logging by default. Otherwise it's enabled by default.
        stderr: bool = build_config.app_runtime != .none,
        /// Whether to log to macOS's unified logging. Enabled by default
        /// on macOS.
        macos: bool = builtin.os.tag.isDarwin(),
    };

    /// Asserts that `self.io_impl` has been initialized.
    pub fn io(self: *GlobalState) std.Io {
        return self.io_impl.io();
    }

    fn initSignals() void {
        // Only posix systems.
        if (comptime builtin.os.tag == .windows) return;

        const p = std.posix;

        var sa: p.Sigaction = .{
            .handler = .{ .handler = p.SIG.IGN },
            .mask = p.sigemptyset(),
            .flags = 0,
        };

        // We ignore SIGPIPE because it is a common signal we may get
        // due to how we implement termio. When a terminal is closed we
        // often write to a broken pipe to exit the read thread. This should
        // be fixed one day but for now this helps make this a bit more
        // robust.
        p.sigaction(p.SIG.PIPE, &sa, null);
    }
};

/// Maintains the Unix resource limits that we set for our process. This
/// can be used to restore the limits to their original values.
pub const ResourceLimits = struct {
    nofile: ?internal_os.rlimit = null,

    pub fn init() ResourceLimits {
        return .{
            // Maximize the number of file descriptors we can have open
            // because we can consume a lot of them if we make many terminals.
            .nofile = internal_os.fixMaxFiles(),
        };
    }

    pub fn restore(self: *const ResourceLimits) void {
        if (self.nofile) |lim| internal_os.restoreMaxFiles(lim);
    }
};

test "init refuses a second call" {
    // Both tags the guard rejects: a live state, and one a failed `init` or a
    // `deinit` left behind. `opts` is undefined because the guard returns
    // before touching it - if it ever stopped, this would read an undefined
    // `Minimal` rather than quietly passing.
    const prev = state;
    defer state = prev;

    state = .{ .initialized = undefined };
    try std.testing.expectError(
        error.AlreadyInitialized,
        init(.{ .tool = undefined }),
    );

    state = .unavailable;
    try std.testing.expectError(
        error.AlreadyInitialized,
        init(.{ .tool = undefined }),
    );
}
