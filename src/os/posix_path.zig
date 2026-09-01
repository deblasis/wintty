//! Translate POSIX paths reported by Windows POSIX-emulation shells (WSL,
//! MSYS2/MinGW/Git-Bash, Cygwin) via OSC 7 into the equivalent Windows paths,
//! and answer the shape questions a reported path raises before it is adopted:
//! whether it is a raw path or a URL, and which machine it names.
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

    var buf: std.ArrayListUnmanaged(u8) = .empty;
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
    // This shadows a hypothetical single-letter root-relative directory (e.g. a
    // literal `/c` dir), but stock MSYS2/Git/Cygwin layouts have none, so the
    // automount reading is correct in practice.
    if (driveAfter(posix_path, "/")) |m| return driveForm(alloc, m);

    // Everything else lives under the install root.
    const root = install_root orelse return error.UnknownRoot;

    var buf: std.ArrayListUnmanaged(u8) = .empty;
    errdefer buf.deinit(alloc);
    try buf.appendSlice(alloc, root);
    try buf.append(alloc, '\\');
    try appendBackslashed(alloc, &buf, posix_path[1..]);
    return buf.toOwnedSlice(alloc);
}

/// Translate a Windows path into its WSL automount form (the inverse direction
/// of `wslToWindows`'s `/mnt/` case):
///
///   C:\Users\x[\rest]  -> /mnt/c/Users/x/rest
///
/// Lowercases the drive letter, converts `\` -> `/`, and strips a leading
/// `\\?\` extended-length prefix. Returns null for any path that is not
/// drive-rooted (UNC such as `\\server\share` or `\\wsl.localhost\...`, or a
/// relative path): such a path has no `/mnt` automount equivalent reachable
/// from inside the distro, so the caller skips integration rather than emit a
/// confidently-wrong path. Caller owns the returned slice.
pub fn windowsToWsl(alloc: Allocator, win_path: []const u8) Allocator.Error!?[]u8 {
    // Strip an extended-length prefix (`\\?\C:\...`) so the drive is detectable.
    // `\\?\UNC\...` becomes `UNC\...` which fails the drive check below (correct:
    // UNC has no /mnt form).
    const p = if (std.mem.startsWith(u8, win_path, "\\\\?\\"))
        win_path["\\\\?\\".len..]
    else
        win_path;

    // Require a `<letter>:` drive root; UNC and relative paths have no /mnt form.
    if (p.len < 2 or p[1] != ':' or !std.ascii.isAlphabetic(p[0])) return null;

    const rest = p[2..]; // keeps the leading separator if any
    var buf: std.ArrayListUnmanaged(u8) = .empty;
    errdefer buf.deinit(alloc);
    try buf.appendSlice(alloc, "/mnt/");
    try buf.append(alloc, std.ascii.toLower(p[0]));
    for (rest) |c| try buf.append(alloc, if (c == '\\') '/' else c);
    return try buf.toOwnedSlice(alloc);
}

/// Whether `path` is an absolute Windows path in a form no URI can be mistaken
/// for: a drive root (`C:\`, `c:/`) or a UNC root (`\\server\share`). This is
/// the shape a Windows-native shell reports its cwd in -- cmd's `PROMPT $p` and
/// the PowerShell integration's OSC 9;9 both emit it raw, because cmd has no way
/// to build a URI at all. A `file:`/`kitty-shell-cwd:` URL never matches: its
/// scheme is longer than one character.
///
/// This answers shape only: raw path or URL. It says nothing about whether the
/// path is one we may adopt -- a UNC path names a host, and `pathHost` is what
/// asks which.
pub fn isWindowsAbsolute(path: []const u8) bool {
    if (std.mem.startsWith(u8, path, "\\\\")) return true;
    if (path.len < 3) return false;
    if (!std.ascii.isAlphabetic(path[0]) or path[1] != ':') return false;
    return path[2] == '\\' or path[2] == '/';
}

/// Which machine a raw Windows path names.
pub const PathHost = union(enum) {
    /// Drive-rooted. Names this machine, implicitly and unforgeably.
    local,

    /// UNC. Names this server, which Windows contacts -- and authenticates
    /// to -- the moment anything resolves the path. The caller decides
    /// whether that server may be contacted.
    server: []const u8,
};

/// The machine `path` names, for a path `isWindowsAbsolute` accepted.
///
/// This exists because a reported cwd becomes a *spawn* directory: Duplicate
/// Tab, Reopen Closed Tab and session restore all hand it to CreateProcess. A
/// UNC directory therefore makes Windows open an SMB connection to whatever
/// server the path names, and authenticate to it -- so a terminal that adopts
/// a UNC cwd on the strength of bytes alone lets anything that can write to
/// the pty choose who receives the user's credentials. OSC 7 has always
/// guarded this by validating its URL's hostname; a raw path carries its host
/// in the `\\server\` prefix instead, and this is where it is recovered so the
/// same rule can be applied to both.
///
/// `error.InvalidPath` for a `\\` form that names no directory we will place:
/// the device namespace (`\\.\COM1`), a bare `\\`, or an extended-length
/// prefix (`\\?\`) introducing neither `UNC\` nor a drive. Extended-length is
/// parsed rather than waved through precisely because `\\?\UNC\server\share`
/// is a second spelling of the same reach, and a check that missed it would
/// name the hole it was closing.
///
/// What this does NOT see: a drive letter mapped to a network share. `Z:\` is
/// `.local` here and can still resolve to another machine. That is a weaker
/// vector -- the mapping is one the user made, to a server they already
/// authenticated to, and a reported `Z:\` reaches nothing unless such a
/// mapping exists -- and finding out would mean a per-drive query on a path
/// that arrives on every prompt. The rule enforced here is about paths that
/// NAME a host, not about every path that can reach one.
pub fn pathHost(path: []const u8) error{InvalidPath}!PathHost {
    if (!std.mem.startsWith(u8, path, "\\\\")) return .local;
    const rest = path[2..];

    // `\\?\` (extended-length) and `\\.\` (device) share a shape and a
    // meaning: what follows is not a server name.
    if (rest.len >= 2 and (rest[0] == '?' or rest[0] == '.') and rest[1] == '\\') {
        const tail = rest[2..];
        if (std.ascii.startsWithIgnoreCase(tail, "UNC\\")) return hostOf(tail[4..]);
        // `\\?\C:\dir` is the long-path spelling of a drive root.
        if (tail.len >= 2 and std.ascii.isAlphabetic(tail[0]) and tail[1] == ':') return .local;
        return error.InvalidPath;
    }

    return hostOf(rest);
}

/// The server name at the head of `s`, where `s` is a UNC path with its `\\`
/// (or `\\?\UNC\`) prefix already removed.
fn hostOf(s: []const u8) error{InvalidPath}!PathHost {
    const end = std.mem.indexOfAny(u8, s, "\\/") orelse s.len;
    if (end == 0) return error.InvalidPath;
    return .{ .server = s[0..end] };
}

/// Whether `host` is a Windows share host that resolves without leaving this
/// machine, and so can never carry a credential off it.
///
/// `wsl.localhost` and its legacy spelling `wsl$` are served by the local WSL
/// service, not by SMB over the wire. `localhost` and the loopback literals
/// reach this machine's own SMB server. The real computer name is local too,
/// but only `hostname.isLocal` can know it; callers compose the two.
///
/// Windows host names are case-insensitive, so this compare is too.
pub fn isLocalShareHost(host: []const u8) bool {
    for ([_][]const u8{
        "wsl.localhost",
        "wsl$",
        "localhost",
        "127.0.0.1",
        "::1",
    }) |local| {
        if (std.ascii.eqlIgnoreCase(host, local)) return true;
    }
    return false;
}

/// Translate the path component of an OSC 7 `file://` URL reported by a
/// Windows-native shell into a plain Windows path:
///
///   /c:/Users/alex  -> c:\Users\alex
///
/// The leading slash is the URI's own path root, not part of the path. Anything
/// that is not drive-rooted once it is gone yields error.InvalidPath: a UNC
/// share arrives here as `//server/share`, which the shells deliberately report
/// via OSC 9;9 instead, and guessing at it would produce a confidently-wrong
/// cwd. Caller owns the returned slice.
pub fn uriPathToWindows(alloc: Allocator, uri_path: []const u8) Error![]u8 {
    const p = if (uri_path.len > 0 and uri_path[0] == '/') uri_path[1..] else uri_path;
    if (p.len < 2 or p[1] != ':' or !std.ascii.isAlphabetic(p[0])) return error.InvalidPath;

    var buf: std.ArrayListUnmanaged(u8) = .empty;
    errdefer buf.deinit(alloc);
    try buf.appendSlice(alloc, p[0..2]);
    try appendBackslashed(alloc, &buf, p[2..]);
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
    var buf: std.ArrayListUnmanaged(u8) = .empty;
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

test "posix_path: wsl /mnt drive forms" {
    try expectTranslate("C:\\Users\\alex", "/mnt/c/Users/alex", "Ubuntu");
    try expectTranslate("C:\\", "/mnt/c", "Ubuntu");
    try expectTranslate("C:\\", "/mnt/c/", "Ubuntu");
    try expectTranslate("D:\\work\\repo", "/mnt/d/work/repo", null); // no distro needed
    // Uppercase drive letter regardless of POSIX casing.
    try expectTranslate("C:\\src", "/mnt/c/src", "Ubuntu");
}

test "posix_path: wsl UNC distro forms" {
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

test "posix_path: wsl unknown distro on non-/mnt path errors" {
    try std.testing.expectError(
        error.UnknownDistro,
        wslToWindows(std.testing.allocator, "/home/alex", null),
    );
}

test "posix_path: wsl non-absolute or empty is invalid" {
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
test "posix_path: OSC 7 URL forms extract and translate (reportPwd seam)" {
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

test "posix_path: isWindowsAbsolute separates raw paths from URLs" {
    // What cmd's `PROMPT $p` and the PowerShell integration's OSC 9;9 emit.
    try std.testing.expect(isWindowsAbsolute("C:\\Users\\alex"));
    try std.testing.expect(isWindowsAbsolute("c:/Users/alex"));
    try std.testing.expect(isWindowsAbsolute("C:\\"));
    try std.testing.expect(isWindowsAbsolute("\\\\server\\share"));
    try std.testing.expect(isWindowsAbsolute("\\\\wsl.localhost\\Ubuntu\\home"));

    // Every URL form the OSC 7 shells emit stays on the URI path.
    try std.testing.expect(!isWindowsAbsolute("file://host/c:/Users/alex"));
    try std.testing.expect(!isWindowsAbsolute("kitty-shell-cwd://wsl/home/alex"));
    try std.testing.expect(!isWindowsAbsolute(""));
    try std.testing.expect(!isWindowsAbsolute("C:"));
    try std.testing.expect(!isWindowsAbsolute("relative\\path"));
    // A drive-relative path names no directory on its own.
    try std.testing.expect(!isWindowsAbsolute("C:Users"));
}

test "posix_path: pathHost recovers the server a raw path names" {
    // Drive roots name this machine and nothing else.
    try expectLocal("C:\\Users\\alex");
    try expectLocal("c:/Users/alex");
    // ...including in their extended-length spelling.
    try expectLocal("\\\\?\\C:\\Users\\alex");

    try expectServer("server", "\\\\server\\share");
    try expectServer("server", "\\\\server\\share\\deep\\dir");
    try expectServer("server", "\\\\server");
    try expectServer("wsl.localhost", "\\\\wsl.localhost\\Ubuntu\\home\\alex");
    // The second spelling of the same reach. Missing it would leave the hole
    // this check exists to close.
    try expectServer("evil.example.com", "\\\\?\\UNC\\evil.example.com\\share");
    try expectServer("evil.example.com", "\\\\?\\unc\\evil.example.com\\share");

    // Shapes that name no directory we will place.
    try std.testing.expectError(error.InvalidPath, pathHost("\\\\"));
    try std.testing.expectError(error.InvalidPath, pathHost("\\\\\\share"));
    // The device namespace is not a directory.
    try std.testing.expectError(error.InvalidPath, pathHost("\\\\.\\COM1"));
    try std.testing.expectError(error.InvalidPath, pathHost("\\\\?\\GLOBALROOT\\Device\\X"));
}

test "posix_path: isLocalShareHost admits only hosts that never reach the wire" {
    try std.testing.expect(isLocalShareHost("wsl.localhost"));
    try std.testing.expect(isLocalShareHost("WSL.localhost"));
    try std.testing.expect(isLocalShareHost("wsl$"));
    try std.testing.expect(isLocalShareHost("localhost"));
    try std.testing.expect(isLocalShareHost("127.0.0.1"));

    try std.testing.expect(!isLocalShareHost("evil.example.com"));
    try std.testing.expect(!isLocalShareHost("fileserver"));
    try std.testing.expect(!isLocalShareHost(""));
    // A prefix of a local name is not a local name.
    try std.testing.expect(!isLocalShareHost("wsl.localhost.evil.example.com"));
}

fn expectLocal(path: []const u8) !void {
    switch (try pathHost(path)) {
        .local => {},
        .server => return error.TestExpectedLocal,
    }
}

fn expectServer(expected: []const u8, path: []const u8) !void {
    switch (try pathHost(path)) {
        .local => return error.TestExpectedServer,
        .server => |host| try std.testing.expectEqualStrings(expected, host),
    }
}

// The native-shell twin of the reportPwd seam test above: the PowerShell
// integration's OSC 7 URL through the same parse chain, then out the
// no-translation arm.
test "posix_path: OSC 7 file:// from a native Windows shell (reportPwd seam)" {
    const builtin = @import("builtin");
    const uri = @import("uri.zig");

    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const aa = arena.allocator();

    const u = try uri.parse("file://MYPC/c:/Users/alex", .{
        .mac_address = comptime builtin.os.tag != .macos,
        .raw_path = false,
    });
    const path = try u.path.toRawMaybeAlloc(aa);
    const got = try uriPathToWindows(aa, path);
    try std.testing.expectEqualStrings("c:\\Users\\alex", got);
}

test "posix_path: uriPathToWindows rejects what it cannot place" {
    try expectUriPath("c:\\Users\\alex", "/c:/Users/alex");
    try expectUriPath("D:\\", "/D:/");
    // UNC arrives as `//server/share`; OSC 9;9 carries that form instead.
    try std.testing.expectError(
        error.InvalidPath,
        uriPathToWindows(std.testing.allocator, "//server/share"),
    );
    try std.testing.expectError(
        error.InvalidPath,
        uriPathToWindows(std.testing.allocator, "/home/alex"),
    );
    try std.testing.expectError(
        error.InvalidPath,
        uriPathToWindows(std.testing.allocator, ""),
    );
}

fn expectUriPath(expected: []const u8, uri_path: []const u8) !void {
    const got = try uriPathToWindows(std.testing.allocator, uri_path);
    defer std.testing.allocator.free(got);
    try std.testing.expectEqualStrings(expected, got);
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

fn expectWinToWsl(expected: ?[]const u8, win_path: []const u8) !void {
    const got = try windowsToWsl(std.testing.allocator, win_path);
    defer if (got) |g| std.testing.allocator.free(g);
    if (expected) |e| {
        try std.testing.expectEqualStrings(e, got orelse return error.UnexpectedNull);
    } else {
        try std.testing.expect(got == null);
    }
}

test "posix_path: windowsToWsl drive-rooted" {
    try expectWinToWsl("/mnt/c/Users/x/share/ghostty", "C:\\Users\\x\\share\\ghostty");
}

test "posix_path: windowsToWsl lowercases drive" {
    try expectWinToWsl("/mnt/d/Foo", "D:\\Foo");
}

test "posix_path: windowsToWsl accepts forward slashes" {
    try expectWinToWsl("/mnt/c/a/b", "C:/a/b");
}

test "posix_path: windowsToWsl strips extended-length prefix" {
    try expectWinToWsl("/mnt/c/Users/x", "\\\\?\\C:\\Users\\x");
}

test "posix_path: windowsToWsl rejects UNC" {
    try expectWinToWsl(null, "\\\\server\\share");
    try expectWinToWsl(null, "\\\\wsl.localhost\\Ubuntu\\home\\a");
    try expectWinToWsl(null, "\\\\?\\UNC\\server\\share");
}

test "posix_path: windowsToWsl rejects relative" {
    try expectWinToWsl(null, "relative\\path");
}

test "posix_path: windowsToWsl bare drive" {
    // A drive root with no remainder maps to `/mnt/<d>` (no trailing slash).
    try expectWinToWsl("/mnt/c", "C:");
}
