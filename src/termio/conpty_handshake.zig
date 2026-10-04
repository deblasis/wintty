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
/// The same reply sessiond sends, and the string a reader here hands conpty
/// before the parser could. Pinned against the parser's own answer by
/// "DA1: what the parser answers is what the pty reader sends" in
/// stream_handler.zig, which runs a real parser; comparing this against a
/// second copy of the literal here would only prove the file agrees with
/// itself.
pub const DA1_REPLY = "\x1b[?62;4;22c";

/// How far into a pty's output the handshake can be. conpty's own
/// preamble is a few dozen bytes; anything later is the child's.
pub const WINDOW: usize = 256;

/// The most bytes of the window that can still be the front of a request on
/// the next read: everything but the last byte the request needs.
const CARRY_MAX = DA1_REQUEST.len - 1;

pub const Handshake = struct {
    seen: usize = 0,
    done: bool = false,

    /// The tail of the window already scanned and still unmatched, which is
    /// what a request conpty split between two writes (`ESC [` at the end of
    /// one read, `c` at the start of the next) is matched against. Without it
    /// the request is in neither chunk and the pane waits out the timeout.
    carry: [CARRY_MAX]u8 = @splat(0),
    carry_len: usize = 0,

    pub const Result = struct {
        /// The chunk with the request removed, a prefix of the input slice.
        bytes: []u8,
        /// Whether the caller owes conpty `DA1_REPLY` now.
        reply: bool,
    };

    /// Remove conpty's startup DA1 request from `chunk`, in place, the first
    /// time it appears within the window, whether the request lies wholly in
    /// this chunk or straddles the seam behind it. Every chunk after the
    /// window, or after the request was found, passes through untouched.
    pub fn filter(self: *Handshake, chunk: []u8) Result {
        if (self.done) return .{ .bytes = chunk, .reply = false };

        const room = WINDOW - self.seen;
        const scan_len = @min(chunk.len, room);

        // One window over the seam, so the request is matched whole while the
        // carry still counts for nothing: it is only ever the front of a match,
        // never a match of its own, so what leaves the chunk is measured in the
        // chunk rather than assumed to be the whole request.
        var window: [CARRY_MAX + WINDOW]u8 = undefined;
        @memcpy(window[0..self.carry_len], self.carry[0..self.carry_len]);
        @memcpy(window[self.carry_len..][0..scan_len], chunk[0..scan_len]);
        const filled = self.carry_len + scan_len;

        // The longest tail of the window that is still a proper prefix of the
        // request, which is the only thing a later read could complete. A
        // partial match, not a partial tail: `t ESC` is two bytes long and the
        // `t` cannot begin `ESC [ c`, and treating it as carry is what would
        // put the match's start before the chunk.
        var keep: usize = 0;
        var k: usize = CARRY_MAX;
        while (k > 0) : (k -= 1) {
            if (filled >= k and
                std.mem.eql(u8, window[filled - k .. filled], DA1_REQUEST[0..k]))
            {
                keep = k;
                break;
            }
        }

        const at = std.mem.indexOf(u8, window[0..filled], DA1_REQUEST);

        self.seen += scan_len;
        if (self.seen >= WINDOW) self.done = true;

        if (at) |i| {
            self.done = true;
            // The match may start in the carry, in which case part of it is
            // already in the chunk in front of this one and only its tail is
            // here to drop: the ESC [ went to the parser on the last read, and
            // the ESC that opens this one aborts the sequence they opened
            // rather than completing it.
            const first = @max(i, self.carry_len);
            const start = first - self.carry_len;
            const drop = i + DA1_REQUEST.len - first;
            const tail = chunk[start + drop ..];
            std.mem.copyForwards(u8, chunk[start..], tail);
            return .{ .bytes = chunk[0 .. chunk.len - drop], .reply = true };
        }

        self.carry_len = keep;
        @memcpy(self.carry[0..keep], window[filled - keep .. filled]);
        return .{ .bytes = chunk, .reply = false };
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

test "conpty handshake: a request split across two reads is still answered" {
    // conpty's preamble is one write in the recording, but the pty reader is
    // a reader: nothing guarantees a read returns what a write produced. With
    // the request cut at its last byte the first chunk holds `ESC [` and the
    // second holds `c`, so no chunk ever contains `ESC [ c` on its own and a
    // filter that scans each chunk alone leaves the pane waiting out the
    // timeout.
    var hs: Handshake = .{};
    var head = "\x1b[1t\x1b[".*;
    const first = hs.filter(&head);
    try testing.expect(!first.reply);
    // The partial prefix goes to the parser as it is: a CSI introducer with
    // nothing after it is the parser's to buffer, and the ESC that opens the
    // next chunk aborts it.
    try testing.expectEqualStrings("\x1b[1t\x1b[", first.bytes);

    var tail = "c\x1b[?1004h".*;
    const second = hs.filter(&tail);
    try testing.expect(second.reply);
    // Only the byte still in this chunk is dropped; the ESC [ went out above.
    try testing.expectEqualStrings("\x1b[?1004h", second.bytes);
    try testing.expect(hs.done);
}

test "conpty handshake: a request split one byte early is answered too" {
    // The other seam: the chunk ends after the ESC and the next one opens with
    // the bracket, so the carry holds one byte and two thirds of the request
    // are still in this chunk.
    var hs: Handshake = .{};
    var head = "\x1b[1t\x1b".*;
    try testing.expect(!hs.filter(&head).reply);

    var tail = "[c\x1b[?1004h".*;
    const r = hs.filter(&tail);
    try testing.expect(r.reply);
    try testing.expectEqualStrings("\x1b[?1004h", r.bytes);
}

test "conpty handshake: a prefix that never completes is the parser's" {
    // The carry is not a claim: an ESC that no request follows is ordinary
    // output, and it must reach the parser whole rather than be swallowed and
    // re-emitted with the next chunk.
    var hs: Handshake = .{};
    var head = "out\x1b".*;
    try testing.expect(!hs.filter(&head).reply);

    var tail = "\x1b]0;title\x07$ ".*;
    const r = hs.filter(&tail);
    try testing.expect(!r.reply);
    try testing.expectEqualStrings("\x1b]0;title\x07$ ", r.bytes);
    try testing.expect(!hs.done);
}
