const std = @import("std");
const builtin = @import("builtin");
const assert = @import("../quirks.zig").inlineAssert;
const Allocator = std.mem.Allocator;
const internal_os = @import("../os/main.zig");
const global = @import("../global.zig");

const log = std.log.scoped(.config);

fn xdgPath(alloc: Allocator, subdir: []const u8) ![]const u8 {
    var environ_map = try global.environMap();
    defer environ_map.deinit();
    return try internal_os.xdg.config(
        global.io(),
        alloc,
        &environ_map,
        .{ .subdir = subdir },
    );
}

/// Default path for the XDG home configuration file. Returned value
/// must be freed by the caller.
pub fn defaultXdgPath(alloc: Allocator) ![]const u8 {
    return try xdgPath(alloc, "wintty/config.wintty");
}

/// Path used before Wintty renamed its configuration directory. Kept so an
/// existing install keeps loading rather than silently starting on defaults.
/// Returned value must be freed by the caller.
pub fn ghosttyXdgPath(alloc: Allocator) ![]const u8 {
    return try xdgPath(alloc, "ghostty/config.ghostty");
}

/// Ghostty <1.3.0 default path for the XDG home configuration file.
/// Returned value must be freed by the caller.
pub fn legacyDefaultXdgPath(alloc: Allocator) ![]const u8 {
    return try xdgPath(alloc, "ghostty/config");
}

/// Preferred default path for the XDG home configuration file.
/// Returned value must be freed by the caller.
pub fn preferredXdgPath(alloc: Allocator) ![]const u8 {
    // Newest first. The first entry is also what we return when nothing
    // exists yet, so a fresh install writes to the current path.
    const candidates = [_]*const fn (Allocator) anyerror![]const u8{
        defaultXdgPath,
        ghosttyXdgPath,
        legacyDefaultXdgPath,
    };

    var preferred: ?[]const u8 = null;
    errdefer if (preferred) |p| alloc.free(p);
    for (candidates, 0..) |candidate, i| {
        const path = try candidate(alloc);
        if (open(global.io(), path)) |f| {
            f.close(global.io());
            if (preferred) |p| alloc.free(p);
            return path;
        } else |_| {}

        if (i == 0) preferred = path else alloc.free(path);
    }

    // Nothing exists. Return the current path.
    return preferred.?;
}

/// Default path for the macOS Application Support configuration file.
/// Returned value must be freed by the caller.
pub fn defaultAppSupportPath(alloc: Allocator) ![]const u8 {
    return try internal_os.macos.appSupportDir(alloc, "config.wintty");
}

/// Path used before Wintty renamed its configuration file. Returned value
/// must be freed by the caller.
pub fn ghosttyAppSupportPath(alloc: Allocator) ![]const u8 {
    return try internal_os.macos.appSupportDir(alloc, "config.ghostty");
}

/// Ghostty <1.3.0 default path for the macOS Application Support
/// configuration file. Returned value must be freed by the caller.
pub fn legacyDefaultAppSupportPath(alloc: Allocator) ![]const u8 {
    return try internal_os.macos.appSupportDir(alloc, "config");
}

/// Preferred default path for the macOS Application Support configuration file.
/// Returned value must be freed by the caller.
pub fn preferredAppSupportPath(alloc: Allocator) ![]const u8 {
    // Newest first, same ordering rule as preferredXdgPath.
    const candidates = [_]*const fn (Allocator) anyerror![]const u8{
        defaultAppSupportPath,
        ghosttyAppSupportPath,
        legacyDefaultAppSupportPath,
    };

    var preferred: ?[]const u8 = null;
    errdefer if (preferred) |p| alloc.free(p);
    for (candidates, 0..) |candidate, i| {
        const path = try candidate(alloc);
        if (open(global.io(), path)) |f| {
            f.close(global.io());
            if (preferred) |p| alloc.free(p);
            return path;
        } else |_| {}

        if (i == 0) preferred = path else alloc.free(path);
    }

    // Nothing exists. Return the current path.
    return preferred.?;
}

/// Returns the path to the preferred default configuration file.
/// This is the file where users should place their configuration.
///
/// This doesn't create or populate the file with any default
/// contents; downstream callers must handle this.
///
/// The returned value must be freed by the caller.
pub fn preferredDefaultFilePath(alloc: Allocator) ![]const u8 {
    switch (builtin.os.tag) {
        .macos => {
            // macOS prefers the Application Support directory
            // if it exists.
            const app_support_path = try preferredAppSupportPath(alloc);
            const app_support_file = open(global.io(), app_support_path) catch {
                // Try the XDG path if it exists
                const xdg_path = try preferredXdgPath(alloc);
                const xdg_file = open(global.io(), xdg_path) catch {
                    // If neither file exists, use app support
                    alloc.free(xdg_path);
                    return app_support_path;
                };
                xdg_file.close(global.io());
                alloc.free(app_support_path);
                return xdg_path;
            };
            app_support_file.close(global.io());
            return app_support_path;
        },

        // All other platforms use XDG only
        else => return try preferredXdgPath(alloc),
    }
}

const OpenFileError = error{
    FileNotFound,
    FileIsEmpty,
    FileOpenFailed,
    NotAFile,
};

/// Opens the file at the given path and returns the file handle
/// if it exists and is non-empty. This also constrains the possible
/// errors to a smaller set that we can explicitly handle.
pub fn open(io: std.Io, path: []const u8) OpenFileError!std.Io.File {
    assert(std.fs.path.isAbsolute(path));

    var file = std.Io.Dir.openFileAbsolute(
        io,
        path,
        .{},
    ) catch |err| switch (err) {
        error.FileNotFound => return OpenFileError.FileNotFound,
        else => {
            log.warn("unexpected file open error path={s} err={}", .{
                path,
                err,
            });
            return OpenFileError.FileOpenFailed;
        },
    };
    errdefer file.close(io);

    const stat = file.stat(io) catch |err| {
        log.warn("error getting file stat path={s} err={}", .{
            path,
            err,
        });
        return OpenFileError.FileOpenFailed;
    };
    switch (stat.kind) {
        .file => {},
        else => return OpenFileError.NotAFile,
    }

    if (stat.size == 0) return OpenFileError.FileIsEmpty;

    return file;
}
