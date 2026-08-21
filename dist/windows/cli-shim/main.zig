//! Console launcher for the GUI Wintty binary.
//!
//! Wintty.exe is a GUI-subsystem binary so that launching it from the
//! Start menu or Explorer does not flash a loader-created console before
//! the splash. Windows decides whether a shell waits for a process from
//! that subsystem byte and nothing else, so `wintty +version` typed at a
//! prompt returns to the prompt immediately and prints afterwards, which
//! reads as a hang or a lost command.
//!
//! One binary cannot have both. This is the other half: a console-
//! subsystem launcher that forwards its arguments to the real
//! executable, lets it inherit the console it was given, waits for it,
//! and exits with its exit code. `code.cmd` and `wt.exe` are the same
//! pattern.
//!
//! It lives one directory below the app so that both can be called
//! `wintty` without colliding on a case-insensitive filesystem.

const std = @import("std");

const app_name = "Wintty.exe";

pub fn main(init: std.process.Init) !u8 {
    const io = init.io;
    const arena = init.arena.allocator();

    const exe_dir = try std.process.executableDirPathAlloc(io, arena);
    const app_path = try std.fs.path.join(arena, &.{ exe_dir, "..", app_name });

    const args = try init.minimal.args.toSlice(arena);
    if (args.len == 0) return 1;

    // argv[0] becomes the real executable; everything the caller typed
    // after our own name passes through untouched, so quoting and flags
    // reach libghostty's CLI exactly as written.
    const argv = try arena.alloc([]const u8, args.len);
    argv[0] = app_path;
    for (args[1..], 1..) |arg, i| argv[i] = arg;

    // stdin, stdout and stderr all default to .inherit, which is the
    // entire point of this binary: the child writes to the console this
    // process was handed rather than to one of its own.
    var child = std.process.spawn(io, .{ .argv = argv }) catch |err| {
        std.debug.print(
            "wintty: cannot start {s}: {s}\n",
            .{ app_path, @errorName(err) },
        );
        return 1;
    };

    // Wait for a CLI action, get out of the way for a launch.
    //
    // `wintty +show-config` is a command and the shell has to wait for
    // it, which is this binary's whole reason to exist. `wintty` on its
    // own opens a terminal window, and holding the calling shell hostage
    // until the user closes that window would make the launcher worse
    // than calling the GUI binary directly.
    if (!isCliAction(argv)) return 0;

    const term = try child.wait(io);
    return switch (term) {
        .exited => |code| code,
        // The child did not exit on its own terms, and reporting 0 would
        // tell a script it succeeded. Listed rather than caught by an
        // `else` so a new Term variant is a compile error here.
        .signal, .stopped, .unknown => 1,
    };
}

/// Every bare-word subcommand the app accepts without a `+`.
///
/// A copy, and it has to be: this binary decides whether to wait before
/// the app has run, so it cannot ask. The copy is pinned to the original
/// by CliShimParityTests, which parses this array out of this file and
/// compares it with `CliAliases.Actions` - the same technique
/// CliAliasParityTests already uses to pin that table to the Action enum
/// in src/cli/ghostty.zig. Keep the marker comments; the test finds the
/// array by them.
// wintty:aliases:begin
const aliases = [_][]const u8{
    "boo",
    "crash-report",
    "edit-config",
    "explain-config",
    "help",
    "list-actions",
    "list-colors",
    "list-fonts",
    "list-keybinds",
    "list-themes",
    "list-themes-tui",
    "new-tab",
    "new-window",
    "show-config",
    "show-face",
    "ssh",
    "ssh-cache",
    "toggle-quick-terminal",
    "validate-config",
    "version",
};
// wintty:aliases:end

/// Whether this invocation is a CLI action rather than a launch.
///
/// Mirrors what Program.MainImpl treats as a command: a `+verb`, one of
/// the bare-word aliases above, the version spellings it intercepts
/// directly, and a help request. Getting this wrong in the permissive
/// direction returns the prompt before the output, which is the symptom
/// this binary exists to remove; getting it wrong the other way holds a
/// shell until the user closes a terminal window. The first is annoying
/// and the second is broken, so anything uncertain stays out.
fn isCliAction(argv: []const []const u8) bool {
    if (argv.len < 2) return false;
    const first = argv[1];

    if (std.mem.startsWith(u8, first, "+")) return true;

    // `version` and `help` are in the alias table; these two are not,
    // and Program.MainImpl intercepts them before libghostty sees argv.
    if (std.mem.eql(u8, first, "--version") or std.mem.eql(u8, first, "-v")) return true;

    for (aliases) |alias| {
        if (std.mem.eql(u8, first, alias)) return true;
    }

    // Help, with the same `-e` rule the C# side applies: -e hands the
    // rest of the line to a child command, so a help flag after it
    // belongs to that command and this is a launch.
    for (argv[1..]) |arg| {
        if (std.mem.eql(u8, arg, "-e")) return false;
        if (std.mem.eql(u8, arg, "--help") or
            std.mem.eql(u8, arg, "-h") or
            std.mem.eql(u8, arg, "/?")) return true;
    }

    return false;
}
