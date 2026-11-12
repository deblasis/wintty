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

/// The HEAD tag that may name this build's release version, or null when
/// none applies.
///
/// Only an unnamespaced `v...` tag is a candidate: `vt-*` belongs to
/// libghostty-vt (`libVersion` reads it), `tip` is a pre-release marker,
/// namespaced tags (`series/v2`, `backup/v1.0.0`) never name a version, and
/// a `w...` tag is the fork's own release tag, which the app version string
/// must not pick up. A candidate that is not exactly `expected` is an error
/// rather than a skip: a release tag that disagrees with build.zig is a real
/// defect and must fail the build loudly.
///
/// The tag list comes from `git tag --points-at HEAD` (GitVersion.detect),
/// so this walks every tag on HEAD, and the candidate rule is the whole of
/// "only a release tag can name a version".
pub fn releaseTag(
    tags: []const []const u8,
    expected: []const u8,
) error{TagMismatch}!?[]const u8 {
    for (tags) |tag| {
        if (!isVersionTagCandidate(tag)) continue;
        if (!std.mem.eql(u8, tag, expected)) return error.TagMismatch;
        return tag;
    }
    return null;
}

fn isVersionTagCandidate(tag: []const u8) bool {
    // libghostty-vt release tags are checked by libVersion.
    if (std.mem.startsWith(u8, tag, "vt-")) return false;

    // Tip releases behave just like any other pre-release so we skip.
    if (std.mem.eql(u8, tag, "tip")) return false;

    // Namespaced tags are never release tags. --match globs tag names do not
    // stop at a slash, so `vendor/v2` and `backup/v1.0.0` sail through a
    // pattern check; the namespace rule has to be explicit.
    if (std.mem.indexOfScalar(u8, tag, '/') != null) return false;

    // Only a `v` name can be a release. This is what keeps `w1.0.0-rc.9`
    // (the fork's release tag) and any stray marker from naming a version
    // or tripping the mismatch error.
    return std.mem.startsWith(u8, tag, "v");
}

test "releaseTag picks an exact release tag" {
    const tags: []const []const u8 = &.{ "v1.4.0", "tip" };
    try std.testing.expectEqualStrings("v1.4.0", (try releaseTag(tags, "v1.4.0")).?);
}

test "releaseTag ignores library, tip, namespaced and fork tags" {
    const tags: []const []const u8 = &.{
        "vt-1.0.1",
        "tip",
        "series/v2",
        "vendor/v2",
        "w1.0.0-rc.9",
        "pr-1451-assets",
        "wip-sync-66f7c39f",
    };
    try std.testing.expect((try releaseTag(tags, "v9.9.9")) == null);
}

test "releaseTag rejects a release tag that disagrees with build.zig" {
    const tags: []const []const u8 = &.{"v1.3.1"};
    try std.testing.expectError(error.TagMismatch, releaseTag(tags, "v1.4.0"));
}

test "releaseTag reads through a namespaced tag only to the v tag" {
    const tags: []const []const u8 = &.{ "series/v2", "v1.3.1" };
    try std.testing.expectEqualStrings("v1.3.1", (try releaseTag(tags, "v1.3.1")).?);
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
