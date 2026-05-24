//! Types and functions related to the DEC Sixel image protocol.
//!
//! Sixel is a DEC-defined inline image protocol delivered via DCS
//! escape sequences (ESC P [Pa;Pb;Ph] q ... ESC \). This module
//! is the re-export surface; implementation lives under
//! `src/terminal/sixel/`.
//!
//! Re-exports are added as each sub-module lands across the PR
//! stack.

test {
    @import("std").testing.refAllDecls(@This());
}
