//! Fonts that can be embedded with Ghostty. Note they are only actually
//! embedded in the binary if they are referenced by the code, so fonts
//! used for tests will not result in the final binary being larger.
//!
//! Be careful to ensure that any fonts you embed are licensed for
//! redistribution and include their license as necessary.

/// Default fonts that we prefer for Ghostty.
pub const variable = @embedFile("jetbrains_mono_variable");
pub const variable_italic = @embedFile("jetbrains_mono_variable_italic");

/// Symbols-only nerd font.
pub const symbols_nerd_font = @embedFile("nerd_fonts_symbols_only");

/// Static jetbrains mono faces. `regular` is used by tests; `bold`,
/// `italic`, and `bold_italic` are currently unused.
pub const regular = @embedFile("jetbrains_mono_regular");
pub const bold = @embedFile("jetbrains_mono_bold");
pub const italic = @embedFile("jetbrains_mono_italic");
pub const bold_italic = @embedFile("jetbrains_mono_bold_italic");

/// Emoji fonts
pub const emoji = @embedFile("res/NotoColorEmoji.ttf");
pub const emoji_text = @embedFile("res/NotoEmoji-Regular.ttf");

// Fonts below are ONLY used for testing.

/// Fonts with general properties
pub const arabic = @embedFile("res/KawkabMono-Regular.ttf");

/// A font for testing which is patched with nerd font symbols.
pub const test_nerd_font = @embedFile("res/JetBrainsMonoNerdFont-Regular.ttf");

/// Specific font families below:
pub const code_new_roman = @embedFile("res/CodeNewRoman-Regular.otf");
pub const inconsolata = @embedFile("res/Inconsolata-Regular.ttf");
pub const geist_mono = @embedFile("res/GeistMono-Regular.ttf");
pub const jetbrains_mono = @embedFile("res/JetBrainsMonoNoNF-Regular.ttf");
pub const julia_mono = @embedFile("res/JuliaMono-Regular.ttf");

/// Cozette is a unique font because it embeds some emoji characters
/// but has a text presentation.
pub const cozette = @embedFile("res/CozetteVector.ttf");

/// Monaspace has weird ligature behaviors we want to test in our shapers
/// so we embed it here.
pub const monaspace_neon = @embedFile("res/MonaspaceNeon-Regular.otf");

/// Terminus TTF is a scalable font with bitmap glyphs at various sizes.
pub const terminus_ttf = @embedFile("res/TerminusTTF-Regular.ttf");

/// Spleen is a monospaced bitmap font available in multiple formats.
/// Used for testing bitmap font support across different file formats.
pub const spleen_bdf = @embedFile("res/spleen-8x16.bdf");
pub const spleen_pcf = @embedFile("res/spleen-8x16.pcf");
pub const spleen_otb = @embedFile("res/spleen-8x16.otb");

test "variable face defaults to the Regular instance" {
    // lib-vt source archives intentionally exclude full Ghostty font fixtures.
    if (comptime @import("terminal_options").artifact == .lib) return error.SkipZigTest;

    const std = @import("std");
    const sfnt = @import("opentype/sfnt.zig");
    const alloc = std.testing.allocator;

    // The inspector hands this face to imgui without pinning a variation
    // axis, so FreeType renders whatever the fvar default instance is. If
    // that stops being Regular the entire inspector UI changes weight, and
    // nothing else in the tree would notice: the terminal faces pin `wght`
    // explicitly in SharedGridSet.
    const face = try sfnt.SFNT.init(variable, alloc);
    defer face.deinit(alloc);

    const fvar = face.getTable("fvar").?;
    const axes_offset = std.mem.readInt(u16, fvar[4..6], .big);
    const axis_count = std.mem.readInt(u16, fvar[8..10], .big);
    const axis_size = std.mem.readInt(u16, fvar[10..12], .big);

    var weight_axes: usize = 0;
    for (0..axis_count) |i| {
        const axis = fvar[axes_offset + i * axis_size ..][0..axis_size];
        if (!std.mem.eql(u8, axis[0..4], "wght")) continue;
        weight_axes += 1;

        const default: sfnt.Fixed = @bitCast(
            std.mem.readInt(i32, axis[8..12], .big),
        );
        try std.testing.expectEqual(400, default.int);
    }
    try std.testing.expectEqual(1, weight_axes);
}
