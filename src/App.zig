//! App is the primary GUI application for ghostty. This builds the window,
//! sets up the renderer, etc. The primary run loop is started by calling
//! the "run" function.
const App = @This();

const std = @import("std");
const builtin = @import("builtin");
const assert = @import("quirks.zig").inlineAssert;
const Allocator = std.mem.Allocator;
const apprt = @import("apprt.zig");
const Surface = @import("Surface.zig");
const input = @import("input.zig");
const configpkg = @import("config.zig");
const Config = configpkg.Config;
const BlockingQueue = @import("datastruct/main.zig").BlockingQueue;
const renderer = @import("renderer.zig");
const font = @import("font/main.zig");
const global = @import("global.zig");

const log = std.log.scoped(.app);

const SurfaceList = std.ArrayListUnmanaged(*apprt.Surface);

/// General purpose allocator
alloc: Allocator,

/// The list of surfaces that are currently active.
surfaces: SurfaceList,

/// This is true if the app that Ghostty is in is focused. This may
/// mean that no surfaces (terminals) are focused but the app is still
/// focused, i.e. may an about window. On macOS, this concept is known
/// as the "active" app while focused windows are known as the
/// "main" window.
///
/// This is used to determine if keyboard shortcuts that are non-global
/// should be processed. If the app is not focused, then we don't want
/// to process keyboard shortcuts that are not global.
///
/// This defaults to true since we assume that the app is focused when
/// Ghostty is initialized but a well behaved apprt should call
/// focusEvent to set this to the correct value right away.
focused: bool = true,

/// The last focused surface. This surface may not be valid;
/// you must always call hasSurface to validate it.
focused_surface: ?*Surface = null,

/// The mailbox that can be used to send this thread messages. Note
/// this is a blocking queue so if it is full you will get errors (or block).
mailbox: Mailbox.Queue,

/// The set of font GroupCache instances shared by surfaces with the
/// same font configuration.
font_grid_set: font.SharedGridSet,

// Used to rate limit desktop notifications. Some platforms (notably macOS) will
// run out of resources if desktop notifications are sent too fast and the OS
// will kill Ghostty.
last_notification_time: ?std.Io.Timestamp = null,
last_notification_digest: u64 = 0,

/// The conditional state of the configuration. See the equivalent field
/// in the Surface struct for more information. In this case, this applies
/// to the app-level config and as a default for new surfaces.
config_conditional_state: configpkg.ConditionalState,

/// Set to false once we've created at least one surface. This
/// never goes true again. This can be used by surfaces to determine
/// if they are the first surface.
first: bool = true,

pub const CreateError = Allocator.Error || font.SharedGridSet.InitError;

/// Create a new app instance. This returns a stable pointer to the app
/// instance which is required for callbacks.
pub fn create(alloc: Allocator) CreateError!*App {
    var app = try alloc.create(App);
    errdefer alloc.destroy(app);
    try app.init(alloc);

    // If font discovery supports warmup, then we call it. Some font
    // mechanisms (e.g. CoreText) have a multi-millisecond one-time cost
    // on startup.
    if (comptime @hasDecl(font.Discover, "warmup")) {
        if (std.Thread.spawn(
            .{},
            font.Discover.warmup,
            .{},
        )) |thr| thr.detach() else |err| {
            log.warn("font warmup thread spawn failed err={}", .{err});
        }
    }

    // Same for the renderer's graphics API (e.g. Metal), which pays
    // one-time framework initialization costs on first use.
    if (comptime @hasDecl(renderer.Renderer.API, "warmup")) {
        if (std.Thread.spawn(
            .{},
            renderer.Renderer.API.warmup,
            .{},
        )) |thr| thr.detach() else |err| {
            log.warn("renderer warmup thread spawn failed err={}", .{err});
        }
    }

    return app;
}

/// Initialize the main app instance. This creates the main window, sets
/// up the renderer state, compiles the shaders, etc. This is the primary
/// "startup" logic.
///
/// After calling this function, well behaved apprts should then call
/// `focusEvent` to set the initial focus state of the app.
pub fn init(
    self: *App,
    alloc: Allocator,
) CreateError!void {
    var font_grid_set = try font.SharedGridSet.init(alloc);
    errdefer font_grid_set.deinit();

    self.* = .{
        .alloc = alloc,
        .surfaces = .empty,
        .mailbox = .{},
        .font_grid_set = font_grid_set,
        .config_conditional_state = .{},
    };
}

pub fn deinit(self: *App) void {
    // Clean up all our surfaces
    for (self.surfaces.items) |surface| surface.deinit();
    self.surfaces.deinit(self.alloc);

    // Clean up our font group cache
    // We should have zero items in the grid set at this point because
    // destroy only gets called when the app is shutting down and this
    // should gracefully close all surfaces.
    assert(self.font_grid_set.count() == 0);
    self.font_grid_set.deinit();
}

pub fn destroy(self: *App) void {
    // Deinitialize the app
    self.deinit();

    // Free the app memory
    self.alloc.destroy(self);
}

/// Tick ticks the app loop. This will drain our mailbox and process those
/// events. This should be called by the application runtime on every loop
/// tick.
pub fn tick(self: *App, rt_app: *apprt.App) !void {
    // Drain our mailbox
    try self.drainMailbox(rt_app);
}

/// Update the configuration associated with the app. This can only be
/// called from the main thread. The caller owns the config memory. The
/// memory can be freed immediately when this returns.
pub fn updateConfig(self: *App, rt_app: *apprt.App, config: *const Config) !void {
    // Go through and update all of the surface configurations.
    for (self.surfaces.items) |surface| {
        try surface.core().handleMessage(.{ .change_config = config });
    }

    // Apply our conditional state. If we fail to apply the conditional state
    // then we log and attempt to move forward with the old config.
    // We only apply this to the app-level config because the surface
    // config applies its own conditional state.
    var applied_: ?configpkg.Config = config.changeConditionalState(
        self.config_conditional_state,
    ) catch |err| err: {
        log.warn("failed to apply conditional state to config err={}", .{err});
        break :err null;
    };
    defer if (applied_) |*c| c.deinit();
    const applied: *const configpkg.Config = if (applied_) |*c| c else config;

    // Notify the apprt that the app has changed configuration.
    _ = try rt_app.performAction(
        .app,
        .config_change,
        .{ .config = applied },
    );
}

/// Add an initialized surface. This is really only for the runtime
/// implementations to call and should NOT be called by general app users.
/// The surface must be from the pool.
pub fn addSurface(
    self: *App,
    rt_surface: *apprt.Surface,
) Allocator.Error!void {
    try self.surfaces.append(self.alloc, rt_surface);

    // Since we have non-zero surfaces, we can cancel the quit timer.
    // It is up to the apprt if there is a quit timer at all and if it
    // should be canceled.
    _ = rt_surface.rtApp().performAction(
        .app,
        .quit_timer,
        .stop,
    ) catch |err| {
        log.warn("error stopping quit timer err={}", .{err});
    };
}

/// Delete the surface from the known surface list. This will NOT call the
/// destructor or free the memory.
pub fn deleteSurface(self: *App, rt_surface: *apprt.Surface) void {
    // If this surface is the focused surface then we need to clear it.
    // There was a bug where we relied on hasSurface to return false and
    // just let focused surface be but the allocator was reusing addresses
    // after free and giving false positives, so we must clear it.
    if (self.focused_surface) |focused| {
        if (focused == rt_surface.core()) {
            self.focused_surface = null;
        }
    }

    var i: usize = 0;
    while (i < self.surfaces.items.len) {
        if (self.surfaces.items[i] == rt_surface) {
            _ = self.surfaces.swapRemove(i);
            continue;
        }

        i += 1;
    }

    // If we have no surfaces, we can start the quit timer. It is up to the
    // apprt to determine if this is necessary.
    if (self.surfaces.items.len == 0) _ = rt_surface.rtApp().performAction(
        .app,
        .quit_timer,
        .start,
    ) catch |err| {
        log.warn("error starting quit timer err={}", .{err});
    };
}

/// The last focused surface. This is only valid while on the main thread
/// before tick is called.
pub fn focusedSurface(self: *const App) ?*Surface {
    const surface = self.focused_surface orelse return null;
    if (!self.hasSurface(surface)) return null;
    return surface;
}

/// Returns true if confirmation is needed to quit the app. It is up to
/// the apprt to call this.
pub fn needsConfirmQuit(self: *const App) bool {
    for (self.surfaces.items) |v| {
        if (v.core().needsConfirmQuit()) return true;
    }

    return false;
}

/// Drain the mailbox.
fn drainMailbox(self: *App, rt_app: *apprt.App) !void {
    while (self.mailbox.pop(global.io())) |message| {
        if (comptime std.log.logEnabled(.debug, .app)) {
            switch (message) {
                // these tend to be way too verbose for normal debugging
                .redraw_surface => {},
                else => log.debug("mailbox message={t}", .{message}),
            }
        }
        switch (message) {
            .open_config => |v| try self.performAction(
                rt_app,
                .{
                    .open_config = switch (v) {
                        .os_open => .os_open,
                        .new_window => .new_window,
                    },
                },
            ),
            .new_window => |msg| try self.newWindow(rt_app, msg),
            .close => |surface| self.closeSurface(surface),
            .surface_message => |msg| try self.surfaceMessage(msg.surface, msg.message),
            .redraw_surface => |surface| try self.redrawSurface(rt_app, surface),

            // If we're quitting, then we set the quit flag and stop
            // draining the mailbox immediately. This lets us defer
            // mailbox processing to the next tick so that the apprt
            // can try to quit as quickly as possible.
            .quit => {
                log.info("quit message received, short circuiting mailbox drain", .{});
                try self.performAction(rt_app, .quit);
                return;
            },
        }
    }
}

pub fn closeSurface(self: *App, surface: *Surface) void {
    if (!self.hasSurface(surface)) return;
    surface.close();
}

pub fn focusSurface(self: *App, surface: *Surface) void {
    if (!self.hasSurface(surface)) return;
    self.focused_surface = surface;
}

fn redrawSurface(
    self: *App,
    rt_app: *apprt.App,
    surface: *apprt.Surface,
) !void {
    if (!self.hasRtSurface(surface)) return;

    _ = try rt_app.performAction(
        .{ .surface = surface.core() },
        .render,
        {},
    );
}

/// Create a new window
pub fn newWindow(self: *App, rt_app: *apprt.App, msg: Message.NewWindow) !void {
    const target: apprt.Target = target: {
        const parent = msg.parent orelse break :target .app;
        if (self.hasSurface(parent)) break :target .{ .surface = parent };
        break :target .app;
    };

    _ = try rt_app.performAction(
        target,
        .new_window,
        {},
    );
}

/// Handle an app-level focus event. This should be called whenever
/// the focus state of the entire app containing Ghostty changes.
/// This is separate from surface focus events. See the `focused`
/// field for more information.
pub fn focusEvent(self: *App, focused: bool) void {
    // Prevent redundant focus events
    if (self.focused == focused) return;

    log.debug("focus event focused={}", .{focused});
    self.focused = focused;
}

/// Handle a key event at the app-scope. If this key event is used,
/// this will return true and the caller shouldn't continue processing
/// the event. If the event is not used, this will return false.
///
/// If the app currently has focus then all key events are processed.
/// If the app does not have focus then only global key events are
/// processed.
pub fn keyEvent(
    self: *App,
    rt_app: *apprt.App,
    event: input.KeyEvent,
) bool {
    switch (event.action) {
        // We don't care about key release events.
        .release => return false,

        // Continue processing key press events.
        .press, .repeat => {},
    }

    // Get the keybind entry for this event. We don't support key sequences
    // so we can look directly in the top-level set.
    const entry = rt_app.config.keybind.set.getEvent(event) orelse return false;
    const leaf: input.Binding.Set.GenericLeaf = switch (entry.value_ptr.*) {
        // Sequences aren't supported. Our configuration parser verifies
        // this for global keybinds but we may still get an entry for
        // a non-global keybind.
        .leader => return false,

        // Leaf entries are good
        inline .leaf, .leaf_chained => |leaf| leaf.generic(),
    };
    const actions: []const input.Binding.Action = leaf.actionsSlice();
    assert(actions.len > 0);

    // If we aren't focused, then we only process global keybinds.
    if (!self.focused and !leaf.flags.global) return false;

    // Global keybinds are done using performAll so that they
    // can target all surfaces too.
    if (leaf.flags.global) {
        self.performAllChainedAction(rt_app, actions);
        return true;
    }

    // Must be focused to process non-global keybinds
    assert(self.focused);
    assert(!leaf.flags.global);

    // If we are focused, then we process keybinds only if they are
    // app-scoped. Otherwise, we do nothing. Surface-scoped should
    // be processed by Surface.keyEvent. For chained actions, all
    // actions must be app-scoped.
    for (actions) |action| if (action.scoped(.app) == null) return false;
    for (actions) |action| {
        self.performAction(
            rt_app,
            action.scoped(.app).?,
        ) catch |err| {
            log.warn("error performing app keybind action action={s} err={}", .{
                @tagName(action),
                err,
            });
        };
    }

    return true;
}

/// Call to notify Ghostty that the color scheme for the app has changed.
/// "Color scheme" in this case refers to system themes such as "light/dark".
pub fn colorSchemeEvent(
    self: *App,
    rt_app: *apprt.App,
    scheme: apprt.ColorScheme,
) !void {
    const new_scheme: configpkg.ConditionalState.Theme = switch (scheme) {
        .light => .light,
        .dark => .dark,
    };

    // If our scheme didn't change, then we don't do anything.
    if (self.config_conditional_state.theme == new_scheme) return;

    // Setup our conditional state which has the current color theme.
    self.config_conditional_state.theme = new_scheme;

    // Request our configuration be reloaded because the new scheme may
    // impact the colors of the app.
    _ = try rt_app.performAction(
        .app,
        .reload_config,
        .{ .soft = true },
    );
}

/// Perform a binding action. This only accepts actions that are scoped
/// to the app. Callers can use performAllAction to perform any action
/// and any non-app-scoped actions will be performed on all surfaces.
pub fn performAction(
    self: *App,
    rt_app: *apprt.App,
    action: input.Binding.Action.Scoped(.app),
) !void {
    switch (action) {
        .unbind => unreachable,
        .ignore => {},
        .quit => _ = try rt_app.performAction(.app, .quit, {}),
        .new_window => _ = try self.newWindow(rt_app, .{ .parent = null }),
        .open_config => |v| _ = try rt_app.performAction(
            .app,
            .open_config,
            switch (v) {
                .os_open => .os_open,
                .new_window => .new_window,
            },
        ),
        .reload_config => _ = try rt_app.performAction(.app, .reload_config, .{}),
        .close_all_windows => _ = try rt_app.performAction(.app, .close_all_windows, {}),
        .toggle_quick_terminal => _ = try rt_app.performAction(.app, .toggle_quick_terminal, {}),
        .toggle_visibility => _ = try rt_app.performAction(.app, .toggle_visibility, {}),
        .check_for_updates => _ = try rt_app.performAction(.app, .check_for_updates, {}),
        .show_gtk_inspector => _ = try rt_app.performAction(.app, .show_gtk_inspector, {}),
        .undo => _ = try rt_app.performAction(.app, .undo, {}),

        .redo => _ = try rt_app.performAction(.app, .redo, {}),
    }
}

/// Performs a chained action. We will continue executing each action
/// even if there is a failure in a prior action.
pub fn performAllChainedAction(
    self: *App,
    rt_app: *apprt.App,
    actions: []const input.Binding.Action,
) void {
    for (actions) |action| {
        self.performAllAction(rt_app, action) catch |err| {
            log.warn("error performing chained action action={s} err={}", .{
                @tagName(action),
                err,
            });
        };

        // This slice is owned by the keybind set of the surface the
        // binding came from, and that surface is now gone, so there is
        // nothing left to read.
        if (action.closesSurface()) break;
    }
}

/// Perform an app-wide binding action. If the action is surface-specific
/// then it will be performed on all surfaces. To perform only app-scoped
/// actions, use performAction.
pub fn performAllAction(
    self: *App,
    rt_app: *apprt.App,
    action: input.Binding.Action,
) !void {
    switch (action.scope()) {
        // App-scoped actions are handled by the app so that they aren't
        // repeated for each surface (since each surface forwards
        // app-scoped actions back up).
        .app => try self.performAction(
            rt_app,
            action.scoped(.app).?, // asserted through the scope match
        ),

        // Surface-scoped actions are performed on all surfaces. Errors
        // are logged but processing continues.
        .surface => for (self.surfaces.items) |surface| {
            _ = surface.core().performBindingAction(action) catch |err| {
                log.warn("error performing binding action on surface id={x} err={}", .{
                    surface.core().id,
                    err,
                });
            };
        },
    }
}

/// Handle a window message
fn surfaceMessage(self: *App, surface: *Surface, msg: apprt.surface.Message) !void {
    // We want to ensure our window is still active. Window messages
    // are quite rare and we normally don't have many windows so we do
    // a simple linear search here.
    if (self.hasSurface(surface)) {
        try surface.handleMessage(msg);
    }

    // Window was not found, it probably quit before we handled the message.
    // Not a problem.
}

fn hasSurface(self: *const App, surface: *const Surface) bool {
    for (self.surfaces.items) |v| {
        if (v.core() == surface) return true;
    }

    return false;
}

/// Search for a surface by a 64 bit unique ID.
pub fn findSurfaceByID(self: *const App, id: u64) ?*Surface {
    for (self.surfaces.items) |v| {
        const surface: *Surface = v.core();
        if (surface.id == id) return surface;
    }

    return null;
}

fn hasRtSurface(self: *const App, surface: *apprt.Surface) bool {
    for (self.surfaces.items) |v| {
        if (v == surface) return true;
    }

    return false;
}

/// The message types that can be sent to the app thread.
pub const Message = union(enum) {
    // Open the configuration file
    open_config: OpenConfig,

    /// Create a new terminal window.
    new_window: NewWindow,

    /// Close a surface. This notifies the runtime that a surface
    /// should close.
    close: *Surface,

    /// Quit
    quit: void,

    /// A message for a specific surface.
    surface_message: struct {
        surface: *Surface,
        message: apprt.surface.Message,
    },

    /// Redraw a surface. This only has an effect for runtimes that
    /// use single-threaded draws. To redraw a surface for all runtimes,
    /// wake up the renderer thread. The renderer thread will send this
    /// message if it needs to.
    redraw_surface: *apprt.Surface,

    /// Release anything the message owns. `Mailbox.push` calls this when
    /// a full queue makes it give the message up.
    ///
    /// Exhaustive on purpose: a variant that starts owning memory has to
    /// be decided here rather than compiling into a silent leak on every
    /// drop.
    pub fn deinit(self: Message) void {
        switch (self) {
            .surface_message => |v| v.message.deinit(),

            .open_config,
            .new_window,
            .close,
            .quit,
            .redraw_surface,
            => {},
        }
    }

    const NewWindow = struct {
        /// The parent surface
        parent: ?*Surface = null,
    };

    pub const OpenConfig = enum {
        /// Open the config in the OS default editor.
        os_open,
        /// Open the config in a new window using $EDITOR or $VISUAL
        new_window,
    };
};

/// Mailbox is the way that other threads send the app thread messages.
pub const Mailbox = struct {
    /// The type used for sending messages to the app thread.
    pub const Queue = BlockingQueue(Message, 64);

    rt_app: *apprt.App,
    mailbox: *Queue,

    /// Send a message to the surface. `.forever` takes ownership of
    /// `msg`: it is either queued or released.
    ///
    /// `.forever` does not park here. This queue is drained by `App.tick`
    /// alone and the apprt calls that from its wakeup handler -- on the
    /// embedded runtime that handler is the only caller there is -- so a
    /// producer waiting inside the queue is waiting for a drain its own
    /// wake has to start. It waits in windows instead and re-issues the
    /// wake after each one.
    ///
    /// The budget is the background pair, roughly a minute, because every
    /// producer that reaches this path is a background thread: the search
    /// thread, the termio writer and the pty reader. The cost of the wait
    /// is a stalled search or a stalled child, not a frozen window.
    ///
    /// It is a budget and not an unbounded wait because a wedged consumer
    /// is not the same thing as a dead one. The app thread can be alive
    /// and itself blocked pushing to another full mailbox, and then a
    /// producer that never gives up is one half of a deadlock that only a
    /// kill recovers from. Giving up costs one message; `Message.deinit`
    /// is what makes that cost bounded rather than a leak.
    ///
    /// The re-issued wake has no test of its own. Under
    /// `-Dapp-runtime=none` -- the only runtime that compiles here --
    /// `apprt.none.App.wakeup` is an empty body, so a fixed and an
    /// unfixed push are observationally identical and reverting this
    /// produces no failure. The loop is tested in
    /// `BlockingQueue.pushWake`; the give-up path is tested below.
    ///
    /// `push` is the streaming policy: it fails fast while the queue is
    /// latched wedged. Use `pushRequired` for the rare message whose
    /// loss nothing re-derives.
    pub fn push(self: Mailbox, msg: Message, timeout: Queue.Timeout) Queue.Size {
        const result = switch (timeout) {
            .forever => self.pushBounded(
                msg,
                Queue.wake_retry_timeout_ns,
                Queue.wake_retry_attempts,
                // The search thread emits match batches per tick and the
                // pty reader emits per OSC, so this queue's producers do
                // arrive in streams. Spending the budget once per stall
                // rather than once per message is what keeps a wedged app
                // thread from stalling them for as long as it is wedged.
                .fail_fast,
            ),

            .instant, .ns => self.mailbox.push(global.io(), msg, timeout),
        };

        // Wake up our app loop
        self.rt_app.wakeup();

        return result;
    }

    /// Push a one-shot message that nothing re-derives if it is lost.
    ///
    /// The latch that `push` respects turns a *delayed* delivery into a
    /// *permanent* loss for this class of message: an app thread wedged
    /// past one budget and then recovering would still have delivered it
    /// on the next attempt, and `.fail_fast` throws that attempt away.
    /// `.persist` spends the full budget instead, which is what the queue
    /// did for every message before the latch existed.
    ///
    /// This is deliberately NOT the policy for `password_input`, the
    /// other one-shot on this queue: that one is re-derived by the
    /// termios poll every 200ms until the surface agrees, so a give-up
    /// there costs one poll rather than the state. `.child_exited` has no
    /// such poll -- `processExitCommon` runs once per surface lifetime --
    /// so the budget is the only thing standing between a wedge and a tab
    /// that never learns its child is gone.
    ///
    /// Where this stands against the merge base: better, not equal. On
    /// `218b8243db` this function's ancestor waited on the not-full
    /// condition with no timeout at all, and only a `pop` can signal
    /// that condition, so a producer facing an app thread that had
    /// stopped draining parked here forever. `.persist` **bounds** that
    /// hang at one background budget; it does not restore one.
    ///
    /// The cost, stated plainly, and it is worse on Windows than on
    /// POSIX. On POSIX the caller is `Exec.processExit`, an `xev.Process`
    /// wait callback on the termio writer thread, so a wedged app thread
    /// parks that thread for one background budget per child exit.
    ///
    /// On Windows -- which is what this fork ships -- the caller is
    /// `Exec.winProcessWaitThread`, a dedicated `WaitForSingleObject`
    /// thread, and at teardown the wait reaches the UI thread through two
    /// joins: `Surface.deinit` joins the io thread, and `Exec.threadExit`
    /// joins the wait thread. In that path the stall is **guaranteed, not
    /// conditional**: the app thread is inside `Surface.deinit`, so it is
    /// by construction not draining this mailbox, and no separate "wedged
    /// app thread" precondition is needed -- a full mailbox at close time
    /// is enough. Worse, that is the push `Exec.threadExit` itself calls
    /// harmless: `App.surfaceMessage` gates on `hasSurface` and the
    /// surface is being destroyed, so the budget is spent delivering a
    /// message guaranteed to be discarded.
    ///
    /// That path is worth closing and is filed as a follow-up
    /// (suppressing the teardown push in `Exec`), not fixed here: the
    /// flag would be written on the io thread and read on the wait
    /// thread, so it needs an ordering argument rather than an
    /// assignment, and it can only narrow the window -- the wait thread
    /// can already be inside `processExitCommon` when the flag is set.
    ///
    /// It is bounded either way: past the budget the message is still
    /// given up and freed.
    pub fn pushRequired(self: Mailbox, msg: Message) Queue.Size {
        return self.pushRequiredBounded(
            msg,
            Queue.wake_retry_timeout_ns,
            Queue.wake_retry_attempts,
        );
    }

    /// `pushRequired` with the budget as a parameter, so a test can make
    /// the wait short enough to observe. The wedge token is the whole
    /// point of the function and is what a mutation should revert.
    fn pushRequiredBounded(
        self: Mailbox,
        msg: Message,
        timeout_ns: u64,
        max_attempts: usize,
    ) Queue.Size {
        const result = self.pushBounded(msg, timeout_ns, max_attempts, .persist);

        // Wake up our app loop
        self.rt_app.wakeup();

        return result;
    }

    fn pushBounded(
        self: Mailbox,
        msg: Message,
        timeout_ns: u64,
        max_attempts: usize,
        wedge: Queue.Wedge,
    ) Queue.Size {
        const size = self.mailbox.pushWake(
            global.io(),
            msg,
            self.rt_app,
            wakeApp,
            timeout_ns,
            max_attempts,
            wedge,
        );
        if (size == 0) {
            log.warn("app mailbox full, message dropped", .{});
            msg.deinit();
        }

        return size;
    }

    fn wakeApp(rt_app: *apprt.App) void {
        rt_app.wakeup();
    }
};

test "app mailbox push frees the message it has to give up" {
    const testing = std.testing;
    const build_config = @import("build_config.zig");

    // The only runtime whose `wakeup` ignores its receiver, which is what
    // lets this test hand it an undefined one.
    if (build_config.app_runtime != .none) return error.SkipZigTest;

    const alloc = testing.allocator;
    const io = global.io();

    const queue = try Mailbox.Queue.create(alloc);
    defer queue.destroy(alloc);

    var rt_app: apprt.App = undefined;
    const mailbox: Mailbox = .{ .rt_app = &rt_app, .mailbox = queue };

    // Fill the queue and never drain it. An app thread that is alive but
    // blocked on another full mailbox looks exactly like this from a
    // producer's side, and that is the state an unbounded wait here turns
    // into a deadlock.
    while (queue.push(io, .{ .quit = {} }, .{ .instant = {} }) > 0) {}

    // A pwd longer than the inline capacity is heap allocated, so
    // std.testing.allocator fails this test if the give-up path leaks it.
    const pwd = "/" ++ ("d" ** 400);
    const req = try apprt.surface.Message.WriteReq.init(alloc, @as([]const u8, pwd));
    try testing.expect(req == .alloc);

    // A tiny budget so the test finishes; production uses the queue's
    // wake_retry_* defaults.
    const size = mailbox.pushBounded(.{ .surface_message = .{
        .surface = undefined,
        .message = .{ .pwd_change = req },
    } }, 1 * std.time.ns_per_ms, 3, .fail_fast);

    try testing.expectEqual(@as(Mailbox.Queue.Size, 0), size);
}

test "app mailbox pushRequired ignores a latch that push respects" {
    const testing = std.testing;
    const build_config = @import("build_config.zig");

    // The only runtime whose `wakeup` ignores its receiver, which is what
    // lets this test hand it an undefined one.
    if (build_config.app_runtime != .none) return error.SkipZigTest;

    const alloc = testing.allocator;
    const io = global.io();

    const queue = try Mailbox.Queue.create(alloc);
    defer queue.destroy(alloc);

    var rt_app: apprt.App = undefined;
    const mailbox: Mailbox = .{ .rt_app = &rt_app, .mailbox = queue };

    // A wedged app thread: full, and nothing drains it.
    while (queue.push(io, .{ .quit = {} }, .{ .instant = {} }) > 0) {}

    // Latch the queue the way a stream of search results would.
    const window_ns = 20 * std.time.ns_per_ms;
    try testing.expectEqual(@as(Mailbox.Queue.Size, 0), mailbox.pushBounded(
        .{ .quit = {} },
        window_ns,
        3,
        .fail_fast,
    ));
    try testing.expect(queue.wedged.load(.acquire));

    // `.child_exited` is the message this exists for: one shot, nothing
    // re-derives it. It must still spend its budget against the latch,
    // because an app thread that recovers inside that budget delivers it.
    // Measured rather than asserted on the token, so swapping `.persist`
    // for `.fail_fast` in `pushRequiredBounded` fails here: three 20ms
    // windows cannot complete in under 30ms, and a fail-fast give-up is
    // a single non-blocking attempt.
    const start = std.Io.Timestamp.now(io, .awake);
    try testing.expectEqual(@as(Mailbox.Queue.Size, 0), mailbox.pushRequiredBounded(
        .{ .surface_message = .{
            .surface = undefined,
            .message = .{ .child_exited = .{ .exit_code = 0, .runtime_ms = 0 } },
        } },
        window_ns,
        3,
    ));

    // One-directional: load on the build machine can only lengthen this,
    // never shorten it, so a busy host cannot make this flaky.
    try testing.expect(start.untilNow(io, .awake).toMilliseconds() >= 30);
}

// Wasm API.
pub const Wasm = if (!builtin.target.isWasm()) struct {} else struct {
    const wasm = @import("os/wasm.zig");
    const alloc = wasm.alloc;

    // export fn app_new(config: *Config) ?*App {
    //     return app_new_(config) catch |err| { log.err("error initializing app err={}", .{err});
    //         return null;
    //     };
    // }
    //
    // fn app_new_(config: *Config) !*App {
    //     const app = try App.create(alloc, config);
    //     errdefer app.destroy();
    //
    //     const result = try alloc.create(App);
    //     result.* = app;
    //     return result;
    // }
    //
    // export fn app_free(ptr: ?*App) void {
    //     if (ptr) |v| {
    //         v.destroy();
    //         alloc.destroy(v);
    //     }
    // }
};
