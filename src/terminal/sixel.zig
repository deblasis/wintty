//! Types and functions related to the DEC Sixel image protocol.
//!
//! Sixel is a DEC-defined inline image protocol delivered via DCS
//! escape sequences (ESC P [Pa;Pb;Ph] q ... ESC \). This module
//! is the re-export surface; implementation lives under
//! `src/terminal/sixel/`.
//!
//! Re-exports are added as submodules land.

pub const Command = @import("sixel/command.zig").Command;
pub const PaintOp = @import("sixel/command.zig").PaintOp;
pub const PaletteOp = @import("sixel/command.zig").PaletteOp;
pub const Raster = @import("sixel/command.zig").Raster;
pub const parseRasterAttribs = @import("sixel/raster.zig").parseRasterAttribs;
pub const RasterError = @import("sixel/raster.zig").Error;
pub const MAX_RGBA_BYTES = @import("sixel/raster.zig").MAX_RGBA_BYTES;
pub const Palette = @import("sixel/palette.zig").Palette;
pub const Rgba = @import("sixel/palette.zig").Rgba;
pub const Parser = @import("sixel/parser.zig").Parser;

test {
    @import("std").testing.refAllDecls(@This());
}
