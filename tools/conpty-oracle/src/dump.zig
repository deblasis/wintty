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

/// Comparison dump for pitting one transport against another (ConPTY vs
/// raw pipe). Unlike `dump`, this isolates *transport fidelity* from the
/// preamble a host injects: ConPTY emits an OSC 4 palette + DECSET modes
/// at startup that a raw-pipe stream never carries, and resolving palette
/// indices to RGB would let each terminal's palette table leak in. So:
///   - `.palette = null` -> a cell using color index N emits `38;5;N` in
///     BOTH dumps regardless of palette (color *intent* is compared, not
///     the palette table), while truecolor cells stay RGB;
///   - `.extra = .none` -> only the grid content with per-cell SGR, no
///     palette/modes/tabstops preamble and no cursor/charset trailer (the
///     grid and per-cell styles always live in the content, not `extra`).
/// The cursor position is appended manually so cursor / alt-screen
/// divergence is still caught.
pub fn dumpCells(alloc: Allocator, bytes: []const u8, cols: u16, rows: u16) ![]u8 {
    var t: vt.Terminal = try .init(alloc, .{ .cols = cols, .rows = rows });
    defer t.deinit(alloc);

    var stream = t.vtStream();
    defer stream.deinit();
    stream.nextSlice(bytes);

    // Visual-identity normalization. conhost paints a blank space after a
    // colored word with that word's foreground (`red ` stays red through
    // the space), where raw VT resets first. A space has no foreground
    // glyph, so this is invisible -- but it registers as a cell diff. So
    // reset any blank `.codepoint` cell (space or empty) whose *only*
    // non-default attributes are invisible on a blank cell (fg, bold,
    // italic, faint, blink). Cells with a background, inverse, or a line
    // decoration (underline/strikethrough/overline) ARE visible when blank
    // and are left untouched. Both transports get the same normalization,
    // so any surviving diff is a genuinely visible one.
    {
        var it = t.screens.active.pages.rowIterator(.right_down, .{ .screen = .{} }, null);
        while (it.next()) |pin| {
            for (pin.cells(.all)) |*cell| {
                if (cell.style_id == 0) continue; // already default
                if (cell.content_tag != .codepoint) continue; // grapheme/wide/bg cell
                const cp = cell.codepoint();
                if (cp != ' ' and cp != 0) continue; // has a visible glyph
                const st = pin.style(cell);
                if (std.meta.activeTag(st.bg_color) != .none) continue; // bg is visible
                if (st.flags.inverse) continue; // inverse shows fg as bg
                if (st.flags.underline != .none) continue;
                if (st.flags.strikethrough) continue;
                if (st.flags.overline) continue;
                cell.style_id = 0; // remaining attrs are invisible on a blank cell
            }
        }
    }

    var f: vt.formatter.TerminalFormatter = .init(&t, .{
        .emit = .vt,
        .unwrap = false,
        .trim = false,
        .palette = null, // indices stay indices; fair across differing palettes
    });
    f.extra = .none; // grid content only, no host preamble/trailer

    var out: std.Io.Writer.Allocating = .init(alloc);
    defer out.deinit();
    try f.format(&out.writer);

    const cur = t.screens.active.cursor;
    try out.writer.print("\n#cursor x={} y={} pending_wrap={} screen={s}\n", .{
        cur.x, cur.y, cur.pending_wrap, @tagName(t.screens.active_key),
    });

    return out.toOwnedSlice();
}
