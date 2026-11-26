const std = @import("std");
const builtin = @import("builtin");
const Allocator = std.mem.Allocator;
const testing = std.testing;

/// Search for "cmd" in the PATH and return the absolute path. This will
/// always allocate if there is a non-null result. The caller must free the
/// resulting value.
///
/// On Windows, honors PATHEXT when searching for bare command names.
/// If cmd is already a path or has an extension, tries it literally first
/// before attempting PATHEXT extensions.
///
/// Reads PATH and PATHEXT from `environ_map` and hands them to `expandPath`,
/// which owns the walk and all of its parsing.
pub fn expand(
    io: std.Io,
    alloc: Allocator,
    environ_map: *const std.process.Environ.Map,
    cmd: []const u8,
) !?[]u8 {
    // An unset PATH reaches the walk as an empty one, so that an
    // already-a-path command is still returned first.
    return expandPath(
        io,
        alloc,
        environ_map.get("PATH") orelse "",
        environ_map.get("PATHEXT"),
        cmd,
    );
}

/// Same contract as `expand`, but walks the PATH text it is given.
/// `path` is the value of PATH and `pathext` the value of PATHEXT, or null
/// when PATHEXT is unset (Windows then uses the documented default set).
pub fn expandPath(
    io: std.Io,
    alloc: Allocator,
    path: []const u8,
    pathext: ?[]const u8,
    cmd: []const u8,
) !?[]u8 {
    // If the command already contains a path separator, return as-is.
    // POSIX: '/'. Windows additionally accepts '\\' and drive-letter
    // prefixes like 'X:'. Without the Windows extensions a path such
    // as `C:\Windows\System32\cmd.exe` would miss the fast path and
    // get joined onto every PATH dir, always producing null.
    const already_path = std.mem.indexOfScalar(u8, cmd, '/') != null or
        (builtin.target.os.tag == .windows and
            (std.mem.indexOfScalar(u8, cmd, '\\') != null or
                (cmd.len >= 2 and std.ascii.isAlphabetic(cmd[0]) and cmd[1] == ':')));
    if (already_path) {
        return try alloc.dupe(u8, cmd);
    }

    // Parse PATHEXT on Windows. Only bare names (no extension) need it;
    // anything already carrying an extension is tried literally.
    var pathext_list: ?[][]const u8 = null;
    const has_extension = std.mem.indexOfScalar(u8, cmd, '.') != null;
    defer if (pathext_list) |pl| alloc.free(pl);

    if (builtin.target.os.tag == .windows and !has_extension) {
        // Fall back to the documented default set when PATHEXT is unset.
        const pathext_str = pathext orelse ".COM;.EXE;.BAT;.CMD";

        var ext_count: usize = 0;
        var count_it = std.mem.tokenizeScalar(u8, pathext_str, ';');
        while (count_it.next()) |_| ext_count += 1;

        pathext_list = try alloc.alloc([]const u8, ext_count);
        var idx: usize = 0;
        var ext_it = std.mem.tokenizeScalar(u8, pathext_str, ';');
        while (ext_it.next()) |ext| {
            pathext_list.?[idx] = ext;
            idx += 1;
        }
    }

    var path_buf: [std.Io.Dir.max_path_bytes]u8 = undefined;
    var it = std.mem.tokenizeScalar(u8, path, std.fs.path.delimiter);
    var seen_eacces = false;
    while (it.next()) |search_path| {
        // We need enough space in our path buffer to store this
        // candidate plus its null terminator.
        const path_len = search_path.len + cmd.len + 1;
        if (path_buf.len <= path_len) return error.PathTooLong;
        if (try tryPathImpl(io, alloc, search_path, cmd, &path_buf, &seen_eacces)) |result| {
            return result;
        }

        // On Windows, if no extension, try with PATHEXT extensions.
        if (builtin.target.os.tag == .windows and !has_extension and pathext_list != null) {
            for (pathext_list.?) |ext| {
                const combined_len = cmd.len + ext.len;
                if (combined_len > std.Io.Dir.max_path_bytes) return error.PathTooLong;
                var cmd_with_ext: [std.Io.Dir.max_path_bytes]u8 = undefined;
                @memcpy(cmd_with_ext[0..cmd.len], cmd);
                @memcpy(cmd_with_ext[cmd.len..][0..ext.len], ext);
                const cmd_ext_str = cmd_with_ext[0..combined_len];

                if (try tryPathImpl(io, alloc, search_path, cmd_ext_str, &path_buf, &seen_eacces)) |result| {
                    return result;
                }
            }
        }
    }

    if (seen_eacces) return error.AccessDenied;

    return null;
}

/// Helper function to try opening a file at search_path/cmd.
/// Returns the allocated full path on success, null on FileNotFound,
/// tracks AccessDenied in seen_eacces pointer, or error on other failures.
fn tryPathImpl(
    io: std.Io,
    alloc: Allocator,
    search_path: []const u8,
    cmd: []const u8,
    path_buf: *[std.Io.Dir.max_path_bytes]u8,
    seen_eacces: *bool,
) !?[]u8 {
    const path_len = search_path.len + cmd.len + 1;
    // The separator and the NUL both live at or after `search_path.len`, so
    // the write needs `path_len + 1` bytes: `path_buf[path_len] = 0` is one
    // past the joined text. Accepting `len == path_len` wrote that NUL out of
    // bounds, and the PATHEXT loop below calls this without the caller's
    // stricter pre-check, so a long directory plus a long bare name could
    // reach it.
    if (path_buf.len <= path_len) return error.PathTooLong;

    // Copy in the full path
    @memcpy(path_buf[0..search_path.len], search_path);
    path_buf[search_path.len] = std.fs.path.sep;
    @memcpy(path_buf[search_path.len + 1 ..][0..cmd.len], cmd);
    path_buf[path_len] = 0;
    const full_path = path_buf[0..path_len :0];

    // Stat it
    const f = std.Io.Dir.cwd().openFile(
        io,
        full_path,
        .{},
    ) catch |err| switch (err) {
        error.FileNotFound => return null,
        error.AccessDenied => {
            // Accumulate this and return it later so we can try other
            // paths that we have access to.
            seen_eacces.* = true;
            return null;
        },
        // The entry is text the OS will not turn into a name, so nothing can
        // sit behind it: a miss, and the walk goes on.
        error.BadPathName => return null,
        // The entry names a network location that cannot be reached. It holds
        // nothing this walk can read: a miss, and the walk goes on.
        error.NetworkNotFound => return null,
        else => return err,
    };
    defer f.close(io);
    const stat = try f.stat(io);
    if (stat.kind != .directory and isExecutable(stat.permissions)) {
        return try alloc.dupe(u8, full_path);
    }

    return null;
}

fn isExecutable(perms: std.Io.File.Permissions) bool {
    return switch (builtin.target.os.tag) {
        .windows => true,
        else => posix: {
            break :posix switch (std.posix.mode_t) {
                u0 => true,
                else => perms.toMode() & 0o0111 != 0,
            };
        },
    };
}

// `uname -n` is the *nix equivalent of `hostname.exe` on Windows
test "expand: hostname" {
    var environ_map = try testing.environ.createMap(testing.allocator);
    defer environ_map.deinit();
    const executable = if (builtin.target.os.tag == .windows) "hostname.exe" else "uname";
    const path = (try expand(testing.io, testing.allocator, &environ_map, executable)).?;
    defer testing.allocator.free(path);
    try testing.expect(path.len > executable.len);
}

test "expand: does not exist" {
    var environ_map = try testing.environ.createMap(testing.allocator);
    defer environ_map.deinit();
    const path = try expand(testing.io, testing.allocator, &environ_map, "thisreallyprobablydoesntexist123");
    try testing.expect(path == null);
}

test "expand: slash" {
    var environ_map = try testing.environ.createMap(testing.allocator);
    defer environ_map.deinit();
    const path = (try expand(testing.io, testing.allocator, &environ_map, "foo/env")).?;
    defer testing.allocator.free(path);
    try testing.expect(path.len == 7);
}

test "expand: windows backslash passes through" {
    if (builtin.target.os.tag != .windows) return error.SkipZigTest;
    var environ_map = try testing.environ.createMap(testing.allocator);
    defer environ_map.deinit();
    const input = "C:\\Windows\\System32\\cmd.exe";
    const path = (try expand(testing.io, testing.allocator, &environ_map, input)).?;
    defer testing.allocator.free(path);
    try testing.expectEqualStrings(input, path);
}

test "expand: windows drive-letter only passes through" {
    if (builtin.target.os.tag != .windows) return error.SkipZigTest;
    var environ_map = try testing.environ.createMap(testing.allocator);
    defer environ_map.deinit();
    // No separator, but the drive prefix means this is already a
    // path (drive-relative) and expand() must not treat it as a
    // bare name to search PATH for.
    const input = "C:cmd.exe";
    const path = (try expand(testing.io, testing.allocator, &environ_map, input)).?;
    defer testing.allocator.free(path);
    try testing.expectEqualStrings(input, path);
}

test "expand: windows bare cmd.exe resolves on PATH" {
    if (builtin.target.os.tag != .windows) return error.SkipZigTest;
    var environ_map = try testing.environ.createMap(testing.allocator);
    defer environ_map.deinit();
    const path = (try expand(testing.io, testing.allocator, &environ_map, "cmd.exe")).?;
    defer testing.allocator.free(path);
    // System32\cmd.exe lives on the default Windows PATH.
    try testing.expect(std.ascii.endsWithIgnoreCase(path, "cmd.exe"));
    try testing.expect(path.len > "cmd.exe".len);
}

test "expand: windows bare pwsh resolves via PATHEXT" {
    if (builtin.target.os.tag != .windows) return error.SkipZigTest;
    var environ_map = try testing.environ.createMap(testing.allocator);
    defer environ_map.deinit();
    // This test requires pwsh.exe to be on the system PATH.
    // If not found, it returns null rather than erroring.
    const path = try expand(testing.io, testing.allocator, &environ_map, "pwsh");
    if (path) |p| {
        defer testing.allocator.free(p);
        try testing.expect(std.ascii.endsWithIgnoreCase(p, "pwsh.exe") or std.ascii.endsWithIgnoreCase(p, "pwsh.com") or std.ascii.endsWithIgnoreCase(p, "pwsh.bat") or std.ascii.endsWithIgnoreCase(p, "pwsh.cmd"));
        try testing.expect(p.len > "pwsh".len);
    }
}

test "expand: windows name with extension does not PATHEXT hunt" {
    if (builtin.target.os.tag != .windows) return error.SkipZigTest;
    var environ_map = try testing.environ.createMap(testing.allocator);
    defer environ_map.deinit();
    // If we provide a name with an extension (even unknown), it should
    // try that literally, not attempt PATHEXT extensions.
    const path = try expand(testing.io, testing.allocator, &environ_map, "cmd.xyz");
    try testing.expect(path == null);
}

test "expand: windows extension present bypasses PATHEXT" {
    if (builtin.target.os.tag != .windows) return error.SkipZigTest;
    var environ_map = try testing.environ.createMap(testing.allocator);
    defer environ_map.deinit();
    // cmd.exe should resolve literally via the literal-first attempt,
    // not via PATHEXT hunting.
    const path = (try expand(testing.io, testing.allocator, &environ_map, "cmd.exe")).?;
    defer testing.allocator.free(path);
    try testing.expect(std.ascii.endsWithIgnoreCase(path, "cmd.exe"));
}

test "expand: reject path without room for null terminator" {
    var environ_map: std.process.Environ.Map =
        .init(testing.allocator);
    defer environ_map.deinit();

    // The directory, separator and command exactly fill the buffer,
    // leaving no room for the null terminator.
    var search_path: [std.Io.Dir.max_path_bytes - "/x".len]u8 = undefined;
    @memset(&search_path, 'a');
    try environ_map.put("PATH", &search_path);

    try testing.expectError(
        error.PathTooLong,
        expand(testing.io, testing.allocator, &environ_map, "x"),
    );
}

test "tryPathImpl: a full path leaves no room for the NUL" {
    // The join writes a separator and a NUL at or after `search_path.len`, so
    // `search_path.len + cmd.len + 1 == path_buf.len` has no room for the NUL.
    // The caller's own pre-check refuses that shape for the literal candidate,
    // but the Windows PATHEXT loop calls this join without it, so the bound
    // here has to hold on its own. Calling the join directly is what exercises
    // that path: through `expand` the oversized path dies inside std's UTF-16
    // conversion before the bound can decide anything.
    var search_path: [std.Io.Dir.max_path_bytes - 2]u8 = undefined;
    @memset(&search_path, 'a');
    var path_buf: [std.Io.Dir.max_path_bytes]u8 = undefined;
    var seen_eacces = false;
    try testing.expectError(
        error.PathTooLong,
        tryPathImpl(testing.io, testing.allocator, &search_path, "x", &path_buf, &seen_eacces),
    );
}

/// A real directory holding a file named `name`, with the absolute path of
/// that directory, for tests that walk a PATH entry by entry.
const Fixture = struct {
    tmp: testing.TmpDir,
    dir_path: [:0]u8,

    fn init(name: []const u8) !Fixture {
        var tmp = testing.tmpDir(.{});
        errdefer tmp.cleanup();
        try tmp.dir.writeFile(testing.io, .{ .sub_path = name, .data = "" });
        const dir_path = try tmp.dir.realPathFileAlloc(testing.io, ".", testing.allocator);
        return .{ .tmp = tmp, .dir_path = dir_path };
    }

    fn deinit(self: *Fixture) void {
        testing.allocator.free(self.dir_path);
        self.tmp.cleanup();
    }
};

// Two entries the OS refuses to open, each with a different error from
// `openFile`. Each shape below runs once per entry as its own test, so that
// one entry failing cannot hide what the other did.

/// Control characters make the OS reject the name as text.
const bad_name_entry = "\x01\x02";

/// A share on a host that does not exist.
const offline_share_entry = "\\\\host-does-not-exist-1477\\share";

/// `<entry>;<dir>`: the refused entry first, a real directory after it.
fn pathAfter(entry: []const u8, dir: []const u8) ![]u8 {
    return std.fmt.allocPrint(testing.allocator, "{s};{s}", .{ entry, dir });
}

/// The walk passes over `entry` and finds the command in the next entry.
fn expectWalkContinues(entry: []const u8) !void {
    var fx = try Fixture.init("cmd-1477.exe");
    defer fx.deinit();
    const path_value = try pathAfter(entry, fx.dir_path);
    defer testing.allocator.free(path_value);

    const found = (try expandPath(testing.io, testing.allocator, path_value, null, "cmd-1477.exe")).?;
    defer testing.allocator.free(found);

    try testing.expect(std.mem.startsWith(u8, found, fx.dir_path));
    try testing.expect(std.mem.endsWith(u8, found, "cmd-1477.exe"));
}

/// The walk passes over `entry`, finds nothing in the next entry, and ends
/// in a clean miss rather than the entry's error.
fn expectCleanMissAfter(entry: []const u8) !void {
    var fx = try Fixture.init("other-1477.exe");
    defer fx.deinit();
    const path_value = try pathAfter(entry, fx.dir_path);
    defer testing.allocator.free(path_value);

    try testing.expectEqual(
        @as(?[]u8, null),
        try expandPath(testing.io, testing.allocator, path_value, null, "cmd-1477.exe"),
    );
}

/// A PATH that holds only `entry` ends in a clean miss.
fn expectCleanMissOnly(entry: []const u8) !void {
    try testing.expectEqual(
        @as(?[]u8, null),
        try expandPath(testing.io, testing.allocator, entry, null, "cmd-1477.exe"),
    );
}

test "expandPath: the walk continues past a BadPathName entry" {
    try expectWalkContinues(bad_name_entry);
}

test "expandPath: a BadPathName entry does not turn a miss into an error" {
    try expectCleanMissAfter(bad_name_entry);
    try expectCleanMissOnly(bad_name_entry);
}

test "expandPath: the walk continues past a NetworkNotFound entry" {
    try expectWalkContinues(offline_share_entry);
}

test "expandPath: a NetworkNotFound entry does not turn a miss into an error" {
    try expectCleanMissAfter(offline_share_entry);
    try expectCleanMissOnly(offline_share_entry);
}

/// A directory holding a file that exists and that the process may not open,
/// with no ACL of the test's own to change or restore: the registry hive is
/// readable only with privileges a test run does not have.
const denied_dir = "C:\\Windows\\System32\\config";
const denied_name = "SAM";

/// Skips unless `denied_dir` really denies this process, so the tests below
/// pin the denial memory only where there is a denial to remember.
fn requireDeniedFile() !void {
    if (builtin.target.os.tag != .windows) return error.SkipZigTest;
    const probe = std.fmt.allocPrint(testing.allocator, "{s}\\{s}", .{ denied_dir, denied_name }) catch return error.SkipZigTest;
    defer testing.allocator.free(probe);
    if (std.Io.Dir.cwd().openFile(testing.io, probe, .{})) |f| {
        f.close(testing.io);
        return error.SkipZigTest;
    } else |err| {
        if (err != error.AccessDenied) return error.SkipZigTest;
    }
}

test "expandPath: a command found behind a denied entry is still returned" {
    try requireDeniedFile();
    var fx = try Fixture.init(denied_name);
    defer fx.deinit();
    const path_value = try pathAfter(denied_dir, fx.dir_path);
    defer testing.allocator.free(path_value);

    const found = (try expandPath(testing.io, testing.allocator, path_value, null, denied_name)).?;
    defer testing.allocator.free(found);

    try testing.expect(std.mem.startsWith(u8, found, fx.dir_path));
    try testing.expect(std.mem.endsWith(u8, found, denied_name));
}

test "expandPath: denied everywhere ends in AccessDenied" {
    try requireDeniedFile();
    try testing.expectError(
        error.AccessDenied,
        expandPath(testing.io, testing.allocator, denied_dir, null, denied_name),
    );
}

test "expandPath: a PATH too long to join is still a hard failure" {
    // The literal candidate is refused by the walk's own pre-check, before
    // any entry is opened, so this pins that the pre-check still returns the
    // error instead of becoming a miss.
    var long_entry: [std.Io.Dir.max_path_bytes]u8 = undefined;
    @memset(&long_entry, 'a');
    const path_value = try std.fmt.allocPrint(testing.allocator, "{s};.", .{&long_entry});
    defer testing.allocator.free(path_value);

    try testing.expectError(
        error.PathTooLong,
        expandPath(testing.io, testing.allocator, path_value, null, "x"),
    );
}
