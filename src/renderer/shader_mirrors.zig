//! Comptime scanners tying shader-side mirrors of renderer data back to the
//! Zig definitions they were hand-copied from. Each backend calls these from
//! a `comptime` block beside its own definitions, against shader source it
//! embeds itself, so drift fails the build where the copy lives.
//!
//! Everything here scans comment-stripped source: a commented-out old
//! definition must not satisfy a pin, or the classic edit (comment the old
//! line, change the live one) would ship drift green.

const std = @import("std");
const meta_compat = @import("../meta_compat.zig");

/// `src` with line and block comments blanked. The comment characters are
/// replaced with spaces so offsets inside the remainder keep their spacing;
/// nothing here depends on absolute offsets.
fn stripComments(comptime src: []const u8) []const u8 {
    comptime {
        var out: [src.len]u8 = undefined;
        var i: usize = 0;
        while (i < src.len) {
            if (src[i] == '/' and i + 1 < src.len and src[i + 1] == '/') {
                while (i < src.len and src[i] != '\n') : (i += 1) out[i] = ' ';
                continue;
            }
            if (src[i] == '/' and i + 1 < src.len and src[i + 1] == '*') {
                out[i] = ' ';
                out[i + 1] = ' ';
                i += 2;
                while (i < src.len) : (i += 1) {
                    if (src[i] == '*' and i + 1 < src.len and src[i + 1] == '/') {
                        out[i] = ' ';
                        out[i + 1] = ' ';
                        i += 2;
                        break;
                    }
                    out[i] = ' ';
                }
                continue;
            }
            out[i] = src[i];
            i += 1;
        }
        const frozen = out;
        return &frozen;
    }
}

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
pub fn hasMirrored(comptime src: []const u8, comptime name: []const u8, comptime value: []const u8) bool {
    const stripped = comptime stripComments(src);
    var i: usize = 0;
    while (std.mem.indexOfPos(u8, stripped, i, name)) |at| {
        i = at + name.len;
        if (at > 0 and isIdentChar(stripped[at - 1])) continue;
        if (i < stripped.len and isIdentChar(stripped[i])) continue;

        var j = i;
        while (j < stripped.len and (stripped[j] == ' ' or stripped[j] == '\t')) j += 1;
        if (j < stripped.len and stripped[j] == '=') {
            j += 1;
            while (j < stripped.len and (stripped[j] == ' ' or stripped[j] == '\t')) j += 1;
        }
        if (j + value.len > stripped.len) continue;
        if (!std.mem.eql(u8, stripped[j .. j + value.len], value)) continue;
        const end = j + value.len;
        if (end < stripped.len and isIdentChar(stripped[end])) continue;

        // The value must run to the end of its statement: a strict prefix
        // of a longer expression ("3u" for "3u << 4") is drift, not a
        // mirror.
        var k = end;
        while (k < stripped.len and (stripped[k] == ' ' or stripped[k] == '\t')) k += 1;
        if (k < stripped.len and stripped[k] != ',' and stripped[k] != ';' and
            stripped[k] != '\n' and stripped[k] != '\r') continue;
        return true;
    }
    return false;
}

/// `hasMirrored` with a drift message naming the shader and the definition.
pub fn expectMirrored(
    comptime src: []const u8,
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

/// The `i`-prefixed camelCase block member name as the Zig field spells it:
/// `iSelectionForegroundColor` -> `selection_foreground_color`.
fn defaultZigName(comptime glsl: []const u8) []const u8 {
    comptime {
        var start: usize = 0;
        if (glsl.len > 1 and glsl[0] == 'i' and std.ascii.isUpper(glsl[1])) start = 1;
        var out: []const u8 = "";
        for (glsl[start..]) |c| {
            if (std.ascii.isUpper(c)) {
                if (out.len > 0) out = out ++ "_";
                out = out ++ &[_]u8{std.ascii.toLower(c)};
            } else {
                out = out ++ &[_]u8{c};
            }
        }
        return out;
    }
}

fn zigNameFor(comptime glsl: []const u8, comptime renames: anytype) []const u8 {
    inline for (renames) |pair| {
        if (comptime std.mem.eql(u8, pair[0], glsl)) return pair[1];
    }
    return defaultZigName(glsl);
}

/// True when every scalar in `T` (through arrays and vectors) is a float, or
/// every scalar is an integer. Used to catch a float member swapped for an
/// int one at the same offset and size.
fn zigFloatClass(comptime T: type) bool {
    return switch (@typeInfo(T)) {
        .float => true,
        .int => false,
        .bool => false,
        .vector => |v| @typeInfo(v.child) == .float,
        .array => |a| zigFloatClass(a.child),
        .@"struct" => blk: {
            var acc: ?bool = null;
            inline for (meta_compat.structFields(T)) |field| {
                const f = zigFloatClass(field.type);
                if (acc == null) acc = f else if (acc.? != f) @compileError(
                    "mixed float and integer fields; the scanner cannot class " ++
                        @typeName(T),
                );
            }
            break :blk acc orelse false;
        },
        else => @compileError("cannot class " ++ @typeName(T)),
    };
}

const Member = struct { type: []const u8, name: []const u8, array: usize };

/// Parse the members of `uniform <block> { ... }` from comment-stripped
/// `src`, in declaration order, failing the build when a line does not
/// parse. Uniform blocks do not nest.
fn blockMembers(
    comptime src: []const u8,
    comptime block: []const u8,
    comptime what: []const u8,
) []const Member {
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

    comptime var members: []const Member = &.{};
    var line = at + open.len;
    while (std.mem.indexOfScalarPos(u8, src, line, '\n')) |nl| {
        if (nl >= close) break;
        const trimmed = std.mem.trim(u8, src[line..nl], " \t\r");
        if (std.mem.startsWith(u8, trimmed, "uniform ")) {
            const rest = std.mem.trim(u8, trimmed["uniform ".len..], " \t");
            const sp = std.mem.indexOfAny(u8, rest, " \t") orelse @compileError(
                std.fmt.comptimePrint(
                    "shader mirror drift in {s}: unparsed member in block {s}: {s}",
                    .{ what, block, trimmed },
                ),
            );
            const glsl_type = rest[0..sp];
            var name = std.mem.trim(u8, rest[sp..], " \t");
            var array: usize = 1;
            if (std.mem.indexOfScalar(u8, name, '[')) |lb| {
                const rb = std.mem.indexOfScalarPos(u8, name, lb, ']') orelse @compileError(
                    std.fmt.comptimePrint(
                        "shader mirror drift in {s}: unparsed array in block {s}: {s}",
                        .{ what, block, trimmed },
                    ),
                );
                array = std.fmt.parseInt(usize, name[lb + 1 .. rb], 10) catch @compileError(
                    std.fmt.comptimePrint(
                        "shader mirror drift in {s}: unparsed array length in block {s}: {s}",
                        .{ what, block, trimmed },
                    ),
                );
                name = name[0..lb];
            }
            name = std.mem.trim(u8, name, " \t;");
            members = members ++ &[_]Member{.{ .type = glsl_type, .name = name, .array = array }};
        }
        line = nl + 1;
    }
    return members;
}

/// Assert the std140 uniform block `block` declares exactly `expected`
/// members, counted as its `uniform ` lines.
pub fn expectBlockMemberCount(
    comptime src: []const u8,
    comptime block: []const u8,
    comptime expected: usize,
    comptime what: []const u8,
) void {
    const stripped = comptime stripComments(src);
    const members = comptime blockMembers(stripped, block, what);
    if (members.len != expected) {
        @compileError(std.fmt.comptimePrint(
            "shader mirror drift in {s}: uniform block {s} declares {d} members, " ++
                "expected {d}; update the shader copy or the Zig definition",
            .{ what, block, members.len, expected },
        ));
    }
}

/// Hold the whole `uniform <block>` in `src` to the extern struct `T` that
/// the CPU writes byte-for-byte: same members, same order, same std140
/// offsets, same total size, and floats on one side never integers on the
/// other. Members whose names genuinely differ pass `renames` as
/// `.{ .{ "glsl_name", "zig_name" }, ... }` pairs; a renamed pair skips the
/// float/int class check, so a rename is also the place to document a packed
/// spelling (`grid_size_packed_2u16` against a `[2]u16` field).
pub fn expectBlockLayout(
    comptime src: []const u8,
    comptime block: []const u8,
    comptime T: type,
    comptime renames: anytype,
    comptime what: []const u8,
) void {
    const stripped = comptime stripComments(src);
    const members = comptime blockMembers(stripped, block, what);
    const fields = meta_compat.structFields(T);

    if (members.len != fields.len) {
        @compileError(std.fmt.comptimePrint(
            "shader mirror drift in {s}: uniform block {s} declares {d} members " ++
                "({s}) but {s} has {d} fields ({s}); the two are one block",
            .{
                what,       block,         members.len, memberNames(members), @typeName(T),
                fields.len, fieldNames(T),
            },
        ));
    }

    @setEvalBranchQuota(100000);
    var offset: usize = 0;
    var max_align: usize = 1;
    inline for (members, 0..) |member, i| {
        const field = fields[i];
        const zig_name = comptime zigNameFor(member.name, renames);
        if (!comptime std.mem.eql(u8, zig_name, field.name)) {
            @compileError(std.fmt.comptimePrint(
                "shader mirror drift in {s}: member {d} of block {s} is {s}, which " ++
                    "maps to \"{s}\", but {s} declares \"{s}\" there; the block and " ++
                    "the struct are written in one order or they are misaligned",
                .{ what, i, block, member.name, zig_name, @typeName(T), field.name },
            ));
        }

        // std140 placement of this member. An array's base alignment is its
        // element's rounded up to 16, the same rounding its stride takes.
        const elem = std140Elem(member.type, what);
        const is_array = member.array > 1 or isGlslArrayType(member.type);
        const stride = if (is_array) std.mem.alignForward(usize, elem.size, 16) else elem.size;
        const member_align = if (is_array) @max(elem.align_, 16) else elem.align_;
        const total = stride * member.array;
        offset = std.mem.alignForward(usize, offset, member_align);
        const at = offset;
        offset += total;
        max_align = @max(max_align, member_align);

        const actual = @offsetOf(T, field.name);
        if (actual != at) {
            @compileError(std.fmt.comptimePrint(
                "shader mirror drift in {s}: std140 puts {s} at offset {d} but " ++
                    "{s}.{s} sits at {d}; the two layouts must agree",
                .{ what, member.name, at, @typeName(T), field.name, actual },
            ));
        }
        // The Zig side mirrors a vec3 as either [3]f32 (packing the next
        // float at +12) or [4]f32 (padding the slot to 16), so member sizes
        // are checked only through the offsets they force and the total.

        // Same size does not mean same class: a float member swapped for an
        // integer one at the same offset still reads garbage.
        var renamed = false;
        inline for (renames) |pair| {
            if (comptime std.mem.eql(u8, pair[0], member.name)) renamed = true;
        }
        if (!renamed) {
            const glsl_float = !std.mem.eql(u8, member.type, "int") and
                !std.mem.eql(u8, member.type, "uint");
            if (glsl_float != zigFloatClass(field.type)) {
                @compileError(std.fmt.comptimePrint(
                    "shader mirror drift in {s}: {s} is {s} but {s}.{s} is {s}; " ++
                        "one side changed type, or the pair needs a rename entry",
                    .{ what, member.name, member.type, @typeName(T), field.name, @typeName(field.type) },
                ));
            }
        }
    }
    const size = std.mem.alignForward(usize, offset, max_align);
    if (@sizeOf(T) != size) {
        @compileError(std.fmt.comptimePrint(
            "shader mirror drift in {s}: block {s} lays out to {d} bytes but " ++
                "{s} is {d}; the two layouts must agree",
            .{ what, block, size, @typeName(T), @sizeOf(T) },
        ));
    }
}

fn isGlslArrayType(comptime t: []const u8) bool {
    return std.mem.indexOfScalar(u8, t, '[') != null;
}

fn std140Elem(comptime t: []const u8, comptime what: []const u8) struct { size: usize, align_: usize } {
    if (std.mem.eql(u8, t, "float") or std.mem.eql(u8, t, "int") or
        std.mem.eql(u8, t, "uint") or std.mem.eql(u8, t, "bool"))
    {
        return .{ .size = 4, .align_ = 4 };
    }
    if (std.mem.eql(u8, t, "vec2")) return .{ .size = 8, .align_ = 8 };
    if (std.mem.eql(u8, t, "vec3")) return .{ .size = 12, .align_ = 16 };
    if (std.mem.eql(u8, t, "vec4")) return .{ .size = 16, .align_ = 16 };
    if (std.mem.eql(u8, t, "mat4")) return .{ .size = 64, .align_ = 16 };
    @compileError(std.fmt.comptimePrint(
        "shader mirror drift in {s}: no std140 layout known for GLSL type \"{s}\"; " ++
            "add it to std140Elem rather than guessing",
        .{ what, t },
    ));
}

fn memberNames(comptime members: []const Member) []const u8 {
    comptime {
        var out: []const u8 = "";
        for (members, 0..) |m, i| {
            if (i > 0) out = out ++ ", ";
            out = out ++ m.name;
        }
        return out;
    }
}

fn fieldNames(comptime T: type) []const u8 {
    comptime {
        var out: []const u8 = "";
        for (meta_compat.structFields(T), 0..) |f, i| {
            if (i > 0) out = out ++ ", ";
            out = out ++ f.name;
        }
        return out;
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

    // A commented-out definition is not a mirror.
    try testing.expect(!hasMirrored("// #define NO_MIN_CONTRAST 1u\n", "NO_MIN_CONTRAST", "1u"));
    try testing.expect(!hasMirrored(
        "/* #define NO_MIN_CONTRAST 1u */\n#define NO_MIN_CONTRAST 2u\n",
        "NO_MIN_CONTRAST",
        "1u",
    ));
}

test "stripComments blanks line and block comments" {
    const testing = std.testing;
    const src =
        \\uniform vec3 a; // trailing
        \\/* multi
        \\   line */ uniform vec3 b;
    ;
    const stripped = comptime stripComments(src);
    try testing.expect(std.mem.indexOf(u8, stripped, "trailing") == null);
    try testing.expect(std.mem.indexOf(u8, stripped, "uniform vec3 b;") != null);
}
