//! Kitty clipboard protocol (OSC 5522) write transactions: the
//! stateful accumulation of wdata chunks and walias aliases until the
//! commit packet arrives. See clipboard.zig for the protocol overview.

const std = @import("std");
const assert = @import("../../quirks.zig").inlineAssert;
const Allocator = std.mem.Allocator;
const clipboard = @import("../clipboard.zig");
const clipboard_command = @import("clipboard_command.zig");

const Metadata = clipboard_command.Metadata;
const Payload = clipboard_command.Payload;
const max_mime_len = clipboard_command.max_mime_len;

const log = std.log.scoped(.kitty_clipboard);

/// Maximum total decoded bytes accumulated by one write transaction.
/// We hardcode this for now but probably will make this configurable
/// later.
pub const max_write_size = 32 * 1024 * 1024;

/// Maximum MIME types and aliases per write transaction.
pub const max_write_mimes = 64;
pub const max_write_aliases = 64;

/// One MIME representation of committed clipboard data. This is the
/// same type the clipboard write effect consumes so committed contents
/// can be passed through directly.
pub const Content = clipboard.Content;

/// The state of one in-flight write transaction: a single `type=write`
/// plus all the `wdata` chunks and `walias` aliases until completion
/// or error.
pub const WriteState = struct {
    arena: std.heap.ArenaAllocator,
    loc: clipboard.Location,
    id: []const u8,
    pw: []const u8,
    name: []const u8,
    spool: std.ArrayListUnmanaged(u8) = .empty,
    entries: std.ArrayListUnmanaged(Entry) = .empty,
    aliases: std.ArrayListUnmanaged(Alias) = .empty,

    /// Index into entries currently receiving data.
    current: ?usize = null,

    /// Set when max_write_size was exceeded; excess data is dropped
    /// but the write still completes.
    truncated: bool = false,

    const Entry = struct {
        /// Owned by the transaction arena.
        mime: []const u8,
        start: usize = 0,
        len: usize = 0,
    };

    const Alias = struct {
        /// Owned by the transaction arena.
        alias: []const u8,
        target: []const u8,
    };

    /// Begin a transaction from a type=write packet.
    pub fn init(alloc: Allocator, meta: *const Metadata) Allocator.Error!WriteState {
        assert(meta.op == .write);
        var arena: std.heap.ArenaAllocator = .init(alloc);
        errdefer arena.deinit();
        const id = try arena.allocator().dupe(u8, meta.id);
        const pw = try arena.allocator().dupe(u8, meta.pw);
        const name = try arena.allocator().dupe(u8, meta.name);
        return .{
            .arena = arena,
            .loc = meta.loc,
            .id = id,
            .pw = pw,
            .name = name,
        };
    }

    pub fn deinit(self: *WriteState, alloc: Allocator) void {
        self.spool.deinit(alloc);
        self.entries.deinit(alloc);
        self.aliases.deinit(alloc);
        self.arena.deinit();
    }

    /// Accumulate one wdata chunk carrying data for meta.mime (which
    /// must be non-empty; an empty mime is a commit, not data).
    pub fn data(
        self: *WriteState,
        alloc: Allocator,
        meta: *const Metadata,
        payload: []const u8,
    ) error{OutOfMemory}!void {
        assert(meta.op == .wdata);
        assert(meta.mime.len > 0);
        // Switch the receiving entry if this chunk is for a different
        // MIME type than the last one.
        entry: {
            if (self.current) |idx| {
                const entry = &self.entries.items[idx];
                if (std.mem.eql(u8, entry.mime, meta.mime)) {
                    break :entry;
                }

                // Finalize the previous region.
                entry.len = self.spool.items.len - entry.start;
            }

            // Re-using an earlier MIME type starts a fresh region,
            // overwriting the previous mapping.
            for (self.entries.items, 0..) |*entry, idx| {
                if (std.mem.eql(u8, entry.mime, meta.mime)) {
                    entry.start = self.spool.items.len;
                    entry.len = 0;
                    self.current = idx;
                    break :entry;
                }
            }

            if (self.entries.items.len >= max_write_mimes) {
                log.warn(
                    "clipboard write has too many MIME types, ignoring mime={s}",
                    .{meta.mime},
                );
                self.current = null;
                return;
            }

            try self.entries.append(alloc, .{
                .mime = try self.arena.allocator().dupe(u8, meta.mime),
                .start = self.spool.items.len,
            });
            self.current = self.entries.items.len - 1;
        }

        // Each packet's payload is independently base64-encoded: per
        // the spec, "payload is base64 encoded data" and clients chunk
        // the data before encoding. An invalid chunk is dropped and
        // the transaction continues.
        const decoded = Payload.init(
            alloc,
            payload,
        ) catch |err| switch (err) {
            error.OutOfMemory => return error.OutOfMemory,
            error.Invalid => {
                log.warn("clipboard write chunk has invalid base64, ignoring chunk", .{});
                return;
            },
        };
        defer decoded.deinit(alloc);

        const remaining = max_write_size -| self.spool.items.len;
        const n = @min(decoded.data.len, remaining);
        try self.spool.appendSlice(alloc, decoded.data[0..n]);
        if (n < decoded.data.len) {
            self.truncated = true;
            logTruncatedOnce();
        }
    }

    /// Register aliases from a walias packet: meta.mime is the target
    /// (the type that carries data) and the payload is a base64-encoded,
    /// whitespace-separated list of aliases. Returns error.Invalid for
    /// an undecodable payload, which aborts the transaction with EINVAL.
    pub fn alias(
        self: *WriteState,
        alloc: Allocator,
        meta: *const Metadata,
        payload: []const u8,
    ) error{ OutOfMemory, Invalid }!void {
        assert(meta.op == .walias);
        assert(meta.mime.len > 0);
        const decoded = try Payload.init(alloc, payload);
        defer decoded.deinit(alloc);
        var it = decoded.mimeIterator();

        // Copy the target only if at least one valid alias exists.
        const target: []const u8 = target: {
            while (it.next()) |name| {
                if (name.len > max_mime_len) continue;
                break :target try self.arena.allocator().dupe(u8, meta.mime);
            }

            // If we didn't find a target then ignore it.
            return;
        };

        // Rewind so the alias that satisfied the check above is
        // associated too.
        it.reset();

        // Associate the aliases
        while (it.next()) |name| {
            if (name.len > max_mime_len) continue;

            // A repeated alias overwrites its previous target.
            for (self.aliases.items) |*a| {
                if (std.mem.eql(u8, a.alias, name)) {
                    a.target = target;
                    break;
                }
            } else {
                if (self.aliases.items.len >= max_write_aliases) {
                    log.warn("clipboard write has too many aliases, ignoring", .{});
                    return;
                }

                try self.aliases.append(alloc, .{
                    .alias = try self.arena.allocator().dupe(u8, name),
                    .target = target,
                });
            }
        }
    }

    /// The result of a committed transaction. All slices borrow the
    /// WriteState's memory and are valid until it is deinited.
    pub const Committed = struct {
        loc: clipboard.Location,
        id: []const u8,
        pw: []const u8,
        name: []const u8,
        truncated: bool,
        contents: []const Content,

        pub fn deinit(self: *const Committed, alloc: Allocator) void {
            alloc.free(self.contents);
        }
    };

    /// Commit the transaction (a wdata packet without a MIME type).
    /// The caller must use the result, call Committed.deinit, and then
    /// deinit this state.
    pub fn commit(
        self: *WriteState,
        alloc: Allocator,
    ) error{OutOfMemory}!Committed {
        // Finalize the region receiving data.
        if (self.current) |idx| {
            const entry = &self.entries.items[idx];
            entry.len = self.spool.items.len - entry.start;
            self.current = null;
        }

        // Resolve the final MIME map: entries in arrival order, then
        // aliases applied sequentially against the evolving map so
        // chained aliases work like kitty's dict iteration. An alias
        // whose target has no mapping is dropped; an alias colliding
        // with an existing name overwrites it.
        var contents: std.ArrayListUnmanaged(Content) = .empty;
        defer contents.deinit(alloc);
        try contents.ensureTotalCapacity(
            alloc,
            self.entries.items.len + self.aliases.items.len,
        );

        for (self.entries.items) |*entry| {
            contents.appendAssumeCapacity(.{
                .mime = entry.mime,
                .data = self.spool.items[entry.start..][0..entry.len],
            });
        }

        for (self.aliases.items) |*a| {
            const target: Content = target: {
                for (contents.items) |c| {
                    if (std.mem.eql(u8, c.mime, a.target)) {
                        break :target c;
                    }
                }
                continue;
            };

            for (contents.items) |*c| {
                if (std.mem.eql(u8, c.mime, a.alias)) {
                    c.data = target.data;
                    break;
                }
            } else {
                contents.appendAssumeCapacity(.{
                    .mime = a.alias,
                    .data = target.data,
                });
            }
        }

        return .{
            .loc = self.loc,
            .id = self.id,
            .pw = self.pw,
            .name = self.name,
            .truncated = self.truncated,
            .contents = try contents.toOwnedSlice(alloc),
        };
    }

    fn logTruncatedOnce() void {
        // Log spam protection: this can be hit for every chunk of an
        // oversized write.
        const S = struct {
            var logged: bool = false;
        };
        if (!S.logged) {
            S.logged = true;
            log.warn(
                "clipboard write exceeds {} bytes, truncating",
                .{max_write_size},
            );
        }
    }
};

test "write: basic transaction" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const begin_meta: Metadata = .{ .op = .write, .id = "42" };
    var state: WriteState = try .init(alloc, &begin_meta);
    defer state.deinit(alloc);

    try state.data(alloc, &.{ .op = .wdata, .mime = "text/plain" }, "R2hvc3R0eQ=="); // "Ghostty"

    const committed = try state.commit(alloc);
    defer committed.deinit(alloc);
    try testing.expectEqualStrings("42", committed.id);
    try testing.expect(committed.loc == .standard);
    try testing.expectEqual(@as(usize, 1), committed.contents.len);
    try testing.expectEqualStrings("text/plain", committed.contents[0].mime);
    try testing.expectEqualStrings("Ghostty", committed.contents[0].data);
}

test "write: chunked data accumulates" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const begin_meta: Metadata = .{ .op = .write };
    var state: WriteState = try .init(alloc, &begin_meta);
    defer state.deinit(alloc);

    try state.data(alloc, &.{ .op = .wdata, .mime = "text/plain" }, "SGVsbG8="); // "Hello"
    try state.data(alloc, &.{ .op = .wdata, .mime = "text/plain" }, "V29ybGQ="); // "World"

    const committed = try state.commit(alloc);
    defer committed.deinit(alloc);
    try testing.expectEqualStrings("HelloWorld", committed.contents[0].data);
}

test "write: multiple mimes in order" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const begin_meta: Metadata = .{ .op = .write };
    var state: WriteState = try .init(alloc, &begin_meta);
    defer state.deinit(alloc);

    try state.data(alloc, &.{ .op = .wdata, .mime = "text/plain" }, "YQ=="); // "a"
    try state.data(alloc, &.{ .op = .wdata, .mime = "text/html" }, "Yg=="); // "b"

    const committed = try state.commit(alloc);
    defer committed.deinit(alloc);
    try testing.expectEqual(@as(usize, 2), committed.contents.len);
    try testing.expectEqualStrings("text/plain", committed.contents[0].mime);
    try testing.expectEqualStrings("a", committed.contents[0].data);
    try testing.expectEqualStrings("text/html", committed.contents[1].mime);
    try testing.expectEqualStrings("b", committed.contents[1].data);
}

test "write: reused mime overwrites" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const begin_meta: Metadata = .{ .op = .write };
    var state: WriteState = try .init(alloc, &begin_meta);
    defer state.deinit(alloc);

    try state.data(alloc, &.{ .op = .wdata, .mime = "text/plain" }, "YQ=="); // "a"
    try state.data(alloc, &.{ .op = .wdata, .mime = "text/html" }, "Yg=="); // "b"
    try state.data(alloc, &.{ .op = .wdata, .mime = "text/plain" }, "Yw=="); // "c"

    const committed = try state.commit(alloc);
    defer committed.deinit(alloc);
    try testing.expectEqual(@as(usize, 2), committed.contents.len);
    // Position preserved, data replaced.
    try testing.expectEqualStrings("text/plain", committed.contents[0].mime);
    try testing.expectEqualStrings("c", committed.contents[0].data);
}

test "write: invalid base64 chunk is dropped, transaction continues" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const begin_meta: Metadata = .{ .op = .write };
    var state: WriteState = try .init(alloc, &begin_meta);
    defer state.deinit(alloc);

    try state.data(alloc, &.{ .op = .wdata, .mime = "text/plain" }, "SGVsbG8="); // "Hello"
    try state.data(alloc, &.{ .op = .wdata, .mime = "text/plain" }, "!!!bad!!!");
    try state.data(alloc, &.{ .op = .wdata, .mime = "text/plain" }, "V29ybGQ="); // "World"

    const committed = try state.commit(alloc);
    defer committed.deinit(alloc);
    try testing.expectEqualStrings("HelloWorld", committed.contents[0].data);
}

test "write: aliases resolve at commit" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const begin_meta: Metadata = .{ .op = .write };
    var state: WriteState = try .init(alloc, &begin_meta);
    defer state.deinit(alloc);

    try state.data(alloc, &.{ .op = .wdata, .mime = "text/plain" }, "R2hvc3R0eQ=="); // "Ghostty"

    // Alias "TEXT UTF8_STRING" -> text/plain.
    const alias_meta: Metadata = .{ .op = .walias, .mime = "text/plain" };
    try state.alias(alloc, &alias_meta, "VEVYVCBVVEY4X1NUUklORw==");

    const committed = try state.commit(alloc);
    defer committed.deinit(alloc);
    try testing.expectEqual(@as(usize, 3), committed.contents.len);
    try testing.expectEqualStrings("TEXT", committed.contents[1].mime);
    try testing.expectEqualStrings("Ghostty", committed.contents[1].data);
    try testing.expectEqualStrings("UTF8_STRING", committed.contents[2].mime);
    try testing.expectEqualStrings("Ghostty", committed.contents[2].data);
}

test "write: alias without data target is dropped" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const begin_meta: Metadata = .{ .op = .write };
    var state: WriteState = try .init(alloc, &begin_meta);
    defer state.deinit(alloc);

    const alias_meta: Metadata = .{ .op = .walias, .mime = "text/plain" };
    try state.alias(alloc, &alias_meta, "VEVYVA=="); // "TEXT"

    const committed = try state.commit(alloc);
    defer committed.deinit(alloc);
    try testing.expectEqual(@as(usize, 0), committed.contents.len);
}
