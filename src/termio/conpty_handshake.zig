//! The bundled conpty's startup handshake, answered by the pty reader.
//!
//! conpty.dll 1.22 and later open every pseudoconsole by writing
//! `ESC [ 1 t`, then asking the terminal for its primary device attributes
//! (`ESC [ c`, DA1), and hold the child's first output until an answer
//! arrives or a timeout runs out. With the 1.25 build Wintty bundles that
//! timeout measures about 3 s for cmd.exe (a probe answering at once gets
//! the child's first output at 46 ms), and a WSL child never comes out of
//! it at all: the pane stays blank with wsl.exe alive behind it, and one
//! DA1 reply typed into it releases the shell (deblasis/wintty#1268).
//!
//! The terminal parser answers DA1 too, but only by way of the stream
//! handler, the termio mailbox and the IO thread's write queue, none of
//! which is guaranteed to be up when conpty asks: the reader starts inside
//! `threadEnter`, before the IO loop is armed. A daemon pane never relied on
//! any of that (sessiond answers this request itself, in
//! src/sessiond/src/conpty_handshake.zig on the release side), so a local
//! pane now does the same: the Windows reader removes the request from the
//! stream and writes the reply straight to the pty's input. Removing it is
//! what keeps the parser from answering a second time; a second answer
//! would reach the shell as typed input.
//!
//! Only the handshake: the first `WINDOW` bytes of a pty's output, which
//! conpty writes before the child can print anything. A DA1 a program asks
//! later reaches the parser like every other sequence and is answered
//! there.
//!
//! Pure: the reader owns the bytes and the reply path.
const std = @import("std");

/// The request, as conpty writes it.
pub const DA1_REQUEST = "\x1b[c";

/// What the parser answers for DA1 (`StreamHandler.deviceAttributes`,
/// clipboard access withheld): level 2 conformance, Sixel, colour text.
/// The same reply sessiond sends.
pub const DA1_REPLY = "\x1b[?62;4;22c";

/// How far into a pty's output the handshake can be. conpty's own
/// preamble is a few dozen bytes; anything later is the child's.
pub const WINDOW: usize = 256;

pub const Handshake = struct {
    seen: usize = 0,
    done: bool = false,

    pub const Result = struct {
        /// The chunk with the request removed, a prefix of the input slice.
        bytes: []u8,
        /// Whether the caller owes conpty `DA1_REPLY` now.
        reply: bool,
    };

    /// Remove conpty's startup DA1 request from `chunk`, in place, the first
    /// time it appears within the window. Every chunk after the window, or
    /// after the request was found, passes through untouched.
    pub fn filter(self: *Handshake, chunk: []u8) Result {
        if (self.done) return .{ .bytes = chunk, .reply = false };
        const room = WINDOW - self.seen;
        const scan = chunk[0..@min(chunk.len, room)];
        self.seen += scan.len;
        if (self.seen >= WINDOW) self.done = true;
        const at = std.mem.indexOf(u8, scan, DA1_REQUEST) orelse
            return .{ .bytes = chunk, .reply = false };
        self.done = true;
        const tail = chunk[at + DA1_REQUEST.len ..];
        std.mem.copyForwards(u8, chunk[at..], tail);
        return .{ .bytes = chunk[0 .. chunk.len - DA1_REQUEST.len], .reply = true };
    }
};

const testing = std.testing;

test "conpty handshake: the 1.25 preamble is answered and the request removed" {
    // The exact bytes the bundled conpty.dll 1.25 writes first, as a probe
    // recorded them: `ESC [ 1 t`, then the request with focus and
    // win32-input mode on, in that order and in one write.
    var buf = "\x1b[1t\x1b[c\x1b[?1004h\x1b[?9001h".*;
    var hs: Handshake = .{};
    const r = hs.filter(&buf);
    try testing.expect(r.reply);
    try testing.expectEqualStrings("\x1b[1t\x1b[?1004h\x1b[?9001h", r.bytes);
    try testing.expect(hs.done);
}

test "conpty handshake: answered once, so a later DA1 reaches the parser" {
    var hs: Handshake = .{};
    var first = "\x1b[c".*;
    try testing.expect(hs.filter(&first).reply);

    // A program asking for DA1 itself is the parser's to answer.
    var later = "prompt\x1b[c".*;
    const r = hs.filter(&later);
    try testing.expect(!r.reply);
    try testing.expectEqualStrings("prompt\x1b[c", r.bytes);
}

test "conpty handshake: a request past the window is not conpty's" {
    var hs: Handshake = .{};
    var filler: [WINDOW]u8 = @splat('x');
    const f = hs.filter(&filler);
    try testing.expect(!f.reply);
    try testing.expectEqual(@as(usize, WINDOW), f.bytes.len);
    try testing.expect(hs.done);

    var late = "\x1b[c".*;
    const r = hs.filter(&late);
    try testing.expect(!r.reply);
    try testing.expectEqualStrings("\x1b[c", r.bytes);
}

test "conpty handshake: an inbox conhost that never asks passes through" {
    var hs: Handshake = .{};
    var out = "hello\r\n".*;
    const r = hs.filter(&out);
    try testing.expect(!r.reply);
    try testing.expectEqualStrings("hello\r\n", r.bytes);
    try testing.expect(!hs.done);
}

test "conpty handshake: the reply is the parser's own DA1 answer" {
    // The parser answers with this when clipboard writes are denied; a
    // reply that differed would tell conpty something the terminal does
    // not say about itself.
    try testing.expectEqualStrings("\x1b[?62;4;22c", DA1_REPLY);
}
