//! VT-awareness classification for Windows shell executables.
//!
//! This is orthogonal to src/termio/shell_integration.zig's `Shell`
//! enum: `Shell` identifies bash/zsh/etc for RC-file injection;
//! `Awareness` says whether the shell speaks VT natively or uses the
//! Win32 Console API. A shell can be recognized here without being
//! recognized there (e.g. `wsl.exe`) and vice versa.
//!
//! The C# port at windows/Ghostty.Core/Shell/ShellDetector.cs keeps
//! the same table; edit both together until the C# side is retired.

const std = @import("std");
const testing = std.testing;
const log = std.log.scoped(.windows_shell);

pub const Awareness = enum {
    unknown,
    vt_aware,
    console_api,
};

const known = std.StaticStringMap(Awareness).initComptime(.{
    .{ "pwsh", .vt_aware },
    .{ "wsl", .vt_aware },
    .{ "ssh", .vt_aware },
    .{ "bash", .vt_aware },
    .{ "nu", .vt_aware },
    .{ "zsh", .vt_aware },
    .{ "fish", .vt_aware },
    .{ "elvish", .vt_aware },
    .{ "xonsh", .vt_aware },
    .{ "cmd", .console_api },
    .{ "powershell", .console_api },
});

/// Classify an executable path or single-token command string. Strips
/// surrounding quotes, directory prefix, and a trailing `.exe`
/// suffix, then matches case-insensitively against the known table.
/// Returns `.unknown` for anything unrecognized.
///
/// This function does not parse argv flags. Callers with a full
/// command line should split off the first token before calling.
pub fn classify(exe_path: []const u8) Awareness {
    const trimmed = std.mem.trim(u8, exe_path, "\"' \t\r\n");
    if (trimmed.len == 0) return .unknown;

    // Last path separator (forward or back slash).
    const base_start = blk: {
        var i: usize = trimmed.len;
        while (i > 0) : (i -= 1) {
            const c = trimmed[i - 1];
            if (c == '\\' or c == '/') break :blk i;
        }
        break :blk 0;
    };
    var base = trimmed[base_start..];

    // Strip trailing .exe case-insensitively.
    if (base.len >= 4 and std.ascii.eqlIgnoreCase(base[base.len - 4 ..], ".exe")) {
        base = base[0 .. base.len - 4];
    }

    // StaticStringMap is case-sensitive; lowercase into a stack buffer.
    var buf: [64]u8 = undefined;
    if (base.len > buf.len) {
        // Any realistic shell basename fits; log for diagnosability.
        log.debug("shell basename too long ({d}B) - treating as unknown", .{base.len});
        return .unknown;
    }
    const lower = std.ascii.lowerString(buf[0..base.len], base);

    return known.get(lower) orelse .unknown;
}

test "classify: pwsh variants" {
    try testing.expectEqual(Awareness.vt_aware, classify("pwsh"));
    try testing.expectEqual(Awareness.vt_aware, classify("pwsh.exe"));
    try testing.expectEqual(Awareness.vt_aware, classify("PWSH.EXE"));
    try testing.expectEqual(Awareness.vt_aware, classify("C:\\Program Files\\PowerShell\\7\\pwsh.exe"));
}

test "classify: wsl, ssh, bash" {
    try testing.expectEqual(Awareness.vt_aware, classify("wsl.exe"));
    try testing.expectEqual(Awareness.vt_aware, classify("ssh.exe"));
    try testing.expectEqual(Awareness.vt_aware, classify("bash.exe"));
    try testing.expectEqual(Awareness.vt_aware, classify("C:\\Windows\\System32\\wsl.exe"));
}

test "classify: nu, zsh, fish" {
    try testing.expectEqual(Awareness.vt_aware, classify("nu.exe"));
    try testing.expectEqual(Awareness.vt_aware, classify("zsh"));
    try testing.expectEqual(Awareness.vt_aware, classify("fish"));
}

test "classify: elvish, xonsh" {
    try testing.expectEqual(Awareness.vt_aware, classify("elvish.exe"));
    try testing.expectEqual(Awareness.vt_aware, classify("xonsh"));
}

test "classify: cmd.exe is console_api" {
    try testing.expectEqual(Awareness.console_api, classify("cmd"));
    try testing.expectEqual(Awareness.console_api, classify("cmd.exe"));
    try testing.expectEqual(Awareness.console_api, classify("CMD.EXE"));
    try testing.expectEqual(Awareness.console_api, classify("C:\\Windows\\System32\\cmd.exe"));
}

test "classify: powershell 5.1 is console_api" {
    try testing.expectEqual(Awareness.console_api, classify("powershell"));
    try testing.expectEqual(Awareness.console_api, classify("powershell.exe"));
    try testing.expectEqual(Awareness.console_api, classify("PowerShell.exe"));
}

test "classify: unknown returns unknown" {
    try testing.expectEqual(Awareness.unknown, classify("my-custom-repl.exe"));
    try testing.expectEqual(Awareness.unknown, classify("python.exe"));
    try testing.expectEqual(Awareness.unknown, classify("notepad.exe"));
}

test "classify: strips surrounding quotes" {
    try testing.expectEqual(Awareness.vt_aware, classify("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\""));
    try testing.expectEqual(Awareness.console_api, classify("'cmd.exe'"));
}

test "classify: handles forward slashes" {
    try testing.expectEqual(Awareness.vt_aware, classify("C:/Program Files/PowerShell/7/pwsh.exe"));
}

test "classify: empty and whitespace" {
    try testing.expectEqual(Awareness.unknown, classify(""));
    try testing.expectEqual(Awareness.unknown, classify("   "));
    try testing.expectEqual(Awareness.unknown, classify("\t\n"));
}

test "classify: handles very long path safely" {
    // Longer than the 64-byte lowercase buffer. Must return .unknown
    // instead of crashing or false-matching.
    var long_path: [128]u8 = undefined;
    @memset(&long_path, 'a');
    try testing.expectEqual(Awareness.unknown, classify(&long_path));
}
