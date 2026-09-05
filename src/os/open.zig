const std = @import("std");
const builtin = @import("builtin");
const Allocator = std.mem.Allocator;
const build_config = @import("../build_config.zig");
const apprt = @import("../apprt.zig");
const global = @import("../global.zig");

const log = std.log.scoped(.@"os-open");

/// Schemes this opener is willing to hand off to the OS. This is the last
/// line of defense before an OSC 8 hyperlink target or a regex-matched
/// path/URL reaches `rundll32`, `open`, or `xdg-open`: without it, a link
/// whose visible text looks like `https://...` but whose target is a local
/// executable, UNC path, or `.desktop`/`.app` bundle would launch on click.
///
/// Keep this in sync with the scheme alternation in src/config/url.zig.
const allowed_schemes = [_][]const u8{
    "http",
    "https",
    "mailto",
    "ftp",
    "ftps",
    "ssh",
    "sftp",
    "tel",
    "news",
    "gemini",
    "irc",
    "ircs",
};

/// Returns true if `url` is safe to pass to the default opener.
///
/// `allow_file` permits the `file:` scheme and should only be set when the
/// caller can guarantee the URL is exactly the text the user saw (e.g. a
/// path matched by the terminal's link regex). It must stay false for OSC 8
/// hyperlinks, since their target can differ arbitrarily from the visible
/// text that was clicked.
pub fn isSchemeAllowed(url: []const u8, allow_file: bool) bool {
    // The URL becomes an argv element to an external process. A leading
    // dash could be misread as a flag by that process, so refuse it
    // outright regardless of what follows.
    if (url.len == 0 or url[0] == '-') return false;

    const colon = std.mem.indexOfScalar(u8, url, ':') orelse return false;
    const scheme = url[0..colon];
    if (!isValidSchemeSyntax(scheme)) return false;

    if (allow_file and std.ascii.eqlIgnoreCase(scheme, "file")) return true;

    for (allowed_schemes) |allowed| {
        if (std.ascii.eqlIgnoreCase(scheme, allowed)) return true;
    }

    return false;
}

/// RFC 3986 scheme syntax: ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )
fn isValidSchemeSyntax(scheme: []const u8) bool {
    if (scheme.len == 0 or !std.ascii.isAlphabetic(scheme[0])) return false;
    for (scheme[1..]) |c| {
        if (!std.ascii.isAlphanumeric(c) and c != '+' and c != '-' and c != '.') {
            return false;
        }
    }
    return true;
}

test "url scheme allow-list accepts the mirrored scheme list" {
    for (allowed_schemes) |scheme| {
        var buf: [64]u8 = undefined;
        const url = try std.fmt.bufPrint(&buf, "{s}:example", .{scheme});
        try std.testing.expect(isSchemeAllowed(url, false));
    }
}

test "url scheme allow-list accepts https" {
    try std.testing.expect(isSchemeAllowed("https://example.com", false));
}

test "url scheme allow-list rejects file by default" {
    try std.testing.expect(!isSchemeAllowed("file:///Applications/Calc.app", false));
}

test "url scheme allow-list allows file when the caller opts in" {
    try std.testing.expect(isSchemeAllowed("file:///Applications/Calc.app", true));
}

test "url scheme allow-list rejects javascript" {
    try std.testing.expect(!isSchemeAllowed("javascript:alert(1)", false));
    try std.testing.expect(!isSchemeAllowed("javascript:alert(1)", true));
}

test "url scheme allow-list rejects a leading dash" {
    try std.testing.expect(!isSchemeAllowed("-rf", false));
    try std.testing.expect(!isSchemeAllowed("--help=file:///etc/passwd", true));
}

test "url scheme allow-list rejects a scheme-less string" {
    try std.testing.expect(!isSchemeAllowed("not a url", false));
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
        .linux, .freebsd => .{ .argv = &.{ "xdg-open", "--", url } },
        .windows => .{ .argv = &.{ "rundll32", "url.dll,FileProtocolHandler", url } },
        .macos => switch (kind) {
            .text => .{ .argv = &.{ "open", "-t", "--", url } },
            .html, .unknown => .{ .argv = &.{ "open", "--", url } },
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
