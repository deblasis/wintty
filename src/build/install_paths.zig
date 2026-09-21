//! Comparing install paths, split out so it can be tested.
//!
//! It lives in its own file because `src/build/test.zig` roots a module at
//! `src/build/`, and anything it imports has to stay inside that path.
//! `GhosttyResources.zig` reaches out to `../apprt`, `../font` and more
//! through Config.zig and SharedDeps.zig, so importing it there does not
//! compile. Only std is used here.

const std = @import("std");

/// Whether two install paths, both relative to the install prefix, name the
/// same place.
///
/// The build writes these with the host separator, so on a Windows host they
/// come back with backslashes while the paths they are checked against are
/// written the way the rest of the build code writes paths. Comparing the
/// bytes directly made the Windows install guard fail against its own build.
pub fn samePath(a: []const u8, b: []const u8) bool {
    if (a.len != b.len) return false;
    for (a, b) |x, y| {
        const nx = if (x == '\\') '/' else x;
        const ny = if (y == '\\') '/' else y;
        if (nx != ny) return false;
    }
    return true;
}

test "samePath ignores which separator the host wrote" {
    const testing = std.testing;

    try testing.expect(samePath(
        "share\\terminfo\\ghostty.terminfo",
        "share/terminfo/ghostty.terminfo",
    ));
    try testing.expect(samePath(
        "ghostty/shell-integration",
        "ghostty\\shell-integration",
    ));
    try testing.expect(samePath("ghostty/themes", "ghostty/themes"));
}

test "samePath does not treat different paths as equal" {
    const testing = std.testing;

    try testing.expect(!samePath("ghostty/themes", "ghostty/shell-integration"));
    // Length is part of it: a trailing separator is a different string, and
    // the guard compares against an exact install path.
    try testing.expect(!samePath("ghostty/shell-integration", "ghostty/shell-integration/"));
    try testing.expect(!samePath("", "a"));
    try testing.expect(samePath("", ""));
}
