//! Terminfo source format. This can be used to encode terminfo files.
//! This cannot parse terminfo source files yet because it isn't something
//! I need to do but this can be added later.
//!
//! Background: https://invisible-island.net/ncurses/man/terminfo.5.html

const Source = @This();

const std = @import("std");

/// The set of names for the terminal. These match the TERM environment variable
/// and are used to look up this terminal. Historically, the final name in the
/// list was the most common name for the terminal and contains spaces and
/// other characters. See terminfo(5) for details.
names: []const [:0]const u8,

/// The set of capabilities in this terminfo file.
capabilities: []const Capability,

/// A capability in a terminfo file. This also includes any "use" capabilities
/// since they behave just like other capabilities as documented in terminfo(5).
pub const Capability = struct {
    /// The name of capability. This is the "Cap-name" value in terminfo(5).
    name: []const u8,
    value: Value,

    pub const Value = union(enum) {
        /// Canceled value, i.e. suffixed with @
        canceled: void,

        /// Boolean values are always true if they exist so there is no value.
        boolean: void,

        /// Numeric values are always "unsigned decimal integers". The size
        /// of the integer is unspecified in terminfo(5). I chose 32-bits
        /// because it is a common integer size but this may be wrong.
        numeric: u32,

        string: [:0]const u8,
    };
};

/// Encode as a terminfo source file. The encoding is always done in a
/// human-readable format with whitespace. Fields are always written in the
/// order of the slices on this struct; this will not do any reordering.
pub fn encode(self: Source, writer: *std.Io.Writer) !void {
    // Encode the names in the order specified
    for (self.names, 0..) |name, i| {
        if (i != 0) try writer.writeAll("|");
        try writer.writeAll(name);
    }
    try writer.writeAll(",\n");

    // Encode each of the capabilities in the order specified
    for (self.capabilities) |cap| {
        try writer.writeAll("\t");
        try writer.writeAll(cap.name);
        switch (cap.value) {
            .canceled => try writer.writeAll("@"),
            .boolean => {},
            .numeric => |v| try writer.print("#{d}", .{v}),
            .string => |v| try writer.print("={s}", .{v}),
        }
        try writer.writeAll(",\n");
    }
}

/// The capability names XTGETTCAP answers without the table defining them:
/// TN is the terminal's name, Co its color count, RGB says the color strings
/// carry 8 bits per component.
const synthesized_keys = [_][]const u8{ "TN", "Co", "RGB" };

/// Returns a StaticStringMap for all of the capabilities in this terminfo.
/// The value is the value that should be sent as a response to XTGETTCAP.
/// Important: the value is the FULL response included the escape sequences.
///
/// The responses are null-terminated so that they can be handed directly
/// to APIs that require a sentinel (e.g. the libghostty-vt write_pty
/// callback) without copying.
pub fn xtgettcapMap(comptime self: Source) std.StaticStringMap([:0]const u8) {
    const KV = struct { []const u8, [:0]const u8 };

    // TN, Co and RGB are synthesized below and the table's own names become
    // map keys as they are, so a table that defines a synthesized name
    // itself, or names one of its capabilities twice, would put two answers
    // under one XTGETTCAP query. The map cannot represent that, so both are
    // compile errors. The uniqueness check is quadratic in the table size.
    comptime {
        @setEvalBranchQuota(4_000_000);
        for (self.capabilities) |cap| {
            for (synthesized_keys) |key| {
                if (std.mem.eql(u8, cap.name, key)) @compileError(
                    std.fmt.comptimePrint(
                        "capability {s} is answered by XTGETTCAP itself (TN, Co, " ++
                            "RGB); the terminfo table must not define it",
                        .{key},
                    ),
                );
            }
        }
        for (self.capabilities, 0..) |cap, i| {
            for (self.capabilities[i + 1 ..]) |other| {
                if (std.mem.eql(u8, cap.name, other.name)) @compileError(
                    std.fmt.comptimePrint(
                        "capability {s} is defined twice in the terminfo table; " ++
                            "XTGETTCAP would carry two answers for one query",
                        .{cap.name},
                    ),
                );
            }
        }
    }

    // Co is not its own fact: it is the table's own colors count, so the
    // XTGETTCAP answer and the compiled terminfo cannot disagree.
    const colors: u32 = colors: {
        for (self.capabilities) |cap| {
            if (std.mem.eql(u8, cap.name, "colors")) {
                switch (cap.value) {
                    .numeric => |v| break :colors v,
                    .boolean, .canceled, .string => @compileError(
                        "XTGETTCAP answers Co from the table's colors capability, " ++
                            "so colors must be numeric",
                    ),
                }
            }
        }
        @compileError(
            "XTGETTCAP answers Co from the table's colors capability, so the " ++
                "terminfo table must define a numeric colors capability",
        );
    };

    // We have all of our capabilities plus TN, Co, and RGB which aren't
    // in the capabilities list but are query-able.
    const len = self.capabilities.len + 3;
    var kvs: [len]KV = @splat(.{ "", "" });

    // We first build all of our entries with raw K=V pairs.
    kvs[0] = .{ "TN", self.names[0] };
    kvs[1] = .{ "Co", std.fmt.comptimePrint("{d}", .{colors}) };
    kvs[2] = .{ "RGB", "8" };
    for (self.capabilities, 3..) |cap, i| {
        kvs[i] = .{
            cap.name,
            switch (cap.value) {
                .canceled => @compileError("canceled not handled yet"),
                .boolean => "",
                .string => |v| string: {
                    @setEvalBranchQuota(100_000);
                    // If a string contains parameters, then we do not escape
                    // anything within the string. I BELIEVE the history here is
                    // xterm initially only supported specific capabilities and none
                    // had parameters so it returned the tcap encoded form. Later,
                    // Kitty added support for more capabilities some of which
                    // have parameters. But Kitty returned them in terminfo source
                    // format. So we need to handle both cases.
                    if (std.mem.indexOfScalar(u8, v, '%') != null) break :string v;
                    // No-parameters. Encode and return.
                    // First replace \E with the escape char (0x1B)
                    var result = comptimeReplace(v, "\\E", "\x1b");
                    // Replace '^' with the control char version of that char.
                    while (std.mem.indexOfScalar(u8, result, '^')) |idx| {
                        if (idx > 0) @compileError("handle control-char in middle of string");
                        const replacement = switch (result[idx + 1]) {
                            '?' => 0x7F, // DEL, special cased from ncurses
                            else => |c| c - 64,
                        };
                        result = comptimeReplace(
                            result,
                            result[idx .. idx + 2],
                            &.{replacement},
                        );
                    }
                    break :string result;
                },
                .numeric => |v| numeric: {
                    var buf: [11]u8 = @splat(0);
                    var writer: std.Io.Writer = .fixed(buf[0..10]);
                    writer.printInt(v, 10, .upper, .{}) catch unreachable;
                    const final = buf;
                    break :numeric final[0..writer.end :0];
                },
            },
        };
    }

    // Now go through and convert them all to hex-encoded strings.
    for (&kvs) |*entry| {
        // The key is just the raw hex-encoded string
        entry[0] = hexencode(entry[0]);

        // The value is more complex. The buffer is zeroed so the byte
        // after the response is already the null terminator.
        var buf: [5 + entry[0].len + 1 + (entry[1].len * 2) + 2 + 1]u8 = @splat(0);
        const out = if (std.mem.eql(u8, entry[1], "")) std.fmt.bufPrint(
            &buf,
            "\x1bP1+r{s}\x1b\\",
            .{entry[0]}, // important: hex-encoded name
        ) catch unreachable else std.fmt.bufPrint(
            &buf,
            "\x1bP1+r{s}={s}\x1b\\",
            .{ entry[0], hexencode(entry[1]) }, // important: hex-encoded name
        ) catch unreachable;

        const final = buf;
        entry[1] = final[0..out.len :0];
    }

    const kvs_final = kvs;
    return std.StaticStringMap([:0]const u8).initComptime(&kvs_final);
}

fn hexencode(comptime input: []const u8) []const u8 {
    return comptime &(std.fmt.bytesToHex(input, .upper));
}

/// std.mem.replace but comptime-only so we can return the string
/// allocated in comptime memory.
fn comptimeReplace(
    input: []const u8,
    needle: []const u8,
    replacement: []const u8,
) [:0]const u8 {
    comptime {
        const len = std.mem.replacementSize(u8, input, needle, replacement);
        var buf: [len + 1]u8 = @splat(0);
        _ = std.mem.replace(u8, input, needle, replacement, buf[0..len]);
        const final = buf;
        return final[0..len :0];
    }
}

test "xtgettcap map" {
    const testing = std.testing;

    const src: Source = .{
        .names = &.{
            "ghostty",
            "xterm-ghostty",
            "Ghostty",
        },

        .capabilities = &.{
            .{ .name = "am", .value = .{ .boolean = {} } },
            .{ .name = "colors", .value = .{ .numeric = 256 } },
            .{ .name = "kx", .value = .{ .string = "^?" } },
            .{ .name = "kbs", .value = .{ .string = "^H" } },
            .{ .name = "kf1", .value = .{ .string = "\\EOP" } },
            .{ .name = "Smulx", .value = .{ .string = "\\E[4:%p1%dm" } },
        },
    };

    const map = comptime src.xtgettcapMap();
    try testing.expectEqualStrings(
        "\x1bP1+r616D\x1b\\",
        map.get(hexencode("am")).?,
    );
    try testing.expectEqualStrings(
        "\x1bP1+r544E=67686F73747479\x1b\\",
        map.get(hexencode("TN")).?,
    );
    try testing.expectEqualStrings(
        "\x1bP1+r436F=323536\x1b\\",
        map.get(hexencode("Co")).?,
    );
    try testing.expectEqualStrings(
        "\x1bP1+r524742=38\x1b\\",
        map.get(hexencode("RGB")).?,
    );
    try testing.expectEqualStrings(
        "\x1bP1+r6B78=7F\x1b\\",
        map.get(hexencode("kx")).?,
    );
    try testing.expectEqualStrings(
        "\x1bP1+r6B6273=08\x1b\\",
        map.get(hexencode("kbs")).?,
    );
    try testing.expectEqualStrings(
        "\x1bP1+r6B6631=1B4F50\x1b\\",
        map.get(hexencode("kf1")).?,
    );
    try testing.expectEqualStrings(
        "\x1bP1+r536D756C78=5C455B343A25703125646D\x1b\\",
        map.get(hexencode("Smulx")).?,
    );
}

test "encode" {
    const src: Source = .{
        .names = &.{
            "ghostty",
            "xterm-ghostty",
            "Ghostty",
        },

        .capabilities = &.{
            .{ .name = "am", .value = .{ .boolean = {} } },
            .{ .name = "ccc", .value = .{ .canceled = {} } },
            .{ .name = "colors", .value = .{ .numeric = 256 } },
            .{ .name = "bel", .value = .{ .string = "^G" } },
        },
    };

    // Encode
    var buf: [1024]u8 = undefined;
    var writer: std.Io.Writer = .fixed(&buf);
    try src.encode(&writer);

    const expected =
        "ghostty|xterm-ghostty|Ghostty,\n" ++
        "\tam,\n" ++
        "\tccc@,\n" ++
        "\tcolors#256,\n" ++
        "\tbel=^G,\n";
    try std.testing.expectEqualStrings(@as([]const u8, expected), writer.buffered());
}
