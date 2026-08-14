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
    /// If the optional mutex is given, it must already be LOCKED. If the
    /// send would block, we'll unlock this mutex, resend the message, and
    /// lock it again. This handles an edge case where queues are full.
    /// This may not apply to all writer types.
    pub fn send(
        self: *Mailbox,
        msg: termio.Message,
        mutex: ?*std.Io.Mutex,
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
                if (mutex) |m| m.unlock(global.io());
                defer if (mutex) |m| m.lockUncancelable(global.io());
                if (mb.queue.push(global.io(), msg, .{ .forever = {} }) == 0) msg.deinit();
            },
        }
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
