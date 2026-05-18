const std = @import("std");
const Allocator = std.mem.Allocator;

const assert = @import("../../../quirks.zig").inlineAssert;
const simd = @import("../../../simd/main.zig");

const Parser = @import("../../osc.zig").Parser;
const Command = @import("../../osc.zig").Command;
const kitty_graphics = @import("../../kitty/graphics.zig");

const log = std.log.scoped(.osc_iterm2);

const Key = enum {
    AddAnnotation,
    AddHiddenAnnotation,
    Block,
    Button,
    ClearCapturedOutput,
    ClearScrollback,
    Copy,
    CopyToClipboard,
    CurrentDir,
    CursorShape,
    Custom,
    Disinter,
    EndCopy,
    File,
    FileEnd,
    FilePart,
    HighlightCursorLine,
    MultipartFile,
    OpenURL,
    PopKeyLabels,
    PushKeyLabels,
    RemoteHost,
    ReportCellSize,
    ReportVariable,
    RequestAttention,
    RequestUpload,
    SetBackgroundImageFile,
    SetBadgeFormat,
    SetColors,
    SetKeyLabel,
    SetMark,
    SetProfile,
    SetUserVar,
    ShellIntegrationVersion,
    StealFocus,
    UnicodeVersion,
};

// Instead of using `std.meta.stringToEnum` we set up a StaticStringMap so
// that we can get ASCII case-insensitive lookups.
const Map = std.StaticStringMapWithEql(Key, std.ascii.eqlIgnoreCase);
const map: Map = .initComptime(
    map: {
        const fields = @typeInfo(Key).@"enum".fields;
        var tmp: [fields.len]struct { [:0]const u8, Key } = undefined;
        for (fields, 0..) |field, i| {
            tmp[i] = .{ field.name, @enumFromInt(field.value) };
        }
        break :map tmp;
    },
);

/// Parse OSC 1337
/// https://iterm2.com/documentation-escape-codes.html
pub fn parse(parser: *Parser, _: ?u8) ?*Command {
    assert(parser.state == .@"1337");

    const cap = if (parser.capture) |*c| c else {
        parser.state = .invalid;
        return null;
    };
    cap.writer.writeByte(0) catch {
        parser.state = .invalid;
        return null;
    };
    const data = cap.trailing();

    const key_str: [:0]u8, const value_: ?[:0]u8 = kv: {
        const index = std.mem.indexOfScalar(u8, data, '=') orelse {
            break :kv .{ data[0 .. data.len - 1 :0], null };
        };
        data[index] = 0;
        break :kv .{ data[0..index :0], data[index + 1 .. data.len - 1 :0] };
    };

    const key = map.get(key_str) orelse {
        parser.command = .invalid;
        return null;
    };

    switch (key) {
        .File => {
            // iTerm2 inline image transmission. Value is
            // `key=value;key=value:BASE64`. The options block ends at the
            // first ':'; the base64 alphabet excludes ':'.
            //
            // MVP only honors `inline=1`; geometry hints (width, height,
            // preserveAspectRatio, size, name) are accepted but unused.
            // Without `inline=1` the image is a download-to-disk request,
            // which has no wintty analog so we reject those.
            const value = value_ orelse {
                parser.command = .invalid;
                return null;
            };

            const colon = std.mem.indexOfScalar(u8, value, ':') orelse {
                log.debug("OSC 1337 File= rejected: no payload separator", .{});
                parser.command = .invalid;
                return null;
            };

            const options = value[0..colon];
            const payload = value[colon + 1 .. value.len :0];

            if (payload.len == 0) {
                log.debug("OSC 1337 File= rejected: empty payload", .{});
                parser.command = .invalid;
                return null;
            }

            // Walk options looking for `inline=1`. Key match is
            // case-insensitive; the value is matched literally because
            // iTerm2's documented values for `inline` are exactly `1`
            // and `0`.
            var inline_display = false;
            var it = std.mem.splitScalar(u8, options, ';');
            while (it.next()) |kv| {
                const eq = std.mem.indexOfScalar(u8, kv, '=') orelse continue;
                const k = kv[0..eq];
                const v = kv[eq + 1 ..];
                if (std.ascii.eqlIgnoreCase(k, "inline") and
                    std.mem.eql(u8, v, "1"))
                {
                    inline_display = true;
                    break;
                }
            }

            if (!inline_display) {
                // iTerm2 treats non-inline File= as a download to disk;
                // we have no equivalent in wintty.
                log.debug("OSC 1337 File= rejected: missing inline=1", .{});
                parser.command = .invalid;
                return null;
            }

            parser.command = .{ .iterm2_image_transmit = payload };
            return &parser.command;
        },

        .Copy => {
            var value = value_ orelse {
                parser.command = .invalid;
                return null;
            };

            // Sending a blank entry to clear the clipboard is an OSC 52-ism,
            // make sure that is invalid here.
            if (value.len == 0) {
                parser.command = .invalid;
                return null;
            }

            // base64 value must be prefixed by a colon
            if (value[0] != ':') {
                parser.command = .invalid;
                return null;
            }

            value = value[1..value.len :0];

            // Sending a blank entry to clear the clipboard is an OSC 52-ism,
            // make sure that is invalid here.
            if (value.len == 0) {
                parser.command = .invalid;
                return null;
            }

            // Sending a '?' to query the clipboard is an OSC 52-ism, make sure
            // that is invalid here.
            if (value.len == 1 and value[0] == '?') {
                parser.command = .invalid;
                return null;
            }

            // It would be better to check for valid base64 data here, but that
            // would mean parsing the base64 data twice in the "normal" case.

            parser.command = .{
                .clipboard_contents = .{
                    .kind = 'c',
                    .data = value,
                },
            };
            return &parser.command;
        },

        .CurrentDir => {
            const value = value_ orelse {
                parser.command = .invalid;
                return null;
            };
            if (value.len == 0) {
                parser.command = .invalid;
                return null;
            }
            parser.command = .{
                .report_pwd = .{
                    .value = value,
                },
            };
            return &parser.command;
        },

        .AddAnnotation,
        .AddHiddenAnnotation,
        .Block,
        .Button,
        .ClearCapturedOutput,
        .ClearScrollback,
        .CopyToClipboard,
        .CursorShape,
        .Custom,
        .Disinter,
        .EndCopy,
        .FileEnd,
        .FilePart,
        .HighlightCursorLine,
        .MultipartFile,
        .OpenURL,
        .PopKeyLabels,
        .PushKeyLabels,
        .RemoteHost,
        .ReportCellSize,
        .ReportVariable,
        .RequestAttention,
        .RequestUpload,
        .SetBackgroundImageFile,
        .SetBadgeFormat,
        .SetColors,
        .SetKeyLabel,
        .SetMark,
        .SetProfile,
        .SetUserVar,
        .ShellIntegrationVersion,
        .StealFocus,
        .UnicodeVersion,
        => {
            log.debug("unimplemented OSC 1337: {t}", .{key});
            parser.command = .invalid;
            return null;
        },
    }
    return &parser.command;
}

/// Decode a base64 payload from an iTerm2 OSC 1337 File= sequence and
/// synthesize a kitty graphics command that transmits and displays it as
/// a PNG at the current cursor position. The caller owns the returned
/// Command and must call deinit on it; the Command owns the decoded
/// byte buffer.
///
/// Returns error.InvalidData if the base64 is malformed, or
/// error.UnsupportedFormat if the decoded bytes don't carry a PNG
/// signature. Ghostty's kitty graphics decoder is PNG-only today, so
/// rejecting other formats here surfaces a clearer error than letting
/// the decoder reject mid-pipeline. iTerm2 emitters that send JPEG or
/// GIF will hit this path.
pub fn synthKittyCommand(
    alloc: Allocator,
    payload: []const u8,
) !kitty_graphics.Command {
    const max_len = simd.base64.maxLen(payload);
    if (max_len == 0) return error.InvalidData;

    // Mirror the in-place decode pattern used by the kitty graphics
    // command parser (graphics_command.zig decodeData): allocate up to
    // max_len, decode in place, shrink via ArrayList.toOwnedSlice so
    // the Command's data buffer carries no trailing unused bytes.
    var data: std.ArrayList(u8) = .empty;
    errdefer data.deinit(alloc);
    try data.resize(alloc, max_len);

    const decoded = simd.base64.decode(payload, data.items) catch {
        return error.InvalidData;
    };
    data.items.len = decoded.len;

    const png_sig = [_]u8{ 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    if (data.items.len < png_sig.len or
        !std.mem.eql(u8, data.items[0..png_sig.len], &png_sig))
    {
        return error.UnsupportedFormat;
    }

    return .{
        .control = .{ .transmit_and_display = .{
            .transmission = .{
                .format = .png,
                .medium = .direct,
            },
            .display = .{},
        } },
        .data = try data.toOwnedSlice(alloc),
    };
}

test "OSC: 1337: test valid unimplemented key with no value" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;SetBadgeFormat";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test valid unimplemented key with empty value" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;SetBadgeFormat=";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test valid unimplemented key with non-empty value" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;SetBadgeFormat=abc123";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test valid key with lower case and with no value" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;setbadgeformat";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test valid key with lower case and with empty value" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;setbadgeformat=";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test valid key with lower case and with non-empty value" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;setbadgeformat=abc123";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test invalid key with no value" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;BobrKurwa";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test invalid key with empty value" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;BobrKurwa=";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test invalid key with non-empty value" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;BobrKurwa=abc123";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test Copy with no value" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;Copy";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test Copy with empty value" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;Copy=";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test Copy with only prefix colon" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;Copy=:";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test Copy with question mark" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;Copy=:?";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test Copy with non-empty value that is invalid base64" {
    // For performance reasons, we don't check for valid base64 data
    // right now.
    return error.SkipZigTest;

    // const testing = std.testing;

    // var p: Parser = .init(testing.allocator);
    // defer p.deinit();

    // const input = "1337;Copy=:abc123";
    // for (input) |ch| p.next(ch);

    // try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test Copy with non-empty value that is valid base64 but not prefixed with a colon" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;Copy=YWJjMTIz";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test Copy with non-empty value that is valid base64" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;Copy=:YWJjMTIz";
    for (input) |ch| p.next(ch);

    const cmd = p.end('\x1b').?.*;
    try testing.expect(cmd == .clipboard_contents);
    try testing.expectEqual('c', cmd.clipboard_contents.kind);
    try testing.expectEqualStrings("YWJjMTIz", cmd.clipboard_contents.data);
}

test "OSC: 1337: test CurrentDir with no value" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;CurrentDir";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test CurrentDir with empty value" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;CurrentDir=";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test CurrentDir with non-empty value" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;CurrentDir=abc123";
    for (input) |ch| p.next(ch);

    const cmd = p.end('\x1b').?.*;
    try testing.expect(cmd == .report_pwd);
    try testing.expectEqualStrings("abc123", cmd.report_pwd.value);
}

test "OSC: 1337: test File inline=1 produces iterm2_image_transmit" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;File=inline=1:iVBORw0KGgo=";
    for (input) |ch| p.next(ch);

    const cmd = p.end('\x1b').?.*;
    try testing.expect(cmd == .iterm2_image_transmit);
    try testing.expectEqualStrings("iVBORw0KGgo=", cmd.iterm2_image_transmit);
}

test "OSC: 1337: test File with extra options before inline=1" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;File=name=Zm9v;size=4;inline=1:YWJjZA==";
    for (input) |ch| p.next(ch);

    const cmd = p.end('\x1b').?.*;
    try testing.expect(cmd == .iterm2_image_transmit);
    try testing.expectEqualStrings("YWJjZA==", cmd.iterm2_image_transmit);
}

test "OSC: 1337: test File without inline=1 is rejected" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;File=name=foo:iVBORw0KGgo=";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test File with inline=0 is rejected" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;File=inline=0:iVBORw0KGgo=";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test File with no payload separator is invalid" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;File=inline=1";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test File with empty payload is invalid" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;File=inline=1:";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test File with case-insensitive Inline=1" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;File=Inline=1:YWJjZA==";
    for (input) |ch| p.next(ch);

    const cmd = p.end('\x1b').?.*;
    try testing.expect(cmd == .iterm2_image_transmit);
    try testing.expectEqualStrings("YWJjZA==", cmd.iterm2_image_transmit);
}

test "synthKittyCommand: minimal 1x1 PNG yields transmit_and_display PNG command" {
    const testing = std.testing;
    const alloc = testing.allocator;

    // Canonical 1x1 transparent PNG, 67 bytes, base64-encoded.
    const payload =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJ" ++
        "AAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    var cmd = try synthKittyCommand(alloc, payload);
    defer cmd.deinit(alloc);

    try testing.expect(cmd.control == .transmit_and_display);
    const td = cmd.control.transmit_and_display;
    try testing.expect(td.transmission.format == .png);
    try testing.expect(td.transmission.medium == .direct);

    // PNG signature: 89 50 4E 47 0D 0A 1A 0A
    const sig = [_]u8{ 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    try testing.expect(cmd.data.len >= sig.len);
    try testing.expectEqualSlices(u8, &sig, cmd.data[0..sig.len]);
}

test "synthKittyCommand: invalid base64 returns InvalidData" {
    const testing = std.testing;
    const alloc = testing.allocator;

    // The OSC parser strips the payload at the first ':', but a
    // malformed sequence could still reach the helper with non-base64
    // bytes.
    const payload = "!!!not base64!!!";

    try testing.expectError(error.InvalidData, synthKittyCommand(alloc, payload));
}

test "synthKittyCommand: non-PNG bytes return UnsupportedFormat" {
    const testing = std.testing;
    const alloc = testing.allocator;

    // "abcd" base64-encoded. Decodes to valid base64 but missing the
    // PNG signature.
    const payload = "YWJjZA==";

    try testing.expectError(
        error.UnsupportedFormat,
        synthKittyCommand(alloc, payload),
    );
}

test "synthKittyCommand: payload shorter than PNG signature returns UnsupportedFormat" {
    const testing = std.testing;
    const alloc = testing.allocator;

    // "x" base64-encoded => 1 decoded byte, less than the 8-byte
    // PNG signature.
    const payload = "eA==";

    try testing.expectError(
        error.UnsupportedFormat,
        synthKittyCommand(alloc, payload),
    );
}
