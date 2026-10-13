//! Comptime scanners tying shader-side mirrors of renderer data back to the
//! Zig definitions they were hand-copied from. Each backend calls these from
//! a `comptime` block beside its own definitions, against shader source it
//! embeds itself, so drift fails the build where the copy lives.

const std = @import("std");

/// True when `src` holds `name` as a whole identifier followed by optional
/// whitespace, an optional `=`, optional whitespace, and the exact literal
/// `value` ending at a non-identifier character. Matches the three mirror
/// shapes these shaders use alike:
///
///     #define NO_MIN_CONTRAST  1u
///     const uint NO_MIN_CONTRAST = 1u;
///     NO_MIN_CONTRAST = 1u,
///
/// The identifier boundaries keep longer names (`CURSORSTYLE_BLOCK` vs
/// `CURSORSTYLE_BLOCK_HOLLOW`) and plain usage sites from matching.
pub fn hasMirrored(src: []const u8, comptime name: []const u8, comptime value: []const u8) bool {
    var i: usize = 0;
    while (std.mem.indexOfPos(u8, src, i, name)) |at| {
        i = at + name.len;
        if (at > 0 and isIdentChar(src[at - 1])) continue;
        if (i < src.len and isIdentChar(src[i])) continue;

        var j = i;
        while (j < src.len and (src[j] == ' ' or src[j] == '\t')) j += 1;
        if (j < src.len and src[j] == '=') {
            j += 1;
            while (j < src.len and (src[j] == ' ' or src[j] == '\t')) j += 1;
        }
        if (j + value.len > src.len) continue;
        if (!std.mem.eql(u8, src[j .. j + value.len], value)) continue;
        const end = j + value.len;
        if (end < src.len and isIdentChar(src[end])) continue;
        return true;
    }
    return false;
}

/// `hasMirrored` with a drift message naming the shader and the definition.
pub fn expectMirrored(
    src: []const u8,
    comptime name: []const u8,
    comptime value: []const u8,
    comptime what: []const u8,
) void {
    if (!hasMirrored(src, name, value)) {
        @compileError(std.fmt.comptimePrint(
            "shader mirror drift in {s}: expected \"{s} {s}\" in the shader source; " ++
                "update the shader copy or the Zig definition",
            .{ what, name, value },
        ));
    }
}

/// Assert the std140 uniform block `block` declares exactly `expected`
/// members, counted as its `uniform ` lines.
pub fn expectBlockMemberCount(
    src: []const u8,
    comptime block: []const u8,
    comptime expected: usize,
    comptime what: []const u8,
) void {
    const open = "uniform " ++ block ++ " {";
    const at = std.mem.indexOf(u8, src, open) orelse @compileError(
        std.fmt.comptimePrint(
            "shader mirror drift in {s}: uniform block {s} not found in the shader source",
            .{ what, block },
        ),
    );
    const close = std.mem.indexOfPos(u8, src, at, "};") orelse @compileError(
        std.fmt.comptimePrint(
            "shader mirror drift in {s}: uniform block {s} is never closed",
            .{ what, block },
        ),
    );

    var count: usize = 0;
    var line = at + open.len;
    while (std.mem.indexOfScalarPos(u8, src, line, '\n')) |nl| {
        if (nl >= close) break;
        const trimmed = std.mem.trim(u8, src[line..nl], " \t\r");
        if (std.mem.startsWith(u8, trimmed, "uniform ")) count += 1;
        line = nl + 1;
    }

    if (count != expected) {
        @compileError(std.fmt.comptimePrint(
            "shader mirror drift in {s}: uniform block {s} declares {d} members, " ++
                "expected {d}; update the shader copy or the Zig definition",
            .{ what, block, count, expected },
        ));
    }
}

/// Assert `@sizeOf(T)` still matches the layout the shader side assumes.
pub fn expectSize(
    comptime T: type,
    comptime expected: usize,
    comptime what: []const u8,
) void {
    const actual = @sizeOf(T);
    if (actual != expected) {
        @compileError(std.fmt.comptimePrint(
            "shader mirror drift in {s}: @sizeOf({s}) is {d}, expected {d}; " ++
                "update the shader copy or the Zig definition",
            .{ what, @typeName(T), actual, expected },
        ));
    }
}

/// Assert `field` of `T` still sits at the byte offset the shader side
/// assumes.
pub fn expectOffset(
    comptime T: type,
    comptime field: []const u8,
    comptime expected: usize,
    comptime what: []const u8,
) void {
    const actual = @offsetOf(T, field);
    if (actual != expected) {
        @compileError(std.fmt.comptimePrint(
            "shader mirror drift in {s}: @{s} of {s} is at offset {d}, expected {d}; " ++
                "update the shader copy or the Zig definition",
            .{ what, field, @typeName(T), actual, expected },
        ));
    }
}

/// Uppercase an identifier at comptime ("block_hollow" -> "BLOCK_HOLLOW").
pub fn upper(comptime in: []const u8) []const u8 {
    comptime {
        var out: [in.len]u8 = undefined;
        for (in, 0..) |c, i| out[i] = std.ascii.toUpper(c);
        const frozen = out;
        return &frozen;
    }
}

fn isIdentChar(c: u8) bool {
    return std.ascii.isAlphanumeric(c) or c == '_';
}

test "hasMirrored matches every mirror shape and rejects lookalikes" {
    const testing = std.testing;

    // The three shapes: #define, const, enum member.
    try testing.expect(hasMirrored("#define NO_MIN_CONTRAST  1u\n", "NO_MIN_CONTRAST", "1u"));
    try testing.expect(hasMirrored("const uint NO_MIN_CONTRAST = 1u;\n", "NO_MIN_CONTRAST", "1u"));
    try testing.expect(hasMirrored("  NO_MIN_CONTRAST = 1u,\n", "NO_MIN_CONTRAST", "1u"));

    // Shifted values keep the whole literal.
    try testing.expect(hasMirrored("BG_IMAGE_NO_FIT = 3u << 4,\n", "BG_IMAGE_NO_FIT", "3u << 4"));
    try testing.expect(!hasMirrored("BG_IMAGE_NO_FIT = 3u << 4,\n", "BG_IMAGE_NO_FIT", "3u"));

    // Longer names, other values, usage sites and comments do not match.
    try testing.expect(!hasMirrored("#define CURSORSTYLE_BLOCK_HOLLOW 1\n", "CURSORSTYLE_BLOCK", "1"));
    try testing.expect(!hasMirrored("#define NO_MIN_CONTRAST  2u\n", "NO_MIN_CONTRAST", "1u"));
    try testing.expect(!hasMirrored("(bools & NO_MIN_CONTRAST) == 0", "NO_MIN_CONTRAST", "1u"));
    try testing.expect(!hasMirrored("carry NO_MIN_CONTRAST and are\n", "NO_MIN_CONTRAST", "1u"));
    try testing.expect(!hasMirrored("SOME_NO_MIN_CONTRAST 1u\n", "NO_MIN_CONTRAST", "1u"));
}
