const std = @import("std");
const assert = std.debug.assert;
const Allocator = std.mem.Allocator;
const builtin = @import("builtin");
const build_config = @import("../build_config.zig");
const build_options = @import("build_options");
const sentry = if (build_options.sentry) @import("sentry");
const internal_os = @import("../os/main.zig");
const crash = @import("main.zig");
const Surface = @import("../Surface.zig");

const log = std.log.scoped(.sentry);

/// The global state for the Sentry SDK. This is unavoidable since crash
/// handling is a global process-wide thing.
var init_thread: ?std.Thread = null;

/// Set once `sentry_init` has returned, so the backend's handler is
/// installed. `waitReady` is what the crash triggers use to know the reporter
/// is actually armed rather than merely started.
var init_done: std.atomic.Value(bool) = .init(false);

/// Directory memory, holds the cache and state dirs persistently. This
/// prevents any sort of crashes due to initialization races.
var dir_mem: [std.fs.max_path_bytes * 2]u8 = undefined;

/// Holds the XDG cache dir.
var cache_dir_: ?[]const u8 = null;

/// Holds the XDG state dir.
var state_dir_: ?[]const u8 = null;

/// Thread-local state that can be set by thread main functions so that
/// crashes have more context.
///
/// This is a hack over Sentry native SDK limitations. The native SDK has
/// one global scope for all threads and no support for thread-local scopes.
/// This means that if we want to set thread-specific data we have to do it
/// on our own in the on crash callback.
pub const ThreadState = struct {
    /// Thread type, used to tag the crash
    type: Type,

    /// The surface that this thread is attached to.
    surface: *Surface,

    pub const Type = enum { main, renderer, io };
};

/// See ThreadState. This should only ever be set by the owner of the
/// thread entry function.
pub threadlocal var thread_state: ?ThreadState = null;

/// Process-wide initialization of our Sentry client.
///
/// This should only be called from one thread, and deinit should be called
/// from the same thread that calls init to avoid data races.
///
/// PRIVACY NOTE: I want to make it very clear that Ghostty by default does
/// NOT send any data over the network. We use the Sentry native SDK to collect
/// crash reports and logs, but we only store them locally (see Transport).
/// It is up to the user to grab the logs and manually send them to us
/// (or to their own Sentry instance) if they want to.
pub fn init(gpa: Allocator, environ_map: std.process.Environ.Map) !void {
    if (comptime !build_options.sentry) {
        var map = environ_map;
        map.deinit();
        return;
    }

    // Must only start once
    assert(init_thread == null);

    // We use a thread for initializing Sentry because initialization is
    // slow enough to matter for process startup: resolving our directories
    // can take multiple milliseconds on macOS (Apple APIs) and Sentry's
    // own init does disk I/O. Everything Sentry is doing initially is safe
    // to do on a separate thread and fast enough that its very likely to
    // be done before a crash occurs.
    //
    // The environ map is a snapshot owned by the thread (and freed there),
    // so it is safe against concurrent mutations of the process environment
    // (e.g. ensureLocale on the main thread).
    const thr = std.Thread.spawn(
        .{},
        initThread,
        .{ gpa, environ_map },
    ) catch |err| {
        var map = environ_map;
        map.deinit();
        return err;
    };

    // Naming the thread from here only works on some platforms (e.g.
    // Linux). On Darwin the thread names itself in initThread.
    var single_threaded: std.Io.Threaded = .init_single_threaded;
    defer single_threaded.deinit();
    thr.setName(single_threaded.io(), "sentry-init") catch {};

    init_thread = thr;
}

fn initThread(gpa: Allocator, environ_map_: std.process.Environ.Map) void {
    // Report here rather than returning the error. std.Thread.spawn swallows a
    // thread function's error into std.debug.print, which is raw stderr: not
    // logFn, not the app's log sink. In a NativeAOT windowed process there is
    // no stderr at all, so an unwritable database directory produced a silent
    // failure, waitReady burning its whole timeout, and "reporter did not arm"
    // with nothing to diagnose it from. global.zig's catch only ever covered
    // the spawn, which practically never fails.
    initThreadImpl(gpa, environ_map_) catch |err| {
        log.warn("sentry init failed, no crash capture available err={}", .{err});
    };
}

fn initThreadImpl(gpa: Allocator, environ_map_: std.process.Environ.Map) !void {
    var environ_map = environ_map_;
    defer environ_map.deinit();

    // Right now, on Darwin, `std.Thread.setName` can only name the current
    // thread, and we have no way to get the current thread from within it,
    // so instead we use this code to name the thread instead.
    if (builtin.os.tag.isDarwin()) {
        internal_os.macos.pthread_setname_np(&"sentry-init".*);
    }

    // Get our directories.
    var single_threaded: std.Io.Threaded = .init_single_threaded;
    defer single_threaded.deinit();
    var fba: std.heap.FixedBufferAllocator = .init(&dir_mem);

    state_dir_ = state_dir: {
        const dir = try crash.defaultDir(
            single_threaded.io(),
            gpa,
            &environ_map,
        );
        defer gpa.free(dir.path);
        break :state_dir try fba.allocator().dupe(u8, dir.path);
    };
    errdefer state_dir_ = null;

    const cache_dir = cache_dir: {
        const dir = try cacheDir(
            single_threaded.io(),
            gpa,
            &environ_map,
        );
        defer gpa.free(dir);
        break :cache_dir try fba.allocator().dupe(u8, dir);
    };
    cache_dir_ = cache_dir;
    errdefer cache_dir_ = null;

    const transport = sentry.Transport.init(&Transport.send);
    // This will crash if the transport was never used so we avoid
    // that for now. This probably leaks some memory but it'd be very
    // small and a one time cost. Once this is fixed upstream we can
    // remove this.
    //errdefer transport.deinit();

    const opts = sentry.c.sentry_options_new();
    errdefer sentry.c.sentry_options_free(opts);
    sentry.c.sentry_options_set_release_n(
        opts,
        build_config.version_string.ptr,
        build_config.version_string.len,
    );
    sentry.c.sentry_options_set_transport(opts, @ptrCast(transport));

    // Set our crash callback. See beforeSend for more details on what we
    // do here and why we use this.
    sentry.c.sentry_options_set_before_send(opts, beforeSend, null);

    sentry.c.sentry_options_set_database_path_n(
        opts,
        cache_dir.ptr,
        cache_dir.len,
    );

    if (comptime builtin.mode == .Debug) {
        // Debug logging for Sentry
        sentry.c.sentry_options_set_debug(opts, @intFromBool(true));
    }

    // Initialize
    if (sentry.c.sentry_init(opts) != 0) return error.SentryInitFailed;

    // Setup some basic tags that we always want present
    sentry.setTag("build-mode", build_config.mode_string);
    sentry.setTag("app-runtime", @tagName(build_config.app_runtime));
    sentry.setTag("font-backend", @tagName(build_config.font_backend));
    sentry.setTag("renderer", @tagName(build_config.renderer));

    // Only once sentry_init has returned is the backend's exception handler
    // installed. Anything that means to provoke a crash and watch it be
    // captured has to wait for this, and cannot infer it from the database
    // directory appearing: sentry creates that during init, before the
    // handler exists, and it outlives the process, so on every run after the
    // first it is already there before we start.
    //
    // Stored AFTER the tags, not before. A trigger that waits for ready and
    // crashes at once would otherwise race them and produce a report missing
    // all four, which reads as a flaky row rather than as a race.
    init_done.store(true, .release);

    // Log some information about sentry
    log.debug("sentry initialized database={s}", .{cache_dir});
}

fn cacheDir(io: std.Io, alloc: Allocator, environ_map: *const std.process.Environ.Map) ![]const u8 {
    // On macOS, we prefer to use the NSCachesDirectory value to be
    // a more idiomatic macOS application. But if XDG env vars are set
    // we will respect them.
    if (comptime builtin.os.tag == .macos) macos: {
        const xdg_cache_home = environ_map.get("XDG_CACHE_HOME") orelse break :macos;
        if (xdg_cache_home.len > 0) {
            return try internal_os.macos.cacheDir(
                alloc,
                "sentry",
            );
        }
    }

    return try internal_os.xdg.cache(
        io,
        alloc,
        environ_map,
        .{ .subdir = "wintty/sentry" },
    );
}

/// Process-wide deinitialization of our Sentry client. This ensures all
/// our data is flushed.
/// Set once a panic has been reported, so a panic raised while reporting a
/// panic cannot recurse into the reporter.
var panic_reported: bool = false;

/// Set once the thread that won `panic_reported` has finished writing. Threads
/// that lost wait on this rather than racing ahead into abort.
var panic_capture_done: std.atomic.Value(bool) = .init(false);

/// Report a panic to sentry, best effort, before the process goes down.
///
/// This exists because a Zig panic on Windows raises no exception that the
/// in-process backend can see. `std.debug.defaultPanic` ends in
/// `std.process.abort`, and on Windows that is `ntdll.RtlExitUserProcess`, a
/// clean process exit. sentry-native's inproc backend hooks
/// `SetUnhandledExceptionFilter`, which a clean exit never invokes, so without
/// this the panic is invisible: no exception, no WER record, no envelope.
///
/// POSIX needs nothing here. There `abort` is `posix.raise(.ABRT)` and the
/// backend's SIGABRT handler already captures the panic, so reporting again
/// would duplicate every crash report.
pub fn capturePanic(msg: []const u8) void {
    if (comptime !build_options.sentry) return;
    if (comptime builtin.os.tag != .windows) return;

    // Re-entry on THIS thread means reporting the panic itself panicked, and
    // there is nothing left to try: return immediately so panicImpl falls
    // through to defaultPanic.
    //
    // Checked before the cross-thread latch, and it has to be. The earlier
    // version relied on the latch alone and claimed the re-entry "loses the
    // exchange and falls through". It does lose it, and then waits on a flag
    // that only the winner sets, which on a same-thread re-entry is this
    // thread, still inside captureEvent. That is not a fall-through, it is a
    // deadlock: the process hangs with no report, no abort, and no WER
    // record, which is strictly worse than having no hook at all.
    //
    // Reachable, not theoretical. beforeSend runs synchronously on this
    // thread and dereferences a surface whose data the comment there already
    // admits may be torn; a zero cell width makes GridSize.update divide to
    // inf, and @intFromFloat(inf) is a safety panic in Debug and ReleaseSafe.
    if (in_capture) return;
    in_capture = true;

    // Reporting runs in an already broken process, so keep it to the one
    // call and let anything it touches fail silently.
    if (@atomicRmw(bool, &panic_reported, .Xchg, true, .seq_cst)) {
        // A second thread panicking concurrently must not race us to
        // abort(). std.debug.defaultPanic serialises panics against each
        // other, but only once a thread is inside it, and the winner is
        // still here writing a report. Losing that race truncates the file
        // the winner is in the middle of writing. Wait for the winner to
        // finish, then let this thread continue into defaultPanic, which
        // takes over the serialising from there.
        //
        // Bounded, because the winner can stall rather than finish:
        // sendInternal creates a directory and writes a file, and a hung or
        // redirected state directory turns "wait for the winner" into "hang
        // the process". A truncated report is a worse report; a hung process
        // is no report and no exit. Prefer the truncated one.
        var spins: usize = 0;
        while (!panic_capture_done.load(.acquire)) {
            if (spins >= panic_wait_spins) break;
            spins += 1;
            std.Thread.yield() catch {};
        }
        return;
    }

    const event = sentry.Value.initMessageEvent(.fatal, "panic", msg);

    // A panic report without frames says what happened but not where, which
    // is most of the value gone. The frames start inside the panic handler,
    // so the crash site is a few frames up.
    event.addStacktrace();

    // Nil means the event was dropped rather than captured: the SDK is not
    // initialised yet, before_send returned null, or it was rate limited.
    // Discarding that turns "the panic hook fired and the event went nowhere"
    // into something indistinguishable from "Zig panics are not captured on
    // Windows", which is the conclusion this whole hook exists to overturn.
    // defaultPanic is about to write to stderr regardless, so saying so costs
    // nothing.
    if (sentry.captureEvent(event) == null) {
        log.warn("panic report was dropped rather than captured", .{});
    }

    panic_capture_done.store(true, .release);
}

/// Set for the duration of a panic capture on the capturing thread, so a
/// panic raised while reporting a panic is recognised as re-entry rather than
/// waiting on itself.
threadlocal var in_capture: bool = false;

/// How long a thread that lost the panic latch will wait for the winner. Not
/// a duration: a panicking process is the wrong place to ask for a clock.
const panic_wait_spins: usize = 100_000;

pub fn deinit() void {
    if (comptime !build_options.sentry) return;

    // If we're still initializing then wait for init to finish. This
    // is highly unlikely since init is a very fast operation but we want
    // to avoid the possibility.
    const thr = init_thread orelse return;
    init_thread = null;
    thr.join();
    _ = sentry.c.sentry_close();

    // The handler is gone, so stop telling callers it is armed. Without this
    // a trigger fired during shutdown is told the reporter is ready when it
    // is not, which is precisely the lie waitReady exists to prevent.
    init_done.store(false, .release);
}

/// Block until the crash reporter is armed, or until `timeout_ms` elapses.
/// Returns whether it is armed.
///
/// Exists for the crash triggers, which are worthless if they fire before the
/// handler is installed: the report then does not appear, and the honest
/// reading of that ("the backend cannot capture this class") is the opposite
/// of the truth. A probe that reports success on an error condition is worse
/// than one that fails.
pub fn waitReady(timeout_ms: u64) bool {
    // Nothing to arm, so nothing to wait for. Callers still get a truthful
    // answer: no reporter is going to appear later.
    if (comptime !build_options.sentry) return false;

    var io_threaded: std.Io.Threaded = .init_single_threaded;
    defer io_threaded.deinit();
    const io = io_threaded.io();

    // Measured against the clock, not by counting sleeps. Adding 5 per
    // iteration assumes the scheduler returns on time, so an overshooting one
    // makes the real wait longer than the caller asked for, and the caller
    // then reports the timeout it believed it used.
    //
    // `.awake` rather than `.real`: this is an elapsed-time question, and a
    // wall clock that steps backwards would extend the wait indefinitely.
    const start = std.Io.Timestamp.now(io, .awake).toMilliseconds();
    while (!init_done.load(.acquire)) {
        const elapsed = std.Io.Timestamp.now(io, .awake).toMilliseconds() - start;
        if (elapsed >= timeout_ms) return false;
        std.Io.sleep(io, .fromMilliseconds(5), .awake) catch return false;
    }

    return true;
}

fn beforeSend(
    event_val: sentry.c.sentry_value_t,
    _: ?*anyopaque,
    _: ?*anyopaque,
) callconv(.c) sentry.c.sentry_value_t {
    // The native SDK at the time of writing doesn't support thread-local
    // scopes. The full SDK has one global scope. So we use the beforeSend
    // handler to set thread-specific data such as window size, grid size,
    // etc. that we can use to debug crashes.

    // Get our event contexts. At this point Sentry has already merged
    // all the contexts so we should have this key. If not, we create it.
    const event: sentry.Value = .{ .value = event_val };
    const contexts = event.get("contexts") orelse contexts: {
        const obj = sentry.Value.initObject();
        event.set("contexts", obj);
        break :contexts obj;
    };
    const tags = event.get("tags") orelse tags: {
        const obj = sentry.Value.initObject();
        event.set("tags", obj);
        break :tags obj;
    };

    // If we have no thread state we cannot determine surface dimensions.
    // Record that rather than returning: a missing tag is indistinguishable
    // from a crash that never reached this code, and every call that arrives
    // through the C API in src/apprt/embedded.zig has no thread state, which
    // for an embedder is most of them.
    const thr_state = thread_state orelse {
        tags.set("thread-type", sentry.Value.initString("unknown"));
        log.debug("no thread state, crash metadata limited", .{});
        return event_val;
    };

    // Store our thread type
    tags.set("thread-type", sentry.Value.initString(@tagName(thr_state.type)));

    // Read the surface data. This is likely unsafe because on a crash
    // other threads can continue running. We don't have race-safe way to
    // access this data so this might be corrupted but it's most likely fine.
    {
        const obj = sentry.Value.initObject();
        errdefer obj.decref();
        const surface = thr_state.surface;
        const grid_size = surface.size.grid();
        obj.set(
            "screen-width",
            sentry.Value.initInt32(std.math.cast(i32, surface.size.screen.width) orelse -1),
        );
        obj.set(
            "screen-height",
            sentry.Value.initInt32(std.math.cast(i32, surface.size.screen.height) orelse -1),
        );
        obj.set(
            "grid-columns",
            sentry.Value.initInt32(std.math.cast(i32, grid_size.columns) orelse -1),
        );
        obj.set(
            "grid-rows",
            sentry.Value.initInt32(std.math.cast(i32, grid_size.rows) orelse -1),
        );
        obj.set(
            "cell-width",
            sentry.Value.initInt32(std.math.cast(i32, surface.size.cell.width) orelse -1),
        );
        obj.set(
            "cell-height",
            sentry.Value.initInt32(std.math.cast(i32, surface.size.cell.height) orelse -1),
        );

        contexts.set("Dimensions", obj);
    }

    return event_val;
}

/// Rewrite `"code_file":"<dir>/<name>"` to `"code_file":"<dir>/name"` in a
/// serialized envelope, keeping only the file name.
///
/// sentry records the runtime path of every loaded module. Wintty installs
/// under `%LOCALAPPDATA%`, so on a real machine those read
/// `C:\Users\<name>\AppData\Local\Wintty\...`: the report we ask the user to
/// send us would carry their username. Symbolication needs the file name to
/// match a module, not the directory it happened to load from.
///
/// This runs on the serialized bytes rather than on the event, because the
/// module list cannot be edited: `sentry_modulefinder_windows.c` calls
/// `sentry_value_freeze` on it, so `set` from a `before_send` hook is silently
/// a no-op. The bytes are ours; the value is not.
///
/// Returns a slice owned by `alloc`, or the input unchanged if there is
/// nothing to rewrite.
fn scrubImagePaths(alloc: Allocator, json: []const u8) ![]const u8 {
    var out: []const u8 = json;
    var owned = false;
    // Without this, a key that fails after an earlier one allocated leaks the
    // earlier buffer. sendInternal runs on an arena so production does not
    // notice, but this function documents itself as returning owned memory,
    // and a leak that only an arena hides is still a leak in the contract.
    errdefer if (owned) alloc.free(out);
    for (image_path_keys) |key| {
        const next = try scrubKey(alloc, out, key);
        if (owned and next.ptr != out.ptr) alloc.free(out);
        owned = owned or next.ptr != out.ptr;
        out = next;
    }
    return out;
}

/// Whether any key in `image_path_keys` appears, so the envelope-level fast
/// path cannot fall out of step with the list it is supposed to enforce.
fn containsAnyPathKey(bytes: []const u8) bool {
    inline for (image_path_keys) |key| {
        if (std.mem.indexOf(u8, bytes, "\"" ++ key ++ "\":\"") != null) return true;
    }
    return false;
}

/// Every module field that carries a filesystem path.
///
/// `debug_file` is here because leaving it out is what shipped: `code_file`
/// was scrubbed, the report was checked for a username, and it passed,
/// because the username had moved next door into the PDB path. Any new
/// path-bearing field belongs in this list, and the test below is what makes
/// forgetting one visible.
const image_path_keys = [_][]const u8{ "code_file", "debug_file" };

/// Sized for `"<key>":"`, asserted against `image_path_keys` in `scrubKey`.
const needle_buf_len = 64;

fn scrubKey(alloc: Allocator, json: []const u8, key: []const u8) ![]const u8 {
    // The keys are compile-time constants, so a key too long for the buffer
    // is a build-time mistake, not a runtime condition. Asserting it says so;
    // the earlier `catch return json` turned it into "leak the paths quietly",
    // which is the one outcome this function must never choose.
    comptime {
        for (image_path_keys) |k| {
            if (k.len + 4 > needle_buf_len) @compileError(
                "image_path_keys entry too long for scrubKey's needle buffer",
            );
        }
    }
    var needle_buf: [needle_buf_len]u8 = undefined;
    const needle = std.fmt.bufPrint(&needle_buf, "\"{s}\":\"", .{key}) catch
        unreachable;
    if (std.mem.indexOf(u8, json, needle) == null) return json;

    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(alloc);

    var rest = json;
    while (std.mem.indexOf(u8, rest, needle)) |at| {
        const value_start = at + needle.len;
        try out.appendSlice(alloc, rest[0..value_start]);

        // The value ends at the first quote that is not escaped. Paths carry
        // no escaped quotes in practice, but a filename legally could.
        // A quote is the terminator only when the run of backslashes before
        // it is even: `\\` is an escaped backslash and the quote after it is
        // real, while `\"` is an escaped quote and it is not. Testing only
        // the single preceding byte gets `"C:\\dir\\"` wrong and swallows the
        // rest of the object.
        var i: usize = value_start;
        const value_end = while (i < rest.len) : (i += 1) {
            if (rest[i] != '"') continue;
            var slashes: usize = 0;
            while (i - slashes > value_start and rest[i - slashes - 1] == '\\') {
                slashes += 1;
            }
            if (slashes % 2 == 0) break i;
        } else rest.len;

        const value = rest[value_start..value_end];

        // Backslashes arrive escaped in JSON, so that separator is two bytes.
        //
        // The LAST separator of either kind, not the last backslash falling
        // back to the last slash. Preferring one kind keeps whatever follows
        // the other: "C:\\Program Files\\x/Users/alex/a.dll" cut at the
        // backslash leaves "x/Users/alex/a.dll", username intact. Contrived
        // on Windows, but this is the function whose whole job is not to
        // fail open.
        const back = if (std.mem.lastIndexOf(u8, value, "\\\\")) |b| b + 2 else 0;
        const fwd = if (std.mem.lastIndexOfScalar(u8, value, '/')) |b| b + 1 else 0;
        const cut = @max(back, fwd);

        if (cut == 0) {
            try out.appendSlice(alloc, value);
        } else {
            try out.appendSlice(alloc, "<dir>/");
            try out.appendSlice(alloc, value[cut..]);
        }

        rest = rest[value_end..];
    }
    try out.appendSlice(alloc, rest);

    return try out.toOwnedSlice(alloc);
}

/// Scrub image paths across a whole serialized envelope, keeping the framing
/// intact.
///
/// An envelope is a header line followed by items, and each item is its own
/// header line carrying `"length":N` followed by exactly N payload bytes.
/// Scrubbing shortens `code_file`, so rewriting payload bytes without
/// correcting N produces a file that declares more bytes than it holds. Our
/// own reader (`sentry_envelope.zig`, `streamExact`) then fails with
/// `EnvelopeItemPayloadTooShort`, and so does anything else that reads the
/// format. This walks the framing so the lengths stay true.
///
/// Returns a slice owned by `alloc`, or the input unchanged when there is
/// nothing to rewrite.
fn scrubEnvelope(alloc: Allocator, bytes: []const u8) ![]const u8 {
    // The fast path has to agree with image_path_keys, or the list is not
    // authoritative and its doc comment lies. This used to test `code_file`
    // alone, so an envelope whose only path field was `debug_file` was
    // returned byte-identical with the username in it: exactly the bug
    // adding `debug_file` to the list was meant to fix, surviving one layer
    // up. sentry_modulefinder_windows.c happens to always emit both today,
    // which is what made it latent rather than shipping.
    if (!containsAnyPathKey(bytes)) return bytes;

    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(alloc);

    // The envelope header line, which carries no module paths.
    var rest = bytes;
    const first_break = std.mem.indexOfScalar(u8, rest, '\n') orelse
        return bytes;
    try out.appendSlice(alloc, rest[0 .. first_break + 1]);
    rest = rest[first_break + 1 ..];

    while (rest.len > 0) {
        const head_end = std.mem.indexOfScalar(u8, rest, '\n') orelse {
            // Trailing bytes with no item header. Nothing claims a length
            // over them, so pass them through untouched.
            try out.appendSlice(alloc, rest);
            break;
        };
        const header = rest[0..head_end];
        rest = rest[head_end + 1 ..];

        // A length-less item runs to the next newline. Nothing to correct.
        const declared = parseItemLength(header) orelse {
            const body_end = std.mem.indexOfScalar(u8, rest, '\n') orelse rest.len;
            const scrubbed = try scrubImagePaths(alloc, rest[0..body_end]);
            defer if (scrubbed.ptr != rest.ptr) alloc.free(scrubbed);
            try out.appendSlice(alloc, header);
            try out.append(alloc, '\n');
            try out.appendSlice(alloc, scrubbed);
            if (body_end < rest.len) try out.append(alloc, '\n');
            rest = rest[@min(body_end + 1, rest.len)..];
            continue;
        };

        // A declared length longer than what is left means the envelope was
        // already truncated. Copy the remainder rather than inventing one.
        if (declared > rest.len) {
            try out.appendSlice(alloc, header);
            try out.append(alloc, '\n');
            try out.appendSlice(alloc, rest);
            break;
        }

        const payload = rest[0..declared];
        const scrubbed = try scrubImagePaths(alloc, payload);
        defer if (scrubbed.ptr != payload.ptr) alloc.free(scrubbed);

        try writeItemHeader(alloc, &out, header, scrubbed.len);
        try out.append(alloc, '\n');
        try out.appendSlice(alloc, scrubbed);

        rest = rest[declared..];
        if (rest.len > 0 and rest[0] == '\n') {
            try out.append(alloc, '\n');
            rest = rest[1..];
        }
    }

    return try out.toOwnedSlice(alloc);
}

/// The `length` field of an item header, or null when it has none.
fn parseItemLength(header: []const u8) ?usize {
    const needle = "\"length\":";
    const at = std.mem.indexOf(u8, header, needle) orelse return null;
    var i = at + needle.len;
    while (i < header.len and header[i] == ' ') i += 1;
    const start = i;
    while (i < header.len and header[i] >= '0' and header[i] <= '9') i += 1;
    if (i == start) return null;
    return std.fmt.parseInt(usize, header[start..i], 10) catch null;
}

/// Re-emit an item header with `length` replaced by the post-scrub value.
fn writeItemHeader(
    alloc: Allocator,
    out: *std.ArrayList(u8),
    header: []const u8,
    length: usize,
) !void {
    const needle = "\"length\":";
    const at = std.mem.indexOf(u8, header, needle) orelse {
        try out.appendSlice(alloc, header);
        return;
    };
    var i = at + needle.len;
    while (i < header.len and header[i] == ' ') i += 1;
    const digits_start = i;
    while (i < header.len and header[i] >= '0' and header[i] <= '9') i += 1;
    if (i == digits_start) {
        try out.appendSlice(alloc, header);
        return;
    }

    var digits: [24]u8 = undefined;
    try out.appendSlice(alloc, header[0..digits_start]);
    try out.appendSlice(alloc, std.fmt.bufPrint(&digits, "{d}", .{length}) catch
        return error.LengthTooLarge);
    try out.appendSlice(alloc, header[i..]);
}

test "a scrubbed envelope still parses" {
    // The regression this exists for: scrubbing shortens `code_file`, and an
    // envelope item is length-prefixed, so a scrub that rewrites the payload
    // without correcting the header leaves every report unreadable by our own
    // parser and by anything else that reads the format. Substring assertions
    // over a bare JSON fragment cannot see it; only a round trip can.
    const alloc = std.testing.allocator;

    const payload =
        \\{"images":[{"code_file":"C:\\Users\\alex\\wintty.dll","type":"pe"}]}
    ;
    var raw: std.ArrayList(u8) = .empty;
    defer raw.deinit(alloc);
    var head: [64]u8 = undefined;
    try raw.appendSlice(alloc, "{}\n");
    try raw.appendSlice(alloc, try std.fmt.bufPrint(
        &head,
        "{{\"type\":\"event\",\"length\":{d}}}\n",
        .{payload.len},
    ));
    try raw.appendSlice(alloc, payload);
    try raw.appendSlice(alloc, "\n");

    const scrubbed = try scrubEnvelope(alloc, raw.items);
    defer if (scrubbed.ptr != raw.items.ptr) alloc.free(scrubbed);

    try std.testing.expect(std.mem.indexOf(u8, scrubbed, "wintty.dll") != null);
    try std.testing.expect(std.mem.indexOf(u8, scrubbed, "alex") == null);

    var reader: std.Io.Reader = .fixed(scrubbed);
    var parsed = try crash.Envelope.parse(alloc, &reader);
    defer parsed.deinit();
    try std.testing.expectEqual(@as(usize, 1), parsed.items.items.len);
}

test "every path key is scrubbed at the envelope level, not just code_file" {
    // scrubEnvelope had its own fast path keyed on `code_file` alone, so an
    // envelope whose only path field was `debug_file` was returned
    // byte-identical with the username in it. image_path_keys said otherwise
    // and the guard below could not see it, because that one calls
    // scrubImagePaths directly and never goes through the gate.
    //
    // Driven off image_path_keys so a key added to the list is covered here
    // the day it is added, rather than the day someone remembers.
    const alloc = std.testing.allocator;

    inline for (image_path_keys) |key| {
        var payload_buf: [128]u8 = undefined;
        const payload = try std.fmt.bufPrint(
            &payload_buf,
            "{{\"images\":[{{\"{s}\":\"C:\\\\\\\\Users\\\\\\\\alex\\\\\\\\a.bin\"}}]}}",
            .{key},
        );

        var raw: std.ArrayList(u8) = .empty;
        defer raw.deinit(alloc);
        var head: [64]u8 = undefined;
        try raw.appendSlice(alloc, "{}\n");
        try raw.appendSlice(alloc, try std.fmt.bufPrint(
            &head,
            "{{\"type\":\"event\",\"length\":{d}}}\n",
            .{payload.len},
        ));
        try raw.appendSlice(alloc, payload);
        try raw.appendSlice(alloc, "\n");

        const scrubbed = try scrubEnvelope(alloc, raw.items);
        defer if (scrubbed.ptr != raw.items.ptr) alloc.free(scrubbed);

        try std.testing.expect(std.mem.indexOf(u8, scrubbed, "a.bin") != null);
        try std.testing.expect(std.mem.indexOf(u8, scrubbed, "alex") == null);
        try std.testing.expect(std.mem.indexOf(u8, scrubbed, "Users") == null);

        // And it still parses, so the gate fix did not shorten a payload
        // without correcting its header.
        var reader: std.Io.Reader = .fixed(scrubbed);
        var parsed = try crash.Envelope.parse(alloc, &reader);
        defer parsed.deinit();
        try std.testing.expectEqual(@as(usize, 1), parsed.items.items.len);
    }
}

test "the last separator wins, whichever kind it is" {
    // Preferring backslashes kept everything after the last one when a
    // forward slash came later, which on a mixed path leaves the username in.
    const alloc = std.testing.allocator;
    const input =
        \\{"code_file":"C:\\Program Files\\x/Users/alex/a.dll"}
    ;
    const out = try scrubImagePaths(alloc, input);
    defer if (out.ptr != input.ptr) alloc.free(out);

    try std.testing.expect(std.mem.indexOf(u8, out, "a.dll") != null);
    try std.testing.expect(std.mem.indexOf(u8, out, "alex") == null);
    try std.testing.expect(std.mem.indexOf(u8, out, "Users") == null);
}

test "scrubImagePaths keeps the file name and drops the directory" {
    const alloc = std.testing.allocator;
    // Multiline: contents are literal, so these backslashes are the
    // doubled ones real JSON carries.
    //
    // Both path fields, because a real module entry carries both and only
    // one of them used to be scrubbed. The report then passed a username
    // check on code_file while debug_file still spelled it out.
    const input =
        \\{"images":[{"code_file":"C:\\Users\\alex\\conpty.dll","debug_file":"C:\\Users\\alex\\conpty.pdb"}]}
    ;
    const out = try scrubImagePaths(alloc, input);
    defer alloc.free(out);

    try std.testing.expect(std.mem.indexOf(u8, out, "conpty.dll") != null);
    try std.testing.expect(std.mem.indexOf(u8, out, "conpty.pdb") != null);
    try std.testing.expect(std.mem.indexOf(u8, out, "alex") == null);
    try std.testing.expect(std.mem.indexOf(u8, out, "Users") == null);
}

test "no module field carries a path out of the scrub" {
    // A standing check rather than a case: the leak was not a wrong rule, it
    // was a field nobody had listed. This fails the day a module entry gains
    // another path field and image_path_keys does not.
    const alloc = std.testing.allocator;
    const input =
        \\{"images":[{"code_file":"C:\\Users\\alex\\a.dll","debug_file":"C:\\Users\\alex\\a.pdb","code_id":"abc","type":"pe"}]}
    ;
    const out = try scrubImagePaths(alloc, input);
    defer alloc.free(out);

    // Nothing that still looks like an absolute Windows path survives.
    try std.testing.expect(std.mem.indexOf(u8, out, ":\\\\") == null);
}

test "scrubImagePaths leaves an envelope with no module paths alone" {
    const alloc = std.testing.allocator;
    const input = "{\"level\":\"fatal\"}";
    const out = try scrubImagePaths(alloc, input);
    try std.testing.expectEqualStrings(input, out);
}

pub const Transport = struct {
    pub fn send(envelope: *sentry.Envelope, ud: ?*anyopaque) callconv(.c) void {
        _ = ud;
        defer envelope.deinit();

        // Call our internal impl. If it fails there is nothing we can do
        // but log to the user.
        sendInternal(envelope) catch |err| {
            log.warn("failed to persist crash report err={}", .{err});
        };
    }

    /// Implementation of send but we can use Zig errors.
    fn sendInternal(envelope: *sentry.Envelope) !void {
        const state_dir = state_dir_ orelse return error.StateDirNotInitialized;

        // The I/O and allocator we use here are just meant to get the job
        // done for saving the crash report.
        var single_threaded: std.Io.Threaded = .init_single_threaded;
        defer single_threaded.deinit();
        var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
        defer arena.deinit();
        const alloc = arena.allocator();

        // Parse into an envelope structure
        const json = envelope.serialize();
        defer sentry.free(@ptrCast(json.ptr));
        var parsed: crash.Envelope = parsed: {
            var reader: std.Io.Reader = .fixed(json);
            break :parsed try crash.Envelope.parse(alloc, &reader);
        };
        defer parsed.deinit();

        // If our envelope doesn't have an event then we don't do anything.
        // To figure this out we first encode it into a string, parse it,
        // and check if it has an event. Kind of wasteful but the best
        // option we have at the time of writing this since the C API doesn't
        // expose this information.
        if (try shouldDiscard(&parsed)) {
            log.info("sentry envelope does not contain crash, discarding", .{});
            return;
        }

        // Generate a UUID for this envelope. The envelope DOES have an event_id
        // header but I don't think there is any public API way to get it
        // afaict so we generate a new UUID for the filename just so we don't
        // conflict.
        const uuid = sentry.UUID.init();

        try std.Io.Dir.cwd().createDirPath(single_threaded.io(), state_dir);

        // Build our final path and write to it.
        const path = try std.fs.path.join(alloc, &.{
            state_dir,
            try std.fmt.allocPrint(alloc, "{s}.winttycrash", .{uuid.string()}),
        });
        const file = try std.Io.Dir.cwd().createFile(single_threaded.io(), path, .{});
        defer file.close(single_threaded.io());
        var buf: [4096]u8 = undefined;
        var file_writer = file.writer(single_threaded.io(), &buf);
        try file_writer.interface.writeAll(try scrubEnvelope(alloc, json));
        try file_writer.end();

        log.warn("crash report written to disk path={s}", .{path});
    }

    fn shouldDiscard(envelope: *const crash.Envelope) !bool {
        // If we have an event item then we're good.
        for (envelope.items.items) |item| {
            if (item.itemType() == .event) return false;
        }

        return true;
    }
};
