//! Blocking queue implementation aimed primarily for message passing
//! between threads.

const std = @import("std");
const Allocator = std.mem.Allocator;
const compat_thread = @import("../lib/compat/thread.zig");

/// Returns a blocking queue implementation for type T.
///
/// This is tailor made for ghostty usage so it isn't meant to be maximally
/// generic, but I'm happy to make it more generic over time. Traits of this
/// queue that are specific to our usage:
///
///   - Fixed size. We expect our queue to quickly drain and also not be
///     too large so we prefer a fixed size queue for now.
///   - No blocking pop. We use an external event loop mechanism such as
///     eventfd to notify our waiter that there is no data available so
///     we don't need to implement a blocking pop.
///   - Drain function. Most queues usually pop one at a time. We have
///     a mechanism for draining since on every IO loop our TTY drains
///     the full queue so we can get rid of the overhead of a ton of
///     locks and bounds checking and do a one-time drain.
///
/// One key usage pattern is that our blocking queues are single producer
/// single consumer (SPSC). This should let us do some interesting optimizations
/// in the future. At the time of writing this, the blocking queue implementation
/// is purposely naive to build something quickly, but we should benchmark
/// and make this more optimized as necessary.
pub fn BlockingQueue(
    comptime T: type,
    comptime capacity: usize,
) type {
    return struct {
        const Self = @This();

        // The type we use for queue size types. We can optimize this
        // in the future to be the correct bit-size for our preallocated
        // size for this queue.
        pub const Size = u32;

        // The bounds of this queue. We recast this to Size so we can do math.
        const bounds: Size = @intCast(capacity);

        /// Specifies the timeout for an operation.
        pub const Timeout = union(enum) {
            /// Fail instantly (non-blocking).
            instant: void,

            /// Run forever or until interrupted
            forever: void,

            /// Nanoseconds
            ns: u64,
        };

        /// Our data. The values are undefined until they are written.
        data: [bounds]T = undefined,

        /// The next location to write (next empty loc) and next location
        /// to read (next non-empty loc). The number of written elements.
        write: Size = 0,
        read: Size = 0,
        len: Size = 0,

        /// The big mutex that must be held to read/write.
        mutex: std.Io.Mutex = .init,

        /// A CV for being notified when the queue is no longer full. This is
        /// used for writing. Note we DON'T have a CV for waiting on the
        /// queue not being EMPTY because we use external notifiers for that.
        cond_not_full: std.Io.Condition = .init,
        not_full_waiters: usize = 0,

        /// Allocate the blocking queue on the heap.
        pub fn create(alloc: Allocator) Allocator.Error!*Self {
            const ptr = try alloc.create(Self);
            errdefer alloc.destroy(ptr);

            ptr.* = .{
                .data = undefined,
                .len = 0,
                .write = 0,
                .read = 0,
                .mutex = .init,
                .cond_not_full = .init,
                .not_full_waiters = 0,
            };

            return ptr;
        }

        /// Free all the resources for this queue. This should only be
        /// called once all producers and consumers have quit.
        pub fn destroy(self: *Self, alloc: Allocator) void {
            self.* = undefined;
            alloc.destroy(self);
        }

        /// How long one `pushWake` attempt waits for a slot before the
        /// consumer's wake is re-issued, and how many of those windows a
        /// producer spends before it treats the consumer as gone. Roughly
        /// a minute. This is the budget for a producer that is never the
        /// UI thread, where the cost of the wait is a stalled background
        /// job.
        pub const wake_retry_timeout_ns: u64 = 250 * std.time.ns_per_ms;
        pub const wake_retry_attempts: usize = 240;

        /// The same pair for a queue whose producer can be the UI thread,
        /// where the cost of the wait is a frozen window. Windows paints
        /// a window "Not Responding" after about five seconds without a
        /// message pump and offers to kill it, so the total has to stay
        /// well under that: a minute-long "bound" is not one a user can
        /// tell apart from a hang. The shorter window also re-issues the
        /// consumer's wake five times as often, and that re-issue is what
        /// recovers a lost notify.
        pub const wake_retry_timeout_ns_ui: u64 = 50 * std.time.ns_per_ms;
        pub const wake_retry_attempts_ui: usize = 40;

        /// Push a value to the queue. This returns the total size of the
        /// queue (unread items) after the push, so a queued value always
        /// returns at least one.
        ///
        /// A return of zero means the value was NOT queued and the caller
        /// still owns it: a value that owns memory must be released on
        /// that path. Only `.instant` and `.ns` can return zero.
        /// `.forever` waits until a slot is genuinely free, so it never
        /// returns zero -- and never returns at all while the consumer is
        /// not draining. Producers of a queue that is drained only when
        /// its consumer is woken should use `pushWake` instead, because a
        /// `.forever` wait there withholds the wake it waits on.
        pub fn push(self: *Self, io: std.Io, value: T, timeout: Timeout) Size {
            self.mutex.lockUncancelable(io);
            defer self.mutex.unlock(io);

            // The
            if (self.full()) {
                switch (timeout) {
                    // If we're not waiting, then we failed to write.
                    .instant => return 0,

                    .forever => {
                        self.not_full_waiters += 1;
                        defer self.not_full_waiters -= 1;

                        // Being woken doesn't mean there is a slot for
                        // us: we have multiple producers, so another one
                        // can take the freed slot before we reacquire
                        // the mutex. Wait again instead of dropping the
                        // value, which callers have no way to notice.
                        while (self.full()) {
                            self.cond_not_full.waitUncancelable(io, &self.mutex);
                        }
                    },

                    .ns => |ns| {
                        self.not_full_waiters += 1;
                        defer self.not_full_waiters -= 1;

                        // Same as above, except we resolve the timeout to
                        // an absolute deadline first so that waiting
                        // again can't extend the caller's timeout.
                        const relative: std.Io.Timeout = .{ .duration = .{
                            .raw = .fromNanoseconds(ns),
                            .clock = .awake,
                        } };
                        const deadline = relative.toDeadline(io);

                        while (self.full()) {
                            compat_thread.waitTimeout(
                                &self.cond_not_full,
                                io,
                                &self.mutex,
                                deadline,
                            ) catch return 0;
                        }
                    },
                }
            }

            // Add our data and update our accounting
            self.data[self.write] = value;
            self.write += 1;
            if (self.write >= bounds) self.write -= bounds;
            self.len += 1;

            return self.len;
        }

        /// Push a value, re-issuing the consumer's wake between bounded
        /// attempts instead of parking on the not-full condition.
        ///
        /// Our mailboxes are drained by a thread that only runs when it
        /// is woken, so a producer that waits inside the queue is waiting
        /// for a drain its own wake has to start, and on the IOCP backend
        /// that wake can be lost outright (#1036). `wakeFn` is therefore
        /// called after every window that did not land the value, which
        /// is what makes the wait recoverable.
        ///
        /// A zero return means the value was not queued and THE CALLER
        /// STILL OWNS IT: anything holding memory must be released on
        /// that path. There is deliberately no budget that never gives
        /// up. Every mailbox in this tree can release a refused message,
        /// and a producer that waits without a limit takes its own thread
        /// out of service for as long as the consumer is wedged -- which
        /// is how two mailboxes deadlock each other rather than one of
        /// them losing a message.
        ///
        /// Mutation-testing note: `wakeFn` is a comptime function
        /// parameter, so `_ = wakeFn;` does not compile ("pointless
        /// discard of function parameter"). Neutralise the call instead,
        /// e.g. `if (attempts > 1_000_000) wakeFn(ctx);`.
        pub fn pushWake(
            self: *Self,
            io: std.Io,
            value: T,
            ctx: anytype,
            comptime wakeFn: fn (@TypeOf(ctx)) void,
            timeout_ns: u64,
            max_attempts: usize,
        ) Size {
            var attempts: usize = 0;
            while (true) {
                const result = self.push(io, value, .{ .ns = timeout_ns });
                if (result > 0) return result;

                wakeFn(ctx);

                attempts += 1;
                if (attempts >= max_attempts) return 0;
            }
        }

        /// Pop a value from the queue without blocking.
        pub fn pop(self: *Self, io: std.Io) ?T {
            self.mutex.lockUncancelable(io);
            defer self.mutex.unlock(io);

            // If we're empty we have nothing
            if (self.len == 0) return null;

            // Get the index we're going to read data from and do some
            // accounting. We don't copy the value here to avoid copying twice.
            const n = self.read;
            self.read += 1;
            if (self.read >= bounds) self.read -= bounds;
            self.len -= 1;

            // If we have consumers waiting on a full queue, notify.
            if (self.not_full_waiters > 0) self.cond_not_full.signal(io);

            return self.data[n];
        }

        /// Pop all values from the queue. This will hold the big mutex
        /// until `deinit` is called on the return value. This is used if
        /// you know you're going to "pop" and utilize all the values
        /// quickly to avoid many locks, bounds checks, and cv signals.
        pub fn drain(self: *Self, io: std.Io) DrainIterator {
            self.mutex.lockUncancelable(io);
            return .{ .queue = self };
        }

        pub const DrainIterator = struct {
            queue: *Self,

            pub fn next(self: *DrainIterator) ?T {
                if (self.queue.len == 0) return null;

                // Read and account
                const n = self.queue.read;
                self.queue.read += 1;
                if (self.queue.read >= bounds) self.queue.read -= bounds;
                self.queue.len -= 1;

                return self.queue.data[n];
            }

            pub fn deinit(self: *DrainIterator, io: std.Io) void {
                // If we have consumers waiting on a full queue, notify.
                if (self.queue.not_full_waiters > 0) self.queue.cond_not_full.signal(io);

                // Unlock
                self.queue.mutex.unlock(io);
            }
        };

        /// Returns true if the queue is full. This is not public because
        /// it requires the lock to be held.
        inline fn full(self: *Self) bool {
            return self.len == bounds;
        }
    };
}

test "basic push and pop" {
    const testing = std.testing;
    const alloc = testing.allocator;
    const io = testing.io;

    const Q = BlockingQueue(u64, 4);
    const q = try Q.create(alloc);
    defer q.destroy(alloc);

    // Should have no values
    try testing.expect(q.pop(io) == null);

    // Push until we're full
    try testing.expectEqual(@as(Q.Size, 1), q.push(io, 1, .{ .instant = {} }));
    try testing.expectEqual(@as(Q.Size, 2), q.push(io, 2, .{ .instant = {} }));
    try testing.expectEqual(@as(Q.Size, 3), q.push(io, 3, .{ .instant = {} }));
    try testing.expectEqual(@as(Q.Size, 4), q.push(io, 4, .{ .instant = {} }));
    try testing.expectEqual(@as(Q.Size, 0), q.push(io, 5, .{ .instant = {} }));

    // Pop!
    try testing.expect(q.pop(io).? == 1);
    try testing.expect(q.pop(io).? == 2);
    try testing.expect(q.pop(io).? == 3);
    try testing.expect(q.pop(io).? == 4);
    try testing.expect(q.pop(io) == null);

    // Drain does nothing
    var it = q.drain(io);
    try testing.expect(it.next() == null);
    it.deinit(io);

    // Verify we can still push
    try testing.expectEqual(@as(Q.Size, 1), q.push(io, 1, .{ .instant = {} }));
}

test "timed push" {
    const testing = std.testing;
    const alloc = testing.allocator;
    const io = testing.io;

    const Q = BlockingQueue(u64, 1);
    const q = try Q.create(alloc);
    defer q.destroy(alloc);

    // Push
    try testing.expectEqual(@as(Q.Size, 1), q.push(io, 1, .{ .instant = {} }));
    try testing.expectEqual(@as(Q.Size, 0), q.push(io, 2, .{ .instant = {} }));

    // Timed push should fail
    try testing.expectEqual(@as(Q.Size, 0), q.push(io, 2, .{ .ns = 1000 }));
}

test "BlockingQueue push forever retries when woken with no free slot" {
    const testing = std.testing;
    const alloc = testing.allocator;
    const io = testing.io;

    const Q = BlockingQueue(u64, 1);
    const q = try Q.create(alloc);
    defer q.destroy(alloc);

    // Fill the queue so the pusher below has to wait for a slot.
    try testing.expectEqual(@as(Q.Size, 1), q.push(io, 1, .{ .instant = {} }));

    const Pusher = struct {
        q: *Q,
        result: Q.Size = 0,

        fn run(self: *@This(), thread_io: std.Io) void {
            self.result = self.q.push(thread_io, 2, .{ .forever = {} });
        }
    };

    var pusher: Pusher = .{ .q = q };
    const thread = try std.Thread.spawn(.{}, Pusher.run, .{ &pusher, io });

    // Wait until the pusher is parked on the condition.
    while (true) {
        q.mutex.lockUncancelable(io);
        const parked = q.not_full_waiters > 0;
        q.mutex.unlock(io);
        if (parked) break;
        std.Thread.yield() catch {};
    }

    // Wake the pusher while the queue is still full. This is what a
    // second producer taking the slot first looks like from the woken
    // pusher's side: there is nothing for it, so it has to keep waiting
    // instead of dropping the value.
    for (0..1000) |_| {
        q.cond_not_full.broadcast(io);
        std.Thread.yield() catch {};
    }

    q.mutex.lockUncancelable(io);
    const still_parked = q.not_full_waiters > 0;
    q.mutex.unlock(io);

    // Free a slot so the pusher can finish either way, then join before
    // asserting so a failure doesn't leave the thread behind.
    try testing.expect(q.pop(io).? == 1);
    thread.join();

    try testing.expect(still_parked);
    try testing.expectEqual(@as(Q.Size, 1), pusher.result);
    try testing.expect(q.pop(io).? == 2);
}

test "BlockingQueue pushWake wakes the consumer on every window it loses" {
    const testing = std.testing;
    const alloc = testing.allocator;
    const io = testing.io;

    const Q = BlockingQueue(u64, 1);
    const q = try Q.create(alloc);
    defer q.destroy(alloc);

    // Fill the queue and never drain it. That is what a consumer asleep
    // on a lost wake looks like from a producer's side.
    try testing.expectEqual(@as(Q.Size, 1), q.push(io, 1, .{ .instant = {} }));

    var woken: usize = 0;
    const result = q.pushWake(
        io,
        2,
        &woken,
        countWake,
        1 * std.time.ns_per_ms,
        3,
    );

    // The invariant: a producer that cannot land its value must have
    // woken the consumer anyway, once per window, because that wake is
    // the only thing that frees the slot it is waiting for.
    try testing.expectEqual(@as(usize, 3), woken);

    // It gave up rather than parking, and took nothing with it.
    try testing.expectEqual(@as(Q.Size, 0), result);
    try testing.expect(q.pop(io).? == 1);
}

test "BlockingQueue pushWake lands once a wake frees a slot" {
    const testing = std.testing;
    const alloc = testing.allocator;
    const io = testing.io;

    const Q = BlockingQueue(u64, 1);
    const q = try Q.create(alloc);
    defer q.destroy(alloc);

    try testing.expectEqual(@as(Q.Size, 1), q.push(io, 1, .{ .instant = {} }));

    // A consumer that drains only when it is woken.
    const Consumer = struct {
        q: *Q,
        io: std.Io,
        woken: usize = 0,

        fn wake(self: *@This()) void {
            self.woken += 1;
            _ = self.q.pop(self.io);
        }
    };

    var consumer: Consumer = .{ .q = q, .io = io };
    const result = q.pushWake(
        io,
        2,
        &consumer,
        Consumer.wake,
        1 * std.time.ns_per_ms,
        3,
    );

    try testing.expectEqual(@as(Q.Size, 1), result);
    try testing.expectEqual(@as(usize, 1), consumer.woken);
    try testing.expect(q.pop(io).? == 2);
}

fn countWake(woken: *usize) void {
    woken.* += 1;
}
