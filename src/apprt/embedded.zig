//! Application runtime for the embedded version of Ghostty. The embedded
//! version is when Ghostty is embedded within a parent host application,
//! rather than owning the application lifecycle itself. This is used for
//! example for the macOS build of Ghostty so that we can use a native
//! Swift+XCode-based application.

const std = @import("std");
const builtin = @import("builtin");
const assert = @import("../quirks.zig").inlineAssert;
const Allocator = std.mem.Allocator;
const objc = @import("objc");
const apprt = @import("../apprt.zig");
const font = @import("../font/main.zig");
const input = @import("../input.zig");
const internal_os = @import("../os/main.zig");
const renderer = @import("../renderer.zig");
const terminal = @import("../terminal/main.zig");
const CoreApp = @import("../App.zig");
const CoreInspector = @import("../inspector/main.zig").Inspector;
const CoreSurface = @import("../Surface.zig");

/// DirectX12 imgui backend for the inspector (Windows only). On other
/// platforms this is an empty namespace so the Inspector still compiles.
const dx12_imgui = if (builtin.os.tag == .windows)
    @import("../renderer/directx12/imgui.zig")
else
    struct {};
const configpkg = @import("../config.zig");
const Config = configpkg.Config;
const String = @import("../main_c.zig").String;

const log = std.log.scoped(.embedded_window);

pub const resourcesDir = internal_os.resourcesDir;

pub const App = struct {
    /// Because we only expect the embedding API to be used in embedded
    /// environments, the options are extern so that we can expose it
    /// directly to a C callconv and not pay for any translation costs.
    ///
    /// C type: ghostty_runtime_config_s
    pub const Options = extern struct {
        /// These are just aliases to make the function signatures below
        /// more obvious what values will be sent.
        const AppUD = ?*anyopaque;
        const SurfaceUD = ?*anyopaque;

        /// Userdata that is passed to all the callbacks.
        userdata: AppUD = null,

        /// True if the selection clipboard is supported.
        supports_selection_clipboard: bool = false,

        /// Callback called to wakeup the event loop. This should trigger
        /// a full tick of the app loop.
        wakeup: *const fn (AppUD) callconv(.c) void,

        /// Callback called to handle an action.
        action: *const fn (*App, apprt.Target.C, apprt.Action.C) callconv(.c) bool,

        /// Read the clipboard value. Returns true if the clipboard request
        /// was started and complete_clipboard_request may be called with the
        /// given state pointer. Returns false if the clipboard request couldn't
        /// be started (such as when no text is available for a paste request).
        read_clipboard: *const fn (SurfaceUD, c_int, *apprt.ClipboardRequest) callconv(.c) bool,

        /// This may be called after a read clipboard call to request
        /// confirmation that the clipboard value is safe to read. The embedder
        /// must call complete_clipboard_request with the given request.
        confirm_read_clipboard: *const fn (
            SurfaceUD,
            [*:0]const u8,
            *apprt.ClipboardRequest,
            apprt.ClipboardRequestType,
        ) callconv(.c) void,

        /// Write the clipboard value.
        write_clipboard: *const fn (
            SurfaceUD,
            c_int,
            [*]const CAPI.ClipboardContent,
            usize,
            bool,
        ) callconv(.c) void,

        /// Close the current surface given by this function.
        close_surface: ?*const fn (SurfaceUD, bool) callconv(.c) void = null,
    };

    /// This is the key event sent for ghostty_surface_key and
    /// ghostty_app_key.
    pub const KeyEvent = struct {
        action: input.Action,
        mods: input.Mods,
        consumed_mods: input.Mods,
        keycode: u32,
        text: ?[:0]const u8,
        unshifted_codepoint: u32,
        composing: bool,

        /// Convert a libghostty key event into a core key event.
        fn core(self: KeyEvent) ?input.KeyEvent {
            const text: []const u8 = if (self.text) |v| v else "";
            const unshifted_codepoint: u21 = std.math.cast(
                u21,
                self.unshifted_codepoint,
            ) orelse 0;

            // We want to get the physical unmapped key to process keybinds.
            const physical_key = keycode: for (input.keycodes.entries) |entry| {
                if (entry.native == self.keycode) break :keycode entry.key;
            } else .unidentified;

            // Build our final key event
            return .{
                .action = self.action,
                .key = physical_key,
                .mods = self.mods,
                .consumed_mods = self.consumed_mods,
                .composing = self.composing,
                .utf8 = text,
                .unshifted_codepoint = unshifted_codepoint,
            };
        }
    };

    core_app: *CoreApp,
    opts: Options,
    keymap: input.Keymap,

    /// The configuration for the app. This is owned by this structure.
    config: Config,

    pub fn init(
        self: *App,
        core_app: *CoreApp,
        config: *const Config,
        opts: Options,
    ) !void {
        // We have to clone the config.
        const alloc = core_app.alloc;
        var config_clone = try config.clone(alloc);
        errdefer config_clone.deinit();

        var keymap = try input.Keymap.init();
        errdefer keymap.deinit();

        self.* = .{
            .core_app = core_app,
            .config = config_clone,
            .opts = opts,
            .keymap = keymap,
        };
    }

    pub fn terminate(self: *App) void {
        self.keymap.deinit();
        self.config.deinit();
    }

    /// Returns true if there are any global keybinds in the configuration.
    pub fn hasGlobalKeybinds(self: *const App) bool {
        var it = self.config.keybind.set.bindings.iterator();
        while (it.next()) |entry| {
            switch (entry.value_ptr.*) {
                .leader => {},
                inline .leaf, .leaf_chained => |leaf| if (leaf.flags.global) return true,
            }
        }

        return false;
    }

    /// The target of a key event. This is used to determine some subtly
    /// different behavior between app and surface key events.
    pub const KeyTarget = union(enum) {
        app,
        surface: *Surface,
    };

    /// See CoreApp.focusEvent
    pub fn focusEvent(self: *App, focused: bool) void {
        self.core_app.focusEvent(focused);
    }

    /// See CoreApp.keyEvent.
    pub fn keyEvent(
        self: *App,
        target: KeyTarget,
        event: KeyEvent,
    ) !bool {
        // Convert our C key event into a Zig one.
        const input_event: input.KeyEvent = event.core() orelse
            return false;

        // Invoke the core Ghostty logic to handle this input.
        const effect: CoreSurface.InputEffect = switch (target) {
            .app => if (self.core_app.keyEvent(
                self,
                input_event,
            )) .consumed else .ignored,

            .surface => |surface| try surface.core_surface.keyCallback(
                input_event,
            ),
        };

        return switch (effect) {
            .closed => true,
            .ignored => false,
            .consumed => true,
        };
    }

    /// This should be called whenever the keyboard layout was changed.
    pub fn reloadKeymap(self: *App) !void {
        // Reload the keymap
        try self.keymap.reload();
    }

    /// Loads the keyboard layout.
    ///
    /// Kind of expensive so this should be avoided if possible. When I say
    /// "kind of expensive" I mean that its not something you probably want
    /// to run on every keypress.
    pub fn keyboardLayout(self: *const App) input.KeyboardLayout {
        // We only support keyboard layout detection on macOS.
        if (comptime builtin.os.tag != .macos) return .unknown;

        // Any layout larger than this is not something we can handle.
        var buf: [256]u8 = undefined;
        const id = self.keymap.sourceId(&buf) catch |err| {
            comptime assert(@TypeOf(err) == error{OutOfMemory});
            return .unknown;
        };

        return input.KeyboardLayout.mapAppleId(id) orelse .unknown;
    }

    pub fn wakeup(self: *const App) void {
        self.opts.wakeup(self.opts.userdata);
    }

    pub fn wait(self: *const App) !void {
        _ = self;
    }

    /// Create a new surface for the app.
    fn newSurface(self: *App, opts: Surface.Options) !*Surface {
        // Grab a surface allocation because we're going to need it.
        var surface = try self.core_app.alloc.create(Surface);
        errdefer self.core_app.alloc.destroy(surface);

        // Create the surface
        try surface.init(self, opts);
        errdefer surface.deinit();

        return surface;
    }

    /// Close the given surface.
    pub fn closeSurface(self: *App, surface: *Surface) void {
        surface.deinit();
        self.core_app.alloc.destroy(surface);
    }

    pub fn redrawInspector(self: *App, surface: *Surface) void {
        _ = self;
        surface.queueInspectorRender();
    }

    /// Perform a given action. Returns `true` if the action was able to be
    /// performed, `false` otherwise.
    pub fn performAction(
        self: *App,
        target: apprt.Target,
        comptime action: apprt.Action.Key,
        value: apprt.Action.Value(action),
    ) !bool {
        // Special case certain actions before they are sent to the
        // embedded apprt.
        self.performPreAction(target, action, value);

        log.debug("dispatching action target={t} action={} value={any}", .{
            target,
            action,
            value,
        });
        return self.opts.action(
            self,
            target.cval(),
            @unionInit(apprt.Action, @tagName(action), value).cval(),
        );
    }

    fn performPreAction(
        self: *App,
        target: apprt.Target,
        comptime action: apprt.Action.Key,
        value: apprt.Action.Value(action),
    ) void {
        // Special case certain actions before they are sent to the embedder
        switch (action) {
            .set_title => switch (target) {
                .app => {},
                .surface => |surface| {
                    // Dupe the title so that we can store it. If we get an allocation
                    // error we just ignore it, since this only breaks a few minor things.
                    const alloc = self.core_app.alloc;
                    if (surface.rt_surface.title) |v| alloc.free(v);
                    surface.rt_surface.title = alloc.dupeZ(u8, value.title) catch null;
                },
            },

            .config_change => switch (target) {
                .surface => {},

                // For app updates, we update our core config. We need to
                // clone it because the caller owns the param.
                .app => if (value.config.clone(self.core_app.alloc)) |config| {
                    self.config.deinit();
                    self.config = config;
                } else |err| {
                    log.err("error updating app config err={}", .{err});
                },
            },

            else => {},
        }
    }

    /// Send the given IPC to a running Ghostty. Returns `true` if the action was
    /// able to be performed, `false` otherwise.
    ///
    /// Note that this is a static function. Since this is called from a CLI app (or
    /// some other process that is not Ghostty) there is no full-featured apprt App
    /// to use.
    pub fn performIpc(
        _: Allocator,
        _: apprt.ipc.Target,
        comptime action: apprt.ipc.Action.Key,
        _: apprt.ipc.Action.Value(action),
    ) (Allocator.Error || std.posix.WriteError || apprt.ipc.Errors)!bool {
        switch (action) {
            .new_window => return false,
            .toggle_quick_terminal => return false,
        }
    }
};

/// Platform-specific configuration for libghostty.
pub const Platform = union(PlatformTag) {
    macos: MacOS,
    ios: IOS,
    windows: Windows,

    // If our build target for libghostty is not darwin then we do
    // not include macos support at all.
    pub const MacOS = if (builtin.target.os.tag.isDarwin()) struct {
        /// The view to render the surface on.
        nsview: objc.Object,
    } else void;

    pub const IOS = if (builtin.target.os.tag.isDarwin()) struct {
        /// The view to render the surface on.
        uiview: objc.Object,
    } else void;

    pub const Windows = if (builtin.target.os.tag == .windows) struct {
        /// The HWND to render into, or null for composition/shared texture modes.
        hwnd: ?std.os.windows.HANDLE,
        /// Non-null selects SwapChainPanel mode. The renderer only checks
        /// it for null; it no longer binds the panel itself. The embedder
        /// binds the surface handle from ghostty_surface_get_swap_chain_handle
        /// via ISwapChainPanelNative2::SetSwapChainHandle.
        swap_chain_panel: ?*anyopaque = null,
        /// Shared-texture surface configuration. Only honoured when
        /// both `hwnd` and `swap_chain_panel` are null and
        /// `shared_texture.enabled` is true. Mirrors the nested
        /// `shared_texture` struct in ghostty_platform_windows_s.
        shared_texture: SharedTexture = .{},

        pub const SharedTexture = struct {
            enabled: bool = false,
            width: u32 = 0,
            height: u32 = 0,
        };
    } else void;

    // The C ABI compatible version of this union. The tag is expected
    // to be stored elsewhere.
    pub const C = extern union {
        macos: extern struct {
            nsview: ?*anyopaque,
        },

        ios: extern struct {
            uiview: ?*anyopaque,
        },

        windows: extern struct {
            hwnd: ?*anyopaque,
            swap_chain_panel: ?*anyopaque,
            // Mirrors the anonymous `shared_texture` sub-struct in
            // ghostty_platform_windows_s. The C side declares this as
            // an anonymous nested struct; we flatten it into an inline
            // extern struct here to preserve the same layout.
            shared_texture: extern struct {
                enabled: bool,
                width: u32,
                height: u32,
            },
        },
    };

    /// Initialize a Platform a tag and configuration from the C ABI.
    pub fn init(tag_int: c_int, c_platform: C) !Platform {
        const tag = try std.meta.intToEnum(PlatformTag, tag_int);
        return switch (tag) {
            .macos => if (MacOS != void) macos: {
                const config = c_platform.macos;
                const nsview = objc.Object.fromId(config.nsview orelse
                    break :macos error.NSViewMustBeSet);
                break :macos .{ .macos = .{ .nsview = nsview } };
            } else error.UnsupportedPlatform,

            .ios => if (IOS != void) ios: {
                const config = c_platform.ios;
                const uiview = objc.Object.fromId(config.uiview orelse
                    break :ios error.UIViewMustBeSet);
                break :ios .{ .ios = .{ .uiview = uiview } };
            } else error.UnsupportedPlatform,

            .windows => if (Windows != void) .{ .windows = .{
                .hwnd = c_platform.windows.hwnd,
                .swap_chain_panel = c_platform.windows.swap_chain_panel,
                .shared_texture = .{
                    .enabled = c_platform.windows.shared_texture.enabled,
                    .width = c_platform.windows.shared_texture.width,
                    .height = c_platform.windows.shared_texture.height,
                },
            } } else error.UnsupportedPlatform,
        };
    }
};

pub const PlatformTag = enum(c_int) {
    // "0" is reserved for invalid so we can detect unset values
    // from the C API.

    macos = 1,
    ios = 2,
    windows = 3,
};

pub const EnvVar = extern struct {
    /// The name of the environment variable.
    key: [*:0]const u8,

    /// The value of the environment variable.
    value: [*:0]const u8,
};

pub const Surface = struct {
    app: *App,
    platform: Platform,
    userdata: ?*anyopaque = null,
    core_surface: CoreSurface,
    content_scale: apprt.ContentScale,
    size: apprt.SurfaceSize,
    cursor_pos: apprt.CursorPos,
    inspector: ?*Inspector = null,

    /// The current title of the surface. The embedded apprt saves this so
    /// that getTitle works without the implementer needing to save it.
    title: ?[:0]const u8 = null,

    /// Windows buffers WM_KEYDOWN here so a following WM_CHAR can
    /// attach text before dispatching through key encoding.
    pending_key: if (builtin.os.tag == .windows)
        ?PendingKey
    else
        void = if (builtin.os.tag == .windows) null else {},

    /// Text buffer for the pending key. Lives outside the optional so
    /// it isn't poisoned when the optional is cleared in debug builds.
    /// Sized to match GTK's im_buf so IME compositions aren't truncated.
    pending_key_text: if (builtin.os.tag == .windows)
        [128]u8
    else
        void = if (builtin.os.tag == .windows) .{0} ** 128 else {},

    /// Input redirect for in-process features (e.g., theme picker).
    input_redirect: if (builtin.os.tag == .windows)
        ?InputRedirect
    else
        void = if (builtin.os.tag == .windows) null else {},

    scroll_redirect: if (builtin.os.tag == .windows)
        ?ScrollRedirect
    else
        void = if (builtin.os.tag == .windows) null else {},

    resize_redirect: if (builtin.os.tag == .windows)
        ?ResizeRedirect
    else
        void = if (builtin.os.tag == .windows) null else {},

    const PendingKey = struct {
        event: App.KeyEvent,
    };

    /// Intercepts events at the apprt boundary before they reach
    /// core Surface. Used by the inline theme picker.
    const InputRedirect = struct {
        callback: *const fn (ud: ?*anyopaque, event: *const input.KeyEvent) bool,
        userdata: ?*anyopaque,
    };

    /// yoff is positive for scroll-up, negative for scroll-down.
    /// Return true if consumed.
    const ScrollRedirect = struct {
        callback: *const fn (ud: ?*anyopaque, yoff: f64) bool,
        userdata: ?*anyopaque,
    };

    /// Called after the core surface recalculates its grid, so cols/rows
    /// reflect the new size.
    const ResizeRedirect = struct {
        callback: *const fn (ud: ?*anyopaque, cols: u16, rows: u16) void,
        userdata: ?*anyopaque,
    };

    /// Surface initialization options.
    pub const Options = extern struct {
        /// The platform that this surface is being initialized for and
        /// the associated platform-specific configuration.
        platform_tag: c_int = 0,
        platform: Platform.C = std.mem.zeroes(Platform.C),

        /// Userdata passed to some of the callbacks.
        userdata: ?*anyopaque = null,

        /// The scale factor of the screen.
        scale_factor: f64 = 1,

        /// The font size to inherit. If 0, default font size will be used.
        font_size: f32 = 0,

        /// The working directory to load into.
        working_directory: ?[*:0]const u8 = null,

        /// The command to run in the new surface. If this is set then
        /// the "wait-after-command" option is also automatically set to true,
        /// since this is used for scripting.
        ///
        /// This command always run in a shell (e.g. via `/bin/sh -c`),
        /// despite Ghostty allowing directly executed commands via config.
        /// This is a legacy thing and we should probably change it in the
        /// future once we have a concrete use case.
        command: ?[*:0]const u8 = null,

        /// Extra environment variables to set for the surface.
        env_vars: ?[*]EnvVar = null,
        env_var_count: usize = 0,

        /// Input to send to the command after it is started.
        initial_input: ?[*:0]const u8 = null,

        /// Wait after the command exits
        wait_after_command: bool = false,

        /// Context for the new surface
        context: apprt.surface.NewSurfaceContext = .window,
    };

    pub fn init(self: *Surface, app: *App, opts: Options) !void {
        self.* = .{
            .app = app,
            .platform = try .init(opts.platform_tag, opts.platform),
            .userdata = opts.userdata,
            .core_surface = undefined,
            .content_scale = .{
                .x = @floatCast(opts.scale_factor),
                .y = @floatCast(opts.scale_factor),
            },
            .size = .{ .width = 800, .height = 600 },
            .cursor_pos = .{ .x = -1, .y = -1 },
        };

        // Add ourselves to the list of surfaces on the app.
        try app.core_app.addSurface(self);
        errdefer app.core_app.deleteSurface(self);

        // Shallow copy the config so that we can modify it.
        var config = try apprt.surface.newConfig(app.core_app, &app.config, opts.context);
        defer config.deinit();

        // If we have a working directory from the options then we set it.
        if (opts.working_directory) |c_wd| {
            const wd = std.mem.sliceTo(c_wd, 0);
            if (wd.len > 0) wd: {
                var dir = std.fs.openDirAbsolute(wd, .{}) catch |err| {
                    log.warn(
                        "error opening requested working directory dir={s} err={}",
                        .{ wd, err },
                    );
                    break :wd;
                };
                defer dir.close();

                const stat = dir.stat() catch |err| {
                    log.warn(
                        "failed to stat requested working directory dir={s} err={}",
                        .{ wd, err },
                    );
                    break :wd;
                };

                if (stat.kind != .directory) {
                    log.warn(
                        "requested working directory is not a directory dir={s}",
                        .{wd},
                    );
                    break :wd;
                }

                var wd_val: configpkg.WorkingDirectory = .{ .path = wd };
                if (wd_val.finalize(config.arenaAlloc())) |_| {
                    config.@"working-directory" = wd_val;
                } else |err| {
                    log.warn(
                        "error finalizing working directory config dir={s} err={}",
                        .{ wd_val.path, err },
                    );
                }
            }
        }

        // If we have a command from the options then we set it.
        if (opts.command) |c_command| {
            const cmd = std.mem.sliceTo(c_command, 0);
            if (cmd.len > 0) {
                config.command = .{ .shell = cmd };
                config.@"wait-after-command" = true;
            }
        }

        // Apply any environment variables that were requested.
        if (opts.env_var_count > 0) {
            const alloc = config.arenaAlloc();
            for (opts.env_vars.?[0..opts.env_var_count]) |env_var| {
                const key = std.mem.sliceTo(env_var.key, 0);
                const value = std.mem.sliceTo(env_var.value, 0);
                try config.env.map.put(
                    alloc,
                    try alloc.dupeZ(u8, key),
                    try alloc.dupeZ(u8, value),
                );
            }
        }

        // If we have an initial input then we set it.
        if (opts.initial_input) |c_input| {
            const alloc = config.arenaAlloc();

            // We need to escape the string because the "raw" field
            // expects a Zig string.
            var buf: std.Io.Writer.Allocating = .init(alloc);
            defer buf.deinit();
            try std.zig.stringEscape(
                std.mem.sliceTo(c_input, 0),
                &buf.writer,
            );

            config.input.list.clearRetainingCapacity();
            try config.input.list.append(
                alloc,
                .{ .raw = try buf.toOwnedSliceSentinel(0) },
            );
        }

        // Wait after command
        if (opts.wait_after_command) {
            config.@"wait-after-command" = true;
        }

        // Initialize our surface right away. We're given a view that is
        // ready to use.
        try self.core_surface.init(
            app.core_app.alloc,
            &config,
            app.core_app,
            app,
            self,
        );
        errdefer self.core_surface.deinit();

        // If our options requested a specific font-size, set that.
        if (opts.font_size != 0) {
            var font_size = self.core_surface.font_size;
            font_size.points = opts.font_size;
            try self.core_surface.setFontSize(font_size);
        }
    }

    pub fn deinit(self: *Surface) void {
        // Shut down our inspector
        self.freeInspector();

        // Free our title
        if (self.title) |v| self.app.core_app.alloc.free(v);

        // Remove ourselves from the list of known surfaces in the app.
        self.app.core_app.deleteSurface(self);

        // Clean up our core surface so that all the rendering and IO stop.
        self.core_surface.deinit();
    }

    /// Initialize the inspector instance. A surface can only have one
    /// inspector at any given time, so this will return the previous inspector
    /// if it was already initialized.
    pub fn initInspector(self: *Surface) !*Inspector {
        if (self.inspector) |v| return v;

        const alloc = self.app.core_app.alloc;
        const inspector = try alloc.create(Inspector);
        errdefer alloc.destroy(inspector);
        inspector.* = try .init(self);
        self.inspector = inspector;
        return inspector;
    }

    pub fn freeInspector(self: *Surface) void {
        if (self.inspector) |v| {
            v.deinit();
            self.app.core_app.alloc.destroy(v);
            self.inspector = null;
        }
    }

    pub fn core(self: *Surface) *CoreSurface {
        return &self.core_surface;
    }

    pub fn rtApp(self: *const Surface) *App {
        return self.app;
    }

    pub fn close(self: *const Surface, process_alive: bool) void {
        const func = self.app.opts.close_surface orelse {
            log.info("runtime embedder does not support closing a surface", .{});
            return;
        };

        func(self.userdata, process_alive);
    }

    pub fn getContentScale(self: *const Surface) !apprt.ContentScale {
        return self.content_scale;
    }

    pub fn getSize(self: *const Surface) !apprt.SurfaceSize {
        return self.size;
    }

    pub fn getTitle(self: *Surface) ?[:0]const u8 {
        return self.title;
    }

    pub fn supportsClipboard(
        self: *const Surface,
        clipboard_type: apprt.Clipboard,
    ) bool {
        return switch (clipboard_type) {
            .standard => true,
            .selection, .primary => self.app.opts.supports_selection_clipboard,
        };
    }

    pub fn clipboardRequest(
        self: *Surface,
        clipboard_type: apprt.Clipboard,
        state: apprt.ClipboardRequest,
    ) !bool {
        // We need to allocate to get a pointer to store our clipboard request
        // so that it is stable until the read_clipboard callback and call
        // complete_clipboard_request. This sucks but clipboard requests aren't
        // high throughput so it's probably fine.
        const alloc = self.app.core_app.alloc;
        const state_ptr = try alloc.create(apprt.ClipboardRequest);
        errdefer alloc.destroy(state_ptr);
        state_ptr.* = state;

        const started = self.app.opts.read_clipboard(
            self.userdata,
            @intCast(@intFromEnum(clipboard_type)),
            state_ptr,
        );
        if (!started) {
            alloc.destroy(state_ptr);
            return false;
        }

        return true;
    }

    fn completeClipboardRequest(
        self: *Surface,
        str: [:0]const u8,
        state: *apprt.ClipboardRequest,
        confirmed: bool,
    ) void {
        const alloc = self.app.core_app.alloc;

        // Attempt to complete the request, but we may request
        // confirmation.
        self.core_surface.completeClipboardRequest(
            state.*,
            str,
            confirmed,
        ) catch |err| switch (err) {
            error.UnsafePaste,
            error.UnauthorizedPaste,
            => {
                self.app.opts.confirm_read_clipboard(
                    self.userdata,
                    str.ptr,
                    state,
                    state.*,
                );

                return;
            },

            else => log.err("error completing clipboard request err={}", .{err}),
        };

        // We don't defer this because the clipboard confirmation route
        // preserves the clipboard request.
        alloc.destroy(state);
    }

    pub fn setClipboard(
        self: *const Surface,
        clipboard_type: apprt.Clipboard,
        contents: []const apprt.ClipboardContent,
        confirm: bool,
    ) !void {
        const alloc = self.app.core_app.alloc;
        const array = try alloc.alloc(CAPI.ClipboardContent, contents.len);
        defer alloc.free(array);
        for (contents, 0..) |content, i| {
            array[i] = .{
                .mime = content.mime,
                .data = content.data,
            };
        }

        self.app.opts.write_clipboard(
            self.userdata,
            @intCast(@intFromEnum(clipboard_type)),
            array.ptr,
            array.len,
            confirm,
        );
    }

    pub fn getCursorPos(self: *const Surface) !apprt.CursorPos {
        return self.cursor_pos;
    }

    pub fn refresh(self: *Surface) void {
        self.core_surface.refreshCallback() catch |err| {
            log.err("error in refresh callback err={}", .{err});
            return;
        };
    }

    pub fn draw(self: *Surface) void {
        self.core_surface.draw() catch |err| {
            log.err("error in draw err={}", .{err});
            return;
        };
    }

    pub fn updateContentScale(self: *Surface, x: f64, y: f64) void {
        // We are an embedded API so the caller can send us all sorts of
        // garbage. We want to make sure that the float values are valid
        // and we don't want to support fractional scaling below 1.
        const x_scaled = @max(1, if (std.math.isNan(x)) 1 else x);
        const y_scaled = @max(1, if (std.math.isNan(y)) 1 else y);

        self.content_scale = .{
            .x = @floatCast(x_scaled),
            .y = @floatCast(y_scaled),
        };

        self.core_surface.contentScaleCallback(self.content_scale) catch |err| {
            log.err("error in content scale callback err={}", .{err});
            return;
        };
    }

    pub fn updateSize(self: *Surface, width: u32, height: u32) void {
        // Runtimes sometimes generate superfluous resize events even
        // if the size did not actually change (SwiftUI). We check
        // that the size actually changed from what we last recorded
        // since resizes are expensive.
        if (self.size.width == width and self.size.height == height) return;

        self.size = .{
            .width = width,
            .height = height,
        };

        // When an in-process feature owns the screen (Windows-only
        // resize_redirect, e.g. the inline theme picker), wrap the
        // reflow + redraw in a synchronized update (DEC mode 2026) so the
        // renderer never presents the intermediate alt-screen reflow frame
        // before the feature clears and redraws. Mode 2026 is begun BEFORE
        // sizeCallback so the reflow happens under the hold, and ended only
        // after the feature has redrawn, yielding a single clean present.
        // ?2026l is emitted on every exit path of this (non-re-entrant)
        // call so the hold is always released. (#219)
        if (comptime builtin.os.tag == .windows) {
            if (self.resize_redirect) |redirect| {
                self.core_surface.writeVt("\x1b[?2026h");
                self.core_surface.sizeCallback(self.size) catch |err| {
                    log.err("error in size callback err={}", .{err});
                    self.core_surface.writeVt("\x1b[?2026l");
                    return;
                };
                const grid = self.core_surface.size.grid();
                redirect.callback(redirect.userdata, @intCast(grid.columns), @intCast(grid.rows));
                self.core_surface.writeVt("\x1b[?2026l");
                return;
            }
        }

        // Default path: no in-process feature owns the screen.
        self.core_surface.sizeCallback(self.size) catch |err| {
            log.err("error in size callback err={}", .{err});
            return;
        };
    }

    pub fn colorSchemeCallback(self: *Surface, scheme: apprt.ColorScheme) void {
        self.core_surface.colorSchemeCallback(scheme) catch |err| {
            log.err("error setting color scheme err={}", .{err});
            return;
        };
    }

    pub fn mouseButtonCallback(
        self: *Surface,
        action: input.MouseButtonState,
        button: input.MouseButton,
        mods: input.Mods,
    ) bool {
        return self.core_surface.mouseButtonCallback(action, button, mods) catch |err| {
            log.err("error in mouse button callback err={}", .{err});
            return false;
        };
    }

    pub fn mousePressureCallback(
        self: *Surface,
        stage: input.MousePressureStage,
        pressure: f64,
    ) void {
        self.core_surface.mousePressureCallback(stage, pressure) catch |err| {
            log.err("error in mouse pressure callback err={}", .{err});
            return;
        };
    }

    pub fn scrollCallback(
        self: *Surface,
        xoff: f64,
        yoff: f64,
        mods: input.ScrollMods,
    ) void {
        if (comptime builtin.os.tag == .windows) {
            if (self.scroll_redirect) |redirect| {
                if (redirect.callback(redirect.userdata, yoff))
                    return;
            }
        }

        self.core_surface.scrollCallback(xoff, yoff, mods) catch |err| {
            log.err("error in scroll callback err={}", .{err});
            return;
        };
    }

    pub fn cursorPosCallback(
        self: *Surface,
        x: f64,
        y: f64,
        mods: input.Mods,
    ) void {
        // Convert our unscaled x/y to scaled.
        const pos = self.cursorPosToPixels(.{
            .x = @floatCast(x),
            .y = @floatCast(y),
        }) catch |err| {
            log.err(
                "error converting cursor pos to scaled pixels in cursor pos callback err={}",
                .{err},
            );
            return;
        };

        // There are cases where the platform reports a mouse motion event
        // without the cursor actually moving. For example, on macOS, updating
        // the window title can trigger a phantom mouse-move event at the same
        // coordinates. This can cause the mouse to incorrectly unhide when
        // mouse-hide-while-typing is enabled (commonly seen with TUI apps
        // like Zellij that frequently update the title). To prevent incorrect
        // behavior, we only continue with callback logic if the cursor has
        // actually moved.
        if (@abs(self.cursor_pos.x - pos.x) < 1 and
            @abs(self.cursor_pos.y - pos.y) < 1) return;

        self.cursor_pos = pos;

        self.core_surface.cursorPosCallback(self.cursor_pos, mods) catch |err| {
            log.err("error in cursor pos callback err={}", .{err});
            return;
        };
    }

    pub fn preeditCallback(self: *Surface, preedit_: ?[]const u8) void {
        _ = self.core_surface.preeditCallback(preedit_) catch |err| {
            log.err("error in preedit callback err={}", .{err});
            return;
        };
    }

    pub fn textCallback(self: *Surface, text: []const u8) void {
        _ = self.core_surface.textCallback(text) catch |err| {
            log.err("error in key callback err={}", .{err});
            return;
        };
    }

    /// Dispatch a key event through the core key handling path.
    fn dispatchKey(self: *Surface, event: App.KeyEvent) bool {
        return self.app.keyEvent(
            .{ .surface = self },
            event,
        ) catch |err| {
            log.warn("error processing key event err={}", .{err});
            return false;
        };
    }

    /// Flush a buffered Windows key event. No-op on non-Windows.
    fn flushPendingKey(self: *Surface) void {
        if (comptime builtin.os.tag != .windows) return;
        if (self.pending_key) |pending| {
            self.pending_key = null;
            _ = self.dispatchKey(pending.event);
        }
    }

    pub fn focusCallback(self: *Surface, focused: bool) void {
        // Flush any buffered key event on focus loss so it isn't
        // silently dropped (e.g. user presses a key then Alt-Tabs
        // before WM_CHAR arrives).
        if (!focused) self.flushPendingKey();

        self.core_surface.focusCallback(focused) catch |err| {
            log.err("error in focus callback err={}", .{err});
            return;
        };
    }

    pub fn occlusionCallback(self: *Surface, visible: bool) void {
        self.core_surface.occlusionCallback(visible) catch |err| {
            log.err("error in occlusion callback err={}", .{err});
            return;
        };
    }

    fn queueInspectorRender(self: *Surface) void {
        _ = self.app.performAction(
            .{ .surface = &self.core_surface },
            .render_inspector,
            {},
        ) catch |err| {
            log.err("error rendering the inspector err={}", .{err});
            return;
        };
    }

    pub fn newSurfaceOptions(self: *const Surface, context: apprt.surface.NewSurfaceContext) apprt.Surface.Options {
        const font_size: f32 = font_size: {
            if (!self.app.config.@"window-inherit-font-size") break :font_size 0;
            break :font_size self.core_surface.font_size.points;
        };

        const working_directory: ?[*:0]const u8 = wd: {
            if (!apprt.surface.shouldInheritWorkingDirectory(context, &self.app.config)) break :wd null;
            const cwd = self.core_surface.pwd(self.app.core_app.alloc) catch null orelse break :wd null;
            defer self.app.core_app.alloc.free(cwd);
            break :wd self.app.core_app.alloc.dupeZ(u8, cwd) catch null;
        };

        return .{
            .font_size = font_size,
            .working_directory = working_directory,
            .context = context,
        };
    }

    pub fn defaultTermioEnv(self: *const Surface) !std.process.EnvMap {
        const alloc = self.app.core_app.alloc;
        var env = try internal_os.getEnvMap(alloc);
        errdefer env.deinit();

        if (comptime builtin.target.os.tag.isDarwin()) {
            if (env.get("__XCODE_BUILT_PRODUCTS_DIR_PATHS") != null) {
                env.remove("__XCODE_BUILT_PRODUCTS_DIR_PATHS");
                env.remove("__XPC_DYLD_LIBRARY_PATH");
                env.remove("DYLD_FRAMEWORK_PATH");
                env.remove("DYLD_INSERT_LIBRARIES");
                env.remove("DYLD_LIBRARY_PATH");
                env.remove("LD_LIBRARY_PATH");
                env.remove("SECURITYSESSIONID");
                env.remove("XPC_SERVICE_NAME");
            }

            // Remove this so that running `ghostty` within Ghostty works.
            env.remove("GHOSTTY_MAC_LAUNCH_SOURCE");

            // If we were launched from the desktop then we want to
            // remove the LANGUAGE env var so that we don't inherit
            // our translation settings for Ghostty. If we aren't from
            // the desktop then we didn't set our LANGUAGE var so we
            // don't need to remove it.
            if (internal_os.launchedFromDesktop()) env.remove("LANGUAGE");
        }

        return env;
    }

    /// The cursor position from the host directly is in screen coordinates but
    /// all our interface works in pixels.
    fn cursorPosToPixels(self: *const Surface, pos: apprt.CursorPos) !apprt.CursorPos {
        const scale = try self.getContentScale();
        return .{ .x = pos.x * scale.x, .y = pos.y * scale.y };
    }
};

/// Inspector is the state required for the terminal inspector. A terminal
/// inspector is 1:1 with a Surface.
pub const Inspector = struct {
    const cimgui = @import("dcimgui");

    surface: *Surface,
    ig_ctx: *cimgui.c.ImGuiContext,
    backend: ?Backend = null,
    content_scale: f64 = 1,

    /// User-controlled zoom for the inspector UI (font + spacing), adjusted
    /// with Ctrl++/Ctrl+- (and Ctrl+0 to reset). Multiplies on top of the
    /// platform content scale.
    ui_scale: f64 = 1,

    /// Our previous instant used to calculate delta time for animations.
    instant: ?std.time.Instant = null,

    /// SRV descriptor heap backing the imgui font atlas for the DirectX12
    /// backend (Windows only). Owned here so its address stays stable for
    /// imgui's descriptor callbacks.
    dx12_heap: if (builtin.os.tag == .windows) ?dx12_imgui.SrvHeap else void =
        if (builtin.os.tag == .windows) null else {},

    const Backend = enum {
        metal,
        directx12,

        pub fn deinit(self: Backend) void {
            switch (self) {
                .metal => if (builtin.target.os.tag.isDarwin()) cimgui.ImGui_ImplMetal_Shutdown(),
                .directx12 => if (builtin.os.tag == .windows) dx12_imgui.shutdown(),
            }
        }
    };

    pub fn init(surface: *Surface) !Inspector {
        const ig_ctx = cimgui.c.ImGui_CreateContext(null) orelse return error.OutOfMemory;
        errdefer cimgui.c.ImGui_DestroyContext(ig_ctx);
        cimgui.c.ImGui_SetCurrentContext(ig_ctx);
        const io: *cimgui.c.ImGuiIO = cimgui.c.ImGui_GetIO();
        io.BackendPlatformName = "ghostty_embedded";

        // Setup our core inspector
        CoreInspector.setup();
        surface.core_surface.activateInspector() catch |err| {
            log.err("failed to activate inspector err={}", .{err});
        };

        return .{
            .surface = surface,
            .ig_ctx = ig_ctx,
        };
    }

    pub fn deinit(self: *Inspector) void {
        self.surface.core_surface.deactivateInspector();
        cimgui.c.ImGui_SetCurrentContext(self.ig_ctx);
        if (self.backend) |v| v.deinit();
        self.deinitDx12Heap();
        cimgui.c.ImGui_DestroyContext(self.ig_ctx);
    }

    /// Queue a render for the next frame.
    pub fn queueRender(self: *Inspector) void {
        self.surface.queueInspectorRender();
    }

    /// Initialize the inspector for a metal backend.
    pub fn initMetal(self: *Inspector, device: objc.Object) bool {
        defer device.msgSend(void, objc.sel("release"), .{});
        cimgui.c.ImGui_SetCurrentContext(self.ig_ctx);

        if (self.backend) |v| {
            v.deinit();
            self.backend = null;
        }

        if (!cimgui.ImGui_ImplMetal_Init(device.value)) {
            log.warn("failed to initialize metal backend", .{});
            return false;
        }
        self.backend = .metal;

        log.debug("initialized metal backend", .{});
        return true;
    }

    pub fn renderMetal(
        self: *Inspector,
        command_buffer: objc.Object,
        desc: objc.Object,
    ) !void {
        defer {
            command_buffer.msgSend(void, objc.sel("release"), .{});
            desc.msgSend(void, objc.sel("release"), .{});
        }
        assert(self.backend == .metal);
        //log.debug("render", .{});

        // Setup our imgui frame. We need to render multiple frames to ensure
        // ImGui completes all its state processing. I don't know how to fix
        // this.
        for (0..2) |_| {
            cimgui.ImGui_ImplMetal_NewFrame(desc.value);
            try self.newFrame();
            cimgui.c.ImGui_NewFrame();

            // Build our UI
            render: {
                const surface = &self.surface.core_surface;
                const inspector = surface.inspector orelse break :render;
                inspector.render(surface);
            }

            // Render
            cimgui.c.ImGui_Render();
        }

        // MTLRenderCommandEncoder
        const encoder = command_buffer.msgSend(
            objc.Object,
            objc.sel("renderCommandEncoderWithDescriptor:"),
            .{desc.value},
        );
        defer encoder.msgSend(void, objc.sel("endEncoding"), .{});
        cimgui.ImGui_ImplMetal_RenderDrawData(
            cimgui.c.ImGui_GetDrawData(),
            command_buffer.value,
            encoder.value,
        );
    }

    /// Release the DirectX12 SRV heap if one is allocated. No-op off Windows.
    fn deinitDx12Heap(self: *Inspector) void {
        if (comptime builtin.os.tag != .windows) return;
        if (self.dx12_heap) |*h| {
            h.deinit();
            self.dx12_heap = null;
        }
    }

    /// Initialize the inspector for a DirectX12 backend. `device` and
    /// `command_queue` are `ID3D12Device`/`ID3D12CommandQueue` pointers from
    /// the host's inspector window; `rtv_format` is its swap chain's
    /// DXGI_FORMAT. Returns false on non-Windows or on failure.
    pub fn initDirectX12(
        self: *Inspector,
        device: *anyopaque,
        command_queue: *anyopaque,
        num_frames: u32,
        rtv_format: u32,
    ) bool {
        if (comptime builtin.os.tag != .windows) return false;

        cimgui.c.ImGui_SetCurrentContext(self.ig_ctx);

        if (self.backend) |v| {
            v.deinit();
            self.backend = null;
        }
        self.deinitDx12Heap();

        const d3d12 = @import("../renderer/directx12/d3d12.zig");
        const dev: *d3d12.ID3D12Device = @ptrCast(@alignCast(device));
        const queue: *d3d12.ID3D12CommandQueue = @ptrCast(@alignCast(command_queue));

        self.dx12_heap = dx12_imgui.createHeap(dev) catch {
            log.warn("failed to create inspector dx12 srv heap", .{});
            return false;
        };
        if (!dx12_imgui.init(dev, queue, num_frames, rtv_format, &self.dx12_heap.?)) {
            log.warn("failed to initialize directx12 backend", .{});
            self.deinitDx12Heap();
            return false;
        }
        self.backend = .directx12;

        log.debug("initialized directx12 backend", .{});
        return true;
    }

    pub fn renderDirectX12(self: *Inspector, command_list: *anyopaque) !void {
        if (comptime builtin.os.tag != .windows) return;
        // The host should only call this after a successful init; bail
        // cleanly rather than unwrap a null heap if that invariant breaks.
        if (self.backend != .directx12) return;

        cimgui.c.ImGui_SetCurrentContext(self.ig_ctx);

        // Render multiple frames to ensure ImGui completes its state
        // processing, matching the Metal backend.
        for (0..2) |_| {
            dx12_imgui.newFrame();
            try self.newFrame();
            cimgui.c.ImGui_NewFrame();

            render: {
                const surface = &self.surface.core_surface;
                const inspector = surface.inspector orelse break :render;
                inspector.render(surface);
            }

            cimgui.c.ImGui_Render();
        }

        const d3d12 = @import("../renderer/directx12/d3d12.zig");
        dx12_imgui.renderDrawData(
            cimgui.c.ImGui_GetDrawData(),
            @as(*d3d12.ID3D12GraphicsCommandList, @ptrCast(@alignCast(command_list))),
            &self.dx12_heap.?,
        );
    }

    pub fn updateContentScale(self: *Inspector, x: f64, y: f64) void {
        _ = y;

        // Cache our scale because we use it for cursor position calculations.
        self.content_scale = x;
        self.applyScale();
    }

    /// (Re)build the imgui style from defaults and apply the effective scale.
    /// Spacing/padding scale with content_scale * ui_scale; the font scales
    /// with ui_scale via FontScaleMain (the imgui 1.92 font zoom factor).
    fn applyScale(self: *Inspector) void {
        cimgui.c.ImGui_SetCurrentContext(self.ig_ctx);

        // Setup a new style and scale it appropriately. We must use the
        // ImGuiStyle constructor to get proper default values (e.g.,
        // CurveTessellationTol) rather than zero-initialized values.
        var style: cimgui.c.ImGuiStyle = undefined;
        cimgui.ext.ImGuiStyle_ImGuiStyle(&style);
        cimgui.c.ImGuiStyle_ScaleAllSizes(
            &style,
            @floatCast(self.content_scale * self.ui_scale),
        );
        style.FontScaleMain = @floatCast(self.ui_scale);
        const active_style = cimgui.c.ImGui_GetStyle();
        active_style.* = style;
    }

    /// Multiply the user zoom by `factor` (e.g. 1.1 to zoom in, 1/1.1 to
    /// zoom out), clamped to a sane range, and re-apply the style.
    pub fn zoomBy(self: *Inspector, factor: f64) void {
        self.ui_scale = std.math.clamp(self.ui_scale * factor, 0.5, 3.0);
        self.applyScale();
        self.queueRender();
    }

    /// Reset the user zoom to 1.0.
    pub fn zoomReset(self: *Inspector) void {
        self.ui_scale = 1;
        self.applyScale();
        self.queueRender();
    }

    pub fn updateSize(self: *Inspector, width: u32, height: u32) void {
        cimgui.c.ImGui_SetCurrentContext(self.ig_ctx);
        const io: *cimgui.c.ImGuiIO = cimgui.c.ImGui_GetIO();
        io.DisplaySize = .{ .x = @floatFromInt(width), .y = @floatFromInt(height) };
    }

    pub fn mouseButtonCallback(
        self: *Inspector,
        action: input.MouseButtonState,
        button: input.MouseButton,
        mods: input.Mods,
    ) void {
        _ = mods;

        self.queueRender();
        cimgui.c.ImGui_SetCurrentContext(self.ig_ctx);
        const io: *cimgui.c.ImGuiIO = cimgui.c.ImGui_GetIO();

        const imgui_button = switch (button) {
            .left => cimgui.c.ImGuiMouseButton_Left,
            .middle => cimgui.c.ImGuiMouseButton_Middle,
            .right => cimgui.c.ImGuiMouseButton_Right,
            else => return, // unsupported
        };

        cimgui.c.ImGuiIO_AddMouseButtonEvent(io, imgui_button, action == .press);
    }

    pub fn scrollCallback(
        self: *Inspector,
        xoff: f64,
        yoff: f64,
        mods: input.ScrollMods,
    ) void {
        self.queueRender();
        cimgui.c.ImGui_SetCurrentContext(self.ig_ctx);
        const io: *cimgui.c.ImGuiIO = cimgui.c.ImGui_GetIO();

        // For precision scrolling (trackpads), the values are in pixels which
        // scroll way too fast. Scale them down to approximate discrete wheel
        // notches. imgui expects 1.0 to scroll ~5 lines of text.
        const scale: f64 = if (mods.precision) 0.1 else 1.0;
        cimgui.c.ImGuiIO_AddMouseWheelEvent(
            io,
            @floatCast(xoff * scale),
            @floatCast(yoff * scale),
        );
    }

    pub fn cursorPosCallback(self: *Inspector, x: f64, y: f64) void {
        self.queueRender();
        cimgui.c.ImGui_SetCurrentContext(self.ig_ctx);
        const io: *cimgui.c.ImGuiIO = cimgui.c.ImGui_GetIO();
        cimgui.c.ImGuiIO_AddMousePosEvent(
            io,
            @floatCast(x * self.content_scale),
            @floatCast(y * self.content_scale),
        );
    }

    pub fn focusCallback(self: *Inspector, focused: bool) void {
        self.queueRender();
        cimgui.c.ImGui_SetCurrentContext(self.ig_ctx);
        const io: *cimgui.c.ImGuiIO = cimgui.c.ImGui_GetIO();
        cimgui.c.ImGuiIO_AddFocusEvent(io, focused);
    }

    pub fn textCallback(self: *Inspector, text: [:0]const u8) void {
        self.queueRender();
        cimgui.c.ImGui_SetCurrentContext(self.ig_ctx);
        const io: *cimgui.c.ImGuiIO = cimgui.c.ImGui_GetIO();
        cimgui.c.ImGuiIO_AddInputCharactersUTF8(io, text.ptr);
    }

    pub fn keyCallback(
        self: *Inspector,
        action: input.Action,
        key: input.Key,
        mods: input.Mods,
    ) !void {
        self.queueRender();
        cimgui.c.ImGui_SetCurrentContext(self.ig_ctx);
        const io: *cimgui.c.ImGuiIO = cimgui.c.ImGui_GetIO();

        // Update all our modifiers
        cimgui.c.ImGuiIO_AddKeyEvent(io, cimgui.c.ImGuiKey_LeftShift, mods.shift);
        cimgui.c.ImGuiIO_AddKeyEvent(io, cimgui.c.ImGuiKey_LeftCtrl, mods.ctrl);
        cimgui.c.ImGuiIO_AddKeyEvent(io, cimgui.c.ImGuiKey_LeftAlt, mods.alt);
        cimgui.c.ImGuiIO_AddKeyEvent(io, cimgui.c.ImGuiKey_LeftSuper, mods.super);

        // Send our keypress
        if (key.imguiKey()) |imgui_key| {
            cimgui.c.ImGuiIO_AddKeyEvent(
                io,
                imgui_key,
                action == .press or action == .repeat,
            );
        }
    }

    fn newFrame(self: *Inspector) !void {
        const io: *cimgui.c.ImGuiIO = cimgui.c.ImGui_GetIO();

        // Determine our delta time
        const now = try std.time.Instant.now();
        io.DeltaTime = if (self.instant) |prev| delta: {
            const since_ns: f64 = @floatFromInt(now.since(prev));
            const ns_per_s: f64 = @floatFromInt(std.time.ns_per_s);
            const since_s: f32 = @floatCast(since_ns / ns_per_s);
            break :delta @max(0.00001, since_s);
        } else (1.0 / 60.0);
        self.instant = now;
    }
};

// C API
pub const CAPI = struct {
    const global = &@import("../global.zig").state;

    /// This is the same as Surface.KeyEvent but this is the raw C API version.
    const KeyEvent = extern struct {
        action: input.Action,
        mods: c_int,
        consumed_mods: c_int,
        keycode: u32,
        text: ?[*:0]const u8,
        unshifted_codepoint: u32,
        composing: bool,

        /// Convert to Zig key event.
        fn keyEvent(self: KeyEvent) App.KeyEvent {
            return .{
                .action = self.action,
                .mods = @bitCast(@as(
                    input.Mods.Backing,
                    @truncate(@as(c_uint, @bitCast(self.mods))),
                )),
                .consumed_mods = @bitCast(@as(
                    input.Mods.Backing,
                    @truncate(@as(c_uint, @bitCast(self.consumed_mods))),
                )),
                .keycode = self.keycode,
                .text = if (self.text) |ptr| std.mem.sliceTo(ptr, 0) else null,
                .unshifted_codepoint = if (self.unshifted_codepoint != 0)
                    self.unshifted_codepoint
                else
                    unshiftedCodepointFromKeycode(self.keycode),
                .composing = self.composing,
            };
        }

        /// Derive the unshifted codepoint from a Win32 scancode via
        /// MapVirtualKeyW (scancode -> VK -> base character). Returns
        /// 0 on non-Windows.
        fn unshiftedCodepointFromKeycode(keycode: u32) u32 {
            if (comptime builtin.os.tag != .windows) return 0;

            const win32 = struct {
                const MAPVK_VSC_TO_VK_EX = 3;
                const MAPVK_VK_TO_CHAR = 2;
                extern "user32" fn MapVirtualKeyW(uCode: u32, uMapType: u32) callconv(.winapi) u32;
            };

            // Extended keys have 0xE000 prefix in our scancode
            // encoding. MapVirtualKeyW expects the raw scancode
            // with the extended bit in the high byte (0xE0xx).
            const vk = win32.MapVirtualKeyW(keycode, win32.MAPVK_VSC_TO_VK_EX);
            if (vk == 0) return 0;

            // Bit 31 set means dead key -- mask it off.
            var ch = win32.MapVirtualKeyW(vk, win32.MAPVK_VK_TO_CHAR) & 0x7FFFFFFF;

            // Lowercase A-Z to match the unshifted physical key.
            if (ch >= 'A' and ch <= 'Z') ch = ch - 'A' + 'a';

            return ch;
        }
    };

    const SurfaceSize = extern struct {
        columns: u16,
        rows: u16,
        width_px: u32,
        height_px: u32,
        cell_width_px: u32,
        cell_height_px: u32,
    };

    // ghostty_clipboard_content_s
    const ClipboardContent = extern struct {
        mime: [*:0]const u8,
        data: [*:0]const u8,
    };

    // ghostty_text_s
    const Text = extern struct {
        tl_px_x: f64,
        tl_px_y: f64,
        offset_start: u32,
        offset_len: u32,
        text: ?[*:0]const u8,
        text_len: usize,

        pub fn deinit(self: *Text) void {
            if (self.text) |ptr| {
                global.alloc.free(ptr[0..self.text_len :0]);
            }
        }
    };

    // Matches ghostty_config_color_s: separate r/g/b components (not a packed
    // value) for C-friendliness, consistent with the rest of the C API.
    const Color = extern struct {
        r: u8,
        g: u8,
        b: u8,

        fn from(rgb: terminal.color.RGB) Color {
            return .{ .r = rgb.r, .g = rgb.g, .b = rgb.b };
        }
    };

    // ghostty_cell_s: one resolved cell.
    const CellEntry = extern struct {
        codepoint: u32,
        fg: Color,
        bg: Color,
    };

    // ghostty_cells_s: the viewport as row-major resolved cells.
    const Cells = extern struct {
        cells: ?[*]CellEntry = null,
        rows: u16 = 0,
        cols: u16 = 0,

        pub fn deinit(self: *Cells) void {
            if (self.cells) |ptr| {
                const len: usize = @as(usize, self.rows) * @as(usize, self.cols);
                global.alloc.free(ptr[0..len]);
                self.cells = null;
            }
        }
    };

    // ghostty_point_s
    const Point = extern struct {
        tag: Tag,
        coord_tag: CoordTag,
        x: u32,
        y: u32,

        const Tag = enum(c_int) {
            active = 0,
            viewport = 1,
            screen = 2,
            history = 3,
        };

        const CoordTag = enum(c_int) {
            exact = 0,
            top_left = 1,
            bottom_right = 2,
        };

        fn pin(
            self: Point,
            screen: *const terminal.Screen,
        ) ?terminal.Pin {
            // The core point tag.
            const tag: terminal.point.Tag = switch (self.tag) {
                inline else => |tag| @field(
                    terminal.point.Tag,
                    @tagName(tag),
                ),
            };

            // Clamp our point to the screen bounds.
            const clamped_x = @min(self.x, screen.pages.cols -| 1);
            const clamped_y = @min(self.y, screen.pages.rows -| 1);

            return switch (self.coord_tag) {
                // Exact coordinates require a specific pin.
                .exact => exact: {
                    const pt_x = std.math.cast(
                        terminal.size.CellCountInt,
                        clamped_x,
                    ) orelse std.math.maxInt(terminal.size.CellCountInt);

                    const pt: terminal.Point = switch (tag) {
                        inline else => |v| @unionInit(
                            terminal.Point,
                            @tagName(v),
                            .{ .x = pt_x, .y = clamped_y },
                        ),
                    };

                    break :exact screen.pages.pin(pt) orelse null;
                },

                .top_left => screen.pages.getTopLeft(tag),

                .bottom_right => screen.pages.getBottomRight(tag),
            };
        }
    };

    // ghostty_selection_s
    const Selection = extern struct {
        tl: Point,
        br: Point,
        rectangle: bool,

        fn core(
            self: Selection,
            screen: *const terminal.Screen,
        ) ?terminal.Selection {
            return .{
                .bounds = .{ .untracked = .{
                    .start = self.tl.pin(screen) orelse return null,
                    .end = self.br.pin(screen) orelse return null,
                } },
                .rectangle = self.rectangle,
            };
        }
    };

    // Reference the conditional exports based on target platform
    // so they're included in the C API.
    comptime {
        if (builtin.target.os.tag.isDarwin()) {
            _ = Darwin;
        }
        if (builtin.os.tag == .windows) {
            _ = Windows;
        }
    }

    /// Create a new app.
    export fn ghostty_app_new(
        opts: *const apprt.runtime.App.Options,
        config: *const Config,
    ) ?*App {
        return app_new_(opts, config) catch |err| {
            log.err("error initializing app err={}", .{err});
            return null;
        };
    }

    fn app_new_(
        opts: *const apprt.runtime.App.Options,
        config: *const Config,
    ) !*App {
        const core_app = try CoreApp.create(global.alloc);
        errdefer core_app.destroy();

        // Create our runtime app
        var app = try global.alloc.create(App);
        errdefer global.alloc.destroy(app);
        try app.init(core_app, config, opts.*);
        errdefer app.terminate();

        return app;
    }

    /// Tick the event loop. This should be called whenever the "wakeup"
    /// callback is invoked for the runtime.
    export fn ghostty_app_tick(v: *App) void {
        v.core_app.tick(v) catch |err| {
            log.err("error app tick err={}", .{err});
        };
    }

    /// Return the userdata associated with the app.
    export fn ghostty_app_userdata(v: *App) ?*anyopaque {
        return v.opts.userdata;
    }

    export fn ghostty_app_free(v: *App) void {
        const core_app = v.core_app;
        v.terminate();
        global.alloc.destroy(v);
        core_app.destroy();
    }

    /// Update the focused state of the app.
    export fn ghostty_app_set_focus(
        app: *App,
        focused: bool,
    ) void {
        app.focusEvent(focused);
    }

    /// Notify the app of a global keypress capture. This will return
    /// true if the key was captured by the app, in which case the caller
    /// should not process the key.
    export fn ghostty_app_key(
        app: *App,
        event: KeyEvent,
    ) bool {
        return app.keyEvent(.app, event.keyEvent()) catch |err| {
            log.warn("error processing key event err={}", .{err});
            return false;
        };
    }

    /// Returns true if the given key event would trigger a binding
    /// if it were sent to the surface right now. The "right now"
    /// is important because things like trigger sequences are only
    /// valid until the next key event.
    export fn ghostty_config_key_is_binding(
        config: *Config,
        event: KeyEvent,
    ) bool {
        const core_event = event.keyEvent().core() orelse {
            log.warn("error processing key event", .{});
            return false;
        };

        return config.keyEventIsBinding(core_event);
    }

    /// Notify the app that the keyboard was changed. This causes the
    /// keyboard layout to be reloaded from the OS.
    export fn ghostty_app_keyboard_changed(v: *App) void {
        v.reloadKeymap() catch |err| {
            log.err("error reloading keyboard map err={}", .{err});
            return;
        };
    }

    /// Open the configuration.
    export fn ghostty_app_open_config(v: *App) void {
        _ = v.performAction(.app, .open_config, {}) catch |err| {
            log.err("error reloading config err={}", .{err});
            return;
        };
    }

    /// Update the configuration to the provided config. This will propagate
    /// to all surfaces as well.
    export fn ghostty_app_update_config(
        v: *App,
        config: *const Config,
    ) void {
        v.core_app.updateConfig(v, config) catch |err| {
            log.err("error updating config err={}", .{err});
            return;
        };
    }

    /// Returns true if the app needs to confirm quitting.
    export fn ghostty_app_needs_confirm_quit(v: *App) bool {
        return v.core_app.needsConfirmQuit();
    }

    /// Returns true if the app has global keybinds.
    export fn ghostty_app_has_global_keybinds(v: *App) bool {
        return v.hasGlobalKeybinds();
    }

    /// Update the color scheme of the app.
    export fn ghostty_app_set_color_scheme(v: *App, scheme_raw: c_int) void {
        const scheme = std.meta.intToEnum(apprt.ColorScheme, scheme_raw) catch {
            log.warn(
                "invalid color scheme to ghostty_surface_set_color_scheme value={}",
                .{scheme_raw},
            );
            return;
        };

        v.core_app.colorSchemeEvent(v, scheme) catch |err| {
            log.err("error setting color scheme err={}", .{err});
            return;
        };
    }

    /// Returns initial surface options.
    export fn ghostty_surface_config_new() apprt.Surface.Options {
        return .{};
    }

    /// Create a new surface as part of an app.
    export fn ghostty_surface_new(
        app: *App,
        opts: *const apprt.Surface.Options,
    ) ?*Surface {
        return surface_new_(app, opts) catch |err| {
            log.err("error initializing surface err={}", .{err});
            return null;
        };
    }

    fn surface_new_(
        app: *App,
        opts: *const apprt.Surface.Options,
    ) !*Surface {
        return try app.newSurface(opts.*);
    }

    export fn ghostty_surface_free(ptr: *Surface) void {
        ptr.app.closeSurface(ptr);
    }

    /// Returns the userdata associated with the surface.
    export fn ghostty_surface_userdata(surface: *Surface) ?*anyopaque {
        return surface.userdata;
    }

    /// Returns the app associated with a surface.
    export fn ghostty_surface_app(surface: *Surface) *App {
        return surface.app;
    }

    /// Returns the config to use for surfaces that inherit from this one.
    export fn ghostty_surface_inherited_config(
        surface: *Surface,
        source: apprt.surface.NewSurfaceContext,
    ) Surface.Options {
        return surface.newSurfaceOptions(source);
    }

    /// Update the configuration to the provided config for only this surface.
    export fn ghostty_surface_update_config(
        surface: *Surface,
        config: *const Config,
    ) void {
        surface.core_surface.updateConfig(config) catch |err| {
            log.err("error updating config err={}", .{err});
            return;
        };
    }

    /// Returns true if the surface needs to confirm quitting.
    export fn ghostty_surface_needs_confirm_quit(surface: *Surface) bool {
        return surface.core_surface.needsConfirmQuit();
    }

    /// Returns true if the surface process has exited.
    export fn ghostty_surface_process_exited(surface: *Surface) bool {
        return surface.core_surface.child_exited;
    }

    /// Returns true if the surface has a selection.
    export fn ghostty_surface_has_selection(surface: *Surface) bool {
        return surface.core_surface.hasSelection();
    }

    /// Same as ghostty_surface_read_text but reads from the user selection,
    /// if any.
    export fn ghostty_surface_read_selection(
        surface: *Surface,
        result: *Text,
    ) bool {
        const core_surface = &surface.core_surface;
        core_surface.renderer_state.mutex.lock();
        defer core_surface.renderer_state.mutex.unlock();

        // If we don't have a selection, do nothing.
        const core_sel = core_surface.io.terminal.screens.active.selection orelse return false;

        // Read the text from the selection.
        return readTextLocked(surface, core_sel, result);
    }

    /// Read some arbitrary text from the surface.
    ///
    /// This is an expensive operation so it shouldn't be called too
    /// often. We recommend that callers cache the result and throttle
    /// calls to this function.
    export fn ghostty_surface_read_text(
        surface: *Surface,
        sel: Selection,
        result: *Text,
    ) bool {
        surface.core_surface.renderer_state.mutex.lock();
        defer surface.core_surface.renderer_state.mutex.unlock();

        const core_sel = sel.core(
            surface.core_surface.renderer_state.terminal.screens.active,
        ) orelse return false;

        return readTextLocked(surface, core_sel, result);
    }

    fn readTextLocked(
        surface: *Surface,
        core_sel: terminal.Selection,
        result: *Text,
    ) bool {
        const core_surface = &surface.core_surface;

        // Get our text directly from the core surface.
        const text = core_surface.dumpTextLocked(
            global.alloc,
            core_sel,
        ) catch |err| {
            log.warn("error reading text err={}", .{err});
            return false;
        };

        const vp: CoreSurface.Text.Viewport = text.viewport orelse .{
            .tl_px_x = -1,
            .tl_px_y = -1,
            .offset_start = 0,
            .offset_len = 0,
        };

        result.* = .{
            .tl_px_x = vp.tl_px_x,
            .tl_px_y = vp.tl_px_y,
            .offset_start = vp.offset_start,
            .offset_len = vp.offset_len,
            .text = text.text.ptr,
            .text_len = text.text.len,
        };

        return true;
    }

    export fn ghostty_surface_free_text(_: *Surface, ptr: *Text) void {
        ptr.deinit();
    }

    /// Read the viewport cells with resolved colors. Used by the tab overview
    /// to render a colored preview. Expensive; callers should cache + throttle.
    export fn ghostty_surface_read_cells(
        surface: *Surface,
        result: *Cells,
    ) bool {
        const core_surface = &surface.core_surface;
        core_surface.renderer_state.mutex.lock();
        defer core_surface.renderer_state.mutex.unlock();
        return readCellsLocked(core_surface, result) catch |err| {
            log.warn("error reading cells err={}", .{err});
            return false;
        };
    }

    fn readCellsLocked(core_surface: *CoreSurface, result: *Cells) !bool {
        const alloc = global.alloc;

        // Build a render state for the current viewport. This resolves cell
        // colors (palette, default fg/bg, reverse mode) the same way the
        // renderer does, so we don't reimplement color logic here.
        var state: terminal.RenderState = .empty;
        defer state.deinit(alloc);
        try state.update(alloc, core_surface.renderer_state.terminal);

        // Iterate the populated row_data: update() resizes it to exactly the
        // viewport height, so its length is the authoritative row count here
        // (using it instead of state.rows keeps the loop self-evidently in bounds).
        const row_data = state.row_data.slice();
        const row_cells = row_data.items(.cells);
        const rows: usize = row_cells.len;
        const cols: usize = state.cols;
        if (rows == 0 or cols == 0) return false;

        const out = try alloc.alloc(CellEntry, rows * cols);
        errdefer alloc.free(out);

        // resolveCell does the shared, bounds-clamped per-cell resolution.
        for (0..rows) |y| {
            for (0..cols) |x| {
                const resolved = state.resolveCell(x, y);
                out[y * cols + x] = .{
                    .codepoint = resolved.codepoint,
                    .fg = Color.from(resolved.fg),
                    .bg = Color.from(resolved.bg),
                };
            }
        }

        result.* = .{
            .cells = out.ptr,
            .rows = @intCast(rows),
            .cols = @intCast(cols),
        };
        return true;
    }

    export fn ghostty_surface_free_cells(_: *Surface, ptr: *Cells) void {
        ptr.deinit();
    }

    /// Tell the surface that it needs to schedule a render
    export fn ghostty_surface_refresh(surface: *Surface) void {
        surface.refresh();
    }

    /// Tell the surface that it needs to schedule a render
    /// call as soon as possible (NOW if possible).
    export fn ghostty_surface_draw(surface: *Surface) void {
        surface.draw();
    }

    /// Write raw VT bytes into the surface's terminal parser. The bytes
    /// are processed as if they came from the PTY -- VT sequences update
    /// the terminal grid, cursor, colors, etc. Thread-safe.
    export fn ghostty_surface_vt_write(
        surface: *Surface,
        data: [*]const u8,
        len: usize,
    ) void {
        surface.core_surface.writeVt(data[0..len]);
    }

    /// Run the inline theme picker on a surface. Non-blocking: sets
    /// up picker state and input redirect, then returns. The
    /// theme_callback fires on preview (browsing) and confirm (Enter).
    /// The embedder must call ghostty_surface_list_themes_deinit once
    /// the picker signals should_quit. Returns null on error.
    export fn ghostty_surface_list_themes(
        surface: *Surface,
        theme_cb: ?*const fn ([*:0]const u8, bool) callconv(.c) void,
    ) ?*anyopaque {
        // Windows only: drives Surface.{input,scroll,resize}_redirect.
        if (comptime builtin.os.tag != .windows) return null;

        const picker_mod = @import("../cli/inline_theme_picker.zig");
        const alloc = global.alloc;

        // Discover themes using an arena.
        var arena = std.heap.ArenaAllocator.init(alloc);
        const arena_alloc = arena.allocator();

        const themes = picker_mod.discoverThemes(arena_alloc) catch return null;
        if (themes.len == 0) {
            arena.deinit();
            return null;
        }

        const grid = surface.core_surface.size.grid();

        // Write callback: feeds VT bytes into the surface's terminal.
        const write_cb = struct {
            fn write(ud: ?*anyopaque, data: [*]const u8, len: usize) void {
                const cs: *CoreSurface = @ptrCast(@alignCast(ud));
                cs.writeVt(data[0..len]);
            }
        }.write;

        const picker = picker_mod.InlineThemePicker.init(
            alloc,
            themes,
            arena,
            @intCast(grid.columns),
            @intCast(grid.rows),
            write_cb,
            @ptrCast(&surface.core_surface),
            theme_cb,
        ) catch {
            arena.deinit();
            return null;
        };

        // Set up input redirect so keys go to the picker.
        const input_cb = struct {
            fn handle(ud: ?*anyopaque, event: *const input.KeyEvent) bool {
                const p: *picker_mod.InlineThemePicker = @ptrCast(@alignCast(ud));
                return p.handleKey(event);
            }
        }.handle;

        surface.input_redirect = .{
            .callback = input_cb,
            .userdata = @ptrCast(picker),
        };

        // Set up scroll redirect so mouse wheel goes to the picker.
        const scroll_cb = struct {
            fn handle(ud: ?*anyopaque, yoff: f64) bool {
                const p: *picker_mod.InlineThemePicker = @ptrCast(@alignCast(ud));
                return p.handleScroll(yoff);
            }
        }.handle;

        surface.scroll_redirect = .{
            .callback = scroll_cb,
            .userdata = @ptrCast(picker),
        };

        // Set up resize redirect so the picker redraws on window resize.
        const resize_cb = struct {
            fn handle(ud: ?*anyopaque, cols: u16, rows: u16) void {
                const p: *picker_mod.InlineThemePicker = @ptrCast(@alignCast(ud));
                p.resize(cols, rows);
            }
        }.handle;

        surface.resize_redirect = .{
            .callback = resize_cb,
            .userdata = @ptrCast(picker),
        };

        // Enter alt screen and render initial frame.
        picker.enter();

        return @ptrCast(picker);
    }

    /// Check if the inline theme picker has finished (user confirmed
    /// or cancelled). Returns true if the picker wants to quit.
    export fn ghostty_surface_list_themes_should_quit(picker_ptr: ?*anyopaque) bool {
        const picker_mod = @import("../cli/inline_theme_picker.zig");
        const picker: *picker_mod.InlineThemePicker = @ptrCast(@alignCast(picker_ptr orelse return true));
        return picker.should_quit;
    }

    /// Clean up the inline theme picker and restore the surface.
    /// Must be called after the picker signals should_quit.
    export fn ghostty_surface_list_themes_deinit(
        surface: *Surface,
        picker_ptr: ?*anyopaque,
    ) void {
        // Windows only: drives Surface.{input,scroll,resize}_redirect.
        if (comptime builtin.os.tag != .windows) return;

        const picker_mod = @import("../cli/inline_theme_picker.zig");
        const picker: *picker_mod.InlineThemePicker = @ptrCast(@alignCast(picker_ptr orelse return));

        // Clear input, scroll, and resize redirects.
        surface.input_redirect = null;
        surface.scroll_redirect = null;
        surface.resize_redirect = null;

        // Exit alt screen and restore terminal.
        picker.exit();

        // Nudge the shell to redraw its prompt; it was blocked on
        // stdin while the picker ran in-process.
        surface.core_surface.writePtyInput("\r");

        picker.deinit();
    }

    /// Return the ID3D12Device* used by this surface's renderer. Shared
    /// texture consumers should call OpenSharedResource1 on this same
    /// device to avoid cross-device synchronization issues. Returns
    /// null on non-DX12 builds or before the device finishes init.
    export fn ghostty_surface_get_d3d12_device(surface: *Surface) ?*anyopaque {
        if (comptime builtin.os.tag != .windows) return null;
        const api = surface.core_surface.renderer.api;
        // Only the DX12 renderer has a `dev` field holding an ID3D12Device.
        if (comptime !@hasField(@TypeOf(api), "dev")) return null;
        const dev = api.dev orelse return null;
        return @ptrCast(dev.device);
    }

    /// Return the IDXGISwapChain1* used by this surface's renderer.
    /// Bind it to a Windows.UI.Composition visual via
    /// ICompositorInterop.CreateCompositionSurfaceForSwapChain.
    /// Returns null on non-DX12 or before the swap chain is created.
    export fn ghostty_surface_get_swap_chain(surface: *Surface) ?*anyopaque {
        if (comptime builtin.os.tag != .windows) return null;
        const api = surface.core_surface.renderer.api;
        if (comptime !@hasField(@TypeOf(api), "dev")) return null;
        const dev = api.dev orelse return null;
        const sc = dev.swap_chain orelse return null;
        return @ptrCast(sc);
    }

    /// Return the DirectComposition surface handle backing this surface's
    /// swap chain in SwapChainPanel mode. Bind it to a WinUI 3
    /// SwapChainPanel via ISwapChainPanelNative2::SetSwapChainHandle.
    /// Returns null on non-DX12 builds or when the surface is not in
    /// SwapChainPanel mode.
    export fn ghostty_surface_get_swap_chain_handle(surface: *Surface) ?*anyopaque {
        if (comptime builtin.os.tag != .windows) return null;
        const api = surface.core_surface.renderer.api;
        if (comptime !@hasField(@TypeOf(api), "dev")) return null;
        const dev = api.dev orelse return null;
        return @ptrCast(dev.swap_chain_surface_handle orelse return null);
    }

    /// Mirrors ghostty_surface_shared_texture_s in include/ghostty.h.
    const SharedTextureSnapshotC = extern struct {
        resource_handle: ?*anyopaque,
        fence_handle: ?*anyopaque,
        fence_value: u64,
        width: u32,
        height: u32,
        version: u64,
    };

    /// Fill `out` with an atomic snapshot of the shared-texture state
    /// for this surface. Returns false if the surface is not in
    /// shared-texture mode (in which case `out` is untouched).
    export fn ghostty_surface_shared_texture(
        surface: *Surface,
        out: *SharedTextureSnapshotC,
    ) bool {
        if (comptime builtin.os.tag != .windows) return false;
        const api_ptr = &surface.core_surface.renderer.api;
        if (comptime @TypeOf(api_ptr.*) != renderer.DirectX12) return false;
        if (api_ptr.dev == null) return false;
        const dev = &api_ptr.dev.?;

        dev.shared_texture_mutex.lock();
        defer dev.shared_texture_mutex.unlock();

        const st = dev.shared_texture orelse return false;

        out.* = .{
            .resource_handle = @ptrCast(st.resource_handle),
            .fence_handle = @ptrCast(st.fence_handle),
            .fence_value = dev.fence_value.load(.acquire),
            .width = st.width,
            .height = st.height,
            .version = st.version,
        };
        return true;
    }

    /// Update the size of a surface. This will trigger resize notifications
    /// to the pty and the renderer.
    export fn ghostty_surface_set_size(surface: *Surface, w: u32, h: u32) void {
        surface.updateSize(w, h);
        // For composition surfaces (no HWND), the renderer cannot query
        // the window size via GetClientRect. Forward the desired dimensions
        // so the resize detection loop in beginFrame picks up the change.
        surface.core_surface.renderer.setTargetSize(w, h);
        // Wake the renderer thread so it applies the new size in
        // beginFrame without waiting for the ~8ms draw-timer tick.
        // Single futex op, safe from any thread. The 120Hz draw timer
        // is the backstop if the wakeup is coalesced.
        surface.core_surface.renderer_thread.wakeup.notify() catch {};
    }

    /// Return the size information a surface has.
    export fn ghostty_surface_size(surface: *Surface) SurfaceSize {
        const grid_size = surface.core_surface.size.grid();
        return .{
            .columns = grid_size.columns,
            .rows = grid_size.rows,
            .width_px = surface.core_surface.size.screen.width,
            .height_px = surface.core_surface.size.screen.height,
            .cell_width_px = surface.core_surface.size.cell.width,
            .cell_height_px = surface.core_surface.size.cell.height,
        };
    }

    /// Returns the PID of the foreground process for the surface PTY.
    export fn ghostty_surface_foreground_pid(surface: *Surface) u64 {
        return surface.core_surface.getProcessInfo(.foreground_pid) orelse 0;
    }

    /// Returns the PTY name for the surface. The returned string must be
    /// freed by the caller via ghostty_string_free.
    export fn ghostty_surface_tty_name(surface: *Surface) String {
        const tty_name = surface.core_surface.getProcessInfo(.tty_name) orelse return .empty;
        const copy = surface.app.core_app.alloc.dupeZ(u8, tty_name) catch |err| {
            log.err("error allocating tty name err={}", .{err});
            return .empty;
        };

        return .fromSlice(copy);
    }

    /// Update the color scheme of the surface.
    export fn ghostty_surface_set_color_scheme(surface: *Surface, scheme_raw: c_int) void {
        const scheme = std.meta.intToEnum(apprt.ColorScheme, scheme_raw) catch {
            log.warn(
                "invalid color scheme to ghostty_surface_set_color_scheme value={}",
                .{scheme_raw},
            );
            return;
        };

        surface.colorSchemeCallback(scheme);
    }

    /// Update the content scale of the surface.
    export fn ghostty_surface_set_content_scale(surface: *Surface, x: f64, y: f64) void {
        surface.updateContentScale(x, y);
    }

    /// Update the focused state of a surface.
    export fn ghostty_surface_set_focus(surface: *Surface, focused: bool) void {
        surface.focusCallback(focused);
    }

    /// Update the occlusion state of a surface.
    export fn ghostty_surface_set_occlusion(surface: *Surface, visible: bool) void {
        surface.occlusionCallback(visible);
    }

    /// Filter the mods if necessary. This handles settings such as
    /// `macos-option-as-alt`. The filtered mods should be used for
    /// key translation but should NOT be sent back via the `_key`
    /// function -- the original mods should be used for that.
    export fn ghostty_surface_key_translation_mods(
        surface: *Surface,
        mods_raw: c_int,
    ) c_int {
        const mods: input.Mods = @bitCast(@as(
            input.Mods.Backing,
            @truncate(@as(c_uint, @bitCast(mods_raw))),
        ));
        const result = mods.translation(
            surface.core_surface.config.macos_option_as_alt orelse
                surface.app.keyboardLayout().detectOptionAsAlt(),
        );
        return @intCast(@as(input.Mods.Backing, @bitCast(result)));
    }

    /// Send this for raw keypresses (i.e. the keyDown event on macOS).
    /// This will handle the keymap translation and send the appropriate
    /// key and char events.
    ///
    /// On Windows, a press/repeat with no text is buffered until the
    /// following ghostty_surface_text attaches text, handling the split
    /// WM_KEYDOWN / WM_CHAR pattern so embedders don't combine manually.
    export fn ghostty_surface_key(
        surface: *Surface,
        event: KeyEvent,
    ) bool {
        const key_event = event.keyEvent();

        if (comptime builtin.os.tag == .windows) {
            // Theme picker etc. intercepts before keybinding resolution.
            if (surface.input_redirect) |redirect| {
                const core_event = key_event.core() orelse return false;
                if (redirect.callback(redirect.userdata, &core_event))
                    return true;
            }

            // Flush any prior pending key that never got text (arrows,
            // function keys, backspace).
            surface.flushPendingKey();

            // No text yet: buffer until ghostty_surface_text arrives.
            if (key_event.text == null and key_event.action != .release) {
                surface.pending_key = .{ .event = key_event };
                return false;
            }

            return surface.dispatchKey(key_event);
        }

        return surface.dispatchKey(key_event);
    }

    /// Returns true if the given key event would trigger a binding
    /// if it were sent to the surface right now. The "right now"
    /// is important because things like trigger sequences are only
    /// valid until the next key event.
    export fn ghostty_surface_key_is_binding(
        surface: *Surface,
        event: KeyEvent,
        c_flags: ?*input.Binding.Flags.C,
    ) bool {
        const core_event = event.keyEvent().core() orelse {
            log.warn("error processing key event", .{});
            return false;
        };

        const flags = surface.core_surface.keyEventIsBinding(
            core_event,
        ) orelse return false;
        if (c_flags) |ptr| ptr.* = flags.cval();
        return true;
    }

    /// Send raw text to the terminal. Treated as paste, unless on
    /// Windows there is a pending key event from ghostty_surface_key,
    /// in which case the text attaches to that key and dispatches
    /// through key encoding (WM_CHAR after WM_KEYDOWN).
    export fn ghostty_surface_text(
        surface: *Surface,
        ptr: [*]const u8,
        len: usize,
    ) void {
        if (comptime builtin.os.tag == .windows) {
            if (surface.pending_key) |*pending| {
                const text = ptr[0..len];

                // Don't attach C0 control characters. WM_CHAR delivers
                // the raw byte (e.g. 0x03 for Ctrl+C) but the key
                // encoder wants the printable character. Mirrors GTK.
                const is_c0 = text.len == 1 and (text[0] < 0x20 or text[0] == 0x7f);

                var event = pending.event;

                if (!is_c0) {
                    const copy_len: usize = @min(text.len, surface.pending_key_text.len - 1);

                    // Buffer lives outside the optional so the text
                    // survives setting pending_key to null (Zig poisons
                    // optional payloads in debug builds).
                    @memcpy(surface.pending_key_text[0..copy_len], text[0..copy_len]);
                    surface.pending_key_text[copy_len] = 0;

                    event.text = surface.pending_key_text[0..copy_len :0];
                }

                surface.pending_key = null;
                _ = surface.dispatchKey(event);
                return;
            }
        }

        surface.textCallback(ptr[0..len]);
    }

    /// Set the preedit text for the surface. This is used for IME
    /// composition. If the length is 0, then the preedit text is cleared.
    export fn ghostty_surface_preedit(
        surface: *Surface,
        ptr: [*]const u8,
        len: usize,
    ) void {
        surface.preeditCallback(if (len == 0) null else ptr[0..len]);
    }

    /// Returns true if the surface currently has mouse capturing
    /// enabled.
    export fn ghostty_surface_mouse_captured(surface: *Surface) bool {
        return surface.core_surface.mouseCaptured();
    }

    /// Tell the surface that it needs to schedule a render
    export fn ghostty_surface_mouse_button(
        surface: *Surface,
        action: input.MouseButtonState,
        button: input.MouseButton,
        mods: c_int,
    ) bool {
        return surface.mouseButtonCallback(
            action,
            button,
            @bitCast(@as(
                input.Mods.Backing,
                @truncate(@as(c_uint, @bitCast(mods))),
            )),
        );
    }

    /// Update the mouse position within the view.
    export fn ghostty_surface_mouse_pos(
        surface: *Surface,
        x: f64,
        y: f64,
        mods: c_int,
    ) void {
        surface.cursorPosCallback(
            x,
            y,
            @bitCast(@as(
                input.Mods.Backing,
                @truncate(@as(c_uint, @bitCast(mods))),
            )),
        );
    }

    export fn ghostty_surface_mouse_scroll(
        surface: *Surface,
        x: f64,
        y: f64,
        scroll_mods: c_int,
    ) void {
        surface.scrollCallback(
            x,
            y,
            @bitCast(@as(u8, @truncate(@as(c_uint, @bitCast(scroll_mods))))),
        );
    }

    export fn ghostty_surface_mouse_pressure(
        surface: *Surface,
        stage_raw: u32,
        pressure: f64,
    ) void {
        const stage = std.meta.intToEnum(
            input.MousePressureStage,
            stage_raw,
        ) catch {
            log.warn(
                "invalid mouse pressure stage value={}",
                .{stage_raw},
            );
            return;
        };

        surface.mousePressureCallback(stage, pressure);
    }

    export fn ghostty_surface_ime_point(
        surface: *Surface,
        x: *f64,
        y: *f64,
        width: *f64,
        height: *f64,
    ) void {
        const pos = surface.core_surface.imePoint();
        x.* = pos.x;
        y.* = pos.y;
        width.* = pos.width;
        height.* = pos.height;
    }

    /// Request that the surface become closed. This will go through the
    /// normal trigger process that a close surface input binding would.
    export fn ghostty_surface_request_close(ptr: *Surface) void {
        ptr.core_surface.close();
    }

    /// Request that the surface split in the given direction.
    export fn ghostty_surface_split(ptr: *Surface, direction: apprt.action.SplitDirection) void {
        _ = ptr.app.performAction(
            .{ .surface = &ptr.core_surface },
            .new_split,
            direction,
        ) catch |err| {
            log.err("error creating new split err={}", .{err});
            return;
        };
    }

    /// Focus on the next split (if any).
    export fn ghostty_surface_split_focus(
        ptr: *Surface,
        direction: apprt.action.GotoSplit,
    ) void {
        _ = ptr.app.performAction(
            .{ .surface = &ptr.core_surface },
            .goto_split,
            direction,
        ) catch |err| {
            log.err("error creating new split err={}", .{err});
            return;
        };
    }

    /// Resize the current split by moving the split divider in the given
    /// direction. `direction` specifies which direction the split divider will
    /// move relative to the focused split. `amount` is a fractional value
    /// between 0 and 1 that specifies by how much the divider will move.
    export fn ghostty_surface_split_resize(
        ptr: *Surface,
        direction: apprt.action.ResizeSplit.Direction,
        amount: u16,
    ) void {
        _ = ptr.app.performAction(
            .{ .surface = &ptr.core_surface },
            .resize_split,
            .{ .direction = direction, .amount = amount },
        ) catch |err| {
            log.err("error resizing split err={}", .{err});
            return;
        };
    }

    /// Equalize the size of all splits in the current window.
    export fn ghostty_surface_split_equalize(ptr: *Surface) void {
        _ = ptr.app.performAction(
            .{ .surface = &ptr.core_surface },
            .equalize_splits,
            {},
        ) catch |err| {
            log.err("error equalizing splits err={}", .{err});
            return;
        };
    }

    /// Invoke an action on the surface.
    export fn ghostty_surface_binding_action(
        ptr: *Surface,
        action_ptr: [*]const u8,
        action_len: usize,
    ) bool {
        const action_str = action_ptr[0..action_len];
        const action = input.Binding.Action.parse(action_str) catch |err| {
            log.err("error parsing binding action action={s} err={}", .{ action_str, err });
            return false;
        };

        return ptr.core_surface.performBindingAction(action) catch |err| {
            log.err("error performing binding action action={f} err={}", .{ action, err });
            return false;
        };
    }

    /// Complete a clipboard read request started via the read callback.
    /// This can only be called once for a given request. Once it is called
    /// with a request the request pointer will be invalidated.
    export fn ghostty_surface_complete_clipboard_request(
        ptr: *Surface,
        str: [*:0]const u8,
        state: *apprt.ClipboardRequest,
        confirmed: bool,
    ) void {
        ptr.completeClipboardRequest(
            std.mem.sliceTo(str, 0),
            state,
            confirmed,
        );
    }

    export fn ghostty_surface_inspector(ptr: *Surface) ?*Inspector {
        return ptr.initInspector() catch |err| {
            log.err("error initializing inspector err={}", .{err});
            return null;
        };
    }

    export fn ghostty_inspector_free(ptr: *Surface) void {
        ptr.freeInspector();
    }

    export fn ghostty_inspector_set_size(ptr: *Inspector, w: u32, h: u32) void {
        ptr.updateSize(w, h);
    }

    export fn ghostty_inspector_set_content_scale(ptr: *Inspector, x: f64, y: f64) void {
        ptr.updateContentScale(x, y);
    }

    /// Multiply the inspector's user zoom by `factor` (>1 zooms in, <1 out).
    export fn ghostty_inspector_zoom_by(ptr: *Inspector, factor: f64) void {
        ptr.zoomBy(factor);
    }

    /// Reset the inspector's user zoom to 1.0.
    export fn ghostty_inspector_zoom_reset(ptr: *Inspector) void {
        ptr.zoomReset();
    }

    export fn ghostty_inspector_mouse_button(
        ptr: *Inspector,
        action: input.MouseButtonState,
        button: input.MouseButton,
        mods: c_int,
    ) void {
        ptr.mouseButtonCallback(
            action,
            button,
            @bitCast(@as(
                input.Mods.Backing,
                @truncate(@as(c_uint, @bitCast(mods))),
            )),
        );
    }

    export fn ghostty_inspector_mouse_pos(ptr: *Inspector, x: f64, y: f64) void {
        ptr.cursorPosCallback(x, y);
    }

    export fn ghostty_inspector_mouse_scroll(
        ptr: *Inspector,
        x: f64,
        y: f64,
        scroll_mods: c_int,
    ) void {
        ptr.scrollCallback(
            x,
            y,
            @bitCast(@as(u8, @truncate(@as(c_uint, @bitCast(scroll_mods))))),
        );
    }

    export fn ghostty_inspector_key(
        ptr: *Inspector,
        action: input.Action,
        key: input.Key,
        c_mods: c_int,
    ) void {
        ptr.keyCallback(
            action,
            key,
            @bitCast(@as(
                input.Mods.Backing,
                @truncate(@as(c_uint, @bitCast(c_mods))),
            )),
        ) catch |err| {
            log.err("error processing key event err={}", .{err});
            return;
        };
    }

    export fn ghostty_inspector_text(
        ptr: *Inspector,
        str: [*:0]const u8,
    ) void {
        ptr.textCallback(std.mem.sliceTo(str, 0));
    }

    export fn ghostty_inspector_set_focus(ptr: *Inspector, focused: bool) void {
        ptr.focusCallback(focused);
    }

    /// Sets the window background blur on macOS to the desired value.
    /// I do this in Zig as an extern function because I don't know how to
    /// call these functions in Swift.
    ///
    /// This uses an undocumented, non-public API because this is what
    /// every terminal appears to use, including Terminal.app.
    export fn ghostty_set_window_background_blur(
        app: *App,
        window: *anyopaque,
    ) void {
        // This is only supported on macOS
        if (comptime builtin.target.os.tag != .macos) return;

        const config = &app.config;

        // Do nothing if we don't have background transparency enabled
        if (config.@"background-opacity" >= 1.0) return;

        const nswindow = objc.Object.fromId(window);
        _ = CGSSetWindowBackgroundBlurRadius(
            CGSDefaultConnectionForThread(),
            nswindow.msgSend(usize, objc.sel("windowNumber"), .{}),
            @intCast(config.@"background-blur".cval()),
        );
    }

    /// See ghostty_set_window_background_blur
    extern "c" fn CGSSetWindowBackgroundBlurRadius(*anyopaque, usize, c_int) i32;
    extern "c" fn CGSDefaultConnectionForThread() *anyopaque;

    // Darwin-only C APIs.
    const Darwin = struct {
        export fn ghostty_surface_set_display_id(ptr: *Surface, display_id: u32) void {
            const surface = &ptr.core_surface;
            _ = surface.renderer_thread.mailbox.push(
                .{ .macos_display_id = display_id },
                .{ .forever = {} },
            );
            surface.renderer_thread.wakeup.notify() catch {};
        }

        /// This returns a CTFontRef that should be used for quicklook
        /// highlighted text. This is always the primary font in use
        /// regardless of the selected text. If coretext is not in use
        /// then this will return nothing.
        export fn ghostty_surface_quicklook_font(ptr: *Surface) ?*anyopaque {
            // For non-CoreText we just return null.
            if (comptime font.options.backend != .coretext) {
                return null;
            }

            // We'll need content scale so fail early if we can't get it.
            const content_scale = ptr.getContentScale() catch return null;

            // Get the shared font grid. We acquire a read lock to
            // read the font face. It should not be deferred since
            // we're loading the primary face.
            const grid = ptr.core_surface.renderer.font_grid;
            grid.lock.lockShared();
            defer grid.lock.unlockShared();

            const collection = &grid.resolver.collection;
            const face = collection.getFace(.{}) catch return null;

            // We need to unscale the content scale. We apply the
            // content scale to our font stack because we are rendering
            // at 1x but callers of this should be using scaled or apply
            // scale themselves.
            const size: f32 = size: {
                const num = face.font.copyAttribute(.size) orelse
                    break :size 12;
                defer num.release();
                var v: f32 = 12;
                _ = num.getValue(.float, &v);
                break :size v;
            };

            const copy = face.font.copyWithAttributes(
                size / content_scale.y,
                null,
                null,
            ) catch return null;

            return copy;
        }

        /// This returns the selected word for quicklook. This will populate
        /// the buffer with the word under the cursor and the selection
        /// info so that quicklook can be rendered.
        ///
        /// This does not modify the selection active on the surface (if any).
        export fn ghostty_surface_quicklook_word(
            ptr: *Surface,
            result: *Text,
        ) bool {
            const surface = &ptr.core_surface;
            surface.renderer_state.mutex.lock();
            defer surface.renderer_state.mutex.unlock();

            // Get our word selection
            const sel = sel: {
                const screen: *terminal.Screen = surface.renderer_state.terminal.screens.active;
                const pos = try ptr.getCursorPos();
                const pt_viewport = surface.posToViewport(pos.x, pos.y);
                const pin = screen.pages.pin(.{
                    .viewport = .{
                        .x = pt_viewport.x,
                        .y = pt_viewport.y,
                    },
                }) orelse {
                    if (comptime std.debug.runtime_safety) unreachable;
                    return false;
                };
                break :sel surface.io.terminal.screens.active.selectWord(
                    pin,
                    surface.config.selection_word_chars,
                ) orelse return false;
            };

            // Read the selection
            return readTextLocked(ptr, sel, result);
        }

        export fn ghostty_inspector_metal_init(ptr: *Inspector, device: objc.c.id) bool {
            return ptr.initMetal(.fromId(device));
        }

        export fn ghostty_inspector_metal_render(
            ptr: *Inspector,
            command_buffer: objc.c.id,
            descriptor: objc.c.id,
        ) void {
            return ptr.renderMetal(
                .fromId(command_buffer),
                .fromId(descriptor),
            ) catch |err| {
                log.err("error rendering inspector err={}", .{err});
                return;
            };
        }

        export fn ghostty_inspector_metal_shutdown(ptr: *Inspector) void {
            if (ptr.backend) |v| {
                v.deinit();
                ptr.backend = null;
            }
        }
    };

    // Windows-only C APIs.
    const Windows = struct {
        export fn ghostty_inspector_directx12_init(
            ptr: *Inspector,
            device: ?*anyopaque,
            command_queue: ?*anyopaque,
            num_frames: u32,
            rtv_format: u32,
        ) bool {
            return ptr.initDirectX12(
                device orelse return false,
                command_queue orelse return false,
                num_frames,
                rtv_format,
            );
        }

        export fn ghostty_inspector_directx12_render(
            ptr: *Inspector,
            command_list: ?*anyopaque,
        ) void {
            return ptr.renderDirectX12(command_list orelse return) catch |err| {
                log.err("error rendering inspector err={}", .{err});
                return;
            };
        }

        export fn ghostty_inspector_directx12_shutdown(ptr: *Inspector) void {
            if (ptr.backend) |v| {
                v.deinit();
                ptr.backend = null;
            }
            ptr.deinitDx12Heap();
        }
    };
};
