const builtin = @import("builtin");
const std = @import("std");
const apprt = @import("../apprt.zig");
const inputpkg = @import("../input.zig");
const global = @import("../global.zig");
const String = @import("../main_c.zig").String;

const Config = @import("Config.zig");
const c_get = @import("c_get.zig");
const edit = @import("edit.zig");
const Key = @import("key.zig").Key;
const wintty_theme = @import("wintty_theme.zig");

const log = std.log.scoped(.config);

/// Create a new configuration filled with the initial default values.
export fn ghostty_config_new() ?*Config {
    const result = global.alloc().create(Config) catch |err| {
        log.err("error allocating config err={}", .{err});
        return null;
    };

    result.* = Config.default(global.alloc()) catch |err| {
        log.err("error creating config err={}", .{err});
        global.alloc().destroy(result);
        return null;
    };

    return result;
}

export fn ghostty_config_free(ptr: ?*Config) void {
    if (ptr) |v| {
        v.deinit();
        global.alloc().destroy(v);
    }
}

/// Deep clone the configuration.
export fn ghostty_config_clone(self: *Config) ?*Config {
    const result = global.alloc().create(Config) catch |err| {
        log.err("error allocating config err={}", .{err});
        return null;
    };

    result.* = self.clone(global.alloc()) catch |err| {
        log.err("error cloning config err={}", .{err});
        global.alloc().destroy(result);
        return null;
    };

    return result;
}

/// Load the configuration from the CLI args.
export fn ghostty_config_load_cli_args(self: *Config) void {
    self.loadCliArgs(global.alloc()) catch |err| {
        log.err("error loading config err={}", .{err});
    };
}

/// Load the configuration from the default file locations. This
/// is usually done first. The default file locations are locations
/// such as the home directory.
///
/// Reads only, and reports what it found. Sync with
/// ghostty_config_default_files_e.
///
/// Creating the starter file on a first run is
/// `ghostty_config_create_default_file`, and the split is the point:
/// this runs again every time a running app rebuilds its config, an
/// editor saves the config file by swapping a temp file in, and a rebuild
/// landing in that gap used to leave a starter config exactly where the
/// save was about to go (deblasis/wintty#676).
///
/// The answer distinguishes the three outcomes rather than handing back a
/// config full of defaults for all of them, so a caller can tell a user
/// who configured nothing from a configuration that was not readable at
/// the moment it looked, and keep what it is already running on.
///
/// `found_out`, when given, receives how many of the default configuration
/// files exist, readable or not. There is more than one default location
/// and they are layered, so the verdict on its own cannot see the file
/// being saved disappear while another one still reads: that load reports
/// a perfectly good LOADED with one file fewer, and only the count says so.
/// Zero on an error.
export fn ghostty_config_load_default_files(
    self: *Config,
    found_out: ?*c_int,
) Config.DefaultFiles {
    const answer = self.loadDefaultFiles(global.alloc()) catch |err| {
        log.err("error loading config err={}", .{err});
        if (found_out) |out| out.* = 0;
        return .unreadable;
    };
    if (found_out) |out| out.* = @intCast(answer.found);
    return answer.result;
}

/// Create the starter configuration file at the preferred default
/// location. Returns true if it was written.
///
/// For a first run only, which means a caller that has just been told
/// GHOSTTY_CONFIG_DEFAULT_FILES_ABSENT by a load it did at startup. It
/// refuses to overwrite, so a config file that arrives between that
/// answer and this call survives.
export fn ghostty_config_create_default_file() bool {
    return Config.createDefaultFile(global.alloc()) catch |err| {
        log.warn("error creating template config file err={}", .{err});
        return false;
    };
}

/// Load the configuration from a specific file path.
/// The path must be null-terminated.
export fn ghostty_config_load_file(self: *Config, path: [*:0]const u8) void {
    const path_slice = std.mem.span(path);
    self.loadFile(global.alloc(), path_slice) catch |err| {
        log.err("error loading config from file path={s} err={}", .{ path_slice, err });
    };
}

/// Load the configuration from the user-specified configuration
/// file locations in the previously loaded configuration. This will
/// recursively continue to load up to a built-in limit.
export fn ghostty_config_load_recursive_files(self: *Config) void {
    self.loadRecursiveFiles(global.alloc()) catch |err| {
        log.err("error loading config err={}", .{err});
    };
}

/// Set the desktop colour scheme this config resolves against, for the
/// conditional `theme = light:...,dark:...` form and for the built-in
/// theme pair. Returns false if the call did nothing.
///
/// Without this an embedder holding its own config handle resolves every
/// conditional against the default scheme (light) no matter what the
/// desktop is set to, and reads back colours the terminal never renders.
///
/// Must be called before ghostty_config_finalize, which is where the theme
/// is applied, and is refused afterwards rather than accepted-and-ignored.
/// Accepting it would be worse than useless: the recorded scheme would then
/// disagree with the colours already resolved, and the next real desktop
/// change would compare equal to it and be dropped as "no change", leaving
/// the config stuck on the wrong half until the user flipped twice. To
/// react to a scheme change after finalize, rebuild the config instead.
export fn ghostty_config_set_color_scheme(self: *Config, scheme_raw: c_int) bool {
    const scheme = std.enums.fromInt(apprt.ColorScheme, scheme_raw) orelse {
        log.warn("invalid color scheme value={}", .{scheme_raw});
        return false;
    };

    if (self._finalized) {
        log.warn("color scheme set after finalize, ignoring", .{});
        return false;
    }

    self._conditional_state.theme = switch (scheme) {
        .light => .light,
        .dark => .dark,
    };
    return true;
}

/// Whether this config resolved its colours from the built-in theme pair,
/// i.e. whether nothing anywhere in the loaded config set `theme`.
///
/// An embedder cannot answer this by reading the user's config file itself:
/// `theme` can be set in a file pulled in by `config-file`, and following
/// that recursion means reimplementing the loader's include rules. Ask the
/// config that actually did the loading instead.
export fn ghostty_config_theme_is_builtin(self: *Config) bool {
    if (comptime !wintty_theme.enabled) return false;
    return self.theme == null;
}

/// The built-in theme applied when no theme is configured, in config file
/// syntax, or an empty string on a build that has no built-in theme.
///
/// Exists so an embedder drawing chrome around the terminal can resolve the
/// same colours the terminal resolved without a second copy of the palette
/// to keep in step.
///
/// Unlike every other ghostty_string_s producer, this one points at static
/// storage and must NOT be passed to ghostty_string_free. Freeing it hands
/// the allocator a pointer it never owned. The ptr is null on a build with
/// no built-in theme, so callers must check before reading.
export fn ghostty_config_builtin_theme(scheme_raw: c_int) String {
    if (comptime !wintty_theme.enabled) return .empty;
    const scheme = std.enums.fromInt(apprt.ColorScheme, scheme_raw) orelse {
        log.warn("invalid color scheme value={}", .{scheme_raw});
        return .empty;
    };
    return .fromSlice(wintty_theme.forScheme(switch (scheme) {
        .light => .light,
        .dark => .dark,
    }));
}

export fn ghostty_config_finalize(self: *Config) void {
    self.finalize() catch |err| {
        log.err("error finalizing config err={}", .{err});
    };
}

export fn ghostty_config_get(
    self: *Config,
    ptr: *anyopaque,
    key_str: [*]const u8,
    len: usize,
) bool {
    @setEvalBranchQuota(10_000);
    const key = std.meta.stringToEnum(Key, key_str[0..len]) orelse return false;
    return c_get.get(self, key, ptr);
}

export fn ghostty_config_trigger(
    self: *Config,
    str: [*]const u8,
    len: usize,
) inputpkg.Binding.Trigger.C {
    return config_trigger_(self, str[0..len]) catch |err| err: {
        log.err("error finding trigger err={}", .{err});
        break :err .{};
    };
}

fn config_trigger_(
    self: *Config,
    str: []const u8,
) !inputpkg.Binding.Trigger.C {
    const action = try inputpkg.Binding.Action.parse(str);
    const trigger: inputpkg.Binding.Trigger = self.keybind.set.getTrigger(action) orelse .{};
    return trigger.cval();
}

export fn ghostty_config_diagnostics_count(self: *Config) u32 {
    return @intCast(self._diagnostics.items().len);
}

export fn ghostty_config_get_diagnostic(self: *Config, idx: u32) Diagnostic {
    const items = self._diagnostics.items();
    if (idx >= items.len) return .{};
    const message = self._diagnostics.precompute.messages.items[idx];
    return .{ .message = message.ptr };
}

export fn ghostty_config_keybinds_count(self: *Config) u32 {
    return @intCast(self.keybindsCList().len);
}

export fn ghostty_config_get_keybind(self: *Config, idx: u32) inputpkg.Binding.Set.CEntry {
    const list = self.keybindsCList();
    if (idx >= list.len) return .{};
    return list[idx];
}

export fn ghostty_config_open_path() String {
    const path = edit.openPath(global.alloc()) catch |err| {
        log.err("error opening config in editor err={}", .{err});
        return .empty;
    };

    return .fromSlice(path);
}

/// Same resolution as ghostty_config_open_path without its create side
/// effect: the path a `--no-config` run should name. That run ignores the
/// config, so it must not even leave an empty file behind where none
/// existed.
export fn ghostty_config_open_path_no_create() String {
    const path = edit.openPathNoCreate(global.alloc()) catch |err| {
        log.err("error resolving config path err={}", .{err});
        return .empty;
    };

    return .fromSlice(path);
}

/// Sync with ghostty_diagnostic_s
const Diagnostic = extern struct {
    message: [*:0]const u8 = "",
};

// The two exports issue #676 turns on, driven as C callers drive them, over
// a config root of this test's own. Without these the whole pair is
// unreferenced by any test and can be hard-coded to a constant answer while
// everything stays green, which is precisely the failure the fix is about:
// an app where no reload ever applies looks identical from the zig side.

test "ghostty_config_load_default_files reports and counts, and creates nothing" {
    const testing = std.testing;
    const alloc = testing.allocator;

    // macOS also searches Application Support, which this root does not
    // move, so the counts below would not be the whole picture there.
    if (comptime builtin.os.tag == .macos) return error.SkipZigTest;

    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();

    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const root = buf[0..try tmp.dir.realPath(testing.io, &buf)];
    const env = try Config.TestXdgConfigHome.set(alloc, root);
    defer env.restore(alloc);

    {
        var cfg = try Config.default(alloc);
        defer cfg.deinit();

        var found: c_int = -7;
        try testing.expectEqual(
            Config.DefaultFiles.absent,
            ghostty_config_load_default_files(&cfg, &found),
        );
        try testing.expectEqual(@as(c_int, 0), found);
    }

    // Reading answered "nothing here" and wrote nothing: that write is the
    // bug, and it landed on top of an editor's save in progress.
    try testing.expectError(
        error.FileNotFound,
        tmp.dir.access(testing.io, "wintty" ++ std.fs.path.sep_str ++ "config.wintty", .{}),
    );

    try tmp.dir.createDirPath(testing.io, "wintty");
    try tmp.dir.writeFile(testing.io, .{
        .sub_path = "wintty" ++ std.fs.path.sep_str ++ "config.wintty",
        .data = "font-size = 20\n",
    });

    {
        var cfg = try Config.default(alloc);
        defer cfg.deinit();

        var found: c_int = -7;
        try testing.expectEqual(
            Config.DefaultFiles.loaded,
            ghostty_config_load_default_files(&cfg, &found),
        );
        try testing.expectEqual(@as(c_int, 1), found);
        // Not just the verdict: the settings really are in the config.
        try testing.expectEqual(20, cfg.@"font-size");
    }

    // The out parameter is optional, for callers that do not want it.
    {
        var cfg = try Config.default(alloc);
        defer cfg.deinit();
        try testing.expectEqual(
            Config.DefaultFiles.loaded,
            ghostty_config_load_default_files(&cfg, null),
        );
    }
}

test "ghostty_config_create_default_file writes one, and never over one that arrived" {
    const testing = std.testing;
    const alloc = testing.allocator;

    if (comptime builtin.os.tag == .macos) return error.SkipZigTest;

    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();

    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const root = buf[0..try tmp.dir.realPath(testing.io, &buf)];
    const env = try Config.TestXdgConfigHome.set(alloc, root);
    defer env.restore(alloc);

    const sub_path = "wintty" ++ std.fs.path.sep_str ++ "config.wintty";
    try testing.expect(ghostty_config_create_default_file());

    const first = try tmp.dir.readFileAlloc(testing.io, sub_path, alloc, .limited(64 * 1024));
    defer alloc.free(first);
    try testing.expect(std.mem.indexOf(u8, first, "This is the configuration file") != null);

    // The race: the decision to create was taken while there was no config
    // file, and the user's save landed between that and this call.
    try tmp.dir.writeFile(testing.io, .{ .sub_path = sub_path, .data = "font-size = 20\n" });
    try testing.expect(!ghostty_config_create_default_file());

    const after = try tmp.dir.readFileAlloc(testing.io, sub_path, alloc, .limited(64 * 1024));
    defer alloc.free(after);
    try testing.expectEqualStrings("font-size = 20\n", after);
}

test "ghostty_config_get: bool" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var cfg = try Config.default(alloc);
    defer cfg.deinit();
    cfg.maximize = true;

    var out = false;
    const key = "maximize";
    try testing.expect(ghostty_config_get(&cfg, &out, key, key.len));
    try testing.expect(out);
}

test "ghostty_config_get: enum" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var cfg = try Config.default(alloc);
    defer cfg.deinit();
    cfg.@"window-theme" = .dark;

    var out: [*:0]const u8 = undefined;
    const key = "window-theme";
    try testing.expect(ghostty_config_get(&cfg, @ptrCast(&out), key, key.len));
    const str = std.mem.sliceTo(out, 0);
    try testing.expectEqualStrings("dark", str);
}

test "ghostty_config_get: optional null returns false" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var cfg = try Config.default(alloc);
    defer cfg.deinit();
    cfg.@"unfocused-split-fill" = null;

    var out: Config.Color.C = undefined;
    const key = "unfocused-split-fill";
    try testing.expect(!ghostty_config_get(&cfg, @ptrCast(&out), key, key.len));
}

test "ghostty_config_get: unknown key returns false" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var cfg = try Config.default(alloc);
    defer cfg.deinit();

    var out = false;
    const key = "not-a-real-key";
    try testing.expect(!ghostty_config_get(&cfg, &out, key, key.len));
}

test "ghostty_config_get: optional string null returns true" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var cfg = try Config.default(alloc);
    defer cfg.deinit();
    cfg.title = null;

    var out: ?[*:0]const u8 = undefined;
    const key = "title";
    try testing.expect(ghostty_config_get(&cfg, @ptrCast(&out), key, key.len));
    try testing.expect(out == null);
}

test "ghostty_config_get: float" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var cfg = try Config.default(alloc);
    defer cfg.deinit();
    cfg.@"background-opacity" = 0.42;

    var out: f64 = 0;
    const key = "background-opacity";
    try testing.expect(ghostty_config_get(&cfg, &out, key, key.len));
    try testing.expectApproxEqAbs(@as(f64, 0.42), out, 0.000001);
}

test "ghostty_config_get: struct cval conversion" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var cfg = try Config.default(alloc);
    defer cfg.deinit();
    cfg.background = .{ .r = 12, .g = 34, .b = 56 };

    var out: Config.Color.C = undefined;
    const key = "background";
    try testing.expect(ghostty_config_get(&cfg, @ptrCast(&out), key, key.len));
    try testing.expectEqual(@as(u8, 12), out.r);
    try testing.expectEqual(@as(u8, 34), out.g);
    try testing.expectEqual(@as(u8, 56), out.b);
}

test "ghostty_config_keybinds: count and get" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var cfg = try Config.default(alloc);
    defer cfg.deinit();
    const count = ghostty_config_keybinds_count(&cfg);
    try testing.expect(count > 0);

    const kb = ghostty_config_get_keybind(&cfg, 0);
    try testing.expect(kb.step_count >= 1);
    try testing.expect(kb.action[0] != 0);

    const oob = ghostty_config_get_keybind(&cfg, count);
    try testing.expectEqual(@as(u32, 0), oob.step_count);
}

test "ghostty_config_trigger: default keybind" {
    const testing = std.testing;

    var cfg = try Config.default(testing.allocator);
    defer cfg.deinit();

    // Default commands should be fetchable through config_trigger_
    {
        const trigger = try config_trigger_(&cfg, "open_config");
        try testing.expectEqual(.unicode, trigger.tag);
        try testing.expectEqual(@as(u32, ','), trigger.key.unicode);
    }
    {
        const trigger = try config_trigger_(&cfg, "reload_config");
        try testing.expectEqual(.unicode, trigger.tag);
        try testing.expectEqual(@as(u32, ','), trigger.key.unicode);
    }
    // Performable bindings are not tracked in the reverse map,
    // so config_trigger_ should return a default (empty) trigger.
    if (comptime builtin.target.os.tag.isDarwin()) {
        const next = try config_trigger_(&cfg, "navigate_search:next");
        try testing.expectEqual(.physical, next.tag);
        try testing.expectEqual(.unidentified, next.key.physical);

        const prev = try config_trigger_(&cfg, "navigate_search:previous");
        try testing.expectEqual(.physical, prev.tag);
        try testing.expectEqual(.unidentified, prev.key.physical);
    }
    {
        const trigger = try config_trigger_(&cfg, "adjust_selection:left");
        try testing.expectEqual(.physical, trigger.tag);
        try testing.expectEqual(.unidentified, trigger.key.physical);
    }
}
