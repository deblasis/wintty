//! The built-in light/dark theme pair Wintty falls back to when the user
//! has not configured a theme.
//!
//! Upstream Ghostty has no default theme: an unconfigured install is always
//! dark (`#282c34` on the Tomorrow Night palette) whatever the desktop
//! around it is set to. That is a reasonable default on macOS and Linux,
//! where Ghostty is usually installed deliberately by someone who will go
//! on to configure it. On Windows it reads as a bug, because the terminal
//! is frequently the first thing launched on a fresh machine and it lands
//! beside a light-themed shell.
//!
//! So Wintty ships a pair instead, selected from the conditional theme
//! state the app feeds in from the OS. Both halves are applied through the
//! same overlay path a user theme file uses, so anything set in the user's
//! own config still wins.
//!
//! The colours are taken from the application icon: the electric blue of
//! the ghost's glow is the accent, the ghost's own silver is the dark-mode
//! foreground, and the near-black indigo of the icon's corners is the
//! dark-mode field. Every colour here clears WCAG AA (4.5:1) against its
//! background, except the two or three slots programs use as a fill rather
//! than as text: slot 0 in the dark half, slots 7 and 15 in the light half.
//! Those are held to the other half of the same bargain instead -- they must
//! stay distinguishable from the background, and text in the foreground
//! colour drawn on top of them must itself clear AA.
//!
//! Which slots those are flips with the polarity, which is the trap here. A
//! light theme whose "white" slots are dark passes a naive
//! every-slot-against-the-background check and then renders `ESC[47m` as
//! dark-on-dark. `wintty_theme_test.zig` encodes the polarity-aware rule, and
//! separately pins that slot 0 and slot 15 can never collide.

const builtin = @import("builtin");
const conditional = @import("conditional.zig");

/// Whether an unconfigured install gets the built-in pair.
///
/// Windows only: the macOS and Linux builds share this source tree and are
/// expected to behave like upstream Ghostty, whose unconfigured default is
/// the compile-time colours in Config.zig.
pub const enabled = builtin.os.tag == .windows;

/// The theme source for a given desktop colour scheme, in Ghostty config
/// syntax. Parsed by the same iterator that reads a theme file, so any
/// valid config key is allowed here.
pub fn forScheme(theme: conditional.State.Theme) []const u8 {
    return switch (theme) {
        .light => light,
        .dark => dark,
    };
}

pub const dark: []const u8 =
    \\background = #131620
    \\foreground = #d5d9e5
    \\cursor-color = #4babef
    \\selection-background = #2b3350
    \\selection-foreground = #f2f4fa
    \\palette = 0=#2a2f3d
    \\palette = 1=#f0787f
    \\palette = 2=#7fd69b
    \\palette = 3=#edc77a
    \\palette = 4=#4babef
    \\palette = 5=#b98cf0
    \\palette = 6=#5bd5e8
    \\palette = 7=#d5d9e5
    \\palette = 8=#7a8296
    \\palette = 9=#ff9aa0
    \\palette = 10=#9ce6b4
    \\palette = 11=#ffd99a
    \\palette = 12=#7bc5ff
    \\palette = 13=#d3abff
    \\palette = 14=#8ae7f5
    \\palette = 15=#f2f4fa
    \\
;

pub const light: []const u8 =
    \\background = #f4f6fb
    \\foreground = #1e2333
    \\cursor-color = #1668c4
    \\selection-background = #cfe0f5
    \\selection-foreground = #141828
    \\palette = 0=#1e2333
    \\palette = 1=#c0334a
    \\palette = 2=#1f7a4d
    \\palette = 3=#8a6410
    \\palette = 4=#1668c4
    \\palette = 5=#7a3fbf
    \\palette = 6=#0f6e80
    \\palette = 7=#b4bacb
    \\palette = 8=#666e81
    \\palette = 9=#a82a3e
    \\palette = 10=#186540
    \\palette = 11=#73530c
    \\palette = 12=#0f55a6
    \\palette = 13=#65329f
    \\palette = 14=#0b5a69
    \\palette = 15=#cfd5e3
    \\
;

test {
    _ = @import("wintty_theme_test.zig");
}
