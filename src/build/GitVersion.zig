const Version = @This();

const std = @import("std");

/// The short hash (7 characters) of the latest commit.
short_hash: []const u8,

/// True if there was a diff at build time.
changes: bool,

/// Every tag on the latest commit, such as `tip`, `v1.4.1`, and `vt-0.1.0`.
tags: []const []const u8,

/// The branch that was checked out at the time of the build.
branch: []const u8,

/// Initialize the version and detect it from the Git environment. This
/// allocates using the build allocator and doesn't free.
pub fn detect(b: *std.Build) !Version {
    // Execute a bunch of git commands to determine the automatic version.
    // runAllowFail needs an out-param but only writes it when the child
    // fails, and both lookups below return on every failure, so nothing
    // ever reads this back.
    var discard_code: u8 = 0;
    const branch: []const u8 = b: {
        const tmp: []u8 = b.runAllowFail(
            &[_][]const u8{ "git", "-C", b.root.subPathOrDot(), "rev-parse", "--abbrev-ref", "HEAD" },
            &discard_code,
            .ignore,
        ) catch |err| switch (err) {
            error.FileNotFound => return error.GitNotFound,
            error.ExitCodeFailure => return error.GitNotRepository,
            else => return err,
        };

        break :b sanitizeBranch(tmp);
    };

    const short_hash = short_hash: {
        const output = b.runAllowFail(
            &[_][]const u8{ "git", "-C", b.root.subPathOrDot(), "-c", "log.showSignature=false", "log", "--pretty=format:%h", "-n", "1" },
            &discard_code,
            .ignore,
        ) catch |err| switch (err) {
            error.FileNotFound => return error.GitNotFound,
            // Same degradation as the branch lookup above: a git that runs but
            // fails here means we cannot describe this checkout, which
            // Config.init turns into a dev version. Without this prong the
            // error escapes detect() bare and fails the configure phase.
            error.ExitCodeFailure => return error.GitNotRepository,
            else => return err,
        };

        break :short_hash std.mem.trim(u8, output, &std.ascii.whitespace);
    };

    const tags: []const []const u8 = tags: {
        var tag_code: u8 = 0;
        const output = b.runAllowFail(
            &[_][]const u8{ "git", "-C", b.root.subPathOrDot(), "tag", "--points-at", "HEAD" },
            &tag_code,
            .ignore,
        ) catch |err| switch (err) {
            error.FileNotFound => return error.GitNotFound,
            else => return err,
        };

        var list: std.ArrayList([]const u8) = .empty;
        var it = std.mem.tokenizeAny(u8, output, "\r\n ");
        while (it.next()) |tag| try list.append(b.allocator, tag);
        break :tags list.items;
    };

    // Its own out-param: runAllowFail writes a code only when the child
    // fails, so sharing one would leave an earlier failure's code sitting in
    // it and report a clean tree as dirty.
    var diff_code: u8 = 0;
    _ = b.runAllowFail(&[_][]const u8{
        "git",
        "-C",
        b.root.subPathOrDot(),
        "diff",
        "--quiet",
        "--exit-code",
    }, &diff_code, .ignore) catch |err| switch (err) {
        error.FileNotFound => return error.GitNotFound,
        error.ExitCodeFailure => {}, // expected
        else => return err,
    };
    const changes = diff_code != 0;

    return .{
        .short_hash = short_hash,
        .changes = changes,
        .tags = tags,
        .branch = branch,
    };
}

/// Git's raw `rev-parse --abbrev-ref HEAD` output, rewritten in place into a
/// name usable as a semantic version pre-release identifier, which only allows
/// [0-9A-Za-z-]. Slashes would also mess up dist tarball paths.
///
/// Trim before sanitizing, not after: git terminates the ref name with a
/// newline, and the loop would rewrite that into a "-" that no later trim can
/// tell apart from one the branch name really ends with.
fn sanitizeBranch(raw: []u8) []u8 {
    const trimmed = raw[0..std.mem.trimEnd(u8, raw, "\r\n ").len];
    for (trimmed) |*c| {
        if (!std.ascii.isAlphanumeric(c.*) and c.* != '-') c.* = '-';
    }
    return trimmed;
}

test "sanitizeBranch trims git's line ending before rewriting it" {
    var raw = "fix/zero-config\n".*;
    try std.testing.expectEqualStrings("fix-zero-config", sanitizeBranch(&raw));
}

test "sanitizeBranch keeps only what a pre-release identifier allows" {
    // The rewrite is byte-wise, so the two bytes of the non-ASCII codepoint
    // become two dashes rather than one.
    var raw = "feature/caf\u{e8}_v1.2\r\n".*;
    const name = sanitizeBranch(&raw);
    try std.testing.expectEqualStrings("feature-caf---v1-2", name);
    for (name) |c| try std.testing.expect(std.ascii.isAlphanumeric(c) or c == '-');
}

test "sanitizeBranch keeps a dash the branch name really ends with" {
    var raw = "wip-\n".*;
    try std.testing.expectEqualStrings("wip-", sanitizeBranch(&raw));
}
