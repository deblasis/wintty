//! Emits the wintty logo above CLI action output using the kitty graphics
//! protocol. Detection is conservative: we only emit when stdout is a TTY
//! *and* the env vars positively identify a terminal we know supports kitty
//! graphics. The APC payload is supposed to be ignored by terminals that
//! don't recognize it, but some older terminals print the base64 chunks as
//! garbage on screen, so a false-positive is much worse than a false-negative.
//!
//! The embedded `wintty_logo.png` is a copy of `images/icons/icon_128.png`.
//! Keep it in sync when the app icon changes (no automatic sync step — Zig
//! `@embedFile` is restricted to the source package, and a build-time copy
//! step isn't worth the wiring for an asset that turns over this rarely).

const std = @import("std");
const Allocator = std.mem.Allocator;
const build_config = @import("../build_config.zig");

const logo_png = @embedFile("wintty_logo.png");

/// Returns true when stdout is a TTY connected to a terminal we have positive
/// reason to believe supports the kitty graphics protocol. Allocates a
/// throwaway env map; +actions only run once per process so the cost is
/// negligible.
pub fn supported(alloc: Allocator, stdout: std.fs.File) bool {
    if (!stdout.isTty()) return false;

    var env = std.process.getEnvMap(alloc) catch return false;
    defer env.deinit();

    // Wintty sets TERM_PROGRAM=<build_config.term_program> when spawning a
    // shell; "ghostty" stays in the match list so shells inherited from
    // pre-rebrand binaries still get the logo. WezTerm uses the same var.
    if (env.get("TERM_PROGRAM")) |v| {
        if (std.mem.eql(u8, v, build_config.term_program)) return true;
        if (std.mem.eql(u8, v, "ghostty")) return true;
        if (std.mem.eql(u8, v, "WezTerm")) return true;
    }

    // Real kitty sets KITTY_WINDOW_ID; nothing else does.
    if (env.get("KITTY_WINDOW_ID") != null) return true;

    // TERM=xterm-kitty or similar.
    if (env.get("TERM")) |term| {
        if (std.mem.indexOf(u8, term, "kitty") != null) return true;
    }

    return false;
}

/// Emit the logo PNG via the kitty graphics protocol, followed by enough
/// newlines to move the cursor below the rendered image. Caller is responsible
/// for checking `supported()` first.
pub fn write(alloc: Allocator, writer: *std.Io.Writer) !void {
    const Base64 = std.base64.standard.Encoder;
    const encoded_len = Base64.calcSize(logo_png.len);
    const encoded = try alloc.alloc(u8, encoded_len);
    defer alloc.free(encoded);
    _ = Base64.encode(encoded, logo_png);

    // Render the image in a fixed 16-column x 8-row cell area. This keeps the
    // logo a predictable size across fonts, and tells us exactly how many
    // newlines we need afterwards to move the cursor past the image.
    const cols = 16;
    const rows = 8;

    // 4096 base64 chars is the kitty-recommended chunk size.
    const chunk_size = 4096;
    var offset: usize = 0;
    while (offset < encoded.len) {
        const end = @min(offset + chunk_size, encoded.len);
        const more: u8 = if (end < encoded.len) '1' else '0';

        if (offset == 0) {
            // f=100: PNG. a=T: transmit and display. t=d: direct (inline)
            // data. c/r: target display size in cells. m: more chunks follow.
            try writer.print(
                "\x1b_Gf=100,a=T,t=d,c={d},r={d},m={c};{s}\x1b\\",
                .{ cols, rows, more, encoded[offset..end] },
            );
        } else {
            try writer.print(
                "\x1b_Gm={c};{s}\x1b\\",
                .{ more, encoded[offset..end] },
            );
        }
        offset = end;
    }

    // Kitty advances the cursor by the image's row height after rendering
    // (default a=T behavior). One blank line is enough for breathing room
    // between the logo and following text.
    try writer.writeByte('\n');
}
