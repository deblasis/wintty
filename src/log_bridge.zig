//! Embedder log bridge for libghostty.
//!
//! Exports a C API that lets the embedder (e.g. the Windows C# shell)
//! register a callback to receive every std.log message produced by
//! libghostty. The callback is invoked from inside logFn in
//! main_ghostty.zig, alongside the existing macOS-unified-log and stderr
//! sinks. On Windows this is the only sink that actually reaches the
//! user: the WinUI 3 GUI-subsystem exe has no console and macOS unified
//! log is not available, so Zig logs otherwise vanish.
//!
//! Level mapping contract (stable, do not change these integers once
//! embedders exist):
//!   0 -> debug
//!   1 -> info
//!   2 -> warn
//!   3 -> err
//!
//! Thread safety: logFn may be called from any thread, so the global
//! callback pointer is loaded with an atomic read. The callback itself
//! must be reentrant-safe and must not call back into libghostty's
//! logger in a way that would deadlock.
//!
//! Encoding: scope bytes are ASCII (they come from Zig enum tag names).
//! Message bytes may be arbitrary UTF-8 (paths, shell command values,
//! etc.). We pass raw bytes in both cases; the embedder decodes.

const std = @import("std");
const builtin = @import("builtin");

/// Stable integer mapping for std.log.Level. std.log.Level's own
/// ordinals are not part of any public contract and could shift across
/// Zig versions; by going through this enum the ABI seen by embedders
/// is pinned regardless of std internals. Keep the values synchronized
/// with the embedder (see the level mapping contract in the file doc
/// comment above).
pub const Level = enum(u32) {
    debug = 0,
    info = 1,
    warn = 2,
    err = 3,
};

/// Callback signature the embedder registers. The scope and message
/// bytes are NOT null-terminated; use the companion length. user_data
/// is echoed verbatim from the registration call.
pub const LogCallback = ?*const fn (
    level: u32,
    scope_ptr: [*]const u8,
    scope_len: usize,
    message_ptr: [*]const u8,
    message_len: usize,
    user_data: ?*anyopaque,
) callconv(.c) void;

/// Maximum rendered message length in bytes. Messages longer than this
/// are truncated and get a suffix appended (see truncated_suffix).
/// The bound exists so we can render the formatted message into a
/// fixed stack buffer per log call and never allocate. 64 KiB is far
/// larger than any real log line but small enough to fit comfortably on
/// the stack of any Zig-created thread.
pub const max_message_bytes: usize = 64 * 1024;

/// Appended to the end of a truncated message so consumers can tell.
/// Chosen short and ASCII so it always fits in the final bytes of the
/// buffer without further truncation considerations.
pub const truncated_suffix: []const u8 = " [truncated]";

// Global callback state. Split across two atomics so the function
// pointer and user_data stay in sync at the moment of registration:
// both are stored atomically, and the dispatch path reads the callback
// pointer first, then user_data. A benign race is possible where a
// caller tears down the callback between the two loads and we pass
// stale user_data into a null callback - but we always check the
// callback pointer for null before invoking, so no harm occurs.
//
// We store the function pointer as a usize (its bit pattern) rather
// than a typed pointer because std.atomic.Value does not support
// function-pointer generic args. A zero value means "no callback".
var cb_bits: std.atomic.Value(usize) = std.atomic.Value(usize).init(0);
var cb_user_data: std.atomic.Value(usize) = std.atomic.Value(usize).init(0);

/// Install (or clear) the embedder log callback. Pass null to clear.
/// Safe to call from any thread.
pub fn setCallback(cb: LogCallback, user_data: ?*anyopaque) void {
    const cb_as_int: usize = if (cb) |fn_ptr| @intFromPtr(fn_ptr) else 0;
    const ud_as_int: usize = if (user_data) |ud| @intFromPtr(ud) else 0;
    // Release ordering on the store pair so any writes the embedder
    // did to memory it will hand back via user_data happen-before the
    // callback sees them.
    cb_user_data.store(ud_as_int, .release);
    cb_bits.store(cb_as_int, .release);
}

/// Dispatch a log event to the embedder callback, if one is set.
/// Called from logFn. The format+args are rendered into a stack
/// buffer so the callback receives a fully-formatted message without
/// any Zig formatter reentering the logger.
pub fn dispatch(
    level: std.log.Level,
    comptime scope: @TypeOf(.EnumLiteral),
    comptime format: []const u8,
    args: anytype,
) void {
    // Fast path: no embedder, nothing to do. One atomic load per log
    // call is a few nanoseconds, which is negligible compared to the
    // cost of formatting the message.
    const cb_int = cb_bits.load(.acquire);
    if (cb_int == 0) return;

    const cb: *const fn (
        u32,
        [*]const u8,
        usize,
        [*]const u8,
        usize,
        ?*anyopaque,
    ) callconv(.c) void = @ptrFromInt(cb_int);

    const ud_int = cb_user_data.load(.acquire);
    const ud: ?*anyopaque = if (ud_int == 0) null else @ptrFromInt(ud_int);

    const level_int: u32 = @intFromEnum(levelToExport(level));

    // Render the message into a stack buffer using a fixed Writer.
    // std.Io.Writer.fixed keeps the bytes it successfully wrote in
    // `writer.end` even when a subsequent write fails with
    // error.WriteFailed (the zig 0.15 fixed writer's overflow error),
    // so we can salvage a partial render and append a truncation
    // suffix instead of dropping the whole message.
    var buf: [max_message_bytes]u8 = undefined;
    var writer: std.Io.Writer = .fixed(&buf);
    const overflowed: bool = blk: {
        writer.print(format, args) catch break :blk true;
        break :blk false;
    };
    const rendered: []const u8 = if (overflowed) overflow: {
        // On overflow writer.end is at or near buf.len. Reserve room
        // for the suffix by moving the end back by suffix.len (this
        // may drop a handful of bytes off the tail of the partial
        // message, which is an acceptable trade for a clear marker).
        const keep = if (buf.len >= truncated_suffix.len) buf.len - truncated_suffix.len else 0;
        @memcpy(buf[keep..][0..truncated_suffix.len], truncated_suffix);
        break :overflow buf[0..buf.len];
    } else buf[0..writer.end];

    // Scope bytes are the enum tag name. For the default scope we pass
    // an empty byte slice so the embedder can decide how to render it.
    const scope_name: []const u8 = if (scope == .default) "" else @tagName(scope);

    // Normalize: avoid passing a null base pointer when len is 0. Some
    // C# marshalers refuse to decode a (null, 0) pair.
    const scope_ptr: [*]const u8 = if (scope_name.len == 0) &empty_sentinel else scope_name.ptr;
    const msg_ptr: [*]const u8 = if (rendered.len == 0) &empty_sentinel else rendered.ptr;

    cb(level_int, scope_ptr, scope_name.len, msg_ptr, rendered.len, ud);
}

/// One-byte sentinel so we never hand a null base pointer to the
/// embedder, even for zero-length slices. Using a file-scope const
/// gives it a stable address.
const empty_sentinel: [1]u8 = .{0};

fn levelToExport(level: std.log.Level) Level {
    return switch (level) {
        .debug => .debug,
        .info => .info,
        .warn => .warn,
        .err => .err,
    };
}

// --- C API ------------------------------------------------------------
// Exported into libghostty from src/main_c.zig.

/// Register or clear the embedder log callback.
///
/// Passing null for `cb` clears any previously-installed callback. The
/// callback is invoked from whichever thread emits a std.log call, so
/// embedders must handle multi-threaded invocation.
///
/// Level integers are stable: see the Level enum in this file.
///
/// Scope and message bytes are NOT null-terminated. Use the companion
/// length argument.
pub export fn ghostty_log_set_callback(
    cb: LogCallback,
    user_data: ?*anyopaque,
) void {
    setCallback(cb, user_data);
}

// --- Tests ------------------------------------------------------------

const testing = std.testing;

const CaptureState = struct {
    level: u32 = 0,
    scope: [64]u8 = undefined,
    scope_len: usize = 0,
    message: [256]u8 = undefined,
    message_len: usize = 0,
    called: usize = 0,
};

fn captureCallback(
    level: u32,
    scope_ptr: [*]const u8,
    scope_len: usize,
    message_ptr: [*]const u8,
    message_len: usize,
    user_data: ?*anyopaque,
) callconv(.c) void {
    const state: *CaptureState = @ptrCast(@alignCast(user_data.?));
    state.level = level;
    state.scope_len = @min(scope_len, state.scope.len);
    @memcpy(state.scope[0..state.scope_len], scope_ptr[0..state.scope_len]);
    state.message_len = @min(message_len, state.message.len);
    @memcpy(state.message[0..state.message_len], message_ptr[0..state.message_len]);
    state.called += 1;
}

test "dispatch with no callback set is a no-op" {
    setCallback(null, null);
    // Must not crash even though no callback is registered.
    dispatch(.info, .some_scope, "hello {d}", .{42});
}

test "dispatch routes level, scope, and formatted message" {
    var state: CaptureState = .{};
    setCallback(captureCallback, &state);
    defer setCallback(null, null);

    dispatch(.info, .test_scope, "hello {d}", .{42});

    try testing.expectEqual(@as(usize, 1), state.called);
    try testing.expectEqual(@as(u32, @intFromEnum(Level.info)), state.level);
    try testing.expectEqualStrings("test_scope", state.scope[0..state.scope_len]);
    try testing.expectEqualStrings("hello 42", state.message[0..state.message_len]);
}

test "dispatch forwards user_data verbatim to the callback" {
    const Probe = struct {
        var seen_user_data: ?*anyopaque = null;
        var expected: u32 = 0xdeadbeef;
        fn cb(
            _: u32,
            _: [*]const u8,
            _: usize,
            _: [*]const u8,
            _: usize,
            user_data: ?*anyopaque,
        ) callconv(.c) void {
            seen_user_data = user_data;
        }
    };
    Probe.seen_user_data = null;

    setCallback(Probe.cb, @ptrCast(&Probe.expected));
    defer setCallback(null, null);

    dispatch(.info, .s, "x", .{});

    try testing.expect(Probe.seen_user_data != null);
    try testing.expectEqual(
        @as(?*anyopaque, @ptrCast(&Probe.expected)),
        Probe.seen_user_data,
    );
}

test "dispatch maps each std.log.Level to the documented integer" {
    var state: CaptureState = .{};
    setCallback(captureCallback, &state);
    defer setCallback(null, null);

    dispatch(.debug, .s, "d", .{});
    try testing.expectEqual(@as(u32, 0), state.level);

    dispatch(.info, .s, "i", .{});
    try testing.expectEqual(@as(u32, 1), state.level);

    dispatch(.warn, .s, "w", .{});
    try testing.expectEqual(@as(u32, 2), state.level);

    dispatch(.err, .s, "e", .{});
    try testing.expectEqual(@as(u32, 3), state.level);
}

test "dispatch with default scope passes an empty scope slice" {
    var state: CaptureState = .{};
    setCallback(captureCallback, &state);
    defer setCallback(null, null);

    dispatch(.info, .default, "x", .{});
    try testing.expectEqual(@as(usize, 0), state.scope_len);
}

test "dispatch truncates an oversized message with a suffix" {
    // Capture into a buffer large enough to hold the full
    // max_message_bytes + suffix so we can inspect the tail directly.
    const Big = struct {
        var called: usize = 0;
        var seen_len: usize = 0;
        var buf: [max_message_bytes]u8 = undefined;
    };
    Big.called = 0;
    Big.seen_len = 0;

    const Cb = struct {
        fn cb(
            _: u32,
            _: [*]const u8,
            _: usize,
            message_ptr: [*]const u8,
            message_len: usize,
            _: ?*anyopaque,
        ) callconv(.c) void {
            Big.called += 1;
            Big.seen_len = @min(message_len, Big.buf.len);
            @memcpy(Big.buf[0..Big.seen_len], message_ptr[0..Big.seen_len]);
        }
    };

    setCallback(Cb.cb, null);
    defer setCallback(null, null);

    // Build a format string that is guaranteed to overflow the stack
    // buffer: max_message_bytes + 16 copies of 'A'. After truncation
    // the rendered length must equal buf.len and the tail must be
    // truncated_suffix.
    const big = "A" ** (max_message_bytes + 16);
    dispatch(.info, .s, big, .{});

    try testing.expectEqual(@as(usize, 1), Big.called);
    try testing.expectEqual(max_message_bytes, Big.seen_len);
    // Tail is the truncation suffix.
    const tail_start = max_message_bytes - truncated_suffix.len;
    try testing.expectEqualStrings(truncated_suffix, Big.buf[tail_start..max_message_bytes]);
    // Everything before the suffix is still 'A'.
    for (Big.buf[0..tail_start]) |c| {
        try testing.expectEqual(@as(u8, 'A'), c);
    }
}

test "clearing the callback stops dispatch" {
    var state: CaptureState = .{};
    setCallback(captureCallback, &state);

    dispatch(.info, .s, "first", .{});
    try testing.expectEqual(@as(usize, 1), state.called);

    setCallback(null, null);
    dispatch(.info, .s, "second", .{});
    // Still 1 - the clear prevented the second call.
    try testing.expectEqual(@as(usize, 1), state.called);
}
