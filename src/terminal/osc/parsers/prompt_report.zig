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
//!
//! Trust model: v1 is unauthenticated, deliberately. Anything holding the
//! pty can forge a report, exactly as anything holding the pty can forge
//! OSC 7 and OSC 9;9, and nothing in the framing distinguishes the shell
//! integration script from a program the shell ran. The one check between a
//! hostile child and a spawn into a credential-bearing directory is the UNC
//! host-locality test `reportPwd` applies to every reported pwd, whichever
//! sequence carried it.
//!
//! There is no authenticity field and adding one later is not a fix: an
//! optional one is worthless because an attacker omits it, and a required
//! one is a flag day. A version that wants authenticity needs a nonce the
//! terminal hands the shell out of band (the environment) and compares on
//! every report, and that is a v2 with `version` doing the job it exists to
//! do. Saying so here is the honest state of v1 rather than a reserved
//! field nobody checks.

const std = @import("std");

const Parser = @import("../../osc.zig").Parser;
const Command = @import("../../osc.zig").Command;

const log = std.log.scoped(.osc_prompt_report);

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
/// as the arena that holds the unescaped strings and the record. A payload
/// above the limit is dropped, which costs a report and nothing else: OSC 7
/// and OSC 9;9 still carry the directory on the same prompt.
pub const max_hex_len = 1024;

/// Smallest the arena can be, and the arithmetic that says it is enough.
///
/// The arena is whatever the decoded JSON does not need, so it is smallest
/// when the payload is largest. Everything the parse allocates comes out of
/// it:
///
///   * the record, once, claimed before the scanner takes anything;
///   * up to two copies of any string in the payload. One is the copy the
///     scanner has to make to undo escapes, one is the `dupeZ` that gives
///     the record a NUL-terminated pointer. That 2x is what sets the bound;
///   * the same two copies for strings the record never keeps. An escaped
///     key is unescaped into the arena before it is compared, and an
///     escaped value under a key this build has never heard of is unescaped
///     by `skipValue`. Neither is ever freed, because an arena does not.
///     So the adversarial shape is not one long path, it is a payload that
///     is all escapes, unknown keys included;
///   * the scanner's own nesting stack, which a deeply nested unknown value
///     grows one bit per level.
///
/// No string is longer than the JSON that carries it and no set of them is
/// longer either, so 2x the JSON covers every copy of every string whatever
/// the shape. The rest is the record, per-allocation alignment slack and
/// the nesting stack, which the headroom below covers with room to spare.
const arena_min_len = Parser.MAX_BUF - max_hex_len / 2;
comptime {
    const json_max = max_hex_len / 2;
    std.debug.assert(arena_min_len >= 2 * json_max + @sizeOf(Report) + 256);
}

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
    /// Windows path from a Windows shell, not a URL. Required in v1 and
    /// never empty: carrying a directory is what a v1 report is for, and a
    /// shell that does not know where it is sends no report rather than an
    /// empty one. See the rejection in `decode` for why an empty value must
    /// not be allowed to travel.
    ///
    /// Holds no byte below 0x20 and no 0x7f, so it is safe to hand to a
    /// consumer that treats it as a C string or writes it back out.
    cwd: [:0]const u8,

    /// "exit". Exit code of the command that ran before this prompt.
    exit_code: ?i64 = null,

    /// "shell". Which shell produced the report, e.g. "pwsh". Control-byte
    /// free, like every string in this record.
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
        //
        // `.invalid` is redundant here because `end` is terminal, but every
        // other parser in this directory sets it on a rejection and a lone
        // exception is a thing to re-derive rather than read.
        parser.state = .invalid;
        return null;
    };

    parser.command = .{ .prompt_report = report };
    return &parser.command;
}

fn decode(parser: *Parser) ?*const Report {
    const cap = if (parser.capture) |*c| c else return null;
    const data = cap.trailing();

    // "<kind> ; " and at least one hex pair.
    if (data.len < 4) return null;
    if (data[0] != kind_prompt) return null;
    if (data[1] != ';') return null;

    const hex = data[2..];

    // Size before shape. An oversized payload is logged and a malformed one
    // is not, so checking shape first would let a child that wants the drop
    // to be silent get it for the price of one odd byte.
    if (hex.len > max_hex_len) {
        log.warn("OSC 7777 payload too large, dropped len={d}", .{hex.len});
        return null;
    }
    if (hex.len % 2 != 0) return null;

    // Decode over the hex itself. Byte i reads from 2+2i and 3+2i and writes
    // to i, so the read cursor is always ahead of the write cursor and no
    // source byte is clobbered before it is read.
    const json_len = hex.len / 2;
    const json, const scratch = parser.decodeInPlace(json_len);
    std.debug.assert(scratch.len >= arena_min_len);
    for (0..json_len) |i| {
        const hi = hexDigit(hex[i * 2]) orelse return null;
        const lo = hexDigit(hex[i * 2 + 1]) orelse return null;
        json[i] = (hi << 4) | lo;
    }

    var fba: std.heap.FixedBufferAllocator = .init(scratch);
    const alloc = fba.allocator();

    // Claimed before the scanner takes any of the arena, so the record
    // itself can never be the allocation that does not fit.
    const report = alloc.create(Report) catch {
        logArenaExhausted("the record");
        return null;
    };

    var scanner: std.json.Scanner = .initCompleteInput(alloc, json);
    defer scanner.deinit();

    switch (nextToken(&scanner, alloc, "object begin") orelse return null) {
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
        const key: []const u8 = switch (nextToken(&scanner, alloc, "key") orelse return null) {
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
            scanner.skipValue() catch |err| {
                if (err == error.OutOfMemory) logArenaExhausted("an unknown value");
                return null;
            };
        }
    }

    switch (nextToken(&scanner, alloc, "end of document") orelse return null) {
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

    const dir = cwd orelse return null;
    if (dir.len == 0) {
        // A v1 report exists to carry a directory, so one that carries none
        // is not a v1 report. Rejecting it here rather than downstream is
        // what keeps `Stream` a plain dispatch: the pwd slot reads an empty
        // value as "forget the directory", and a report that means "the
        // shell does not know" must not be able to wipe the directory OSC 7
        // and OSC 9;9 just set on the same prompt. A shell that does not
        // know where it is sends no report, which is what the shipped
        // integration does. A kind that genuinely means "reset" can say so
        // on its own terms.
        return null;
    }

    report.* = .{
        .version = v,
        .cwd = dir,
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

/// Say so when the arena runs out. Every failure here drops the report, so
/// without this line our sizing bug and a child sending junk look identical
/// in a log: both are one report that never arrived. `arena_min_len`'s
/// comptime assert is the reason this should be unreachable, and this line
/// is how we would find out it is not.
fn logArenaExhausted(comptime what: []const u8) void {
    log.warn(
        "OSC 7777 arena exhausted at " ++ what ++
            ", report dropped; this is a sizing bug, not a bad payload",
        .{},
    );
}

/// One token, with an arena exhaustion distinguished from a malformed
/// payload. Both still drop the report.
fn nextToken(
    scanner: *std.json.Scanner,
    alloc: std.mem.Allocator,
    comptime what: []const u8,
) ?std.json.Token {
    return scanner.nextAlloc(alloc, .alloc_if_needed) catch |err| {
        if (err == error.OutOfMemory) logArenaExhausted(what);
        return null;
    };
}

/// Read one string value and copy it NUL-terminated into the arena. The copy
/// is what lets the record hand a C consumer a `[*:0]const u8` without the
/// consumer knowing where the bytes came from.
fn nextString(
    scanner: *std.json.Scanner,
    alloc: std.mem.Allocator,
) ?[:0]const u8 {
    const s: []const u8 = switch (nextToken(scanner, alloc, "a string") orelse return null) {
        .string => |v| v,
        .allocated_string => |v| v,
        else => return null,
    };

    // The emitter produces UTF-8 and the JSON escape decoder produces UTF-8,
    // but the bytes are the child's to choose, and a consumer of this record
    // will treat them as text.
    if (!std.unicode.utf8ValidateSlice(s)) return null;

    // And valid UTF-8 is not enough. `utf8ValidateSlice` accepts every C0
    // byte, NUL included, and `std.json` decodes `\u0000`, `\u001b` and
    // `\u0007` into exactly those bytes, so a child can put one inside a
    // string the framing was built to keep clean.
    //
    // NUL is the sharp one. The record hands a C consumer a
    // `[*:0]const u8`, which stops at the interior NUL, while the Zig
    // consumers carry the whole slice: two readers of one record disagreeing
    // about what the shell said. The escape bytes are the same argument the
    // hex framing makes -- a payload byte must not be able to act as a
    // control byte downstream.
    //
    // Refusing 0x01 through 0x1f costs nothing real: Win32 cannot create a
    // directory whose name contains one. DEL goes with them for the same
    // reason it is not a path character.
    for (s) |b| if (b < 0x20 or b == 0x7f) return null;

    return alloc.dupeZ(u8, s) catch {
        logArenaExhausted("a string copy");
        return null;
    };
}

fn nextInt(
    scanner: *std.json.Scanner,
    alloc: std.mem.Allocator,
) ?i64 {
    const s: []const u8 = switch (nextToken(scanner, alloc, "a number") orelse return null) {
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
    return switch (nextToken(scanner, alloc, "a bool") orelse return null) {
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

test "OSC 7777: non-ASCII directory survives unescaped" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    // What the shipped emitter actually sends, measured from a directory
    // with these characters under both PowerShell 7 and Windows PowerShell
    // 5.1: ConvertTo-Json leaves non-ASCII as raw UTF-8 inside the JSON
    // string and does not escape it. So this is the branch the encoding
    // defect is fixed on, and it is a different branch from the escaped
    // form below: the bytes are borrowed straight out of the buffer with
    // only utf8ValidateSlice between them and the record.
    const cmd = parseJson(&p,
        \\{"v":1,"cwd":"C:\\Grüße\\日本"}
    ).?.*;

    try testing.expect(cmd == .prompt_report);
    try testing.expectEqualStrings("C:\\Grüße\\日本", cmd.prompt_report.cwd);
}

test "OSC 7777: invalid UTF-8 in a string is rejected" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    // A lone continuation byte inside the string. Nothing stops a child
    // sending one, and every consumer of this record treats cwd as text.
    try testing.expect(parseJson(&p, "{\"v\":1,\"cwd\":\"C:\\\\\x80\"}") == null);
}

test "OSC 7777: escaped non-ASCII directory survives" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    // The escaped form. Our emitter does not produce it (see above), but
    // JSON escapes are legal wherever a string is, and another producer on
    // this schema may well emit them, so the decoder has to handle them.
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

test "OSC 7777: empty cwd rejected" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    // A v1 report carries a directory or it is not a v1 report. The pwd slot
    // downstream reads an empty value as "forget the directory", so letting
    // one through would let a report meaning "I do not know" wipe what OSC 7
    // and OSC 9;9 set on the same prompt.
    try testing.expect(parseJson(&p,
        \\{"v":1,"cwd":""}
    ) == null);
}

test "OSC 7777: control bytes in a string are rejected" {
    const testing = std.testing;

    // Each of these is well formed JSON that std.json decodes into a raw
    // control byte, and each one is accepted by utf8ValidateSlice. The NUL
    // case is the one that matters most: it would give the record a cwd
    // whose C accessor stops early while the Zig consumers carry the whole
    // slice, so two readers of one record would disagree about the path.
    const cases = [_][]const u8{
        // Interior NUL, then a whole forged OSC 0 behind it.
        \\{"v":1,"cwd":"C:\\a\u0000\u001b]0;X\u0007"}
        ,
        // Trailing NUL alone.
        \\{"v":1,"cwd":"C:\\a\u0000"}
        ,
        // ESC and BEL on their own, the bytes the hex framing exists to stop.
        \\{"v":1,"cwd":"C:\\a\u001b"}
        ,
        \\{"v":1,"cwd":"C:\\a\u0007"}
        ,
        // The JSON-native escapes for the same range.
        \\{"v":1,"cwd":"C:\\a\n"}
        ,
        \\{"v":1,"cwd":"C:\\a\t"}
        ,
        // DEL.
        \\{"v":1,"cwd":"C:\\a\u007f"}
        ,
        // And not only cwd: every string in the record takes the screen.
        \\{"v":1,"cwd":"C:\\a","shell":"pw\u0000sh"}
        ,
        \\{"v":1,"cwd":"C:\\a","git_branch":"m\u001bain"}
        ,
    };

    inline for (cases) |case| {
        var p: Parser = .init(null);
        defer p.deinit();
        try testing.expect(parseJson(&p, case) == null);
    }
}

test "OSC 7777: a payload full of escapes never ends its own sequence" {
    const testing = std.testing;

    var p: Parser = .init(null);
    defer p.deinit();

    // The framing property proven end to end rather than asserted: the
    // payload carries the hex for ESC, BEL and ST and the parser still
    // reaches the terminator, because no byte of a hex payload can be one.
    // The report is then refused on the control-byte screen, which is the
    // point: the alternative framing would have truncated the sequence here
    // instead, and left the tail to be read as live bytes.
    const json =
        \\{"v":1,"cwd":"C:\\a\u001b\u0007\u009c"}
    ;
    const hex = comptime hexEncode(json);

    for ("7777;p;") |ch| p.next(ch);
    for (hex) |ch| p.next(ch);

    // Still in the payload, not thrown into `.invalid` partway through it.
    try testing.expect(p.state != .invalid);
    try testing.expect(p.end(0x07) == null);
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
    // sound if the largest payload the parser accepts also parses.
    //
    // The expensive shape is not one long path. It is a payload that is all
    // escapes, because the scanner has to copy every escaped string into the
    // arena to undo them, and the arena never frees. That includes strings
    // the record never keeps: an escaped KEY is unescaped before it is
    // compared, and an escaped value under a key this build does not know is
    // unescaped by `skipValue` and thrown away. So this payload escapes the
    // path, escapes an unknown key, and escapes that key's value.
    const long = "C:\\\\" ++ ("\\u00fc" ** 36) ++ "\\\\dir";
    // "\u0066\u0075\u0074\u0075\u0072\u0065" is "future": a key no build
    // knows, spelled the most expensive way it can be spelled.
    const unknown_key = "\\u0066\\u0075\\u0074\\u0075\\u0072\\u0065";
    const json =
        \\{"v":1,"exit":0,"shell":"powershell","git_dirty":false,"git_head":"0123456789012345678901234567890123456789","git_branch":"release/some-longish-branch-name","
    ++ unknown_key ++ "\":\"" ++ ("\\u00fc" ** 12) ++ "\",\"cwd\":\"" ++ long ++ "\"}";

    // 505 of the 512 bytes the parser will accept, so the arena this exercises
    // is within seven bytes of the smallest it can ever be.
    comptime std.debug.assert(json.len * 2 <= max_hex_len);
    comptime std.debug.assert(json.len * 2 > max_hex_len - 32);

    const cmd = parseJson(&p, json).?.*;
    try testing.expect(cmd == .prompt_report);
    try testing.expectEqual(@as(usize, 3 + 36 * 2 + 4), cmd.prompt_report.cwd.len);
}
