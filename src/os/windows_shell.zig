//! Shell-identity classification for Windows shell executables, used
//! to select a UTF-8 preamble under ConPTY.
//!
//! This is orthogonal to src/termio/shell_integration.zig's `Shell`
//! enum: `Shell` identifies bash/zsh/etc for RC-file injection;
//! `Kind` here identifies the shell for preamble selection. A shell
//! can be recognized here without being recognized there (e.g.
//! `wsl.exe`) and vice versa.

const std = @import("std");
const builtin = @import("builtin");
const windows = @import("windows.zig");
const testing = std.testing;
const log = std.log.scoped(.windows_shell);

/// UTF-8 preamble kind needed to make a shell's *initial* output land
/// as UTF-8 when it runs under ConPTY. We distinguish cmd from
/// powershell (different preamble) and pwsh/powershell-family from the
/// other shells (only powershell-family benefits from the setup).
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
            // `chcp 65001 > $null` sets the conhost output codepage
            // to UTF-8 so the bytes [Console]::OutputEncoding writes
            // are also rendered as UTF-8 by the host. Without it,
            // Nerd Font glyphs from prompt themes (Oh-My-Posh,
            // Starship) come out as `?` even though pwsh's .NET
            // encoding is UTF-8 - the conhost interpreter is still
            // on the system codepage. The `cmd -> pwsh` path doesn't
            // hit this because cmd's own preamble already chcp'd the
            // host before pwsh inherited it. `;` chains statements
            // in PowerShell. Output encoding first, then input so
            // piped stdout and redirected stdin match. See `suffix`
            // for why we set both.
            .pwsh => "chcp 65001 > $null; [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new(); [Console]::InputEncoding = [Console]::OutputEncoding; ",
        };
    }

    const cmd_suffix = [_][:0]const u8{ "/K", "chcp 65001 >nul" };
    const pwsh_suffix = [_][:0]const u8{
        "-NoExit",
        "-Command",
        // `chcp 65001 > $null` sets the conhost output codepage so
        // the bytes [Console]::OutputEncoding writes get rendered as
        // UTF-8 by the host (otherwise Nerd Font glyphs from Oh-My-
        // Posh/Starship come out as `?` even when pwsh's .NET
        // encoding is UTF-8 - the conhost interpreter is still on
        // the system codepage). Then set both output *and* input
        // encodings: the output side fixes what the pane renders;
        // the input side fixes what redirection (`>`, `|`) produces
        // when the user pipes pwsh into another tool.
        "chcp 65001 > $null; [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new(); [Console]::InputEncoding = [Console]::OutputEncoding",
    };
};

/// Fine-grained shell identity used to select a UTF-8 preamble under
/// ConPTY (the sole Windows transport).
pub const Kind = enum {
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

fn preambleOf(kind: Kind) Preamble {
    return switch (kind) {
        .cmd => .cmd,
        .powershell, .pwsh => .pwsh,
        // All other kinds decode their own output; a Windows CP chcp
        // would be ignored at best and misleading at worst.
        .unknown, .wsl, .ssh, .bash, .nu, .zsh, .fish, .elvish, .xonsh => .none,
    };
}

/// Return the UTF-8 preamble needed to make this shell emit UTF-8 on
/// startup. The actual emission gate lives in
/// `Exec.maybeInjectUtf8Preamble` and is driven by the resolved
/// `utf8-console` policy.
pub fn utf8Preamble(exe_path: []const u8) Preamble {
    return preambleOf(identify(exe_path));
}

pub fn identify(exe_path: []const u8) Kind {
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

/// Candidate winpty.exe locations relative to a Cygwin-family bash.exe.
/// Two layouts cover Git for Windows and MSYS2:
///   - same dir as bash:   <dir>\winpty.exe         (Git/MSYS2 usr\bin)
///   - sibling usr\bin:    <parent>\usr\bin\winpty.exe  (Git bin\bash.exe)
///
/// Pure path math; existence is checked by the caller. Caller frees each
/// returned slice. Surrounding quotes/whitespace are stripped so a config
/// value like `"C:\Git\bin\bash.exe"` resolves correctly. We use the
/// Windows-specific dirname and an explicit `\` separator (rather than
/// std.fs.path.join/dirname, which follow the *host* OS) so the result is
/// deterministic when these tests run on a non-Windows CI host.
pub fn winptyCandidatePaths(
    alloc: std.mem.Allocator,
    bash_exe_path: []const u8,
) std.mem.Allocator.Error![2][]const u8 {
    const trimmed = std.mem.trim(u8, bash_exe_path, "\"' \t\r\n");
    const dir = std.fs.path.dirnameWindows(trimmed) orelse ".";
    const parent = std.fs.path.dirnameWindows(dir) orelse dir;
    return .{
        try std.fmt.allocPrint(alloc, "{s}\\winpty.exe", .{dir}),
        try std.fmt.allocPrint(alloc, "{s}\\usr\\bin\\winpty.exe", .{parent}),
    };
}

/// Returns true if the system ANSI codepage (`GetACP()`) is one of the
/// legacy double-byte CJK codepages where forcing UTF-8 on a spawned
/// shell would mojibake legacy `.bat` scripts whose script text is
/// stored in that codepage.
///
/// We only flag the five double-byte CJK codepages (Shift-JIS, GB2312,
/// EUC-KR, Big5, Johab). Single-byte legacy codepages (Thai 874, Hebrew
/// 1255, Vietnamese 1258, etc.) survive a UTF-8 flip of the spawned
/// shell's encoding and are not classified as CJK here.
///
/// Modern CJK developers running native Windows are increasingly UTF-8
/// (VS Code, WSL, Beta-UTF-8 toggle); they can opt back in via
/// `utf8-console = always`.
pub fn isCjkAnsiCodePage() bool {
    if (comptime builtin.os.tag != .windows) return false;
    return isCjkAnsiCodePageFor(windows.exp.kernel32.GetACP());
}

/// Pure-logic variant of `isCjkAnsiCodePage` for testing. Takes an
/// explicit codepage rather than calling `GetACP()`.
pub fn isCjkAnsiCodePageFor(acp: std.os.windows.UINT) bool {
    return switch (acp) {
        932, // ja_JP: Shift-JIS
        936, // zh_CN: GB2312
        949, // ko_KR: EUC-KR
        950, // zh_TW: Big5
        1361, // ko_KR: Johab (legacy)
        => true,
        else => false,
    };
}

test "identify: pwsh variants" {
    try testing.expectEqual(Kind.pwsh, identify("pwsh"));
    try testing.expectEqual(Kind.pwsh, identify("pwsh.exe"));
    try testing.expectEqual(Kind.pwsh, identify("PWSH.EXE"));
    try testing.expectEqual(Kind.pwsh, identify("C:\\Program Files\\PowerShell\\7\\pwsh.exe"));
}

test "identify: wsl, ssh, bash" {
    try testing.expectEqual(Kind.wsl, identify("wsl.exe"));
    try testing.expectEqual(Kind.ssh, identify("ssh.exe"));
    try testing.expectEqual(Kind.bash, identify("bash.exe"));
    try testing.expectEqual(Kind.wsl, identify("C:\\Windows\\System32\\wsl.exe"));
}

test "identify: nu, zsh, fish" {
    try testing.expectEqual(Kind.nu, identify("nu.exe"));
    try testing.expectEqual(Kind.zsh, identify("zsh"));
    try testing.expectEqual(Kind.fish, identify("fish"));
}

test "identify: elvish, xonsh" {
    try testing.expectEqual(Kind.elvish, identify("elvish.exe"));
    try testing.expectEqual(Kind.xonsh, identify("xonsh"));
}

test "identify: cmd.exe" {
    try testing.expectEqual(Kind.cmd, identify("cmd"));
    try testing.expectEqual(Kind.cmd, identify("cmd.exe"));
    try testing.expectEqual(Kind.cmd, identify("CMD.EXE"));
    try testing.expectEqual(Kind.cmd, identify("C:\\Windows\\System32\\cmd.exe"));
}

test "identify: powershell 5.1" {
    try testing.expectEqual(Kind.powershell, identify("powershell"));
    try testing.expectEqual(Kind.powershell, identify("powershell.exe"));
    try testing.expectEqual(Kind.powershell, identify("PowerShell.exe"));
}

test "identify: unknown returns unknown" {
    try testing.expectEqual(Kind.unknown, identify("my-custom-repl.exe"));
    try testing.expectEqual(Kind.unknown, identify("python.exe"));
    try testing.expectEqual(Kind.unknown, identify("notepad.exe"));
}

test "identify: strips surrounding quotes" {
    try testing.expectEqual(Kind.pwsh, identify("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\""));
    try testing.expectEqual(Kind.cmd, identify("'cmd.exe'"));
}

test "identify: handles forward slashes" {
    try testing.expectEqual(Kind.pwsh, identify("C:/Program Files/PowerShell/7/pwsh.exe"));
}

test "identify: empty and whitespace" {
    try testing.expectEqual(Kind.unknown, identify(""));
    try testing.expectEqual(Kind.unknown, identify("   "));
    try testing.expectEqual(Kind.unknown, identify("\t\n"));
}

test "identify: handles very long path safely" {
    // Longer than the 64-byte lowercase buffer. Must return .unknown
    // instead of crashing or false-matching.
    var long_path: [128]u8 = undefined;
    @memset(&long_path, 'a');
    try testing.expectEqual(Kind.unknown, identify(&long_path));
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
    // bash/wsl/ssh/nu don't observe the Windows console CP the way
    // powershell does. Only powershell-family shells need the preamble.
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
    // Setting [Console]::OutputEncoding alone leaves conhost on the
    // system codepage so Nerd Font glyphs render as `?`. The script
    // must run `chcp 65001 > $null` first.
    try testing.expect(std.mem.indexOf(u8, pwsh_suffix[2], "chcp 65001") != null);

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
    // wrapped script readable in logs. Same chcp prefix as the
    // suffix path so wrap-with-existing-Command users get UTF-8
    // conhost too.
    const pwsh_prefix = Preamble.pwsh.prefix();
    try testing.expect(std.mem.indexOf(u8, pwsh_prefix, "chcp 65001") != null);
    try testing.expect(std.mem.indexOf(u8, pwsh_prefix, "[Console]::OutputEncoding") != null);
    try testing.expect(std.mem.indexOf(u8, pwsh_prefix, "[Console]::InputEncoding") != null);
    try testing.expect(std.mem.endsWith(u8, pwsh_prefix, "; "));

    // none: empty.
    try testing.expectEqualStrings("", Preamble.none.prefix());
}

test "winptyCandidatePaths: git bin layout" {
    const c = try winptyCandidatePaths(testing.allocator, "C:\\Program Files\\Git\\bin\\bash.exe");
    defer for (c) |p| testing.allocator.free(p);
    try testing.expectEqualStrings("C:\\Program Files\\Git\\bin\\winpty.exe", c[0]);
    try testing.expectEqualStrings("C:\\Program Files\\Git\\usr\\bin\\winpty.exe", c[1]);
}

test "winptyCandidatePaths: msys2 usr/bin layout" {
    const c = try winptyCandidatePaths(testing.allocator, "C:\\msys64\\usr\\bin\\bash.exe");
    defer for (c) |p| testing.allocator.free(p);
    try testing.expectEqualStrings("C:\\msys64\\usr\\bin\\winpty.exe", c[0]);
}

test "winptyCandidatePaths: strips surrounding quotes" {
    const c = try winptyCandidatePaths(testing.allocator, "\"C:\\Git\\bin\\bash.exe\"");
    defer for (c) |p| testing.allocator.free(p);
    try testing.expectEqualStrings("C:\\Git\\bin\\winpty.exe", c[0]);
}

test "isCjkAnsiCodePage: links GetACP and agrees with the pure-logic helper" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    // Smoke test: catches a broken `GetACP` extern decl on Windows
    // and verifies the wrapper agrees with the testable inner helper
    // for whatever ACP the test host actually has. Per-codepage
    // assertions live in the OS-agnostic tests below.
    try testing.expectEqual(
        isCjkAnsiCodePageFor(windows.exp.kernel32.GetACP()),
        isCjkAnsiCodePage(),
    );
}

test "isCjkAnsiCodePageFor: known CJK codepages return true" {
    try std.testing.expect(isCjkAnsiCodePageFor(932)); // ja_JP Shift-JIS
    try std.testing.expect(isCjkAnsiCodePageFor(936)); // zh_CN GB2312
    try std.testing.expect(isCjkAnsiCodePageFor(949)); // ko_KR EUC-KR
    try std.testing.expect(isCjkAnsiCodePageFor(950)); // zh_TW Big5
    try std.testing.expect(isCjkAnsiCodePageFor(1361)); // ko_KR Johab
}

test "isCjkAnsiCodePageFor: non-CJK codepages return false" {
    try std.testing.expect(!isCjkAnsiCodePageFor(437)); // OEM US
    try std.testing.expect(!isCjkAnsiCodePageFor(850)); // OEM WE (Italian)
    try std.testing.expect(!isCjkAnsiCodePageFor(1252)); // ANSI WE
    try std.testing.expect(!isCjkAnsiCodePageFor(65001)); // UTF-8
    try std.testing.expect(!isCjkAnsiCodePageFor(874)); // Thai (single-byte)
    try std.testing.expect(!isCjkAnsiCodePageFor(1255)); // Hebrew (single-byte)
    try std.testing.expect(!isCjkAnsiCodePageFor(1258)); // Vietnamese (single-byte)
}
