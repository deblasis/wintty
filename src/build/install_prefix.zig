//! 0.17 compatibility for install-prefix strings.
//!
//! `Build.install_prefix`/`install_path`/`getInstallPath` no longer exist at
//! configure time: install locations are `LazyPath.relative` bases resolved
//! during the make phase. The remaining configure-time users here compose
//! *text* for generated packaging metadata (pkg-config `.pc`, desktop files,
//! xcframework bundles).
//!
//! `placeholder` keeps those generators compiling and is only consumed on
//! Darwin/Linux packaging paths; wiring them to the real prefix is a make-time
//! job (emit `${pcfiledir}`-relative prefixes, or pass the install directory as
//! a Run directory arg).

const std = @import("std");

pub const placeholder = "/usr/local";

/// Compile-time stand-in for `Build.getInstallPath` (removed in 0.17).
/// Only for generated metadata and packaging-only steps; live steps should
/// pass a `LazyPath{ .relative = .{ .base = .install_lib } }` argument instead.
pub fn installPath(
    b: *std.Build,
    dir: std.Build.InstallDir,
    sub_path: []const u8,
) []const u8 {
    const dir_name = switch (dir) {
        .prefix => "",
        .lib => "lib",
        .bin => "bin",
        .header => "include",
        .custom => |p| p,
    };
    if (dir_name.len == 0) return b.pathJoin(&.{ placeholder, sub_path });
    return b.pathJoin(&.{ placeholder, dir_name, sub_path });
}
