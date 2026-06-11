//! Inject ghostty shell integration into a WSL (`wsl.exe`) session by setting
//! the distro-side env vars (in `/mnt` form) and forwarding them through
//! WSLENV. Covers zsh (ZDOTDIR) and fish (XDG_DATA_DIRS). Login bash is a
//! documented no-op: it ignores `$ENV` without `--posix` (which we cannot
//! inject into wsl.exe), and we will not write into the distro filesystem —
//! the same stance ghostty takes for Apple's patched `/bin/bash` on macOS.
const std = @import("std");
const builtin = @import("builtin");
const Allocator = std.mem.Allocator;
const EnvMap = std.process.EnvMap;
const posix_path = @import("../os/posix_path.zig");

const log = std.log.scoped(.wsl_shell_integration);

/// Inject zsh + fish shell integration for a WSL session. Best-effort: any
/// failure logs and leaves that piece of the env untouched; absence of
/// integration is the existing behavior, never a wrong value. Both vars are
/// injected unconditionally (no login-shell detection): ZDOTDIR is read only by
/// zsh, and an extra XDG_DATA_DIRS entry is inert for non-fish shells.
/// `resource_dir` is the Windows resources dir; `alloc` is expected to be an
/// arena (values are placed into `env`).
pub fn setup(alloc: Allocator, resource_dir: []const u8, env: *EnvMap) !void {
    // Names we actually set, to register in WSLENV at the end.
    var names: std.ArrayListUnmanaged([]const u8) = .{};
    defer names.deinit(alloc);

    // zsh: ZDOTDIR -> <resource_dir>/shell-integration/zsh (holds our .zshenv,
    // which restores the user's real zsh config).
    //
    // Unlike native setupZsh, we do NOT preserve a pre-existing ZDOTDIR into
    // GHOSTTY_ZSH_ZDOTDIR. Any inbound ZDOTDIR here is a Windows-side value,
    // meaningless inside the distro; forwarding it would make our .zshenv
    // "restore" ZDOTDIR to a bogus Windows path in the distro. The distro's
    // own default (unset -> $HOME) is the correct chain target, which leaving
    // GHOSTTY_ZSH_ZDOTDIR unset yields.
    {
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const win = try std.fmt.bufPrint(&buf, "{s}/shell-integration/zsh", .{resource_dir});
        if (dirExists(win)) {
            if (try posix_path.windowsToWsl(alloc, win)) |wsl_path| {
                defer alloc.free(wsl_path);
                try env.put("ZDOTDIR", wsl_path);
                try names.append(alloc, "ZDOTDIR");
            } else log.warn("WSL: resources dir not drive-rooted, zsh skipped: {s}", .{win});
        } else log.warn("WSL: missing {s}, zsh integration skipped", .{win});
    }

    // fish: prepend <resource_dir>/shell-integration to XDG_DATA_DIRS.
    {
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const win = try std.fmt.bufPrint(&buf, "{s}/shell-integration", .{resource_dir});
        if (dirExists(win)) {
            if (try posix_path.windowsToWsl(alloc, win)) |wsl_dir| {
                defer alloc.free(wsl_dir);
                try env.put("GHOSTTY_SHELL_INTEGRATION_XDG_DIR", wsl_dir);
                try names.append(alloc, "GHOSTTY_SHELL_INTEGRATION_XDG_DIR");

                const current = env.get("XDG_DATA_DIRS") orelse "/usr/local/share:/usr/share";
                const joined = if (current.len == 0)
                    try alloc.dupe(u8, wsl_dir)
                else
                    try std.fmt.allocPrint(alloc, "{s}:{s}", .{ wsl_dir, current });
                defer alloc.free(joined);
                try env.put("XDG_DATA_DIRS", joined);
                try names.append(alloc, "XDG_DATA_DIRS");
            } else log.warn("WSL: resources dir not drive-rooted, fish skipped: {s}", .{win});
        } else log.warn("WSL: missing {s}, fish integration skipped", .{win});
    }

    // GHOSTTY_SHELL_FEATURES is set upstream by setupFeatures (omitted when all
    // features are disabled). Forward it iff present.
    if (env.get("GHOSTTY_SHELL_FEATURES") != null) {
        try names.append(alloc, "GHOSTTY_SHELL_FEATURES");
    }

    try appendWslenv(alloc, env, names.items);
}

fn dirExists(path: []const u8) bool {
    var dir = std.fs.openDirAbsolute(path, .{}) catch return false;
    dir.close();
    return true;
}

/// Append `names` (env var names already set in `env`, with no path-translation
/// flags) to the `WSLENV` variable, preserving any existing value. WSL forwards
/// only variables listed in WSLENV into the distro; a flag-less entry passes
/// through verbatim (no path translation), which is what we want — our values
/// are already in `/mnt` form. No-op when `names` is empty.
fn appendWslenv(alloc: Allocator, env: *EnvMap, names: []const []const u8) !void {
    if (names.len == 0) return;

    var buf: std.ArrayListUnmanaged(u8) = .{};
    defer buf.deinit(alloc);

    if (env.get("WSLENV")) |existing| try buf.appendSlice(alloc, existing);
    for (names) |name| {
        if (buf.items.len > 0) try buf.append(alloc, ':');
        try buf.appendSlice(alloc, name);
    }
    // EnvMap.put copies the value, so the temp buf is safe to free after.
    try env.put("WSLENV", buf.items);
}

test "appendWslenv: empty names is a no-op" {
    var env = EnvMap.init(std.testing.allocator);
    defer env.deinit();
    try appendWslenv(std.testing.allocator, &env, &.{});
    try std.testing.expect(env.get("WSLENV") == null);
}

test "appendWslenv: sets WSLENV when none existed" {
    var env = EnvMap.init(std.testing.allocator);
    defer env.deinit();
    try appendWslenv(std.testing.allocator, &env, &.{ "ZDOTDIR", "XDG_DATA_DIRS" });
    try std.testing.expectEqualStrings("ZDOTDIR:XDG_DATA_DIRS", env.get("WSLENV").?);
}

test "appendWslenv: preserves existing WSLENV" {
    var env = EnvMap.init(std.testing.allocator);
    defer env.deinit();
    try env.put("WSLENV", "USERPROFILE/p");
    try appendWslenv(std.testing.allocator, &env, &.{"ZDOTDIR"});
    try std.testing.expectEqualStrings("USERPROFILE/p:ZDOTDIR", env.get("WSLENV").?);
}

test "setup: injects zsh + fish env and registers WSLENV" {
    // The translator only produces a /mnt path from a Windows drive path, so
    // this end-to-end assertion is meaningful only on Windows (tmp realpath is
    // a drive path there). The translator itself is unit-tested cross-platform.
    if (builtin.os.tag != .windows) return error.SkipZigTest;

    const alloc = std.testing.allocator;

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.makePath("shell-integration/zsh");
    const res = try tmp.dir.realpathAlloc(alloc, ".");
    defer alloc.free(res);

    var env = EnvMap.init(alloc);
    defer env.deinit();
    try env.put("GHOSTTY_SHELL_FEATURES", "cursor:blink,title");

    try setup(alloc, res, &env);

    // ZDOTDIR -> /mnt form of <res>/shell-integration/zsh
    {
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const zsh_win = try std.fmt.bufPrint(&buf, "{s}/shell-integration/zsh", .{res});
        const expected = (try posix_path.windowsToWsl(alloc, zsh_win)).?;
        defer alloc.free(expected);
        try std.testing.expectEqualStrings(expected, env.get("ZDOTDIR").?);
    }

    // XDG_DATA_DIRS prefixed with the /mnt integration dir + Linux defaults.
    {
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const integ_win = try std.fmt.bufPrint(&buf, "{s}/shell-integration", .{res});
        const integ = (try posix_path.windowsToWsl(alloc, integ_win)).?;
        defer alloc.free(integ);
        try std.testing.expectEqualStrings(integ, env.get("GHOSTTY_SHELL_INTEGRATION_XDG_DIR").?);
        const xdg = env.get("XDG_DATA_DIRS").?;
        try std.testing.expect(std.mem.startsWith(u8, xdg, integ));
        try std.testing.expect(std.mem.endsWith(u8, xdg, "/usr/local/share:/usr/share"));
    }

    // WSLENV lists all four forwarded vars.
    {
        const wslenv = env.get("WSLENV").?;
        try std.testing.expect(std.mem.indexOf(u8, wslenv, "ZDOTDIR") != null);
        try std.testing.expect(std.mem.indexOf(u8, wslenv, "XDG_DATA_DIRS") != null);
        try std.testing.expect(std.mem.indexOf(u8, wslenv, "GHOSTTY_SHELL_INTEGRATION_XDG_DIR") != null);
        try std.testing.expect(std.mem.indexOf(u8, wslenv, "GHOSTTY_SHELL_FEATURES") != null);
    }
}

test "setup: omits GHOSTTY_SHELL_FEATURES from WSLENV when unset" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    const alloc = std.testing.allocator;

    var tmp = std.testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.makePath("shell-integration/zsh");
    const res = try tmp.dir.realpathAlloc(alloc, ".");
    defer alloc.free(res);

    var env = EnvMap.init(alloc);
    defer env.deinit();
    // No GHOSTTY_SHELL_FEATURES set.

    try setup(alloc, res, &env);

    const wslenv = env.get("WSLENV").?;
    try std.testing.expect(std.mem.indexOf(u8, wslenv, "GHOSTTY_SHELL_FEATURES") == null);
    try std.testing.expect(std.mem.indexOf(u8, wslenv, "ZDOTDIR") != null);
}
