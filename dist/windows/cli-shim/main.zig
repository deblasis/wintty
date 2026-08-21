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
        // Signalled, stopped or unknown: the child did not exit on its
        // own terms, and reporting 0 would tell a script it succeeded.
        else => 1,
    };
}

/// Whether this invocation is a CLI action rather than a launch.
///
/// The `+verb` spelling is libghostty's own, and Program.cs dispatches
/// on exactly the same test, so the two agree on what counts without
/// this binary needing to know any of the verbs.
fn isCliAction(argv: []const []const u8) bool {
    if (argv.len < 2) return false;
    return std.mem.startsWith(u8, argv[1], "+");
}
