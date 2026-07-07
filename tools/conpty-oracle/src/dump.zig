//! Canonical grid dump: feed captured VT bytes into the ghostty-vt
//! terminal model and format the resulting state deterministically.
//!
//! The dump is the identity function for the oracle: two programs are
//! "cell-identical under ConPTY" iff their dumps are byte-identical.

const std = @import("std");
const Allocator = std.mem.Allocator;
const vt = @import("ghostty-vt");

/// Parse `bytes` into a cols x rows terminal and return the canonical
/// full-fidelity dump. Caller owns the result.
pub fn dump(alloc: Allocator, bytes: []const u8, cols: u16, rows: u16) ![]u8 {
    var t: vt.Terminal = try .init(alloc, .{ .cols = cols, .rows = rows });
    defer t.deinit(alloc);

    // One stream across the whole byte slice so escape sequences split
    // across read boundaries parse correctly.
    var stream = t.vtStream();
    defer stream.deinit();
    stream.nextSlice(bytes);

    var f: vt.formatter.TerminalFormatter = .init(&t, .{
        .emit = .vt, // full styles/colors as SGR
        .unwrap = false,
        .trim = false, // canonical fixed shape
        .palette = &t.colors.palette.current,
    });
    f.extra = .all; // palette+modes+region+tabstops+pwd+keyboard
    f.extra.screen = .all; // cursor CUP + style + hyperlink + DECSCA + kitty-kbd + charsets

    var out: std.Io.Writer.Allocating = .init(alloc);
    defer out.deinit();
    try f.format(&out.writer);

    // Append state the VT dump omits, for full identity.
    try out.writer.print("\n#cursor pending_wrap={} screen={s}\n", .{
        t.screens.active.cursor.pending_wrap,
        @tagName(t.screens.active_key),
    });

    return out.toOwnedSlice();
}
