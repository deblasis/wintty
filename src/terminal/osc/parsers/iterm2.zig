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

/// Parse an iTerm2 OSC 1337 File= dimension value into a cell count.
/// Returns 0 (meaning "no preference, use native sizing") for any value
/// that wintty cannot honor.
///
/// Cases:
/// - Bare integer N > 0      -> N cells.
/// - `auto`, empty           -> 0 silently (matches iTerm2 default).
/// - `Npx`, `N%`             -> 0 with log.warn; Kitty has no
///                              pixel-scaling or percentage primitive.
/// - 0                       -> 0 with log.warn; iTerm2's grammar
///                              doesn't sanction `width=0`, but some
///                              emitters send it; we treat it as a
///                              fallback to native sizing rather than
///                              silently making it indistinguishable
///                              from the missing case.
/// - Non-numeric, overflow   -> 0 silently.
///
/// `key` is included in warning text so an emitter can see which dim
/// was dropped.
fn parseCellDim(key: []const u8, value: []const u8) u32 {
    if (value.len == 0) return 0;
    if (std.ascii.eqlIgnoreCase(value, "auto")) return 0;

    // Trailing `px` or `%` make the value non-cell. Both forms map to
    // 0 with a warning; the renderer falls back to native sizing.
    if (std.mem.endsWith(u8, value, "px") or
        std.mem.endsWith(u8, value, "%"))
    {
        log.warn(
            "OSC 1337 File= {s}={s}: pixel/percent sizing unsupported, ignored",
            .{ key, value },
        );
        return 0;
    }

    const n = std.fmt.parseInt(u32, value, 10) catch return 0;
    if (n == 0) {
        log.warn(
            "OSC 1337 File= {s}={s}: zero is not a valid cell count, ignored",
            .{ key, value },
        );
        return 0;
    }
    return n;
}

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
            // We honor `inline=1` (required) plus geometry hints
            // `width`, `height`, and `preserveAspectRatio` mapped to
            // the Kitty graphics Display struct. Pixel and percent
            // sizing have no Kitty equivalent and log.warn. `name` and
            // `size` are spec-defined but ignored. Without `inline=1`
            // the image is a download-to-disk request which has no
            // wintty analog so we reject those.
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

            // Single pass over the options: pick up inline=1 and the
            // geometry hints in one walk. Key match is case-insensitive;
            // `inline` value is matched literally because iTerm2's
            // documented values are exactly `1` and `0`.
            var inline_display = false;
            var hints: Command.Iterm2ImageHints = .{};
            var it = std.mem.splitScalar(u8, options, ';');
            while (it.next()) |kv| {
                const eq = std.mem.indexOfScalar(u8, kv, '=') orelse continue;
                const k = kv[0..eq];
                const v = kv[eq + 1 ..];

                if (std.ascii.eqlIgnoreCase(k, "inline")) {
                    if (std.mem.eql(u8, v, "1")) inline_display = true;
                } else if (std.ascii.eqlIgnoreCase(k, "width")) {
                    hints.columns = parseCellDim(k, v);
                } else if (std.ascii.eqlIgnoreCase(k, "height")) {
                    hints.rows = parseCellDim(k, v);
                } else if (std.ascii.eqlIgnoreCase(k, "preserveAspectRatio")) {
                    // iTerm2 default is 1. Only flip to false on an
                    // explicit `0`.
                    if (std.mem.eql(u8, v, "0")) hints.preserve_aspect_ratio = false;
                }
                // Unknown keys (name, size, type, ...) are silently
                // ignored. iTerm2 and WezTerm do the same in practice.
            }

            if (!inline_display) {
                // iTerm2 treats non-inline File= as a download to disk;
                // we have no equivalent in wintty.
                log.debug("OSC 1337 File= rejected: missing inline=1", .{});
                parser.command = .invalid;
                return null;
            }

            parser.command = .{ .iterm2_image_transmit = .{
                .payload = payload,
                .hints = hints,
            } };
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
/// a PNG. Geometry hints map into the Display struct: cell width/height
/// become Kitty columns/rows. preserve_aspect_ratio=false is only
/// honored when both columns and rows are set, because Kitty stretches
/// only when both display dimensions are explicitly supplied.
///
/// The caller owns the returned Command and must call deinit on it;
/// the Command owns the decoded byte buffer.
///
/// Returns error.InvalidData if the base64 is malformed, or
/// error.UnsupportedFormat if the decoded bytes don't carry a PNG
/// signature. Ghostty's kitty graphics decoder is PNG-only today, so
/// rejecting other formats here surfaces a clearer error than letting
/// the decoder reject mid-pipeline. iTerm2 emitters that send JPEG or
/// GIF will hit this path.
pub fn synthKittyCommand(
    alloc: Allocator,
    transmit: Command.Iterm2ImageTransmit,
) !kitty_graphics.Command {
    const max_len = simd.base64.maxLen(transmit.payload);
    if (max_len == 0) return error.InvalidData;

    // Mirror the in-place decode pattern used by the kitty graphics
    // command parser (graphics_command.zig decodeData): allocate up to
    // max_len, decode in place, shrink via ArrayList.toOwnedSlice so
    // the Command's data buffer carries no trailing unused bytes.
    var data: std.ArrayList(u8) = .empty;
    errdefer data.deinit(alloc);
    try data.resize(alloc, max_len);

    const decoded = simd.base64.decode(transmit.payload, data.items) catch {
        return error.InvalidData;
    };
    data.items.len = decoded.len;

    const png_sig = [_]u8{ 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    if (data.items.len < png_sig.len or
        !std.mem.eql(u8, data.items[0..png_sig.len], &png_sig))
    {
        return error.UnsupportedFormat;
    }

    // preserve_aspect_ratio=false maps to Kitty's stretch mode, which
    // is implicit when both columns AND rows are set. When only one
    // dimension is supplied we cannot stretch (Kitty preserves aspect
    // either way) so the hint is moot. Emit a log.debug so anyone
    // bisecting a layout issue sees we received but couldn't honor it.
    if (!transmit.hints.preserve_aspect_ratio and
        (transmit.hints.columns == 0 or transmit.hints.rows == 0))
    {
        log.debug(
            "iTerm2 preserveAspectRatio=0 ignored: needs both width and height in cells",
            .{},
        );
    }

    return .{
        .control = .{ .transmit_and_display = .{
            .transmission = .{
                .format = .png,
                .medium = .direct,
            },
            .display = .{
                .columns = transmit.hints.columns,
                .rows = transmit.hints.rows,
            },
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
    const tx = cmd.iterm2_image_transmit;
    try testing.expectEqualStrings("iVBORw0KGgo=", tx.payload);
    try testing.expectEqual(@as(u32, 0), tx.hints.columns);
    try testing.expectEqual(@as(u32, 0), tx.hints.rows);
    try testing.expect(tx.hints.preserve_aspect_ratio);
}

test "OSC: 1337: test File with extra options before inline=1" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;File=name=Zm9v;size=4;inline=1:YWJjZA==";
    for (input) |ch| p.next(ch);

    const cmd = p.end('\x1b').?.*;
    try testing.expect(cmd == .iterm2_image_transmit);
    try testing.expectEqualStrings("YWJjZA==", cmd.iterm2_image_transmit.payload);
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
    try testing.expectEqualStrings("YWJjZA==", cmd.iterm2_image_transmit.payload);
}

test "OSC: 1337: test File with width and height in cells populates hints" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;File=inline=1;width=10;height=5:YWJjZA==";
    for (input) |ch| p.next(ch);

    const cmd = p.end('\x1b').?.*;
    const tx = cmd.iterm2_image_transmit;
    try testing.expectEqual(@as(u32, 10), tx.hints.columns);
    try testing.expectEqual(@as(u32, 5), tx.hints.rows);
    try testing.expect(tx.hints.preserve_aspect_ratio);
}

test "OSC: 1337: test File with width=auto leaves columns at 0" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;File=inline=1;width=auto;height=auto:YWJjZA==";
    for (input) |ch| p.next(ch);

    const tx = p.end('\x1b').?.*.iterm2_image_transmit;
    try testing.expectEqual(@as(u32, 0), tx.hints.columns);
    try testing.expectEqual(@as(u32, 0), tx.hints.rows);
}

test "OSC: 1337: test File with pixel-suffixed width leaves columns at 0" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    // Pixel sizing has no Kitty equivalent; the parser logs a warning
    // and falls back to native sizing.
    const input = "1337;File=inline=1;width=100px;height=50px:YWJjZA==";
    for (input) |ch| p.next(ch);

    const tx = p.end('\x1b').?.*.iterm2_image_transmit;
    try testing.expectEqual(@as(u32, 0), tx.hints.columns);
    try testing.expectEqual(@as(u32, 0), tx.hints.rows);
}

test "OSC: 1337: test File with percent-suffixed width leaves columns at 0" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;File=inline=1;width=80%:YWJjZA==";
    for (input) |ch| p.next(ch);

    const tx = p.end('\x1b').?.*.iterm2_image_transmit;
    try testing.expectEqual(@as(u32, 0), tx.hints.columns);
}

test "OSC: 1337: test File with case-insensitive Width and PreserveAspectRatio" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;File=inline=1;Width=12;PreserveAspectRatio=0:YWJjZA==";
    for (input) |ch| p.next(ch);

    const tx = p.end('\x1b').?.*.iterm2_image_transmit;
    try testing.expectEqual(@as(u32, 12), tx.hints.columns);
    try testing.expect(!tx.hints.preserve_aspect_ratio);
}

test "OSC: 1337: test File with preserveAspectRatio=1 keeps default true" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;File=inline=1;preserveAspectRatio=1:YWJjZA==";
    for (input) |ch| p.next(ch);

    const tx = p.end('\x1b').?.*.iterm2_image_transmit;
    try testing.expect(tx.hints.preserve_aspect_ratio);
}

test "OSC: 1337: test File with non-numeric width is ignored" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;File=inline=1;width=foo:YWJjZA==";
    for (input) |ch| p.next(ch);

    const tx = p.end('\x1b').?.*.iterm2_image_transmit;
    try testing.expectEqual(@as(u32, 0), tx.hints.columns);
}

// Canonical 1x1 transparent PNG, 67 bytes, base64-encoded.
const test_png_b64 =
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJ" ++
    "AAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

test "synthKittyCommand: minimal 1x1 PNG yields transmit_and_display PNG command" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var cmd = try synthKittyCommand(alloc, .{ .payload = test_png_b64 });
    defer cmd.deinit(alloc);

    try testing.expect(cmd.control == .transmit_and_display);
    const td = cmd.control.transmit_and_display;
    try testing.expect(td.transmission.format == .png);
    try testing.expect(td.transmission.medium == .direct);

    // PNG signature: 89 50 4E 47 0D 0A 1A 0A
    const sig = [_]u8{ 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    try testing.expect(cmd.data.len >= sig.len);
    try testing.expectEqualSlices(u8, &sig, cmd.data[0..sig.len]);

    // Default hints leave Display columns and rows at 0 (native size).
    try testing.expectEqual(@as(u32, 0), td.display.columns);
    try testing.expectEqual(@as(u32, 0), td.display.rows);
}

test "synthKittyCommand: invalid base64 returns InvalidData" {
    const testing = std.testing;
    const alloc = testing.allocator;

    try testing.expectError(
        error.InvalidData,
        synthKittyCommand(alloc, .{ .payload = "!!!not base64!!!" }),
    );
}

test "synthKittyCommand: non-PNG bytes return UnsupportedFormat" {
    const testing = std.testing;
    const alloc = testing.allocator;

    // "abcd" base64-encoded. Valid base64, but no PNG signature.
    try testing.expectError(
        error.UnsupportedFormat,
        synthKittyCommand(alloc, .{ .payload = "YWJjZA==" }),
    );
}

test "synthKittyCommand: payload shorter than PNG signature returns UnsupportedFormat" {
    const testing = std.testing;
    const alloc = testing.allocator;

    // "x" base64-encoded => 1 decoded byte, less than the 8-byte
    // PNG signature.
    try testing.expectError(
        error.UnsupportedFormat,
        synthKittyCommand(alloc, .{ .payload = "eA==" }),
    );
}

test "synthKittyCommand: hint columns and rows map to Display" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var cmd = try synthKittyCommand(alloc, .{
        .payload = test_png_b64,
        .hints = .{ .columns = 10, .rows = 5 },
    });
    defer cmd.deinit(alloc);

    const td = cmd.control.transmit_and_display;
    try testing.expectEqual(@as(u32, 10), td.display.columns);
    try testing.expectEqual(@as(u32, 5), td.display.rows);
}

test "synthKittyCommand: only columns set leaves rows at 0 for aspect preservation" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var cmd = try synthKittyCommand(alloc, .{
        .payload = test_png_b64,
        .hints = .{ .columns = 20 },
    });
    defer cmd.deinit(alloc);

    const td = cmd.control.transmit_and_display;
    try testing.expectEqual(@as(u32, 20), td.display.columns);
    // rows=0 lets Kitty compute the height from the image's aspect.
    try testing.expectEqual(@as(u32, 0), td.display.rows);
}

test "synthKittyCommand: preserve_aspect_ratio=false with both dims allows stretch" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var cmd = try synthKittyCommand(alloc, .{
        .payload = test_png_b64,
        .hints = .{
            .columns = 8,
            .rows = 4,
            .preserve_aspect_ratio = false,
        },
    });
    defer cmd.deinit(alloc);

    const td = cmd.control.transmit_and_display;
    // Both dims set => Kitty stretches without preserving aspect.
    try testing.expectEqual(@as(u32, 8), td.display.columns);
    try testing.expectEqual(@as(u32, 4), td.display.rows);
}
