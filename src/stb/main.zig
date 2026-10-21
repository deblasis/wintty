// 0.17 removed @cImport; the build generates the bindings as the `stb_c`
// module (see SharedDeps.addImports / translate_c).
const c = @import("stb_c");

// We'll just add the exports of the functions or types we actually use
// here, no need to export everything from the C lib if we don't use it.
pub const stbir_resize_uint8 = c.stbir_resize_uint8;
