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
//! `threadEnter`, before the IO loop is armed. So the Windows reader answers
//! the handshake itself: it removes the request from the stream and writes
//! the reply straight to the pty's input. Removing it is
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
/// The string a reader here hands conpty before the parser could. Pinned
/// against the parser's own answer by
/// "DA1: what the parser answers is what the pty reader sends" in
/// stream_handler.zig, which runs a real parser; comparing this against a
/// second copy of the literal here would only prove the file agrees with
/// itself.
pub const DA1_REPLY = "\x1b[?62;4;22c";

/// How far into a pty's output the handshake can be. conpty's own
/// preamble is a few dozen bytes; anything later is the child's.
pub const WINDOW: usize = 256;

/// The most bytes of the window that can still be the front of a request on
/// the next read: everything but the last byte the request needs. The
/// reader reserves this much at the head of its buffer so the handshake can
/// keep a held prefix there between reads.
pub const CARRY_MAX = DA1_REQUEST.len - 1;

pub const Handshake = struct {
    seen: usize = 0,
    done: bool = false,

    /// The front of a request a chunk seam cut in two, held back from the
    /// parser until the next read says what it was. `ESC [` emitted and
    /// left dangling in the parser's CSI state eats the next chunk's first
    /// byte as a sequence final whenever the child's output does not open
    /// with an ESC. A hold only ever sits at the end of a chunk, so it
    /// never reorders the bytes read after it; one left holding at the
    /// window boundary flushes, and the parser answers whatever completes
    /// past the window itself.
    carry: [CARRY_MAX]u8 = @splat(0),
    carry_len: usize = 0,

    pub const Result = struct {
        /// The chunk with the request removed, a prefix of the input slice.
        /// While the handshake is live, the bytes a seam cut off the front
        /// of the request ride here at the head of the next result rather
        /// than in the chunk they arrived in: the parser-visible stream is
        /// the input minus the request, byte for byte, just measured from
        /// one read later. The caller consumes a result before the next
        /// call, and everything returned is a slice of `buf`.
        bytes: []u8,
        /// Whether the caller owes conpty `DA1_REPLY` now.
        reply: bool,
    };

    /// Remove conpty's startup DA1 request from the chunk
    /// `buf[CARRY_MAX..][0..n]`, in place, the first time it appears within
    /// the window, whether the request lies wholly in this chunk or
    /// straddles the seam behind it. Every chunk after the window, or after
    /// the request was found, passes through untouched.
    ///
    /// `buf[0..CARRY_MAX]` belongs to the handshake between calls: the
    /// reader reads at `CARRY_MAX`, and a prefix of the request cut by the
    /// seam is staged there and withheld from the result until the next
    /// read resolves it, dropped instead if the request it belongs to
    /// completes. The caller must consume a result before the next call;
    /// everything returned is a slice of `buf`.
    pub fn filter(self: *Handshake, buf: []u8, n: usize) Result {
        const chunk = buf[CARRY_MAX..][0..n];
        if (self.done) return .{ .bytes = chunk, .reply = false };

        // Stage the held prefix in front of the chunk so the seam
        // disappears: one span, matched and cut as if conpty had written it
        // whole. Bytes past the window's last scanned byte ride behind it
        // untouched.
        const base = CARRY_MAX - self.carry_len;
        @memcpy(buf[base..][0..self.carry_len], self.carry[0..self.carry_len]);
        const scan_len = @min(n, WINDOW - self.seen);
        const comp = buf[base .. base + self.carry_len + scan_len];

        const at = std.mem.indexOf(u8, comp, DA1_REQUEST);

        self.seen += scan_len;

        if (at) |i| {
            self.done = true;
            self.carry_len = 0;
            // The whole request goes in one cut, wherever the seam fell:
            // the bytes before it (the preamble, a held prefix) and
            // everything after it are exactly what the parser would have
            // seen had conpty never asked.
            const span = buf[base .. CARRY_MAX + n];
            std.mem.copyForwards(u8, span[i..], span[i + DA1_REQUEST.len ..]);
            return .{ .bytes = buf[base .. CARRY_MAX + n - DA1_REQUEST.len], .reply = true };
        }

        // The longest tail of the composite that is still a proper prefix of
        // the request, which is the only thing a later read could complete.
        // A partial match, not a partial tail: `t ESC` is two bytes long and
        // the `t` cannot begin `ESC [ c`, and treating it as carry is what
        // would put the match's start before the chunk.
        var keep: usize = 0;
        var k: usize = @min(CARRY_MAX, comp.len);
        while (k > 0) : (k -= 1) {
            if (std.mem.eql(u8, comp[comp.len - k ..], DA1_REQUEST[0..k])) {
                keep = k;
                break;
            }
        }

        // A prefix may only be held when the scan ran to the end of the
        // chunk: a partial left in the middle of the span cannot be
        // withheld without reordering the bytes read after it, so it stays
        // in the span and the parser buffers it like any sequence a read
        // seam cut in two. With the window exhausted and nothing held, the
        // handshake is done; a prefix still held when no byte is left to
        // scan into it flushes as the ordinary output it then is, and
        // whatever completes past the window meets it at the parser, which
        // answers a leaked request once -- the reader never writes a second
        // reply beside it.
        const holdable = scan_len == n;
        if (!holdable or (self.seen >= WINDOW and keep == 0)) {
            self.done = self.seen >= WINDOW;
            self.carry_len = 0;
            keep = 0;
        } else {
            self.carry_len = keep;
            @memcpy(self.carry[0..keep], comp[comp.len - keep ..]);
        }

        return .{ .bytes = buf[base .. CARRY_MAX + n - keep], .reply = false };
    }
};

const testing = std.testing;

test "conpty handshake: the 1.25 preamble is answered and the request removed" {
    // The exact bytes the bundled conpty.dll 1.25 writes first, as a probe
    // recorded them: `ESC [ 1 t`, then the request with focus and
    // win32-input mode on, in that order and in one write.
    var hs: Handshake = .{};
    var buf: [CARRY_MAX + 32]u8 = undefined;
    const input = "\x1b[1t\x1b[c\x1b[?1004h\x1b[?9001h";
    @memcpy(buf[CARRY_MAX..][0..input.len], input);
    const r = hs.filter(&buf, input.len);
    try testing.expect(r.reply);
    try testing.expectEqualStrings("\x1b[1t\x1b[?1004h\x1b[?9001h", r.bytes);
    try testing.expect(hs.done);
}

test "conpty handshake: answered once, so a later DA1 reaches the parser" {
    var hs: Handshake = .{};
    var buf: [CARRY_MAX + 16]u8 = undefined;
    const req = "\x1b[c";
    @memcpy(buf[CARRY_MAX..][0..req.len], req);
    try testing.expect(hs.filter(&buf, req.len).reply);

    // A program asking for DA1 itself is the parser's to answer.
    var later_buf: [CARRY_MAX + 16]u8 = undefined;
    const later = "prompt\x1b[c";
    @memcpy(later_buf[CARRY_MAX..][0..later.len], later);
    const r = hs.filter(&later_buf, later.len);
    try testing.expect(!r.reply);
    try testing.expectEqualStrings("prompt\x1b[c", r.bytes);
}

test "conpty handshake: a request past the window is not conpty's" {
    var hs: Handshake = .{};
    var buf: [CARRY_MAX + WINDOW]u8 = undefined;
    @memset(buf[CARRY_MAX..], 'x');
    const f = hs.filter(&buf, WINDOW);
    try testing.expect(!f.reply);
    try testing.expectEqual(@as(usize, WINDOW), f.bytes.len);
    try testing.expect(hs.done);

    var late_buf: [CARRY_MAX + 16]u8 = undefined;
    const late = "\x1b[c";
    @memcpy(late_buf[CARRY_MAX..][0..late.len], late);
    const r = hs.filter(&late_buf, late.len);
    try testing.expect(!r.reply);
    try testing.expectEqualStrings("\x1b[c", r.bytes);
}

test "conpty handshake: an inbox conhost that never asks passes through" {
    var hs: Handshake = .{};
    var buf: [CARRY_MAX + 16]u8 = undefined;
    const out = "hello\r\n";
    @memcpy(buf[CARRY_MAX..][0..out.len], out);
    const r = hs.filter(&buf, out.len);
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
    var head_buf: [CARRY_MAX + 16]u8 = undefined;
    const head = "\x1b[1t\x1b[";
    @memcpy(head_buf[CARRY_MAX..][0..head.len], head);
    const first = hs.filter(&head_buf, head.len);
    try testing.expect(!first.reply);
    // The cut-off introducer does not go to the parser at all: a CSI state
    // with nothing after it would eat the next chunk's first byte as its
    // final unless that byte opened with an ESC. The handshake holds it,
    // and the request leaves the stream whole below.
    try testing.expectEqualStrings("\x1b[1t", first.bytes);

    var tail_buf: [CARRY_MAX + 16]u8 = undefined;
    const tail = "c\x1b[?1004h";
    @memcpy(tail_buf[CARRY_MAX..][0..tail.len], tail);
    const second = hs.filter(&tail_buf, tail.len);
    try testing.expect(second.reply);
    // The held introducer is dropped with the byte that completed it; the
    // parser sees only what follows the request.
    try testing.expectEqualStrings("\x1b[?1004h", second.bytes);
    try testing.expect(hs.done);
}

test "conpty handshake: a request split one byte early is answered too" {
    // The other seam: the chunk ends after the ESC and the next one opens with
    // the bracket, so the carry holds one byte and two thirds of the request
    // are still in this chunk.
    var hs: Handshake = .{};
    var head_buf: [CARRY_MAX + 16]u8 = undefined;
    const head = "\x1b[1t\x1b";
    @memcpy(head_buf[CARRY_MAX..][0..head.len], head);
    const first = hs.filter(&head_buf, head.len);
    try testing.expect(!first.reply);
    try testing.expectEqualStrings("\x1b[1t", first.bytes);

    var tail_buf: [CARRY_MAX + 16]u8 = undefined;
    const tail = "[c\x1b[?1004h";
    @memcpy(tail_buf[CARRY_MAX..][0..tail.len], tail);
    const r = hs.filter(&tail_buf, tail.len);
    try testing.expect(r.reply);
    try testing.expectEqualStrings("\x1b[?1004h", r.bytes);
}

test "conpty handshake: a prefix that never completes is the parser's" {
    // The carry is not a claim: an ESC that no request follows is ordinary
    // output, and it must reach the parser whole rather than be swallowed.
    // Held one read, it comes back at the front of the next result and the
    // ESC that follows it aborts it, so the OSC lands exactly as a parser
    // reading the chunks as they came would have it.
    var hs: Handshake = .{};
    var head_buf: [CARRY_MAX + 16]u8 = undefined;
    const head = "out\x1b";
    @memcpy(head_buf[CARRY_MAX..][0..head.len], head);
    const first = hs.filter(&head_buf, head.len);
    try testing.expect(!first.reply);
    try testing.expectEqualStrings("out", first.bytes);

    var tail_buf: [CARRY_MAX + 16]u8 = undefined;
    const tail = "\x1b]0;title\x07$ ";
    @memcpy(tail_buf[CARRY_MAX..][0..tail.len], tail);
    const second = hs.filter(&tail_buf, tail.len);
    try testing.expect(!second.reply);
    try testing.expectEqualStrings("\x1b\x1b]0;title\x07$ ", second.bytes);
    try testing.expect(!hs.done);
}

test "conpty handshake: a request split at the seam withholds its introducer" {
    // The reader feeds every returned chunk straight to the parser. When the
    // request straddles the seam, the introducer conpty wrote before the
    // break must not ride out with the first chunk: a dangling `ESC [` sits
    // in the parser's CSI state, and the next chunk's first byte is consumed
    // as a sequence final unless that byte happens to be an ESC. A banner's
    // first letter is enough to lose. The introducer is carried instead and
    // only leaves the handshake resolved: dropped with the request it
    // belonged to, or emitted ahead of a later chunk it turns out not to
    // belong to.
    var hs: Handshake = .{};
    var head_buf: [CARRY_MAX + 16]u8 = undefined;
    const head = "\x1b[1t\x1b[";
    @memcpy(head_buf[CARRY_MAX..][0..head.len], head);
    const first = hs.filter(&head_buf, head.len);
    try testing.expect(!first.reply);
    try testing.expectEqualStrings("\x1b[1t", first.bytes);

    var tail_buf: [CARRY_MAX + 16]u8 = undefined;
    const tail = "cPrompt>";
    @memcpy(tail_buf[CARRY_MAX..][0..tail.len], tail);
    const second = hs.filter(&tail_buf, tail.len);
    try testing.expect(second.reply);
    try testing.expectEqualStrings("Prompt>", second.bytes);
    try testing.expect(hs.done);
}

test "conpty handshake: a hold outstanding at the window boundary flushes" {
    // The window bounds where conpty's request can be: its preamble is the
    // pty's first bytes, so a request starting at the boundary is not
    // conpty's. A prefix held out of the window's last scanned byte has no
    // byte left to scan into, so on the next read it flushes as the
    // ordinary output it then is, ahead of that read's own bytes: the
    // parser sees `ESC [ c` whole and answers it once, and the reader never
    // writes a second reply beside it.
    var hs: Handshake = .{};
    var buf: [CARRY_MAX + WINDOW]u8 = undefined;
    @memset(buf[CARRY_MAX..], 'x');
    @memcpy(buf[CARRY_MAX + WINDOW - 2 ..], DA1_REQUEST[0..2]);
    const first = hs.filter(&buf, WINDOW);
    try testing.expect(!first.reply);
    try testing.expectEqual(@as(usize, WINDOW - 2), first.bytes.len);
    try testing.expect(!hs.done);

    var tail_buf: [CARRY_MAX + 16]u8 = undefined;
    const tail = "cX";
    @memcpy(tail_buf[CARRY_MAX..][0..tail.len], tail);
    const second = hs.filter(&tail_buf, tail.len);
    try testing.expect(!second.reply);
    try testing.expectEqualStrings("\x1b[cX", second.bytes);
    try testing.expect(hs.done);

    // And from there every chunk passes through untouched.
    var more_buf: [CARRY_MAX + 16]u8 = undefined;
    const more = "y";
    @memcpy(more_buf[CARRY_MAX..][0..more.len], more);
    const third = hs.filter(&more_buf, more.len);
    try testing.expect(!third.reply);
    try testing.expectEqualStrings("y", third.bytes);
    try testing.expect(hs.done);
}
