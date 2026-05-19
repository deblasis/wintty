const std = @import("std");
const Allocator = std.mem.Allocator;

const assert = @import("../../../quirks.zig").inlineAssert;
const simd = @import("../../../simd/main.zig");

const Parser = @import("../../osc.zig").Parser;
const Command = @import("../../osc.zig").Command;
const apc = @import("../../apc.zig");
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

/// Parsed view of the options block from a File= or MultipartFile=
/// sequence: which `inline=1` toggle the emitter requested, plus the
/// geometry hints we know how to honor. Used by both the single-shot
/// and multipart entry points so the recognized keys stay in lockstep.
const FileOptions = struct {
    inline_display: bool = false,
    hints: Command.Iterm2ImageHints = .{},
};

fn parseFileOptions(options: []const u8) FileOptions {
    var result: FileOptions = .{};
    var it = std.mem.splitScalar(u8, options, ';');
    while (it.next()) |kv| {
        const eq = std.mem.indexOfScalar(u8, kv, '=') orelse continue;
        const k = kv[0..eq];
        const v = kv[eq + 1 ..];

        if (std.ascii.eqlIgnoreCase(k, "inline")) {
            // iTerm2's documented values for `inline` are exactly `1`
            // and `0`; match literally.
            if (std.mem.eql(u8, v, "1")) result.inline_display = true;
        } else if (std.ascii.eqlIgnoreCase(k, "width")) {
            result.hints.columns = parseCellDim(k, v);
        } else if (std.ascii.eqlIgnoreCase(k, "height")) {
            result.hints.rows = parseCellDim(k, v);
        } else if (std.ascii.eqlIgnoreCase(k, "preserveAspectRatio")) {
            // iTerm2 default is 1; only flip to false on explicit `0`.
            if (std.mem.eql(u8, v, "0")) result.hints.preserve_aspect_ratio = false;
        }
        // Unknown keys (name, size, type, ...) are silently ignored.
        // iTerm2 and WezTerm do the same in practice.
    }
    return result;
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
            const payload = value[colon + 1 ..];

            if (payload.len == 0) {
                log.debug("OSC 1337 File= rejected: empty payload", .{});
                parser.command = .invalid;
                return null;
            }

            const parsed = parseFileOptions(options);
            if (!parsed.inline_display) {
                // iTerm2 treats non-inline File= as a download to disk;
                // we have no equivalent in wintty.
                log.debug("OSC 1337 File= rejected: missing inline=1", .{});
                parser.command = .invalid;
                return null;
            }

            parser.command = .{ .iterm2_image_transmit = .{
                .payload = payload,
                .hints = parsed.hints,
            } };
            return &parser.command;
        },

        .MultipartFile => {
            // Start of a multipart inline image transfer. The wire
            // format is `MultipartFile=key=value;key=value...` with no
            // payload here; the chunks arrive in subsequent FilePart=
            // sequences and the transfer ends on FileEnd.
            //
            // We require inline=1 to match the single-shot File= path,
            // so a download-style multipart never enters our state
            // machine.
            const options = value_ orelse "";
            const parsed = parseFileOptions(options);
            if (!parsed.inline_display) {
                log.debug("OSC 1337 MultipartFile= rejected: missing inline=1", .{});
                parser.command = .invalid;
                return null;
            }
            parser.command = .{ .iterm2_multipart_image = .{
                .start = parsed.hints,
            } };
            return &parser.command;
        },

        .FilePart => {
            // One more base64 chunk for the in-flight multipart
            // transfer. The assembler is responsible for orphan-chunk
            // handling (FilePart with no preceding MultipartFile), so
            // we forward the raw bytes (including empty) and let the
            // assembler decide.
            const chunk = value_ orelse "";
            parser.command = .{ .iterm2_multipart_image = .{
                .chunk = chunk,
            } };
            return &parser.command;
        },

        .FileEnd => {
            // Terminator. iTerm2's documented form is `FileEnd` with
            // no `=value`; we accept `FileEnd=` defensively since the
            // upstream `key=value` parse loop happily produces an
            // empty value for the bare key and we don't gain anything
            // by rejecting it.
            parser.command = .{ .iterm2_multipart_image = .end };
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

        .ReportCellSize => {
            // iTerm2's documented form is the bare key
            // `OSC 1337;ReportCellSize ST`. A trailing `=` with no
            // value is accepted defensively because the upstream
            // `key=value` split happily produces an empty value for
            // such input. Any `=non-empty` form would collide with
            // the response shape `ReportCellSize=H;W;scale`, so we
            // reject it.
            if (value_) |v| {
                if (v.len > 0) {
                    parser.command = .invalid;
                    return null;
                }
            }
            parser.command = .iterm2_report_cell_size;
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
        .HighlightCursorLine,
        .OpenURL,
        .PopKeyLabels,
        .PushKeyLabels,
        .RemoteHost,
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
/// synthesize a kitty graphics command that transmits and displays it.
/// Geometry hints map into the Display struct: cell width/height become
/// Kitty columns/rows. preserve_aspect_ratio=false is only honored when
/// both columns and rows are set, because Kitty stretches only when
/// both display dimensions are explicitly supplied.
///
/// The caller owns the returned Command and must call deinit on it;
/// the Command owns the decoded byte buffer.
///
/// The payload format is sniffed from the leading magic bytes: PNG
/// (89 50 4E 47 0D 0A 1A 0A), JPEG (FF D8 FF), and GIF (47 49 46 38)
/// are recognized and tagged on the synthesized Transmission so the
/// downstream image decoder picks the right wuffs path. GIF is
/// rendered as the first frame only. Any other content (BMP, raw
/// pixels, etc.) returns error.UnsupportedFormat so that the caller
/// surfaces a clear error rather than letting the kitty decoder
/// reject mid-pipeline.
///
/// Returns error.InvalidData if the base64 is malformed.
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

    // Sniff the format from the leading magic bytes. PNG, JPEG, and
    // GIF are routed through the kitty graphics image decoder by the
    // respective Format enum tags; anything else is rejected here so
    // the caller gets a clear error rather than letting the decoder
    // reject mid-pipeline.
    const Sniffed = enum { png, jpeg, gif };
    const sniffed: Sniffed = sniffed: {
        const png_sig = [_]u8{ 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (data.items.len >= png_sig.len and
            std.mem.eql(u8, data.items[0..png_sig.len], &png_sig))
        {
            break :sniffed .png;
        }

        const jpeg_sig = [_]u8{ 0xFF, 0xD8, 0xFF };
        if (data.items.len >= jpeg_sig.len and
            std.mem.eql(u8, data.items[0..jpeg_sig.len], &jpeg_sig))
        {
            break :sniffed .jpeg;
        }

        // GIF magic is "GIF8" (47 49 46 38) shared between GIF87a
        // and GIF89a; the trailing version byte and 'a' are not
        // needed for dispatch.
        const gif_sig = [_]u8{ 0x47, 0x49, 0x46, 0x38 };
        if (data.items.len >= gif_sig.len and
            std.mem.eql(u8, data.items[0..gif_sig.len], &gif_sig))
        {
            break :sniffed .gif;
        }

        return error.UnsupportedFormat;
    };

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
                .format = switch (sniffed) {
                    .png => .png,
                    .jpeg => .jpeg,
                    .gif => .gif,
                },
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

/// Cross-OSC state for iTerm2 multipart File= transfers. iTerm2's wire
/// format has no session identifier, so transfers are strictly
/// serialized: at most one assembler `state` is active at a time.
///
/// Lifetime: lives on the stream handler for the duration of the
/// terminal session. `deinit` releases any in-flight payload buffer.
///
/// Sequence: `start` initializes a fresh buffer carrying the hints.
/// `chunk` appends a base64 chunk. `end` returns the accumulated
/// `Iterm2ImageTransmit` and clears the in-progress state; the caller
/// owns the returned payload string and is responsible for freeing it.
pub const Iterm2MultipartAssembler = struct {
    /// Active multipart accumulation, or null when no transfer is in
    /// flight.
    state: ?State = null,

    /// Maximum accumulated base64 payload. Sourced from the APC kitty
    /// graphics path so the two image-transport ceilings stay in
    /// lockstep without manual drift.
    pub const max_payload_bytes: usize = apc.Protocol.defaultMaxBytes(.kitty);

    pub const State = struct {
        hints: Command.Iterm2ImageHints,
        payload: std.ArrayList(u8),
    };

    /// Release the in-flight payload, if any. Safe to call repeatedly.
    pub fn deinit(self: *Iterm2MultipartAssembler, alloc: Allocator) void {
        if (self.state) |*s| s.payload.deinit(alloc);
        self.state = null;
    }

    /// Feed one parser event. Returns a populated transmit on `.end`
    /// when the transfer assembled cleanly; the caller owns the
    /// returned `payload` slice (allocated with `alloc`) and must
    /// free it (passing the transmit to `Command.deinit` is the usual
    /// disposal path). Returns null on `.start`, `.chunk`, or on any
    /// rejected event.
    pub fn handleEvent(
        self: *Iterm2MultipartAssembler,
        alloc: Allocator,
        event: Command.Iterm2MultipartEvent,
    ) !?Command.Iterm2ImageTransmit {
        switch (event) {
            .start => |hints| {
                if (self.state != null) {
                    log.warn(
                        "iTerm2 multipart overrun: new MultipartFile while one was in flight, dropping previous",
                        .{},
                    );
                    self.deinit(alloc);
                }
                self.state = .{
                    .hints = hints,
                    .payload = .empty,
                };
                return null;
            },

            .chunk => |chunk| {
                const s = if (self.state) |*ptr| ptr else {
                    log.warn(
                        "iTerm2 multipart: FilePart with no active transfer, dropping {d} bytes",
                        .{chunk.len},
                    );
                    return null;
                };

                if (s.payload.items.len + chunk.len > max_payload_bytes) {
                    log.warn(
                        "iTerm2 multipart: payload would exceed {d} bytes, dropping transfer",
                        .{max_payload_bytes},
                    );
                    self.deinit(alloc);
                    return null;
                }

                try s.payload.appendSlice(alloc, chunk);
                return null;
            },

            .end => {
                const s = if (self.state) |*ptr| ptr else {
                    log.warn(
                        "iTerm2 multipart: FileEnd with no active transfer, ignoring",
                        .{},
                    );
                    return null;
                };

                // Hand ownership of the payload buffer off to the
                // caller. The caller must free `payload` after use
                // (typically right after the synthKittyCommand call,
                // which copies the bytes via base64 decode).
                const hints = s.hints;
                const payload = try s.payload.toOwnedSlice(alloc);
                self.state = null;

                return .{
                    .payload = payload,
                    .hints = hints,
                };
            },
        }
    }
};

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

test "synthKittyCommand: unknown bytes return UnsupportedFormat" {
    const testing = std.testing;
    const alloc = testing.allocator;

    // "abcd" base64-encoded. Valid base64, but no PNG, JPEG, or GIF
    // signature.
    try testing.expectError(
        error.UnsupportedFormat,
        synthKittyCommand(alloc, .{ .payload = "YWJjZA==" }),
    );
}

// JPEG SOI + APP0 (JFIF) marker start. Enough to satisfy the
// signature sniff in synthKittyCommand; the wuffs decoder validates
// the full structure later in the pipeline.
const test_jpeg_b64 = "/9j/4AAQSkY=";

test "synthKittyCommand: JPEG signature yields transmit_and_display JPEG command" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var cmd = try synthKittyCommand(alloc, .{ .payload = test_jpeg_b64 });
    defer cmd.deinit(alloc);

    try testing.expect(cmd.control == .transmit_and_display);
    const td = cmd.control.transmit_and_display;
    try testing.expect(td.transmission.format == .jpeg);
    try testing.expect(td.transmission.medium == .direct);

    // JPEG SOI: FF D8 FF
    const sig = [_]u8{ 0xFF, 0xD8, 0xFF };
    try testing.expect(cmd.data.len >= sig.len);
    try testing.expectEqualSlices(u8, &sig, cmd.data[0..sig.len]);
}

// GIF89a header. Enough to satisfy the signature sniff; the wuffs
// decoder validates the full structure later in the pipeline.
const test_gif_b64 = "R0lGODlh";

test "synthKittyCommand: GIF signature yields transmit_and_display GIF command" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var cmd = try synthKittyCommand(alloc, .{ .payload = test_gif_b64 });
    defer cmd.deinit(alloc);

    try testing.expect(cmd.control == .transmit_and_display);
    const td = cmd.control.transmit_and_display;
    try testing.expect(td.transmission.format == .gif);
    try testing.expect(td.transmission.medium == .direct);

    // GIF magic: 47 49 46 38 ("GIF8")
    const sig = [_]u8{ 0x47, 0x49, 0x46, 0x38 };
    try testing.expect(cmd.data.len >= sig.len);
    try testing.expectEqualSlices(u8, &sig, cmd.data[0..sig.len]);
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

test "OSC: 1337: test MultipartFile inline=1 emits start with hints" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;MultipartFile=inline=1;width=10;height=5";
    for (input) |ch| p.next(ch);

    const cmd = p.end('\x1b').?.*;
    try testing.expect(cmd == .iterm2_multipart_image);
    try testing.expect(cmd.iterm2_multipart_image == .start);
    const hints = cmd.iterm2_multipart_image.start;
    try testing.expectEqual(@as(u32, 10), hints.columns);
    try testing.expectEqual(@as(u32, 5), hints.rows);
}

test "OSC: 1337: test MultipartFile without inline=1 is rejected" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;MultipartFile=name=foo";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test FilePart emits chunk with raw bytes" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;FilePart=YWJjZA==";
    for (input) |ch| p.next(ch);

    const cmd = p.end('\x1b').?.*;
    try testing.expect(cmd == .iterm2_multipart_image);
    try testing.expect(cmd.iterm2_multipart_image == .chunk);
    try testing.expectEqualStrings("YWJjZA==", cmd.iterm2_multipart_image.chunk);
}

test "OSC: 1337: test FilePart with no value emits empty chunk" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;FilePart";
    for (input) |ch| p.next(ch);

    const cmd = p.end('\x1b').?.*;
    try testing.expect(cmd.iterm2_multipart_image == .chunk);
    try testing.expectEqualStrings("", cmd.iterm2_multipart_image.chunk);
}

test "OSC: 1337: test FileEnd emits end" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;FileEnd";
    for (input) |ch| p.next(ch);

    const cmd = p.end('\x1b').?.*;
    try testing.expect(cmd.iterm2_multipart_image == .end);
}

test "OSC: 1337: test FileEnd with trailing equals also emits end" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;FileEnd=";
    for (input) |ch| p.next(ch);

    const cmd = p.end('\x1b').?.*;
    try testing.expect(cmd.iterm2_multipart_image == .end);
}

test "OSC: 1337: test ReportCellSize bare key emits iterm2_report_cell_size" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;ReportCellSize";
    for (input) |ch| p.next(ch);

    const cmd = p.end('\x1b').?.*;
    try testing.expect(cmd == .iterm2_report_cell_size);
}

test "OSC: 1337: test reportcellsize lowercase emits iterm2_report_cell_size" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    const input = "1337;reportcellsize";
    for (input) |ch| p.next(ch);

    const cmd = p.end('\x1b').?.*;
    try testing.expect(cmd == .iterm2_report_cell_size);
}

test "OSC: 1337: test ReportCellSize with stray value is rejected" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    // The iTerm2 spec defines ReportCellSize as a bare-key query.
    // Any `=value` form would collide with the terminal's response
    // wire format, so we reject it.
    const input = "1337;ReportCellSize=foo";
    for (input) |ch| p.next(ch);

    try testing.expect(p.end('\x1b') == null);
}

test "OSC: 1337: test ReportCellSize with empty value also emits query" {
    const testing = std.testing;

    var p: Parser = .init(testing.allocator);
    defer p.deinit();

    // A trailing `=` with no value is accepted defensively: the
    // upstream key=value split produces an empty value for such
    // input, and emitters built around naive `key=value` formatters
    // can hit this without intending to send a response.
    const input = "1337;ReportCellSize=";
    for (input) |ch| p.next(ch);

    const cmd = p.end('\x1b').?.*;
    try testing.expect(cmd == .iterm2_report_cell_size);
}

test "Iterm2MultipartAssembler: happy path assembles chunks" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var assembler: Iterm2MultipartAssembler = .{};
    defer assembler.deinit(alloc);

    try testing.expectEqual(
        @as(?Command.Iterm2ImageTransmit, null),
        try assembler.handleEvent(alloc, .{ .start = .{ .columns = 7 } }),
    );
    try testing.expectEqual(
        @as(?Command.Iterm2ImageTransmit, null),
        try assembler.handleEvent(alloc, .{ .chunk = "YWJj" }),
    );
    try testing.expectEqual(
        @as(?Command.Iterm2ImageTransmit, null),
        try assembler.handleEvent(alloc, .{ .chunk = "ZA==" }),
    );

    const out = (try assembler.handleEvent(alloc, .end)).?;
    defer alloc.free(out.payload);

    try testing.expectEqualStrings("YWJjZA==", out.payload);
    try testing.expectEqual(@as(u32, 7), out.hints.columns);
    try testing.expect(assembler.state == null);
}

test "Iterm2MultipartAssembler: orphan chunk is dropped" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var assembler: Iterm2MultipartAssembler = .{};
    defer assembler.deinit(alloc);

    try testing.expectEqual(
        @as(?Command.Iterm2ImageTransmit, null),
        try assembler.handleEvent(alloc, .{ .chunk = "YWJj" }),
    );
    try testing.expect(assembler.state == null);
}

test "Iterm2MultipartAssembler: orphan end is dropped" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var assembler: Iterm2MultipartAssembler = .{};
    defer assembler.deinit(alloc);

    try testing.expectEqual(
        @as(?Command.Iterm2ImageTransmit, null),
        try assembler.handleEvent(alloc, .end),
    );
    try testing.expect(assembler.state == null);
}

test "Iterm2MultipartAssembler: overlapping start discards previous" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var assembler: Iterm2MultipartAssembler = .{};
    defer assembler.deinit(alloc);

    _ = try assembler.handleEvent(alloc, .{ .start = .{ .columns = 1 } });
    _ = try assembler.handleEvent(alloc, .{ .chunk = "WA==" });
    _ = try assembler.handleEvent(alloc, .{ .start = .{ .columns = 99 } });
    _ = try assembler.handleEvent(alloc, .{ .chunk = "YQ==" });
    const out = (try assembler.handleEvent(alloc, .end)).?;
    defer alloc.free(out.payload);

    try testing.expectEqualStrings("YQ==", out.payload);
    try testing.expectEqual(@as(u32, 99), out.hints.columns);
}

test "Iterm2MultipartAssembler: oversize transfer is dropped" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var assembler: Iterm2MultipartAssembler = .{};
    defer assembler.deinit(alloc);

    _ = try assembler.handleEvent(alloc, .{ .start = .{} });

    // Pre-load the assembler state up to the cap, then push one more
    // byte and assert the transfer is aborted. The resize does a real
    // allocation of max_payload_bytes; that's deliberately the full
    // 65 MiB so the boundary check exercises the same arithmetic
    // production sees, and the test is cheap enough at this size.
    if (assembler.state) |*s| {
        try s.payload.resize(alloc, Iterm2MultipartAssembler.max_payload_bytes);
    }

    // One more byte must push past the cap and abort the transfer.
    try testing.expectEqual(
        @as(?Command.Iterm2ImageTransmit, null),
        try assembler.handleEvent(alloc, .{ .chunk = "X" }),
    );
    try testing.expect(assembler.state == null);
}
