const std = @import("std");
const builtin = @import("builtin");
const Allocator = std.mem.Allocator;
const build_config = @import("../build_config.zig");
const apprt = @import("../apprt.zig");
const global = @import("../global.zig");

const log = std.log.scoped(.@"os-open");

/// Schemes this opener is willing to hand off to the OS.
///
/// This mirrors the scheme alternation in `src/config/url.zig` (the
/// `url_schemes` constant) exactly: that regex decides what the terminal
/// offers as a clickable link, so anything outside it can only reach us as
/// an OSC 8 target, which is untrusted terminal output. A scheme added to
/// that alternation must be added here too, or it will be detected as a
/// link and then refused on click.
const allowed_schemes = [_][]const u8{
    "http",
    "https",
    "mailto",
    "ftp",
    "file",
    "ssh",
    "git",
    "tel",
    "magnet",
    "ipfs",
    "ipns",
    "gemini",
    "gopher",
    "news",
};

/// Returns true if `url` is safe to hand to the platform's default opener.
///
/// This is the last line of defense before a link target reaches
/// `rundll32`, `open`, or `xdg-open`, all of which run the default verb and
/// so will execute a local file. Without it, a hyperlink whose visible text
/// looks like `https://...` but whose target is an executable, a UNC path,
/// or a `.desktop`/`.app` bundle launches on click.
///
/// `.unknown` targets come from the link regex and are derived from the
/// text the user saw, so filesystem paths are accepted alongside the scheme
/// list; the regex matches bare paths as well as URLs. An `.osc8` target is
/// chosen by the program that wrote the escape and bears no relation to the
/// text that was clicked, so it is held to the scheme list alone, without
/// `file:`.
pub fn isUrlAllowed(kind: apprt.action.OpenUrl.Kind, url: []const u8) bool {
    switch (kind) {
        // Ghostty builds these targets itself (a config file to edit, a
        // rendered help page), so there is no untrusted input to filter.
        .text, .html => return true,
        .unknown, .osc8 => {},
    }

    // The target becomes an argv element of an external process, so a
    // leading dash could be read as an option by that process. Leading
    // whitespace is refused with it: nothing we detect as a link starts
    // with a space, and allowing it would let " javascript:..." slip past
    // the scheme parse below and be taken for a relative path.
    if (url.len == 0 or url[0] == '-' or std.ascii.isWhitespace(url[0])) return false;

    // Control characters never belong in a URL or in a path we would open,
    // and on Windows nothing else stands between this and the shell's
    // default verb.
    if (hasControlChars(url)) return false;

    return switch (kind) {
        .unknown => hasAllowedScheme(url, .{ .file = true }) or isFilesystemPath(url),
        .osc8 => hasAllowedScheme(url, .{ .file = false }),
        .text, .html => unreachable,
    };
}

/// True if `url` starts with a scheme from `allowed_schemes`. `file:` is
/// gated separately because it is in the link regex but is only safe when
/// the target came from the text the user saw.
fn hasAllowedScheme(url: []const u8, opts: struct { file: bool }) bool {
    const scheme = schemeOf(url) orelse return false;
    for (allowed_schemes) |allowed| {
        if (!opts.file and std.ascii.eqlIgnoreCase(allowed, "file")) continue;
        if (std.ascii.eqlIgnoreCase(scheme, allowed)) {
            if (std.ascii.eqlIgnoreCase(scheme, "file") and !isLocalFileAuthority(url)) {
                return false;
            }
            return true;
        }
    }

    return false;
}

/// True if a `file:` URL's authority is empty or `localhost`
/// (case-insensitively). The authority is the text between `file://` and
/// the next `/` (or the end of the string). A non-local authority is a
/// remote host name that, on Windows, `rundll32` resolves to a UNC path and
/// executes the default verb against it, leaking the current user's NTLM
/// credentials to that host.
fn isLocalFileAuthority(url: []const u8) bool {
    const prefix = "file://";
    if (url.len < prefix.len or !std.ascii.eqlIgnoreCase(url[0..prefix.len], prefix)) {
        // No `//` authority marker (e.g. `file:relative/path`): there is no
        // syntax here for a remote host, so there is nothing to refuse.
        return true;
    }

    const rest = url[prefix.len..];
    const authority_end = std.mem.indexOfScalar(u8, rest, '/') orelse rest.len;
    const authority = rest[0..authority_end];
    return authority.len == 0 or std.ascii.eqlIgnoreCase(authority, "localhost");
}

/// True if `value` looks like a filesystem path rather than a URL. The link
/// regex has three branches and two of them match bare paths, so refusing
/// these would break opening `/etc/hosts`, `./notes.md`, `~/notes.md` or
/// `src/config/url.zig`.
fn isFilesystemPath(value: []const u8) bool {
    // Handles `/etc/hosts` everywhere plus `C:\...` and `\\server\share`
    // when we are running on Windows.
    if (std.fs.path.isAbsolute(value)) return true;

    // A drive-qualified path parses as a one letter scheme, so recognize it
    // regardless of the host we are running on: the same string must be
    // classified the same way by the tests on every platform.
    if (value.len >= 3 and
        std.ascii.isAlphabetic(value[0]) and
        value[1] == ':' and
        (value[2] == '\\' or value[2] == '/')) return true;

    if (std.mem.startsWith(u8, value, "./")) return true;
    if (std.mem.startsWith(u8, value, "../")) return true;
    if (std.mem.startsWith(u8, value, "~/")) return true;

    // Bare relative paths (`src/config/url.zig`) carry no scheme at all.
    // A value that does parse as a scheme is a URL we chose not to allow,
    // not a path, so it must not fall through to here.
    //
    // A malformed scheme (one containing a space, say) also lands here as
    // "no scheme" and gets treated as a path. That is safe only because the
    // caller already refused leading whitespace: the link regex cannot
    // produce a space-bearing match that does not already start with `/`,
    // `./`, `../`, or `~/`, one of the path prefixes handled above.
    return schemeOf(value) == null;
}

/// The scheme of `url`, or null if it does not begin with one. RFC 3986
/// scheme syntax: ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ).
fn schemeOf(url: []const u8) ?[]const u8 {
    const colon = std.mem.indexOfScalar(u8, url, ':') orelse return null;
    const scheme = url[0..colon];
    if (scheme.len == 0 or !std.ascii.isAlphabetic(scheme[0])) return null;
    for (scheme[1..]) |c| {
        if (!std.ascii.isAlphanumeric(c) and c != '+' and c != '-' and c != '.') {
            return null;
        }
    }

    return scheme;
}

/// True if `url` contains a C0 control character, DEL, or a C1 control
/// character. C1 reaches us UTF-8 encoded, as 0xC2 followed by 0x80-0x9F.
fn hasControlChars(url: []const u8) bool {
    for (url, 0..) |c, i| {
        if (std.ascii.isControl(c)) return true;
        if (c == 0xc2 and
            i + 1 < url.len and
            url[i + 1] >= 0x80 and
            url[i + 1] <= 0x9f) return true;
    }

    return false;
}

test "url allow-list accepts every scheme the link regex detects" {
    // Spelled out rather than looped over `allowed_schemes` so that a
    // scheme dropped from the list fails here instead of silently agreeing
    // with itself.
    const testing = std.testing;
    try testing.expect(isUrlAllowed(.osc8, "http://example.com"));
    try testing.expect(isUrlAllowed(.osc8, "https://example.com"));
    try testing.expect(isUrlAllowed(.osc8, "mailto:test@example.com"));
    try testing.expect(isUrlAllowed(.osc8, "ftp://example.com"));
    try testing.expect(isUrlAllowed(.osc8, "ssh://example.com"));
    try testing.expect(isUrlAllowed(.osc8, "git://example.com/repo.git"));
    try testing.expect(isUrlAllowed(.osc8, "tel:+18005551234"));
    try testing.expect(isUrlAllowed(.osc8, "magnet:?xt=urn:btih:1234567890"));
    try testing.expect(isUrlAllowed(.osc8, "ipfs://QmSomeHashValue"));
    try testing.expect(isUrlAllowed(.osc8, "ipns://QmSomeHashValue"));
    try testing.expect(isUrlAllowed(.osc8, "gemini://example.com"));
    try testing.expect(isUrlAllowed(.osc8, "gopher://example.com"));
    try testing.expect(isUrlAllowed(.osc8, "news:comp.infosystems.www.servers.unix"));

    // `file:` is in that alternation too, but only the regex path may open
    // it: an OSC 8 target is not the text the user clicked.
    try testing.expect(isUrlAllowed(.unknown, "file:///tmp/notes.md"));
    try testing.expect(!isUrlAllowed(.osc8, "file:///Applications/Calculator.app"));
}

test "url allow-list matches schemes case-insensitively" {
    const testing = std.testing;
    try testing.expect(isUrlAllowed(.osc8, "HTTPS://example.com"));
    try testing.expect(isUrlAllowed(.unknown, "File:///tmp/notes.md"));
    try testing.expect(!isUrlAllowed(.osc8, "File:///tmp/notes.md"));
}

test "url allow-list refuses file urls with a remote authority" {
    const testing = std.testing;
    try testing.expect(isUrlAllowed(.unknown, "file:///C:/x.txt"));
    try testing.expect(isUrlAllowed(.unknown, "file:///etc/hosts"));
    try testing.expect(isUrlAllowed(.unknown, "file://localhost/x"));
    // A non-empty, non-localhost authority resolves to a UNC path on
    // Windows and runs the default verb against that host.
    try testing.expect(!isUrlAllowed(.unknown, "file://evil/share/a.exe"));
    try testing.expect(!isUrlAllowed(.unknown, "file://evil"));
    // Scheme case-insensitivity must not open a bypass for the authority
    // check.
    try testing.expect(!isUrlAllowed(.unknown, "FILE://EVIL/x"));
}

test "url allow-list rejects schemes that execute or reconfigure" {
    const testing = std.testing;
    for ([_][]const u8{
        "javascript:alert(1)",
        "data:text/html,<script>alert(1)</script>",
        "ms-settings:windowsupdate",
        "shell:startup",
        "vbscript:msgbox(1)",
    }) |url| {
        try testing.expect(!isUrlAllowed(.osc8, url));
        try testing.expect(!isUrlAllowed(.unknown, url));
    }
}

test "url allow-list accepts filesystem paths only from the link regex" {
    const testing = std.testing;
    for ([_][]const u8{
        "/etc/hosts",
        "./notes.md",
        "../notes.md",
        "~/notes.md",
        "C:\\Users\\me\\notes.md",
        "src/config/url.zig",
    }) |path| {
        try testing.expect(isUrlAllowed(.unknown, path));
        try testing.expect(!isUrlAllowed(.osc8, path));
    }
}

test "url allow-list rejects a UNC target from an OSC 8 link" {
    const testing = std.testing;
    try testing.expect(!isUrlAllowed(.osc8, "\\\\attacker\\share\\payload.exe"));
    try testing.expect(!isUrlAllowed(.osc8, "//attacker/share/payload.exe"));
}

test "url allow-list rejects a leading dash" {
    const testing = std.testing;
    try testing.expect(!isUrlAllowed(.unknown, "-rf"));
    try testing.expect(!isUrlAllowed(.unknown, "--help=file:///etc/passwd"));
    try testing.expect(!isUrlAllowed(.osc8, "-rf"));
}

test "url allow-list rejects leading whitespace" {
    const testing = std.testing;
    try testing.expect(!isUrlAllowed(.osc8, " https://example.com"));
    try testing.expect(!isUrlAllowed(.unknown, " https://example.com"));
    // Without the whitespace check this would parse as a scheme-less
    // relative path and be opened.
    try testing.expect(!isUrlAllowed(.unknown, " javascript:alert(1)"));
}

test "url allow-list rejects control characters" {
    const testing = std.testing;
    try testing.expect(!isUrlAllowed(.osc8, "https://example.com/\x00"));
    try testing.expect(!isUrlAllowed(.osc8, "https://example.com/\r\nx"));
    try testing.expect(!isUrlAllowed(.osc8, "https://example.com/\x1b]0;x\x07"));
    try testing.expect(!isUrlAllowed(.osc8, "https://example.com/\x7f"));
    // C1, UTF-8 encoded.
    try testing.expect(!isUrlAllowed(.unknown, "/tmp/notes\xc2\x9b.md"));
    try testing.expect(!isUrlAllowed(.unknown, "/tmp/notes\xc2\x80.md"));
    // 0xC2 followed by a continuation byte outside C1 is ordinary text.
    try testing.expect(isUrlAllowed(.unknown, "/tmp/notes\xc2\xa9.md"));
}

test "url allow-list leaves ghostty's own targets alone" {
    const testing = std.testing;
    try testing.expect(isUrlAllowed(.text, "/home/user/.config/ghostty/config"));
    try testing.expect(isUrlAllowed(.html, "/tmp/ghostty-help.html"));
}

/// Open a URL in the default handling application.
///
/// Any output on stderr is logged as a warning in the application logs.
/// Output on stdout is ignored.
///
/// This function is purposely simple for the sake of providing some portable
/// way to open URLs. If you are implementing an apprt for Ghostty, you should
/// consider doing something special-cased for your platform.
pub fn open(
    kind: apprt.action.OpenUrl.Kind,
    url: []const u8,
) !void {
    // On macOS, the apprt handles OSC 8 targets before this fallback. Ghostty's
    // native apprt applies its allowlist, confirmation, and file safety policy.
    // If a macOS embedder declines the action, fail closed rather than bypassing
    // that policy by handing producer-controlled terminal output to `open`.
    if (comptime builtin.os.tag == .macos) {
        if (kind == .osc8) return error.UnsafeOSC8Link;
    }

    var spawn_opts: std.process.SpawnOptions = switch (builtin.os.tag) {
        .linux, .freebsd => .{ .argv = &.{ "xdg-open", url } },
        .windows => .{ .argv = &.{ "rundll32", "url.dll,FileProtocolHandler", url } },
        .macos => switch (kind) {
            .text => .{ .argv = &.{ "open", "-t", url } },
            .html, .unknown => .{ .argv = &.{ "open", url } },
            .osc8 => unreachable,
        },
        .ios => return error.Unimplemented,
        else => @compileError("unsupported OS"),
    };
    // Ignore anything from stdout. This must be set before spawning the
    // process.
    spawn_opts.stdout = .ignore;
    // Pipe stderr so we can log the stderr from the command. This must be set
    // before spawning the process.
    spawn_opts.stderr = .pipe;

    const exe = if (comptime build_config.snap) local_env: {
        // In the snap on Linux the launcher exports LD_LIBRARY_PATH
        // pointing at the snap's bundled libraries. Leaking this into
        // child process can can be problematic, so let's drop it from the
        // env.
        //
        // Note that `spawn` copies the passed in `Environ.Map` into a
        // fresh `Environ` block, so this is safe to release immediately
        // after spawn.
        var environ_map = try global.environMap();
        defer environ_map.deinit();
        _ = environ_map.orderedRemove("LD_LIBRARY_PATH");
        spawn_opts.environ_map = &environ_map;
        break :local_env try std.process.spawn(global.io(), spawn_opts);
    } else
        // Non-snap releases don't need to alter the env.
        try std.process.spawn(global.io(), spawn_opts);

    const thread = try std.Thread.spawn(.{}, openThread, .{ global.io(), exe });
    thread.detach();
}

test "macOS OSC 8 links have no generic opener fallback" {
    if (builtin.os.tag != .macos) return error.SkipZigTest;

    try std.testing.expectError(
        error.UnsafeOSC8Link,
        open(.osc8, "file:///tmp/payload.command"),
    );
}

fn openThread(io: std.Io, exe_: std.process.Child) void {
    // Copy the exe so it is non-const. This is necessary because wait()
    // requires a mutable reference and we can't have one as a thread
    // param.
    var exe = exe_;
    if (exe.stderr) |stderr| {
        var buffer: [256]u8 = undefined;
        var stream = stderr.readerStreaming(io, &buffer);
        const reader = &stream.interface;
        while (true) {
            // Read inclusively so the delimiter is consumed:
            // takeDelimiterExclusive leaves the '\n' buffered, so once the
            // child writes a line this loop would receive an empty slice
            // forever, pinning a core and spamming empty warnings.
            const line = reader.takeDelimiterInclusive('\n') catch |outer| switch (outer) {
                error.EndOfStream => break,
                error.ReadFailed => break,
                error.StreamTooLong => reader.take(buffer.len) catch |inner| switch (inner) {
                    error.ReadFailed => break,
                    error.EndOfStream => break,
                },
            };
            log.warn("open stderr={s}", .{std.mem.trimEnd(u8, line, "\n")});
        }
    }
    _ = exe.wait(io) catch {};
}
