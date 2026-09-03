//! OSC 7777: the shell's per-prompt state as one structured, versioned
//! report.
//!
//! Wire format:
//!
//!     ESC ] 7777 ; <kind> ; <hex-encoded UTF-8 JSON> BEL
//!
//! Two properties of that framing are the reason it exists, and neither is
//! available to the sequences it sits next to (OSC 7, OSC 9;9, OSC 133):
//!
//!   * The payload is hex, so no byte it carries can terminate the sequence
//!     that carries it. ESC, BEL, ST, CAN, SUB and 0x9c all survive, and a
//!     replay that stops mid-sequence cannot splice into the next live bytes
//!     and produce a path nobody sent.
//!
//!   * The payload is UTF-8 by construction, produced by the shell
//!     independently of the console encoding. A child console on Windows
//!     writes in a legacy OEM code page unless someone changed it, which
//!     transcodes or substitutes every non-ASCII byte of a path on its way
//!     out. Hex digits are ASCII, so the code page cannot touch them.
//!
//! 7777 is private. Nothing else in this tree uses it, and a ConPTY
//! pass-through probe on build 26200 confirmed the whole sequence, BEL
//! terminator included, reaches the parent intact on the raw stdout path.
//! Changing the number means re-running that probe.

const std = @import("std");

const Parser = @import("../../osc.zig").Parser;
const Command = @import("../../osc.zig").Command;

const log = std.log.scoped(.osc);

/// Payload kinds. A kind is a short ASCII token so this OSC can carry more
/// than one sort of message later without a second private number. `p` is a
/// prompt report; every other token is reserved and dropped for now.
pub const kind_prompt = 'p';

/// Schema version this parser understands. See `Report` for what the version
/// is allowed to mean.
pub const schema_version: u32 = 1;

/// Longest hex payload accepted, so 512 bytes of JSON. The child chooses
/// this input, so it needs a bound that does not depend on the child being
/// well behaved; the parser's inline buffer supplies the other half of the
/// bound by refusing the capture past 2048 bytes.
///
/// The limit is also what makes the decode allocation-free. The hex is
/// decoded over the top of itself, leaving the rest of the inline buffer
/// (at least 1536 bytes, three times the JSON) as the arena that holds the
/// unescaped strings and the record. A payload above the limit is dropped,
/// which costs a report and nothing else: OSC 7 and OSC 9;9 still carry the
/// directory on the same prompt.
pub const max_hex_len = 1024;

/// One prompt report.
///
/// Rules for growing this schema, which the version field exists to keep
/// honest:
///
///   * Additive only. A consumer ignores fields it does not know, and a
///     field that is not there is not an error.
///   * `version` gates breaking changes. An existing field never quietly
///     changes meaning, unit or nullability; if it must change, the version
///     goes up and old readers stop rather than misread.
///   * Absent and empty are different where it matters. A field that is not
///     present means the shell did not look. An empty string means it looked
///     and there is nothing: a repository with no branch checked out is not
///     the same answer as a prompt that never asked about git.
///
/// Strings point into the parser's buffer and are only valid until the next
/// call into the parser, like every other OSC command's payload.
pub const Report = struct {
    /// "v". Must equal `schema_version`.
    version: u32,

    /// "cwd". The shell's working directory in its own native form: a raw
    /// Windows path from a Windows shell, not a URL. Required in v1, and
    /// may be empty, which means the shell does not know where it is.
    cwd: [:0]const u8,

    /// "exit". Exit code of the command that ran before this prompt.
    exit_code: ?i64 = null,

    /// "shell". Which shell produced the report, e.g. "pwsh".
    shell: ?[:0]const u8 = null,

    /// "git_head". Commit the repository is on, or empty for a repository
    /// with no commits yet.
    git_head: ?[:0]const u8 = null,

    /// "git_branch". Branch name, or empty when HEAD is detached.
    git_branch: ?[:0]const u8 = null,

    /// "git_dirty". Whether the working tree has changes.
    git_dirty: ?bool = null,
};

/// Parse OSC 7777.
pub fn parse(parser: *Parser, _: ?u8) ?*Command {
    const report = decode(parser) orelse {
        // Dropped in silence, like any other OSC we cannot make sense of.
        // The prompt's OSC 7 and OSC 9;9 are unaffected.
        return null;
    };

    parser.command = .{ .prompt_report = report };
    return &parser.command;
}

fn decode(parser: *Parser) ?*const Report {
    const cap = if (parser.capture) |*c| c else return null;

    // The capture is requested `.fixed`, so its bytes are the front of
    // `parser.buffer` and the rest of that buffer is ours to use as scratch.
    // Everything below depends on that.
    if (cap.backing != .fixed) return null;

    const data = cap.trailing();

    // "<kind> ; " and at least one hex pair.
    if (data.len < 4) return null;
    if (data[0] != kind_prompt) return null;
    if (data[1] != ';') return null;

    const hex = data[2..];
    if (hex.len % 2 != 0) return null;
    if (hex.len > max_hex_len) {
        log.warn("OSC 7777 payload too large, dropped len={d}", .{hex.len});
        return null;
    }

    // Decode over the hex itself. Byte i reads from 2+2i and 3+2i and writes
    // to i, so the read cursor is always ahead of the write cursor and no
    // source byte is clobbered before it is read.
    const json_len = hex.len / 2;
    for (0..json_len) |i| {
        const hi = hexDigit(hex[i * 2]) orelse return null;
        const lo = hexDigit(hex[i * 2 + 1]) orelse return null;
        parser.buffer[i] = (hi << 4) | lo;
    }
    const json = parser.buffer[0..json_len];

    var fba: std.heap.FixedBufferAllocator = .init(parser.buffer[json_len..]);
    const alloc = fba.allocator();

    // Claimed before the scanner takes any of the arena, so the record
    // itself can never be the allocation that does not fit.
    const report = alloc.create(Report) catch return null;

    var scanner: std.json.Scanner = .initCompleteInput(alloc, json);
    defer scanner.deinit();

    switch (scanner.nextAlloc(alloc, .alloc_if_needed) catch return null) {
        .object_begin => {},
        else => return null,
    }

    var version: ?u32 = null;
    var cwd: ?[:0]const u8 = null;
    var exit_code: ?i64 = null;
    var shell: ?[:0]const u8 = null;
    var git_head: ?[:0]const u8 = null;
    var git_branch: ?[:0]const u8 = null;
    var git_dirty: ?bool = null;

    while (true) {
        // `.string` borrows from the input and `.allocated_string` owns a
        // copy the scanner had to make to undo escapes. Both are just bytes
        // to us; the arena outlives the record either way.
        const key: []const u8 = switch (scanner.nextAlloc(alloc, .alloc_if_needed) catch return null) {
            .object_end => break,
            .string => |s| s,
            .allocated_string => |s| s,
            else => return null,
        };

        if (std.mem.eql(u8, key, "v")) {
            const n = nextInt(&scanner, alloc) orelse return null;
            version = std.math.cast(u32, n) orelse return null;
        } else if (std.mem.eql(u8, key, "cwd")) {
            cwd = nextString(&scanner, alloc) orelse return null;
        } else if (std.mem.eql(u8, key, "exit")) {
            exit_code = nextInt(&scanner, alloc) orelse return null;
        } else if (std.mem.eql(u8, key, "shell")) {
            shell = nextString(&scanner, alloc) orelse return null;
        } else if (std.mem.eql(u8, key, "git_head")) {
            git_head = nextString(&scanner, alloc) orelse return null;
        } else if (std.mem.eql(u8, key, "git_branch")) {
            git_branch = nextString(&scanner, alloc) orelse return null;
        } else if (std.mem.eql(u8, key, "git_dirty")) {
            git_dirty = nextBool(&scanner, alloc) orelse return null;
        } else {
            // The schema is additive, so a field this build has never heard
            // of is data from a newer shell script and not an error.
            scanner.skipValue() catch return null;
        }
    }

    switch (scanner.nextAlloc(alloc, .alloc_if_needed) catch return null) {
        .end_of_document => {},
        else => return null,
    }

    const v = version orelse return null;
    if (v != schema_version) {
        // Never guess at a schema we were not built for: a field that means
        // one thing at v1 may mean another at v2, and acting on it is worse
        // than acting on nothing.
        log.warn("OSC 7777 unsupported schema version={d}", .{v});
        return null;
    }

    report.* = .{
        .version = v,
        .cwd = cwd orelse return null,
        .exit_code = exit_code,
        .shell = shell,
        .git_head = git_head,
        .git_branch = git_branch,
        .git_dirty = git_dirty,
    };
    return report;
}

fn hexDigit(c: u8) ?u8 {
    return switch (c) {
        '0'...'9' => c - '0',
        'a'...'f' => c - 'a' + 10,
        'A'...'F' => c - 'A' + 10,
        else => null,
    };
}

/// Read one string value and copy it NUL-terminated into the arena. The copy
/// is what lets the record hand a C consumer a `[*:0]const u8` without the
/// consumer knowing where the bytes came from.
fn nextString(
    scanner: *std.json.Scanner,
    alloc: std.mem.Allocator,
) ?[:0]const u8 {
    const s: []const u8 = switch (scanner.nextAlloc(alloc, .alloc_if_needed) catch return null) {
        .string => |v| v,
        .allocated_string => |v| v,
        else => return null,
    };

    // The emitter produces UTF-8 and the JSON escape decoder produces UTF-8,
    // but the bytes are the child's to choose, and a consumer of this record
    // will treat them as text.
    if (!std.unicode.utf8ValidateSlice(s)) return null;

    return alloc.dupeZ(u8, s) catch null;
}

fn nextInt(
    scanner: *std.json.Scanner,
    alloc: std.mem.Allocator,
) ?i64 {
    const s: []const u8 = switch (scanner.nextAlloc(alloc, .alloc_if_needed) catch return null) {
        .number => |v| v,
        .allocated_number => |v| v,
        else => return null,
    };
    return std.fmt.parseInt(i64, s, 10) catch null;
}

fn nextBool(
    scanner: *std.json.Scanner,
    alloc: std.mem.Allocator,
) ?bool {
    return switch (scanner.nextAlloc(alloc, .alloc_if_needed) catch return null) {
        .true => true,
        .false => false,
        else => null,
    };
}

// Test helpers ---------------------------------------------------------

fn hexEncode(comptime json: []const u8) [json.len * 2]u8 {
    const digits = "0123456789ABCDEF";
    var out: [json.len * 2]u8 = undefined;
    for (json, 0..) |b, i| {
        out[i * 2] = digits[b >> 4];
        out[i * 2 + 1] = digits[b & 0x0f];
    }
    return out;
}

fn parseJson(p: *Parser, comptime json: []const u8) ?*Command {
    const hex = comptime hexEncode(json);
    for ("7777;p;") |ch| p.next(ch);
    for (hex) |ch| p.next(ch);
    return p.end(0x07);
}

test "OSC 7777: hex round trip" {
    const testing = std.testing;

    // The encoder used by the tests below has to agree with the decoder, or
    // every one of them proves only that the two are wrong together.
    const json = "{\"v\":1}";
    const hex = comptime hexEncode(json);
    try testing.expectEqualStrings("7B2276223A317D", &hex);

    var decoded: [json.len]u8 = undefined;
    for (0..json.len) |i| {
        decoded[i] = (hexDigit(hex[i * 2]).? << 4) | hexDigit(hex[i * 2 + 1]).?;
    }
    try testing.expectEqualStrings(json, &decoded);
}

test "OSC 7777: full record" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    const cmd = parseJson(&p,
        \\{"v":1,"cwd":"C:\\Users\\me","exit":3,"shell":"pwsh","git_head":"abc","git_branch":"","git_dirty":true}
    ).?.*;

    try testing.expect(cmd == .prompt_report);
    const r = cmd.prompt_report;
    try testing.expectEqual(@as(u32, 1), r.version);
    try testing.expectEqualStrings("C:\\Users\\me", r.cwd);
    try testing.expectEqual(@as(?i64, 3), r.exit_code);
    try testing.expectEqualStrings("pwsh", r.shell.?);
    try testing.expectEqualStrings("abc", r.git_head.?);

    // Present and empty: HEAD is detached, which is a different answer from
    // a report that never mentioned a branch.
    try testing.expectEqualStrings("", r.git_branch.?);
    try testing.expectEqual(@as(?bool, true), r.git_dirty);
}

test "OSC 7777: non-ASCII directory survives" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    // What PowerShell's ConvertTo-Json actually emits for a path with
    // non-ASCII characters: the escaped form, not the raw bytes. This is the
    // case the whole design exists for, so it is checked against the exact
    // UTF-8 the terminal has to end up with.
    const cmd = parseJson(&p,
        \\{"v":1,"cwd":"C:\\Gr\u00fc\u00dfe\\\u65e5\u672c"}
    ).?.*;

    try testing.expect(cmd == .prompt_report);
    try testing.expectEqualStrings(
        "C:\\Gr\u{00fc}\u{00df}e\\\u{65e5}\u{672c}",
        cmd.prompt_report.cwd,
    );
}

test "OSC 7777: astral plane directory survives" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    // Surrogate pairs are the part of \u decoding that is easy to get wrong.
    const cmd = parseJson(&p,
        \\{"v":1,"cwd":"C:\\\ud83d\ude80"}
    ).?.*;

    try testing.expect(cmd == .prompt_report);
    try testing.expectEqualStrings("C:\\\u{1f680}", cmd.prompt_report.cwd);
}

test "OSC 7777: unknown fields are ignored" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    // Every JSON value shape, because skipping is the mechanism that lets a
    // newer shell script talk to an older terminal.
    const cmd = parseJson(&p,
        \\{"v":1,"future":{"a":[1,2,{"b":null}]},"cwd":"C:\\x","other":"s","n":-1,"t":true,"z":null}
    ).?.*;

    try testing.expect(cmd == .prompt_report);
    try testing.expectEqualStrings("C:\\x", cmd.prompt_report.cwd);
}

test "OSC 7777: missing optional fields" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    const cmd = parseJson(&p,
        \\{"v":1,"cwd":"C:\\x"}
    ).?.*;

    try testing.expect(cmd == .prompt_report);
    const r = cmd.prompt_report;
    try testing.expect(r.exit_code == null);
    try testing.expect(r.shell == null);
    try testing.expect(r.git_head == null);
    try testing.expect(r.git_branch == null);
    try testing.expect(r.git_dirty == null);
}

test "OSC 7777: empty cwd is a report, not a rejection" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    // Mirrors OSC 7's empty value: the shell saying it does not know.
    const cmd = parseJson(&p,
        \\{"v":1,"cwd":""}
    ).?.*;

    try testing.expect(cmd == .prompt_report);
    try testing.expectEqualStrings("", cmd.prompt_report.cwd);
}

test "OSC 7777: payload split across feeds" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    const json = "{\"v\":1,\"cwd\":\"C:\\\\x\"}";
    const hex = comptime hexEncode(json);
    const input = "7777;p;" ++ hex;

    // Fed in two chunks with the split inside the hex, which is where a
    // journal replay boundary would land.
    for (input[0 .. input.len / 2]) |ch| p.next(ch);
    for (input[input.len / 2 ..]) |ch| p.next(ch);

    const cmd = p.end(0x07).?.*;
    try testing.expect(cmd == .prompt_report);
    try testing.expectEqualStrings("C:\\x", cmd.prompt_report.cwd);
}

test "OSC 7777: wrong schema version rejected" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();
    try testing.expect(parseJson(&p,
        \\{"v":2,"cwd":"C:\\x"}
    ) == null);

    var p0: Parser = .init(null);
    defer p0.deinit();
    try testing.expect(parseJson(&p0,
        \\{"v":0,"cwd":"C:\\x"}
    ) == null);
}

test "OSC 7777: missing version rejected" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();
    try testing.expect(parseJson(&p,
        \\{"cwd":"C:\\x"}
    ) == null);
}

test "OSC 7777: missing cwd rejected" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();
    try testing.expect(parseJson(&p,
        \\{"v":1,"shell":"pwsh"}
    ) == null);
}

test "OSC 7777: malformed JSON rejected" {
    const testing = std.testing;

    const cases = [_][]const u8{
        "{\"v\":1,\"cwd\":\"C:\\\\x\"",
        "{\"v\":1 \"cwd\":\"x\"}",
        "[1,2,3]",
        "{\"v\":1,\"cwd\":\"C:\\\\x\"}trailing",
        "{\"v\":\"1\",\"cwd\":\"x\"}",
        "{\"v\":1,\"cwd\":42}",
        "{\"v\":1,\"cwd\":null}",
        "{\"v\":1,\"cwd\":\"x\",\"git_dirty\":\"yes\"}",
        "not json at all",
        "{}",
    };

    inline for (cases) |case| {
        var p: Parser = .init(null);
        defer p.deinit();
        try testing.expect(parseJson(&p, case) == null);
    }
}

test "OSC 7777: malformed hex rejected" {
    const testing = std.testing;

    // Non-hex byte, and a byte that is hex-adjacent but not a digit.
    const cases = [_][]const u8{ "7777;p;7B7D2G", "7777;p;7B7D2 " };
    for (cases) |case| {
        var p: Parser = .init(null);
        defer p.deinit();
        for (case) |ch| p.next(ch);
        try testing.expect(p.end(0x07) == null);
    }
}

test "OSC 7777: odd length hex rejected" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    for ("7777;p;7B7D2") |ch| p.next(ch);
    try testing.expect(p.end(0x07) == null);
}

test "OSC 7777: empty payload rejected" {
    const testing = std.testing;

    const cases = [_][]const u8{ "7777;", "7777;p", "7777;p;", "7777;;" };
    for (cases) |case| {
        var p: Parser = .init(null);
        defer p.deinit();
        for (case) |ch| p.next(ch);
        try testing.expect(p.end(0x07) == null);
    }
}

test "OSC 7777: unknown kind rejected" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    // Reserved for a later message kind, not a prompt report.
    for ("7777;x;7B7D") |ch| p.next(ch);
    try testing.expect(p.end(0x07) == null);
}

test "OSC 7777: oversized payload rejected" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    for ("7777;p;") |ch| p.next(ch);
    for (0..max_hex_len + 2) |_| p.next('4');
    try testing.expect(p.end(0x07) == null);
}

test "OSC 7777: payload past the inline buffer is dropped by the capture" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    // The second bound, independent of max_hex_len: nothing about this OSC
    // may reach the allocator, however much the child sends.
    for ("7777;p;") |ch| p.next(ch);
    for (0..Parser.MAX_BUF + 1) |_| p.next('4');

    try testing.expectEqual(Parser.State.invalid, p.state);
    try testing.expect(p.end(0x07) == null);
}

test "OSC 7777: a maximal payload still fits the arena" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    // The arena is the tail of the inline buffer, so the budget is only
    // sound if the largest payload the parser accepts also parses. Every
    // string is escaped, which is the expensive shape: the scanner has to
    // copy each one before the record can point at it.
    const long = "C:\\\\" ++ ("\\u00fc" ** 40) ++ "\\\\dir";
    const json =
        \\{"v":1,"exit":0,"shell":"powershell","git_dirty":false,"git_head":"0123456789012345678901234567890123456789","git_branch":"release/some-longish-branch-name","cwd":"
    ++ long ++ "\"}";

    comptime std.debug.assert(json.len * 2 <= max_hex_len);

    const cmd = parseJson(&p, json).?.*;
    try testing.expect(cmd == .prompt_report);
    try testing.expectEqual(@as(usize, 3 + 40 * 2 + 4), cmd.prompt_report.cwd.len);
}
