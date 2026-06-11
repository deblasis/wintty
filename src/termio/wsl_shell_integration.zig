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
