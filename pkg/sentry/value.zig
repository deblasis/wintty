const std = @import("std");
const assert = std.debug.assert;
const c = @import("c.zig").c;
const Level = @import("level.zig").Level;

/// sentry_value_t
pub const Value = struct {
    /// The underlying value. This is a union that could be represented with
    /// an extern union but I don't want to risk C ABI issues so we wrap it
    /// in a struct.
    value: c.sentry_value_t,

    pub fn initMessageEvent(
        level: Level,
        logger: ?[]const u8,
        message: []const u8,
    ) Value {
        return .{ .value = c.sentry_value_new_message_event_n(
            @intFromEnum(level),
            if (logger) |v| v.ptr else null,
            if (logger) |v| v.len else 0,
            message.ptr,
            message.len,
        ) };
    }

    /// Attach the current call stack to an event.
    ///
    /// Without this a panic event carries the message but no frames, so a
    /// report says what happened and not where. Passing null captures the
    /// stack at the point of call, which is inside the panic handler, so the
    /// crash site sits a few frames up.
    pub fn addStacktrace(self: Value) void {
        c.sentry_event_value_add_stacktrace(self.value, null, 0);
    }

    pub fn initObject() Value {
        return .{ .value = c.sentry_value_new_object() };
    }

    pub fn initString(value: []const u8) Value {
        return .{ .value = c.sentry_value_new_string_n(value.ptr, value.len) };
    }

    pub fn initBool(value: bool) Value {
        return .{ .value = c.sentry_value_new_bool(@intFromBool(value)) };
    }

    pub fn initInt32(value: i32) Value {
        return .{ .value = c.sentry_value_new_int32(value) };
    }

    /// Number of entries in a list or object.
    pub fn len(self: Value) usize {
        return c.sentry_value_get_length(self.value);
    }

    /// Borrowed element of a list. Not owned: do not decref the result.
    pub fn getIndex(self: Value, index: usize) Value {
        return .{ .value = c.sentry_value_get_by_index(self.value, index) };
    }

    /// The string contents of a value, or null if it is not a string.
    /// Borrowed from the value and only valid while it lives.
    pub fn asString(self: Value) ?[]const u8 {
        const ptr = c.sentry_value_as_string(self.value) orelse return null;
        return std.mem.span(ptr);
    }

    pub fn decref(self: Value) void {
        c.sentry_value_decref(self.value);
    }

    pub fn incref(self: Value) Value {
        c.sentry_value_incref(self.value);
    }

    pub fn isNull(self: Value) bool {
        return c.sentry_value_is_null(self.value) != 0;
    }

    /// sentry_value_set_by_key_n
    pub fn set(self: Value, key: []const u8, value: Value) void {
        _ = c.sentry_value_set_by_key_n(
            self.value,
            key.ptr,
            key.len,
            value.value,
        );
    }

    /// sentry_value_set_by_key_n
    pub fn get(self: Value, key: []const u8) ?Value {
        const val: Value = .{ .value = c.sentry_value_get_by_key_n(
            self.value,
            key.ptr,
            key.len,
        ) };
        if (val.isNull()) return null;
        return val;
    }
};
