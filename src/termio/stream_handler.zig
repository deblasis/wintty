const std = @import("std");
const builtin = @import("builtin");
const assert = @import("../quirks.zig").inlineAssert;
const Allocator = std.mem.Allocator;
const global = @import("../global.zig");
const xev = global.xev;
const App = @import("../App.zig");
const apprt = @import("../apprt.zig");
const build_config = @import("../build_config.zig");
const configpkg = @import("../config.zig");
const internal_os = @import("../os/main.zig");
const renderer = @import("../renderer.zig");
const termio = @import("../termio.zig");
const terminal = @import("../terminal/main.zig");
const terminfo = @import("../terminfo/main.zig");
const iterm2_parser = @import("../terminal/osc/parsers/iterm2.zig");
const posix = std.posix;

const log = std.log.scoped(.io_handler);
const log_validate = std.log.scoped(.validate_transport);

/// This is used as the handler for the terminal.Stream type. This is
/// stateful and is expected to live for the entire lifetime of the terminal.
/// It is NOT VALID to stop a stream handler, create a new one, and use that
/// unless all of the member fields are copied.
pub const StreamHandler = struct {
    alloc: Allocator,
    size: *renderer.Size,
    terminal: *terminal.Terminal,

    /// Mailbox for data to the termio thread.
    termio_mailbox: *termio.Mailbox,

    /// Mailbox for the surface.
    surface_mailbox: apprt.surface.Mailbox,

    /// Set once we have flagged the render state that this surface has
    /// produced its first cell of content, so we flag it at most once.
    /// The renderer emits the actual `.first_render` signal after that
    /// content is painted. Surface-scoped: never reset.
    first_content_flagged: bool = false,

    /// The shared render state
    renderer_state: *renderer.State,

    /// The mailbox for notifying the renderer of things.
    renderer_mailbox: *renderer.Thread.Mailbox,

    /// A handle to wake up the renderer. This hints to the renderer that
    /// a repaint should happen. See termio.Options for why this is a pointer.
    renderer_wakeup: *xev.Async,

    /// The renderer's published visibility. Output-driven wakes are
    /// dropped while it reads false; see termio.Options.
    renderer_visible: ?*const std.atomic.Value(bool),

    /// The response to use for ENQ requests. The memory is owned by
    /// whoever owns StreamHandler.
    enquiry_response: []const u8,

    /// OSC 7 path-translation context (WSL UNC, or MSYS2/Git/Cygwin install
    /// root). null for non-POSIX (and non-Windows) surfaces. Owns the duped
    /// distro/install_root string (freed in deinit).
    osc7: ?internal_os.windows_shell.Osc7Context = null,

    /// The color reporting format for OSC requests.
    osc_color_report_format: configpkg.Config.OSCColorReportFormat,

    /// The clipboard write access configuration.
    clipboard_write: configpkg.ClipboardAccess,

    /// Maximum total decoded bytes per Kitty clipboard protocol
    /// (OSC 5522) write transaction; exceeding it aborts with EFBIG.
    clipboard_write_limit: usize,

    //---------------------------------------------------------------
    // Internal state

    /// The APC command handler maintains the APC state. APC is like
    /// CSI or OSC, but it is a private escape sequence that is used
    /// to send commands to the terminal emulator. This is used by
    /// the kitty graphics protocol.
    apc: terminal.apc.Handler = .{},

    /// The DCS handler maintains DCS state. DCS is like CSI or OSC,
    /// but requires more stateful parsing. This is used by functionality
    /// such as XTGETTCAP.
    dcs: terminal.dcs.Handler = .{},

    /// In-flight iTerm2 multipart File= image accumulation. iTerm2 has
    /// no session identifier so transfers are strictly serialized;
    /// this carries the active transfer's hints + buffer between OSC
    /// sequences.
    multipart_iterm2: iterm2_parser.Iterm2MultipartAssembler = .{},

    /// The tmux control mode viewer state.
    tmux_viewer: if (tmux_enabled) ?*terminal.tmux.Viewer else void = if (tmux_enabled) null else {},

    /// Session password grants for the Kitty clipboard protocol.
    /// Requests carrying a granted password skip the permission prompt.
    kitty_clipboard_grants: terminal.kitty.clipboard.Grants = .{},

    /// The in-flight Kitty clipboard protocol (OSC 5522) write
    /// transaction, if any.
    kitty_clipboard_write: ?*terminal.kitty.clipboard.WriteState = null,

    /// This is set to true when a message was written to the termio
    /// mailbox. This can be used by callers to determine if they need
    /// to wake up the termio thread.
    termio_messaged: bool = false,

    /// This is set to true when we've seen a title escape sequence. We use
    /// this to determine if we need to default the window title.
    seen_title: bool = false,

    /// Whether a pwd has ever been reported to the surface from this
    /// handler. The terminal's pwd slot is pre-seeded at spawn from the
    /// subprocess's own working directory with no surface message behind it
    /// (see `Exec.initTerminal`), so a first prompt reporting that same
    /// directory matches a value the surface was never told. Without this,
    /// the dedupe below would swallow the first `pwd_change` of the session,
    /// and an app that treats that first one as the "shell is alive and
    /// reporting" edge would never see it.
    pwd_reported: bool = false,

    pub const Stream = terminal.Stream(StreamHandler);

    /// True if we have tmux control mode built in.
    pub const tmux_enabled = terminal.options.tmux_control_mode;

    pub fn deinit(self: *StreamHandler) void {
        self.apc.deinit();
        self.dcs.deinit();
        self.kittyClipboardWriteAbort();
        self.kitty_clipboard_grants.deinit(self.alloc);
        self.multipart_iterm2.deinit(self.alloc);
        if (self.osc7) |c| switch (c) {
            .wsl => |w| if (w.distro) |d| self.alloc.free(d),
            .rooted => |r| if (r.install_root) |s| self.alloc.free(s),
        };
        if (comptime tmux_enabled) tmux: {
            const viewer = self.tmux_viewer orelse break :tmux;
            viewer.deinit();
            self.alloc.destroy(viewer);
            self.tmux_viewer = null;
        }
    }

    /// This queues a render operation with the renderer thread. The render
    /// isn't guaranteed to happen immediately but it will happen as soon as
    /// practical.
    pub inline fn queueRender(self: *StreamHandler) !void {
        // Output nobody can see is not worth a thread wake: while the
        // renderer is hidden this is the entire cost of the frame, and
        // the visibility transition rebuilds from terminal state.
        if (self.renderer_visible) |v| {
            if (!v.load(.acquire)) return;
        }
        try self.renderer_wakeup.notify();
    }

    /// Change the configuration for this handler.
    pub fn changeConfig(self: *StreamHandler, config: *termio.DerivedConfig) void {
        self.osc_color_report_format = config.osc_color_report_format;
        self.clipboard_write = config.clipboard_write;
        self.clipboard_write_limit = config.clipboard_write_limit;
        self.enquiry_response = config.enquiry_response;
        self.terminal.setDefaultCursorStyle(config.cursor_style);
        self.terminal.setDefaultCursorBlink(config.cursor_blink);

        // The config could have changed any of our colors so update mode 2031
        self.messageWriter(.{ .color_scheme_report = .{ .force = false } });
    }

    inline fn surfaceMessageWriter(
        self: *StreamHandler,
        msg: apprt.surface.Message,
    ) void {
        // See messageWriter which has similar logic and explains why
        // we may have to do this.
        if (self.surface_mailbox.push(msg, .{ .instant = {} }) == 0) {
            self.renderer_state.mutex.unlock(global.io());
            defer self.renderer_state.mutex.lockUncancelable(global.io());
            _ = self.surface_mailbox.push(msg, .{ .forever = {} });
        }
    }

    inline fn messageWriter(self: *StreamHandler, msg: termio.Message) void {
        self.termio_mailbox.send(msg, self.renderer_state.mutex);
        self.termio_messaged = true;
    }

    /// Send a renderer message and unlock the renderer state mutex
    /// if necessary to ensure we don't deadlock.
    ///
    /// This assumes the renderer state mutex is locked.
    inline fn rendererMessageWriter(
        self: *StreamHandler,
        msg: renderer.Message,
    ) void {
        // See termio.Mailbox.send for more details on how this works.

        // Try instant first. If it works then we can return.
        if (self.renderer_mailbox.push(msg, .{ .instant = {} }) > 0) {
            return;
        }

        // Instant would have blocked. Release the renderer mutex,
        // wake up the renderer to allow it to process the message,
        // and then try again.
        self.renderer_state.mutex.unlock(global.io());
        defer self.renderer_state.mutex.lockUncancelable(global.io());
        self.renderer_wakeup.notify() catch |err| {
            // This is an EXTREMELY unlikely case. We still don't return
            // and attempt to send the message because its most likely
            // that everything is fine, but log in case a freeze happens.
            log.warn(
                "failed to notify renderer, may deadlock err={}",
                .{err},
            );
        };
        _ = self.renderer_mailbox.push(msg, .{ .forever = {} });
    }

    pub fn vt(
        self: *StreamHandler,
        comptime action: Stream.Action.Tag,
        value: Stream.Action.Value(action),
    ) void {
        self.vtFallible(action, value) catch |err| {
            log.warn("error handling VT action action={} err={}", .{ action, err });
        };
    }

    inline fn vtFallible(
        self: *StreamHandler,
        comptime action: Stream.Action.Tag,
        value: Stream.Action.Value(action),
    ) !void {
        // The branch hints here are based on real world data
        // which indicates that the most common actions are:
        //
        // 1. print
        // 2. set_attribute
        // 3. carriage_return
        // 4. line_feed
        // 5. cursor_pos
        //
        // Together, these 5 actions make up nearly 98% of
        // all actions encountered in real world scenarios.
        //
        // ref: https://github.com/qwerasd205/asciinema-stats
        switch (action) {
            .print => {
                @branchHint(.likely);
                try self.terminal.print(value.cp);
                if (!self.first_content_flagged) {
                    self.first_content_flagged = true;
                    // We hold `renderer_state.mutex` for the duration of
                    // action dispatch (see `surfaceMessageWriter`), so we can
                    // flag the shared render state directly. The renderer then
                    // emits the one-shot `.first_render` signal once this
                    // content is actually painted, rather than us emitting it
                    // here on first parse (which can race ahead of the paint
                    // when several surfaces start at once).
                    self.renderer_state.first_content = true;
                }
            },
            .print_slice => {
                @branchHint(.likely);
                try self.terminal.printSlice(value.cps);
                if (!self.first_content_flagged) {
                    self.first_content_flagged = true;
                    self.renderer_state.first_content = true;
                }
            },
            .print_repeat => try self.terminal.printRepeat(value),
            .bell => self.bell(),
            .backspace => self.terminal.backspace(),
            .horizontal_tab => self.horizontalTab(value),
            .horizontal_tab_back => self.horizontalTabBack(value),
            .linefeed => {
                @branchHint(.likely);
                try self.linefeed();
            },
            .carriage_return => {
                @branchHint(.likely);
                self.terminal.carriageReturn();
            },
            .enquiry => try self.enquiry(),
            .invoke_charset => self.terminal.invokeCharset(value.bank, value.charset, value.locking),
            .cursor_up => self.terminal.cursorUp(value.value),
            .cursor_down => self.terminal.cursorDown(value.value),
            .cursor_left => self.terminal.cursorLeft(value.value),
            .cursor_right => self.terminal.cursorRight(value.value),
            .cursor_pos => {
                @branchHint(.likely);
                self.terminal.setCursorPos(value.row, value.col);
            },
            .cursor_col => self.terminal.setCursorPos(self.terminal.screens.active.cursor.y + 1, value.value),
            .cursor_row => self.terminal.setCursorPos(value.value, self.terminal.screens.active.cursor.x + 1),
            .cursor_col_relative => self.terminal.setCursorPos(
                self.terminal.screens.active.cursor.y + 1,
                self.terminal.screens.active.cursor.x + 1 +| value.value,
            ),
            .cursor_row_relative => self.terminal.setCursorPos(
                self.terminal.screens.active.cursor.y + 1 +| value.value,
                self.terminal.screens.active.cursor.x + 1,
            ),
            .cursor_style => self.terminal.setCursorStyle(value),
            .erase_display_below => self.terminal.eraseDisplay(.below, value),
            .erase_display_above => self.terminal.eraseDisplay(.above, value),
            .erase_display_complete => {
                self.terminal.scrollViewport(.{ .bottom = {} });
                self.terminal.eraseDisplay(.complete, value);
            },
            .erase_display_scrollback => self.terminal.eraseDisplay(.scrollback, value),
            .erase_display_scroll_complete => self.terminal.eraseDisplay(.scroll_complete, value),
            .erase_line_right => self.terminal.eraseLine(.right, value),
            .erase_line_left => self.terminal.eraseLine(.left, value),
            .erase_line_complete => self.terminal.eraseLine(.complete, value),
            .erase_line_right_unless_pending_wrap => self.terminal.eraseLine(.right_unless_pending_wrap, value),
            .delete_chars => self.terminal.deleteChars(value),
            .erase_chars => self.terminal.eraseChars(value),
            .insert_lines => self.terminal.insertLines(value),
            .insert_blanks => self.terminal.insertBlanks(value),
            .delete_lines => self.terminal.deleteLines(value),
            .scroll_up => try self.terminal.scrollUp(value),
            .scroll_down => self.terminal.scrollDown(value),
            .tab_clear_current => self.terminal.tabClear(.current),
            .tab_clear_all => self.terminal.tabClear(.all),
            .tab_set => self.terminal.tabSet(),
            .tab_reset => self.terminal.tabReset(),
            .index => try self.index(),
            .next_line => try self.nextLine(),
            .reverse_index => try self.reverseIndex(),
            .full_reset => try self.fullReset(),
            .set_mode => try self.setMode(value.mode, true),
            .reset_mode => try self.setMode(value.mode, false),
            .save_mode => self.terminal.modes.save(value.mode),
            .restore_mode => {
                // For restore mode we have to restore but if we set it, we
                // always have to call setMode because setting some modes have
                // side effects and we want to make sure we process those.
                const v = self.terminal.modes.restore(value.mode);
                try self.setMode(value.mode, v);
            },
            .request_mode => try self.requestMode(value.mode),
            .request_mode_unknown => try self.requestModeUnknown(value.mode, value.ansi),
            .top_and_bottom_margin => self.terminal.setTopAndBottomMargin(value.top_left, value.bottom_right),
            .left_and_right_margin => self.terminal.setLeftAndRightMargin(value.top_left, value.bottom_right),
            .left_and_right_margin_ambiguous => {
                if (self.terminal.modes.get(.enable_left_and_right_margin)) {
                    self.terminal.setLeftAndRightMargin(0, 0);
                } else {
                    self.terminal.saveCursor();
                }
            },
            .save_cursor => try self.saveCursor(),
            .restore_cursor => try self.restoreCursor(),
            .modify_key_format => try self.setModifyKeyFormat(value),
            .protected_mode_off => self.terminal.setProtectedMode(.off),
            .protected_mode_iso => self.terminal.setProtectedMode(.iso),
            .protected_mode_dec => self.terminal.setProtectedMode(.dec),
            .mouse_shift_capture => self.terminal.flags.mouse_shift_capture = if (value) .true else .false,
            .size_report => self.sendSizeReport(value),
            .xtversion => try self.reportXtversion(),
            .device_attributes => try self.deviceAttributes(value),
            .device_status => try self.deviceStatusReport(value.request),
            .kitty_keyboard_query => try self.queryKittyKeyboard(),
            .kitty_keyboard_push => {
                log.debug("pushing kitty keyboard mode: {}", .{value.flags});
                self.terminal.screens.active.kitty_keyboard.push(value.flags);
            },
            .kitty_keyboard_pop => {
                log.debug("popping kitty keyboard mode n={}", .{value});
                self.terminal.screens.active.kitty_keyboard.pop(@intCast(value));
            },
            .kitty_keyboard_set => {
                log.debug("setting kitty keyboard mode: set {}", .{value.flags});
                self.terminal.screens.active.kitty_keyboard.set(.set, value.flags);
            },
            .kitty_keyboard_set_or => {
                log.debug("setting kitty keyboard mode: or {}", .{value.flags});
                self.terminal.screens.active.kitty_keyboard.set(.@"or", value.flags);
            },
            .kitty_keyboard_set_not => {
                log.debug("setting kitty keyboard mode: not {}", .{value.flags});
                self.terminal.screens.active.kitty_keyboard.set(.not, value.flags);
            },
            .kitty_color_report => try self.kittyColorReport(value),
            .color_operation => try self.colorOperation(value.op, &value.requests, value.terminator),
            .end_hyperlink => try self.endHyperlink(),
            .active_status_display => self.terminal.status_display = value,
            .decaln => try self.decaln(),
            .window_title => try self.windowTitle(value.title),
            .report_pwd => try self.reportPwd(value.url),
            .show_desktop_notification => try self.showDesktopNotification(value.title, value.body),
            .progress_report => self.progressReport(value),
            .start_hyperlink => try self.startHyperlink(value.uri, value.id),
            .clipboard_contents => try self.clipboardContents(value.kind, value.data),
            .semantic_prompt => try self.semanticPrompt(value),
            .mouse_shape => try self.setMouseShape(value),
            .configure_charset => self.configureCharset(value.slot, value.charset),
            .set_attribute => {
                @branchHint(.likely);
                switch (value) {
                    .unknown => |unk| {
                        // We optimize for the happy path scenario here, since
                        // unknown/invalid SGRs aren't that common in the wild.
                        @branchHint(.unlikely);
                        log.warn("unimplemented or unknown SGR attribute: {any}", .{unk});
                    },
                    else => {
                        @branchHint(.likely);
                        self.terminal.setAttribute(value) catch |err| {
                            @branchHint(.cold);
                            log.warn("error setting attribute {}: {}", .{ value, err });
                        };
                    },
                }
            },
            .dcs_hook => try self.dcsHook(value),
            .dcs_put => try self.dcsPut(value),
            .dcs_unhook => try self.dcsUnhook(),
            .apc_start => self.apc.start(),
            .apc_end => try self.apcEnd(),
            .apc_put => self.apc.feed(self.alloc, value),
            .apc_put_slice => self.apc.feedSlice(self.alloc, value.bytes),
            .kitty_clipboard => try self.kittyClipboard(value),
            .iterm2_image_transmit => try self.iterm2ImageTransmit(value),
            .iterm2_multipart_image => try self.iterm2MultipartImage(value),

            // Unimplemented
            .title_push,
            .title_pop,
            .kitty_dnd,
            => {},
        }
    }

    pub inline fn dcsHook(self: *StreamHandler, dcs: terminal.DCS) !void {
        var cmd = self.dcs.hook(self.alloc, dcs) orelse return;
        defer cmd.deinit();
        try self.dcsCommand(&cmd);
    }

    pub inline fn dcsPut(self: *StreamHandler, byte: u8) !void {
        var cmd = self.dcs.put(byte) orelse return;
        defer cmd.deinit();
        try self.dcsCommand(&cmd);
    }

    pub inline fn dcsUnhook(self: *StreamHandler) !void {
        var cmd = self.dcs.unhook() orelse return;
        defer cmd.deinit();
        try self.dcsCommand(&cmd);
    }

    fn dcsCommand(self: *StreamHandler, cmd: *terminal.dcs.Command) !void {
        // log.warn("DCS command: {}", .{cmd});
        switch (cmd.*) {
            .tmux => |tmux| tmux: {
                // If tmux control mode is disabled at the build level,
                // then this whole block shouldn't be analyzed.
                if (comptime !tmux_enabled) break :tmux;
                log.info("tmux control mode event cmd={f}", .{tmux});

                switch (tmux) {
                    .enter => {
                        // Setup our viewer state
                        assert(self.tmux_viewer == null);
                        const viewer = try self.alloc.create(terminal.tmux.Viewer);
                        errdefer self.alloc.destroy(viewer);
                        viewer.* = try .init(global.io(), self.alloc);
                        errdefer viewer.deinit();
                        self.tmux_viewer = viewer;
                        break :tmux;
                    },

                    .exit => {
                        // Free our viewer state if we have one
                        if (self.tmux_viewer) |viewer| {
                            viewer.deinit();
                            self.alloc.destroy(viewer);
                            self.tmux_viewer = null;
                        }

                        // And always break since we assert below
                        // that we're not handling an exit command.
                        break :tmux;
                    },

                    else => {},
                }

                assert(tmux != .enter);
                assert(tmux != .exit);

                const viewer = self.tmux_viewer orelse {
                    // This can only really happen if we failed to
                    // initialize the viewer on enter.
                    log.info(
                        "received tmux control mode command without viewer: {f}",
                        .{tmux},
                    );

                    break :tmux;
                };

                for (viewer.next(.{ .tmux = tmux })) |action| {
                    log.info("tmux viewer action={f}", .{action});
                    switch (action) {
                        .exit => {
                            // We ignore this because we will fully exit when
                            // our DCS connection ends. We may want to handle
                            // this in the future to notify our GUI we're
                            // disconnected though.
                        },

                        .command => |command| {
                            assert(command.len > 0);
                            assert(command[command.len - 1] == '\n');
                            const msg = try termio.Message.writeReq(
                                self.alloc,
                                command,
                            );
                            self.messageWriter(msg);
                        },

                        .windows => {
                            // TODO
                        },
                    }
                }
            },

            .xtgettcap => |*gettcap| {
                const map = comptime terminfo.ghostty.xtgettcapMap();
                while (gettcap.next()) |key| {
                    const response = map.get(key) orelse continue;
                    self.messageWriter(.{ .write_stable = response });
                }
            },

            .decrqss => |decrqss| {
                var response: [terminal.dcs.Command.DECRQSS.max_response_bytes]u8 = undefined;
                const encoded = try decrqss.encode(self.terminal, &response);
                const msg = try termio.Message.writeReq(
                    self.alloc,
                    encoded,
                );
                self.messageWriter(msg);
            },

            .sixel => |sixel_cmd| {
                // Sixel decode + kitty graphics dispatch. The bridge
                // owns the decoded RGBA and hands it off to kitty.
                // TODO: thread terminal bg color into ctx.bg so P1
                // mode renders against the actual background instead
                // of opaque black.
                var kcmd = terminal.sixel.synthKittyCommand(
                    self.alloc,
                    sixel_cmd,
                    .{},
                ) catch |err| {
                    switch (err) {
                        // Expected no-render paths — empty stream,
                        // oversized geometry already dropped upstream.
                        error.EmptyImage,
                        error.SixelTooLarge,
                        => log.debug("sixel skipped: {t}", .{err}),
                        // Unexpected.
                        error.OutOfMemory,
                        => log.warn("sixel dispatch failed: {t}", .{err}),
                    }
                    return;
                };
                defer kcmd.deinit(self.alloc);
                // Sixel has no response channel; drop any kitty
                // response (DEC didn't define a query path for sixel).
                _ = self.terminal.kittyGraphics(global.io(), self.alloc, &kcmd);
            },
        }
    }

    pub fn apcEnd(self: *StreamHandler) !void {
        var result = self.apc.end() orelse return;
        defer result.deinit(self.alloc);

        // log.warn("APC command: {}", .{result});
        switch (result) {
            .unknown => return,
            .kitty => |*kitty_cmd| {
                if (self.terminal.kittyGraphics(global.io(), self.alloc, kitty_cmd)) |resp| {
                    var buf: [1024]u8 = undefined;
                    var writer: std.Io.Writer = .fixed(&buf);
                    try resp.encode(&writer);
                    const final = writer.buffered();
                    if (final.len > 2) {
                        log.debug("kitty graphics response: {x}", .{final});
                        const msg = try termio.Message.writeReq(self.alloc, final);
                        self.messageWriter(msg);
                    }
                }
            },

            .glyph => |*glyph_req| {
                const resp = self.terminal.glyphProtocol(self.alloc, glyph_req);
                switch (glyph_req.*) {
                    .register, .clear => try self.queueRender(),
                    .support, .query => {},
                }

                if (resp) |r| {
                    var buf: [terminal.apc.glyph.Response.max_wire_bytes]u8 = undefined;
                    var writer: std.Io.Writer = .fixed(&buf);
                    try r.formatWire(&writer);
                    const final = writer.buffered();
                    log.debug("glyph protocol response: {x}", .{final});
                    self.messageWriter(try termio.Message.writeReq(self.alloc, final));
                }
            },
        }
    }

    inline fn bell(self: *StreamHandler) void {
        self.surfaceMessageWriter(.ring_bell);
    }

    inline fn horizontalTab(self: *StreamHandler, count: u16) void {
        for (0..count) |_| {
            const x = self.terminal.screens.active.cursor.x;
            self.terminal.horizontalTab();
            if (x == self.terminal.screens.active.cursor.x) break;
        }
    }

    inline fn horizontalTabBack(self: *StreamHandler, count: u16) void {
        for (0..count) |_| {
            const x = self.terminal.screens.active.cursor.x;
            self.terminal.horizontalTabBack();
            if (x == self.terminal.screens.active.cursor.x) break;
        }
    }

    inline fn linefeed(self: *StreamHandler) !void {
        // Small optimization: call index instead of linefeed because they're
        // identical and this avoids one layer of function call overhead.
        try self.terminal.index();
    }

    pub inline fn reverseIndex(self: *StreamHandler) !void {
        self.terminal.reverseIndex();
    }

    pub inline fn index(self: *StreamHandler) !void {
        try self.terminal.index();
    }

    pub inline fn nextLine(self: *StreamHandler) !void {
        try self.terminal.index();
        self.terminal.carriageReturn();
    }

    pub fn setModifyKeyFormat(self: *StreamHandler, format: terminal.ModifyKeyFormat) !void {
        self.terminal.flags.modify_other_keys_2 = false;
        switch (format) {
            .other_keys_numeric => self.terminal.flags.modify_other_keys_2 = true,
            else => {},
        }
    }

    fn requestMode(self: *StreamHandler, mode: terminal.Mode) !void {
        self.sendModeReport(self.terminal.modes.getReport(.fromMode(mode)));
    }

    fn requestModeUnknown(self: *StreamHandler, mode_raw: u16, ansi: bool) !void {
        self.sendModeReport(self.terminal.modes.getReport(.{ .value = @truncate(mode_raw), .ansi = ansi }));
    }

    fn sendModeReport(self: *StreamHandler, report: terminal.modes.Report) void {
        var data: termio.Message.WriteReq.Small.Array = undefined;
        var writer: std.Io.Writer = .fixed(&data);
        report.encode(&writer) catch |err| {
            log.err("error encoding mode report err={}", .{err});
            return;
        };
        self.messageWriter(.{ .write_small = .{
            .data = data,
            .len = @intCast(writer.buffered().len),
        } });
    }

    pub fn setMode(self: *StreamHandler, mode: terminal.Mode, enabled: bool) !void {
        // Note: this function doesn't need to grab the render state or
        // terminal locks because it is only called from process() which
        // grabs the lock.

        // If we are setting cursor blinking, we ignore it if we have
        // a default cursor blink setting set. This is a really weird
        // behavior so this comment will go deep into trying to explain it.
        //
        // There are two ways to set cursor blinks: DECSCUSR (CSI _ q)
        // and DEC mode 12. DECSCUSR is the modern approach and has a
        // way to revert to the "default" (as defined by the terminal)
        // cursor style and blink by doing "CSI 0 q". DEC mode 12 controls
        // blinking and is either on or off and has no way to set a
        // default. DEC mode 12 is also the more antiquated approach.
        //
        // The problem is that if the user specifies a desired default
        // cursor blink with `cursor-style-blink`, the moment a running
        // program uses DEC mode 12, the cursor blink can never be reset
        // to the default without an explicit DECSCUSR. But if a program
        // is using mode 12, it is by definition not using DECSCUSR.
        // This makes for somewhat annoying interactions where a poorly
        // (or legacy) behaved program will stop blinking, and it simply
        // never restarts.
        //
        // To get around this, we have a special case where if the user
        // specifies some explicit default cursor blink desire, we ignore
        // DEC mode 12. We allow DECSCUSR to still set the cursor blink
        // because programs using DECSCUSR usually are well behaved and
        // reset the cursor blink to the default when they exit.
        //
        // To be extra safe, users can also add a manual `CSI 0 q` to
        // their shell config when they render prompts to ensure the
        // cursor is exactly as they request.
        if (mode == .cursor_blinking and
            self.terminal.cursor.default_blink != null)
        {
            return;
        }

        // We first always set the raw mode on our mode state.
        self.terminal.modes.set(mode, enabled);

        // And then some modes require additional processing.
        switch (mode) {
            // Just noting here that autorepeat has no effect on
            // the terminal. xterm ignores this mode and so do we.
            // We know about just so that we don't log that it is
            // an unknown mode.
            .autorepeat => {},

            // Schedule a render since we changed colors
            .reverse_colors => self.terminal.flags.dirty.reverse_colors = true,

            // Origin resets cursor pos. This is called whether or not
            // we're enabling or disabling origin mode and whether or
            // not the value changed.
            .origin => self.terminal.setCursorPos(1, 1),

            .enable_left_and_right_margin => if (!enabled) {
                // When we disable left/right margin mode we need to
                // reset the left/right margins.
                self.terminal.scrolling_region.left = 0;
                self.terminal.scrolling_region.right = self.terminal.cols - 1;
            },

            .alt_screen_legacy => {
                try self.terminal.switchScreenMode(.@"47", enabled);
            },

            .alt_screen => {
                try self.terminal.switchScreenMode(.@"1047", enabled);
            },

            .alt_screen_save_cursor_clear_enter => {
                try self.terminal.switchScreenMode(.@"1049", enabled);
            },

            // Mode 1048 is xterm's conditional save cursor depending
            // on if alt screen is enabled or not (at the terminal emulator
            // level). Alt screen is always enabled for us so this just
            // does a save/restore cursor.
            .save_cursor => {
                if (enabled) {
                    self.terminal.saveCursor();
                } else {
                    self.terminal.restoreCursor();
                }
            },

            // Force resize back to the window size
            .enable_mode_3 => {
                const grid_size = self.size.grid();
                self.terminal.resize(
                    self.alloc,
                    .{
                        .cols = grid_size.columns,
                        .rows = grid_size.rows,
                    },
                ) catch |err| {
                    log.err("error updating terminal size: {}", .{err});
                };
            },

            .@"132_column" => try self.terminal.deccolm(
                self.alloc,
                if (enabled) .@"132_cols" else .@"80_cols",
            ),

            // We need to start a timer to prevent the emulator being hung
            // forever.
            .synchronized_output => {
                if (enabled) self.messageWriter(.{ .start_synchronized_output = {} });
            },

            .linefeed => {
                self.messageWriter(.{ .linefeed_mode = enabled });
            },

            .in_band_size_reports => if (enabled) self.messageWriter(.{
                .size_report = .mode_2048,
            }),

            .report_visibility => if (enabled) self.messageWriter(.{
                .visibility_report = .{
                    .visible = self.terminal.flags.visible,
                    .force = true,
                },
            }),

            .focus_event => if (enabled) self.messageWriter(.{
                .focused = self.terminal.flags.focused,
            }),

            .mouse_event_x10 => {
                if (enabled) {
                    self.terminal.flags.mouse_event = .x10;
                    try self.setMouseShape(.default);
                } else {
                    self.terminal.flags.mouse_event = .none;
                    try self.setMouseShape(.text);
                }
            },
            .mouse_event_normal => {
                if (enabled) {
                    self.terminal.flags.mouse_event = .normal;
                    try self.setMouseShape(.default);
                } else {
                    self.terminal.flags.mouse_event = .none;
                    try self.setMouseShape(.text);
                }
            },
            .mouse_event_button => {
                if (enabled) {
                    self.terminal.flags.mouse_event = .button;
                    try self.setMouseShape(.default);
                } else {
                    self.terminal.flags.mouse_event = .none;
                    try self.setMouseShape(.text);
                }
            },
            .mouse_event_any => {
                if (enabled) {
                    self.terminal.flags.mouse_event = .any;
                    try self.setMouseShape(.default);
                } else {
                    self.terminal.flags.mouse_event = .none;
                    try self.setMouseShape(.text);
                }
            },

            .mouse_format_utf8 => self.terminal.flags.mouse_format = if (enabled) .utf8 else .x10,
            .mouse_format_sgr => self.terminal.flags.mouse_format = if (enabled) .sgr else .x10,
            .mouse_format_urxvt => self.terminal.flags.mouse_format = if (enabled) .urxvt else .x10,
            .mouse_format_sgr_pixels => self.terminal.flags.mouse_format = if (enabled) .sgr_pixels else .x10,

            else => {},
        }
    }

    inline fn startHyperlink(self: *StreamHandler, uri: []const u8, id: ?[]const u8) !void {
        try self.terminal.screens.active.startHyperlink(uri, id);
    }

    pub inline fn endHyperlink(self: *StreamHandler) !void {
        self.terminal.screens.active.endHyperlink();
    }

    pub fn deviceAttributes(
        self: *StreamHandler,
        req: terminal.DeviceAttributeReq,
    ) !void {
        // For the below, we quack as a VT220. We don't quack as
        // a 420 because we don't support DCS sequences.
        switch (req) {
            .primary => self.messageWriter(.{
                // 62 = Level 2 conformance
                //  4 = Sixel graphics
                // 22 = Color text
                // 52 = Clipboard access
                .write_stable = if (self.clipboard_write != .deny)
                    "\x1B[?62;4;22;52c"
                else
                    "\x1B[?62;4;22c",
            }),

            .secondary => self.messageWriter(.{
                .write_stable = "\x1B[>1;10;0c",
            }),

            else => log.warn("unimplemented device attributes req: {}", .{req}),
        }
    }

    /// The cursor position for a CPR/DECXCPR report, honoring origin mode
    /// (relative to the scrolling region when DECOM is set).
    fn cursorReportPos(self: *StreamHandler) struct { x: usize, y: usize } {
        return if (self.terminal.modes.get(.origin)) .{
            .x = self.terminal.screens.active.cursor.x -| self.terminal.scrolling_region.left,
            .y = self.terminal.screens.active.cursor.y -| self.terminal.scrolling_region.top,
        } else .{
            .x = self.terminal.screens.active.cursor.x,
            .y = self.terminal.screens.active.cursor.y,
        };
    }

    pub fn deviceStatusReport(
        self: *StreamHandler,
        req: terminal.device_status.Request,
    ) !void {
        switch (req) {
            .operating_status => self.messageWriter(.{ .write_stable = "\x1B[0n" }),

            .cursor_position => {
                const pos = self.cursorReportPos();

                // Response always is at least 4 chars, so this leaves the
                // remainder for the row/column as base-10 numbers. This
                // will support a very large terminal.
                //
                // This emits exactly `ESC [ <row> ; <col> R`, the shortest
                // legal CSI 6 n reply. If a reader ever sees a longer
                // payload before the `R`, the extra bytes are not from
                // here -- see #367 for the investigation. Note that the
                // mode 2048 in-band size report (`size_report.zig`) ends
                // in `t`, not `R`, and may precede this one.
                var msg: termio.Message = .{ .write_small = .{} };
                const resp = try std.fmt.bufPrint(&msg.write_small.data, "\x1B[{};{}R", .{
                    pos.y + 1,
                    pos.x + 1,
                });
                msg.write_small.len = @intCast(resp.len);

                self.messageWriter(msg);
            },

            // DECXCPR: extended cursor position adds a page number. Ghostty has
            // no page memory, so the page is always 1.
            .cursor_position_extended => {
                const pos = self.cursorReportPos();
                var msg: termio.Message = .{ .write_small = .{} };
                const resp = try std.fmt.bufPrint(&msg.write_small.data, "\x1B[?{};{};1R", .{
                    pos.y + 1,
                    pos.x + 1,
                });
                msg.write_small.len = @intCast(resp.len);

                self.messageWriter(msg);
            },

            .color_scheme => self.messageWriter(.{ .color_scheme_report = .{ .force = true } }),

            .visibility => self.messageWriter(.{ .visibility_report = .{
                .visible = self.terminal.flags.visible,
                .force = true,
            } }),
        }
    }

    pub inline fn decaln(self: *StreamHandler) !void {
        try self.terminal.decaln();
    }

    pub inline fn saveCursor(self: *StreamHandler) !void {
        self.terminal.saveCursor();
    }

    pub inline fn restoreCursor(self: *StreamHandler) !void {
        self.terminal.restoreCursor();
    }

    pub fn enquiry(self: *StreamHandler) !void {
        log.debug("sending enquiry response={s}", .{self.enquiry_response});
        const msg = try termio.Message.writeReq(self.alloc, self.enquiry_response);
        self.messageWriter(msg);
    }

    fn configureCharset(
        self: *StreamHandler,
        slot: terminal.CharsetSlot,
        set: terminal.Charset,
    ) void {
        self.terminal.configureCharset(slot, set);
    }

    pub fn fullReset(
        self: *StreamHandler,
    ) !void {
        self.terminal.fullReset();
        try self.setMouseShape(.text);

        // Full reset clears Kitty clipboard session grants.
        self.kitty_clipboard_grants.deinit(self.alloc);
        self.kitty_clipboard_grants = .{};

        // Reset resets our palette so we report it for mode 2031.
        self.messageWriter(.{ .color_scheme_report = .{ .force = false } });

        // Clear the progress bar
        self.progressReport(.{ .state = .remove });
    }

    /// Record a Kitty clipboard protocol session grant so future
    /// requests with this password skip the permission prompt.
    pub fn kittyClipboardGrant(
        self: *StreamHandler,
        pw: []const u8,
        dir: terminal.kitty.clipboard.Grants.Direction,
    ) error{OutOfMemory}!void {
        try self.kitty_clipboard_grants.grant(self.alloc, pw, dir, false);
    }

    pub fn queryKittyKeyboard(self: *StreamHandler) !void {
        log.debug("querying kitty keyboard mode", .{});
        var data: termio.Message.WriteReq.Small.Array = undefined;
        const resp = try std.fmt.bufPrint(&data, "\x1b[?{}u", .{
            self.terminal.screens.active.kitty_keyboard.current().int(),
        });

        self.messageWriter(.{
            .write_small = .{
                .data = data,
                .len = @intCast(resp.len),
            },
        });
    }

    pub fn reportXtversion(
        self: *StreamHandler,
    ) !void {
        log.debug("reporting XTVERSION: ghostty {s}", .{build_config.version_string});
        var buf: [288]u8 = undefined;
        const resp = try std.fmt.bufPrint(
            &buf,
            "\x1BP>|{s} {s}\x1B\\",
            .{
                "ghostty",
                build_config.version_string,
            },
        );
        const msg = try termio.Message.writeReq(self.alloc, resp);
        self.messageWriter(msg);
    }

    //-------------------------------------------------------------------------
    // OSC

    fn windowTitle(self: *StreamHandler, title: []const u8) !void {
        var buf: [256]u8 = undefined;
        if (title.len >= buf.len) {
            log.warn("change title requested larger than our buffer size, ignoring", .{});
            return;
        }

        // Set the title on the terminal state. We ignore any errors since
        // we can continue to operate just fine without it.
        self.terminal.setTitle(title) catch |err| {
            log.warn("error setting title in terminal state: {}", .{err});
        };

        @memcpy(buf[0..title.len], title);
        buf[title.len] = 0;

        // Special handling for the empty title. We treat the empty title
        // as resetting to as if we never saw a title. Other terminals
        // behave this way too (e.g. iTerm2).
        if (title.len == 0) {
            // If we have a pwd then we set the title as the pwd else
            // we just set it to blank.
            if (self.terminal.getPwd()) |pwd| pwd: {
                if (pwd.len >= buf.len) break :pwd;
                @memcpy(buf[0..pwd.len], pwd);
                buf[pwd.len] = 0;
            }

            self.surfaceMessageWriter(.{ .set_title = buf });
            self.seen_title = false;
            return;
        }

        self.seen_title = true;
        self.surfaceMessageWriter(.{ .set_title = buf });
    }

    inline fn setMouseShape(
        self: *StreamHandler,
        shape: terminal.MouseShape,
    ) !void {
        // Avoid changing the shape if it is already set to avoid excess
        // cross-thread messaging.
        if (self.terminal.mouse_shape == shape) return;

        self.terminal.mouse_shape = shape;
        self.surfaceMessageWriter(.{ .set_mouse_shape = shape });
    }

    fn clipboardContents(self: *StreamHandler, kind: u8, data: []const u8) !void {
        // Note: we ignore the "kind" field and always use the standard clipboard.
        // iTerm also appears to do this but other terminals seem to only allow
        // certain. Let's investigate more.

        const clipboard_type: apprt.Clipboard = switch (kind) {
            'c' => .standard,
            's' => .selection,
            'p' => .primary,
            else => .standard,
        };

        // Get clipboard contents
        if (data.len == 1 and data[0] == '?') {
            self.surfaceMessageWriter(.{ .clipboard_read = clipboard_type });
            return;
        }

        // Write clipboard contents
        self.surfaceMessageWriter(.{
            .clipboard_write = .{
                .req = try apprt.surface.Message.WriteReq.init(
                    self.alloc,
                    data,
                ),
                .clipboard_type = clipboard_type,
            },
        });
    }

    /// Handle one Kitty clipboard protocol (OSC 5522) packet.
    fn kittyClipboard(
        self: *StreamHandler,
        v: terminal.osc.Command.KittyClipboardProtocol,
    ) error{ OutOfMemory, WriteFailed }!void {
        const kitty_clipboard = terminal.kitty.clipboard;

        // Decode and validate the metadata. Malformed structure drops
        // the packet without a response. Invalid decoded text on a write
        // data or alias packet aborts an in-flight transaction.
        var arena: std.heap.ArenaAllocator = .init(self.alloc);
        defer arena.deinit();
        const meta = (kitty_clipboard.Metadata.parse(
            arena.allocator(),
            v.metadata,
        ) catch |err| switch (err) {
            error.OutOfMemory => return error.OutOfMemory,
            error.InvalidValue => {
                const state = self.kitty_clipboard_write orelse return;
                switch (kitty_clipboard.Metadata.operation(v.metadata) orelse return) {
                    .wdata, .walias => try self.kittyClipboardWriteFinish(
                        state,
                        .EINVAL,
                        v.terminator,
                    ),
                    .read, .write => {},
                }
                return;
            },
        }) orelse return;

        switch (meta.op) {
            .read => try self.kittyClipboardRead(
                &meta,
                v.payload orelse "",
                v.terminator,
            ),

            .write => try self.kittyClipboardWriteBegin(
                &meta,
                v.terminator,
            ),

            .wdata => try self.kittyClipboardWriteData(
                &meta,
                v.payload orelse "",
                v.terminator,
            ),

            .walias => try self.kittyClipboardWriteAlias(
                &meta,
                v.payload orelse "",
                v.terminator,
            ),
        }
    }

    fn kittyClipboardRead(
        self: *StreamHandler,
        meta: *const terminal.kitty.clipboard.Metadata,
        payload: []const u8,
        terminator: terminal.osc.Terminator,
    ) !void {
        const kitty_clipboard = terminal.kitty.clipboard;

        // Everything about the request, including the request struct
        // itself, lives in a single arena that crosses to the surface
        // thread, which owns it from the moment the message is sent.
        var arena: std.heap.ArenaAllocator = .init(self.alloc);
        errdefer arena.deinit();
        const alloc = arena.allocator();

        // The payload is the requested MIME list. A read request with
        // an undecodable payload is dropped without any response,
        // matching kitty.
        const decoded = kitty_clipboard.Payload.init(
            alloc,
            payload,
        ) catch |err| switch (err) {
            error.OutOfMemory => return error.OutOfMemory,
            error.Invalid => {
                arena.deinit();
                return;
            },
        };
        if (!decoded.isValidUtf8()) {
            arena.deinit();
            return;
        }

        // The targets type ('.') asks for the listing of available
        // types rather than data. Requested types beyond the cap are
        // dropped and simply never served, which is how the protocol
        // reports an unavailable type anyway.
        var mimes_buf: [kitty_clipboard.max_read_mimes][]const u8 = undefined;
        var mimes_len: usize = 0;
        var list = false;
        var it = decoded.mimeIterator();
        while (it.next()) |mime| {
            if (std.mem.eql(u8, mime, kitty_clipboard.targets_mime)) {
                list = true;
                continue;
            }
            if (mimes_len == mimes_buf.len) continue;
            mimes_buf[mimes_len] = mime;
            mimes_len += 1;
        }

        // Per the spec a password without a name is no password. A
        // stored session grant for it lets the surface skip its
        // permission prompt.
        const pw: []const u8 = if (meta.name.len > 0) meta.pw else "";
        const granted = self.kittyClipboardReadGranted(pw, mimes_len);

        const req = try alloc.create(apprt.ClipboardRequest.KittyRead);
        const mimes = try alloc.alloc([:0]const u8, mimes_len);
        for (mimes_buf[0..mimes_len], mimes) |src, *dst| {
            dst.* = try alloc.dupeZ(u8, src);
        }
        const id = try alloc.dupe(u8, meta.id);
        const pw_owned = try alloc.dupe(u8, pw);
        const name_owned = try alloc.dupeZ(u8, meta.name);
        req.* = .{
            // The arena must be copied in last so it tracks every
            // allocation above.
            .arena = arena,
            .location = switch (meta.loc) {
                .primary => .primary,
                else => .standard,
            },
            .mimes = mimes,
            .list = list,
            .id = id,
            .pw = pw_owned,
            .name = name_owned,
            .granted = granted,
            .terminator = terminator,
        };

        self.surfaceMessageWriter(.{ .kitty_clipboard_read = req });
    }

    /// Whether a session grant covers a read request, consuming
    /// one-time grants. A prompt-exempt request never consults the
    /// grants: consuming a one-time paste password on a listing would
    /// burn the grant before the follow-up data read.
    fn kittyClipboardReadGranted(
        self: *StreamHandler,
        pw: []const u8,
        mimes_len: usize,
    ) bool {
        if (terminal.kitty.clipboard.readPromptExempt(mimes_len)) return false;
        return self.kitty_clipboard_grants.use(self.alloc, pw, .read);
    }

    /// Begin a Kitty clipboard write transaction (type=write).
    fn kittyClipboardWriteBegin(
        self: *StreamHandler,
        meta: *const terminal.kitty.clipboard.Metadata,
        terminator: terminal.osc.Terminator,
    ) error{ OutOfMemory, WriteFailed }!void {
        // A new write silently replaces any in-flight transaction.
        self.kittyClipboardWriteAbort();

        // A write denied by policy can never succeed, so fail the
        // transaction up front instead of spooling data we'd only
        // throw away. Later wdata packets are ignored.
        if (self.clipboard_write == .deny) {
            log.info("application attempted to write clipboard, but 'clipboard-write' is set to deny", .{});
            try self.kittyClipboardWriteStatus(
                .EPERM,
                meta.id,
                terminator,
            );
            return;
        }

        const state = try self.alloc.create(terminal.kitty.clipboard.WriteState);
        errdefer self.alloc.destroy(state);
        state.* = try .init(self.alloc, meta, .{
            .max_size = self.clipboard_write_limit,
        });
        self.kitty_clipboard_write = state;
    }

    /// Accumulate one wdata chunk, or commit the transaction when the
    /// chunk carries no MIME type.
    fn kittyClipboardWriteData(
        self: *StreamHandler,
        meta: *const terminal.kitty.clipboard.Metadata,
        payload: []const u8,
        terminator: terminal.osc.Terminator,
    ) error{ OutOfMemory, WriteFailed }!void {
        // Data without a transaction is silently ignored, matching
        // kitty.
        const state = self.kitty_clipboard_write orelse return;

        // A wdata packet without a MIME type commits the transaction.
        if (meta.mime.len == 0) return self.kittyClipboardWriteCommit(
            state,
            terminator,
        );

        state.data(
            self.alloc,
            meta,
            payload,
        ) catch |err| switch (err) {
            // Failing to spool matches kitty's EIO for a failed buffer write.
            error.OutOfMemory => {
                try self.kittyClipboardWriteFinish(
                    state,
                    .EIO,
                    terminator,
                );
                return error.OutOfMemory;
            },

            // Data over the write limit aborts the transaction and is
            // reported to the client.
            error.TooLarge => try self.kittyClipboardWriteFinish(
                state,
                .EFBIG,
                terminator,
            ),

            // An invalid base64 payload stream aborts the transaction.
            error.Invalid => try self.kittyClipboardWriteFinish(
                state,
                .EINVAL,
                terminator,
            ),
        };
    }

    /// Register aliases from a walias packet.
    fn kittyClipboardWriteAlias(
        self: *StreamHandler,
        meta: *const terminal.kitty.clipboard.Metadata,
        payload: []const u8,
        terminator: terminal.osc.Terminator,
    ) error{ OutOfMemory, WriteFailed }!void {
        // Aliases without a transaction are silently ignored. Once a
        // transaction exists, a missing target MIME type is invalid and
        // aborts the transaction.
        const state = self.kitty_clipboard_write orelse return;
        if (meta.mime.len == 0) return self.kittyClipboardWriteFinish(
            state,
            .EINVAL,
            terminator,
        );

        state.alias(
            self.alloc,
            meta,
            payload,
        ) catch |err| switch (err) {
            error.OutOfMemory => {
                try self.kittyClipboardWriteFinish(
                    state,
                    .EIO,
                    terminator,
                );
                return error.OutOfMemory;
            },

            // An undecodable alias payload aborts the transaction.
            error.Invalid => try self.kittyClipboardWriteFinish(
                state,
                .EINVAL,
                terminator,
            ),
        };
    }

    /// Commit the transaction: resolve the final contents and forward
    /// them to the surface thread, which owns policy, any permission
    /// prompt, the clipboard write itself, and the final reply.
    fn kittyClipboardWriteCommit(
        self: *StreamHandler,
        state: *terminal.kitty.clipboard.WriteState,
        terminator: terminal.osc.Terminator,
    ) error{ OutOfMemory, WriteFailed }!void {
        self.kittyClipboardWriteSend(
            state,
            terminator,
        ) catch |err| switch (err) {
            error.OutOfMemory => {
                try self.kittyClipboardWriteFinish(
                    state,
                    .EIO,
                    terminator,
                );
                return error.OutOfMemory;
            },

            // The last MIME type's payload stream was not correctly
            // padded, which aborts the transaction.
            error.Invalid => return try self.kittyClipboardWriteFinish(
                state,
                .EINVAL,
                terminator,
            ),
        };

        // The transaction is complete; the surface owns the reply.
        self.kittyClipboardWriteAbort();
    }

    fn kittyClipboardWriteSend(
        self: *StreamHandler,
        state: *terminal.kitty.clipboard.WriteState,
        terminator: terminal.osc.Terminator,
    ) error{ OutOfMemory, Invalid }!void {
        const committed = try state.commit(self.alloc);
        defer committed.deinit(self.alloc);

        // Per the spec a password without a name is no password. A
        // stored session grant for it lets the surface skip its
        // permission prompt.
        const pw: []const u8 = if (committed.name.len > 0) committed.pw else "";
        const granted = self.kitty_clipboard_grants.use(self.alloc, pw, .write);

        // Everything about the request, including the request struct
        // itself, lives in a single arena that crosses to the surface
        // thread, which owns it from the moment the message is sent.
        var arena: std.heap.ArenaAllocator = .init(self.alloc);
        errdefer arena.deinit();
        const alloc = arena.allocator();

        const req = try alloc.create(apprt.ClipboardRequest.KittyWrite);
        const contents = try alloc.alloc(
            apprt.ClipboardContent,
            committed.contents.len,
        );
        for (committed.contents, contents) |src, *dst| dst.* = .{
            .mime = try alloc.dupeZ(u8, src.mime),
            .data = try alloc.dupeZ(u8, src.data),
        };
        const id = try alloc.dupe(u8, committed.id);
        const pw_owned = try alloc.dupe(u8, pw);
        const name_owned = try alloc.dupeZ(u8, committed.name);
        req.* = .{
            // The arena must be copied in last so it tracks every
            // allocation above.
            .arena = arena,
            .location = switch (committed.loc) {
                .primary => .primary,
                else => .standard,
            },
            .contents = contents,
            .id = id,
            .pw = pw_owned,
            .name = name_owned,
            .granted = granted,
            .terminator = terminator,
        };

        self.surfaceMessageWriter(.{ .kitty_clipboard_write = req });
    }

    /// Answer the write transaction with its final status and drop it.
    /// The id echoed is the one from the transaction's opening write
    /// packet, matching kitty.
    fn kittyClipboardWriteFinish(
        self: *StreamHandler,
        state: *const terminal.kitty.clipboard.WriteState,
        status: terminal.kitty.clipboard.Status,
        terminator: terminal.osc.Terminator,
    ) error{ OutOfMemory, WriteFailed }!void {
        defer self.kittyClipboardWriteAbort();
        try self.kittyClipboardWriteStatus(status, state.id, terminator);
    }

    /// Reply to a write transaction with a single status packet.
    fn kittyClipboardWriteStatus(
        self: *StreamHandler,
        status: terminal.kitty.clipboard.Status,
        id: []const u8,
        terminator: terminal.osc.Terminator,
    ) error{ OutOfMemory, WriteFailed }!void {
        var stream: std.Io.Writer.Allocating = .init(self.alloc);
        defer stream.deinit();
        try (terminal.kitty.clipboard.Response{
            .op = .write,
            .status = status,
            .id = id,
            .terminator = terminator,
        }).encode(&stream.writer);
        self.messageWriter(.{ .write_alloc = .{
            .alloc = self.alloc,
            .data = try stream.toOwnedSlice(),
        } });
    }

    /// Drop any in-flight write transaction without responding.
    fn kittyClipboardWriteAbort(self: *StreamHandler) void {
        if (self.kitty_clipboard_write) |state| {
            state.deinit(self.alloc);
            self.alloc.destroy(state);
            self.kitty_clipboard_write = null;
        }
    }

    fn semanticPrompt(
        self: *StreamHandler,
        cmd: Stream.Action.SemanticPrompt,
    ) !void {
        switch (cmd.action) {
            .end_input_start_output => {
                self.surfaceMessageWriter(.start_command);
            },

            .end_command => {
                // The specification seems to not specify the type but
                // other terminals accept 32-bits, but exit codes are really
                // bytes, so we just do our best here.
                const code: u8 = code: {
                    const raw: i32 = cmd.readOption(.exit_code) orelse 0;
                    break :code std.math.cast(u8, raw) orelse 1;
                };

                self.surfaceMessageWriter(.{ .stop_command = code });
            },

            .end_prompt_start_input => {
                self.surfaceMessageWriter(.prompt_input);
            },

            // Handled by Terminal, no special handling by us
            .end_prompt_start_input_terminate_eol,
            .fresh_line,
            .fresh_line_new_prompt,
            .new_command,
            .prompt_start,
            => {},
        }

        // We do this last so failures are still processed correctly
        // above.
        try self.terminal.semanticPrompt(cmd);
    }

    /// Adopt a directory a child reported.
    ///
    /// Three sequences land here and the action does not say which: OSC 7
    /// (a file:// URL), OSC 9;9 (a raw path) and OSC 7777 (a raw path out of
    /// the prompt report). The parameter is named `url` because the action's
    /// field is, and it is only a URL on the first of those, so the warnings
    /// below name the set rather than guessing.
    fn reportPwd(self: *StreamHandler, url: []const u8) !void {
        // Special handling for the empty URL. We treat the empty URL
        // as resetting the pwd as if we never saw a pwd. I can't find any
        // other terminal that does this but it seems like a reasonable
        // behavior that enables some useful features. For example, the macOS
        // proxy icon can be hidden when a program reports it doesn't know
        // the pwd rather than showing a stale pwd.
        if (url.len == 0) {
            // Blank value can never fail because no allocs happen.
            self.terminal.setPwd("") catch unreachable;

            // If we haven't seen a title, we're using the pwd as our title.
            // Set it to blank which will reset our title behavior.
            if (!self.seen_title) {
                try self.windowTitle("");
                assert(!self.seen_title);
            }

            // Report the change.
            self.surfaceMessageWriter(.{ .pwd_change = .{ .stable = "" } });
            return;
        }

        // A Windows-native shell reports a raw path, not a URL: cmd has no way
        // to build one (its `PROMPT $p` token expands to `C:\dir` and nothing
        // else), and the PowerShell integration follows suit on OSC 9;9. There
        // is no scheme to check and no POSIX translation applies -- but a raw
        // path can still name a host, in the `\\server\` prefix, and that host
        // gets the same locality check the URL arm gives its own. A reported
        // cwd is spawned into, and Windows authenticates to whatever server a
        // UNC directory names, so adopting a remote one on the strength of
        // bytes alone would let anything writing to the pty pick who receives
        // the user's credentials.
        if (comptime builtin.os.tag == .windows) {
            if (internal_os.posix_path.isWindowsAbsolute(url)) {
                const host = internal_os.posix_path.pathHost(url) catch {
                    log.warn("reported pwd (OSC 7/9;9/7777) names no directory we can use", .{});
                    return;
                };
                switch (host) {
                    .local => {},
                    .server => |name| if (!uncHostIsLocal(name)) {
                        log.warn("reported pwd (OSC 7/9;9/7777) UNC host ({s}) must be local", .{name});
                        return;
                    },
                }
                return self.setPwdReported(url);
            }
        }

        // Attempt to parse this file-style URI using options appropriate
        // for this OSC 7 context (e.g. kitty-shell-cwd expects the full,
        // unencoded path).
        const uri: std.Uri = internal_os.uri.parse(url, .{
            .mac_address = comptime builtin.os.tag != .macos,
            .raw_path = std.mem.startsWith(u8, url, "kitty-shell-cwd://"),
        }) catch |e| {
            log.warn("invalid url in reported pwd (OSC 7/9;9/7777): {}", .{e});
            return;
        };

        if (!std.mem.eql(u8, "file", uri.scheme) and
            !std.mem.eql(u8, "kitty-shell-cwd", uri.scheme))
        {
            log.warn("reported pwd (OSC 7/9;9/7777) scheme must be file or kitty-shell-cwd, got: {s}", .{uri.scheme});
            return;
        }

        // Every Windows surface is one we spawned ourselves, so the host is as
        // local as it gets -- skip the check (the same trust basis ghostty
        // applies to its own ssh sessions). What the surface kind decides on
        // Windows is the translation below, not whether we listen at all.
        if (comptime builtin.os.tag != .windows) {
            var host_buffer: [std.Io.net.HostName.max_len]u8 = undefined;
            const host = uri.getHost(&host_buffer) catch |err| switch (err) {
                error.UriMissingHost => {
                    log.warn("reported pwd (OSC 7/9;9/7777) uri must contain a hostname: {}", .{err});
                    return;
                },
            };

            // OSC 7 is a little sketchy because anyone can send any value from
            // any host (such an SSH session). The best practice terminals follow
            // is to valid the hostname to be local.
            const host_valid = internal_os.hostname.isLocal(host.bytes) catch |err| switch (err) {
                error.PermissionDenied,
                error.Unexpected,
                => {
                    log.warn("failed to get hostname for reported pwd validation: {}", .{err});
                    return;
                },
            };
            if (!host_valid) {
                log.warn("reported pwd (OSC 7/9;9/7777) host ({s}) must be local", .{host.bytes});
                return;
            }
        }

        // We need the raw path, which might require unescaping. We try to
        // avoid making any heap allocations by using the stack first.
        var arena_alloc: std.heap.ArenaAllocator = .init(self.alloc);
        var stack_alloc = std.heap.stackFallback(1024, arena_alloc.allocator());
        defer arena_alloc.deinit();
        // One `get()` for the whole function: it resets the stack buffer and
        // asserts on a second call, so asking twice both invalidates `path`
        // and aborts a safety-enabled build.
        const salloc = stack_alloc.get();
        const path = try uri.path.toRawMaybeAlloc(salloc);

        // On Windows the URL path is in the reporting shell's own vocabulary;
        // translate it to its Windows form so the title and inherited cwd are
        // usable. Which translator depends on the surface kind: a POSIX
        // emulation reports a POSIX path (WSL UNC vs MSYS2/Cygwin install
        // root), while a native shell reports `/c:/dir`, already Windows-shaped
        // once the URI's own path root is dropped. `reported` shares `path`'s
        // stack-fallback arena, and setPwd/WriteReq copy synchronously, so the
        // lifetime matches the untranslated path.
        const reported = if (comptime builtin.os.tag == .windows)
            (if (self.osc7) |ctx| switch (ctx) {
                .wsl => |w| internal_os.posix_path.wslToWindows(salloc, path, w.distro),
                .rooted => |r| internal_os.posix_path.rootedToWindows(salloc, path, r.install_root),
            } else internal_os.posix_path.uriPathToWindows(salloc, path)) catch |err| {
                log.warn("reported pwd (OSC 7/9;9/7777) path translation failed: {}", .{err});
                return;
            }
        else
            path;

        return self.setPwdReported(reported);
    }

    /// Whether a UNC path's server is this machine. The share pseudo-hosts
    /// (`wsl.localhost`, loopback) are decided by name alone; the real
    /// computer name needs the OS, and gets the same `hostname.isLocal` the
    /// URL arm uses.
    ///
    /// `isLocal` compares the computer name exactly, so a share written in a
    /// case the OS does not report it in -- `\\mypc\x` against a `MYPC` --
    /// reads as remote and the cwd is simply not adopted. That is the safe
    /// direction to be wrong in; a spawn at the profile's directory is a
    /// nuisance, and one at an attacker's is a credential.
    fn uncHostIsLocal(host: []const u8) bool {
        if (internal_os.posix_path.isLocalShareHost(host)) return true;
        return internal_os.hostname.isLocal(host) catch |err| {
            log.warn("failed to get hostname for UNC validation: {}", .{err});
            return false;
        };
    }

    /// Commit a fully-resolved pwd: the terminal's own state, the surface
    /// notification, and the title when no shell has claimed one. Shared by
    /// every arm of `reportPwd` so a raw Windows path and a translated URL
    /// land identically.
    fn setPwdReported(self: *StreamHandler, reported: []const u8) !void {
        log.debug("terminal pwd: {s}", .{reported});

        // One prompt reports its directory three times: OSC 7 and OSC 9;9
        // both carry it and OSC 7777 carries it again. Only the first is
        // news. Each of the others otherwise costs a copy, a heap
        // allocation and a cross-thread message to tell the surface a value
        // it already holds, plus a second cross-thread message and a
        // 256-byte title buffer to set the title to what it already says.
        //
        // The return is whole-function, following `setMouseShape` in this
        // file, because the title needs nothing either: the only title this
        // function ever sets is the one derived from the pwd, so an
        // unchanged pwd is an unchanged title. A title an application set
        // stops us anyway (`seen_title`), and the one thing that can clear
        // `seen_title` behind our back -- an empty OSC 0/2 -- already
        // re-derives the title from the pwd itself.
        //
        // It also stops a directory too long to be a title from re-logging
        // its refusal on every prompt, forever, for a user standing in a
        // deep tree: it is refused once per directory now.
        //
        // `pwd_reported` rather than the pwd alone, because the pwd slot is
        // pre-seeded at spawn without a surface message. See its docs.
        const known = self.terminal.getPwd();
        if (self.pwd_reported and
            known != null and
            std.mem.eql(u8, known.?, reported)) return;
        self.pwd_reported = true;

        try self.terminal.setPwd(reported);

        // Report it to the surface. If creating our write request fails
        // then we just ignore it.
        if (apprt.surface.Message.WriteReq.init(self.alloc, reported)) |req| {
            self.surfaceMessageWriter(.{ .pwd_change = req });
        } else |err| {
            log.warn("error notifying surface of pwd change err={}", .{err});
        }

        // If we haven't seen a title, use our pwd as the title. The reset
        // after the call is load-bearing: `windowTitle` sets `seen_title`,
        // and a title we derived ourselves must not count as one an
        // application set, or the window would stop tracking the folder.
        if (!self.seen_title) {
            try self.windowTitle(reported);
            self.seen_title = false;
        }
    }

    fn colorOperation(
        self: *StreamHandler,
        op: terminal.osc.color.Operation,
        requests: *const terminal.osc.color.List,
        terminator: terminal.osc.Terminator,
    ) !void {
        // We'll need op one day if we ever implement reporting special colors.
        _ = op;

        // return early if there is nothing to do
        if (requests.count() == 0) return;

        var buffer: [1024]u8 = undefined;
        var fba: std.heap.FixedBufferAllocator = .init(&buffer);
        const alloc = fba.allocator();

        var response: std.Io.Writer.Allocating = .init(alloc);

        var it = requests.constIterator(0);
        while (it.next()) |req| {
            switch (req.*) {
                .set => |set| {
                    switch (set.target) {
                        .palette => |i| {
                            self.terminal.flags.dirty.palette = true;
                            self.terminal.colors.palette.set(i, set.color);
                        },
                        .dynamic => |dynamic| switch (dynamic) {
                            .foreground => self.terminal.colors.foreground.set(set.color),
                            .background => self.terminal.colors.background.set(set.color),
                            .cursor => self.terminal.colors.cursor.set(set.color),
                            .pointer_foreground,
                            .pointer_background,
                            .tektronix_foreground,
                            .tektronix_background,
                            .highlight_background,
                            .tektronix_cursor,
                            .highlight_foreground,
                            => log.info("setting dynamic color {s} not implemented", .{
                                @tagName(dynamic),
                            }),
                        },
                        .special => log.info("setting special colors not implemented", .{}),
                    }

                    // Notify the surface of the color change
                    self.surfaceMessageWriter(.{ .color_change = .{
                        .target = set.target,
                        .color = set.color,
                    } });
                },

                .reset => |target| switch (target) {
                    .palette => |i| {
                        self.terminal.flags.dirty.palette = true;
                        self.terminal.colors.palette.reset(i);

                        self.surfaceMessageWriter(.{
                            .color_change = .{
                                .target = target,
                                .color = self.terminal.colors.palette.current[i],
                            },
                        });
                    },
                    .dynamic => |dynamic| switch (dynamic) {
                        .foreground => {
                            self.terminal.colors.foreground.reset();

                            if (self.terminal.colors.foreground.default) |c| {
                                self.surfaceMessageWriter(.{ .color_change = .{
                                    .target = target,
                                    .color = c,
                                } });
                            }
                        },
                        .background => {
                            self.terminal.colors.background.reset();

                            if (self.terminal.colors.background.default) |c| {
                                self.surfaceMessageWriter(.{ .color_change = .{
                                    .target = target,
                                    .color = c,
                                } });
                            }
                        },
                        .cursor => {
                            self.terminal.colors.cursor.reset();

                            if (self.terminal.colors.cursor.default) |c| {
                                self.surfaceMessageWriter(.{ .color_change = .{
                                    .target = target,
                                    .color = c,
                                } });
                            }
                        },
                        .pointer_foreground,
                        .pointer_background,
                        .tektronix_foreground,
                        .tektronix_background,
                        .highlight_background,
                        .tektronix_cursor,
                        .highlight_foreground,
                        => log.warn("resetting dynamic color {s} not implemented", .{
                            @tagName(dynamic),
                        }),
                    },
                    .special => log.info("resetting special colors not implemented", .{}),
                },

                .reset_palette => {
                    const mask = &self.terminal.colors.palette.mask;
                    var mask_it = mask.iterator(.{});
                    while (mask_it.next()) |i| {
                        self.terminal.flags.dirty.palette = true;
                        self.terminal.colors.palette.reset(@intCast(i));
                        self.surfaceMessageWriter(.{
                            .color_change = .{
                                .target = .{ .palette = @intCast(i) },
                                .color = self.terminal.colors.palette.current[i],
                            },
                        });
                    }
                    mask.* = .initEmpty();
                },

                .reset_special => log.warn(
                    "resetting all special colors not implemented",
                    .{},
                ),

                .query => |kind| report: {
                    // Fire validation log before the early-return so we capture
                    // OSC 11 arrival regardless of osc-color-report-format.
                    if (kind == .dynamic and kind.dynamic == .background) {
                        log_validate.info("osc11 from pty: kind=query", .{});
                    }

                    if (self.osc_color_report_format == .none) break :report;

                    const color = switch (kind) {
                        .palette => |i| self.terminal.colors.palette.current[i],
                        .dynamic => |dynamic| switch (dynamic) {
                            .foreground => self.terminal.colors.foreground.get().?,
                            .background => self.terminal.colors.background.get().?,
                            .cursor => self.terminal.colors.cursor.get() orelse
                                self.terminal.colors.foreground.get().?,
                            .pointer_foreground,
                            .pointer_background,
                            .tektronix_foreground,
                            .tektronix_background,
                            .highlight_background,
                            .tektronix_cursor,
                            .highlight_foreground,
                            => {
                                log.info(
                                    "reporting dynamic color {s} not implemented",
                                    .{@tagName(dynamic)},
                                );
                                break :report;
                            },
                        },
                        .special => {
                            log.info("reporting special colors not implemented", .{});
                            break :report;
                        },
                    };

                    switch (self.osc_color_report_format) {
                        .@"16-bit" => switch (kind) {
                            .palette => |i| try response.writer.print(
                                "\x1b]4;{d};rgb:{x:0>4}/{x:0>4}/{x:0>4}",
                                .{
                                    i,
                                    @as(u16, color.r) * 257,
                                    @as(u16, color.g) * 257,
                                    @as(u16, color.b) * 257,
                                },
                            ),
                            .dynamic => |dynamic| try response.writer.print(
                                "\x1b]{d};rgb:{x:0>4}/{x:0>4}/{x:0>4}",
                                .{
                                    @intFromEnum(dynamic),
                                    @as(u16, color.r) * 257,
                                    @as(u16, color.g) * 257,
                                    @as(u16, color.b) * 257,
                                },
                            ),
                            .special => unreachable,
                        },

                        .@"8-bit" => switch (kind) {
                            .palette => |i| try response.writer.print(
                                "\x1b]4;{d};rgb:{x:0>2}/{x:0>2}/{x:0>2}",
                                .{
                                    i,
                                    @as(u16, color.r),
                                    @as(u16, color.g),
                                    @as(u16, color.b),
                                },
                            ),
                            .dynamic => |dynamic| try response.writer.print(
                                "\x1b]{d};rgb:{x:0>2}/{x:0>2}/{x:0>2}",
                                .{
                                    @intFromEnum(dynamic),
                                    @as(u16, color.r),
                                    @as(u16, color.g),
                                    @as(u16, color.b),
                                },
                            ),
                            .special => unreachable,
                        },

                        .none => unreachable,
                    }

                    try response.writer.writeAll(terminator.string());
                },
            }
        }

        if (response.writer.end > 0) {
            // If any of the operations were reports, finalize the report
            // string and send it to the terminal.
            const msg = try termio.Message.writeReq(self.alloc, response.writer.buffered());
            self.messageWriter(msg);
        }
    }

    fn showDesktopNotification(
        self: *StreamHandler,
        title: []const u8,
        body: []const u8,
    ) !void {
        self.surfaceMessageWriter(.{
            .desktop_notification = .init(title, body),
        });
    }

    /// Send a report to the pty.
    pub fn sendSizeReport(self: *StreamHandler, style: terminal.SizeReportStyle) void {
        switch (style) {
            .csi_14_t => self.messageWriter(.{ .size_report = .csi_14_t }),
            .csi_16_t => self.messageWriter(.{ .size_report = .csi_16_t }),
            .csi_18_t => self.messageWriter(.{ .size_report = .csi_18_t }),
            .csi_21_t => self.surfaceMessageWriter(.{ .report_title = .csi_21_t }),
            .iterm2_report_cell_size => self.messageWriter(.{
                .size_report = .iterm2_report_cell_size,
            }),
        }
    }

    fn kittyColorReport(
        self: *StreamHandler,
        request: terminal.kitty.color.OSC,
    ) !void {
        var stream: std.Io.Writer.Allocating = .init(self.alloc);
        defer stream.deinit();
        const writer = &stream.writer;

        for (request.list.items) |item| {
            switch (item) {
                .query => |key| {
                    // If the writer buffer is empty, we need to write our prefix
                    if (stream.written().len == 0) try writer.writeAll("\x1b]21");

                    const color: terminal.color.RGB = switch (key) {
                        .palette => |palette| self.terminal.colors.palette.current[palette],
                        .special => |special| switch (special) {
                            .foreground => self.terminal.colors.foreground.get(),
                            .background => self.terminal.colors.background.get(),
                            .cursor => self.terminal.colors.cursor.get(),
                            else => {
                                log.warn("ignoring unsupported kitty color protocol key: {f}", .{key});
                                continue;
                            },
                        },
                    } orelse {
                        try writer.print(";{f}=", .{key});
                        continue;
                    };

                    try writer.print(
                        ";{f}=rgb:{x:0>2}/{x:0>2}/{x:0>2}",
                        .{ key, color.r, color.g, color.b },
                    );
                },
                .set => |v| switch (v.key) {
                    .palette => |palette| {
                        self.terminal.flags.dirty.palette = true;
                        self.terminal.colors.palette.set(palette, v.color);
                    },

                    .special => |special| switch (special) {
                        .foreground => self.terminal.colors.foreground.set(v.color),
                        .background => self.terminal.colors.background.set(v.color),
                        .cursor => self.terminal.colors.cursor.set(v.color),
                        else => {
                            log.warn(
                                "ignoring unsupported kitty color protocol key: {f}",
                                .{v.key},
                            );
                            continue;
                        },
                    },
                },
                .reset => |key| switch (key) {
                    .palette => |palette| {
                        self.terminal.flags.dirty.palette = true;
                        self.terminal.colors.palette.reset(palette);
                    },

                    .special => |special| switch (special) {
                        .foreground => self.terminal.colors.foreground.reset(),
                        .background => self.terminal.colors.background.reset(),
                        .cursor => self.terminal.colors.cursor.reset(),
                        else => {
                            log.warn(
                                "ignoring unsupported kitty color protocol key: {f}",
                                .{key},
                            );
                            continue;
                        },
                    },
                },
            }
        }

        // If we had any writes to our buffer, we queue them now
        if (stream.written().len > 0) {
            try writer.writeAll(request.terminator.string());
            self.messageWriter(.{
                .write_alloc = .{
                    .alloc = self.alloc,
                    .data = try stream.toOwnedSlice(),
                },
            });
        }

        // Note: we don't have to do a queueRender here because every
        // processed stream will queue a render once it is done processing
        // the read() syscall.
    }

    /// Display a GUI progress report.
    fn progressReport(self: *StreamHandler, report: terminal.osc.Command.ProgressReport) void {
        self.surfaceMessageWriter(.{ .progress_report = report });
    }

    /// Handle an iTerm2 OSC 1337 File= inline image. The transmit
    /// carries the raw base64 payload plus parsed geometry hints; we
    /// decode + synthesize a kitty graphics transmit_and_display
    /// command so it goes through the same image storage and renderer
    /// path that the kitty graphics protocol uses.
    fn iterm2ImageTransmit(
        self: *StreamHandler,
        transmit: terminal.osc.Command.Iterm2ImageTransmit,
    ) !void {
        var cmd = iterm2_parser.synthKittyCommand(
            self.alloc,
            transmit,
        ) catch |err| {
            log.warn("iterm2 inline image dropped: {t}", .{err});
            return;
        };
        defer cmd.deinit(self.alloc);

        // iTerm2's File= protocol has no response channel, unlike the
        // kitty graphics APC. We drop the response.
        _ = self.terminal.kittyGraphics(global.io(), self.alloc, &cmd);
    }

    /// Handle one event from an iTerm2 OSC 1337 multipart File=
    /// transfer. The assembler stitches chunks across OSCs; on FileEnd
    /// it returns an Iterm2ImageTransmit whose payload we own and
    /// must free. We hand it straight to the same synth + dispatch
    /// path the single-shot File= handler uses.
    fn iterm2MultipartImage(
        self: *StreamHandler,
        event: terminal.osc.Command.Iterm2MultipartEvent,
    ) !void {
        const assembled = self.multipart_iterm2.handleEvent(
            self.alloc,
            event,
        ) catch |err| {
            log.warn("iterm2 multipart image dropped: {t}", .{err});
            return;
        };

        const transmit = assembled orelse return;
        defer self.alloc.free(transmit.payload);

        try self.iterm2ImageTransmit(transmit);
    }
};

test "kitty clipboard read: targets-only never consumes a one-time grant" {
    const testing = std.testing;

    var handler: StreamHandler = undefined;
    handler.alloc = testing.allocator;
    handler.kitty_clipboard_grants = .{};
    defer handler.kitty_clipboard_grants.deinit(testing.allocator);
    try handler.kitty_clipboard_grants.grant(testing.allocator, "otp", .read, true);

    // A listing request must not burn the one-time paste password...
    try testing.expect(!handler.kittyClipboardReadGranted("otp", 0));
    // ...so the follow-up data read is still granted, exactly once.
    try testing.expect(handler.kittyClipboardReadGranted("otp", 1));
    try testing.expect(!handler.kittyClipboardReadGranted("otp", 1));
}

test "kitty clipboard write: oversized text replies EFBIG" {
    const testing = std.testing;

    var mailbox = try termio.Mailbox.initSPSC(testing.allocator);
    defer mailbox.deinit(testing.allocator);

    var mutex: std.Io.Mutex = .init;
    mutex.lockUncancelable(global.io());
    defer mutex.unlock(global.io());

    var renderer_state: renderer.State = .{
        .mutex = &mutex,
        .terminal = undefined,
    };
    var handler: StreamHandler = undefined;
    handler.alloc = testing.allocator;
    handler.termio_mailbox = &mailbox;
    handler.renderer_state = &renderer_state;
    handler.clipboard_write = .allow;
    handler.clipboard_write_limit = 4;
    handler.kitty_clipboard_write = null;
    defer handler.kittyClipboardWriteAbort();

    const begin: terminal.kitty.clipboard.Metadata = .{
        .op = .write,
        .id = "macos",
    };
    try handler.kittyClipboardWriteBegin(&begin, .st);
    const state = handler.kitty_clipboard_write.?;
    try state.data(
        testing.allocator,
        &.{ .op = .wdata, .mime = "text/plain" },
        "SGVsbA==", // "Hell"
    );
    try testing.expectError(error.TooLarge, state.data(
        testing.allocator,
        &.{ .op = .wdata, .mime = "text/plain" },
        "bw==", // "o"
    ));
    try handler.kittyClipboardWriteFinish(state, .EFBIG, .st);
    try testing.expect(handler.kitty_clipboard_write == null);

    const response = mailbox.spsc.queue.pop(global.io());
    try testing.expect(response != null);
    const msg = response.?;
    defer msg.deinit();
    switch (msg) {
        .write_alloc => |v| try testing.expectEqualStrings(
            "\x1B]5522;type=write:status=EFBIG:id=macos\x1B\\",
            v.data,
        ),
        else => try testing.expect(false),
    }

    // Teardown leaves no transaction that could be committed and
    // forwarded to the macOS clipboard path.
    try testing.expect(mailbox.spsc.queue.pop(global.io()) == null);
}

/// Everything a pwd report needs from a `StreamHandler`, and nothing else.
/// The handler is huge and mostly irrelevant here, so the fields the pwd path
/// reads are set and the rest is left alone; touching another one from this
/// path would show up as a crash rather than a wrong answer.
const PwdTestHarness = struct {
    arena: std.heap.ArenaAllocator,
    term: terminal.Terminal,
    mutex: std.Io.Mutex,
    renderer_state: renderer.State,
    app_mailbox: *App.Mailbox.Queue,
    rt_app: apprt.App,
    handler: StreamHandler,

    const Counts = struct { pwd: usize = 0, title: usize = 0 };

    fn init(self: *PwdTestHarness, alloc: Allocator) !void {
        self.arena = .init(alloc);
        errdefer self.arena.deinit();

        self.term = try terminal.Terminal.init(
            global.io(),
            alloc,
            .{ .cols = 80, .rows = 24 },
        );
        errdefer self.term.deinit(alloc);

        self.app_mailbox = try App.Mailbox.Queue.create(alloc);
        errdefer self.app_mailbox.destroy(alloc);

        self.mutex = .init;
        self.mutex.lockUncancelable(global.io());

        self.renderer_state = .{ .mutex = &self.mutex, .terminal = &self.term };

        self.handler = undefined;
        self.handler.alloc = self.arena.allocator();
        self.handler.terminal = &self.term;
        self.handler.renderer_state = &self.renderer_state;
        self.rt_app = .{};
        self.handler.surface_mailbox = .{
            // Never dereferenced: the mailbox only carries the pointer
            // through to the app thread, which this test stands in for.
            .surface = undefined,
            .app = .{ .rt_app = &self.rt_app, .mailbox = self.app_mailbox },
        };
        self.handler.osc7 = null;
        self.handler.seen_title = false;
        self.handler.pwd_reported = false;
    }

    fn deinit(self: *PwdTestHarness, alloc: Allocator) void {
        self.mutex.unlock(global.io());
        self.app_mailbox.destroy(alloc);
        self.term.deinit(alloc);
        self.arena.deinit();
    }

    /// Drive raw bytes through a real `Stream`, so what is counted below is
    /// what an actual prompt burst produces and not a hand-called method.
    fn feed(self: *PwdTestHarness, input: []const u8) void {
        var stream: terminal.Stream(*StreamHandler) = .init(.{
            .handler = &self.handler,
        });
        for (input) |c| stream.next(c);
    }

    /// Drain the app mailbox and count what the surface would have acted on.
    fn drain(self: *PwdTestHarness) Counts {
        var counts: Counts = .{};
        while (self.app_mailbox.pop(global.io())) |msg| switch (msg) {
            .surface_message => |sm| switch (sm.message) {
                .pwd_change => counts.pwd += 1,
                .set_title => counts.title += 1,
                else => {},
            },
            else => {},
        };
        return counts;
    }
};

test "pwd: one prompt's OSC 7, 9;9 and 7777 burst reports once" {
    // The burst is Windows-shaped: OSC 9;9 and OSC 7777 both carry a raw
    // Windows path, and only the Windows arm of reportPwd adopts one.
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var h: PwdTestHarness = undefined;
    try h.init(testing.allocator);
    defer h.deinit(testing.allocator);

    // What the shipped PowerShell integration writes for one prompt in
    // C:\Users\me, in the order it writes it. The OSC 7 URL spells the drive
    // lower and percent-encodes the path; the other two carry the raw path.
    // All three name one directory, so the surface hears about it once.
    h.feed(
        "\x1b]7;file://MYPC/c:/Users/me\x07" ++
            "\x1b]9;9;C:\\Users\\me\x07" ++
            "\x1b]7777;p;7B2276223A312C22637764223A22433A5C5C55736572735C5C6D65222C2265786974223A302C227368656C6C223A2270777368227D\x07",
    );

    const first = h.drain();
    try testing.expectEqual(@as(usize, 1), first.pwd);
    try testing.expectEqual(@as(usize, 1), first.title);
    try testing.expectEqualStrings("C:\\Users\\me", h.handler.terminal.getPwd().?);

    // A second identical prompt is not news either.
    h.feed("\x1b]9;9;C:\\Users\\me\x07");
    const again = h.drain();
    try testing.expectEqual(@as(usize, 0), again.pwd);
    try testing.expectEqual(@as(usize, 0), again.title);

    // A genuinely new directory is, once, on both counts.
    h.feed(
        "\x1b]7;file://MYPC/c:/Users/me/src\x07" ++
            "\x1b]9;9;C:\\Users\\me\\src\x07",
    );
    const moved = h.drain();
    try testing.expectEqual(@as(usize, 1), moved.pwd);
    try testing.expectEqual(@as(usize, 1), moved.title);
    try testing.expectEqualStrings("C:\\Users\\me\\src", h.handler.terminal.getPwd().?);
}

test "pwd: a title an application set survives the next prompt's burst" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var h: PwdTestHarness = undefined;
    try h.init(testing.allocator);
    defer h.deinit(testing.allocator);

    // The window title tracks the folder until an application claims it.
    // That is the behaviour the dedupe must not disturb in either direction:
    // the folder still sets the title while no one else has, and stops the
    // moment someone does.
    h.feed("\x1b]9;9;C:\\Users\\me\x07");
    try testing.expectEqual(@as(usize, 1), h.drain().title);
    try testing.expect(!h.handler.seen_title);

    h.feed("\x1b]0;vim\x07");
    _ = h.drain();
    try testing.expect(h.handler.seen_title);

    // A new directory still moves the pwd, and no longer touches the title.
    h.feed("\x1b]9;9;C:\\Users\\me\\src\x07");
    const after = h.drain();
    try testing.expectEqual(@as(usize, 1), after.pwd);
    try testing.expectEqual(@as(usize, 0), after.title);
    try testing.expectEqualStrings("vim", h.handler.terminal.getTitle().?);
}

test "pwd: the first report of a session is not swallowed by the spawn cwd" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;

    const testing = std.testing;
    var h: PwdTestHarness = undefined;
    try h.init(testing.allocator);
    defer h.deinit(testing.allocator);

    // `Exec.initTerminal` seeds the pwd slot from the subprocess's own
    // working directory and sends no surface message, so the first prompt in
    // that directory matches a value the surface was never told. Comparing
    // the pwd alone would drop the first `pwd_change` of every session.
    try h.term.setPwd("C:\\Users\\me");

    h.feed("\x1b]9;9;C:\\Users\\me\x07");
    const counts = h.drain();
    try testing.expectEqual(@as(usize, 1), counts.pwd);
    try testing.expectEqual(@as(usize, 1), counts.title);
}
