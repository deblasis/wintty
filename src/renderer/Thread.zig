//! Represents the renderer thread logic. The renderer thread is able to
//! be woken up to render.
pub const Thread = @This();

const std = @import("std");
const builtin = @import("builtin");
const global = @import("../global.zig");
const xev = global.xev;
const crash = @import("../crash/main.zig");
const internal_os = @import("../os/main.zig");
const rendererpkg = @import("../renderer.zig");
const apprt = @import("../apprt.zig");
const configpkg = @import("../config.zig");
const terminalpkg = @import("../terminal/main.zig");
const BlockingQueue = @import("../datastruct/main.zig").BlockingQueue;
const App = @import("../App.zig");

const Allocator = std.mem.Allocator;
const log = std.log.scoped(.renderer_thread);

const CURSOR_BLINK_INTERVAL = 600;

/// Whether calls to `drawFrame` must be done from the app thread.
///
/// The type used for sending messages to the IO thread. For now this is
/// hardcoded with a capacity. We can make this a comptime parameter in
/// the future if we want it configurable.
pub const Mailbox = BlockingQueue(rendererpkg.Message, 64);

/// Allocator used for some state
alloc: std.mem.Allocator,

/// The main event loop for the application. The user data of this loop
/// is always the allocator used to create the loop. This is a convenience
/// so that users of the loop always have an allocator.
loop: xev.Loop,

/// This can be used to wake up the renderer and force a render safely from
/// any thread.
wakeup: xev.Async,
wakeup_c: xev.Completion = .{},

/// This can be used to stop the renderer on the next loop iteration.
stop: xev.Async,
stop_c: xev.Completion = .{},

/// The timer used for animations (custom shaders, Kitty graphics).
/// Normal rendering is driven by wakeup messages instead.
render_h: xev.Timer,
render_c: xev.Completion = .{},
render_c_cancel: xev.Completion = .{},

/// The kind of work the currently scheduled animation wake needs,
/// stored when the timer is armed.
animation_wake: rendererpkg.Renderer.AnimationWake.Kind = .draw,

/// This async is used to force a draw immediately. This does not
/// coalesce like the wakeup does.
draw_now: xev.Async,
draw_now_c: xev.Completion = .{},

/// The timer used for cursor blinking
cursor_h: xev.Timer,
cursor_c: xev.Completion = .{},
cursor_c_cancel: xev.Completion = .{},

/// Safety-net drain while the surface is not visible. Producers push
/// to this thread's mailbox and then notify `wakeup`; on the IOCP
/// backend that notify can be lost in the window between a completion
/// firing and its re-arm (#1036), and while hidden nothing else ticks
/// this loop (focus loss already canceled the cursor timer, and the
/// termio's output-driven wakes are gated), so the mailbox can sit
/// full with this thread asleep. This timer drains it every few
/// seconds regardless of wakes, bounding any lost-notify stall. It is
/// armed on the `.visible = false` transition and canceled on
/// `.visible = true`.
hidden_drain_h: xev.Timer,
hidden_drain_c: xev.Completion = .{},
hidden_drain_c_cancel: xev.Completion = .{},

/// Incremental scrollback compression scheduling.
compression: Compression = undefined,

/// The surface we're rendering to.
surface: *apprt.Surface,

/// The underlying renderer implementation.
renderer: *rendererpkg.Renderer,

/// Pointer to the shared state that is used to generate the final render.
state: *rendererpkg.State,

/// The mailbox that can be used to send this thread messages. Note
/// this is a blocking queue so if it is full you will get errors (or block).
mailbox: *Mailbox,

/// Mailbox to send messages to the app thread
app_mailbox: App.Mailbox,

/// Configuration we need derived from the main config.
config: DerivedConfig,

flags: packed struct {
    /// This is true when a blinking cursor should be visible and false
    /// when it should not be visible. This is toggled on a timer by the
    /// thread automatically.
    cursor_blink_visible: bool = false,

    /// This is true when the inspector is active.
    has_inspector: bool = false,

    /// This is true when the view is visible. This is used to determine
    /// if we should be rendering or not.
    visible: bool = true,

    /// This is true when the view is focused. This defaults to true
    /// and it is up to the apprt to set the correct value.
    focused: bool = true,
} = .{},

/// Visibility as other threads may read it. `flags.visible` is owned by
/// this thread; the atomic is the same fact published lock-free so the
/// termio side can skip waking us for output nobody can see. The
/// `.visible=true` transition rebuilds from terminal state, so dropped
/// wakes cost nothing but the invisible frames.
visible_flag: std.atomic.Value(bool) = .init(true),

pub const DerivedConfig = struct {
    scrollback_compression: bool,

    pub fn init(config: *const configpkg.Config) DerivedConfig {
        return .{
            .scrollback_compression = config.@"scrollback-compression",
        };
    }
};

/// Initialize the thread. This does not START the thread. This only sets
/// up all the internal state necessary prior to starting the thread. It
/// is up to the caller to start the thread with the threadMain entrypoint.
pub fn init(
    alloc: Allocator,
    config: *const configpkg.Config,
    surface: *apprt.Surface,
    renderer_impl: *rendererpkg.Renderer,
    state: *rendererpkg.State,
    app_mailbox: App.Mailbox,
) !Thread {
    // Create our event loop.
    var loop = try xev.Loop.init(.{});
    errdefer loop.deinit();

    // This async handle is used to "wake up" the renderer and force a render.
    var wakeup_h = try xev.Async.init();
    errdefer wakeup_h.deinit();

    // This async handle is used to stop the loop and force the thread to end.
    var stop_h = try xev.Async.init();
    errdefer stop_h.deinit();

    // The primary timer for rendering.
    var render_h = try xev.Timer.init();
    errdefer render_h.deinit();

    // Draw now async, see comments.
    var draw_now = try xev.Async.init();
    errdefer draw_now.deinit();

    // Setup a timer for blinking the cursor
    var cursor_timer = try xev.Timer.init();
    errdefer cursor_timer.deinit();

    // The safety-net drain for hidden surfaces (see hidden_drain_h).
    var hidden_drain_timer = try xev.Timer.init();
    errdefer hidden_drain_timer.deinit();

    // The mailbox for messaging this thread
    var mailbox = try Mailbox.create(alloc);
    errdefer mailbox.destroy(alloc);

    var result: Thread = .{
        .alloc = alloc,
        .config = .init(config),
        .loop = loop,
        .wakeup = wakeup_h,
        .stop = stop_h,
        .render_h = render_h,
        .draw_now = draw_now,
        .cursor_h = cursor_timer,
        .hidden_drain_h = hidden_drain_timer,
        .surface = surface,
        .renderer = renderer_impl,
        .state = state,
        .mailbox = mailbox,
        .app_mailbox = app_mailbox,
    };

    // Only enable compression if we have it enabled... save some
    // minor resources.
    if (comptime terminalpkg.compression_enabled) {
        result.compression = try .init();
    }

    return result;
}

/// Clean up the thread. This is only safe to call once the thread
/// completes executing; the caller must join prior to this.
pub fn deinit(self: *Thread) void {
    self.stop.deinit();
    self.wakeup.deinit();
    self.render_h.deinit();
    self.draw_now.deinit();
    self.cursor_h.deinit();
    self.hidden_drain_h.deinit();
    if (comptime terminalpkg.compression_enabled)
        self.compression.deinit();
    self.loop.deinit();

    // Nothing can possibly access the mailbox anymore, destroy it.
    self.mailbox.destroy(self.alloc);
}

/// The main entrypoint for the thread.
pub fn threadMain(self: *Thread) void {
    // Call child function so we can use errors...
    self.threadMain_() catch |err| {
        // In the future, we should expose this on the thread struct.
        log.warn("error in renderer err={}", .{err});
    };
}

fn threadMain_(self: *Thread) !void {
    defer log.debug("renderer thread exited", .{});

    // Right now, on Darwin, `std.Thread.setName` can only name the current
    // thread, and we have no way to get the current thread from within it,
    // so instead we use this code to name the thread instead.
    if (builtin.os.tag.isDarwin()) {
        internal_os.macos.pthread_setname_np(&"renderer".*);
    }

    // Setup our crash metadata
    crash.sentry.thread_state = .{
        .type = .renderer,
        .surface = self.renderer.surface_mailbox.surface,
    };
    defer crash.sentry.thread_state = null;

    // Setup our thread QoS
    self.setQosClass();

    // Run our loop start/end callbacks if the renderer cares.
    const has_loop = @hasDecl(rendererpkg.Renderer, "loopEnter");
    if (has_loop) try self.renderer.loopEnter(self);
    defer if (has_loop) self.renderer.loopExit();

    // Run our thread start/end callbacks. This is important because some
    // renderers have to do per-thread setup. For example, OpenGL has to set
    // some thread-local state since that is how it works.
    try self.renderer.threadEnter(self.surface);
    defer self.renderer.threadExit();

    // Start the async handlers
    self.wakeup.wait(&self.loop, &self.wakeup_c, Thread, self, wakeupCallback);
    self.stop.wait(&self.loop, &self.stop_c, Thread, self, stopCallback);
    self.draw_now.wait(&self.loop, &self.draw_now_c, Thread, self, drawNowCallback);

    // Send an initial wakeup message so that we render right away.
    try self.wakeup.notify();

    // Start blinking the cursor.
    self.cursor_h.run(
        &self.loop,
        &self.cursor_c,
        cursorBlinkInterval(),
        Thread,
        self,
        cursorTimerCallback,
    );

    // Arm the animation timer in case the renderer already needs
    // animation wakes (e.g. custom shaders loaded at startup).
    self.armAnimationTimer();

    // Run
    log.debug("starting renderer thread", .{});
    defer log.debug("starting renderer thread shutdown", .{});
    _ = try self.loop.run(.until_done);
}

fn setQosClass(self: *const Thread) void {
    // Thread QoS classes are only relevant on macOS.
    if (comptime !builtin.target.os.tag.isDarwin()) return;

    const class: internal_os.macos.QosClass = class: {
        // If we aren't visible (our view is fully occluded) then we
        // always drop our rendering priority down because it's just
        // mostly wasted work.
        //
        // The renderer itself should be doing this as well (for example
        // Metal will stop our DisplayLink) but this also helps with
        // general forced updates and CPU usage i.e. a rebuild cells call.
        if (!self.flags.visible) break :class .utility;

        // If we're not focused, but we're visible, then we set a higher
        // than default priority because framerates still matter but it isn't
        // as important as when we're focused.
        if (!self.flags.focused) break :class .user_initiated;

        // We are focused and visible, we are the definition of user interactive.
        break :class .user_interactive;
    };

    if (internal_os.macos.setQosClass(class)) {
        log.debug("thread QoS class set class={}", .{class});
    } else |err| {
        log.warn("error setting QoS class err={}", .{err});
    }
}

/// Drain the mailbox.
fn drainMailbox(self: *Thread) !void {
    // There's probably a more elegant way to do this...
    //
    // This is effectively an @autoreleasepool{} block, which we need in
    // order to ensure that autoreleased objects are properly released.
    const pool = if (builtin.os.tag.isDarwin())
        @import("objc").AutoreleasePool.init()
    else
        void;
    defer if (builtin.os.tag.isDarwin()) pool.deinit();

    while (self.mailbox.pop(global.io())) |message| {
        log.debug("mailbox message={}", .{message});
        switch (message) {
            .crash => @panic("crash request, crashing intentionally"),

            .visible => |v| visible: {
                // If our state didn't change we do nothing.
                if (self.flags.visible == v) break :visible;

                // Set our visible state
                self.flags.visible = v;

                // Publish for other threads: termio drops its
                // output-driven redraw wakes while hidden (a wake with
                // nothing to draw costs more than the frames it would
                // have carried), so they need a lock-free read of this.
                self.visible_flag.store(v, .release);

                // Visibility affects our QoS class
                self.setQosClass();

                // Notify the renderer so it can update any state. Before the
                // forced draw below, not after: becoming visible arms a
                // health re-report that the next draw carries, and arming it
                // afterwards would leave that to whatever draw happened to
                // come next.
                self.renderer.setVisible(v);

                // If we became visible then we immediately rebuild cells
                // (renderCallback skips updateFrame while invisible) and
                // draw. Going through renderCallback also reschedules
                // any Kitty graphics animation wakeup that lapsed
                // while we were invisible.
                if (v) _ = renderCallback(self, undefined, undefined, {});

                // Hiding is the strongest "activity stopped" signal a
                // surface gets, and it is also the moment this thread's
                // other wake sources go quiet: output-driven wakes are
                // dropped while hidden (see visible_flag above), so
                // without this kick the compression scheduler would
                // starve on exactly the idle surfaces it exists for.
                // The wake is debounced by its own idle interval and
                // no-ops when nothing changed.
                self.compression.wake(self);

                // The safety-net drain: while hidden, nothing else ticks
                // this loop, so a lost wakeup notify (see hidden_drain_h)
                // would leave producers blocked on a full mailbox. Arm
                // the slow drain on the way in, cancel it on the way out
                // -- the loop is properly awake again by the time a
                // visible transition is processed.
                if (v) {
                    self.cancelHiddenDrain();
                } else {
                    self.armHiddenDrain();
                }

                // Note that we're explicitly today not stopping any
                // cursor timers, draw timers, etc. These things have very
                // little resource cost and properly maintaining their active
                // state across different transitions is going to be bug-prone,
                // so its easier to just let them keep firing and have them
                // check the visible state themselves to control their behavior.
            },

            .deep_idle => {
                // Tier B, renderer half: the image copies and the shaped-run
                // cache exist only to make the next frame cheap, and a
                // deep-idle surface's next frame may be arbitrarily far
                // away. The terminal's image storage stays the source of
                // truth; trimIdleMemory latches images_lost so the first
                // updateFrame after the surface is shown rebuilds the
                // copies -- without the latch, live placements would draw
                // with nothing behind them, because no frame revisits
                // placements whose terminal state did not change.
                self.renderer.trimIdleMemory();

                // Idle is the strongest "activity stopped" signal there is,
                // and it arrives on surfaces that may still be visible (an
                // untouched foreground tab), where the hide transition never
                // fires: kick the compression scheduler the same way a hide
                // does so cold pages shed without waiting on a render.
                self.compression.wake(self);
            },

            .focus => |v| focus: {
                // If our state didn't change we do nothing.
                if (self.flags.focused == v) break :focus;

                // Set our state
                self.flags.focused = v;

                // Focus affects our QoS class
                self.setQosClass();

                // Set it on the renderer
                try self.renderer.setFocus(v);

                // Focus gates custom shader animation, so re-arm
                // the animation timer for the new state.
                self.armAnimationTimer();

                if (!v) {
                    // Losing focus stops the cursor blink below, which is
                    // this thread's last periodic wake while otherwise
                    // visible and idle -- the same starvation the hide
                    // transition kicks against (see .visible).
                    self.compression.wake(self);

                    // If we're not focused, then we stop the cursor blink
                    if (self.cursor_c.state() == .active and
                        self.cursor_c_cancel.state() == .dead)
                    {
                        self.cursor_h.cancel(
                            &self.loop,
                            &self.cursor_c,
                            &self.cursor_c_cancel,
                            void,
                            null,
                            cursorCancelCallback,
                        );
                    }
                } else {
                    // If we're focused, we immediately show the cursor again
                    // and then restart the timer.
                    if (self.cursor_c.state() != .active) {
                        self.flags.cursor_blink_visible = true;
                        self.cursor_h.run(
                            &self.loop,
                            &self.cursor_c,
                            cursorBlinkInterval(),
                            Thread,
                            self,
                            cursorTimerCallback,
                        );
                    }
                }
            },

            .reset_cursor_blink => {
                self.flags.cursor_blink_visible = true;
                if (self.cursor_c.state() == .active) {
                    self.cursor_h.reset(
                        &self.loop,
                        &self.cursor_c,
                        &self.cursor_c_cancel,
                        cursorBlinkInterval(),
                        Thread,
                        self,
                        cursorTimerCallback,
                    );
                }
            },

            .font_grid => |grid| {
                self.renderer.setFontGrid(grid.grid);
                grid.set.deref(grid.old_key);
            },

            .resize => |v| self.renderer.setScreenSize(v),

            .change_config => |config| {
                defer config.alloc.destroy(config.thread);
                defer config.alloc.destroy(config.impl);
                try self.changeConfig(config.thread);
                try self.renderer.changeConfig(config.impl);

                // The config affects what animation wakes the
                // renderer needs (custom shaders, animation mode).
                self.armAnimationTimer();
            },

            .search_viewport_matches => |v| {
                // Note we don't free the new value because we expect our
                // allocators to match.
                if (self.renderer.search_matches) |*m| m.arena.deinit();
                self.renderer.search_matches = v;
                self.renderer.search_matches_dirty = true;
            },

            .search_selected_match => |v| {
                // Note we don't free the new value because we expect our
                // allocators to match.
                if (self.renderer.search_selected_match) |*m| m.arena.deinit();
                self.renderer.search_selected_match = v;
                self.renderer.search_matches_dirty = true;
            },

            .inspector => |v| {
                self.flags.has_inspector = v;
            },

            .macos_display_id => |v| {
                if (@hasDecl(rendererpkg.Renderer, "setMacOSDisplayID")) {
                    try self.renderer.setMacOSDisplayID(v, &self.draw_now);
                }
            },
        }
    }
}

fn changeConfig(self: *Thread, config: *const DerivedConfig) !void {
    // A newly enabled scheduler must reconsider existing history even when no
    // terminal activity occurred while compression was disabled.
    if (comptime terminalpkg.compression_enabled) {
        if (!self.config.scrollback_compression and
            config.scrollback_compression)
        {
            self.compression.activity = null;
        }
    }

    self.config = config.*;
}

/// Trigger a draw. This will not update frame data or anything, it will
/// just trigger a draw/paint.
fn drawFrame(self: *Thread, now: bool) void {
    // If we're invisible, we do not draw.
    if (!self.flags.visible) return;

    // If the renderer is managing a vsync on its own, we only draw
    // when we're forced to via `now`.
    if (!now and self.renderer.hasVsync()) return;

    if (apprt.must_draw_from_app_thread) {
        _ = self.app_mailbox.push(
            .{ .redraw_surface = self.surface },
            .{ .instant = {} },
        );
    } else {
        self.renderer.drawFrame(false) catch |err|
            log.warn("error drawing err={}", .{err});
    }
}

fn wakeupCallback(
    self_: ?*Thread,
    _: *xev.Loop,
    _: *xev.Completion,
    r: xev.Async.WaitError!void,
) xev.CallbackAction {
    _ = r catch |err| {
        log.err("error in wakeup err={}", .{err});
        return .rearm;
    };

    const t = self_.?;

    // When we wake up, we check the mailbox. Mailbox producers should
    // wake up our thread after publishing.
    t.drainMailbox() catch |err|
        log.err("error draining mailbox err={}", .{err});

    // Render immediately
    _ = renderCallback(t, undefined, undefined, {});

    // PageList mutations maintain their own compression dirty state. Checking
    // it here covers output, resize, and viewport scrolling uniformly.
    t.compression.wake(t);

    // The below is not used anymore but if we ever want to introduce
    // a configuration to introduce a delay to coalesce renders, we can
    // use this.
    //
    // // If the timer is already active then we don't have to do anything.
    // if (t.render_c.state() == .active) return .rearm;
    //
    // // Timer is not active, let's start it
    // t.render_h.run(
    //     &t.loop,
    //     &t.render_c,
    //     10,
    //     Thread,
    //     t,
    //     renderCallback,
    // );

    return .rearm;
}

fn drawNowCallback(
    self_: ?*Thread,
    _: *xev.Loop,
    _: *xev.Completion,
    r: xev.Async.WaitError!void,
) xev.CallbackAction {
    _ = r catch |err| {
        log.err("error in draw now err={}", .{err});
        return .rearm;
    };

    // Draw immediately
    const t = self_.?;
    t.drawFrame(true);

    // A draw can leave the renderer wanting another wake (a device
    // rebuild that failed and is due to retry), and this path was the
    // only draw that did not ask.
    t.armAnimationTimer();

    return .rearm;
}

fn renderCallback(
    self_: ?*Thread,
    _: *xev.Loop,
    _: *xev.Completion,
    r: xev.Timer.RunError!void,
) xev.CallbackAction {
    _ = r catch |err| switch (err) {
        // Sent when a scheduled animation wakeup is superseded by a
        // newer one (Timer.reset cancels the pending run). Nothing to
        // do; the replacement timer carries on.
        error.Canceled => return .disarm,
        else => unreachable,
    };
    const t: *Thread = self_ orelse {
        // This shouldn't happen so we log it.
        log.warn("render callback fired without data set", .{});
        return .disarm;
    };

    // If we're not visible there's no point spending CPU rebuilding cells —
    // we'll catch up when the .visible mailbox message flips us back on.
    // Kitty graphics animations pause with us and resume on visibility.
    if (!t.flags.visible) return .disarm;

    // Update our frame data
    t.renderer.updateFrame(
        t.state,
        t.flags.cursor_blink_visible,
    ) catch |err|
        log.warn("error rendering err={}", .{err});

    // Draw
    t.drawFrame(false);

    // Schedule the next animation wake, if the renderer needs one.
    t.armAnimationTimer();

    return .disarm;
}

/// Schedule the animation timer for the renderer's next animation
/// wake, if it needs one.
///
/// This is called after every frame update or animation draw and
/// whenever the wake inputs change (focus, config, visibility
/// regain). Resetting a pending timer is always safe: every call
/// recomputes the wake, so the deadline only ever moves toward the
/// actual next wake.
/// How often the hidden-surface safety-net drain fires. Slow on
/// purpose: it exists to bound a lost-notify stall (#1036), not to do
/// work -- anything real still arrives through the wakeup async, and
/// the cost of a false alarm is one mailbox drain of an empty queue.
const hidden_drain_interval_ms: u64 = 2_000;

/// Arm the safety-net drain (idempotent): a repeating timer whose
/// callback drains the mailbox and re-arms itself while the surface
/// stays hidden. Called on the `.visible = false` transition.
fn armHiddenDrain(self: *Thread) void {
    if (self.hidden_drain_c.state() == .active) return;
    self.hidden_drain_h.run(
        &self.loop,
        &self.hidden_drain_c,
        hidden_drain_interval_ms,
        Thread,
        self,
        hiddenDrainCallback,
    );
}

/// Cancel an armed safety-net drain. Called on `.visible = true`; also
/// safe on the teardown path where the loop is about to stop anyway
/// (the timer deinit after thread join handles that case).
fn cancelHiddenDrain(self: *Thread) void {
    if (self.hidden_drain_c.state() != .active) return;
    if (self.hidden_drain_c_cancel.state() == .active) return;
    self.hidden_drain_h.cancel(
        &self.loop,
        &self.hidden_drain_c,
        &self.hidden_drain_c_cancel,
        Thread,
        self,
        hiddenDrainCancelCallback,
    );
}

/// The tail of the hidden drain: keep the timer going while hidden.
/// Extracted so the idiom is unit-tested against the real xev backend.
fn hiddenDrainRearm(
    loop: *xev.Loop,
    timer: *xev.Timer,
    c: *xev.Completion,
    comptime Userdata: type,
    userdata: ?*Userdata,
    comptime cb: fn (?*Userdata, *xev.Loop, *xev.Completion, xev.Timer.RunError!void) xev.CallbackAction,
) xev.CallbackAction {
    // Re-run with a fresh deadline and disarm this completion, the way
    // cursorTimerCallback and animationTimerCallback do. `.rearm` on a
    // timer re-inserts the elapsed deadline on the IOCP backend and
    // starves the port wait; that starved the stop async and hung
    // Surface.deinit's join on the UI thread.
    timer.run(loop, c, hidden_drain_interval_ms, Userdata, userdata, cb);
    return .disarm;
}

fn hiddenDrainCallback(
    self_: ?*Thread,
    _: *xev.Loop,
    _: *xev.Completion,
    r: xev.Timer.RunError!void,
) xev.CallbackAction {
    _ = r catch |err| switch (err) {
        // A reset supersedes this run; the replacing timer carries on.
        error.Canceled => return .disarm,
        else => unreachable,
    };
    const t = self_ orelse return .disarm;

    // The drain itself. Whatever pushed messages our way gets them
    // processed on this tick even if their notify was the one that got
    // lost; the not-full condition signal inside the drain wakes any
    // blocked producer immediately.
    t.drainMailbox() catch |err|
        log.err("error draining mailbox (hidden safety net) err={}", .{err});

    // Stay armed while hidden; the .visible = true transition cancels.
    if (!t.flags.visible) return hiddenDrainRearm(&t.loop, &t.hidden_drain_h, &t.hidden_drain_c, Thread, t, hiddenDrainCallback);
    return .disarm;
}

fn hiddenDrainCancelCallback(
    _: ?*Thread,
    _: *xev.Loop,
    _: *xev.Completion,
    r: xev.Timer.CancelError!void,
) xev.CallbackAction {
    _ = r catch {};
    return .disarm;
}

fn armAnimationTimer(self: *Thread) void {
    const wake = self.renderer.animationWake() orelse return;
    self.animation_wake = wake.kind;
    self.render_h.reset(
        &self.loop,
        &self.render_c,
        &self.render_c_cancel,
        wake.delay_ms,
        Thread,
        self,
        animationTimerCallback,
    );
}

fn animationTimerCallback(
    self_: ?*Thread,
    _: *xev.Loop,
    _: *xev.Completion,
    r: xev.Timer.RunError!void,
) xev.CallbackAction {
    _ = r catch |err| switch (err) {
        // Sent when a scheduled animation wake is superseded by a
        // newer one (Timer.reset cancels the pending run). Nothing to
        // do; the replacement timer carries on.
        error.Canceled => return .disarm,
        else => unreachable,
    };
    const t: *Thread = self_ orelse {
        // This shouldn't happen so we log it.
        log.warn("animation callback fired without data set", .{});
        return .disarm;
    };

    // Animations pause entirely while we're invisible; the .visible
    // mailbox message re-arms us when we can be seen again.
    if (!t.flags.visible) return .disarm;

    switch (t.animation_wake) {
        // Frame data must be updated (a Kitty animation frame is
        // due). renderCallback updates, draws, and re-arms us.
        .update => return renderCallback(
            t,
            undefined,
            undefined,
            {},
        ),

        // A redraw alone suffices (custom shader time uniform).
        // Draw calls don't update from the terminal state so they
        // are much cheaper than a frame update.
        .draw => {
            t.drawFrame(false);
            t.armAnimationTimer();
            return .disarm;
        },
    }
}

fn cursorTimerCallback(
    self_: ?*Thread,
    _: *xev.Loop,
    _: *xev.Completion,
    r: xev.Timer.RunError!void,
) xev.CallbackAction {
    _ = r catch |err| switch (err) {
        // This is sent when our timer is canceled. That's fine.
        error.Canceled => return .disarm,

        else => {
            log.warn("error in cursor timer callback err={}", .{err});
            unreachable;
        },
    };

    const t: *Thread = self_ orelse {
        // This shouldn't happen so we log it.
        log.warn("render callback fired without data set", .{});
        return .disarm;
    };

    t.flags.cursor_blink_visible = !t.flags.cursor_blink_visible;
    t.wakeup.notify() catch {};

    t.cursor_h.run(
        &t.loop,
        &t.cursor_c,
        cursorBlinkInterval(),
        Thread,
        t,
        cursorTimerCallback,
    );
    return .disarm;
}

fn cursorCancelCallback(
    _: ?*void,
    _: *xev.Loop,
    _: *xev.Completion,
    r: xev.Timer.CancelError!void,
) xev.CallbackAction {
    // This makes it easier to work across platforms where different platforms
    // support different sets of errors, so we just unify it.
    const CancelError = xev.Timer.CancelError || error{
        Canceled,
        NotFound,
        Unexpected,
    };

    _ = r catch |err| switch (@as(CancelError, @errorCast(err))) {
        error.Canceled => {}, // success
        error.NotFound => {}, // completed before it could cancel
        else => {
            log.warn("error in cursor cancel callback err={}", .{err});
            unreachable;
        },
    };

    return .disarm;
}

// fn prepFrameCallback(h: *libuv.Prepare) void {
//     _ = h;
//
//     tracy.frameMark();
// }

fn stopCallback(
    self_: ?*Thread,
    _: *xev.Loop,
    _: *xev.Completion,
    r: xev.Async.WaitError!void,
) xev.CallbackAction {
    _ = r catch unreachable;
    self_.?.loop.stop();
    return .disarm;
}

/// Returns the interval for the blinking cursor in milliseconds.
fn cursorBlinkInterval() u64 {
    if (std.valgrind.runningOnValgrind() > 0) {
        // If we're running under Valgrind, the cursor blink adds enough
        // churn that it makes some stalls annoying unless you're on a
        // super powerful computer, so we delay it.
        //
        // This is a hack, we should change some of our cursor timer
        // logic to be more efficient:
        // https://github.com/ghostty-org/ghostty/issues/8003
        return CURSOR_BLINK_INTERVAL * 5;
    }

    return CURSOR_BLINK_INTERVAL;
}

/// Schedules incremental terminal compression after renderer activity stops.
///
/// This owns all renderer-specific compression state. The terminal decides
/// when compression-relevant activity changes and performs the actual work;
/// the renderer only provides idle scheduling and avoids waiting for the
/// terminal lock.
const Compression = struct {
    const idle_interval = 250;
    const step_interval = 1;

    timer: xev.Timer,
    completion: xev.Completion = .{},
    reset_completion: xev.Completion = .{},
    activity: ?u64 = null,

    fn init() !Compression {
        return .{ .timer = try xev.Timer.init() };
    }

    fn deinit(self: *Compression) void {
        self.timer.deinit();
    }

    /// Start or postpone compression after a renderer wake.
    fn wake(self: *Compression, thread: *Thread) void {
        // If we have no compression then don't do anything.
        if (comptime !terminalpkg.compression_enabled) return;
        if (!thread.config.scrollback_compression) return;

        // PageList activity, rather than a generic renderer wake, restarts the
        // idle interval. In particular, the inspector wakes the renderer every
        // frame without changing terminal contents and must not starve this
        // timer indefinitely.
        if (thread.state.mutex.tryLock()) {
            defer thread.state.mutex.unlock(global.io());
            // A dormant terminal is torn down; the activity read below
            // would walk freed pages, so treat it as nothing-to-do (the
            // step path reaches the same conclusion through its own
            // guard).
            if (thread.state.dormant.load(.acquire)) return;
            const activity = thread.state.terminal.compressionActivity();
            if (self.activity == activity) return;
            self.activity = activity;
        } else if (self.completion.state() == .active) {
            // Contention doesn't prove that compression-relevant activity
            // changed. Keep an existing deadline so frequent inspector frames
            // cannot postpone compression forever. The timer rechecks both the
            // activity token and lock availability before doing any work.
            return;
        }

        // Contention may mean parsing is active. Scheduling is a harmless
        // false positive when no compression work is actually pending, but is
        // necessary when no timer is already active.
        self.schedule(thread, idle_interval);
    }

    /// Start the one-shot timer, or move its deadline if it is already active.
    fn schedule(self: *Compression, thread: *Thread, delay_ms: u64) void {
        self.timer.reset(
            &thread.loop,
            &self.completion,
            &self.reset_completion,
            delay_ms,
            Thread,
            thread,
            timerCallback,
        );
    }

    fn timerCallback(
        thread_: ?*Thread,
        _: *xev.Loop,
        _: *xev.Completion,
        result: xev.Timer.RunError!void,
    ) xev.CallbackAction {
        _ = result catch |err| switch (err) {
            error.Canceled => return .disarm,
            else => {
                log.warn("error in compression timer err={}", .{err});
                return .disarm;
            },
        };

        const thread = thread_ orelse return .disarm;
        const self = &thread.compression;

        if (self.step(thread)) |delay| self.schedule(thread, delay);
        return .disarm;
    }

    /// Try one bounded step without waiting for the terminal lock. The return
    /// value is the delay before another attempt, or null when work is done.
    fn step(self: *Compression, thread: *Thread) ?u64 {
        if (!thread.config.scrollback_compression) return null;

        const state = thread.state;
        if (!state.mutex.tryLock()) return idle_interval;
        defer state.mutex.unlock(global.io());

        // A dormant terminal is torn down; its pages exist only as the
        // IO thread's snapshot bytes. Nothing to compress, and the
        // terminal must not be read.
        if (state.dormant.load(.acquire)) return null;

        const activity = state.terminal.compressionActivity();
        if (self.activity != activity) {
            self.activity = activity;
            return idle_interval;
        }

        return switch (state.terminal.compress(.incremental)) {
            .pending => step_interval,
            .unsupported,
            .complete,
            => null,
        };
    }
};

/// Push a message to a renderer thread's mailbox, re-issuing the
/// thread's wake between attempts, and give up rather than parking.
///
/// This thread drains its mailbox in exactly two places: the `wakeup`
/// callback, and the safety-net timer that only runs while the surface
/// is hidden. On a visible surface the wake is therefore the only thing
/// that can free a slot, so a producer that blocks in the queue instead
/// of issuing it has removed the wake source it is waiting on. That is
/// what #1036 was: on the IOCP backend the Async wake can be lost in the
/// window between a completion firing and its re-arm, and a producer
/// that then parks in the not-full condition never wakes anyone --
/// observed as the UI thread hanging inside
/// `ghostty_surface_set_occlusion`.
///
/// A timed-out push re-issues the wake before trying again, so a single
/// lost notify costs one window rather than the process. Returns the
/// queue length after the push, or zero when the consumer never drained
/// at all -- the one genuinely fatal case (the renderer thread is gone),
/// which an unbounded push would have turned into a deadlock anyway.
///
/// This keeps the background budget, and `.persist` with it, even though
/// seven of its `Surface.zig` producers are on the UI thread, where a
/// minute-long wait is a window the user is told to kill. Both stay until
/// this give-up path frees what it gives up, because it frees nothing
/// today and a cheaper give-up would trade a freeze for a leak.
///
/// The list to cover before moving it, which is larger than round 3
/// claimed:
///
///   - `.change_config` (`Surface.updateConfig`), the largest: a
///     `renderer.Thread.DerivedConfig` and a renderer `DerivedConfig`
///     that itself owns allocations. `rendererpkg.Message.deinit`
///     ALREADY handles this variant, so one call would cover it -- but
///     read the ownership note below before adding that call.
///   - `.font_grid` (`Surface.setFontSize`), a refcount pair rather than
///     memory: a give-up leaks the new grid's ref and strands the old
///     key's. No `deinit` arm covers it.
///   - `.search_viewport_matches` and `.search_selected_match`
///     (`Surface.searchCallback_`), each carrying an arena moved into
///     the message. No `deinit` arm covers these either.
///
/// Ownership note, and why this is not a drive-by fix: adding
/// `msg.deinit()` below is a use-after-free until `Surface.updateConfig`
/// is restructured. Its three `errdefer`s -- `renderer_message.deinit`,
/// the `destroy` of `termio_config_ptr`, and that pointer's `deinit` --
/// are still live at the `try performAction` calls further down that
/// function, after its `pushRendererMailbox` has already handed the
/// message to this thread. Fix `updateConfig`'s ownership first.
///
/// All of this is pre-existing on merge base `218b8243db`, which had the
/// same constants and the same absent ownership handling hand-rolled in
/// `Surface.zig`. Moving the loop here did not introduce it.
pub fn pushMailbox(
    mailbox: *Mailbox,
    wakeup: *xev.Async,
    msg: rendererpkg.Message,
) Mailbox.Size {
    return pushMailboxBounded(
        mailbox,
        wakeup,
        msg,
        Mailbox.wake_retry_timeout_ns,
        Mailbox.wake_retry_attempts,
    );
}

fn pushMailboxBounded(
    mailbox: *Mailbox,
    wakeup: *xev.Async,
    msg: rendererpkg.Message,
    timeout_ns: u64,
    max_attempts: usize,
) Mailbox.Size {
    return mailbox.pushWake(
        global.io(),
        msg,
        wakeup,
        notifyWake,
        timeout_ns,
        max_attempts,
        // Deliberately NOT `.fail_fast`: this give-up path frees
        // nothing, so making give-ups cheaper here would make the
        // pre-existing leak above cheaper to reach. It stays `.persist`
        // until that path frees what it gives up.
        .persist,
    );
}

/// The `pushWake` wake for an `xev.Async`. A notify that fails means the
/// loop is gone, which the retry budget already gives up on.
fn notifyWake(wakeup: *xev.Async) void {
    wakeup.notify() catch {};
}

test "renderer mailbox push wakes the thread when it cannot land" {
    const testing = std.testing;
    const alloc = testing.allocator;
    const io = global.io();

    const mailbox = try Mailbox.create(alloc);
    defer mailbox.destroy(alloc);

    var wakeup: xev.Async = try .init();
    defer wakeup.deinit();

    var loop: xev.Loop = try .init(.{});
    defer loop.deinit();

    // Fill the mailbox and never drain it. That is what a renderer
    // asleep on a lost wake looks like from a producer's side.
    while (mailbox.push(io, .{ .reset_cursor_blink = {} }, .{ .instant = {} }) > 0) {}

    var woken: usize = 0;
    var c: xev.Completion = .{};
    wakeup.wait(&loop, &c, usize, &woken, (struct {
        fn callback(
            ud: ?*usize,
            _: *xev.Loop,
            _: *xev.Completion,
            r: xev.Async.WaitError!void,
        ) xev.CallbackAction {
            _ = r catch return .disarm;
            ud.?.* += 1;
            return .disarm;
        }
    }).callback);

    const Producer = struct {
        mailbox: *Mailbox,
        wakeup: *xev.Async,
        result: Mailbox.Size = 0,

        fn run(self: *@This()) void {
            // A tiny budget so the test finishes; production uses
            // the mailbox's wake_retry_* defaults.
            self.result = pushMailboxBounded(
                self.mailbox,
                self.wakeup,
                .{ .reset_cursor_blink = {} },
                1 * std.time.ns_per_ms,
                3,
            );
        }
    };

    var producer: Producer = .{ .mailbox = mailbox, .wakeup = &wakeup };
    const thread = try std.Thread.spawn(.{}, Producer.run, .{&producer});
    thread.join();

    // The message could not land, because nothing drained.
    try testing.expectEqual(@as(Mailbox.Size, 0), producer.result);

    // The invariant: a producer that cannot land its message must still
    // have woken the consumer, because that wake is the only thing that
    // makes the consumer drain and free the slot the producer wants. A
    // producer that parks in the queue instead withholds it, and the
    // hang is permanent while the surface is visible: the safety-net
    // drain only runs while it is hidden.
    const start: std.Io.Timestamp = .now(io, .awake);
    while (woken == 0) {
        try loop.run(.no_wait);
        const now: std.Io.Timestamp = .now(io, .awake);
        if (start.durationTo(now).toMilliseconds() > 2000) break;
        std.Thread.yield() catch {};
    }
    try testing.expect(woken > 0);

    // Nothing was dropped from the queue on the way.
    try testing.expect(mailbox.pop(io) != null);
}

// The hidden-drain timer must never starve the loop: a timer callback that
// returns `.rearm` on the IOCP backend is re-inserted at its elapsed
// deadline and fires on every tick before the loop waits on the port, so
// a posted async (our `stop`) is never delivered and `Surface.deinit`
// joins forever (2026-09-08 close hang). Timers re-run and `.disarm`;
// only asyncs `.rearm`.
//
// The fire cap below is not a "give up and pass anyway" valve: hitting it
// sets `starved`, which stops the loop immediately and fails the test on
// its own assertion. A starved run can never limp past the cap, get
// lucky on a later tick, and pass `stop_seen` for the wrong reason.
test "hidden drain idiom: a timer that re-runs itself lets a posted async through" {
    const testing = std.testing;

    var loop = try xev.Loop.init(.{});
    defer loop.deinit();

    var timer = try xev.Timer.init();
    defer timer.deinit();
    var timer_c: xev.Completion = .{};

    var stop = try xev.Async.init();
    defer stop.deinit();
    var stop_c: xev.Completion = .{};

    const State = struct {
        // A healthy loop sees `stop` on the timer's first or second
        // fire (see the `timer_fires < 5` assertion below); this cap is
        // only a hard backstop against a genuinely starved loop running
        // forever, and it fails the test rather than rescuing it.
        const starve_fire_cap: u32 = 8;

        timer_fires: u32 = 0,
        stop_seen: bool = false,
        starved: bool = false,
        loop: *xev.Loop,
        timer: *xev.Timer,
        timer_c: *xev.Completion,

        fn onTimer(
            self_: ?*@This(),
            _: *xev.Loop,
            _: *xev.Completion,
            r: xev.Timer.RunError!void,
        ) xev.CallbackAction {
            _ = r catch return .disarm;
            const self = self_.?;
            self.timer_fires += 1;
            if (self.stop_seen) return .disarm;
            if (self.timer_fires > starve_fire_cap) {
                // The timer keeps winning the ready queue and the loop
                // never reaches the port wait: this is the starvation
                // under test. Fail outright instead of disarming and
                // letting a later, now-uncontested tick observe the
                // already-posted `stop` and pass the assertions below
                // for the wrong reason.
                self.starved = true;
                return .disarm;
            }
            // The idiom under test: same as hiddenDrainCallback's tail.
            return hiddenDrainRearm(self.loop, self.timer, self.timer_c, @This(), self, onTimer);
        }

        fn onStop(
            self_: ?*@This(),
            _: *xev.Loop,
            _: *xev.Completion,
            r: xev.Async.WaitError!void,
        ) xev.CallbackAction {
            _ = r catch return .disarm;
            self_.?.stop_seen = true;
            return .disarm;
        }
    };

    var st: State = .{ .loop = &loop, .timer = &timer, .timer_c = &timer_c };
    timer.run(&loop, &timer_c, 0, State, &st, State.onTimer);
    stop.wait(&loop, &stop_c, State, &st, State.onStop);
    try stop.notify();

    // Stop ticking the instant either outcome is decided; a starved run
    // must not get extra ticks in which to recover.
    var ticks: u32 = 0;
    while (!st.stop_seen and !st.starved and ticks < 20) : (ticks += 1) {
        try loop.run(.once);
    }
    try testing.expect(!st.starved);
    try testing.expect(st.stop_seen);
    // Anything close to the starvation cap means the port wait was
    // mostly starved even though it eventually got lucky.
    try testing.expect(st.timer_fires < 5);
}

// The idiom test above drives hiddenDrainRearm directly, so it cannot
// catch a revert of hiddenDrainCallback's callsite (back to a bare
// `return .rearm;`) while the helper itself stays correct. This census
// closes that gap: it scans every xev.Timer callback's own source text
// for the literal string that reintroduces the starvation bug.
test "no xev.Timer callback in this file returns .rearm" {
    const src = @embedFile("Thread.zig");
    const names = [_][]const u8{ "fn hiddenDrainCallback(", "fn cursorTimerCallback(", "fn animationTimerCallback(" };
    for (names) |name| {
        const start = std.mem.indexOf(u8, src, name) orelse return error.CallbackNotFound;
        // The body ends at the next "\n}\n" at column 0.
        const end = start + (std.mem.indexOf(u8, src[start..], "\n}\n") orelse return error.BodyNotClosed);
        try std.testing.expect(std.mem.indexOf(u8, src[start..end], "return .rearm") == null);
    }
}
