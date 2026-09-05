//! Accounting for data handed to the pty whose writes have not finished.

const std = @import("std");

/// Bounds the amount of data queued to a single pty write stream.
///
/// A child that keeps asking the terminal questions (an `ESC [ 6 n` loop,
/// say) without ever reading its own stdin makes us queue a reply per
/// query. Each queued reply holds a pooled write request until the write
/// completes, so with no bound the pool grows for as long as the child
/// misbehaves.
///
/// Terminal replies are advisory: a child that is not reading its input
/// gains nothing from them, so refusing them is better than growing
/// without bound. User input is not advisory and is never refused, so a
/// paste larger than the cap still goes through in full; it just makes us
/// refuse replies until it drains.
///
/// The counter is written from the termio thread (queueing writes and
/// their completions) and read from the read thread (deciding whether to
/// refuse a reply), so it is atomic.
pub const WriteLimit = struct {
    /// Bytes queued to the write stream whose writes have not completed.
    outstanding: std.atomic.Value(usize) = .init(0),

    /// Advisory writes refused since the last warning.
    dropped: std.atomic.Value(usize) = .init(0),

    /// Timestamp in milliseconds of the last warning, zero for never.
    last_warn_ms: std.atomic.Value(i64) = .init(0),

    /// The backlog at which advisory writes start being refused.
    max: usize = default_max,

    /// Enough headroom that no interactive workload reaches it: a pty
    /// that is being read at all drains far faster than this.
    pub const default_max: usize = 1024 * 1024;

    /// The minimum gap between warnings. A child in a query loop would
    /// otherwise turn our own log into the flood.
    pub const warn_interval_ms: i64 = 5 * std.time.ms_per_s;

    /// Account for bytes handed to the write stream.
    pub fn queued(self: *WriteLimit, n: usize) void {
        _ = self.outstanding.fetchAdd(n, .monotonic);
    }

    /// Account for bytes whose write has completed.
    pub fn completed(self: *WriteLimit, n: usize) void {
        _ = self.outstanding.fetchSub(n, .monotonic);
    }

    /// Whether advisory writes should be refused right now.
    pub fn atCapacity(self: *const WriteLimit) bool {
        return self.outstanding.load(.monotonic) >= self.max;
    }

    /// Record an advisory write we refused. Returns how many have been
    /// refused since the last warning if it is time to warn again, and
    /// null if the last warning was too recent.
    pub fn recordDrop(self: *WriteLimit, now_ms: i64) ?usize {
        const count = self.dropped.fetchAdd(1, .monotonic) + 1;

        const last = self.last_warn_ms.load(.monotonic);
        if (last != 0 and now_ms -| last < warn_interval_ms) return null;

        self.last_warn_ms.store(now_ms, .monotonic);
        _ = self.dropped.fetchSub(count, .monotonic);
        return count;
    }
};

test "termio WriteLimit refuses advisory writes past the cap" {
    const testing = std.testing;

    var limit: WriteLimit = .{ .max = 100 };
    try testing.expect(!limit.atCapacity());

    limit.queued(99);
    try testing.expect(!limit.atCapacity());

    limit.queued(1);
    try testing.expect(limit.atCapacity());

    limit.completed(1);
    try testing.expect(!limit.atCapacity());
}

test "termio WriteLimit rate limits its warning" {
    const testing = std.testing;

    var limit: WriteLimit = .{ .max = 1 };

    // The first refusal warns immediately.
    try testing.expectEqual(@as(?usize, 1), limit.recordDrop(1_000));

    // Refusals inside the interval are counted but not warned about.
    try testing.expectEqual(@as(?usize, null), limit.recordDrop(1_001));
    try testing.expectEqual(@as(?usize, null), limit.recordDrop(2_000));

    // The next warning reports everything since the last one.
    try testing.expectEqual(
        @as(?usize, 3),
        limit.recordDrop(1_000 + WriteLimit.warn_interval_ms),
    );
}
