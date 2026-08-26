//! Test root for the build-time code under src/build/.
//!
//! A test binary collects `test` blocks only from files its own `test` and
//! `comptime` blocks reach. A `pub const` naming the import is not enough: on
//! 0.16 an unreferenced pub decl pulls nothing in. That is why src/build/ gets
//! no coverage from the src/main.zig root -- src/build_config.zig imports
//! build/Config.zig to call fromOptions(), and nothing analysed from there
//! reaches Config.init, which is where GitVersion is used.
//!
//! Rooting these files from that graph instead would mean a comptime block in
//! a file the ghostty binary already collects, which drags std.Build into
//! every compilation of it, wasm targets included. So the build-time files get
//! a root of their own: host target, hung off the same `zig build test` step
//! the rest of the suite runs under, since build-time code runs on the host
//! whatever the build is targeting.
//!
//! One import per file. A file that is not listed here has no tests running.

test {
    _ = @import("GitVersion.zig");
}
