const Version = @This();

const std = @import("std");

/// The short hash (7 characters) of the latest commit.
short_hash: []const u8,

/// True if there was a diff at build time.
changes: bool,

/// The tag -- if any -- that this commit is a part of.
tag: ?[]const u8,

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
            &[_][]const u8{ "git", "-C", b.build_root.path orelse ".", "rev-parse", "--abbrev-ref", "HEAD" },
            &discard_code,
            .ignore,
        ) catch |err| switch (err) {
            error.FileNotFound => return error.GitNotFound,
            error.ExitCodeFailure => return error.GitNotRepository,
            else => return err,
        };

        // Trim before sanitizing, not after: git terminates the ref name with
        // a newline, and the loop below would rewrite that into a "-" that no
        // later trim can tell apart from one the branch name really contains.
        const trimmed = tmp[0..std.mem.trimEnd(u8, tmp, "\r\n ").len];

        // Replace characters that are not valid in semantic version
        // pre-release identifiers (which only allow [0-9A-Za-z-]).
        // Slashes would also mess up dist tarball paths.
        for (trimmed) |*c| {
            if (!std.ascii.isAlphanumeric(c.*) and c.* != '-') c.* = '-';
        }

        break :b trimmed;
    };

    const short_hash = short_hash: {
        const output = b.runAllowFail(
            &[_][]const u8{ "git", "-C", b.build_root.path orelse ".", "-c", "log.showSignature=false", "log", "--pretty=format:%h", "-n", "1" },
            &discard_code,
            .ignore,
        ) catch |err| switch (err) {
            error.FileNotFound => return error.GitNotFound,
            else => return err,
        };

        break :short_hash std.mem.trimEnd(u8, output, "\r\n ");
    };

    // Only a release tag names a version. With no filter this returns whatever
    // tag happens to sit on HEAD, and Config.init panics on a tag it does not
    // recognise -- so a tag in an unrelated namespace makes the commit
    // unbuildable. This fork tags every published sync series/vN, which lands
    // on exactly such a commit.
    //
    // --match globs the tag name with refs/tags/ stripped, and it does NOT
    // stop at a slash: `v*` rejects series/v2 only because that name starts
    // with `s`, and would accept vendor/v2 or v-old/v2 just fine. So --exclude
    // carries the namespace rule and --match carries the name rule; drop
    // either and a tag that is not a release can name a version again.
    //
    // They filter; they do not validate. Whether a v* tag is one Config.init
    // accepts stays Config.init's call.
    var tag_code: u8 = 0;
    const tag = b.runAllowFail(
        &[_][]const u8{ "git", "-C", b.build_root.path orelse ".", "describe", "--exact-match", "--tags", "--match", "v*", "--match", "tip", "--exclude", "*/*" },
        &tag_code,
        .ignore,
    ) catch |err| switch (err) {
        error.FileNotFound => return error.GitNotFound,
        error.ExitCodeFailure => "", // expected
        else => return err,
    };

    // Its own out-param: runAllowFail writes a code only when the child fails,
    // so sharing one would leave the describe failure above still sitting in it
    // -- 128 whenever HEAD carries no v* or tip tag -- and report a clean
    // tree as dirty.
    var diff_code: u8 = 0;
    _ = b.runAllowFail(&[_][]const u8{
        "git",
        "-C",
        b.build_root.path orelse ".",
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
        .tag = if (tag.len > 0) std.mem.trimEnd(u8, tag, "\r\n ") else null,
        .branch = branch,
    };
}
