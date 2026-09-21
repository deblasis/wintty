const std = @import("std");
const builtin = @import("builtin");
const Allocator = std.mem.Allocator;
const global = @import("../global.zig");

const log = std.log.scoped(.resources_dir);

/// The directory, relative to the install root, that the resources tree is
/// installed under.
const share_dir = if (builtin.target.os.tag == .freebsd) "local/share" else "share";

/// The paths, relative to the share directory, that tell us we have found the
/// resources tree rather than some unrelated `share`.
///
/// Every platform but Windows names a compiled terminfo entry. Windows names
/// the uncompiled source, because that is all a Windows build can produce
/// without `tic`, so finding the tree there says the tree is ours and says
/// nothing about what a child could read out of it.
/// `termio.Exec.terminfoHasEntry` carries that difference when it picks a
/// TERM.
const sentinels = switch (builtin.target.os.tag) {
    .windows => .{"terminfo/ghostty.terminfo"},
    .macos => .{"terminfo/78/xterm-ghostty"},
    .freebsd => .{ "site-terminfo/g/ghostty", "site-terminfo/x/xterm-ghostty" },
    else => .{ "terminfo/g/ghostty", "terminfo/x/xterm-ghostty" },
};

pub const ResourcesDir = struct {
    /// Avoid accessing these directly, use the app() and host() methods instead.
    app_path: ?[]const u8 = null,
    host_path: ?[]const u8 = null,

    /// Free resources held. Requires the same allocator as when resourcesDir()
    /// is called.
    pub fn deinit(self: *ResourcesDir, alloc: Allocator) void {
        if (self.app_path) |p| alloc.free(p);
        if (self.host_path) |p| alloc.free(p);
    }

    /// Get the directory to the bundled resources directory accessible
    /// by the application.
    pub fn app(self: *const ResourcesDir) ?[]const u8 {
        return self.app_path;
    }

    /// Get the directory to the bundled resources directory accessible
    /// by the host environment (i.e. for sandboxed applications). The
    /// returned directory might not be accessible from the application
    /// itself.
    ///
    /// In non-sandboxed environment, this should be the same as app().
    pub fn host(self: *const ResourcesDir) ?[]const u8 {
        return self.host_path orelse self.app_path;
    }
};

/// Gets the directory to the bundled resources directory, if it
/// exists (not all platforms or packages have it). The output is
/// owned by the caller.
///
/// This is highly Ghostty-specific and can likely be generalized at
/// some point but we can cross that bridge if we ever need to.
pub fn resourcesDir(alloc: Allocator) !ResourcesDir {
    // Use the GHOSTTY_RESOURCES_DIR environment variable in release builds.
    //
    // In debug builds we try using terminfo detection first instead, since
    // if debug Ghostty is launched by an older version of Ghostty, it
    // would inherit the old, stale resources of older Ghostty instead of the
    // freshly built ones under zig-out/share/ghostty.
    //
    // Note: we ALWAYS want to allocate here because the result is always
    // freed, do not try to use internal_os.getenv or posix getenv.
    if (comptime builtin.mode != .Debug) env: {
        const dir = global.environ().getAlloc(alloc, "GHOSTTY_RESOURCES_DIR") catch |err| switch (err) {
            error.EnvironmentVariableMissing => break :env,
            else => return err,
        };

        if (validResourcesDir(dir)) return .{ .app_path = dir };

        log.warn(
            "GHOSTTY_RESOURCES_DIR is not an existing absolute directory, ignoring dir={s}",
            .{dir},
        );
        alloc.free(dir);
    }

    // Get the path to our running binary
    var exe_buf: [std.fs.max_path_bytes]u8 = undefined;
    const exe: []const u8 = exe_buf[0 .. std.process.executablePath(
        global.io(),
        &exe_buf,
    ) catch return .{}];

    if (try resourcesDirFromExe(alloc, exe)) |v| return v;

    // If terminfo detection failed in debug builds (somehow),
    // fallback and use the provided resources dir.
    if (comptime builtin.mode == .Debug) {
        if (global.environ().getAlloc(alloc, "GHOSTTY_RESOURCES_DIR")) |dir| {
            if (validResourcesDir(dir)) return .{ .app_path = dir };

            log.warn(
                "GHOSTTY_RESOURCES_DIR is not an existing absolute directory, ignoring dir={s}",
                .{dir},
            );
            alloc.free(dir);
        } else |err| switch (err) {
            error.InvalidWtf8, error.EnvironmentVariableMissing => {},
            else => return err,
        }
    }

    return .{};
}

/// Climb from `exe_path` towards the filesystem root looking for the bundled
/// resources tree the way an install lays it out, and return it if found.
///
/// Split out from `resourcesDir` so a test can point it at a real packaged
/// layout on disk. The climb is what makes one layout serve executables at
/// different depths: an app at `<root>/app.exe` and a helper at
/// `<root>/bin/helper.exe` both arrive at `<root>/share/ghostty`.
fn resourcesDirFromExe(alloc: Allocator, exe_path: []const u8) !?ResourcesDir {
    var exe = exe_path;
    var dir_buf: [std.fs.max_path_bytes]u8 = undefined;
    while (std.fs.path.dirname(exe)) |dir| {
        exe = dir;

        // Windows: never probe a drive root. `C:\` grants Authenticated Users
        // the right to create folders, so a standard user can plant
        // `C:\share\terminfo\ghostty.terminfo` and have an installed app adopt
        // a tree they own. The climb is nearest-first, so this is only
        // reachable where the install's own tree is missing, which is exactly
        // the partial, portable or damaged install that would take it. Nothing
        // legitimate puts a resources tree at a drive root.
        if (comptime builtin.target.os.tag == .windows) {
            if (std.fs.path.dirname(dir) == null) break;
        }

        // On MacOS, we look for the app bundle path.
        if (comptime builtin.target.os.tag.isDarwin()) {
            inline for (sentinels) |sentinel| {
                if (try maybeDir(
                    &dir_buf,
                    dir,
                    "Contents/Resources",
                    sentinel,
                )) |v| {
                    return .{ .app_path = try std.fs.path.join(alloc, &.{ v, "ghostty" }) };
                }
            }
        }

        // On all platforms (except BSD), we look for a /usr/share style path. This
        // is valid even on Mac since there is nothing that requires
        // Ghostty to be in an app bundle.
        inline for (sentinels) |sentinel| {
            if (try maybeDir(
                &dir_buf,
                dir,
                share_dir,
                sentinel,
            )) |v| {
                return .{ .app_path = try std.fs.path.join(alloc, &.{ v, "ghostty" }) };
            }
        }
    }

    return null;
}

/// Returns true if a GHOSTTY_RESOURCES_DIR value is usable as the resources
/// directory.
///
/// Everything derived from this value assumes a full path to a real
/// directory: the terminfo database is looked for beside it, and the man
/// pages and shell integration scripts are looked for inside it. A relative
/// or missing path produces a broken child environment rather than an
/// error, so we reject it here and fall back to detection.
///
/// This gate applies in release builds too, where the environment value is
/// otherwise taken on trust. The tradeoff is deliberate and it is not free:
/// a directory that exists but that we cannot open at startup, such as a
/// permission-denied or not-yet-mounted share, or on Windows a
/// rooted-but-driveless path such as `\share\ghostty` that isAbsolute
/// rejects outright, is now discarded rather than used. Detection then
/// finds nothing and the child loses terminfo, man pages and shell
/// integration. Every rejection is logged by the caller with the value.
///
/// On Windows the value must also carry the sentinel that detection looks
/// for. Until this tree shipped, pointing this variable somewhere was the
/// only way to get a resources directory on Windows at all, so it was an
/// affordance for developers. Now it redirects code that runs in every
/// session: `ENV` to a `ghostty.bash`, `ZDOTDIR` to a `.zshenv`, `CLINK_PATH`
/// to a directory Clink autoloads EVERY `*.lua` from, and TERMINFO. A user
/// variable is writable by a standard user, so without this a folder they own
/// plus `HKCU\Environment` is arbitrary code in every shell, surviving
/// reboot, reinstall and update, and needing nothing in the install directory
/// (which is what made it work against an admin-only install too).
///
/// Requiring the sentinel is a floor, not a boundary: whoever can create the
/// folder can create the sentinel inside it. What it buys is that the value
/// has to name something shaped like our tree instead of any directory at
/// all, so a stray or inherited variable cannot quietly take over a session.
/// Confining the value to the install root would be the actual boundary and
/// is deliberately not done here, because the test harnesses stage a tree
/// outside it; see the pull request for the recommendation.
fn validResourcesDir(path: []const u8) bool {
    if (path.len == 0) return false;
    if (!std.fs.path.isAbsolute(path)) return false;

    var dir = std.Io.Dir.cwd().openDir(global.io(), path, .{}) catch return false;
    dir.close(global.io());

    if (comptime builtin.target.os.tag == .windows) {
        if (!hasSentinelBeside(path)) return false;
    }

    return true;
}

/// Whether the resources tree's sentinel sits beside `path` the way an
/// install lays it out, i.e. `<parent>/<sentinel>` for a `path` of
/// `<parent>/ghostty`. This is the same evidence `resourcesDirFromExe`
/// requires; it is applied to the environment value so that both routes to a
/// resources directory demand the same thing.
fn hasSentinelBeside(path: []const u8) bool {
    const parent = std.fs.path.dirname(path) orelse return false;

    var buf: [std.fs.max_path_bytes]u8 = undefined;
    inline for (sentinels) |sentinel| {
        if (std.fmt.bufPrint(&buf, "{s}/{s}", .{ parent, sentinel })) |p| {
            if (std.Io.Dir.accessAbsolute(global.io(), p, .{})) {
                return true;
            } else |_| {}
        } else |_| {}
    }

    return false;
}

/// Little helper to check if the "base/sub/suffix" directory exists and
/// if so return true. The "suffix" is just used as a way to verify a directory
/// seems roughly right.
///
/// "buf" must be large enough to fit base + sub + suffix. This is generally
/// max_path_bytes so its not a big deal.
pub fn maybeDir(
    buf: []u8,
    base: []const u8,
    sub: []const u8,
    suffix: []const u8,
) !?[]const u8 {
    const path = try std.fmt.bufPrint(buf, "{s}/{s}/{s}", .{ base, sub, suffix });

    if (std.Io.Dir.accessAbsolute(global.io(), path, .{})) {
        const len = path.len - suffix.len - 1;
        return buf[0..len];
    } else |_| {
        // Folder doesn't exist. If a different error happens its okay
        // we just ignore it and move on.
    }

    return null;
}

/// Lay out a packaged install under `dir`: the app executable at the root, a
/// helper executable one level down in `bin`, and the resources tree in
/// `share` beside them. Mirrors what the Windows build installs and what the
/// packaging copies beside the app.
fn createPackagedLayout(dir: std.Io.Dir) !void {
    const io = std.testing.io;

    try dir.writeFile(io, .{ .sub_path = "Wintty.exe", .data = "" });
    try dir.createDirPath(io, "bin");
    try dir.writeFile(io, .{ .sub_path = "bin/wintty.exe", .data = "" });

    // The sentinel, whatever this platform detects the tree by.
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const sentinel_path = try std.fmt.bufPrint(
        &buf,
        share_dir ++ "/{s}",
        .{sentinels[0]},
    );
    if (std.fs.path.dirname(sentinel_path)) |parent| try dir.createDirPath(io, parent);
    try dir.writeFile(io, .{ .sub_path = sentinel_path, .data = "" });

    // The tree itself.
    try dir.createDirPath(io, share_dir ++ "/ghostty/shell-integration/bash");
    try dir.createDirPath(io, share_dir ++ "/ghostty/themes");
}

/// The resources directory `createPackagedLayout` puts under `root`, spelled
/// the way the lookup spells it: `maybeDir` builds its half with `/` and
/// `std.fs.path.join` appends with the native separator, so on Windows the
/// result mixes the two. Windows accepts that and every consumer goes through
/// the filesystem, so this mirrors it rather than normalising it.
fn packagedResourcesDir(alloc: Allocator, root: []const u8) ![]const u8 {
    const share = try std.fmt.allocPrint(alloc, "{s}/" ++ share_dir, .{root});
    return try std.fs.path.join(alloc, &.{ share, "ghostty" });
}

test "resourcesDirFromExe finds the tree from an app beside it in a packaged layout" {
    const testing = std.testing;
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    try createPackagedLayout(tmp.dir);
    const root = try tmp.dir.realPathFileAlloc(testing.io, ".", alloc);

    const exe = try std.fmt.allocPrint(alloc, "{s}/Wintty.exe", .{root});
    const found = (try resourcesDirFromExe(alloc, exe)) orelse
        return error.ResourcesDirNotFound;

    try testing.expectEqualStrings(
        try packagedResourcesDir(alloc, root),
        found.app().?,
    );
}

test "resourcesDirFromExe climbs out of bin to the tree in a packaged layout" {
    const testing = std.testing;
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    // The CLI executable ships one level deeper than the app. It has to reach
    // the same tree, otherwise `+list-themes` and friends run resourceless in
    // an install where the app is fine.
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    try createPackagedLayout(tmp.dir);
    const root = try tmp.dir.realPathFileAlloc(testing.io, ".", alloc);

    const exe = try std.fmt.allocPrint(alloc, "{s}/bin/wintty.exe", .{root});
    const found = (try resourcesDirFromExe(alloc, exe)) orelse
        return error.ResourcesDirNotFound;

    try testing.expectEqualStrings(
        try packagedResourcesDir(alloc, root),
        found.app().?,
    );
}

test "resourcesDirFromExe ignores a share directory that is not a resources tree" {
    const testing = std.testing;
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    // A `share` above the executable that is not ours. Detection has to want
    // the sentinel and not merely the directory name, or any unrelated
    // `share` up the tree would be adopted.
    //
    // Deliberately asserted against a tree built here rather than against the
    // absence of one anywhere above the temp directory: the climb walks real
    // ancestors, so a test that asserts "nothing is found" is a test about
    // whatever happens to be on the machine. `zig build -p .` alone produces
    // a `share/terminfo/ghostty.terminfo` in the build root and would turn
    // such a test red.
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.createDirPath(testing.io, "install/share/doc");
    try tmp.dir.createDirPath(testing.io, "install/share/ghostty/themes");
    try tmp.dir.writeFile(testing.io, .{ .sub_path = "install/Wintty.exe", .data = "" });
    const root = try tmp.dir.realPathFileAlloc(testing.io, ".", alloc);

    const exe = try std.fmt.allocPrint(alloc, "{s}/install/Wintty.exe", .{root});
    const found = try resourcesDirFromExe(alloc, exe);

    // Either nothing, or something from further up that is genuinely a
    // resources tree. What it must never be is this `share`, which has the
    // right name and no sentinel.
    if (found) |v| {
        const wrong = try std.fmt.allocPrint(alloc, "{s}/install/", .{root});
        try testing.expect(!std.mem.startsWith(u8, v.app().?, wrong));
    }
}

test "resourcesDirFromExe windows: never adopts a tree at a drive root" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const testing = std.testing;
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    // `C:\` grants Authenticated Users the right to create folders, so
    // `C:\share` is plantable by a standard user. The climb must stop before
    // it, whatever is there. Asserting on the real drive root would depend on
    // what happens to be on this machine, so the claim is made against the
    // path shape instead: a drive root has no parent, and that is what the
    // loop now refuses to probe.
    try testing.expect(std.fs.path.dirname("C:\\") == null);
    try testing.expect(std.fs.path.dirname("C:\\Program Files") != null);

    // An executable sitting directly at a drive root therefore finds nothing,
    // even though the loop would otherwise have probed that root.
    const found = try resourcesDirFromExe(alloc, "C:\\Wintty.exe");
    try testing.expect(found == null);
}

test "validResourcesDir rejects a path with no directory component" {
    const testing = std.testing;

    try testing.expect(!validResourcesDir(""));
    try testing.expect(!validResourcesDir("ghostty"));
    try testing.expect(!validResourcesDir("share/ghostty"));
}

test "validResourcesDir rejects a relative path that exists" {
    const testing = std.testing;

    // The current directory always exists and always opens, which is what
    // makes it the input that separates the absolute-path check from the
    // openDir below it. Every other relative path we could name fails to
    // open anyway and so is rejected either way.
    try testing.expect(!validResourcesDir("."));
}

test "validResourcesDir rejects an absolute path that does not exist" {
    const testing = std.testing;

    const missing = if (comptime builtin.os.tag == .windows)
        "C:/ghostty-does-not-exist/share/ghostty"
    else
        "/ghostty-does-not-exist/share/ghostty";

    try testing.expect(!validResourcesDir(missing));
}

test "validResourcesDir accepts the resources directory of a packaged layout" {
    const testing = std.testing;
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    try createPackagedLayout(tmp.dir);
    const root = try tmp.dir.realPathFileAlloc(testing.io, ".", alloc);

    try testing.expect(validResourcesDir(try packagedResourcesDir(alloc, root)));
}

test "validResourcesDir windows: rejects a directory carrying no sentinel" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const testing = std.testing;
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    // A folder a standard user can make, named by GHOSTTY_RESOURCES_DIR. It
    // opens, so the checks above pass it. Accepting it would point ENV,
    // ZDOTDIR, CLINK_PATH and TERMINFO at a directory that has nothing to do
    // with an install, which is the whole reason the sentinel is required.
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.createDirPath(testing.io, "planted/ghostty/shell-integration/cmd");
    const root = try tmp.dir.realPathFileAlloc(testing.io, ".", alloc);

    const planted = try std.fmt.allocPrint(alloc, "{s}/planted/ghostty", .{root});
    try testing.expect(!validResourcesDir(planted));
}

test "validResourcesDir non-windows: takes an existing absolute directory" {
    if (comptime builtin.os.tag == .windows) return error.SkipZigTest;
    const testing = std.testing;
    const TempDir = @import("TempDir.zig");

    // The sentinel requirement is Windows-only: elsewhere a packager may
    // legitimately point this at a tree whose terminfo lives in the system
    // database, and this value has never been the only route to a resources
    // directory there.
    var td = try TempDir.init();
    defer td.deinit();

    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const len = try td.dir.realPath(testing.io, &buf);
    try testing.expect(validResourcesDir(buf[0..len]));
}
