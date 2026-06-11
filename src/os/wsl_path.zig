//! Translate POSIX paths reported by WSL shells (via OSC 7) into Windows paths.
const std = @import("std");
const Allocator = std.mem.Allocator;

pub const Error = error{ UnknownDistro, InvalidPath } || Allocator.Error;

/// Translate a POSIX path reported by a WSL shell into the equivalent Windows path.
///
///   /mnt/<d>[/rest]  -> <D>:\rest                          (drive-letter form)
///   /<rest>          -> \\wsl.localhost\<distro>\<rest>     (UNC form)
///
/// `distro` is the real WSL distribution name (e.g. "Ubuntu-24.04"). Pass null for a
/// default-distro session whose name is unknown: a `/mnt/*` path still translates, but a
/// non-`/mnt` path yields error.UnknownDistro (caller leaves pwd unset). Caller owns the
/// returned slice.
pub fn posixToWindows(
    alloc: Allocator,
    posix_path: []const u8,
    distro: ?[]const u8,
) Error![]u8 {
    // Only absolute POSIX paths are translatable.
    if (posix_path.len == 0 or posix_path[0] != '/') return error.InvalidPath;

    // /mnt/<drive>[/rest] -> drive-letter form (needs no distro).
    if (mountDrive(posix_path)) |m| {
        var buf: std.ArrayListUnmanaged(u8) = .{};
        errdefer buf.deinit(alloc);
        try buf.append(alloc, std.ascii.toUpper(m.drive));
        try buf.appendSlice(alloc, ":\\");
        try appendBackslashed(alloc, &buf, m.rest);
        return buf.toOwnedSlice(alloc);
    }

    // Everything else lives inside the distro filesystem -> UNC form.
    const name = distro orelse return error.UnknownDistro;

    var buf: std.ArrayListUnmanaged(u8) = .{};
    errdefer buf.deinit(alloc);
    try buf.appendSlice(alloc, "\\\\wsl.localhost\\");
    try buf.appendSlice(alloc, name);
    try buf.append(alloc, '\\');
    // posix_path starts with '/'; strip it, backslash the remainder.
    try appendBackslashed(alloc, &buf, posix_path[1..]);
    return buf.toOwnedSlice(alloc);
}

const Mount = struct { drive: u8, rest: []const u8 };

/// Match `/mnt/<drive>(/...)?` where `<drive>` is a single ASCII letter.
/// `rest` has no leading slash and may be empty.
fn mountDrive(path: []const u8) ?Mount {
    const prefix = "/mnt/";
    if (!std.mem.startsWith(u8, path, prefix)) return null;
    const after = path[prefix.len..];
    if (after.len == 0) return null;
    const drive = after[0];
    if (!std.ascii.isAlphabetic(drive)) return null;
    if (after.len == 1) return .{ .drive = drive, .rest = "" };
    if (after[1] != '/') return null; // e.g. /mnt/wsl -> not a drive
    return .{ .drive = drive, .rest = after[2..] };
}

/// Append `s` (a `/`-separated POSIX remainder) translating `/` -> `\`.
fn appendBackslashed(
    alloc: Allocator,
    buf: *std.ArrayListUnmanaged(u8),
    s: []const u8,
) Allocator.Error!void {
    for (s) |c| try buf.append(alloc, if (c == '/') '\\' else c);
}

fn expectTranslate(
    expected: []const u8,
    posix_path: []const u8,
    distro: ?[]const u8,
) !void {
    const got = try posixToWindows(std.testing.allocator, posix_path, distro);
    defer std.testing.allocator.free(got);
    try std.testing.expectEqualStrings(expected, got);
}

test "wsl_path: /mnt drive forms" {
    try expectTranslate("C:\\Users\\alex", "/mnt/c/Users/alex", "Ubuntu");
    try expectTranslate("C:\\", "/mnt/c", "Ubuntu");
    try expectTranslate("C:\\", "/mnt/c/", "Ubuntu");
    try expectTranslate("D:\\work\\repo", "/mnt/d/work/repo", null); // no distro needed
    // Uppercase drive letter regardless of POSIX casing.
    try expectTranslate("C:\\src", "/mnt/c/src", "Ubuntu");
}

test "wsl_path: UNC distro forms" {
    try expectTranslate(
        "\\\\wsl.localhost\\Ubuntu-24.04\\home\\alex",
        "/home/alex",
        "Ubuntu-24.04",
    );
    try expectTranslate("\\\\wsl.localhost\\Debian\\", "/", "Debian");
    // /mnt/wsl is NOT a drive (second segment isn't a single letter) -> UNC.
    try expectTranslate(
        "\\\\wsl.localhost\\Ubuntu\\mnt\\wsl\\foo",
        "/mnt/wsl/foo",
        "Ubuntu",
    );
    // Distro name with dots survives verbatim.
    try expectTranslate(
        "\\\\wsl.localhost\\Ubuntu-22.04\\opt\\x",
        "/opt/x",
        "Ubuntu-22.04",
    );
}

test "wsl_path: unknown distro on non-/mnt path errors" {
    try std.testing.expectError(
        error.UnknownDistro,
        posixToWindows(std.testing.allocator, "/home/alex", null),
    );
}

test "wsl_path: non-absolute or empty is invalid" {
    try std.testing.expectError(
        error.InvalidPath,
        posixToWindows(std.testing.allocator, "", "Ubuntu"),
    );
    try std.testing.expectError(
        error.InvalidPath,
        posixToWindows(std.testing.allocator, "relative/path", "Ubuntu"),
    );
}

// Exercises the exact OSC 7 parse -> path-extract -> translate chain that
// stream_handler.reportPwd runs, for both shell-emitted URL forms. This guards
// the one seam the unit tests above don't reach (URL parsing + fish escaping).
test "wsl_path: OSC 7 URL forms extract and translate (reportPwd seam)" {
    const builtin = @import("builtin");
    const uri = @import("uri.zig");

    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const aa = arena.allocator();

    const Case = struct { url: []const u8, distro: ?[]const u8, want: []const u8 };
    const cases = [_]Case{
        // bash/zsh: \e]7;kitty-shell-cwd://<host><PWD>\a (raw, unescaped path)
        .{
            .url = "kitty-shell-cwd://wsl/home/alex",
            .distro = "Ubuntu-24.04",
            .want = "\\\\wsl.localhost\\Ubuntu-24.04\\home\\alex",
        },
        .{
            .url = "kitty-shell-cwd://wsl/mnt/c/Users/alex",
            .distro = "Ubuntu",
            .want = "C:\\Users\\alex",
        },
        // fish: \e]7;file://<host><url-escaped PWD>\a (percent-decoded path)
        .{
            .url = "file://wsl/home/alex%20space",
            .distro = "Ubuntu",
            .want = "\\\\wsl.localhost\\Ubuntu\\home\\alex space",
        },
    };

    for (cases) |c| {
        const u = try uri.parse(c.url, .{
            .mac_address = comptime builtin.os.tag != .macos,
            .raw_path = std.mem.startsWith(u8, c.url, "kitty-shell-cwd://"),
        });
        const path = try u.path.toRawMaybeAlloc(aa);
        const got = try posixToWindows(aa, path, c.distro);
        try std.testing.expectEqualStrings(c.want, got);
    }
}
