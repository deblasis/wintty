const std = @import("std");
const Allocator = std.mem.Allocator;
const Config = @import("../config/Config.zig");
const configpkg = @import("../config.zig");
const themepkg = @import("../config/theme.zig");
const input = @import("../input.zig");
const global_state = &@import("../global.zig").state;

const zf = @import("zf");

const log = std.log.scoped(.inline_theme_picker);

/// Callback fired when the user previews or confirms a theme.
/// First arg is the null-terminated theme name, second is true on confirm.
pub const ThemeCallback = *const fn ([*:0]const u8, bool) callconv(.c) void;

/// Callback to write VT bytes into the surface's terminal.
pub const WriteCallback = *const fn (ud: ?*anyopaque, data: [*]const u8, len: usize) void;

/// A code segment for syntax-highlighted display in the preview.
const CodeSegment = struct {
    text: []const u8,
    pal: ?usize = null,
    selection: bool = false,
    cursor: bool = false,
};

/// Theme entry discovered from the filesystem.
pub const ThemeEntry = struct {
    name: []const u8,
    path: []const u8,
    location: themepkg.Location,
    rank: ?f64 = null,

    fn lessThan(_: void, lhs: ThemeEntry, rhs: ThemeEntry) bool {
        return std.ascii.orderIgnoreCase(lhs.name, rhs.name) == .lt;
    }
};

/// Discover all available themes from the standard theme directories.
/// Caller owns the returned slice and the arena backing the strings.
pub fn discoverThemes(arena: Allocator) ![]ThemeEntry {
    var themes: std.ArrayList(ThemeEntry) = .empty;

    var it: themepkg.LocationIterator = .{ .arena_alloc = arena };

    while (try it.next()) |loc| {
        var dir = std.fs.cwd().openDir(loc.dir, .{ .iterate = true }) catch |err| {
            if (err != error.FileNotFound)
                log.warn("failed to open theme dir {s}: {}", .{ loc.dir, err });
            continue;
        };
        defer dir.close();

        var walker = dir.iterate();
        while (try walker.next()) |entry| {
            switch (entry.kind) {
                .file, .sym_link => {
                    if (std.mem.eql(u8, entry.name, ".DS_Store"))
                        continue;
                    const path = try std.fs.path.join(arena, &.{ loc.dir, entry.name });
                    try themes.append(arena, .{
                        .path = path,
                        .location = loc.location,
                        .name = try arena.dupe(u8, entry.name),
                    });
                },
                else => {},
            }
        }
    }

    std.mem.sortUnstable(ThemeEntry, themes.items, {}, ThemeEntry.lessThan);
    return themes.items;
}

/// An in-process theme picker that renders via VT escape sequences.
/// Does not use Vaxis -- writes raw ANSI sequences through the write callback.
pub const InlineThemePicker = struct {
    allocator: Allocator,
    /// Arena for per-frame allocations, reset each draw call.
    frame_arena: std.heap.ArenaAllocator,
    /// Arena that owns the theme name/path strings. Freed on deinit.
    theme_arena: ?std.heap.ArenaAllocator,
    themes: []ThemeEntry,
    filtered: std.ArrayList(usize),
    current: usize,
    window: usize,
    cols: u16,
    rows: u16,
    hex: bool,
    mode: Mode,
    search_buf: std.ArrayList(u8),
    should_quit: bool,
    confirmed: bool,

    // Callbacks
    write_fn: WriteCallback,
    write_ud: ?*anyopaque,
    theme_cb: ?ThemeCallback,

    // Track previous theme index for change detection
    prev_theme_idx: ?usize,

    const Mode = enum { normal, search, help };

    // Layout constants
    const list_width: u16 = 32;
    const palette_height: u16 = 6;
    const code_height: u16 = 24;

    pub fn init(
        allocator: Allocator,
        themes: []ThemeEntry,
        theme_arena: ?std.heap.ArenaAllocator,
        cols: u16,
        rows: u16,
        write_fn: WriteCallback,
        write_ud: ?*anyopaque,
        theme_cb: ?ThemeCallback,
    ) !*InlineThemePicker {
        const self = try allocator.create(InlineThemePicker);
        self.* = .{
            .allocator = allocator,
            .frame_arena = std.heap.ArenaAllocator.init(allocator),
            .theme_arena = theme_arena,
            .themes = themes,
            .filtered = try .initCapacity(allocator, themes.len),
            .current = 0,
            .window = 0,
            .cols = cols,
            .rows = rows,
            .hex = false,
            .mode = .normal,
            .search_buf = .empty,
            .should_quit = false,
            .confirmed = false,
            .write_fn = write_fn,
            .write_ud = write_ud,
            .theme_cb = theme_cb,
            .prev_theme_idx = null,
        };

        // Initialize filtered list with all themes
        for (0..themes.len) |i| {
            try self.filtered.append(allocator, i);
        }

        return self;
    }

    pub fn deinit(self: *InlineThemePicker) void {
        const allocator = self.allocator;
        self.frame_arena.deinit();
        self.filtered.deinit(allocator);
        self.search_buf.deinit(allocator);
        if (self.theme_arena) |*arena| arena.deinit();
        self.* = undefined;
        allocator.destroy(self);
    }

    /// Write VT bytes via the write callback.
    fn write(self: *InlineThemePicker, data: []const u8) void {
        self.write_fn(self.write_ud, data.ptr, data.len);
    }

    /// Write a formatted string.
    fn print(self: *InlineThemePicker, comptime fmt: []const u8, args: anytype) void {
        var buf: [4096]u8 = undefined;
        const slice = std.fmt.bufPrint(&buf, fmt, args) catch return;
        self.write(slice);
    }

    /// Move cursor to row, col (1-based).
    fn moveTo(self: *InlineThemePicker, row: u16, col: u16) void {
        self.print("\x1b[{d};{d}H", .{ row + 1, col + 1 });
    }

    /// Set foreground color from RGB.
    fn setFg(self: *InlineThemePicker, r: u8, g: u8, b: u8) void {
        self.print("\x1b[38;2;{d};{d};{d}m", .{ r, g, b });
    }

    /// Set background color from RGB.
    fn setBg(self: *InlineThemePicker, r: u8, g: u8, b: u8) void {
        self.print("\x1b[48;2;{d};{d};{d}m", .{ r, g, b });
    }

    /// Reset all attributes.
    fn resetAttr(self: *InlineThemePicker) void {
        self.write("\x1b[0m");
    }

    /// Set bold.
    fn setBold(self: *InlineThemePicker) void {
        self.write("\x1b[1m");
    }

    /// Set italic.
    fn setItalic(self: *InlineThemePicker) void {
        self.write("\x1b[3m");
    }

    /// Set underline.
    fn setUnderline(self: *InlineThemePicker) void {
        self.write("\x1b[4m");
    }

    /// Enter alt screen and hide cursor, render initial frame.
    pub fn enter(self: *InlineThemePicker) void {
        self.write("\x1b[?1049h"); // alt screen
        self.write("\x1b[?25l"); // hide cursor
        self.draw();
    }

    /// Exit alt screen, restore terminal.
    pub fn exit(self: *InlineThemePicker) void {
        // Restore original terminal default colors before exiting
        // alt screen. OSC 110/111 reset fg/bg to their defaults.
        self.write("\x1b]110\x1b\\"); // reset fg
        self.write("\x1b]111\x1b\\"); // reset bg
        self.write("\x1b[?25h"); // show cursor
        self.write("\x1b[?1049l"); // exit alt screen
    }

    /// Process a key event from the surface input redirect.
    /// Returns true if the event was consumed.
    pub fn handleKey(self: *InlineThemePicker, event: *const input.KeyEvent) bool {
        // Already done -- consume but ignore.
        if (self.should_quit) return true;

        // Only handle press and repeat
        if (event.action == .release) return true;

        const key = event.key;
        const mods = event.mods;

        // Ctrl+C always quits
        if (key == .key_c and mods.ctrl) {
            self.should_quit = true;
            self.notifyThemeChange();
            self.draw();
            return true;
        }

        switch (self.mode) {
            .normal => {
                if (key == .key_q or key == .escape) {
                    self.should_quit = true;
                } else if (key == .slash and !mods.ctrl) {
                    self.mode = .search;
                } else if (key == .f1 or (key == .slash and mods.ctrl)) {
                    self.mode = .help;
                } else if (key == .enter or key == .numpad_enter) {
                    self.confirmed = true;
                    self.should_quit = true;
                    // Fire confirm callback
                    if (self.theme_cb) |cb| {
                        if (self.filtered.items.len > 0 and self.current < self.filtered.items.len) {
                            const idx = self.filtered.items[self.current];
                            const name = self.themes[idx].name;
                            if (name.len < 256) {
                                var buf: [256]u8 = undefined;
                                @memcpy(buf[0..name.len], name);
                                buf[name.len] = 0;
                                cb(@ptrCast(&buf), true);
                            } else {
                                log.warn("theme name too long for callback ({d} bytes): {s}...", .{ name.len, name[0..@min(name.len, 64)] });
                            }
                        }
                    }
                } else if (key == .key_j or key == .arrow_down) {
                    self.moveDown(1);
                } else if (key == .key_k or key == .arrow_up) {
                    self.moveUp(1);
                } else if (key == .page_down) {
                    self.moveDown(20);
                } else if (key == .page_up) {
                    self.moveUp(20);
                } else if (key == .home) {
                    self.current = 0;
                } else if (key == .end) {
                    if (self.filtered.items.len > 0)
                        self.current = self.filtered.items.len - 1;
                } else if (key == .key_h or key == .key_x) {
                    if (!mods.ctrl) self.hex = true;
                } else if (key == .key_d) {
                    self.hex = false;
                } else if (key == .key_f) {
                    // Could cycle filter, but for inline picker we skip
                    // color scheme filtering (no config loading overhead).
                }
                self.notifyThemeChange();
                self.draw();
            },
            .search => {
                if (key == .escape or key == .enter) {
                    self.mode = .normal;
                } else if (mods.ctrl and (key == .key_x or key == .slash)) {
                    self.search_buf.clearRetainingCapacity();
                    self.updateFiltered();
                } else if (key == .backspace) {
                    if (self.search_buf.items.len > 0) {
                        _ = self.search_buf.pop();
                        self.updateFiltered();
                    }
                } else {
                    // Try to get a printable character from utf8 or the key
                    if (event.utf8.len > 0) {
                        self.search_buf.appendSlice(self.allocator, event.utf8) catch {};
                        self.updateFiltered();
                    }
                }
                self.notifyThemeChange();
                self.draw();
            },
            .help => {
                if (key == .escape or key == .f1 or key == .key_q) {
                    self.mode = .normal;
                }
                self.draw();
            },
        }

        return true;
    }

    /// Process a mouse scroll event. Positive yoff = scroll up,
    /// negative = scroll down. Returns true if consumed.
    pub fn handleScroll(self: *InlineThemePicker, yoff: f64) bool {
        if (self.should_quit) return true;
        if (self.mode != .normal) return true;

        if (yoff > 0) {
            self.moveUp(1);
        } else if (yoff < 0) {
            self.moveDown(1);
        } else {
            return true;
        }

        self.notifyThemeChange();
        self.draw();
        return true;
    }

    fn moveUp(self: *InlineThemePicker, count: usize) void {
        if (self.filtered.items.len == 0) {
            self.current = 0;
            return;
        }
        self.current -|= count;
    }

    fn moveDown(self: *InlineThemePicker, count: usize) void {
        if (self.filtered.items.len == 0) {
            self.current = 0;
            return;
        }
        self.current += count;
        if (self.current >= self.filtered.items.len)
            self.current = self.filtered.items.len - 1;
    }

    fn notifyThemeChange(self: *InlineThemePicker) void {
        if (self.theme_cb) |cb| {
            const cur_idx: ?usize = if (self.filtered.items.len > 0 and self.current < self.filtered.items.len)
                self.filtered.items[self.current]
            else
                null;
            if (cur_idx != self.prev_theme_idx) {
                self.prev_theme_idx = cur_idx;
                if (cur_idx) |idx| {
                    const name = self.themes[idx].name;
                    if (name.len < 256) {
                        var buf: [256]u8 = undefined;
                        @memcpy(buf[0..name.len], name);
                        buf[name.len] = 0;
                        cb(@ptrCast(&buf), false);
                    } else {
                        log.warn("theme name too long for callback ({d} bytes): {s}...", .{ name.len, name[0..@min(name.len, 64)] });
                    }
                }
            }
        }
    }

    fn updateFiltered(self: *InlineThemePicker) void {
        // Save current selection name for re-finding after filter
        var selected: []const u8 = "";
        if (self.filtered.items.len > 0 and self.current < self.filtered.items.len) {
            selected = self.themes[self.filtered.items[self.current]].name;
        }

        self.filtered.clearRetainingCapacity();

        if (self.search_buf.items.len > 0) {
            const query = std.ascii.allocLowerString(self.allocator, self.search_buf.items) catch return;
            defer self.allocator.free(query);

            var tokens: std.ArrayList([]const u8) = .empty;
            defer tokens.deinit(self.allocator);

            var it = std.mem.tokenizeScalar(u8, query, ' ');
            while (it.next()) |token| tokens.append(self.allocator, token) catch return;

            for (self.themes, 0..) |*theme, i| {
                theme.rank = zf.rank(theme.name, tokens.items, .{
                    .to_lower = true,
                    .plain = true,
                });
                if (theme.rank != null) self.filtered.append(self.allocator, i) catch {};
            }
        } else {
            for (0..self.themes.len) |i| {
                self.themes[i].rank = null;
                self.filtered.append(self.allocator, i) catch {};
            }
        }

        if (self.filtered.items.len == 0) {
            self.current = 0;
            self.window = 0;
            return;
        }

        // Try to find the previously selected theme
        for (self.filtered.items, 0..) |index, i| {
            if (std.mem.eql(u8, self.themes[index].name, selected)) {
                self.current = i;
                return;
            }
        }
        self.current = 0;
        self.window = 0;
    }

    /// Update the terminal dimensions. Call when the surface resizes.
    pub fn resize(self: *InlineThemePicker, cols: u16, rows: u16) void {
        if (self.should_quit) return;
        self.cols = cols;
        self.rows = rows;
        // Clear immediately to avoid showing stale content from the
        // alt screen reflow during the resize.
        self.write("\x1b[2J\x1b[H");
        self.draw();
    }

    /// Render the current state via VT escape sequences.
    pub fn draw(self: *InlineThemePicker) void {
        _ = self.frame_arena.reset(.retain_capacity);
        const alloc = self.frame_arena.allocator();

        // Clear screen and home cursor
        self.write("\x1b[2J\x1b[H");

        // Adjust window scrolling
        if (self.filtered.items.len == 0) {
            self.current = 0;
            self.window = 0;
        } else {
            const visible_rows = self.rows;
            const end = self.window + visible_rows - 1;
            if (self.current > end)
                self.window = self.current - visible_rows + 1;
            if (self.current < self.window)
                self.window = self.current;
            if (self.window >= self.filtered.items.len)
                self.window = self.filtered.items.len - 1;
        }

        // Draw the theme list (left panel)
        self.drawThemeList();

        // Draw the preview panel (right side)
        self.drawPreview(alloc);

        // Draw overlays based on mode
        switch (self.mode) {
            .normal => {},
            .search => self.drawSearchBox(),
            .help => self.drawHelpOverlay(),
        }

    }

    fn drawThemeList(self: *InlineThemePicker) void {
        const w = @min(list_width, self.cols);

        for (0..self.rows) |row_idx| {
            const index = self.window + row_idx;
            self.moveTo(@intCast(row_idx), 0);
            self.resetAttr();

            if (index >= self.filtered.items.len) {
                // Fill empty rows
                self.writeSpaces(w);
                continue;
            }

            const theme = self.themes[self.filtered.items[index]];
            const is_selected = index == self.current;

            if (is_selected) {
                // Selected: green on dark bg
                self.write("\x1b[38;2;0;170;0m\x1b[48;2;51;51;51m");
                self.write("\xe2\x9d\xaf "); // ">" marker
            } else {
                self.resetAttr();
                self.write("  ");
            }

            // Print theme name, truncated to fit
            const max_name = w -| 4;
            const name_len = @min(theme.name.len, max_name);
            self.write(theme.name[0..name_len]);

            // Fill remaining space
            const used: u16 = @intCast(2 + name_len);
            if (is_selected) {
                if (used < w -| 2) {
                    self.writeSpaces(w - used - 2);
                    self.write(" \xe2\x9d\xae"); // "<" marker
                } else {
                    self.writeSpaces(w -| used);
                }
            } else {
                self.writeSpaces(w -| used);
            }
        }
    }

    fn drawPreview(self: *InlineThemePicker, alloc: Allocator) void {
        const x_off = list_width;
        if (x_off >= self.cols) return;
        const width = self.cols - x_off;

        if (self.filtered.items.len == 0 or self.current >= self.filtered.items.len) {
            // No theme selected -- show "No theme found"
            const msg = "No theme found!";
            const center_row = self.rows / 2;
            const center_col = x_off + (width / 2) -| @as(u16, @intCast(msg.len / 2));
            self.moveTo(center_row, center_col);
            self.resetAttr();
            self.write(msg);
            return;
        }

        const theme = self.themes[self.filtered.items[self.current]];

        // Load theme config to get colors
        var config = Config.default(alloc) catch return;
        defer config.deinit();
        config.loadFile(config._arena.?.allocator(), theme.path) catch {
            // Show error
            const center_row = self.rows / 2;
            self.moveTo(center_row, x_off + 2);
            self.resetAttr();
            self.write("\x1b[31m"); // red
            self.print("Unable to open {s}", .{theme.name});
            return;
        };

        // Set the terminal's default fg/bg via OSC 10/11 so the
        // entire terminal background matches the theme, not just
        // the cells we explicitly paint.
        {
            const fg = config.foreground;
            const bg = config.background;
            self.print("\x1b]10;rgb:{x:0>2}/{x:0>2}/{x:0>2}\x1b\\", .{ fg.r, fg.g, fg.b });
            self.print("\x1b]11;rgb:{x:0>2}/{x:0>2}/{x:0>2}\x1b\\", .{ bg.r, bg.g, bg.b });
        }

        const fg = config.foreground;
        const bg = config.background;

        var next_row: u16 = 0;

        // Theme name header (4 rows)
        {
            self.moveTo(1, x_off + width / 2 -| @as(u16, @intCast(theme.name.len / 2)));
            self.setFg(fg.r, fg.g, fg.b);
            self.setBg(bg.r, bg.g, bg.b);
            self.setBold();
            self.setItalic();
            self.write(theme.name);
            self.resetAttr();

            // Path on next line
            self.moveTo(2, x_off + width / 2 -| @as(u16, @intCast(theme.path.len / 2)));
            self.setFg(fg.r, fg.g, fg.b);
            self.setBg(bg.r, bg.g, bg.b);
            self.write(theme.path[0..@min(theme.path.len, width)]);
            self.resetAttr();
            next_row = 4;
        }

        // Palette grid (16 colors, 8 per row, 2 rows of blocks)
        {
            for (0..16) |i| {
                const ci: u16 = @intCast(i);
                const r = ci / 8;
                const c = ci % 8;

                // Number label
                self.moveTo(next_row + 3 * r, x_off + c * 8);
                self.setFg(fg.r, fg.g, fg.b);
                self.setBg(bg.r, bg.g, bg.b);
                if (self.hex) {
                    self.print(" {x:0>2}", .{i});
                } else {
                    self.print("{d:3}", .{i});
                }

                // Color block (2 rows of 4 chars)
                const pc = paletteColor(config, i);
                self.setFg(pc[0], pc[1], pc[2]);
                self.setBg(bg.r, bg.g, bg.b);
                self.moveTo(next_row + 3 * r, x_off + 4 + c * 8);
                self.write("\xe2\x96\x88\xe2\x96\x88\xe2\x96\x88\xe2\x96\x88"); // ">>>>"
                self.moveTo(next_row + 3 * r + 1, x_off + 4 + c * 8);
                self.write("\xe2\x96\x88\xe2\x96\x88\xe2\x96\x88\xe2\x96\x88");
            }
            next_row += palette_height;
        }

        // Bat-style code sample
        {
            self.drawCodeSample(config, next_row, x_off, width);
            next_row += code_height;
        }

        // Fill remaining space with lorem ipsum
        if (next_row < self.rows) {
            self.drawLoremIpsum(config, next_row, x_off, width);
        }
    }

    fn drawCodeSample(self: *InlineThemePicker, config: Config, start_row: u16, x_off: u16, width: u16) void {
        const fg = config.foreground;
        const bg = config.background;

        // Helper to set a palette color as fg
        const SetPalette = struct {
            picker: *InlineThemePicker,
            cfg: Config,
            bg_color: @TypeOf(config.background),

            fn fg_pal(s: @This(), idx: usize) void {
                const pc = paletteColor(s.cfg, idx);
                s.picker.setFg(pc[0], pc[1], pc[2]);
                s.picker.setBg(s.bg_color.r, s.bg_color.g, s.bg_color.b);
            }
        };

        const sp = SetPalette{ .picker = self, .cfg = config, .bg_color = bg };

        // Line 0: prompt
        self.moveTo(start_row, x_off + 2);
        sp.fg_pal(2);
        self.write("\xe2\x86\x92"); // arrow
        self.resetAttr();
        sp.fg_pal(0);
        self.write(" ");
        sp.fg_pal(4);
        self.write("bat");
        self.resetAttr();
        sp.fg_pal(0);
        self.write(" ");
        sp.fg_pal(6);
        self.setUnderline();
        self.write("ziggzagg.zig");
        self.resetAttr();

        // Line 1: top border
        self.moveTo(start_row + 1, x_off + 2);
        const c238 = paletteColor(config, 238);
        self.setFg(c238[0], c238[1], c238[2]);
        self.setBg(bg.r, bg.g, bg.b);
        self.write("\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80\xe2\x94\xac"); // "-------+"
        // Fill rest with horizontal lines
        if (width > 8) {
            for (0..@min(width - 8, 80)) |_| {
                self.write("\xe2\x94\x80");
            }
        }

        // Line 2: File header
        self.moveTo(start_row + 2, x_off + 2);
        self.setFg(c238[0], c238[1], c238[2]);
        self.setBg(bg.r, bg.g, bg.b);
        self.write("       \xe2\x94\x82 ");
        self.setFg(fg.r, fg.g, fg.b);
        self.write("File: ");
        self.setBold();
        self.write("ziggzagg.zig");
        self.resetAttr();

        // Line 3: separator
        self.moveTo(start_row + 3, x_off + 2);
        self.setFg(c238[0], c238[1], c238[2]);
        self.setBg(bg.r, bg.g, bg.b);
        self.write("\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80\xe2\x94\xbc");
        if (width > 8) {
            for (0..@min(width - 8, 80)) |_| {
                self.write("\xe2\x94\x80");
            }
        }

        // Code lines 1-17
        const code_lines = [_]struct { num: []const u8, segments: []const CodeSegment }{
            .{ .num = "   1   ", .segments = &.{
                .{ .text = "const", .pal = 5 },
                .{ .text = " std ", .pal = null },
                .{ .text = "= @import", .pal = 5 },
                .{ .text = "(", .pal = null },
                .{ .text = "\"std\"", .pal = 10 },
                .{ .text = ");", .pal = null },
            } },
            .{ .num = "   2   ", .segments = &.{} },
            .{ .num = "   3   ", .segments = &.{
                .{ .text = "pub ", .pal = 5 },
                .{ .text = "fn ", .pal = 12 },
                .{ .text = "main", .pal = 2 },
                .{ .text = "() ", .pal = null },
                .{ .text = "!", .pal = 5 },
                .{ .text = "void", .pal = 12 },
                .{ .text = " {", .pal = null },
            } },
            .{ .num = "   4   ", .segments = &.{
                .{ .text = "    ", .pal = null },
                .{ .text = "const ", .pal = 5 },
                .{ .text = "stdout ", .pal = null },
                .{ .text = "=", .pal = 5 },
                .{ .text = " std.Io.getStdOut().writer();", .pal = null },
            } },
            .{ .num = "   5   ", .segments = &.{
                .{ .text = "    ", .pal = null },
                .{ .text = "var ", .pal = 5 },
                .{ .text = "i:", .pal = null },
                .{ .text = " usize", .pal = 12 },
                .{ .text = " =", .pal = 5 },
                .{ .text = " 1", .pal = 4 },
                .{ .text = ";", .pal = null },
            } },
            .{ .num = "   6   ", .segments = &.{
                .{ .text = "    ", .pal = null },
                .{ .text = "while ", .pal = 5 },
                .{ .text = "(i ", .pal = null },
                .{ .text = "<= ", .pal = 5 },
                .{ .text = "16", .pal = 4 },
                .{ .text = ") : (i ", .pal = null },
                .{ .text = "+= ", .pal = 5 },
                .{ .text = "1", .pal = 4 },
                .{ .text = ") {", .pal = null },
            } },
            .{ .num = "   7   ", .segments = &.{
                .{ .text = "        ", .pal = null },
                .{ .text = "if ", .pal = 5 },
                .{ .text = "(i ", .pal = null },
                .{ .text = "% ", .pal = 5 },
                .{ .text = "15 ", .pal = 4 },
                .{ .text = "== ", .pal = 5 },
                .{ .text = "0", .pal = 4 },
                .{ .text = ") {", .pal = null },
            } },
            .{ .num = "   8   ", .segments = &.{
                .{ .text = "            ", .pal = null },
                .{ .text = "try ", .pal = 5 },
                .{ .text = "stdout.writeAll(", .pal = null },
                .{ .text = "\"ZiggZagg", .pal = 10 },
                .{ .text = "\\n", .pal = 12 },
                .{ .text = "\"", .pal = 10 },
                .{ .text = ");", .pal = null },
            } },
            .{ .num = "   9   ", .segments = &.{
                .{ .text = "        ", .pal = null },
                .{ .text = "} ", .pal = null },
                .{ .text = "else if ", .pal = 5 },
                .{ .text = "(i ", .pal = null },
                .{ .text = "% ", .pal = 5 },
                .{ .text = "3 ", .pal = 4 },
                .{ .text = "== ", .pal = 5 },
                .{ .text = "0", .pal = 4 },
                .{ .text = ") {", .pal = null },
            } },
            .{ .num = "  10   ", .segments = &.{
                .{ .text = "            ", .pal = null },
                .{ .text = "try ", .pal = 5 },
                .{ .text = "stdout.writeAll(", .pal = null },
                .{ .text = "\"Zigg", .pal = 10 },
                .{ .text = "\\n", .pal = 12 },
                .{ .text = "\"", .pal = 10 },
                .{ .text = ");", .pal = null },
            } },
            .{ .num = "  11   ", .segments = &.{
                .{ .text = "        ", .pal = null },
                .{ .text = "} ", .pal = null },
                .{ .text = "else if ", .pal = 5 },
                .{ .text = "(i ", .pal = null },
                .{ .text = "% ", .pal = 5 },
                .{ .text = "5 ", .pal = 4 },
                .{ .text = "== ", .pal = 5 },
                .{ .text = "0", .pal = 4 },
                .{ .text = ") {", .pal = null },
            } },
            .{ .num = "  12   ", .segments = &.{
                .{ .text = "            ", .pal = null },
                .{ .text = "try ", .pal = 5 },
                .{ .text = "stdout.writeAll(", .pal = null },
                .{ .text = "\"Zagg", .pal = 10 },
                .{ .text = "\\n", .pal = 12 },
                .{ .text = "\"", .pal = 10 },
                .{ .text = ");", .pal = null },
            } },
            .{ .num = "  13   ", .segments = &.{
                .{ .text = "        ", .pal = null },
                .{ .text = "} ", .pal = null },
                .{ .text = "else ", .pal = 5 },
                .{ .text = "{", .pal = null },
            } },
            .{ .num = "  14   ", .segments = &.{
                .{ .text = "            ", .pal = null },
                .{ .text = "try ", .pal = 5 },
                .{ .text = "stdout.print(\"{d}\\n\", .{i})", .pal = null, .selection = true },
                .{ .text = ";", .pal = null, .cursor = true },
            } },
            .{ .num = "  15   ", .segments = &.{
                .{ .text = "        ", .pal = null },
                .{ .text = "}", .pal = null },
            } },
            .{ .num = "  16   ", .segments = &.{
                .{ .text = "    ", .pal = null },
                .{ .text = "}", .pal = null },
            } },
            .{ .num = "  17   ", .segments = &.{
                .{ .text = " ", .pal = null },
                .{ .text = "}", .pal = null },
            } },
        };

        for (code_lines, 0..) |line, li| {
            const row = start_row + 4 + @as(u16, @intCast(li));
            if (row >= self.rows) break;

            self.moveTo(row, x_off + 2);

            // Line number + separator
            self.setFg(c238[0], c238[1], c238[2]);
            self.setBg(bg.r, bg.g, bg.b);
            self.write(line.num);
            self.write("\xe2\x94\x82 ");

            // Code segments
            for (line.segments) |seg| {
                if (seg.selection) {
                    // Selection style
                    const sel_fg = if (config.@"selection-foreground") |sf| sf.color else bg;
                    const sel_bg = if (config.@"selection-background") |sb| sb.color else config.foreground;
                    self.setFg(sel_fg.r, sel_fg.g, sel_fg.b);
                    self.setBg(sel_bg.r, sel_bg.g, sel_bg.b);
                } else if (seg.cursor) {
                    // Cursor style
                    const cur_fg = if (config.@"cursor-text") |ct| ct.color else bg;
                    const cur_bg = if (config.@"cursor-color") |cc| cc.color else config.foreground;
                    self.setFg(cur_fg.r, cur_fg.g, cur_fg.b);
                    self.setBg(cur_bg.r, cur_bg.g, cur_bg.b);
                } else if (seg.pal) |pal_idx| {
                    const pc = paletteColor(config, pal_idx);
                    self.setFg(pc[0], pc[1], pc[2]);
                    self.setBg(bg.r, bg.g, bg.b);
                } else {
                    self.setFg(fg.r, fg.g, fg.b);
                    self.setBg(bg.r, bg.g, bg.b);
                }
                self.write(seg.text);
            }
            self.resetAttr();
        }

        // Bottom border
        const bottom_row = start_row + 4 + code_lines.len;
        if (bottom_row < self.rows) {
            self.moveTo(@intCast(bottom_row), x_off + 2);
            self.setFg(c238[0], c238[1], c238[2]);
            self.setBg(bg.r, bg.g, bg.b);
            self.write("\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80\xe2\x94\x80\xe2\x94\xb4");
            if (width > 8) {
                for (0..@min(width - 8, 80)) |_| {
                    self.write("\xe2\x94\x80");
                }
            }
        }

        // Starship-style prompt
        const prompt_row: u16 = @intCast(bottom_row + 1);
        if (prompt_row < self.rows) {
            self.moveTo(prompt_row, x_off + 2);
            sp.fg_pal(6);
            self.write("ghostty ");
            self.setFg(fg.r, fg.g, fg.b);
            self.setBg(bg.r, bg.g, bg.b);
            self.write("on ");
            sp.fg_pal(4);
            self.write(" main ");
            sp.fg_pal(1);
            self.write("[+] ");
            self.setFg(fg.r, fg.g, fg.b);
            self.setBg(bg.r, bg.g, bg.b);
            self.write("via ");
            sp.fg_pal(3);
            self.write(" v0.13.0 ");
            self.setFg(fg.r, fg.g, fg.b);
            self.setBg(bg.r, bg.g, bg.b);
            self.write("via ");
            sp.fg_pal(4);
            self.write("  impure (ghostty-env)");
            self.resetAttr();
        }
        if (prompt_row + 1 < self.rows) {
            self.moveTo(prompt_row + 1, x_off + 2);
            sp.fg_pal(4);
            self.write("\xe2\x9c\xa6 ");
            self.setFg(fg.r, fg.g, fg.b);
            self.setBg(bg.r, bg.g, bg.b);
            self.write("at ");
            sp.fg_pal(3);
            self.write("10:36:15 ");
            sp.fg_pal(2);
            self.write("\xe2\x86\x92");
            self.resetAttr();
        }
    }

    fn drawLoremIpsum(self: *InlineThemePicker, config: Config, start_row: u16, x_off: u16, width: u16) void {
        const fg = config.foreground;
        const bg = config.background;

        const lorem = "Lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua";

        var row = start_row + 1;
        var col: u16 = 2;

        self.setFg(fg.r, fg.g, fg.b);
        self.setBg(bg.r, bg.g, bg.b);

        var it = std.mem.tokenizeScalar(u8, lorem, ' ');
        while (row < self.rows) {
            const word = it.next() orelse {
                it.reset();
                continue;
            };
            const wlen: u16 = @intCast(word.len);

            if (col + wlen > width) {
                row += 1;
                col = 2;
                if (row >= self.rows) break;
            }

            self.moveTo(row, x_off + col);

            // Apply special styles for certain words (matching list_themes.zig)
            if (std.mem.eql(u8, "ipsum", word)) {
                const pc = paletteColor(config, 2);
                self.setFg(pc[0], pc[1], pc[2]);
            } else if (std.mem.eql(u8, "consectetur", word)) {
                self.setFg(fg.r, fg.g, fg.b);
                self.setBold();
            } else {
                self.setFg(fg.r, fg.g, fg.b);
            }
            self.setBg(bg.r, bg.g, bg.b);
            self.write(word);
            self.resetAttr();
            self.setFg(fg.r, fg.g, fg.b);
            self.setBg(bg.r, bg.g, bg.b);

            col += wlen + 1;
        }
        self.resetAttr();
    }

    fn drawSearchBox(self: *InlineThemePicker) void {
        const box_width: u16 = @min(self.cols -| 40, 60);
        const box_x = 20;
        const box_y = self.rows -| 5;

        // Top border
        self.moveTo(box_y, box_x);
        self.resetAttr();
        self.write("\xe2\x94\x8c"); // top-left corner
        for (0..box_width) |_| self.write("\xe2\x94\x80");
        self.write("\xe2\x94\x90"); // top-right corner

        // Content row
        self.moveTo(box_y + 1, box_x);
        self.write("\xe2\x94\x82"); // left border
        self.write(self.search_buf.items[0..@min(self.search_buf.items.len, box_width)]);
        const used: u16 = @intCast(@min(self.search_buf.items.len, box_width));
        self.writeSpaces(box_width - used);
        self.write("\xe2\x94\x82"); // right border

        // Bottom border
        self.moveTo(box_y + 2, box_x);
        self.write("\xe2\x94\x94"); // bottom-left corner
        for (0..box_width) |_| self.write("\xe2\x94\x80");
        self.write("\xe2\x94\x98"); // bottom-right corner

        // Show cursor at end of search text
        self.write("\x1b[?25h"); // show cursor
        self.moveTo(box_y + 1, box_x + 1 + used);
    }

    fn drawHelpOverlay(self: *InlineThemePicker) void {
        const help_width: u16 = 60;
        const help_height: u16 = 18;
        const x = self.cols / 2 -| help_width / 2;
        const y = self.rows / 2 -| help_height / 2;

        const key_help = [_]struct { keys: []const u8, desc: []const u8 }{
            .{ .keys = "^C, q, ESC", .desc = "Quit." },
            .{ .keys = "F1, ^/", .desc = "Toggle help window." },
            .{ .keys = "k, Up", .desc = "Move up 1 theme." },
            .{ .keys = "PgUp", .desc = "Move up 20 themes." },
            .{ .keys = "j, Down", .desc = "Move down 1 theme." },
            .{ .keys = "PgDown", .desc = "Move down 20 themes." },
            .{ .keys = "h, x", .desc = "Show palette numbers in hex." },
            .{ .keys = "d", .desc = "Show palette numbers in decimal." },
            .{ .keys = "Home", .desc = "Go to start of the list." },
            .{ .keys = "End", .desc = "Go to end of the list." },
            .{ .keys = "/", .desc = "Start search." },
            .{ .keys = "^X, ^/", .desc = "Clear search." },
            .{ .keys = "Enter", .desc = "Confirm theme." },
        };

        // Draw border
        self.moveTo(y, x);
        self.resetAttr();
        self.write("\xe2\x94\x8c");
        for (0..help_width) |_| self.write("\xe2\x94\x80");
        self.write("\xe2\x94\x90");

        for (0..help_height) |row_idx| {
            const row: u16 = @intCast(row_idx);
            self.moveTo(y + 1 + row, x);
            self.write("\xe2\x94\x82");

            if (row_idx < key_help.len) {
                const entry = key_help[row_idx];
                // Key column (15 chars)
                self.write(" ");
                self.write(entry.keys);
                const key_len: u16 = @intCast(entry.keys.len);
                if (key_len < 14) self.writeSpaces(14 - key_len);
                self.write(" - ");
                self.write(entry.desc);
                const desc_len: u16 = @intCast(entry.desc.len);
                const total = 1 + 14 + 3 + desc_len;
                if (total < help_width) self.writeSpaces(help_width - @as(u16, @intCast(total)));
            } else {
                self.writeSpaces(help_width);
            }

            self.write("\xe2\x94\x82");
        }

        self.moveTo(y + 1 + help_height, x);
        self.write("\xe2\x94\x94");
        for (0..help_width) |_| self.write("\xe2\x94\x80");
        self.write("\xe2\x94\x98");
    }

    fn writeSpaces(self: *InlineThemePicker, count: u16) void {
        const spaces = "                                                                                ";
        var remaining = count;
        while (remaining > 0) {
            const chunk = @min(remaining, @as(u16, @intCast(spaces.len)));
            self.write(spaces[0..chunk]);
            remaining -= chunk;
        }
    }

};

fn paletteColor(config: Config, idx: usize) [3]u8 {
    return [3]u8{
        config.palette.value[idx].r,
        config.palette.value[idx].g,
        config.palette.value[idx].b,
    };
}
