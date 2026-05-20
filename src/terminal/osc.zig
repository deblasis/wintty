//! OSC (Operating System Command) related functions and types.
//!
//! OSC is another set of control sequences for terminal programs that start with
//! "ESC ]". Unlike CSI or standard ESC sequences, they may contain strings
//! and other irregular formatting so a dedicated parser is created to handle it.
const osc = @This();

const std = @import("std");
const builtin = @import("builtin");
const build_options = @import("terminal_options");
const mem = std.mem;
const assert = @import("../quirks.zig").inlineAssert;
const Allocator = mem.Allocator;
const lib = @import("lib.zig");
const LibEnum = lib.Enum;
const kitty_color = @import("kitty/color.zig");
const parsers = @import("osc/parsers.zig");
const encoding = @import("osc/encoding.zig");

pub const color = parsers.color;
pub const semantic_prompt = parsers.semantic_prompt;

const log = std.log.scoped(.osc);

pub const Command = union(Key) {
    /// This generally shouldn't ever be set except as an initial zero value.
    /// Ignore it.
    invalid,

    /// Set the window title of the terminal
    ///
    /// If title mode 0 is set text is expect to be hex encoded (i.e. utf-8
    /// with each code unit further encoded with two hex digits).
    ///
    /// If title mode 2 is set or the terminal is setup for unconditional
    /// utf-8 titles text is interpreted as utf-8. Else text is interpreted
    /// as latin1.
    change_window_title: [:0]const u8,

    /// Set the icon of the terminal window. The name of the icon is not
    /// well defined, so this is currently ignored by Ghostty at the time
    /// of writing this. We just parse it so that we don't get parse errors
    /// in the log.
    change_window_icon: [:0]const u8,

    /// Semantic prompt command: https://gitlab.freedesktop.org/Per_Bothner/specifications/blob/master/proposals/semantic-prompts.md
    semantic_prompt: SemanticPrompt,

    /// Set or get clipboard contents. If data is null, then the current
    /// clipboard contents are sent to the pty. If data is set, this
    /// contents is set on the clipboard.
    clipboard_contents: struct {
        kind: u8,
        data: [:0]const u8,
    },

    /// OSC 7. Reports the current working directory of the shell. This is
    /// a moderately flawed escape sequence but one that many major terminals
    /// support so we also support it. To understand the flaws, read through
    /// this terminal-wg issue: https://gitlab.freedesktop.org/terminal-wg/specifications/-/issues/20
    report_pwd: struct {
        /// The reported pwd value. This is not checked for validity. It should
        /// be a file URL but it is up to the caller to utilize this value.
        value: [:0]const u8,
    },

    /// OSC 22. Set the mouse shape. There doesn't seem to be a standard
    /// naming scheme for cursors but it looks like terminals such as Foot
    /// are moving towards using the W3C CSS cursor names. For OSC parsing,
    /// we just parse whatever string is given.
    mouse_shape: struct {
        value: [:0]const u8,
    },

    /// OSC color operations to set, reset, or report color settings. Some OSCs
    /// allow multiple operations to be specified in a single OSC so we need a
    /// list-like datastructure to manage them. We use std.SegmentedList because
    /// it minimizes the number of allocations and copies because a large
    /// majority of the time there will be only one operation per OSC.
    ///
    /// Currently, these OSCs are handled by `color_operation`:
    ///
    /// 4, 5, 10-19, 104, 105, 110-119
    color_operation: struct {
        op: color.Operation,
        requests: color.List = .{},
        terminator: Terminator = .st,
    },

    /// Kitty color protocol, OSC 21
    /// https://sw.kovidgoyal.net/kitty/color-stack/#id1
    kitty_color_protocol: kitty_color.OSC,

    /// Show a desktop notification (OSC 9 or OSC 777)
    show_desktop_notification: struct {
        title: [:0]const u8,
        body: [:0]const u8,
    },

    /// Start a hyperlink (OSC 8)
    hyperlink_start: struct {
        id: ?[:0]const u8 = null,
        uri: [:0]const u8,
    },

    /// End a hyperlink (OSC 8)
    hyperlink_end: void,

    /// ConEmu sleep (OSC 9;1)
    conemu_sleep: struct {
        duration_ms: u16,
    },

    /// ConEmu show GUI message box (OSC 9;2)
    conemu_show_message_box: [:0]const u8,

    /// ConEmu change tab title (OSC 9;3)
    conemu_change_tab_title: union(enum) {
        reset,
        value: [:0]const u8,
    },

    /// ConEmu progress report (OSC 9;4)
    conemu_progress_report: ProgressReport,

    /// ConEmu wait input (OSC 9;5)
    conemu_wait_input,

    /// ConEmu GUI macro (OSC 9;6)
    conemu_guimacro: [:0]const u8,

    /// ConEmu run process (OSC 9;7)
    conemu_run_process: [:0]const u8,

    /// ConEmu output environment variable (OSC 9;8)
    conemu_output_environment_variable: [:0]const u8,

    /// ConEmu XTerm keyboard and output emulation (OSC 9;10)
    /// https://conemu.github.io/en/TerminalModes.html
    conemu_xterm_emulation: struct {
        /// null => do not change
        /// false => turn off
        /// true => turn on
        keyboard: ?bool,
        /// null => do not change
        /// false => turn off
        /// true => turn on
        output: ?bool,
    },

    /// ConEmu comment (OSC 9;11)
    conemu_comment: [:0]const u8,

    /// Kitty text sizing protocol (OSC 66)
    kitty_text_sizing: parsers.kitty_text_sizing.OSC,

    kitty_clipboard_protocol: KittyClipboardProtocol,

    /// Kitty drag and drop protocol (OSC 72)
    kitty_dnd_protocol: KittyDndProtocol,

    /// OSC 3008. Hierarchical context signalling (UAPI spec).
    /// https://uapi-group.org/specifications/specs/osc_context/
    context_signal: parsers.context_signal.Command,

    /// iTerm2 OSC 1337 File= inline image transmission.
    /// https://iterm2.com/documentation-images.html
    iterm2_image_transmit: Iterm2ImageTransmit,

    /// iTerm2 OSC 1337 multipart inline image transmission. The wire
    /// format is a sequence of three OSC kinds (MultipartFile, FilePart
    /// repeated, FileEnd); the parser emits one event per OSC and the
    /// consumer (a multipart assembler in the stream handler) stitches
    /// them. iTerm2 has no session identifier, so transfers are
    /// strictly serialized.
    iterm2_multipart_image: Iterm2MultipartEvent,

    /// iTerm2 OSC 1337 ReportCellSize query. The terminal replies with
    /// `OSC 1337;ReportCellSize=H;W;scale ST` where H and W are cell
    /// pixel height and width. iTerm2's bare-key query carries no
    /// payload; the parser rejects any `=value` form because it would
    /// collide with the response wire format.
    iterm2_report_cell_size,

    pub const SemanticPrompt = parsers.semantic_prompt.Command;

    /// iTerm2 OSC 1337 File= inline image payload + parsed geometry hints.
    pub const Iterm2ImageTransmit = struct {
        /// Raw base64-encoded image bytes. The consumer is responsible
        /// for decode + format sniff. The slice is not null-terminated;
        /// the multipart assembler produces it from heap concatenation
        /// and the single-shot parser produces it as an interior slice
        /// of its capture buffer.
        payload: []const u8,

        /// Geometry hints parsed from the options block. Defaults
        /// preserve the image's native sizing.
        hints: Iterm2ImageHints = .{},

        /// C ABI representation. The Zig slice is split into a nested
        /// `payload: { ptr, len }` because slices aren't extern-compat.
        ///
        /// LIFETIME: `payload.ptr` is borrowed -- it points into the
        /// parser's capture buffer (single-shot dispatch) or the
        /// multipart assembler's heap concatenation. Consumers must
        /// copy before returning from the dispatch callback.
        // Sync with: ghostty_osc_iterm2_image_transmit_s
        pub const C = extern struct {
            payload: extern struct {
                ptr: [*]const u8,
                len: usize,
            },
            hints: Iterm2ImageHints,
        };

        pub fn cval(self: Iterm2ImageTransmit) C {
            return .{
                .payload = .{ .ptr = self.payload.ptr, .len = self.payload.len },
                .hints = self.hints,
            };
        }
    };

    /// Subset of iTerm2 File= options that can be expressed in the
    /// Kitty graphics Display struct. Unsupported iTerm2 forms
    /// (pixel and percent sizing) are dropped by the parser with a
    /// log.warn.
    ///
    /// Declared `extern struct` so it can be embedded in the C ABI
    /// CValue union directly: every field (u32, u32, bool) is
    /// extern-compatible, but a plain Zig struct doesn't carry a
    /// layout guarantee that the extern union requires.
    pub const Iterm2ImageHints = extern struct {
        /// Display width in terminal cells. 0 = no preference; the
        /// renderer falls back to the image's native sizing.
        columns: u32 = 0,

        /// Display height in terminal cells. 0 = no preference.
        rows: u32 = 0,

        /// Whether to preserve the image's aspect ratio. iTerm2's
        /// default is true. False is only meaningfully honored when
        /// both columns and rows are non-zero, because Kitty stretches
        /// only when both display dimensions are supplied.
        preserve_aspect_ratio: bool = true,
    };

    /// One step of an iTerm2 multipart File= transfer.
    pub const Iterm2MultipartEvent = union(enum) {
        /// `OSC 1337;MultipartFile=options` started a new transfer.
        /// The hints are parsed from the options block. inline=1 is
        /// required at this layer; the parser rejects MultipartFile
        /// without it as .invalid rather than emitting this event.
        start: Iterm2ImageHints,

        /// `OSC 1337;FilePart=BASE64_CHUNK` continued the active
        /// transfer with one more base64 chunk. The slice points into
        /// the parser's capture buffer; the assembler must copy it
        /// before the next OSC resets the buffer.
        chunk: []const u8,

        /// `OSC 1337;FileEnd` terminated the active transfer. The
        /// assembler decodes + dispatches the accumulated payload.
        end,

        // Sync with: ghostty_osc_iterm2_multipart_event_tag_e
        pub const Tag = enum(c_int) {
            start,
            chunk,
            end,
        };

        /// C ABI representation. Mirrors the apprt/action.zig
        /// `KeyTable` pattern: a `Tag` discriminant alongside an
        /// `extern union` of the variants' payloads, both wrapped in
        /// an outer `extern struct { tag, value }`.
        ///
        /// LIFETIME: `chunk.ptr` is borrowed from the parser's
        /// capture buffer. Consumers must copy before the next OSC
        /// dispatches.
        // Sync with: ghostty_osc_iterm2_multipart_event_u
        pub const CValue = extern union {
            start: Iterm2ImageHints,
            chunk: extern struct {
                ptr: [*]const u8,
                len: usize,
            },
        };

        // Sync with: ghostty_osc_iterm2_multipart_event_s
        pub const C = extern struct {
            tag: Tag,
            value: CValue,
        };

        pub fn cval(self: Iterm2MultipartEvent) C {
            return switch (self) {
                .start => |h| .{ .tag = .start, .value = .{ .start = h } },
                .chunk => |s| .{
                    .tag = .chunk,
                    .value = .{ .chunk = .{ .ptr = s.ptr, .len = s.len } },
                },
                .end => .{ .tag = .end, .value = undefined },
            };
        }
    };

    pub const KittyClipboardProtocol = parsers.kitty_clipboard_protocol.OSC;

    pub const KittyDndProtocol = parsers.kitty_dnd_protocol.OSC;

    pub const Key = LibEnum(
        lib.target,
        // NOTE: Order matters, see LibEnum documentation.
        &.{
            "invalid",
            "change_window_title",
            "change_window_icon",
            "semantic_prompt",
            "clipboard_contents",
            "report_pwd",
            "mouse_shape",
            "color_operation",
            "kitty_color_protocol",
            "show_desktop_notification",
            "hyperlink_start",
            "hyperlink_end",
            "conemu_sleep",
            "conemu_show_message_box",
            "conemu_change_tab_title",
            "conemu_progress_report",
            "conemu_wait_input",
            "conemu_guimacro",
            "conemu_run_process",
            "conemu_output_environment_variable",
            "conemu_xterm_emulation",
            "conemu_comment",
            "kitty_text_sizing",
            "kitty_clipboard_protocol",
            "kitty_dnd_protocol",
            "context_signal",
            "iterm2_image_transmit",
            "iterm2_multipart_image",
            "iterm2_report_cell_size",
        },
    );

    pub const ProgressReport = struct {
        pub const State = enum(c_int) {
            remove,
            set,
            @"error",
            indeterminate,
            pause,

            test "ghostty.h Command.ProgressReport.State" {
                if (comptime build_options.artifact == .lib) return error.SkipZigTest;
                try lib.checkGhosttyHEnum(State, "GHOSTTY_PROGRESS_STATE_");
            }
        };

        state: State,
        progress: ?u8 = null,

        // sync with ghostty_action_progress_report_s
        pub const C = extern struct {
            state: c_int,
            progress: i8,
        };

        pub fn cval(self: ProgressReport) C {
            return .{
                .state = @intFromEnum(self.state),
                .progress = if (self.progress) |progress| @intCast(std.math.clamp(
                    progress,
                    0,
                    100,
                )) else -1,
            };
        }
    };

    comptime {
        assert(@sizeOf(Command) == switch (@sizeOf(usize)) {
            4 => 44,
            8 => 64,
            else => unreachable,
        });
    }
};

/// The terminator used to end an OSC command. For OSC commands that demand
/// a response, we try to match the terminator used in the request since that
/// is most likely to be accepted by the calling program.
pub const Terminator = enum {
    /// The preferred string terminator is ESC followed by \
    st,

    /// Some applications and terminals use BELL (0x07) as the string terminator.
    bel,

    pub const C = LibEnum(.c, &.{ "st", "bel" });

    /// Initialize the terminator based on the last byte seen. If the
    /// last byte is a BEL then we use BEL, otherwise we just assume ST.
    pub fn init(ch: ?u8) Terminator {
        return switch (ch orelse return .st) {
            0x07 => .bel,
            else => .st,
        };
    }

    /// The terminator as a string. This is static memory so it doesn't
    /// need to be freed.
    pub fn string(self: Terminator) []const u8 {
        return switch (self) {
            .st => "\x1b\\",
            .bel => "\x07",
        };
    }

    pub fn cval(self: Terminator) C {
        return switch (self) {
            .st => .st,
            .bel => .bel,
        };
    }

    pub fn format(
        self: Terminator,
        comptime _: []const u8,
        _: std.fmt.FormatOptions,
        writer: *std.Io.Writer,
    ) !void {
        try writer.writeAll(self.string());
    }
};

pub const Parser = struct {
    /// Maximum size of a "normal" OSC.
    pub const MAX_BUF = 2048;

    /// Optional allocator used to accept data longer than MAX_BUF.
    /// This only applies to some commands (e.g. OSC 52) that can
    /// reasonably exceed MAX_BUF.
    alloc: ?Allocator,

    /// Current state of the parser.
    state: State,

    /// Buffer for temporary storage of OSC data
    buffer: [MAX_BUF]u8,

    /// Capture state. If this is set then we're actively capturing the
    /// bytes coming into the parser.
    capture: ?Capture,

    /// The command that is the result of parsing.
    command: Command,

    pub const State = enum {
        start,
        invalid,

        // OSC command prefixes. Not all of these are valid OSCs, but may be
        // needed to "bridge" to a valid OSC (e.g. to support OSC 777 we need to
        // have a state "77" even though there is no OSC 77).
        @"0",
        @"1",
        @"2",
        @"3",
        @"4",
        @"5",
        @"6",
        @"7",
        @"8",
        @"9",
        @"30",
        @"300",
        @"3008",
        @"10",
        @"11",
        @"12",
        @"13",
        @"14",
        @"15",
        @"16",
        @"17",
        @"18",
        @"19",
        @"21",
        @"22",
        @"52",
        @"55",
        @"66",
        @"72",
        @"77",
        @"104",
        @"110",
        @"111",
        @"112",
        @"113",
        @"114",
        @"115",
        @"116",
        @"117",
        @"118",
        @"119",
        @"133",
        @"552",
        @"777",
        @"1337",
        @"5522",
    };

    pub fn init(alloc: ?Allocator) Parser {
        var result: Parser = .{
            .alloc = alloc,
            .state = .start,
            .capture = null,
            .command = .invalid,

            // Keeping all our undefined values together so we can
            // visually easily duplicate them in the Valgrind check below.
            .buffer = undefined,
        };
        if (std.valgrind.runningOnValgrind() > 0) {
            // Initialize our undefined fields so Valgrind can catch it.
            // https://github.com/ziglang/zig/issues/19148
            result.buffer = undefined;
        }

        return result;
    }

    /// This must be called to clean up any allocated memory.
    pub fn deinit(self: *Parser) void {
        self.reset();
    }

    /// Reset the parser state.
    pub fn reset(self: *Parser) void {
        // If we're capturing, then stop it.
        if (self.capture) |*cap| cap.deinit();

        // Handle any cleanup that individual OSCs require.
        switch (self.command) {
            .kitty_color_protocol => |*v| kitty_color_protocol: {
                v.deinit(self.alloc orelse break :kitty_color_protocol);
            },
            .color_operation => |*v| color_operation: {
                v.requests.deinit(self.alloc orelse break :color_operation);
            },
            .change_window_icon,
            .change_window_title,
            .clipboard_contents,
            .conemu_change_tab_title,
            .conemu_comment,
            .conemu_guimacro,
            .conemu_output_environment_variable,
            .conemu_progress_report,
            .conemu_run_process,
            .conemu_show_message_box,
            .conemu_sleep,
            .conemu_wait_input,
            .conemu_xterm_emulation,
            .hyperlink_end,
            .hyperlink_start,
            .invalid,
            .mouse_shape,
            .report_pwd,
            .semantic_prompt,
            .show_desktop_notification,
            .kitty_text_sizing,
            .kitty_clipboard_protocol,
            .kitty_dnd_protocol,
            .context_signal,
            .iterm2_image_transmit,
            .iterm2_multipart_image,
            .iterm2_report_cell_size,
            => {},
        }

        self.state = .start;
        self.capture = null;
        self.command = .invalid;

        if (std.valgrind.runningOnValgrind() > 0) {
            // Initialize our undefined fields so Valgrind can catch it.
            // https://github.com/ziglang/zig/issues/19148
            self.buffer = undefined;
        }
    }

    /// Make sure that we have an allocator. If we don't, set the state to
    /// invalid so that any additional OSC data is discarded.
    inline fn ensureAllocator(self: *Parser) bool {
        if (self.alloc != null) return true;
        log.warn("An allocator is required to process OSC {t} but none was provided.", .{self.state});
        self.state = .invalid;
        return false;
    }

    const Capture = struct {
        writer: *std.Io.Writer,
        backing: Backing,

        const Backing = union(enum) {
            fixed: std.Io.Writer,
            allocating: std.Io.Writer.Allocating,
        };

        const Mode = enum {
            fixed,
            allocating,
        };

        pub inline fn fixed(new: *?Capture, buf: []u8) void {
            new.* = .{
                .backing = .{ .fixed = .fixed(buf) },
                .writer = &new.*.?.backing.fixed,
            };
        }

        pub inline fn allocating(
            new: *?Capture,
            alloc: Allocator,
        ) error{OutOfMemory}!void {
            new.* = .{
                .backing = .{ .allocating = try std.Io.Writer.Allocating.initCapacity(
                    alloc,
                    2048,
                ) },
                .writer = &new.*.?.backing.allocating.writer,
            };
        }

        pub fn deinit(self: *Capture) void {
            switch (self.backing) {
                .fixed => {},
                .allocating => |*w| w.deinit(),
            }
        }

        /// Return the captured trailing data. This is the data from the
        /// point that trailing data capture was requested.
        pub inline fn trailing(self: *Capture) []u8 {
            return self.writer.buffered();
        }
    };

    /// Begin capturing trailing data. All inputs to next from this point
    /// forward will be captured into the `self.capture.writer` buffer
    /// which may be backed by either a fixed size or allocating buffer
    /// depending on mode.
    ///
    /// Get the trailing data using `capture.trailing()`. Do not access
    /// the writer directly.
    inline fn captureTrailing(
        self: *Parser,
        comptime mode: Capture.Mode,
    ) void {
        assert(self.capture == null);
        switch (mode) {
            .fixed => Capture.fixed(
                &self.capture,
                &self.buffer,
            ),

            .allocating => {
                const alloc = self.alloc orelse {
                    // We don't have an allocator - fall back to a fixed buffer and hope
                    // that it's big enough.
                    self.captureTrailing(.fixed);
                    return;
                };

                Capture.allocating(
                    &self.capture,
                    alloc,
                ) catch {
                    // The allocator failed for some reason, fall back to a fixed buffer
                    // and hope that it's big enough.
                    self.captureTrailing(.fixed);
                    return;
                };
            },
        }
    }

    /// Consume the next character c and advance the parser state.
    pub fn next(self: *Parser, c: u8) void {
        // If the state becomes invalid for any reason, just discard
        // any further input.
        if (self.state == .invalid) return;

        // If a writer has been initialized, we just accumulate the rest of the
        // OSC sequence in the writer's buffer and skip the state machine.
        if (self.capture) |*cap| {
            cap.writer.writeByte(c) catch |err| switch (err) {
                // We have overflowed our buffer or had some other error, set the
                // state to invalid so that we discard any further input.
                error.WriteFailed => self.state = .invalid,
            };
            return;
        }

        switch (self.state) {
            // handled above, so should never be here
            .invalid => unreachable,

            .start => switch (c) {
                '0' => self.state = .@"0",
                '1' => self.state = .@"1",
                '2' => self.state = .@"2",
                '3' => self.state = .@"3",
                '4' => self.state = .@"4",
                '5' => self.state = .@"5",
                '6' => self.state = .@"6",
                '7' => self.state = .@"7",
                '8' => self.state = .@"8",
                '9' => self.state = .@"9",
                else => self.state = .invalid,
            },

            .@"3" => switch (c) {
                '0' => self.state = .@"30",
                else => self.state = .invalid,
            },

            .@"30" => switch (c) {
                '0' => self.state = .@"300",
                else => self.state = .invalid,
            },

            .@"300" => switch (c) {
                '8' => self.state = .@"3008",
                else => self.state = .invalid,
            },

            .@"3008" => switch (c) {
                ';' => self.captureTrailing(.fixed),
                else => self.state = .invalid,
            },

            .@"1" => switch (c) {
                ';' => self.captureTrailing(.fixed),
                '0' => self.state = .@"10",
                '1' => self.state = .@"11",
                '2' => self.state = .@"12",
                '3' => self.state = .@"13",
                '4' => self.state = .@"14",
                '5' => self.state = .@"15",
                '6' => self.state = .@"16",
                '7' => self.state = .@"17",
                '8' => self.state = .@"18",
                '9' => self.state = .@"19",
                else => self.state = .invalid,
            },

            .@"10" => switch (c) {
                ';' => if (self.ensureAllocator()) self.captureTrailing(.fixed),
                '4' => self.state = .@"104",
                else => self.state = .invalid,
            },

            .@"104" => switch (c) {
                ';' => if (self.ensureAllocator()) self.captureTrailing(.fixed),
                else => self.state = .invalid,
            },

            .@"11" => switch (c) {
                ';' => if (self.ensureAllocator()) self.captureTrailing(.fixed),
                '0' => self.state = .@"110",
                '1' => self.state = .@"111",
                '2' => self.state = .@"112",
                '3' => self.state = .@"113",
                '4' => self.state = .@"114",
                '5' => self.state = .@"115",
                '6' => self.state = .@"116",
                '7' => self.state = .@"117",
                '8' => self.state = .@"118",
                '9' => self.state = .@"119",
                else => self.state = .invalid,
            },

            .@"4",
            .@"12",
            .@"14",
            .@"15",
            .@"16",
            .@"17",
            .@"18",
            .@"19",
            .@"21",
            .@"110",
            .@"111",
            .@"112",
            .@"113",
            .@"114",
            .@"115",
            .@"116",
            .@"117",
            .@"118",
            .@"119",
            => switch (c) {
                ';' => if (self.ensureAllocator()) self.captureTrailing(.fixed),
                else => self.state = .invalid,
            },

            .@"13" => switch (c) {
                ';' => if (self.ensureAllocator()) self.captureTrailing(.fixed),
                '3' => self.state = .@"133",
                else => self.state = .invalid,
            },

            .@"2" => switch (c) {
                ';' => self.captureTrailing(.fixed),
                '1' => self.state = .@"21",
                '2' => self.state = .@"22",
                else => self.state = .invalid,
            },

            .@"5" => switch (c) {
                ';' => if (self.ensureAllocator()) self.captureTrailing(.fixed),
                '2' => self.state = .@"52",
                '5' => self.state = .@"55",
                else => self.state = .invalid,
            },

            .@"6" => switch (c) {
                '6' => self.state = .@"66",
                else => self.state = .invalid,
            },

            .@"52",
            .@"66",
            => switch (c) {
                ';' => self.captureTrailing(.allocating),
                else => self.state = .invalid,
            },

            .@"55" => switch (c) {
                '2' => self.state = .@"552",
                else => self.state = .invalid,
            },

            .@"7" => switch (c) {
                ';' => self.captureTrailing(.fixed),
                '2' => self.state = .@"72",
                '7' => self.state = .@"77",
                else => self.state = .invalid,
            },

            .@"72" => switch (c) {
                ';' => self.captureTrailing(.allocating),
                else => self.state = .invalid,
            },

            .@"77" => switch (c) {
                '7' => self.state = .@"777",
                else => self.state = .invalid,
            },

            .@"133",
            => switch (c) {
                ';' => self.captureTrailing(.fixed),
                '7' => self.state = .@"1337",
                else => self.state = .invalid,
            },

            .@"552" => switch (c) {
                '2' => self.state = .@"5522",
                else => self.state = .invalid,
            },

            .@"1337",
            => switch (c) {
                ';' => self.captureTrailing(.fixed),
                else => self.state = .invalid,
            },

            .@"5522",
            => switch (c) {
                ';' => self.captureTrailing(.allocating),
                else => self.state = .invalid,
            },

            .@"0",
            .@"22",
            .@"777",
            .@"8",
            .@"9",
            => switch (c) {
                ';' => self.captureTrailing(.fixed),
                else => self.state = .invalid,
            },
        }
    }

    /// End the sequence and return the command, if any. If the return value
    /// is null, then no valid command was found. The optional terminator_ch
    /// is the final character in the OSC sequence. This is used to determine
    /// the response terminator.
    ///
    /// The returned pointer is only valid until the next call to the parser.
    /// Callers should copy out any data they wish to retain across calls.
    pub fn end(self: *Parser, terminator_ch: ?u8) ?*Command {
        return switch (self.state) {
            .start => null,

            .invalid => null,

            .@"0",
            .@"2",
            => parsers.change_window_title.parse(self, terminator_ch),

            .@"1" => parsers.change_window_icon.parse(self, terminator_ch),

            .@"4",
            .@"5",
            .@"10",
            .@"11",
            .@"12",
            .@"13",
            .@"14",
            .@"15",
            .@"16",
            .@"17",
            .@"18",
            .@"19",
            .@"104",
            .@"110",
            .@"111",
            .@"112",
            .@"113",
            .@"114",
            .@"115",
            .@"116",
            .@"117",
            .@"118",
            .@"119",
            => parsers.color.parse(self, terminator_ch),

            .@"7" => parsers.report_pwd.parse(self, terminator_ch),

            .@"8" => parsers.hyperlink.parse(self, terminator_ch),

            .@"9" => parsers.osc9.parse(self, terminator_ch),

            .@"21" => parsers.kitty_color.parse(self, terminator_ch),

            .@"22" => parsers.mouse_shape.parse(self, terminator_ch),

            .@"52" => parsers.clipboard_operation.parse(self, terminator_ch),

            .@"55" => null,

            .@"3",
            .@"30",
            .@"300",
            => null,

            .@"3008" => parsers.context_signal.parse(self, terminator_ch),

            .@"6" => null,

            .@"66" => parsers.kitty_text_sizing.parse(self, terminator_ch),

            .@"72" => parsers.kitty_dnd_protocol.parse(self, terminator_ch),

            .@"77" => null,

            .@"133" => parsers.semantic_prompt.parse(self, terminator_ch),

            .@"552" => null,

            .@"777" => parsers.rxvt_extension.parse(self, terminator_ch),

            .@"1337" => parsers.iterm2.parse(self, terminator_ch),

            .@"5522" => parsers.kitty_clipboard_protocol.parse(self, terminator_ch),
        };
    }
};

test {
    _ = parsers;
    _ = encoding;
}
