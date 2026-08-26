//! Contrast guarantees for the built-in theme pair in `wintty_theme.zig`.
//!
//! These parse the theme source the same way a reader would see it, so they
//! also catch a malformed line, and then hold every colour to a WCAG ratio
//! against its own background. The point is that "the default theme is
//! legible" is a property the next person to retouch the palette has to
//! keep, not a thing that was true once.

const std = @import("std");
const testing = std.testing;
const wintty_theme = @import("wintty_theme.zig");

/// WCAG 2.x relative luminance of an sRGB colour.
fn luminance(rgb: [3]u8) f64 {
    var channels: [3]f64 = undefined;
    for (rgb, &channels) |raw, *out| {
        const c = @as(f64, @floatFromInt(raw)) / 255.0;
        out.* = if (c <= 0.03928)
            c / 12.92
        else
            std.math.pow(f64, (c + 0.055) / 1.055, 2.4);
    }
    return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
}

fn contrast(a: [3]u8, b: [3]u8) f64 {
    const la = luminance(a);
    const lb = luminance(b);
    return (@max(la, lb) + 0.05) / (@min(la, lb) + 0.05);
}

fn parseHex(s: []const u8) ![3]u8 {
    const body = if (s.len > 0 and s[0] == '#') s[1..] else s;
    if (body.len != 6) return error.BadHexLength;
    return .{
        try std.fmt.parseInt(u8, body[0..2], 16),
        try std.fmt.parseInt(u8, body[2..4], 16),
        try std.fmt.parseInt(u8, body[4..6], 16),
    };
}

const Parsed = struct {
    background: [3]u8 = undefined,
    foreground: [3]u8 = undefined,
    cursor: [3]u8 = undefined,
    selection_background: [3]u8 = undefined,
    selection_foreground: [3]u8 = undefined,
    palette: [16][3]u8 = undefined,
    palette_seen: [16]bool = @splat(false),
};

/// Minimal reader for the subset of config syntax the theme source uses.
/// Deliberately strict: an unrecognised key is an error rather than a
/// silent skip, so a typo in the theme fails the test instead of leaving
/// a colour at its compile-time default.
fn parse(source: []const u8) !Parsed {
    var out: Parsed = .{};
    var seen_background = false;

    var lines = std.mem.tokenizeScalar(u8, source, '\n');
    while (lines.next()) |raw| {
        const line = std.mem.trim(u8, raw, " \r\t");
        if (line.len == 0) continue;

        const eq = std.mem.indexOfScalar(u8, line, '=') orelse
            return error.MissingEquals;
        const key = std.mem.trim(u8, line[0..eq], " ");
        const value = std.mem.trim(u8, line[eq + 1 ..], " ");

        if (std.mem.eql(u8, key, "background")) {
            out.background = try parseHex(value);
            seen_background = true;
        } else if (std.mem.eql(u8, key, "foreground")) {
            out.foreground = try parseHex(value);
        } else if (std.mem.eql(u8, key, "cursor-color")) {
            out.cursor = try parseHex(value);
        } else if (std.mem.eql(u8, key, "selection-background")) {
            out.selection_background = try parseHex(value);
        } else if (std.mem.eql(u8, key, "selection-foreground")) {
            out.selection_foreground = try parseHex(value);
        } else if (std.mem.eql(u8, key, "palette")) {
            const inner = std.mem.indexOfScalar(u8, value, '=') orelse
                return error.MissingPaletteIndex;
            const idx = try std.fmt.parseInt(u8, value[0..inner], 10);
            if (idx >= 16) return error.PaletteIndexOutOfRange;
            out.palette[idx] = try parseHex(value[inner + 1 ..]);
            out.palette_seen[idx] = true;
        } else {
            return error.UnknownKey;
        }
    }

    if (!seen_background) return error.MissingBackground;
    for (out.palette_seen) |seen| if (!seen) return error.IncompletePalette;
    return out;
}

/// WCAG AA for body text. Everything a program can put on screen as text
/// has to clear this against the theme's own background.
const aa_text = 4.5;

fn expectAtLeast(actual: f64, minimum: f64) !void {
    if (actual >= minimum) return;
    std.debug.print(
        "contrast {d:.2} is below the required {d:.2}\n",
        .{ actual, minimum },
    );
    return error.InsufficientContrast;
}

fn checkTheme(source: []const u8) !void {
    const t = try parse(source);

    try expectAtLeast(contrast(t.background, t.foreground), aa_text);
    try expectAtLeast(contrast(t.background, t.cursor), aa_text);
    try expectAtLeast(
        contrast(t.selection_background, t.selection_foreground),
        aa_text,
    );

    // Slot 0 is the "black" slot. Programs use it as a fill behind other
    // colours rather than as text, and on a dark theme it sits close to the
    // background by convention, so it cannot be held to the text rule. It
    // still has to be told apart from the background, which is the failure
    // that would actually matter: a slot 0 equal to the background makes
    // anything drawn in it disappear.
    for (t.palette, 0..) |color, i| {
        if (i == 0) {
            try testing.expect(contrast(t.background, color) > 1.2);
            continue;
        }
        expectAtLeast(contrast(t.background, color), aa_text) catch |err| {
            std.debug.print("palette slot {d} failed\n", .{i});
            return err;
        };
    }
}

test "built-in dark theme is legible" {
    try checkTheme(wintty_theme.dark);
}

test "built-in light theme is legible" {
    try checkTheme(wintty_theme.light);
}

test "the two halves actually differ in polarity" {
    const d = try parse(wintty_theme.dark);
    const l = try parse(wintty_theme.light);

    // A pair whose halves are both dark would pass every contrast test
    // above and still defeat the entire point of having a pair.
    try testing.expect(luminance(d.background) < 0.1);
    try testing.expect(luminance(l.background) > 0.7);
}

test "forScheme selects the matching half" {
    try testing.expectEqualStrings(
        wintty_theme.dark,
        wintty_theme.forScheme(.dark),
    );
    try testing.expectEqualStrings(
        wintty_theme.light,
        wintty_theme.forScheme(.light),
    );
}
