//! Install Ghostty's terminfo onto a target reached via a launcher command
//! (ssh remote, WSL distro, ...). The terminfo source is compiled into the
//! binary and encoded at runtime, then piped to the target's stdin where a
//! script compiles it with `tic`. Shared by `cli/ssh.zig` and the Windows
//! WSL spawn path in `termio/Exec.zig`.

const std = @import("std");
const testing = std.testing;
const Allocator = std.mem.Allocator;
const ghostty_terminfo = @import("main.zig").ghostty;
const global = @import("../global.zig");

const log = std.log.scoped(.terminfo_install);

pub const Error = error{InstallFailed} || Allocator.Error;

/// The remote install script, read by the target shell. The terminfo source
/// arrives on stdin. The script bails (exit 1) when `tic` is unavailable,
/// otherwise compiles the source into the user's terminfo database. It always
/// recompiles rather than skipping an existing entry so that an updated
/// embedded terminfo replaces stale installs; callers cache successful
/// installs to avoid rerunning it. The `verbose` variant lets the remote
/// `tic` stderr through.
pub fn scriptFor(verbose: bool) []const u8 {
    return if (verbose)
        \\command -v tic >/dev/null 2>&1 || exit 1
        \\mkdir -p ~/.terminfo 2>/dev/null && tic -x - && exit 0
        \\exit 1
    else
        \\command -v tic >/dev/null 2>&1 || exit 1
        \\mkdir -p ~/.terminfo 2>/dev/null && tic -x - 2>/dev/null && exit 0
        \\exit 1
    ;
}

/// Install Ghostty's terminfo onto a target reached via `launcher_prefix`,
/// e.g. `["ssh", ..., dest]` or `["wsl.exe", "-d", distro, "--", "sh", "-c"]`.
/// The encoded terminfo source is piped to the target's stdin; the script
/// appended after `launcher_prefix` compiles it with `tic`.
pub fn install(
    alloc: Allocator,
    launcher_prefix: []const []const u8,
    verbose: bool,
) Error!void {
    var buf: std.Io.Writer.Allocating = .init(alloc);
    defer buf.deinit();
    // The only failure for an Allocating writer is OOM, surfaced as
    // WriteFailed; fold it into our explicit error set.
    ghostty_terminfo.encode(&buf.writer) catch return error.InstallFailed;
    const terminfo = buf.written();

    const argv = try std.mem.concat(alloc, []const u8, &.{
        launcher_prefix,
        &.{scriptFor(verbose)},
    });
    defer alloc.free(argv);

    var child = std.process.spawn(global.io(), .{
        .argv = argv,
        .stdin = .pipe,
        .stdout = .ignore,
        .stderr = if (verbose) .inherit else .ignore,
    }) catch |err| {
        log.warn("terminfo install spawn failed: {}", .{err});
        return error.InstallFailed;
    };

    if (child.stdin) |stdin| {
        stdin.writeStreamingAll(global.io(), terminfo) catch {};
        stdin.close(global.io());
        child.stdin = null;
    }

    const term = child.wait(global.io()) catch |err| {
        log.warn("terminfo install wait failed: {}", .{err});
        return error.InstallFailed;
    };
    switch (term) {
        .exited => |rc| if (rc != 0) return error.InstallFailed,
        else => return error.InstallFailed,
    }
}

test "install: script compiles and encodes ghostty terminfo" {
    const alloc = testing.allocator;

    const script = scriptFor(false);
    try testing.expect(std.mem.indexOf(u8, script, "tic -x -") != null);
    try testing.expect(std.mem.indexOf(u8, script, "command -v tic") != null);

    var buf: std.Io.Writer.Allocating = .init(alloc);
    defer buf.deinit();
    try ghostty_terminfo.encode(&buf.writer);
    try testing.expect(std.mem.indexOf(u8, buf.written(), "xterm-ghostty") != null);
}
