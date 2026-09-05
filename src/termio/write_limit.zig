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
/// Every field is touched from more than one thread. The outstanding
/// count is written by the termio thread (queueing writes and their
/// completions) and read by the read thread (deciding whether to refuse a
/// reply); the drop counter and the warning timestamp are written by both,
/// because a config change hands `changeConfig` a colour report to write
/// from the termio thread while every other reply comes from the read
/// thread. So all of them are atomic, and the warning claims its interval
/// with a compare-exchange rather than a load and a store.
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
        _ = self.dropped.fetchAdd(1, .monotonic);

        // Claim the interval before reporting anything: only the caller
        // that moves the timestamp gets to warn, so two threads dropping
        // at once produce one warning rather than two, and only one of
        // them takes the counter.
        var last = self.last_warn_ms.load(.monotonic);
        while (true) {
            if (last != 0 and now_ms -| last < warn_interval_ms) return null;
            last = self.last_warn_ms.cmpxchgWeak(
                last,
                now_ms,
                .monotonic,
                .monotonic,
            ) orelse break;
        }

        // Taking the whole counter rather than subtracting what we
        // counted keeps the drops another thread added meanwhile, and
        // can't underflow.
        return self.dropped.swap(0, .monotonic);
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

test "termio WriteLimit accounts for every drop with two threads warning" {
    const testing = std.testing;

    const thread_count = 4;
    const drops_per_thread = 500;

    // A config change writes a colour report from the termio thread while
    // the read thread is refusing replies, so two threads can be in
    // recordDrop at once. Whatever the interleaving, every drop has to be
    // reported exactly once or still be waiting in the counter: reporting
    // one twice, or losing one, means the subtraction underflowed.
    const Dropper = struct {
        limit: *WriteLimit,
        reported: usize = 0,

        fn run(self: *@This()) void {
            for (0..drops_per_thread) |i| {
                const now: i64 = 1 + @as(i64, @intCast(i)) * WriteLimit.warn_interval_ms;
                if (self.limit.recordDrop(now)) |n| self.reported += n;
            }
        }
    };

    var limit: WriteLimit = .{};
    var droppers: [thread_count]Dropper = undefined;
    var threads: [thread_count]std.Thread = undefined;
    for (&droppers, &threads) |*d, *t| {
        d.* = .{ .limit = &limit };
        t.* = try std.Thread.spawn(.{}, Dropper.run, .{d});
    }
    for (&threads) |t| t.join();

    var total: usize = limit.dropped.load(.monotonic);
    for (&droppers) |*d| total += d.reported;
    try testing.expectEqual(
        @as(usize, thread_count * drops_per_thread),
        total,
    );
}
