const std = @import("std");
const Allocator = std.mem.Allocator;
const renderer = @import("../renderer.zig");
const terminal = @import("../terminal/main.zig");
const termio = @import("../termio.zig");
const MessageData = @import("../datastruct/main.zig").MessageData;

/// The messages that can be sent to an IO thread.
///
/// This is not a tiny structure (~40 bytes at the time of writing this comment),
/// but the messages are IO thread sends are also very few. At the current size
/// we can queue 26,000 messages before consuming a MB of RAM.
pub const Message = union(enum) {
    /// Represents a write request. Magic number comes from the largest
    /// other union value. It can be upped if we add a larger union member
    /// in the future.
    pub const WriteReq = MessageData(u8, 38);

    /// Classification of a pty write so the termio thread can decide
    /// whether to deliver it to the child. Used by Windows to suppress
    /// VT response bytes when the child is pwsh under raw-pipe (bypass)
    /// stdin: in that configuration PSReadLine is disabled and
    /// Console.ReadLine treats the response bytes as command text,
    /// polluting the prompt buffer.
    ///
    /// The default is `.input` so writes that aren't explicitly tagged
    /// (keystrokes, paste, mouse events, focus events, anything user-
    /// driven) pass through unchanged.
    pub const WriteKind = enum {
        /// User-driven write: keystrokes, paste, mouse events, focus
        /// events, anything that should always reach the child.
        input,
        /// Reply to a parser-driven query from the child (cursor
        /// position, device attributes, color queries, etc.). May be
        /// suppressed on Windows when the child cannot consume CSI
        /// bytes on stdin.
        response,
    };

    /// Wrappers around the three `WriteReq` variants that carry an
    /// extra `kind` tag (see `WriteKind`). Field names match
    /// `WriteReq.Small`/`WriteReq.Alloc` so call sites that don't care
    /// about `kind` keep working unchanged via the default.
    pub const WriteSmallReq = struct {
        data: WriteReq.Small.Array = undefined,
        len: WriteReq.Small.Len = 0,
        kind: WriteKind = .input,
    };

    pub const WriteStableReq = struct {
        data: []const u8,
        kind: WriteKind = .input,
    };

    pub const WriteAllocReq = struct {
        alloc: Allocator,
        data: []u8,
        kind: WriteKind = .input,
    };

    /// Request a color scheme report is sent to the pty.
    color_scheme_report: struct {
        /// Force write the current color scheme
        force: bool,
    },

    /// Purposely crash the renderer. This is used for testing and debugging.
    /// See the "crash" binding action.
    crash: void,

    /// The derived configuration to update the implementation with. This
    /// is allocated via the allocator and is expected to be freed when done.
    change_config: struct {
        alloc: Allocator,
        ptr: *termio.Termio.DerivedConfig,
    },

    /// Activate or deactivate the inspector.
    inspector: bool,

    /// Resize the window.
    resize: renderer.Size,

    /// Request a size report is sent to the pty ([in-band
    /// size report, mode 2048](https://gist.github.com/rockorager/e695fb2924d36b2bcf1fff4a3704bd83) and
    /// [XTWINOPS](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html#h4-Functions-using-CSI-_-ordered-by-the-final-character-lparen-s-rparen:CSI-Ps;Ps;Ps-t.1EB0)).
    size_report: SizeReport,

    /// Clear the screen.
    clear_screen: struct {
        /// Include clearing the history
        history: bool,
    },

    /// Scroll the viewport
    scroll_viewport: terminal.Terminal.ScrollViewport,

    /// Selection scrolling. If this is set to true then the termio
    /// thread starts a timer that will trigger a `selection_scroll_tick`
    /// message back to the surface. This ping/pong is because the
    /// surface thread doesn't have access to an event loop from libghostty.
    selection_scroll: bool,

    /// Jump forward/backward n prompts.
    jump_to_prompt: isize,

    /// Send this when a synchronized output mode is started. This will
    /// start the timer so that the output mode is disabled after a
    /// period of time so that a bad actor can't hang the terminal.
    start_synchronized_output: void,

    /// Enable or disable linefeed mode (mode 20).
    linefeed_mode: bool,

    /// The surface gained or lost focus.
    focused: bool,

    /// Write where the data fits in the union.
    write_small: WriteSmallReq,

    /// Write where the data pointer is stable.
    write_stable: WriteStableReq,

    /// Write where the data is allocated and must be freed.
    write_alloc: WriteAllocReq,

    /// Return a write request for the given data. This will use
    /// write_small if it fits or write_alloc otherwise. This should NOT
    /// be used for stable pointers which can be manually set to write_stable.
    ///
    /// The resulting message is tagged as `.input`. Callers that want to
    /// tag the write as a parser-driven response should set
    /// `.kind = .response` on the resulting variant before sending.
    pub fn writeReq(alloc: Allocator, data: anytype) !Message {
        return switch (try WriteReq.init(alloc, data)) {
            .stable => unreachable,
            .small => |v| Message{ .write_small = .{ .data = v.data, .len = v.len } },
            .alloc => |v| Message{ .write_alloc = .{ .alloc = v.alloc, .data = v.data } },
        };
    }

    /// The types of size reports that we support.
    pub const SizeReport = terminal.size_report.Style;
};

test {
    std.testing.refAllDecls(@This());
}

test {
    // Ensure we don't grow our IO message size without explicitly wanting to.
    // The previous size was 40 bytes; adding the WriteKind tag bumped the
    // largest write variant by one byte. The bump is intentional - see
    // WriteKind for the reason. Update this constant only when another
    // intentional growth happens.
    const testing = std.testing;
    try testing.expectEqual(@as(usize, 41), @sizeOf(Message));
}
