const std = @import("std");
const Allocator = std.mem.Allocator;
const global = @import("../global.zig");
const xev = global.xev;
const renderer = @import("../renderer.zig");
const termio = @import("../termio.zig");
const BlockingQueue = @import("../datastruct/main.zig").BlockingQueue;

const log = std.log.scoped(.io_writer);

/// A queue used for storing messages that is periodically drained.
/// Typically used by a multi-threaded application. The capacity is
/// hardcoded to a value that empirically has made sense for Ghostty usage
/// but I'm open to changing it with good arguments.
const Queue = BlockingQueue(termio.Message, 64);

/// The location to where write-related messages are sent.
pub const Mailbox = union(enum) {
    // /// Write messages to an unbounded list backed by an allocator.
    // /// This is useful for single-threaded applications where you're not
    // /// afraid of running out of memory. You should be careful that you're
    // /// processing this in a timely manner though since some heavy workloads
    // /// will produce a LOT of messages.
    // ///
    // /// At the time of authoring this, the primary use case for this is
    // /// testing more than anything, but it probably will have a use case
    // /// in libghostty eventually.
    // unbounded: std.ArrayList(termio.Message),

    /// Write messages to a SPSC queue for multi-threaded applications.
    spsc: struct {
        queue: *Queue,

        /// There must be exactly one Async per mailbox. This union is a
        /// value type that gets copied on its way to its owner (Surface.init
        /// builds it, termio.Options carries it, Termio takes it), and the
        /// IOCP backend keeps its wait registration and a mutex inline in
        /// the Async, so it cannot survive living here by value. Boxing it
        /// makes an accidental copy harmless rather than silent.
        wakeup: *xev.Async,
    },

    /// Init the SPSC writer.
    pub fn initSPSC(alloc: Allocator) !Mailbox {
        const queue = try Queue.create(alloc);
        errdefer queue.destroy(alloc);

        const wakeup = try alloc.create(xev.Async);
        errdefer alloc.destroy(wakeup);
        wakeup.* = try .init();
        errdefer wakeup.deinit();

        return .{ .spsc = .{ .queue = queue, .wakeup = wakeup } };
    }

    pub fn deinit(self: *Mailbox, alloc: Allocator) void {
        switch (self.*) {
            .spsc => |*v| {
                while (v.queue.pop(global.io())) |msg| msg.deinit();
                v.queue.destroy(alloc);
                v.wakeup.deinit();
                alloc.destroy(v.wakeup);
            },
        }

        // Poison in safe builds so a second deinit crashes near the fault
        // instead of silently double-freeing. This does not protect the
        // other copies of the union, only the one being torn down.
        self.* = undefined;
    }

    /// Sends the given message without notifying there are messages.
    ///
    /// Takes ownership of `msg`: it is either queued or released, so a
    /// message that owns a write buffer cannot leak here.
    ///
    /// If the optional mutex is given, it must already be LOCKED. If the
    /// send would block, we'll unlock this mutex, resend the message, and
    /// lock it again. This handles an edge case where queues are full.
    /// This may not apply to all writer types.
    ///
    /// The budget is the droppable one because most of this queue's
    /// producers are UI-thread input handlers reaching us through
    /// `Surface.queueIo`. A push that parks here parks the thread that
    /// drains the app mailbox, and the writer thread this waits on can
    /// itself be parked on that same app mailbox -- two unbounded hops
    /// that hold each other up forever, which is what this bound exists
    /// to break.
    ///
    /// Use `sendRequired` for a message the consumer is blocked on
    /// rather than merely informed by.
    pub fn send(
        self: *Mailbox,
        msg: termio.Message,
        mutex: ?*std.Io.Mutex,
    ) void {
        self.sendBudgeted(msg, mutex, .droppable);
    }

    /// Send a message the consumer is blocked on, from a producer that
    /// is never the UI thread.
    ///
    /// This does NOT promise the message is never dropped, and nothing
    /// here could: a producer that waits without a limit on a wedged
    /// consumer does not deliver the message either, it just stops being
    /// a producer. For the one caller this has -- tmux control-mode
    /// commands on the pty read thread -- that trade is strictly worse,
    /// because a read thread that stops reading stops draining the
    /// child's output and wedges the session harder than losing one
    /// command would. What `required` buys is a budget two orders of
    /// magnitude longer than a droppable send's, and immunity to the
    /// wedge latch, so the command survives anything short of a consumer
    /// that has genuinely stopped.
    pub fn sendRequired(
        self: *Mailbox,
        msg: termio.Message,
        mutex: ?*std.Io.Mutex,
    ) void {
        self.sendBudgeted(msg, mutex, .required);
    }

    /// What a send spends before it gives the message up, chosen by what
    /// the caller can tolerate losing rather than by which mailbox it
    /// happens to be pushing to. One number for the whole queue cannot
    /// be right for both a mouse-motion report and a tmux command.
    pub const Budget = struct {
        timeout_ns: u64,
        attempts: usize,
        wedge: Queue.Wedge,

        /// One event, whose producer can be the UI thread. Short budget,
        /// and it fails fast while the consumer is latched wedged so a
        /// stream of events costs one budget rather than one apiece.
        pub const droppable: Budget = .{
            .timeout_ns = Queue.wake_retry_timeout_ns_ui,
            .attempts = Queue.wake_retry_attempts_ui,
            .wedge = .fail_fast,
        };

        /// State the consumer has to observe, from a background
        /// producer. Long budget, and it ignores the latch: a droppable
        /// send losing its budget must not decide this one's fate.
        pub const required: Budget = .{
            .timeout_ns = Queue.wake_retry_timeout_ns,
            .attempts = Queue.wake_retry_attempts,
            .wedge = .persist,
        };
    };

    fn sendBudgeted(
        self: *Mailbox,
        msg: termio.Message,
        mutex: ?*std.Io.Mutex,
        budget: Budget,
    ) void {
        self.sendBounded(
            msg,
            mutex,
            budget.timeout_ns,
            budget.attempts,
            budget.wedge,
        );
    }

    fn sendBounded(
        self: *Mailbox,
        msg: termio.Message,
        mutex: ?*std.Io.Mutex,
        timeout_ns: u64,
        max_attempts: usize,
        wedge: Queue.Wedge,
    ) void {
        switch (self.*) {
            .spsc => |*mb| send: {
                // Try to write to the queue with an instant timeout. This is the
                // fast path because we can queue without a lock.
                if (mb.queue.push(global.io(), msg, .{ .instant = {} }) > 0) break :send;

                // If we enter this conditional, the queue is full. We wake up
                // the writer thread so that it can process messages to clear up
                // space. However, the writer thread may require the renderer
                // lock so we need to unlock.
                mb.wakeup.notify() catch |err| {
                    log.warn("failed to wake up writer, data will be dropped err={}", .{err});
                    msg.deinit();
                    return;
                };

                // Unlock the renderer state so the writer thread can acquire it.
                // Then try to queue our message before continuing. This is a very
                // slow path because we are having a lot of contention for data.
                // But this only gets triggered in certain pathological cases.
                //
                // Note that writes themselves don't require a lock, but there
                // are other messages in the writer queue (resize, focus) that
                // could acquire the lock. This is why we have to release our lock
                // here.
                //
                // The wake above is what makes the writer thread drain,
                // and a single notify can be lost outright on the IOCP
                // backend, so keep re-issuing it while we wait rather
                // than sleeping on the queue with no wake source left.
                //
                // The queue wait is bounded. A message here can own a
                // write buffer, but `termio.Message.deinit` releases
                // exactly those variants -- the failed-notify path above
                // already calls it -- so giving up costs one message
                // rather than the thread.
                //
                // The function as a whole is NOT bounded: the deferred
                // re-acquire below is `lockUncancelable`, which has no
                // timed form, so a renderer-state mutex held elsewhere
                // still stalls us after the budget expires. Pre-existing
                // shape; the budget bounds the queue wait, not this.
                if (mutex) |m| m.unlock(global.io());
                defer if (mutex) |m| m.lockUncancelable(global.io());
                const size = mb.queue.pushWake(
                    global.io(),
                    msg,
                    mb.wakeup,
                    notifyWake,
                    timeout_ns,
                    max_attempts,
                    wedge,
                );
                if (size == 0) {
                    log.warn("io mailbox full, message dropped", .{});
                    msg.deinit();
                }
            },
        }
    }

    /// The `pushWake` wake for our writer thread. A notify that fails
    /// means the loop is gone, which the retry budget already gives up
    /// on.
    fn notifyWake(wakeup: *xev.Async) void {
        wakeup.notify() catch {};
    }

    /// Notify that there are new messages. This may be a noop depending
    /// on the writer type.
    pub fn notify(self: *Mailbox) void {
        switch (self.*) {
            .spsc => |*v| v.wakeup.notify() catch |err| {
                log.warn("failed to notify writer, data will be dropped err={}", .{err});
            },
        }
    }
};

test "Mailbox: spsc wakeup survives copying the union" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var mailbox = try Mailbox.initSPSC(alloc);
    defer mailbox.deinit(alloc);

    // This assert is what actually guards the invariant: boxing makes the
    // wait/notify below share one Async by construction, so only a revert
    // to a by-value field can fail, and it fails here rather than hanging
    // on a notify that went to a different instance.
    try testing.expectEqual(*xev.Async, @TypeOf(mailbox.spsc.wakeup));

    var loop = try xev.Loop.init(.{});
    defer loop.deinit();

    // Exercise the wiring end to end. Only meaningful on backends that keep
    // the registration inline in the Async; the test backend on Linux is
    // epoll, whose Async is a bare eventfd and is copy-safe either way.
    var consumer = mailbox;
    var producer = mailbox;

    var fired: bool = false;
    var c: xev.Completion = .{};
    consumer.spsc.wakeup.wait(&loop, &c, bool, &fired, (struct {
        fn callback(
            ud: ?*bool,
            _: *xev.Loop,
            _: *xev.Completion,
            r: xev.Async.WaitError!void,
        ) xev.CallbackAction {
            _ = r catch return .disarm;
            ud.?.* = true;
            return .disarm;
        }
    }).callback);

    producer.notify();
    try loop.run(.until_done);
    try testing.expect(fired);
}

test "Mailbox: a push it has to give up frees the message" {
    const testing = std.testing;
    const alloc = testing.allocator;
    const io = global.io();

    var mailbox = try Mailbox.initSPSC(alloc);
    defer mailbox.deinit(alloc);

    var loop = try xev.Loop.init(.{});
    defer loop.deinit();

    // Register the wait so the notify inside `send` succeeds. Without it
    // the send can take the failed-notify branch instead, which frees the
    // message for a different reason and would make this test pass
    // whatever the retry path does.
    var fired: bool = false;
    var c: xev.Completion = .{};
    mailbox.spsc.wakeup.wait(&loop, &c, bool, &fired, (struct {
        fn callback(
            ud: ?*bool,
            _: *xev.Loop,
            _: *xev.Completion,
            r: xev.Async.WaitError!void,
        ) xev.CallbackAction {
            _ = r catch return .disarm;
            ud.?.* = true;
            return .disarm;
        }
    }).callback);

    // Fill the queue and never drain it. A writer thread that is alive
    // but parked pushing to the full app mailbox looks exactly like this
    // from a producer's side, and the producer here is usually the UI
    // thread coming through `Surface.queueIo`.
    while (mailbox.spsc.queue.push(io, .{ .focused = true }, .{ .instant = {} }) > 0) {}

    // A write too large for the union is heap allocated, so
    // std.testing.allocator fails this test if the give-up path leaks it.
    const data = "e" ** 128;
    const msg = try termio.Message.writeReq(alloc, @as([]const u8, data));
    try testing.expect(msg == .write_alloc);

    // A tiny budget so the test finishes; production uses `Budget`.
    mailbox.sendBounded(msg, null, 1 * std.time.ns_per_ms, 3, .fail_fast);

    // Nothing landed, so nothing was dropped from the queue either.
    try testing.expect(mailbox.spsc.queue.len == 64);
}

test "Mailbox: a required send outlasts a droppable one and ignores the wedge" {
    const testing = std.testing;

    // The policy, not the numbers: a message the consumer is blocked on
    // gets more budget than one that is merely an event, and a droppable
    // send that has already lost its budget cannot shorten it.
    const droppable = Mailbox.Budget.droppable;
    const required = Mailbox.Budget.required;

    try testing.expect(
        required.timeout_ns * required.attempts >
            droppable.timeout_ns * droppable.attempts,
    );
    try testing.expectEqual(Queue.Wedge.fail_fast, droppable.wedge);
    try testing.expectEqual(Queue.Wedge.persist, required.wedge);
}
