const std = @import("std");
const Config = @import("../../config/Config.zig");
const help_strings = @import("help_strings");

pub fn main(init: std.process.Init) !void {
    var buffer: [2048]u8 = undefined;
    var stdout_writer = std.Io.File.stdout().writer(init.io, &buffer);
    const stdout = &stdout_writer.interface;
    try genConfig(stdout);
    try stdout.flush();
}

/// Options the Windows app cannot use, and that do not carry the `macos-`
/// name prefix.
///
/// Every entry here was checked twice before it was added: its doc comment
/// in Config.zig rules Windows out, *and* nothing under
/// `windows/` reads the key. Both halves matter. Prose alone is not enough,
/// and neither is the prefix.
///
/// If the Windows app ever starts reading one of these, delete it from the
/// list so the option shows up in the reference again.
const unsupported_names = [_][]const u8{
    // "Draw fonts with a thicker stroke, if supported. This is currently
    // only supported on macOS." A CoreText stroke tweak; the DirectWrite
    // path has no equivalent and never reads it.
    "font-thicken",
    // Strength dial for font-thicken, and dead for the same reason.
    "font-thicken-strength",

    // "This setting is currently only supported on macOS." Display P3
    // interpretation of terminal colors, done by the macOS layer.
    "window-colorspace",

    // "Note: this is only supported on macOS. The GTK runtime does not
    // support setting the window position". Both halves of the pair are
    // listed: -y is grouped under -x and would be dropped along with it,
    // but naming it keeps the two consistent if the field order ever
    // changes.
    "window-position-x",
    "window-position-y",

    // "Resize the window in discrete increments of the focused surface's
    // cell size ... Currently only supported on macOS."
    "window-step-resize",

    // "This setting is only supported currently on macOS." The Windows
    // renderer has its own present model and does not read the key.
    "window-vsync",

    // "Only implemented on macOS." Describes how the quick terminal
    // follows macOS Spaces, which Windows has no counterpart for.
    "quick-terminal-space-behavior",

    // "This is only supported on macOS currently, since Linux builds are
    // distributed via package managers". Sparkle-backed updates; Wintty
    // ships no updater that reads this.
    "auto-update",
    // The release channel for that same updater: "This only works on macOS
    // since only macOS has an auto-update feature."
    "auto-update-channel",

    // "Only implemented on Linux and macOS." Not macOS-only, but just as
    // unusable here: nothing under windows/ reads it, and whether an initial
    // window opens is decided by the shell itself.
    "initial-window",
};

/// Whether an option has no place in the Wintty reference, because the
/// Windows app cannot act on it.
///
/// Two signals, both exact: the `macos-` name prefix, which is always
/// Windows-irrelevant, and the hand-checked
/// list above. Matching prose like "only supported on macOS" looks tempting
/// and is wrong: that sentence describes *upstream's* platform support, not
/// this fork's. `window-save-state` and `undo-timeout` both carry it, and
/// the Windows app implements both -- filtering on prose would have hidden
/// options Windows users actually set. `quick-terminal-animation-duration`
/// says "Only implemented on macOS" and Windows reads it too.
///
/// Options that merely mention macOS while working everywhere are left
/// alone. Rewriting their prose to drop the macOS clause is not something a
/// generator can do safely, since the surrounding sentence usually depends
/// on it.
fn isUnsupportedOnWindows(comptime name: []const u8) bool {
    if (std.mem.startsWith(u8, name, "macos-")) return true;
    for (unsupported_names) |candidate| {
        if (std.mem.eql(u8, name, candidate)) return true;
    }
    return false;
}

pub fn genConfig(writer: *std.Io.Writer) !void {
    // Write the header
    try writer.writeAll(
        \\---
        \\title: Reference
        \\description: Reference of all Ghostty configuration options.
        \\editOnGithubLink: https://github.com/ghostty-org/ghostty/edit/main/src/config/Config.zig
        \\---
        \\
        \\This is a reference of the Ghostty configuration options. These
        \\options are ordered roughly by how common they are to be used
        \\and grouped with related options. I recommend utilizing your
        \\browser's search functionality to find the option you're looking
        \\for.
        \\
        \\Options Wintty cannot act on, including every macOS-only one,
        \\are omitted. Options unique to this build are listed
        \\under
        \\[Windows-Only Options](/docs/config/windows-only).
        \\
        \\In the future, we'll have a more user-friendly way to view and
        \\organize these options.
        \\
        \\
    );

    @setEvalBranchQuota(200_000);
    const fields = @typeInfo(Config).@"struct".fields;
    inline for (fields, 0..) |field, i| {
        if (field.name[0] == '_') continue;
        if (!@hasDecl(help_strings.Config, field.name)) continue;

        // Skipping a documented field also drops the undocumented fields
        // grouped under it, since those are only reachable through the
        // inner loop below.
        if (comptime isUnsupportedOnWindows(field.name)) continue;

        // Write the field name.
        try writer.writeAll("## `");
        try writer.writeAll(field.name);
        try writer.writeAll("`\n");

        // For all subsequent fields with no docs, they are grouped
        // with the previous field.
        if (i + 1 < fields.len) {
            inline for (fields[i + 1 ..]) |next_field| {
                if (next_field.name[0] == '_') break;
                if (@hasDecl(help_strings.Config, next_field.name)) break;

                // A macOS-only field can be grouped under an option we are
                // keeping. Drop its header without ending the group.
                if (comptime isUnsupportedOnWindows(next_field.name)) continue;

                try writer.writeAll("## `");
                try writer.writeAll(next_field.name);
                try writer.writeAll("`\n");
            }
        }

        // Newline after our headers
        try writer.writeAll("\n");

        var iter = std.mem.splitScalar(
            u8,
            @field(help_strings.Config, field.name),
            '\n',
        );

        // We do some really rough markdown "parsing" here so that
        // we can fix up some styles for what our website expects.
        var block: ?enum {
            /// Plaintext, do nothing.
            text,

            /// Code block, wrap in triple backticks. We use indented
            /// code blocks in our comments but the website parser only
            /// supports triple backticks.
            code,

            /// Callouts. We detect these based on paragraphs starting
            /// with "Note:", "Warning:", etc. (case-insensitive).
            callout_note,
            callout_warning,
        } = null;

        while (iter.next()) |s| {
            // Empty line resets our block
            if (std.mem.eql(u8, s, "")) {
                try endBlock(writer, block);
                block = null;

                try writer.writeAll("\n");
                continue;
            }

            // If we don't have a block figure out our type.
            const first: bool = block == null;
            if (block == null) {
                if (std.mem.startsWith(u8, s, "    ")) {
                    block = .code;
                    try writer.writeAll("```\n");
                } else if (std.ascii.startsWithIgnoreCase(s, "note:")) {
                    block = .callout_note;
                    try writer.writeAll("<Note>\n");
                } else if (std.ascii.startsWithIgnoreCase(s, "warning:")) {
                    block = .callout_warning;
                    try writer.writeAll("<Warning>\n");
                } else {
                    block = .text;
                }
            }

            try writer.writeAll(switch (block.?) {
                .text => s,
                .callout_note => if (first) s["note:".len..] else s,
                .callout_warning => if (first) s["warning:".len..] else s,

                .code => if (std.mem.startsWith(u8, s, "    "))
                    s[4..]
                else
                    s,
            });
            try writer.writeAll("\n");
        }
        try endBlock(writer, block);
        try writer.writeAll("\n");
    }
}

fn endBlock(writer: *std.Io.Writer, block: anytype) !void {
    if (block) |v| switch (v) {
        .text => {},
        .code => try writer.writeAll("```\n"),
        .callout_note => try writer.writeAll("</Note>\n"),
        .callout_warning => try writer.writeAll("</Warning>\n"),
    };
}
