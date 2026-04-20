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

/// UTF-8 preamble kind needed to make a shell's *initial* output land
/// as UTF-8 when it runs under ConPTY. Separate from `Awareness`
/// because we need to distinguish cmd from powershell (same awareness,
/// different preamble) and pwsh from the other vt_aware shells
/// (same awareness, but only powershell-family benefits from the
/// setup under forced conpty-mode=never - see # 302).
///
/// The setup runs once at shell startup inside ConPTY's conhost.exe,
/// which does not inherit the caller's console codepage.
pub const Preamble = enum {
    /// No preamble: either the shell is unknown, or it already handles
    /// its own encoding (e.g. wsl / bash / nu all decode their own
    /// output regardless of the Windows console CP).
    none,
    /// cmd.exe: run `chcp 65001 >nul` at startup and stay interactive.
    cmd,
    /// PowerShell (pwsh.exe or Windows PowerShell 5.1): assign
    /// `[Console]::OutputEncoding` and `InputEncoding` before the
    /// prompt appears.
    pwsh,

    /// Argv elements to append after the user's existing argv so that
    /// the configured shell runs the UTF-8 setup at startup. String
    /// literals live in `.rodata`, so callers using an arena for argv
    /// can append the returned slices directly without duping.
    pub fn suffix(self: Preamble) []const [:0]const u8 {
        return switch (self) {
            .none => &.{},
            .cmd => &cmd_suffix,
            .pwsh => &pwsh_suffix,
        };
    }

    /// Text to prepend to a user-supplied script when the user already
    /// consumed the shell's "rest of command line" slot (e.g. `cmd /C
    /// <script>`, `pwsh -Command <script>`). The returned slice is an
    /// empty string for `.none`; otherwise it ends in whatever statement
    /// terminator the shell needs so the caller can just concatenate it
    /// in front of the user's script. See `suffix` for the
    /// non-conflicting argv-append form.
    ///
    /// SECURITY: the returned strings are compile-time constants. Do
    /// not interpolate user input into a new prefix string - that
    /// would turn this into a shell-injection sink.
    ///
    /// The pwsh prefix uses `[System.Text.UTF8Encoding]::new()` whose
    /// parameterless ctor defaults to `encoderShouldEmitUTF8Identifier
    /// = false` (no BOM) and `throwOnInvalidBytes = false` (lenient
    /// decode - U+FFFD substitution on malformed bytes). Both are the
    /// right choice for a terminal; do not switch to
    /// `[Encoding]::UTF8` or a stricter ctor without understanding the
    /// BOM side effects on piped output.
    pub fn prefix(self: Preamble) []const u8 {
        return switch (self) {
            .none => "",
            // cmd's `&&` only runs the user's script when chcp
            // succeeded. chcp 65001 has no failure modes on supported
            // Windows SKUs; the `&&` variant matches the shell-wrap
            // path in Exec.zig so both entrypoints behave identically
            // if a future SKU ever breaks chcp. `>nul` silences the
            // "Active code page: 65001" banner.
            .cmd => "chcp 65001 >nul && ",
            // `;` chains statements in PowerShell. Output encoding
            // first, then input so piped stdout and redirected stdin
            // match. See `suffix` for why we set both.
            .pwsh => "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new(); [Console]::InputEncoding = [Console]::OutputEncoding; ",
        };
    }

    const cmd_suffix = [_][:0]const u8{ "/K", "chcp 65001 >nul" };
    const pwsh_suffix = [_][:0]const u8{
        "-NoExit",
        "-Command",
        // Set both output *and* input encodings: the output side fixes
        // what the pane renders; the input side fixes what redirection
        // (`>`, `|`) produces when the user pipes pwsh into another
        // tool.
        "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new(); [Console]::InputEncoding = [Console]::OutputEncoding",
    };
};

/// Fine-grained shell identity used to select a UTF-8 preamble under
/// ConPTY. Kept internal; exposed only through `utf8Preamble`.
const Kind = enum {
    unknown,
    cmd,
    powershell,
    pwsh,
    wsl,
    ssh,
    bash,
    nu,
    zsh,
    fish,
    elvish,
    xonsh,
};

const kinds = std.StaticStringMap(Kind).initComptime(.{
    .{ "pwsh", .pwsh },
    .{ "wsl", .wsl },
    .{ "ssh", .ssh },
    .{ "bash", .bash },
    .{ "nu", .nu },
    .{ "zsh", .zsh },
    .{ "fish", .fish },
    .{ "elvish", .elvish },
    .{ "xonsh", .xonsh },
    .{ "cmd", .cmd },
    .{ "powershell", .powershell },
});

fn awarenessOf(kind: Kind) Awareness {
    return switch (kind) {
        .unknown => .unknown,
        .cmd, .powershell => .console_api,
        .pwsh, .wsl, .ssh, .bash, .nu, .zsh, .fish, .elvish, .xonsh => .vt_aware,
    };
}

fn preambleOf(kind: Kind) Preamble {
    return switch (kind) {
        .cmd => .cmd,
        .powershell, .pwsh => .pwsh,
        // All other kinds decode their own output; a Windows CP chcp
        // would be ignored at best and misleading at worst.
        .unknown, .wsl, .ssh, .bash, .nu, .zsh, .fish, .elvish, .xonsh => .none,
    };
}

/// Classify an executable path or single-token command string. Strips
/// surrounding quotes, directory prefix, and a trailing `.exe`
/// suffix, then matches case-insensitively against the known table.
/// Returns `.unknown` for anything unrecognized.
///
/// This function does not parse argv flags. Callers with a full
/// command line should split off the first token before calling.
pub fn classify(exe_path: []const u8) Awareness {
    return awarenessOf(identify(exe_path));
}

/// Return the UTF-8 preamble needed to make this shell emit UTF-8 on
/// startup under ConPTY. Callers should invoke this only when the
/// transport actually resolves to ConPTY; the raw-pipe bypass path
/// already inherits our UTF-8 parent console (see PR # 301).
pub fn utf8Preamble(exe_path: []const u8) Preamble {
    return preambleOf(identify(exe_path));
}

fn identify(exe_path: []const u8) Kind {
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

    return kinds.get(lower) orelse .unknown;
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

test "utf8Preamble: cmd.exe returns .cmd" {
    try testing.expectEqual(Preamble.cmd, utf8Preamble("cmd"));
    try testing.expectEqual(Preamble.cmd, utf8Preamble("cmd.exe"));
    try testing.expectEqual(Preamble.cmd, utf8Preamble("CMD.EXE"));
    try testing.expectEqual(Preamble.cmd, utf8Preamble("C:\\Windows\\System32\\cmd.exe"));
}

test "utf8Preamble: pwsh.exe returns .pwsh" {
    try testing.expectEqual(Preamble.pwsh, utf8Preamble("pwsh"));
    try testing.expectEqual(Preamble.pwsh, utf8Preamble("pwsh.exe"));
    try testing.expectEqual(Preamble.pwsh, utf8Preamble("PWSH.EXE"));
    try testing.expectEqual(Preamble.pwsh, utf8Preamble("C:\\Program Files\\PowerShell\\7\\pwsh.exe"));
}

test "utf8Preamble: powershell 5.1 returns .pwsh" {
    try testing.expectEqual(Preamble.pwsh, utf8Preamble("powershell"));
    try testing.expectEqual(Preamble.pwsh, utf8Preamble("powershell.exe"));
    try testing.expectEqual(Preamble.pwsh, utf8Preamble("PowerShell.exe"));
}

test "utf8Preamble: vt-aware non-powershell shells return .none" {
    // bash/wsl/ssh/nu don't observe the Windows console CP the same way
    // powershell does, and auto-mode routes them through the bypass
    // path anyway. Only powershell-family shells need the preamble
    // under forced conpty-mode=never.
    try testing.expectEqual(Preamble.none, utf8Preamble("bash.exe"));
    try testing.expectEqual(Preamble.none, utf8Preamble("wsl.exe"));
    try testing.expectEqual(Preamble.none, utf8Preamble("ssh.exe"));
    try testing.expectEqual(Preamble.none, utf8Preamble("nu"));
    try testing.expectEqual(Preamble.none, utf8Preamble("zsh"));
    try testing.expectEqual(Preamble.none, utf8Preamble("fish"));
}

test "utf8Preamble: unknown returns .none" {
    try testing.expectEqual(Preamble.none, utf8Preamble("my-custom-repl.exe"));
    try testing.expectEqual(Preamble.none, utf8Preamble("python.exe"));
    try testing.expectEqual(Preamble.none, utf8Preamble(""));
}

test "utf8Preamble: suffix argv matches ConPTY setup contract" {
    // cmd: /K lets the shell stay interactive after chcp.
    const cmd_suffix = Preamble.cmd.suffix();
    try testing.expectEqual(@as(usize, 2), cmd_suffix.len);
    try testing.expectEqualStrings("/K", cmd_suffix[0]);
    try testing.expectEqualStrings("chcp 65001 >nul", cmd_suffix[1]);

    // pwsh: -NoExit mirrors the cmd /K behavior; -Command runs the
    // setup before dropping the user into the prompt.
    const pwsh_suffix = Preamble.pwsh.suffix();
    try testing.expectEqual(@as(usize, 3), pwsh_suffix.len);
    try testing.expectEqualStrings("-NoExit", pwsh_suffix[0]);
    try testing.expectEqualStrings("-Command", pwsh_suffix[1]);
    try testing.expect(std.mem.indexOf(u8, pwsh_suffix[2], "[Console]::OutputEncoding") != null);
    try testing.expect(std.mem.indexOf(u8, pwsh_suffix[2], "[Console]::InputEncoding") != null);

    // none: empty.
    try testing.expectEqual(@as(usize, 0), Preamble.none.suffix().len);
}

test "utf8Preamble: prefix ends with shell-appropriate separator" {
    // cmd: `&&` chains on success, preserving the user's script when
    // chcp somehow fails; trailing space so concatenation doesn't
    // mash into the user's script.
    const cmd_prefix = Preamble.cmd.prefix();
    try testing.expect(std.mem.startsWith(u8, cmd_prefix, "chcp 65001"));
    try testing.expect(std.mem.endsWith(u8, cmd_prefix, " && "));

    // pwsh: `;` is a statement separator; trailing space keeps the
    // wrapped script readable in logs.
    const pwsh_prefix = Preamble.pwsh.prefix();
    try testing.expect(std.mem.indexOf(u8, pwsh_prefix, "[Console]::OutputEncoding") != null);
    try testing.expect(std.mem.indexOf(u8, pwsh_prefix, "[Console]::InputEncoding") != null);
    try testing.expect(std.mem.endsWith(u8, pwsh_prefix, "; "));

    // none: empty.
    try testing.expectEqualStrings("", Preamble.none.prefix());
}
