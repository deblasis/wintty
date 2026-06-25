const std = @import("std");
const builtin = @import("builtin");
const Allocator = std.mem.Allocator;
const ArenaAllocator = std.heap.ArenaAllocator;
const EnvMap = std.process.EnvMap;
const config = @import("../config.zig");
const homedir = @import("../os/homedir.zig");
const internal_os = @import("../os/main.zig");

const log = std.log.scoped(.shell_integration);

/// Shell types we support
pub const Shell = enum {
    bash,
    cmd,
    elvish,
    fish,
    nushell,
    powershell,
    zsh,
};

/// The result of setting up a shell integration.
pub const ShellIntegration = struct {
    /// The successfully-integrated shell.
    shell: Shell,

    /// The command to use to start the shell with the integration.
    /// In most cases this is identical to the command given but for
    /// bash in particular it may be different.
    ///
    /// The memory is allocated in the arena given to setup.
    command: config.Command,
};

/// Set up the command execution environment for automatic
/// integrated shell integration and return a ShellIntegration
/// struct describing the integration.  If integration fails
/// (shell type couldn't be detected, etc.), this will return null.
///
/// The allocator is used for temporary values and to allocate values
/// in the ShellIntegration result. It is expected to be an arena to
/// simplify cleanup.
pub fn setup(
    alloc_arena: Allocator,
    resource_dir: []const u8,
    command: config.Command,
    env: *EnvMap,
    force_shell: ?Shell,
) !?ShellIntegration {
    const shell: Shell = force_shell orelse
        try detectShell(alloc_arena, command) orelse
        return null;

    const new_command: config.Command = switch (shell) {
        .bash => try setupBash(
            alloc_arena,
            command,
            resource_dir,
            env,
        ),

        .nushell => try setupNushell(
            alloc_arena,
            command,
            resource_dir,
            env,
        ),

        .powershell => try setupPowerShell(
            alloc_arena,
            command,
            resource_dir,
            env,
        ),

        .zsh => try setupZsh(
            alloc_arena,
            command,
            resource_dir,
            env,
        ),

        .cmd => try setupCmd(alloc_arena, command, resource_dir, env),

        .fish => try setupFish(
            alloc_arena,
            command,
            resource_dir,
            env,
        ),

        .elvish => xdg: {
            // elvish has a native Windows build and keeps the Windows path
            // conventions (cygwin = false).
            if (!try setupXdgDataDirs(alloc_arena, resource_dir, env, false)) return null;
            break :xdg try command.clone(alloc_arena);
        },
    } orelse return null;

    return .{
        .shell = shell,
        .command = new_command,
    };
}

test "force shell" {
    const testing = std.testing;

    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    inline for (@typeInfo(Shell).@"enum".fields) |field| {
        const shell = @field(Shell, field.name);

        var res: TmpResourcesDir = try .init(alloc, shell);
        defer res.deinit();

        const result = try setup(
            alloc,
            res.path,
            .{ .shell = "sh" },
            &env,
            shell,
        );
        try testing.expectEqual(shell, result.?.shell);
    }
}

test "shell integration failure" {
    const testing = std.testing;

    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    const result = try setup(
        alloc,
        "/nonexistent",
        .{ .shell = "sh" },
        &env,
        null,
    );

    try testing.expect(result == null);
    try testing.expectEqual(0, env.count());
}

fn detectShell(alloc: Allocator, command: config.Command) !?Shell {
    var arg_iter = try command.argIterator(alloc);
    defer arg_iter.deinit();

    const arg0 = arg_iter.next() orelse return null;
    const exe = std.fs.path.basename(arg0);

    // Windows shells are spelled with a `.exe` suffix and case-insensitively
    // (e.g. `C:\msys64\usr\bin\zsh.exe`, or a Start Menu `PWSH.EXE`); POSIX
    // shells are bare lowercase names. Strip the suffix case-insensitively
    // and match case-insensitively so both forms detect. Without this, the
    // MSYS2/Cygwin `bash.exe`/`zsh.exe`/`fish.exe` would go undetected and
    // never get shell integration.
    const name = if (std.ascii.endsWithIgnoreCase(exe, ".exe"))
        exe[0 .. exe.len - 4]
    else
        exe;

    if (std.ascii.eqlIgnoreCase("bash", name)) {
        // Apple distributes their own patched version of Bash 3.2
        // on macOS that disables the ENV-based POSIX startup path.
        // This means we're unable to perform our automatic shell
        // integration sequence in this specific environment.
        //
        // If we're running "/bin/bash" on Darwin, we can assume
        // we're using Apple's Bash because /bin is non-writable
        // on modern macOS due to System Integrity Protection.
        if (comptime builtin.target.os.tag.isDarwin()) {
            if (std.mem.eql(u8, "/bin/bash", arg0)) {
                return null;
            }
        }
        return .bash;
    }

    if (std.ascii.eqlIgnoreCase("elvish", name)) return .elvish;
    if (std.ascii.eqlIgnoreCase("fish", name)) return .fish;
    if (std.ascii.eqlIgnoreCase("nu", name)) return .nushell;
    if (std.ascii.eqlIgnoreCase("zsh", name)) return .zsh;
    if (std.ascii.eqlIgnoreCase("pwsh", name)) return .powershell;
    if (std.ascii.eqlIgnoreCase("powershell", name)) return .powershell;
    if (std.ascii.eqlIgnoreCase("cmd", name)) return .cmd;

    return null;
}

test detectShell {
    const testing = std.testing;
    const alloc = testing.allocator;

    try testing.expect(try detectShell(alloc, .{ .shell = "sh" }) == null);
    try testing.expectEqual(.bash, try detectShell(alloc, .{ .shell = "bash" }));
    try testing.expectEqual(.elvish, try detectShell(alloc, .{ .shell = "elvish" }));
    try testing.expectEqual(.fish, try detectShell(alloc, .{ .shell = "fish" }));
    try testing.expectEqual(.nushell, try detectShell(alloc, .{ .shell = "nu" }));
    try testing.expectEqual(.zsh, try detectShell(alloc, .{ .shell = "zsh" }));

    if (comptime builtin.target.os.tag.isDarwin()) {
        try testing.expect(try detectShell(alloc, .{ .shell = "/bin/bash" }) == null);
    }

    try testing.expectEqual(.bash, try detectShell(alloc, .{ .shell = "bash -c 'command'" }));
    try testing.expectEqual(.bash, try detectShell(alloc, .{ .shell = "\"/a b/bash\"" }));

    try testing.expectEqual(.powershell, try detectShell(alloc, .{ .shell = "pwsh" }));
    try testing.expectEqual(.powershell, try detectShell(alloc, .{ .shell = "pwsh.exe" }));
    try testing.expectEqual(.powershell, try detectShell(alloc, .{ .shell = "powershell" }));
    try testing.expectEqual(.powershell, try detectShell(alloc, .{ .shell = "powershell.exe" }));
    try testing.expectEqual(.cmd, try detectShell(alloc, .{ .shell = "cmd" }));
    try testing.expectEqual(.cmd, try detectShell(alloc, .{ .shell = "cmd.exe" }));
    try testing.expectEqual(.cmd, try detectShell(alloc, .{ .shell = "CMD.EXE" }));

    // MSYS2/Cygwin shells are spelled with a `.exe` suffix; they must
    // detect the same as their bare POSIX names so integration applies.
    try testing.expectEqual(.zsh, try detectShell(alloc, .{ .shell = "zsh.exe" }));
    try testing.expectEqual(.fish, try detectShell(alloc, .{ .shell = "fish.exe" }));
    try testing.expectEqual(.bash, try detectShell(alloc, .{ .shell = "bash.exe" }));
    // Forward slashes so basename splits on POSIX CI hosts too (see note below).
    try testing.expectEqual(.zsh, try detectShell(alloc, .{ .shell = "C:/msys64/usr/bin/zsh.exe -i" }));

    // std.fs.path.basename uses POSIX semantics on non-Windows hosts,
    // so a backslash-only path is treated as a single component. Only
    // assert the fully-qualified case where basename actually splits.
    if (comptime builtin.target.os.tag == .windows) {
        try testing.expectEqual(.powershell, try detectShell(alloc, .{ .shell = "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\"" }));
    }
}

/// Set up the shell integration features environment variable.
pub fn setupFeatures(
    env: *EnvMap,
    features: config.ShellIntegrationFeatures,
    cursor_blink: bool,
) !void {
    const fields = @typeInfo(@TypeOf(features)).@"struct".fields;
    const capacity: usize = capacity: {
        comptime var n: usize = fields.len - 1; // commas
        inline for (fields) |field| n += field.name.len;
        n += ":steady".len; // cursor value
        break :capacity n;
    };

    var buf: [capacity]u8 = undefined;
    var writer: std.Io.Writer = .fixed(&buf);

    // Sort the fields so that the output is deterministic. This is
    // done at comptime so it has no runtime cost
    const fields_sorted: [fields.len][]const u8 = comptime fields: {
        var fields_sorted: [fields.len][]const u8 = undefined;
        for (fields, 0..) |field, i| fields_sorted[i] = field.name;
        std.mem.sortUnstable(
            []const u8,
            &fields_sorted,
            {},
            (struct {
                fn lessThan(_: void, lhs: []const u8, rhs: []const u8) bool {
                    return std.ascii.orderIgnoreCase(lhs, rhs) == .lt;
                }
            }).lessThan,
        );
        break :fields fields_sorted;
    };

    inline for (fields_sorted) |name| {
        if (@field(features, name)) {
            if (writer.end > 0) try writer.writeByte(',');
            try writer.writeAll(name);

            if (std.mem.eql(u8, name, "cursor")) {
                try writer.writeAll(if (cursor_blink) ":blink" else ":steady");
            }
        }
    }

    if (writer.end > 0) {
        try env.put("GHOSTTY_SHELL_FEATURES", buf[0..writer.end]);
    }
}

test "setup features" {
    const testing = std.testing;

    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    // Test: all features enabled
    {
        var env = EnvMap.init(alloc);
        defer env.deinit();

        try setupFeatures(&env, .{ .cursor = true, .sudo = true, .title = true, .@"ssh-env" = true, .@"ssh-terminfo" = true, .path = true }, true);
        try testing.expectEqualStrings("cursor:blink,path,ssh-env,ssh-terminfo,sudo,title", env.get("GHOSTTY_SHELL_FEATURES").?);
    }

    // Test: all features disabled
    {
        var env = EnvMap.init(alloc);
        defer env.deinit();

        try setupFeatures(&env, std.mem.zeroes(config.ShellIntegrationFeatures), true);
        try testing.expect(env.get("GHOSTTY_SHELL_FEATURES") == null);
    }

    // Test: mixed features
    {
        var env = EnvMap.init(alloc);
        defer env.deinit();

        try setupFeatures(&env, .{ .cursor = false, .sudo = true, .title = false, .@"ssh-env" = true, .@"ssh-terminfo" = false, .path = false }, true);
        try testing.expectEqualStrings("ssh-env,sudo", env.get("GHOSTTY_SHELL_FEATURES").?);
    }

    // Test: blinking cursor
    {
        var env = EnvMap.init(alloc);
        defer env.deinit();
        try setupFeatures(&env, .{ .cursor = true, .sudo = false, .title = false, .@"ssh-env" = false, .@"ssh-terminfo" = false, .path = false }, true);
        try testing.expectEqualStrings("cursor:blink", env.get("GHOSTTY_SHELL_FEATURES").?);
    }

    // Test: steady cursor
    {
        var env = EnvMap.init(alloc);
        defer env.deinit();
        try setupFeatures(&env, .{ .cursor = true, .sudo = false, .title = false, .@"ssh-env" = false, .@"ssh-terminfo" = false, .path = false }, false);
        try testing.expectEqualStrings("cursor:steady", env.get("GHOSTTY_SHELL_FEATURES").?);
    }
}

/// Setup the bash automatic shell integration. This works by
/// starting bash in POSIX mode and using the ENV environment
/// variable to load our bash integration script. This prevents
/// bash from loading its normal startup files, which becomes
/// our script's responsibility (along with disabling POSIX
/// mode).
///
/// This returns a new (allocated) shell command string that
/// enables the integration or null if integration failed.
/// Convert a Windows path to its Cygwin/MSYS2 POSIX form, e.g.
/// `C:\Users\x` -> `/c/Users/x`. MSYS2/Git-bash/Cygwin shells interpret
/// the integration env paths (ENV, ZDOTDIR, XDG_DATA_DIRS) as POSIX, so
/// the Windows paths Ghostty builds must be translated for them to
/// resolve. Assumes the default mount (drive `C:` -> `/c`), which is what
/// MSYS2 and Git for Windows use; a custom mount prefix (rare) would
/// differ. Non-drive-rooted paths just get backslashes normalized.
/// Pure string transform; callers gate on the platform/shell.
fn winToCygwinPath(alloc: Allocator, path: []const u8) ![]u8 {
    // Extended-length UNC (`\\?\UNC\server\share`) maps to Cygwin's
    // `//server/share`. Handle it before the generic `\\?\` strip below,
    // which would otherwise leave a bogus literal `UNC` segment.
    const unc_prefix = "\\\\?\\UNC\\";
    if (std.mem.startsWith(u8, path, unc_prefix)) {
        const rest = path[unc_prefix.len..];
        const out = try alloc.alloc(u8, 2 + rest.len);
        out[0] = '/';
        out[1] = '/';
        for (rest, 0..) |c, i| out[2 + i] = if (c == '\\') '/' else c;
        return out;
    }

    // Strip a plain extended-length prefix (`\\?\`) that Win32 APIs and
    // std.fs.realpath can produce, so the drive letter is detectable.
    // Callers pass absolute, separator-rooted paths (a realpath of the
    // resources dir); drive-relative forms like `C:foo` are not expected.
    const p = if (std.mem.startsWith(u8, path, "\\\\?\\")) path[4..] else path;

    if (p.len >= 2 and p[1] == ':' and std.ascii.isAlphabetic(p[0])) {
        const rest = p[2..]; // keeps the leading separator
        const out = try alloc.alloc(u8, 2 + rest.len);
        out[0] = '/';
        out[1] = std.ascii.toLower(p[0]);
        for (rest, 0..) |c, i| out[2 + i] = if (c == '\\') '/' else c;
        return out;
    }
    const out = try alloc.dupe(u8, p);
    for (out) |*c| {
        if (c.* == '\\') c.* = '/';
    }
    return out;
}

/// Returns the form of `win_path` that a shell-integration env var should
/// carry: on Windows, the Cygwin POSIX form (the bash/zsh/fish Ghostty
/// integrates under ConPTY are Cygwin-family — MSYS2/Git/Cygwin); on other
/// platforms, the path unchanged. WSL bash (a `bash.exe`/`wsl.exe` stub) is
/// out of scope (see maybeWrapGitBashWithWinpty in Exec.zig); if a user
/// points `command` at it, the POSIX path simply won't resolve in the
/// distro and integration silently no-ops, exactly as before this change.
fn shellEnvPath(alloc: Allocator, win_path: []const u8) ![]const u8 {
    if (comptime builtin.os.tag != .windows) return win_path;
    return try winToCygwinPath(alloc, win_path);
}

fn setupBash(
    alloc: Allocator,
    command: config.Command,
    resource_dir: []const u8,
    env: *EnvMap,
) !?config.Command {
    var stack_fallback = std.heap.stackFallback(4096, alloc);
    var cmd = internal_os.shell.ShellCommandBuilder.init(stack_fallback.get());
    defer cmd.deinit();

    // Iterator that yields each argument in the original command line.
    // This will allocate once proportionate to the command line length.
    var iter = try command.argIterator(alloc);
    defer iter.deinit();

    // Start accumulating arguments with the executable and initial flags.
    if (iter.next()) |exe| {
        try cmd.appendArg(exe);
    } else return null;
    try cmd.appendArg("--posix");

    // Stores the list of intercepted command line flags that will be passed
    // to our shell integration script: --norc --noprofile
    // We always include at least "1" so the script can differentiate between
    // being manually sourced or automatically injected (from here).
    var buf: [32]u8 = undefined;
    var inject: std.Io.Writer = .fixed(&buf);
    try inject.writeAll("1");

    // Walk through the rest of the given arguments. If we see an option that
    // would require complex or unsupported integration behavior, we bail out
    // and skip loading our shell integration. Users can still manually source
    // the shell integration script.
    //
    // Unsupported options:
    //  -c          -c is always non-interactive
    //  --posix     POSIX mode (a la /bin/sh)
    var rcfile: ?[]const u8 = null;
    while (iter.next()) |arg| {
        if (std.mem.eql(u8, arg, "--posix")) {
            return null;
        } else if (std.mem.eql(u8, arg, "--norc")) {
            try inject.writeAll(" --norc");
        } else if (std.mem.eql(u8, arg, "--noprofile")) {
            try inject.writeAll(" --noprofile");
        } else if (std.mem.eql(u8, arg, "--rcfile") or std.mem.eql(u8, arg, "--init-file")) {
            rcfile = iter.next();
        } else if (arg.len > 1 and arg[0] == '-' and arg[1] != '-') {
            // '-c command' is always non-interactive
            if (std.mem.indexOfScalar(u8, arg, 'c') != null) {
                return null;
            }
            try cmd.appendArg(arg);
        } else if (std.mem.eql(u8, arg, "-") or std.mem.eql(u8, arg, "--")) {
            // All remaining arguments should be passed directly to the shell
            // command. We shouldn't perform any further option processing.
            try cmd.appendArg(arg);
            while (iter.next()) |remaining_arg| {
                try cmd.appendArg(remaining_arg);
            }
            break;
        } else {
            try cmd.appendArg(arg);
        }
    }

    // Preserve an existing ENV value. We're about to overwrite it.
    if (env.get("ENV")) |v| {
        try env.put("GHOSTTY_BASH_ENV", v);
    }

    // Set our new ENV to point to our integration script.
    var script_path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const script_path = try std.fmt.bufPrint(
        &script_path_buf,
        "{s}/shell-integration/bash/ghostty.bash",
        .{resource_dir},
    );
    if (std.fs.openFileAbsolute(script_path, .{})) |file| {
        file.close();
        // The existence check above uses the Windows path; the env var
        // must carry the POSIX form so MSYS2/Cygwin bash can source it.
        try env.put("ENV", try shellEnvPath(alloc, script_path));
    } else |err| {
        log.warn("unable to open {s}: {}", .{ script_path, err });
        env.remove("GHOSTTY_BASH_ENV");
        return null;
    }

    try env.put("GHOSTTY_BASH_INJECT", buf[0..inject.end]);
    if (rcfile) |v| {
        try env.put("GHOSTTY_BASH_RCFILE", v);
    }

    // In POSIX mode, HISTFILE defaults to ~/.sh_history, so unless we're
    // staying in POSIX mode (--posix), change it back to ~/.bash_history.
    if (env.get("HISTFILE") == null) {
        var home_buf: [1024]u8 = undefined;
        if (try homedir.home(&home_buf)) |home| {
            var histfile_buf: [std.fs.max_path_bytes]u8 = undefined;
            const histfile = try std.fmt.bufPrint(
                &histfile_buf,
                "{s}/.bash_history",
                .{home},
            );
            try env.put("HISTFILE", histfile);
            try env.put("GHOSTTY_BASH_UNEXPORT_HISTFILE", "1");
        }
    }

    // Return a copy of our modified command line to use as the shell command.
    return .{ .shell = try alloc.dupeZ(u8, try cmd.toOwnedSlice()) };
}

test "bash" {
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .bash);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    const command = try setupBash(alloc, .{ .shell = "bash" }, res.path, &env);
    try testing.expectEqualStrings("bash --posix", command.?.shell);
    try testing.expectEqualStrings("1", env.get("GHOSTTY_BASH_INJECT").?);

    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    try testing.expectEqualStrings(
        // On Windows the ENV value is the Cygwin POSIX form; identity elsewhere.
        try shellEnvPath(alloc, try std.fmt.bufPrint(&path_buf, "{s}/ghostty.bash", .{res.shell_path})),
        env.get("ENV").?,
    );
}

test "bash: unsupported options" {
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .bash);
    defer res.deinit();

    const cmdlines = [_][:0]const u8{
        "bash --posix",
        "bash --rcfile script.sh --posix",
        "bash --init-file script.sh --posix",
        "bash -c script.sh",
        "bash -ic script.sh",
    };

    for (cmdlines) |cmdline| {
        var env = EnvMap.init(alloc);
        defer env.deinit();

        try testing.expect(try setupBash(alloc, .{ .shell = cmdline }, res.path, &env) == null);
        try testing.expectEqual(0, env.count());
    }
}

test "bash: inject flags" {
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .bash);
    defer res.deinit();

    // bash --norc
    {
        var env = EnvMap.init(alloc);
        defer env.deinit();

        const command = try setupBash(alloc, .{ .shell = "bash --norc" }, res.path, &env);
        try testing.expectEqualStrings("bash --posix", command.?.shell);
        try testing.expectEqualStrings("1 --norc", env.get("GHOSTTY_BASH_INJECT").?);
    }

    // bash --noprofile
    {
        var env = EnvMap.init(alloc);
        defer env.deinit();

        const command = try setupBash(alloc, .{ .shell = "bash --noprofile" }, res.path, &env);
        try testing.expectEqualStrings("bash --posix", command.?.shell);
        try testing.expectEqualStrings("1 --noprofile", env.get("GHOSTTY_BASH_INJECT").?);
    }
}

test "bash: rcfile" {
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .bash);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    // bash --rcfile
    {
        const command = try setupBash(alloc, .{ .shell = "bash --rcfile profile.sh" }, res.path, &env);
        try testing.expectEqualStrings("bash --posix", command.?.shell);
        try testing.expectEqualStrings("profile.sh", env.get("GHOSTTY_BASH_RCFILE").?);
    }

    // bash --init-file
    {
        const command = try setupBash(alloc, .{ .shell = "bash --init-file profile.sh" }, res.path, &env);
        try testing.expectEqualStrings("bash --posix", command.?.shell);
        try testing.expectEqualStrings("profile.sh", env.get("GHOSTTY_BASH_RCFILE").?);
    }
}

test "bash: HISTFILE" {
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .bash);
    defer res.deinit();

    // HISTFILE unset
    {
        var env = EnvMap.init(alloc);
        defer env.deinit();

        _ = try setupBash(alloc, .{ .shell = "bash" }, res.path, &env);
        try testing.expect(std.mem.endsWith(u8, env.get("HISTFILE").?, ".bash_history"));
        try testing.expectEqualStrings("1", env.get("GHOSTTY_BASH_UNEXPORT_HISTFILE").?);
    }

    // HISTFILE set
    {
        var env = EnvMap.init(alloc);
        defer env.deinit();

        try env.put("HISTFILE", "my_history");

        _ = try setupBash(alloc, .{ .shell = "bash" }, res.path, &env);
        try testing.expectEqualStrings("my_history", env.get("HISTFILE").?);
        try testing.expect(env.get("GHOSTTY_BASH_UNEXPORT_HISTFILE") == null);
    }
}

test "bash: ENV" {
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .bash);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    try env.put("ENV", "env.sh");

    _ = try setupBash(alloc, .{ .shell = "bash" }, res.path, &env);
    try testing.expectEqualStrings("env.sh", env.get("GHOSTTY_BASH_ENV").?);

    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    try testing.expectEqualStrings(
        // On Windows the ENV value is the Cygwin POSIX form; identity elsewhere.
        try shellEnvPath(alloc, try std.fmt.bufPrint(&path_buf, "{s}/ghostty.bash", .{res.shell_path})),
        env.get("ENV").?,
    );
}

test "bash: additional arguments" {
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .bash);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    // "-" argument separator
    {
        const command = try setupBash(alloc, .{ .shell = "bash - --arg file1 file2" }, res.path, &env);
        try testing.expectEqualStrings("bash --posix - --arg file1 file2", command.?.shell);
    }

    // "--" argument separator
    {
        const command = try setupBash(alloc, .{ .shell = "bash -- --arg file1 file2" }, res.path, &env);
        try testing.expectEqualStrings("bash --posix -- --arg file1 file2", command.?.shell);
    }
}

test "bash: missing resources" {
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var tmp_dir = testing.tmpDir(.{});
    defer tmp_dir.cleanup();

    const resources_dir = try tmp_dir.dir.realpathAlloc(alloc, ".");
    defer alloc.free(resources_dir);

    var env = EnvMap.init(alloc);
    defer env.deinit();

    try testing.expect(try setupBash(alloc, .{ .shell = "bash" }, resources_dir, &env) == null);
    try testing.expectEqual(0, env.count());
}

/// Setup automatic shell integration for shells that include
/// their modules from paths in `XDG_DATA_DIRS` env variable.
///
/// The shell-integration path is prepended to `XDG_DATA_DIRS`.
/// It is also saved in the `GHOSTTY_SHELL_INTEGRATION_XDG_DIR` variable
/// so that the shell can refer to it and safely remove this directory
/// from `XDG_DATA_DIRS` when integration is complete.
/// `cygwin` selects the path conventions of the target shell on Windows:
/// when true (MSYS2/Cygwin fish), the integration path is emitted in POSIX
/// form (`/c/...`) and joined with the POSIX `:` separator; when false
/// (native-capable elvish), the Windows path and native separator are kept.
/// On non-Windows platforms `cygwin` is irrelevant (paths are already
/// POSIX and the native separator is `:`).
fn setupXdgDataDirs(
    alloc: Allocator,
    resource_dir: []const u8,
    env: *EnvMap,
    cygwin: bool,
) !bool {
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;

    // Get our path to the shell integration directory.
    const integ_path = try std.fmt.bufPrint(
        &path_buf,
        "{s}/shell-integration",
        .{resource_dir},
    );
    var integ_dir = std.fs.openDirAbsolute(integ_path, .{}) catch |err| {
        log.warn("unable to open {s}: {}", .{ integ_path, err });
        return false;
    };
    integ_dir.close();

    // The existence check above used the Windows path; the env vars must
    // carry the POSIX form for a Cygwin/MSYS2 shell (e.g. fish under MSYS2),
    // and that shell joins XDG_DATA_DIRS with `:`, not the Windows `;`.
    const win_cygwin = builtin.os.tag == .windows and cygwin;
    const env_path: []const u8 = if (win_cygwin)
        try winToCygwinPath(alloc, integ_path)
    else
        integ_path;
    const delimiter: u8 = if (win_cygwin) ':' else std.fs.path.delimiter;

    // Set an env var so we can remove this from XDG_DATA_DIRS later.
    // This happens in the shell integration config itself. We do this
    // so that our modifications don't interfere with other commands.
    try env.put("GHOSTTY_SHELL_INTEGRATION_XDG_DIR", env_path);

    // If no XDG_DATA_DIRS set use the default value as specified.
    // This ensures that the default directories aren't lost by setting
    // our desired integration dir directly. See #2711.
    // <https://specifications.freedesktop.org/basedir-spec/0.6/#variables>
    const xdg_data_dirs_key = "XDG_DATA_DIRS";
    const current = env.get(xdg_data_dirs_key) orelse "/usr/local/share:/usr/share";
    const joined = if (current.len == 0)
        try alloc.dupe(u8, env_path)
    else
        try std.fmt.allocPrint(alloc, "{s}{c}{s}", .{ env_path, delimiter, current });
    try env.put(xdg_data_dirs_key, joined);

    return true;
}

test "xdg: empty XDG_DATA_DIRS" {
    if (builtin.os.tag == .windows) return error.SkipZigTest;

    const testing = std.testing;

    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .fish);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    try testing.expect(try setupXdgDataDirs(alloc, res.path, &env, true));

    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    try testing.expectEqualStrings(
        try std.fmt.bufPrint(&path_buf, "{s}/shell-integration", .{res.path}),
        env.get("GHOSTTY_SHELL_INTEGRATION_XDG_DIR").?,
    );
    try testing.expectEqualStrings(
        try std.fmt.bufPrint(&path_buf, "{s}/shell-integration:/usr/local/share:/usr/share", .{res.path}),
        env.get("XDG_DATA_DIRS").?,
    );
}

test "xdg: existing XDG_DATA_DIRS" {
    if (builtin.os.tag == .windows) return error.SkipZigTest;

    const testing = std.testing;

    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .fish);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    try env.put("XDG_DATA_DIRS", "/opt/share");

    try testing.expect(try setupXdgDataDirs(alloc, res.path, &env, true));

    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    try testing.expectEqualStrings(
        try std.fmt.bufPrint(&path_buf, "{s}/shell-integration", .{res.path}),
        env.get("GHOSTTY_SHELL_INTEGRATION_XDG_DIR").?,
    );
    try testing.expectEqualStrings(
        try std.fmt.bufPrint(&path_buf, "{s}/shell-integration:/opt/share", .{res.path}),
        env.get("XDG_DATA_DIRS").?,
    );
}

test "xdg: missing resources" {
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var tmp_dir = testing.tmpDir(.{});
    defer tmp_dir.cleanup();

    const resources_dir = try tmp_dir.dir.realpathAlloc(alloc, ".");
    defer alloc.free(resources_dir);

    var env = EnvMap.init(alloc);
    defer env.deinit();

    try testing.expect(!try setupXdgDataDirs(alloc, resources_dir, &env, false));
    try testing.expectEqual(0, env.count());
}

test "winToCygwinPath: drive-rooted path" {
    const testing = std.testing;
    const out = try winToCygwinPath(testing.allocator, "C:\\Users\\x\\share\\ghostty");
    defer testing.allocator.free(out);
    try testing.expectEqualStrings("/c/Users/x/share/ghostty", out);
}

test "winToCygwinPath: lowercases drive and handles forward slashes" {
    const testing = std.testing;
    const a = try winToCygwinPath(testing.allocator, "D:\\Foo");
    defer testing.allocator.free(a);
    try testing.expectEqualStrings("/d/Foo", a);
    const b = try winToCygwinPath(testing.allocator, "C:/a/b");
    defer testing.allocator.free(b);
    try testing.expectEqualStrings("/c/a/b", b);
}

test "winToCygwinPath: non-drive path just normalizes slashes" {
    const testing = std.testing;
    const out = try winToCygwinPath(testing.allocator, "relative\\path");
    defer testing.allocator.free(out);
    try testing.expectEqualStrings("relative/path", out);
}

test "winToCygwinPath: strips extended-length prefix" {
    const testing = std.testing;
    const out = try winToCygwinPath(testing.allocator, "\\\\?\\C:\\Users\\x");
    defer testing.allocator.free(out);
    try testing.expectEqualStrings("/c/Users/x", out);
}

test "winToCygwinPath: UNC paths map to //server/share" {
    const testing = std.testing;
    const ext = try winToCygwinPath(testing.allocator, "\\\\?\\UNC\\server\\share\\x");
    defer testing.allocator.free(ext);
    try testing.expectEqualStrings("//server/share/x", ext);

    const plain = try winToCygwinPath(testing.allocator, "\\\\server\\share");
    defer testing.allocator.free(plain);
    try testing.expectEqualStrings("//server/share", plain);
}

test "bash: ENV is a POSIX path on Windows" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .bash);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    _ = try setupBash(alloc, .{ .shell = "bash" }, res.path, &env);

    const v = env.get("ENV") orelse return error.NoEnv;
    try testing.expect(std.mem.startsWith(u8, v, "/"));
    try testing.expect(std.mem.indexOfScalar(u8, v, '\\') == null);
    try testing.expect(std.mem.indexOfScalar(u8, v, ':') == null);
    try testing.expect(std.mem.endsWith(u8, v, "/shell-integration/bash/ghostty.bash"));
}

test "zsh: ZDOTDIR is a POSIX path on Windows" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .zsh);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    _ = try setupZsh(alloc, .{ .shell = "zsh" }, res.path, &env);

    const v = env.get("ZDOTDIR") orelse return error.NoZdotdir;
    try testing.expect(std.mem.startsWith(u8, v, "/"));
    try testing.expect(std.mem.indexOfScalar(u8, v, '\\') == null);
    try testing.expect(std.mem.endsWith(u8, v, "/shell-integration/zsh"));
}

test "xdg fish: POSIX path and ':' delimiter on Windows" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .fish);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();
    try env.put("XDG_DATA_DIRS", "/usr/share");

    try testing.expect(try setupXdgDataDirs(alloc, res.path, &env, true));

    const dir = env.get("GHOSTTY_SHELL_INTEGRATION_XDG_DIR") orelse return error.NoXdgDir;
    try testing.expect(std.mem.startsWith(u8, dir, "/"));
    try testing.expect(std.mem.indexOfScalar(u8, dir, '\\') == null);

    const dirs = env.get("XDG_DATA_DIRS") orelse return error.NoXdgDirs;
    try testing.expect(std.mem.startsWith(u8, dirs, "/"));
    // POSIX join, and the prior value is preserved after a ':'.
    try testing.expect(std.mem.endsWith(u8, dirs, ":/usr/share"));
}

test "xdg elvish: keeps the Windows path on Windows" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .elvish);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    try testing.expect(try setupXdgDataDirs(alloc, res.path, &env, false));

    // Native elvish keeps the Windows path (drive-letter ':' present).
    const dir = env.get("GHOSTTY_SHELL_INTEGRATION_XDG_DIR") orelse return error.NoXdgDir;
    try testing.expect(std.mem.indexOfScalar(u8, dir, ':') != null);
}

/// Set up automatic Nushell shell integration. This works by adding our
/// shell resource directory to the `XDG_DATA_DIRS` environment variable,
/// which Nushell will use to load `nushell/vendor/autoload/ghostty.nu`.
///
/// We then add `--execute 'use ghostty ...'` to the nu command line to
/// automatically enable our shelll features.
fn setupNushell(
    alloc: Allocator,
    command: config.Command,
    resource_dir: []const u8,
    env: *EnvMap,
) !?config.Command {
    // Add our XDG_DATA_DIRS entry (for nushell/vendor/autoload/). This
    // makes our 'ghostty' module automatically available, even if any
    // of the later checks abort the rest of our automatic integration.
    if (!try setupXdgDataDirs(alloc, resource_dir, env, false)) return null;

    var stack_fallback = std.heap.stackFallback(4096, alloc);
    var cmd = internal_os.shell.ShellCommandBuilder.init(stack_fallback.get());
    defer cmd.deinit();

    // Iterator that yields each argument in the original command line.
    // This will allocate once proportionate to the command line length.
    var iter = try command.argIterator(alloc);
    defer iter.deinit();

    // Start accumulating arguments with the executable and initial flags.
    if (iter.next()) |exe| {
        try cmd.appendArg(exe);
    } else return null;

    // Tell nu to immediately "use" all of the exported functions in our
    // 'ghostty' module.
    //
    // We can consider making this more specific based on the set of
    // enabled shell features (e.g. `use ghostty sudo`). At the moment,
    // shell features are all runtime-guarded in the nushell script.
    try cmd.appendArg("--execute 'use ghostty *'");

    // Walk through the rest of the given arguments. If we see an option that
    // would require complex or unsupported integration behavior, we bail out
    // and skip loading our shell integration. Users can still manually source
    // the shell integration module.
    //
    // Unsupported options:
    //  -c / --command      -c is always non-interactive
    //  --lsp               --lsp starts the language server
    while (iter.next()) |arg| {
        if (std.mem.eql(u8, arg, "--command") or std.mem.eql(u8, arg, "--lsp")) {
            return null;
        } else if (arg.len > 1 and arg[0] == '-' and arg[1] != '-') {
            if (std.mem.indexOfScalar(u8, arg, 'c') != null) {
                return null;
            }
            try cmd.appendArg(arg);
        } else if (std.mem.eql(u8, arg, "-") or std.mem.eql(u8, arg, "--")) {
            // All remaining arguments should be passed directly to the shell
            // command. We shouldn't perform any further option processing.
            try cmd.appendArg(arg);
            while (iter.next()) |remaining_arg| {
                try cmd.appendArg(remaining_arg);
            }
            break;
        } else {
            try cmd.appendArg(arg);
        }
    }

    // Return a copy of our modified command line to use as the shell command.
    return .{ .shell = try alloc.dupeZ(u8, try cmd.toOwnedSlice()) };
}

test "nushell" {
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .nushell);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    const command = try setupNushell(alloc, .{ .shell = "nu" }, res.path, &env);
    try testing.expectEqualStrings("nu --execute 'use ghostty *'", command.?.shell);

    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    try testing.expectEqualStrings(
        try std.fmt.bufPrint(&path_buf, "{s}/shell-integration", .{res.path}),
        env.get("GHOSTTY_SHELL_INTEGRATION_XDG_DIR").?,
    );
    try testing.expectStringStartsWith(
        env.get("XDG_DATA_DIRS").?,
        try std.fmt.bufPrint(&path_buf, "{s}/shell-integration", .{res.path}),
    );
}

test "nushell: unsupported options" {
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .nushell);
    defer res.deinit();

    const cmdlines = [_][:0]const u8{
        "nu --command exit",
        "nu --lsp",
        "nu -c script.sh",
        "nu -ic script.sh",
    };

    for (cmdlines) |cmdline| {
        var env = EnvMap.init(alloc);
        defer env.deinit();

        try testing.expect(try setupNushell(alloc, .{ .shell = cmdline }, res.path, &env) == null);
        try testing.expect(env.get("XDG_DATA_DIRS") != null);
        try testing.expect(env.get("GHOSTTY_SHELL_INTEGRATION_XDG_DIR") != null);
    }
}

test "nushell: missing resources" {
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var tmp_dir = testing.tmpDir(.{});
    defer tmp_dir.cleanup();

    const resources_dir = try tmp_dir.dir.realpathAlloc(alloc, ".");
    defer alloc.free(resources_dir);

    var env = EnvMap.init(alloc);
    defer env.deinit();

    try testing.expect(try setupNushell(alloc, .{ .shell = "nu" }, resources_dir, &env) == null);
    try testing.expectEqual(0, env.count());
}

/// Setup PowerShell shell integration. PowerShell has no equivalent of
/// bash's `ENV` or zsh's `ZDOTDIR` to auto-source a script, so we always
/// export the absolute path to our integration script via the
/// `GHOSTTY_SHELL_INTEGRATION_PS1` environment variable. Users can opt in
/// manually by dot-sourcing that path from their `$PROFILE`.
///
/// For a bare interactive shell (the user configured just `pwsh` with no
/// arguments of their own) we go further and rewrite the launch command to
/// auto-source the script:
///
///     <pwsh> -NoExit -ExecutionPolicy Bypass -Command ". '<resource_dir>/.../ghostty.ps1'"
///
/// PowerShell still loads the user's `$PROFILE` first, then runs the
/// `-Command`, which dot-sources our script. The script wraps the
/// now-final prompt and emits the OSC 133 marks. If the user supplied
/// their own command or arguments we leave the command untouched so we
/// never clobber their invocation (the env var fallback still works).
fn setupPowerShell(
    alloc_arena: Allocator,
    command: config.Command,
    resource_dir: []const u8,
    env: *EnvMap,
) !?config.Command {
    // Use forward slashes for path composition to match the style of the
    // other shell-integration setup functions. PowerShell on Windows
    // accepts forward slashes in paths just fine.
    const script_path = try std.fmt.allocPrint(
        alloc_arena,
        "{s}/shell-integration/powershell/ghostty.ps1",
        .{resource_dir},
    );

    try env.put("GHOSTTY_SHELL_INTEGRATION_PS1", script_path);

    // Inspect the configured command. We auto-inject only when it's a
    // bare interactive shell: argv is exactly the executable with no
    // user-supplied arguments. Anything else (e.g. `pwsh -NoLogo` or a
    // `-Command`/`-File` invocation) is left alone so we don't override
    // what the user asked for.
    var iter = try command.argIterator(alloc_arena);
    defer iter.deinit();

    const exe = iter.next() orelse return null;
    // A second argument means the user provided their own command line.
    if (iter.next() != null) return try command.clone(alloc_arena);

    // Build the dot-source command. Single quotes keep the path literal
    // for PowerShell even if it contains spaces; this assumes resource_dir
    // (Ghostty-owned) never contains a single quote, which PowerShell would
    // otherwise require doubled. We emit a `.direct` command so the
    // `-Command` payload survives downstream argv parsing as a single
    // argument (a `.shell` string would be re-split on spaces, breaking the
    // dot-source expression).
    const dot_source = try std.fmt.allocPrintSentinel(
        alloc_arena,
        ". '{s}'",
        .{script_path},
        0,
    );

    // `-ExecutionPolicy Bypass` is process-scoped (it only affects the pwsh we
    // spawn, never the user's persisted machine/user policy) and is required
    // for correctness: Windows PowerShell 5.1 defaults to `Restricted` on
    // client Windows, under which dot-sourcing our `.ps1` fails with
    // "running scripts is disabled on this system", breaking integration and
    // printing a security error on every launch. The script we source is
    // Ghostty's own local file (no Mark-of-the-Web), so the bypass only
    // unblocks our trusted script.
    const argv = try alloc_arena.alloc([:0]const u8, 6);
    argv[0] = try alloc_arena.dupeZ(u8, exe);
    argv[1] = try alloc_arena.dupeZ(u8, "-NoExit");
    argv[2] = try alloc_arena.dupeZ(u8, "-ExecutionPolicy");
    argv[3] = try alloc_arena.dupeZ(u8, "Bypass");
    argv[4] = try alloc_arena.dupeZ(u8, "-Command");
    argv[5] = dot_source;

    return .{ .direct = argv };
}

test "powershell" {
    const testing = std.testing;

    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .powershell);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    // A bare `pwsh` is rewritten to auto-source the integration script.
    const command = try setupPowerShell(alloc, .{ .shell = "pwsh" }, res.path, &env);
    try testing.expect(command.? == .direct);
    const argv = command.?.direct;
    try testing.expectEqual(@as(usize, 6), argv.len);
    try testing.expectEqualStrings("pwsh", argv[0]);
    try testing.expectEqualStrings("-NoExit", argv[1]);
    // Process-scoped policy bypass so a default-Restricted Windows
    // PowerShell 5.1 can still dot-source our integration script.
    try testing.expectEqualStrings("-ExecutionPolicy", argv[2]);
    try testing.expectEqualStrings("Bypass", argv[3]);
    try testing.expectEqualStrings("-Command", argv[4]);
    // The dot-source argument references our integration script.
    try testing.expect(std.mem.indexOf(u8, argv[5], "ghostty.ps1") != null);
    try testing.expect(std.mem.startsWith(u8, argv[5], ". '"));

    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    try testing.expectEqualStrings(
        try std.fmt.bufPrint(&path_buf, "{s}/ghostty.ps1", .{res.shell_path}),
        env.get("GHOSTTY_SHELL_INTEGRATION_PS1").?,
    );
}

test "powershell: user-supplied args are left untouched" {
    const testing = std.testing;

    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .powershell);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    // The user gave their own arguments, so we don't rewrite the command;
    // we only export the opt-in env var.
    const command = try setupPowerShell(
        alloc,
        .{ .shell = "pwsh -NoLogo -NoProfile" },
        res.path,
        &env,
    );
    try testing.expect(command.? == .shell);
    try testing.expectEqualStrings("pwsh -NoLogo -NoProfile", command.?.shell);

    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    try testing.expectEqualStrings(
        try std.fmt.bufPrint(&path_buf, "{s}/ghostty.ps1", .{res.shell_path}),
        env.get("GHOSTTY_SHELL_INTEGRATION_PS1").?,
    );
}

/// Setup cmd.exe shell integration. cmd has no rc file or pre/post-exec
/// hooks, but it re-expands the PROMPT env var on every prompt and supports
/// `$e` (ESC) on Windows 10+. We wrap the prompt body in OSC 133;A / 133;B
/// (prompt-start / input-start) and report cwd via OSC 9;9. PROMPT cannot
/// emit command start/end (C/D) marks. Unlike the script-based shells, cmd
/// has no way to read GHOSTTY_SHELL_FEATURES at runtime, so these marks are
/// always emitted (the gated features do not apply to cmd anyway).
///
/// For the missing C/D marks we additionally prepend our shell-integration
/// "cmd" directory to CLINK_PATH. When the user runs Clink, it autoloads
/// `ghostty.lua` from there and emits OSC 133;C / 133;D;<code> to complement
/// the PROMPT-based A/B marks. CLINK_PATH is a native Windows path list
/// (`;`-separated), so no POSIX path conversion is involved, and it is a
/// harmless no-op when Clink is not installed. We skip it when the
/// integration directory is missing (e.g. a build without resources).
fn setupCmd(
    alloc_arena: Allocator,
    command: config.Command,
    resource_dir: []const u8,
    env: *EnvMap,
) !?config.Command {
    // Preserve the user's prompt body if set, else cmd's default `$p$g`.
    const body = env.get("PROMPT") orelse "$p$g";
    // `$e` = ESC, terminator ST = `$e\`. OSC 9;9 carries cwd via `$p`.
    const wrapped = try std.fmt.allocPrint(
        alloc_arena,
        "$e]133;A$e\\$e]9;9;$p$e\\{s}$e]133;B$e\\",
        .{body},
    );
    try env.put("PROMPT", wrapped);

    // Forward slashes are fine for Clink on Windows, matching the other
    // setup functions' path style.
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const clink_dir = try std.fmt.bufPrint(
        &path_buf,
        "{s}/shell-integration/cmd",
        .{resource_dir},
    );
    if (std.fs.openDirAbsolute(clink_dir, .{})) |dir_| {
        var dir = dir_;
        dir.close();
        try env.put(
            "CLINK_PATH",
            // CLINK_PATH is a Windows-format list and is always
            // ';'-separated, independent of the host (Clink only runs on
            // Windows; using the host path delimiter would emit ':' under
            // unit tests on POSIX).
            try internal_os.prependEnv(
                alloc_arena,
                env.get("CLINK_PATH") orelse "",
                clink_dir,
                ';',
            ),
        );
    } else |err| switch (err) {
        // No integration dir is the expected case for a build without
        // resources (e.g. lib-only): base PROMPT marks still apply and
        // Clink users just won't get the C/D marks. Don't warn on every
        // cmd launch for it; reserve warn for genuine failures.
        error.FileNotFound => log.debug("cmd: no clink integration dir at {s}", .{clink_dir}),
        else => log.warn("cmd: clink integration dir unavailable {s}: {}", .{ clink_dir, err }),
    }

    return try command.clone(alloc_arena);
}

test "cmd: PROMPT carries OSC 133 marks" {
    const testing = std.testing;
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .cmd);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    _ = try setupCmd(alloc, .{ .shell = "cmd.exe" }, res.path, &env);

    const prompt = env.get("PROMPT") orelse return error.NoPrompt;
    try testing.expect(std.mem.indexOf(u8, prompt, "133;A") != null);
    try testing.expect(std.mem.indexOf(u8, prompt, "133;B") != null);
    try testing.expect(std.mem.indexOf(u8, prompt, "9;9") != null);
}

test "cmd: preserves existing PROMPT body" {
    const testing = std.testing;
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .cmd);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    try env.put("PROMPT", "$p$g$s");

    _ = try setupCmd(alloc, .{ .shell = "cmd.exe" }, res.path, &env);

    const prompt = env.get("PROMPT") orelse return error.NoPrompt;
    try testing.expect(std.mem.indexOf(u8, prompt, "$p$g$s") != null);
    try testing.expect(std.mem.indexOf(u8, prompt, "133;A") != null);
    try testing.expect(std.mem.indexOf(u8, prompt, "133;B") != null);
}

test "cmd: CLINK_PATH includes the shell-integration cmd dir" {
    const testing = std.testing;
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .cmd);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    _ = try setupCmd(alloc, .{ .shell = "cmd.exe" }, res.path, &env);

    // Exact match: with no prior CLINK_PATH, ours is the sole entry.
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    try testing.expectEqualStrings(
        try std.fmt.bufPrint(&path_buf, "{s}/shell-integration/cmd", .{res.path}),
        env.get("CLINK_PATH") orelse return error.NoClinkPath,
    );
}

test "cmd: CLINK_PATH prepends ours and preserves existing entries" {
    const testing = std.testing;
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(alloc, .cmd);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    try env.put("CLINK_PATH", "C:\\my\\scripts");

    _ = try setupCmd(alloc, .{ .shell = "cmd.exe" }, res.path, &env);

    const clink = env.get("CLINK_PATH") orelse return error.NoClinkPath;
    // Windows path-list delimiter is ';'. Ours is prepended.
    try testing.expect(std.mem.endsWith(u8, clink, "C:\\my\\scripts"));
    try testing.expect(std.mem.indexOf(u8, clink, ";") != null);
    try testing.expect(std.mem.indexOf(u8, clink, "shell-integration") != null);
}

test "cmd: CLINK_PATH is left unset when the cmd integration dir is absent" {
    const testing = std.testing;
    var arena = std.heap.ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    // A resources dir whose shell-integration has bash but no cmd/.
    var res: TmpResourcesDir = try .init(alloc, .bash);
    defer res.deinit();

    var env = EnvMap.init(alloc);
    defer env.deinit();

    _ = try setupCmd(alloc, .{ .shell = "cmd.exe" }, res.path, &env);

    try testing.expect(env.get("PROMPT") != null);
    try testing.expect(env.get("CLINK_PATH") == null);
}

/// Setup the zsh automatic shell integration. This works by setting
/// ZDOTDIR to our resources dir so that zsh will load our config. This
/// config then loads the true user config.
fn setupZsh(
    alloc: Allocator,
    command: config.Command,
    resource_dir: []const u8,
    env: *EnvMap,
) !?config.Command {
    // Preserve an existing ZDOTDIR value. We're about to overwrite it.
    if (env.get("ZDOTDIR")) |old| {
        try env.put("GHOSTTY_ZSH_ZDOTDIR", old);
    }

    // Set our new ZDOTDIR to point to our shell resource directory.
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const integ_path = try std.fmt.bufPrint(
        &path_buf,
        "{s}/shell-integration/zsh",
        .{resource_dir},
    );
    var integ_dir = std.fs.openDirAbsolute(integ_path, .{}) catch |err| {
        log.warn("unable to open {s}: {}", .{ integ_path, err });
        return null;
    };
    integ_dir.close();
    // Existence check uses the Windows path; the env var carries the POSIX
    // form so MSYS2/Cygwin zsh resolves ZDOTDIR.
    try env.put("ZDOTDIR", try shellEnvPath(alloc, integ_path));

    return try command.clone(alloc);
}

test "zsh" {
    const testing = std.testing;

    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(testing.allocator, .zsh);
    defer res.deinit();

    var env = EnvMap.init(testing.allocator);
    defer env.deinit();

    const command = try setupZsh(alloc, .{ .shell = "zsh" }, res.path, &env);
    try testing.expectEqualStrings("zsh", command.?.shell);
    try testing.expectEqualStrings(try shellEnvPath(alloc, res.shell_path), env.get("ZDOTDIR").?);
    try testing.expect(env.get("GHOSTTY_ZSH_ZDOTDIR") == null);
}

test "zsh: ZDOTDIR" {
    const testing = std.testing;

    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(testing.allocator, .zsh);
    defer res.deinit();

    var env = EnvMap.init(testing.allocator);
    defer env.deinit();

    try env.put("ZDOTDIR", "$HOME/.config/zsh");

    const command = try setupZsh(alloc, .{ .shell = "zsh" }, res.path, &env);
    try testing.expectEqualStrings("zsh", command.?.shell);
    try testing.expectEqualStrings(try shellEnvPath(alloc, res.shell_path), env.get("ZDOTDIR").?);
    try testing.expectEqualStrings("$HOME/.config/zsh", env.get("GHOSTTY_ZSH_ZDOTDIR").?);
}

test "zsh: missing resources" {
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var tmp_dir = testing.tmpDir(.{});
    defer tmp_dir.cleanup();

    const resources_dir = try tmp_dir.dir.realpathAlloc(alloc, ".");
    defer alloc.free(resources_dir);

    var env = EnvMap.init(alloc);
    defer env.deinit();

    try testing.expect(try setupZsh(alloc, .{ .shell = "zsh" }, resources_dir, &env) == null);
    try testing.expectEqual(0, env.count());
}

/// Setup automatic shell integration for fish.
///
/// On every platform we export the shell-integration dir via XDG_DATA_DIRS
/// (Linux fish derives $__fish_vendor_confdirs from it, and the integration
/// script's own ghostty_restore_xdg_data_dir cleanup needs
/// GHOSTTY_SHELL_INTEGRATION_XDG_DIR + XDG_DATA_DIRS).
///
/// On Windows, fish is always MSYS2/Cygwin (there is no native Windows fish
/// build), and its $__fish_vendor_confdirs is a fixed set that ignores the
/// runtime XDG_DATA_DIRS — so the vendor conf is never auto-loaded there.
/// We deliver it explicitly by appending `-C "source $GHOSTTY_SHELL_INTEGRATION_XDG_DIR/.../ghostty-shell-integration.fish"`
/// (see the inline comment below for why the path is referenced via the env
/// var rather than embedded). `-C` (--init-command) runs after fish reads its
/// configuration but before interactive input, so sourcing the file registers
/// its `--on-event fish_prompt` handler normally — the same end state as
/// Linux's auto-load.
fn setupFish(
    alloc: Allocator,
    command: config.Command,
    resource_dir: []const u8,
    env: *EnvMap,
) !?config.Command {
    if (!try setupXdgDataDirs(alloc, resource_dir, env, true)) return null;

    if (comptime builtin.os.tag == .windows) {
        var stack_fallback = std.heap.stackFallback(4096, alloc);
        var cmd = internal_os.shell.ShellCommandBuilder.init(stack_fallback.get());
        defer cmd.deinit();

        // Preserve the original command line.
        var iter = try command.argIterator(alloc);
        defer iter.deinit();
        while (iter.next()) |arg| try cmd.appendArg(arg);

        // Append `-C "source $GHOSTTY_SHELL_INTEGRATION_XDG_DIR/.../ghostty-shell-integration.fish"`.
        // The outer double quotes make ArgIteratorGeneral (single_quotes =
        // false) re-parse the value as a single argument.
        //
        // We reference the vendor conf through the
        // GHOSTTY_SHELL_INTEGRATION_XDG_DIR env var (set above by
        // setupXdgDataDirs, in POSIX form) instead of embedding the literal
        // path, for two reasons:
        //   1. The spawned `.shell` command line then carries no filesystem
        //      path. Embedding one risks cmd.exe metacharacters (`( )` from
        //      `Program Files (x86)`, `%`, `!`) tripping
        //      windowsShellNeedsCmdWrapping in Exec.zig, which would route the
        //      whole command through `cmd.exe /C` — making cmd.exe (not fish)
        //      the spawned process. `$VAR` is expanded by fish at runtime, not
        //      on the spawn-side command line, so no path metacharacter ever
        //      reaches that check.
        //   2. fish does not word-split variable expansions, so a path with
        //      spaces resolves as a single token natively.
        try cmd.appendArg("-C");
        try cmd.appendArg(
            "\"source $GHOSTTY_SHELL_INTEGRATION_XDG_DIR/fish/vendor_conf.d/ghostty-shell-integration.fish\"",
        );

        return .{ .shell = try alloc.dupeZ(u8, try cmd.toOwnedSlice()) };
    }

    // Non-Windows: the XDG export above is sufficient (Linux/macOS fish).
    return try command.clone(alloc);
}

test "fish" {
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var res: TmpResourcesDir = try .init(testing.allocator, .fish);
    defer res.deinit();

    var env = EnvMap.init(testing.allocator);
    defer env.deinit();

    const command = try setupFish(alloc, .{ .shell = "fish -i" }, res.path, &env);

    // The XDG dir is exported on every platform; the integration script's own
    // ghostty_restore_xdg_data_dir cleanup relies on it.
    try testing.expect(env.get("GHOSTTY_SHELL_INTEGRATION_XDG_DIR") != null);

    if (comptime builtin.os.tag == .windows) {
        // Windows fish is always MSYS2/Cygwin, whose $__fish_vendor_confdirs
        // ignores runtime XDG_DATA_DIRS, so the vendor conf is delivered
        // explicitly via `-C source`. The path is referenced through the
        // GHOSTTY_SHELL_INTEGRATION_XDG_DIR env var rather than embedded, so
        // the command line carries no filesystem path that could trip the
        // cmd.exe metacharacter check in Exec.zig (e.g. `Program Files (x86)`).
        try testing.expectEqualStrings(
            "fish -i -C \"source $GHOSTTY_SHELL_INTEGRATION_XDG_DIR/fish/vendor_conf.d/ghostty-shell-integration.fish\"",
            command.?.shell,
        );
        // Regression guard: no resource path is embedded in the command, so
        // a metacharacter in the install path can never reach windowsShell-
        // NeedsCmdWrapping (which would force a cmd.exe wrapper).
        try testing.expect(std.mem.indexOfScalar(u8, command.?.shell, '(') == null);
    } else {
        // Other platforms: command unchanged; the XDG export does the work.
        try testing.expectEqualStrings("fish -i", command.?.shell);
    }
}

test "fish: missing resources" {
    const testing = std.testing;
    var arena = ArenaAllocator.init(testing.allocator);
    defer arena.deinit();
    const alloc = arena.allocator();

    var tmp_dir = testing.tmpDir(.{});
    defer tmp_dir.cleanup();

    const resources_dir = try tmp_dir.dir.realpathAlloc(alloc, ".");
    defer alloc.free(resources_dir);

    var env = EnvMap.init(alloc);
    defer env.deinit();

    try testing.expect(try setupFish(alloc, .{ .shell = "fish" }, resources_dir, &env) == null);
    try testing.expectEqual(0, env.count());
}

/// Test helper that creates a temporary resources directory with shell integration paths.
const TmpResourcesDir = struct {
    allocator: Allocator,
    tmp_dir: std.testing.TmpDir,
    path: []const u8,
    shell_path: []const u8,

    fn init(allocator: Allocator, shell: Shell) !TmpResourcesDir {
        var tmp_dir = std.testing.tmpDir(.{});
        errdefer tmp_dir.cleanup();

        var path_buf: [std.fs.max_path_bytes]u8 = undefined;
        const relative_shell_path = try std.fmt.bufPrint(
            &path_buf,
            "shell-integration/{s}",
            .{@tagName(shell)},
        );
        try tmp_dir.dir.makePath(relative_shell_path);

        const path = try tmp_dir.dir.realpathAlloc(allocator, ".");
        errdefer allocator.free(path);

        const shell_path = try std.fmt.allocPrint(
            allocator,
            "{s}/{s}",
            .{ path, relative_shell_path },
        );
        errdefer allocator.free(shell_path);

        switch (shell) {
            .bash => try tmp_dir.dir.writeFile(.{
                .sub_path = "shell-integration/bash/ghostty.bash",
                .data = "",
            }),
            else => {},
        }

        return .{
            .allocator = allocator,
            .tmp_dir = tmp_dir,
            .path = path,
            .shell_path = shell_path,
        };
    }

    fn deinit(self: *TmpResourcesDir) void {
        self.allocator.free(self.shell_path);
        self.allocator.free(self.path);
        self.tmp_dir.cleanup();
    }
};
