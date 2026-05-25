//! Types and functions related to the DEC Sixel image protocol.
//!
//! Sixel is a DEC-defined inline image protocol delivered via DCS
//! escape sequences (ESC P [Pa;Pb;Ph] q ... ESC \). This module
//! is the re-export surface; implementation lives under
//! `src/terminal/sixel/`.

// Only types with external callers are re-exported. dcs.Handler uses
// Parser as its DCS state and Command as the unhook output. decode +
// Image + DecodeCtx are the decoder API. Palette is consumed by
// decoder.zig directly via its sibling import; it stays re-exported
// here so refAllDecls reaches its tests.
pub const Command = @import("sixel/command.zig").Command;
pub const Parser = @import("sixel/parser.zig").Parser;
pub const Palette = @import("sixel/palette.zig").Palette;
pub const decode = @import("sixel/decoder.zig").decode;
pub const Image = @import("sixel/decoder.zig").Image;
pub const DecodeCtx = @import("sixel/decoder.zig").DecodeCtx;
pub const synthKittyCommand = @import("sixel/image.zig").synthKittyCommand;

test {
    @import("std").testing.refAllDecls(@This());
}
