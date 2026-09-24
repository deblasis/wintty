//! The environment a new terminal starts from on Windows.
//!
//! Windows Terminal rebuilds every new tab's environment from the registry at
//! the moment the tab is created (`reloadEnvironmentVariables`, on by
//! default). The terminal's own process environment never reaches the tab.
//! That matters because a desktop app's environment is frozen when it is
//! launched: a variable the user adds, edits or removes afterwards, or a PATH
//! entry an installer writes, is invisible to it, and an app relaunched by its
//! own updater carries whatever its predecessor held, however old.
//!
//! This module gives Wintty the same rule. `paneEnv` starts from
//! `CreateEnvironmentBlock(token, FALSE)` for the current user: the block a
//! fresh logon composes (system and user variables, both PATH scopes merged,
//! volatile entries in, values expanded). The only things carried over from
//! Wintty's own process are
//!
//!   - the test harness's variables (`XDG_CONFIG_HOME` and every `WINTTY_*`
//!     key), and only while the harness marker `WINTTY_TEST_CONFIG` is set,
//!     so a test run's panes stay inside the run;
//!   - a stripped `NO_COLOR`: when the app removes it from its own environment
//!     to turn color on, it also sets `WINTTY_NO_COLOR_STRIPPED`, and new
//!     terminals then drop the registry's NO_COLOR too. A NO_COLOR that was
//!     only absent when Wintty started is not a strip: one set in the system
//!     settings afterwards reaches new terminals.
//!
//! Everything a terminal adds on top (TERM, GHOSTTY_RESOURCES_DIR, the `env`
//! config entries) is applied later by termio, exactly as before.
//!
//! The `reload-env` config key turns this off, which gives every terminal
//! Wintty's own environment instead.

const std = @import("std");
const builtin = @import("builtin");
const Allocator = std.mem.Allocator;
const EnvMap = std.process.Environ.Map;

/// Set by the test harness on every process it launches. While it is set,
/// the harness's own variables are carried into new terminals.
pub const test_marker = "WINTTY_TEST_CONFIG";

/// Set in the app's own environment when it strips NO_COLOR on purpose.
pub const no_color_stripped = "WINTTY_NO_COLOR_STRIPPED";

/// True for the variables the test harness uses to keep a run isolated:
/// the config root and Wintty's own `WINTTY_*` keys (state base, daemon
/// pipe, log and data locations, the marker itself).
pub fn isTestLever(key: []const u8) bool {
    if (std.ascii.eqlIgnoreCase(key, no_color_stripped)) return false;
    if (std.ascii.eqlIgnoreCase(key, "XDG_CONFIG_HOME")) return true;
    return key.len > "WINTTY_".len and std.ascii.startsWithIgnoreCase(key, "WINTTY_");
}

/// Compose a terminal's base environment: `fresh` (the logon block), plus
/// the test levers from `process` while the harness marker is set, minus
/// `NO_COLOR` when the app has stripped it. Pure, so the rule
/// is testable without a logon block. The result is allocated with `alloc`
/// and owned by the caller.
pub fn compose(
    alloc: Allocator,
    fresh: *const EnvMap,
    process: *const EnvMap,
) Allocator.Error!EnvMap {
    var out = try fresh.clone(alloc);
    errdefer out.deinit();

    if (process.get(test_marker) != null) {
        var it = process.iterator();
        while (it.next()) |entry| {
            if (isTestLever(entry.key_ptr.*)) try out.put(entry.key_ptr.*, entry.value_ptr.*);
        }
    }

    if (process.get(no_color_stripped) != null) _ = out.orderedRemove("NO_COLOR");

    return out;
}

pub const FreshError = Allocator.Error || error{LogonEnvironmentUnavailable};

/// The environment a fresh logon composes for the current user, read now.
/// Windows only.
pub fn freshLogonMap(alloc: Allocator) FreshError!EnvMap {
    if (comptime builtin.os.tag != .windows) return error.LogonEnvironmentUnavailable;

    const create = userenv.createEnvironmentBlock() orelse
        return error.LogonEnvironmentUnavailable;

    var token: std.os.windows.HANDLE = undefined;
    if (advapi32.OpenProcessToken(
        std.os.windows.GetCurrentProcess(),
        TOKEN_QUERY | TOKEN_DUPLICATE,
        &token,
    ) == .FALSE) return error.LogonEnvironmentUnavailable;
    defer std.os.windows.CloseHandle(token);

    // bInherit FALSE: the block a logon composes, not this process's
    // variables layered on top of it. That is the whole point.
    var block: ?[*:0]u16 = null;
    if (create.create(&block, token, .FALSE) == .FALSE)
        return error.LogonEnvironmentUnavailable;
    const ptr = block orelse return error.LogonEnvironmentUnavailable;
    defer _ = create.destroy(ptr);

    var map: EnvMap = .init(alloc);
    errdefer map.deinit();
    try map.putWindowsBlock(.{ .ptr = ptr });
    return map;
}

/// A new terminal's base environment on Windows: the fresh logon block
/// composed with `process` (see `compose`).
pub fn paneEnv(alloc: Allocator, process: *const EnvMap) FreshError!EnvMap {
    var fresh = try freshLogonMap(alloc);
    defer fresh.deinit();
    return compose(alloc, &fresh, process);
}

// ---------- Win32 --------------------------------------------------------

const TOKEN_DUPLICATE: std.os.windows.DWORD = 0x0002;
const TOKEN_QUERY: std.os.windows.DWORD = 0x0008;

const advapi32 = struct {
    extern "advapi32" fn OpenProcessToken(
        ProcessHandle: std.os.windows.HANDLE,
        DesiredAccess: std.os.windows.DWORD,
        TokenHandle: *std.os.windows.HANDLE,
    ) callconv(.winapi) std.os.windows.BOOL;
};

/// userenv.dll is resolved at run time rather than linked: libghostty also
/// ships as a static library, and a link-time import would make every static
/// consumer add userenv.lib to its own link.
const userenv = struct {
    const CreateFn = *const fn (
        lpEnvironment: *?[*:0]u16,
        hToken: ?std.os.windows.HANDLE,
        bInherit: std.os.windows.BOOL,
    ) callconv(.winapi) std.os.windows.BOOL;
    const DestroyFn = *const fn (lpEnvironment: [*:0]u16) callconv(.winapi) std.os.windows.BOOL;

    const Fns = struct { create: CreateFn, destroy: DestroyFn };

    const kernel32 = struct {
        extern "kernel32" fn LoadLibraryExW(
            lpLibFileName: [*:0]const u16,
            hFile: ?std.os.windows.HANDLE,
            dwFlags: std.os.windows.DWORD,
        ) callconv(.winapi) ?std.os.windows.HMODULE;
        extern "kernel32" fn GetProcAddress(
            hModule: std.os.windows.HMODULE,
            lpProcName: [*:0]const u8,
        ) callconv(.winapi) ?*const anyopaque;
    };

    const LOAD_LIBRARY_SEARCH_SYSTEM32: std.os.windows.DWORD = 0x0000_0800;

    var create_addr = std.atomic.Value(usize).init(0);
    var destroy_addr = std.atomic.Value(usize).init(0);

    /// The two entry points, or null when userenv.dll or either export is
    /// missing. Resolved once per process from System32 only, so a new
    /// terminal does not take another module reference each time; racing
    /// callers store the same addresses.
    fn createEnvironmentBlock() ?Fns {
        if (create_addr.load(.acquire) == 0 or destroy_addr.load(.acquire) == 0) {
            const name = std.unicode.utf8ToUtf16LeStringLiteral("userenv.dll");
            const module = kernel32.LoadLibraryExW(name, null, LOAD_LIBRARY_SEARCH_SYSTEM32) orelse return null;
            const create = kernel32.GetProcAddress(module, "CreateEnvironmentBlock") orelse return null;
            const destroy = kernel32.GetProcAddress(module, "DestroyEnvironmentBlock") orelse return null;
            create_addr.store(@intFromPtr(create), .release);
            destroy_addr.store(@intFromPtr(destroy), .release);
        }
        return .{
            .create = @ptrFromInt(create_addr.load(.acquire)),
            .destroy = @ptrFromInt(destroy_addr.load(.acquire)),
        };
    }
};

// ---------- Tests --------------------------------------------------------

const testing = std.testing;

fn mapOf(alloc: Allocator, pairs: []const [2][]const u8) !EnvMap {
    var m: EnvMap = .init(alloc);
    errdefer m.deinit();
    for (pairs) |p| try m.put(p[0], p[1]);
    return m;
}

test "compose starts from the fresh block, not the process" {
    const alloc = testing.allocator;
    var fresh = try mapOf(alloc, &.{
        .{ "PATH", "C:\\Windows;C:\\Users\\me\\bin" },
        .{ "POSH_THEMES_PATH", "C:\\themes" },
    });
    defer fresh.deinit();
    // A stale launcher: an old PATH, no POSH_THEMES_PATH, and a variable
    // it set itself.
    var process = try mapOf(alloc, &.{
        .{ "PATH", "C:\\Windows" },
        .{ "FOO", "1" },
    });
    defer process.deinit();

    var out = try compose(alloc, &fresh, &process);
    defer out.deinit();

    try testing.expectEqualStrings("C:\\Windows;C:\\Users\\me\\bin", out.get("PATH").?);
    try testing.expectEqualStrings("C:\\themes", out.get("POSH_THEMES_PATH").?);
    try testing.expect(out.get("FOO") == null);
    try testing.expectEqual(@as(usize, 2), out.count());
}

test "compose carries the harness variables only under the marker" {
    const alloc = testing.allocator;
    var fresh = try mapOf(alloc, &.{.{ "PATH", "C:\\Windows" }});
    defer fresh.deinit();

    var unmarked = try mapOf(alloc, &.{
        .{ "XDG_CONFIG_HOME", "C:\\tmp\\cfg" },
        .{ "WINTTY_STATE_BASE", "C:\\tmp\\state" },
    });
    defer unmarked.deinit();
    {
        var out = try compose(alloc, &fresh, &unmarked);
        defer out.deinit();
        try testing.expect(out.get("XDG_CONFIG_HOME") == null);
        try testing.expect(out.get("WINTTY_STATE_BASE") == null);
    }

    var marked = try mapOf(alloc, &.{
        .{ "WINTTY_TEST_CONFIG", "1" },
        .{ "XDG_CONFIG_HOME", "C:\\tmp\\cfg" },
        .{ "WINTTY_STATE_BASE", "C:\\tmp\\state" },
        .{ "UNRELATED", "x" },
    });
    defer marked.deinit();
    {
        var out = try compose(alloc, &fresh, &marked);
        defer out.deinit();
        try testing.expectEqualStrings("1", out.get("WINTTY_TEST_CONFIG").?);
        try testing.expectEqualStrings("C:\\tmp\\cfg", out.get("XDG_CONFIG_HOME").?);
        try testing.expectEqualStrings("C:\\tmp\\state", out.get("WINTTY_STATE_BASE").?);
        try testing.expect(out.get("UNRELATED") == null);
    }
}

test "compose drops NO_COLOR only when the app stripped it" {
    const alloc = testing.allocator;
    var fresh = try mapOf(alloc, &.{.{ "NO_COLOR", "1" }});
    defer fresh.deinit();

    // The app stripped it (and said so): the registry's NO_COLOR goes too.
    var stripped = try mapOf(alloc, &.{.{ no_color_stripped, "1" }});
    defer stripped.deinit();
    {
        var out = try compose(alloc, &fresh, &stripped);
        defer out.deinit();
        try testing.expect(out.get("NO_COLOR") == null);
        try testing.expect(out.get(no_color_stripped) == null);
    }

    // Absent at launch, no strip: a NO_COLOR the user set in the system
    // settings since reaches the terminal, as in a Windows Terminal tab.
    var absent = try mapOf(alloc, &.{});
    defer absent.deinit();
    {
        var out = try compose(alloc, &fresh, &absent);
        defer out.deinit();
        try testing.expectEqualStrings("1", out.get("NO_COLOR").?);
    }
}
test "compose never adds NO_COLOR the logon block lacks" {
    const alloc = testing.allocator;
    var fresh = try mapOf(alloc, &.{.{ "PATH", "C:\\Windows" }});
    defer fresh.deinit();
    var process = try mapOf(alloc, &.{.{ "NO_COLOR", "1" }});
    defer process.deinit();
    var out = try compose(alloc, &fresh, &process);
    defer out.deinit();
    try testing.expect(out.get("NO_COLOR") == null);
}

test "isTestLever" {
    try testing.expect(isTestLever("XDG_CONFIG_HOME"));
    try testing.expect(isTestLever("xdg_config_home"));
    try testing.expect(isTestLever("WINTTY_TEST_CONFIG"));
    try testing.expect(isTestLever("wintty_sessiond_pipe"));
    try testing.expect(!isTestLever("WINTTY_"));
    try testing.expect(!isTestLever("PATH"));
    try testing.expect(!isTestLever("XDG_DATA_HOME"));
    try testing.expect(!isTestLever(no_color_stripped));
}

test "freshLogonMap reads a real logon block" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    // A variable only this process holds. bInherit FALSE means a logon block
    // never carries it; with TRUE (this process's block layered on, so a
    // launcher's `FOO=1` reaches every terminal) SystemRoot and PATH would
    // still be there, and only this probe fails.
    const k32 = struct {
        extern "kernel32" fn SetEnvironmentVariableW(n: [*:0]const u16, v: ?[*:0]const u16) callconv(.winapi) std.os.windows.BOOL;
    };
    const probe = std.unicode.utf8ToUtf16LeStringLiteral("WINDOWS_LOGON_ENV_LAUNCHER_ONLY");
    _ = k32.SetEnvironmentVariableW(probe, std.unicode.utf8ToUtf16LeStringLiteral("1"));
    defer _ = k32.SetEnvironmentVariableW(probe, null);

    const alloc = testing.allocator;
    var map = try freshLogonMap(alloc);
    defer map.deinit();
    // Every logon block names the system root and carries a PATH.
    try testing.expect(map.get("SystemRoot") != null);
    try testing.expect(map.get("PATH") != null);
    try testing.expect(map.get("WINDOWS_LOGON_ENV_LAUNCHER_ONLY") == null);
}
