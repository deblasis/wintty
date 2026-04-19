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
pub fn expand(alloc: Allocator, cmd: []const u8) !?[]u8 {
    // If the command already contains a path separator, return as-is.
    // POSIX: '/'. Windows additionally accepts '\\' and drive-letter
    // prefixes like 'X:'. Without the Windows extensions a path such
    // as `C:\Windows\System32\cmd.exe` would miss the fast path and
    // get joined onto every PATH dir, always producing null.
    const already_path = std.mem.indexOfScalar(u8, cmd, '/') != null or
        (builtin.os.tag == .windows and
            (std.mem.indexOfScalar(u8, cmd, '\\') != null or
                (cmd.len >= 2 and std.ascii.isAlphabetic(cmd[0]) and cmd[1] == ':')));
    if (already_path) {
        return try alloc.dupe(u8, cmd);
    }

    const PATH = switch (builtin.os.tag) {
        .windows => blk: {
            const win_path = std.process.getenvW(std.unicode.utf8ToUtf16LeStringLiteral("PATH")) orelse return null;
            const path = try std.unicode.utf16LeToUtf8Alloc(alloc, win_path);
            break :blk path;
        },
        else => std.posix.getenvZ("PATH") orelse return null,
    };
    defer if (builtin.os.tag == .windows) alloc.free(PATH);

    // Parse PATHEXT on Windows
    var pathext_list: ?[][]const u8 = null;
    var pathext_buf: ?[]u8 = null;
    const has_extension = std.mem.indexOfScalar(u8, cmd, '.') != null;
    defer {
        if (pathext_buf) |pb| alloc.free(pb);
        if (pathext_list) |pl| alloc.free(pl);
    }

    if (builtin.os.tag == .windows and !has_extension) {
        const pathext_str = blk: {
            if (std.process.getenvW(std.unicode.utf8ToUtf16LeStringLiteral("PATHEXT"))) |we| {
                const utf8_pathext = try std.unicode.utf16LeToUtf8Alloc(alloc, we);
                break :blk utf8_pathext;
            } else {
                // Fallback to default Windows extensions
                break :blk try alloc.dupe(u8, ".COM;.EXE;.BAT;.CMD");
            }
        };
        pathext_buf = pathext_str;

        // Count semicolons to determine how many extensions
        var ext_count: usize = 1;
        for (pathext_str) |ch| {
            if (ch == ';') ext_count += 1;
        }

        // Allocate and populate extension list
        pathext_list = try alloc.alloc([]const u8, ext_count);
        var idx: usize = 0;
        var it = std.mem.tokenizeScalar(u8, pathext_str, ';');
        while (it.next()) |ext| {
            pathext_list.?[idx] = ext;
            idx += 1;
        }
    }

    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    var it = std.mem.tokenizeScalar(u8, PATH, std.fs.path.delimiter);
    var seen_eacces = false;
    while (it.next()) |search_path| {
        // First, try the command as-is (literal match, or if it has an extension)
        if (try tryPathImpl(alloc, search_path, cmd, &path_buf, &seen_eacces)) |result| {
            return result;
        }

        // On Windows, if no extension, try with PATHEXT extensions
        if (builtin.os.tag == .windows and !has_extension and pathext_list != null) {
            for (pathext_list.?) |ext| {
                // Build cmd + extension
                const combined_len = cmd.len + ext.len;
                if (combined_len > std.fs.max_path_bytes) return error.PathTooLong;
                var cmd_with_ext: [std.fs.max_path_bytes]u8 = undefined;
                @memcpy(cmd_with_ext[0..cmd.len], cmd);
                @memcpy(cmd_with_ext[cmd.len..][0..ext.len], ext);
                const cmd_ext_str = cmd_with_ext[0..combined_len];

                if (try tryPathImpl(alloc, search_path, cmd_ext_str, &path_buf, &seen_eacces)) |result| {
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
fn tryPathImpl(alloc: Allocator, search_path: []const u8, cmd: []const u8, path_buf: *[std.fs.max_path_bytes]u8, seen_eacces: *bool) !?[]u8 {
    const path_len = search_path.len + cmd.len + 1;
    if (path_buf.len < path_len) return error.PathTooLong;

    // Copy in the full path
    @memcpy(path_buf[0..search_path.len], search_path);
    path_buf[search_path.len] = std.fs.path.sep;
    @memcpy(path_buf[search_path.len + 1 ..][0..cmd.len], cmd);
    path_buf[path_len] = 0;
    const full_path = path_buf[0..path_len :0];

    // Try to open the file
    const f = std.fs.cwd().openFile(
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
        else => return err,
    };
    defer f.close();
    const stat = try f.stat();
    if (stat.kind != .directory and isExecutable(stat.mode)) {
        return try alloc.dupe(u8, full_path);
    }

    return null;
}

fn isExecutable(mode: std.fs.File.Mode) bool {
    if (builtin.os.tag == .windows) return true;
    return mode & 0o0111 != 0;
}

// `uname -n` is the *nix equivalent of `hostname.exe` on Windows
test "expand: hostname" {
    const executable = if (builtin.os.tag == .windows) "hostname.exe" else "uname";
    const path = (try expand(testing.allocator, executable)).?;
    defer testing.allocator.free(path);
    try testing.expect(path.len > executable.len);
}

test "expand: does not exist" {
    const path = try expand(testing.allocator, "thisreallyprobablydoesntexist123");
    try testing.expect(path == null);
}

test "expand: slash" {
    const path = (try expand(testing.allocator, "foo/env")).?;
    defer testing.allocator.free(path);
    try testing.expect(path.len == 7);
}

test "expand: windows backslash passes through" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    const input = "C:\\Windows\\System32\\cmd.exe";
    const path = (try expand(testing.allocator, input)).?;
    defer testing.allocator.free(path);
    try testing.expectEqualStrings(input, path);
}

test "expand: windows drive-letter only passes through" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    // No separator, but the drive prefix means this is already a
    // path (drive-relative) and expand() must not treat it as a
    // bare name to search PATH for.
    const input = "C:cmd.exe";
    const path = (try expand(testing.allocator, input)).?;
    defer testing.allocator.free(path);
    try testing.expectEqualStrings(input, path);
}

test "expand: windows bare cmd.exe resolves on PATH" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    const path = (try expand(testing.allocator, "cmd.exe")).?;
    defer testing.allocator.free(path);
    // System32\cmd.exe lives on the default Windows PATH.
    try testing.expect(std.ascii.endsWithIgnoreCase(path, "cmd.exe"));
    try testing.expect(path.len > "cmd.exe".len);
}

test "expand: windows bare pwsh resolves via PATHEXT" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    // This test requires pwsh.exe to be on the system PATH.
    // If not found, it returns null rather than erroring.
    const path = try expand(testing.allocator, "pwsh");
    if (path) |p| {
        defer testing.allocator.free(p);
        try testing.expect(std.ascii.endsWithIgnoreCase(p, "pwsh.exe") or std.ascii.endsWithIgnoreCase(p, "pwsh.com") or std.ascii.endsWithIgnoreCase(p, "pwsh.bat") or std.ascii.endsWithIgnoreCase(p, "pwsh.cmd"));
        try testing.expect(p.len > "pwsh".len);
    }
}

test "expand: windows name with extension does not PATHEXT hunt" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    // If we provide a name with an extension (even unknown), it should
    // try that literally, not attempt PATHEXT extensions.
    const path = try expand(testing.allocator, "cmd.xyz");
    try testing.expect(path == null);
}

test "expand: windows extension present bypasses PATHEXT" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    // cmd.exe should resolve literally via the literal-first attempt,
    // not via PATHEXT hunting.
    const path = (try expand(testing.allocator, "cmd.exe")).?;
    defer testing.allocator.free(path);
    try testing.expect(std.ascii.endsWithIgnoreCase(path, "cmd.exe"));
}
