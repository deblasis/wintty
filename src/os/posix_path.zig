//! Translate POSIX paths reported by Windows POSIX-emulation shells (WSL,
//! MSYS2/MinGW/Git-Bash, Cygwin) via OSC 7 into the equivalent Windows paths.
const std = @import("std");
const Allocator = std.mem.Allocator;

pub const Error = error{ UnknownDistro, UnknownRoot, InvalidPath } || Allocator.Error;

/// Translate a POSIX path reported by a WSL shell into the equivalent Windows path.
///
///   /mnt/<d>[/rest]  -> <D>:\rest                          (drive-letter form)
///   /<rest>          -> \\wsl.localhost\<distro>\<rest>     (UNC form)
///
/// `distro` is the real WSL distribution name (e.g. "Ubuntu-24.04"). Pass null for a
/// default-distro session whose name is unknown: a `/mnt/*` path still translates, but a
/// non-`/mnt` path yields error.UnknownDistro (caller leaves pwd unset). Caller owns the
/// returned slice.
pub fn wslToWindows(
    alloc: Allocator,
    posix_path: []const u8,
    distro: ?[]const u8,
) Error![]u8 {
    // Only absolute POSIX paths are translatable.
    if (posix_path.len == 0 or posix_path[0] != '/') return error.InvalidPath;

    // /mnt/<drive>[/rest] -> drive-letter form (needs no distro).
    if (driveAfter(posix_path, "/mnt/")) |m| return driveForm(alloc, m);

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

/// Translate a POSIX path reported by a MSYS2/MinGW/Git-Bash or Cygwin shell
/// into the equivalent Windows path.
///
///   /cygdrive/<d>[/rest]  -> <D>:\rest          (Cygwin drive mount)
///   /<d>[/rest]           -> <D>:\rest          (MSYS2/Git default automount)
///   /<rest>               -> <install_root>\<rest>
///
/// `install_root` is an already-Windows path with no trailing separator
/// (e.g. "C:\msys64"). Pass null when it could not be derived: drive-form paths
/// still translate, but a root-relative path yields error.UnknownRoot (caller
/// leaves pwd unset). Caller owns the returned slice.
pub fn rootedToWindows(
    alloc: Allocator,
    posix_path: []const u8,
    install_root: ?[]const u8,
) Error![]u8 {
    if (posix_path.len == 0 or posix_path[0] != '/') return error.InvalidPath;

    // Cygwin's distinctive mount prefix (checked first — it is longer and would
    // otherwise be mis-read as a root-relative `/cygdrive` directory).
    if (driveAfter(posix_path, "/cygdrive/")) |m| return driveForm(alloc, m);
    // MSYS2/Git default automount: a single-letter top-level segment is a drive.
    if (driveAfter(posix_path, "/")) |m| return driveForm(alloc, m);

    // Everything else lives under the install root.
    const root = install_root orelse return error.UnknownRoot;

    var buf: std.ArrayListUnmanaged(u8) = .{};
    errdefer buf.deinit(alloc);
    try buf.appendSlice(alloc, root);
    try buf.append(alloc, '\\');
    try appendBackslashed(alloc, &buf, posix_path[1..]);
    return buf.toOwnedSlice(alloc);
}

const Mount = struct { drive: u8, rest: []const u8 };

/// Match `<prefix><drive>(/...)?` where `<drive>` is a single ASCII letter and
/// the char after it is `/` or end-of-string. `rest` has no leading slash and
/// may be empty. Returns null when the segment after `<prefix>` is not a bare
/// single-letter drive (e.g. `/mnt/wsl`, `/usr`, `/cygdriveX`).
fn driveAfter(path: []const u8, prefix: []const u8) ?Mount {
    if (!std.mem.startsWith(u8, path, prefix)) return null;
    const after = path[prefix.len..];
    if (after.len == 0) return null;
    const drive = after[0];
    if (!std.ascii.isAlphabetic(drive)) return null;
    if (after.len == 1) return .{ .drive = drive, .rest = "" };
    if (after[1] != '/') return null;
    return .{ .drive = drive, .rest = after[2..] };
}

/// Build the drive-letter form `<D>:\<backslashed rest>` from a matched mount.
fn driveForm(alloc: Allocator, m: Mount) Error![]u8 {
    var buf: std.ArrayListUnmanaged(u8) = .{};
    errdefer buf.deinit(alloc);
    try buf.append(alloc, std.ascii.toUpper(m.drive));
    try buf.appendSlice(alloc, ":\\");
    try appendBackslashed(alloc, &buf, m.rest);
    return buf.toOwnedSlice(alloc);
}

/// Append `s` (a `/`-separated POSIX remainder) translating `/` -> `\`.
///
/// A literal backslash in a Linux path (legal but exotic, e.g. `/home/a\b`) is
/// passed through verbatim and so reads as a Windows path separator in the
/// result. We accept this: such paths are vanishingly rare and the only
/// consequence is a slightly-wrong title/cwd, never a crash.
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
    const got = try wslToWindows(std.testing.allocator, posix_path, distro);
    defer std.testing.allocator.free(got);
    try std.testing.expectEqualStrings(expected, got);
}

fn expectRooted(
    expected: []const u8,
    posix_path: []const u8,
    root: ?[]const u8,
) !void {
    const got = try rootedToWindows(std.testing.allocator, posix_path, root);
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
        wslToWindows(std.testing.allocator, "/home/alex", null),
    );
}

test "wsl_path: non-absolute or empty is invalid" {
    try std.testing.expectError(
        error.InvalidPath,
        wslToWindows(std.testing.allocator, "", "Ubuntu"),
    );
    try std.testing.expectError(
        error.InvalidPath,
        wslToWindows(std.testing.allocator, "relative/path", "Ubuntu"),
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
        const got = try wslToWindows(aa, path, c.distro);
        try std.testing.expectEqualStrings(c.want, got);
    }

    // Rooted (MSYS2/Cygwin) form through the same parse chain.
    {
        const u = try uri.parse("kitty-shell-cwd://msys/c/Users/alex", .{
            .mac_address = comptime builtin.os.tag != .macos,
            .raw_path = true,
        });
        const path = try u.path.toRawMaybeAlloc(aa);
        const got = try rootedToWindows(aa, path, "C:\\msys64");
        try std.testing.expectEqualStrings("C:\\Users\\alex", got);
    }
}

test "posix_path: rooted drive forms (MSYS2 /c and Cygwin /cygdrive)" {
    // MSYS2/Git default automount: /<d>/...
    try expectRooted("C:\\Users\\alex", "/c/Users/alex", "C:\\msys64");
    try expectRooted("C:\\", "/c", "C:\\msys64");
    try expectRooted("C:\\", "/c/", "C:\\msys64");
    // Cygwin: /cygdrive/<d>/...
    try expectRooted("D:\\work\\repo", "/cygdrive/d/work/repo", "C:\\cygwin64");
    try expectRooted("D:\\", "/cygdrive/d", "C:\\cygwin64");
    // Drive form needs no root.
    try expectRooted("E:\\x", "/e/x", null);
    try expectRooted("E:\\x", "/cygdrive/e/x", null);
}

test "posix_path: rooted root-relative forms map under install_root" {
    try expectRooted("C:\\msys64\\home\\alex", "/home/alex", "C:\\msys64");
    try expectRooted("C:\\msys64\\usr\\bin", "/usr/bin", "C:\\msys64");
    try expectRooted("C:\\msys64\\", "/", "C:\\msys64");
    try expectRooted("C:\\cygwin64\\etc", "/etc", "C:\\cygwin64");
    // 'cygdriveX' is NOT the Cygwin mount prefix -> root-relative.
    try expectRooted("C:\\msys64\\cygdriveX", "/cygdriveX", "C:\\msys64");
}

test "posix_path: rooted unknown root on root-relative path errors" {
    try std.testing.expectError(
        error.UnknownRoot,
        rootedToWindows(std.testing.allocator, "/home/alex", null),
    );
}

test "posix_path: rooted non-absolute or empty is invalid" {
    try std.testing.expectError(
        error.InvalidPath,
        rootedToWindows(std.testing.allocator, "", "C:\\msys64"),
    );
    try std.testing.expectError(
        error.InvalidPath,
        rootedToWindows(std.testing.allocator, "rel/path", "C:\\msys64"),
    );
}
