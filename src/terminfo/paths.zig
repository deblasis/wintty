//! Shared, import-free path facts about Ghostty's terminfo entry.
//!
//! The build graph (`src/build/GhosttyResources.zig`) and the runtime
//! (`src/os/resourcesdir.zig`) both import this file, so it imports nothing
//! but `std`, and it has to stay that way: `terminfo/main.zig` reaches
//! `global.zig` through `install.zig`, and `global.zig` needs the build's
//! generated modules that the build graph itself cannot provide.
//!
//! The entry names are spelled here once. The runtime table in `ghostty.zig`
//! comptime-asserts that its own names match, so renaming the entry there
//! fails the build until this file moves with it, and every path derived
//! below moves with the names.

const std = @import("std");

/// The TERM name of the terminfo entry: the first name `ghostty.zig` lists,
/// and the value a child's TERM carries. The hashed database layouts file
/// the entry under a directory derived from its first character.
pub const term_name = "xterm-ghostty";

/// The entry's preferred name: the second name `ghostty.zig` lists. The
/// installed source file and the letter-layout database leaves are named
/// after it.
pub const preferred_name = "ghostty";

/// The names of the terminfo entry, in the order `ghostty.zig` lists them.
pub const names = [_][]const u8{ term_name, preferred_name };

/// The directory, relative to the resources share directory, that holds the
/// compiled terminfo database.
pub const db_dir = "terminfo";

/// FreeBSD's package convention installs the database under `site-terminfo`
/// instead.
pub const freebsd_db_dir = "site-terminfo";

/// The uncompiled terminfo source file, named after the preferred name. A
/// Windows install ships only this file (there is no `tic` there), and it is
/// the one file the Windows resources-directory detection names.
pub const source_file = preferred_name ++ ".terminfo";

/// The Windows sentinel, relative to the share directory: the uncompiled
/// source under the database directory. `GhosttyResources.windows_required`
/// installs exactly `share/` plus this and comptime-asserts the two agree.
pub const windows_sentinel = db_dir ++ "/" ++ source_file;

/// Where tic's directory-per-letter database layout puts an entry named
/// `name`: a directory named for the entry's first character. This is the
/// layout the Linux and FreeBSD package trees are detected by.
fn letterLeaf(comptime name: []const u8) []const u8 {
    return std.fmt.comptimePrint("{c}/{s}", .{ name[0], name });
}

/// Where tic's hashed database layout puts an entry named `name`: the first
/// character as two lowercase hex digits. This is what `tic -x` writes
/// (measured: `78/xterm-ghostty`), and `termio.Exec.terminfoHasEntry`
/// derives the same shape at runtime when it reads a database back.
fn hashedLeaf(comptime name: []const u8) []const u8 {
    return std.fmt.comptimePrint("{x:0>2}/{s}", .{ name[0], name });
}

/// The compiled database leaves, relative to the database directory, in the
/// order the resources-directory detectors probe them: the preferred name
/// first, then the TERM name.
pub const compiled_leaves = [_][]const u8{
    letterLeaf(preferred_name),
    letterLeaf(term_name),
};

/// The single compiled leaf the macOS app bundle tree is detected by.
pub const macos_compiled_leaf = hashedLeaf(term_name);

test "terminfo paths derive from the entry names" {
    try std.testing.expectEqualStrings("xterm-ghostty", term_name);
    try std.testing.expectEqualStrings("ghostty", preferred_name);
    try std.testing.expectEqualStrings("terminfo", db_dir);
    try std.testing.expectEqualStrings("site-terminfo", freebsd_db_dir);
    try std.testing.expectEqualStrings("ghostty.terminfo", source_file);
    try std.testing.expectEqualStrings("terminfo/ghostty.terminfo", windows_sentinel);
    try std.testing.expectEqualStrings("g/ghostty", compiled_leaves[0]);
    try std.testing.expectEqualStrings("x/xterm-ghostty", compiled_leaves[1]);
    try std.testing.expectEqualStrings("78/xterm-ghostty", macos_compiled_leaf);
}
