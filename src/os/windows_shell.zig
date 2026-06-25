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
const Allocator = std.mem.Allocator;

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
///   - same dir as bash:  <dir>\winpty.exe
///       matches MSYS2 (usr\bin\bash.exe) and Git's usr\bin\bash.exe
///   - parent's usr\bin:  <parent>\usr\bin\winpty.exe
///       matches Git's bin\bash.exe (winpty lives one level up in usr\bin)
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

/// True for the POSIX-emulation shells (MSYS2/Git-Bash/Cygwin) that report
/// `/`-rooted POSIX cwd paths via OSC 7. Excludes nu/elvish/xonsh, whose native
/// Windows builds report `C:\...` already (translating those would corrupt a
/// correct path).
fn isRootedPosixShell(kind: Kind) bool {
    return switch (kind) {
        .bash, .zsh, .fish => true,
        else => false,
    };
}

/// Derive the install root from a POSIX-shell exe path by stripping the known
/// layout suffix: `<root>\usr\bin\<sh>.exe` (MSYS2, Git's usr layout) or
/// `<root>\bin\<sh>.exe` (Git, Cygwin). Owned dupe; null if neither layout
/// matches (the caller then no-ops root-relative paths rather than guessing).
/// Pure Windows path math (dirnameWindows/basenameWindows) so it is
/// deterministic when these tests run on a non-Windows CI host.
pub fn installRootFromExe(alloc: std.mem.Allocator, arg0: []const u8) ?[]u8 {
    const trimmed = std.mem.trim(u8, arg0, "\"' \t\r\n");
    const bin = std.fs.path.dirnameWindows(trimmed) orelse return null;
    if (!std.ascii.eqlIgnoreCase(std.fs.path.basenameWindows(bin), "bin")) return null;
    const up1 = std.fs.path.dirnameWindows(bin) orelse return null;
    const root = if (std.ascii.eqlIgnoreCase(std.fs.path.basenameWindows(up1), "usr"))
        (std.fs.path.dirnameWindows(up1) orelse return null)
    else
        up1;
    // A drive-root install (`C:\bin\bash.exe`) leaves a trailing separator
    // (`C:\`); strip it so the result honors rootedToWindows's "no trailing
    // separator" contract (otherwise root-relative paths get a doubled `\`).
    const normalized = std.mem.trimRight(u8, root, "\\/");
    if (normalized.len == 0) return null;
    return alloc.dupe(u8, normalized) catch null;
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

/// True if `arg0` is the WSL launcher (`wsl`/`wsl.exe`, case-insensitive,
/// any path). Cheap pre-check so callers can skip work for non-WSL commands.
///
/// Delegates to `identify`, which strips surrounding quotes and splits on
/// both path separators, so it is deterministic regardless of host. A
/// hand-rolled `std.fs.path.basename` here would follow the *host* separator
/// and miss a `C:\...\wsl.exe` arg0 on POSIX (and silently disagree with
/// `identify` on quoted paths).
pub fn isWslExe(arg0: []const u8) bool {
    return identify(arg0) == .wsl;
}

test "isWslExe: windows path, forward slashes, quoted, bare, non-wsl" {
    try testing.expect(isWslExe("C:\\Windows\\System32\\wsl.exe"));
    try testing.expect(isWslExe("C:/Windows/System32/wsl.exe"));
    try testing.expect(isWslExe("\"wsl.exe\""));
    try testing.expect(isWslExe("wsl"));
    try testing.expect(!isWslExe("C:\\Windows\\System32\\cmd.exe"));
    try testing.expect(!isWslExe(""));
}

/// If `argv` invokes the WSL launcher, return the target distro from
/// `-d <distro>` / `--distribution <distro>` (and the `=`-joined forms), or
/// `""` for the default distro. Returns null for non-WSL commands. Pure; safe
/// to call on any platform.
pub fn wslDistro(argv: []const []const u8) ?[]const u8 {
    if (argv.len == 0 or !isWslExe(argv[0])) return null;

    var i: usize = 1;
    while (i < argv.len) : (i += 1) {
        const a = argv[i];
        if (std.mem.eql(u8, a, "-d") or std.mem.eql(u8, a, "--distribution")) {
            if (i + 1 < argv.len) return argv[i + 1];
            return "";
        }
        // `=`-joined forms. WSL profiles emit the space-separated form, but a
        // hand-written command may use these; recognize them so we don't
        // mis-target the default distro.
        if (std.mem.startsWith(u8, a, "-d=")) return a["-d=".len..];
        if (std.mem.startsWith(u8, a, "--distribution=")) return a["--distribution=".len..];
        // `--` ends WSL options; everything after is the in-distro command.
        if (std.mem.eql(u8, a, "--")) break;
    }
    return ""; // wsl with no explicit distro -> default
}

/// Trim trailing NUL code units from a REG_SZ value read via `RegGetValueW`.
/// `RegGetValueW` guarantees NUL-termination and reports a byte count that
/// includes the terminator; some values report extra padding NULs. Returns
/// null if nothing remains.
fn trimRegSz(raw: []const u16) ?[]const u16 {
    var end = raw.len;
    while (end > 0 and raw[end - 1] == 0) end -= 1;
    if (end == 0) return null;
    return raw[0..end];
}

/// Build the per-distro registry subkey path `<base>\<guid>` into `buf`,
/// NUL-terminated. Returns a sentinel-terminated UTF-16 slice (suitable for an
/// `LPCWSTR` via `.ptr`) or null if `buf` is too small.
fn lxssSubkeyFor(buf: []u16, base: []const u16, guid: []const u16) ?[:0]const u16 {
    const needed = base.len + 1 + guid.len + 1; // base + '\' + guid + NUL
    if (needed > buf.len) return null;
    var i: usize = 0;
    @memcpy(buf[i..][0..base.len], base);
    i += base.len;
    buf[i] = '\\';
    i += 1;
    @memcpy(buf[i..][0..guid.len], guid);
    i += guid.len;
    buf[i] = 0;
    return buf[0..i :0];
}

/// Read a REG_SZ value into `buf` via a single `RegGetValueW` call (which
/// opens, queries, and closes the key with type enforcement). Returns the
/// value as a UTF-16 slice with trailing NUL(s) trimmed, or null on any
/// non-success status (including `ERROR_MORE_DATA` for an over-long value).
/// `subkey` and `value` must be NUL-terminated (`LPCWSTR`).
fn regGetSzW(
    buf: []u16,
    subkey: [*:0]const u16,
    value: [*:0]const u16,
) ?[]const u16 {
    var size_bytes: windows.DWORD = @intCast(buf.len * @sizeOf(u16));
    const status = windows.advapi32.RegGetValueW(
        windows.HKEY_CURRENT_USER,
        subkey,
        value,
        windows.advapi32.RRF.RT_REG_SZ,
        null,
        @ptrCast(buf.ptr),
        &size_bytes,
    );
    if (status != 0) return null; // anything but ERROR_SUCCESS (0)
    const len_u16 = size_bytes / @sizeOf(u16);
    if (len_u16 == 0 or len_u16 > buf.len) return null;
    return trimRegSz(buf[0..len_u16]);
}

/// Resolve the default WSL distribution's real name from the registry. WSL
/// stores it under `HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss`: the
/// `DefaultDistribution` GUID names a subkey whose `DistributionName` value is
/// the human name (e.g. "Ubuntu-24.04"). Caller owns the returned slice.
/// Returns null on any failure (no WSL, no default distro, registry or
/// conversion error) — best-effort, matching the OSC 7 no-op fallback.
/// Windows-only; returns null elsewhere.
pub fn defaultDistroName(alloc: Allocator) ?[]u8 {
    if (comptime builtin.os.tag != .windows) return null;

    const lxss = std.unicode.utf8ToUtf16LeStringLiteral(
        "Software\\Microsoft\\Windows\\CurrentVersion\\Lxss",
    );
    const default_distribution =
        std.unicode.utf8ToUtf16LeStringLiteral("DefaultDistribution");
    const distribution_name =
        std.unicode.utf8ToUtf16LeStringLiteral("DistributionName");

    // 1) Default-distro GUID under Lxss. A GUID is "{8-4-4-4-12}" = 38 chars;
    //    [64] leaves margin (an over-long value just yields ERROR_MORE_DATA -> null).
    var guid_buf: [64]u16 = undefined;
    const guid = regGetSzW(&guid_buf, lxss, default_distribution) orelse return null;

    // 2) Per-GUID subkey: Lxss\{guid}.
    var subkey_buf: [128]u16 = undefined;
    const subkey = lxssSubkeyFor(&subkey_buf, lxss, guid) orelse return null;

    // 3) DistributionName under that subkey.
    var name_buf: [256]u16 = undefined;
    const name_w = regGetSzW(&name_buf, subkey.ptr, distribution_name) orelse return null;

    // 4) UTF-16 -> owned UTF-8.
    return std.unicode.utf16LeToUtf8Alloc(alloc, name_w) catch null;
}

/// Launch context describing a WSL surface, derived from its spawn argv.
pub const WslContext = struct {
    /// Real WSL distribution name (e.g. "Ubuntu-24.04"), or null when this is a
    /// WSL surface whose distro name is unknown. A bare `wsl.exe` default-distro
    /// session starts null here; `Termio` then fills it via `defaultDistroName`
    /// (registry lookup), falling back to null if that fails.
    distro: ?[]const u8 = null,

    /// Detect WSL launch context from the spawn argv. Returns null for non-WSL
    /// (or non-Windows) surfaces. When `distro` is present it is duped from
    /// `alloc` and the owner must free it.
    pub fn fromArgs(
        alloc: std.mem.Allocator,
        args: []const [:0]const u8,
    ) ?WslContext {
        if (comptime builtin.os.tag != .windows) return null;
        if (args.len == 0 or !isWslExe(args[0])) return null;

        // wslDistro takes []const []const u8; build a thin non-sentinel view.
        const view = alloc.alloc([]const u8, args.len) catch return null;
        defer alloc.free(view);
        for (args, 0..) |a, i| view[i] = a;

        const distro = wslDistro(view) orelse return null;
        if (distro.len == 0) return .{ .distro = null }; // default distro, name deferred
        const owned = alloc.dupe(u8, distro) catch return .{ .distro = null };
        return .{ .distro = owned };
    }
};

/// OSC 7 context for a MSYS2/Git-Bash/Cygwin surface: the install root used to
/// resolve root-relative POSIX paths. null when it could not be derived from
/// argv[0] (root-relative paths then no-op; drive-form paths still translate).
pub const RootedContext = struct { install_root: ?[]const u8 = null };

/// What a Windows POSIX-emulation surface needs to translate OSC 7 paths: a WSL
/// distro (UNC form) or a MSYS2/Cygwin install root (rooted form).
pub const Osc7Context = union(enum) {
    wsl: WslContext,
    rooted: RootedContext,
};

/// Detect the OSC 7 path-translation context from a spawn argv. Pure and
/// deterministic (the wsl arm's default-distro registry resolution happens
/// later, in Termio). Returns null for non-Windows, non-POSIX shells
/// (cmd/pwsh/nu/...), and empty argv. When present, owned strings inside must
/// be freed by the caller (Termio errdefer + StreamHandler.deinit).
pub fn osc7ContextFromArgs(
    alloc: std.mem.Allocator,
    args: []const [:0]const u8,
) ?Osc7Context {
    if (comptime builtin.os.tag != .windows) return null;
    if (args.len == 0) return null;
    // wsl.exe is handled here and is not a rooted kind, so it can never reach
    // the rooted gate below (keep that true if isRootedPosixShell changes).
    if (WslContext.fromArgs(alloc, args)) |w| return .{ .wsl = w };
    if (isRootedPosixShell(identify(args[0]))) {
        return .{ .rooted = .{ .install_root = installRootFromExe(alloc, args[0]) } };
    }
    return null;
}

test "wslDistro: detects distro and default, ignores non-WSL" {
    {
        const argv = [_][]const u8{ "wsl.exe", "-d", "Ubuntu" };
        try testing.expectEqualStrings("Ubuntu", wslDistro(&argv).?);
    }
    {
        const argv = [_][]const u8{ "C:\\Windows\\System32\\wsl.exe", "--distribution", "Ubuntu-24.04" };
        try testing.expectEqualStrings("Ubuntu-24.04", wslDistro(&argv).?);
    }
    {
        const argv = [_][]const u8{ "wsl.exe", "--distribution=Debian" };
        try testing.expectEqualStrings("Debian", wslDistro(&argv).?);
    }
    {
        const argv = [_][]const u8{ "wsl.exe", "-d=Arch" };
        try testing.expectEqualStrings("Arch", wslDistro(&argv).?);
    }
    {
        // bare wsl: default distro -> empty-string sentinel.
        const argv = [_][]const u8{"wsl.exe"};
        try testing.expectEqualStrings("", wslDistro(&argv).?);
    }
    {
        // `--` ends WSL options; no -d before it -> default.
        const argv = [_][]const u8{ "wsl.exe", "--", "htop" };
        try testing.expectEqualStrings("", wslDistro(&argv).?);
    }
    {
        const argv = [_][]const u8{ "pwsh.exe", "-NoLogo" };
        try testing.expect(wslDistro(&argv) == null);
    }
    {
        const argv = [_][]const u8{ "C:\\msys64\\usr\\bin\\bash.exe", "-i" };
        try testing.expect(wslDistro(&argv) == null);
    }
    {
        const argv = [_][]const u8{};
        try testing.expect(wslDistro(&argv) == null);
    }
}

test "WslContext.fromArgs: explicit distro" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const alloc = std.testing.allocator;
    const args = [_][:0]const u8{ "wsl.exe", "-d", "Ubuntu-24.04" };
    const ctx = WslContext.fromArgs(alloc, &args).?;
    defer if (ctx.distro) |d| alloc.free(d);
    try std.testing.expectEqualStrings("Ubuntu-24.04", ctx.distro.?);
}

test "WslContext.fromArgs: =-joined distro" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const alloc = std.testing.allocator;
    const args = [_][:0]const u8{ "wsl.exe", "-d=Ubuntu" };
    const ctx = WslContext.fromArgs(alloc, &args).?;
    defer if (ctx.distro) |d| alloc.free(d);
    try std.testing.expectEqualStrings("Ubuntu", ctx.distro.?);
}

test "WslContext.fromArgs: bare wsl is WSL with unknown distro" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const alloc = std.testing.allocator;
    const args = [_][:0]const u8{"wsl.exe"};
    const ctx = WslContext.fromArgs(alloc, &args).?;
    try std.testing.expect(ctx.distro == null);
}

test "WslContext.fromArgs: non-WSL and empty are null" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const alloc = std.testing.allocator;
    const pwsh = [_][:0]const u8{ "pwsh.exe", "-NoLogo" };
    try std.testing.expect(WslContext.fromArgs(alloc, &pwsh) == null);
    const empty = [_][:0]const u8{};
    try std.testing.expect(WslContext.fromArgs(alloc, &empty) == null);
}

test "trimRegSz: trims trailing NULs, preserves interior, rejects empty" {
    // Single trailing NUL (the common RegGetValueW case).
    {
        const v = [_]u16{ 'U', 'b', 'u', 0 };
        try testing.expectEqualSlices(u16, &[_]u16{ 'U', 'b', 'u' }, trimRegSz(&v).?);
    }
    // Multiple trailing NULs.
    {
        const v = [_]u16{ 'X', 0, 0, 0 };
        try testing.expectEqualSlices(u16, &[_]u16{'X'}, trimRegSz(&v).?);
    }
    // No trailing NUL at all.
    {
        const v = [_]u16{ 'a', 'b' };
        try testing.expectEqualSlices(u16, &[_]u16{ 'a', 'b' }, trimRegSz(&v).?);
    }
    // Interior NUL is preserved; only trailing trimmed.
    {
        const v = [_]u16{ 'a', 0, 'b', 0 };
        try testing.expectEqualSlices(u16, &[_]u16{ 'a', 0, 'b' }, trimRegSz(&v).?);
    }
    // All-NUL and empty -> null.
    {
        const v = [_]u16{ 0, 0 };
        try testing.expect(trimRegSz(&v) == null);
        const empty = [_]u16{};
        try testing.expect(trimRegSz(&empty) == null);
    }
}

test "lxssSubkeyFor: builds base\\{guid}, NUL-terminated, bounds-checked" {
    const base = std.unicode.utf8ToUtf16LeStringLiteral("Lxss");
    const guid = std.unicode.utf8ToUtf16LeStringLiteral("{abc}");

    var buf: [32]u16 = undefined;
    const out = lxssSubkeyFor(&buf, base, guid).?;

    var u8buf: [32]u8 = undefined;
    const n = try std.unicode.utf16LeToUtf8(&u8buf, out);
    try testing.expectEqualStrings("Lxss\\{abc}", u8buf[0..n]);

    // Sentinel-terminated at out.len.
    try testing.expectEqual(@as(u16, 0), buf[out.len]);

    // Too-small buffer -> null, no overflow.
    var tiny: [4]u16 = undefined;
    try testing.expect(lxssSubkeyFor(&tiny, base, guid) == null);
}

test "defaultDistroName: links, never crashes, returns null-or-nonempty" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const alloc = testing.allocator;
    // Host-dependent: a name on a machine with a default distro, null otherwise.
    // We only assert it links, runs, and frees cleanly.
    if (defaultDistroName(alloc)) |name| {
        defer alloc.free(name);
        try testing.expect(name.len > 0);
    }
}

test "integration: resolved default distro feeds OSC 7 UNC translation" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const alloc = testing.allocator;
    const posix_path = @import("posix_path.zig");

    // No default distro on this host (e.g. CI without WSL) -> nothing to prove.
    const distro = defaultDistroName(alloc) orelse return;
    defer alloc.free(distro);

    // The gap this slice closes: an in-distro path (which yielded
    // error.UnknownDistro for a bare-wsl surface before resolution) must now
    // translate to the UNC form using the resolved name.
    const win = try posix_path.wslToWindows(alloc, "/home/alex", distro);
    defer alloc.free(win);
    try testing.expect(std.mem.startsWith(u8, win, "\\\\wsl.localhost\\"));
    try testing.expect(std.mem.indexOf(u8, win, distro) != null);
    try testing.expect(std.mem.endsWith(u8, win, "\\home\\alex"));
}

test "installRootFromExe: MSYS2 usr\\bin layout" {
    const alloc = testing.allocator;
    const r = installRootFromExe(alloc, "C:\\msys64\\usr\\bin\\bash.exe").?;
    defer alloc.free(r);
    try testing.expectEqualStrings("C:\\msys64", r);
}

test "installRootFromExe: Git/Cygwin bin layout" {
    const alloc = testing.allocator;
    {
        const r = installRootFromExe(alloc, "C:\\Program Files\\Git\\bin\\bash.exe").?;
        defer alloc.free(r);
        try testing.expectEqualStrings("C:\\Program Files\\Git", r);
    }
    {
        const r = installRootFromExe(alloc, "C:\\cygwin64\\bin\\zsh.exe").?;
        defer alloc.free(r);
        try testing.expectEqualStrings("C:\\cygwin64", r);
    }
}

test "installRootFromExe: unknown layout or bare name -> null" {
    const alloc = testing.allocator;
    try testing.expect(installRootFromExe(alloc, "bash.exe") == null);
    try testing.expect(installRootFromExe(alloc, "C:\\Windows\\System32\\bash.exe") == null);
    try testing.expect(installRootFromExe(alloc, "C:\\tools\\bash.exe") == null);
}

test "installRootFromExe: drive-root install strips trailing separator" {
    const alloc = testing.allocator;
    // <drive>\bin\bash.exe would yield root "C:\" -> normalized to "C:" so
    // rootedToWindows doesn't produce a doubled separator.
    const r = installRootFromExe(alloc, "C:\\bin\\bash.exe").?;
    defer alloc.free(r);
    try testing.expectEqualStrings("C:", r);
}

test "isRootedPosixShell: bash/zsh/fish only" {
    try testing.expect(isRootedPosixShell(.bash));
    try testing.expect(isRootedPosixShell(.zsh));
    try testing.expect(isRootedPosixShell(.fish));
    try testing.expect(!isRootedPosixShell(.nu));
    try testing.expect(!isRootedPosixShell(.pwsh));
    try testing.expect(!isRootedPosixShell(.wsl));
    try testing.expect(!isRootedPosixShell(.unknown));
}

test "osc7ContextFromArgs: wsl arm" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const alloc = testing.allocator;
    const args = [_][:0]const u8{ "wsl.exe", "-d", "Ubuntu" };
    const ctx = osc7ContextFromArgs(alloc, &args).?;
    defer switch (ctx) {
        .wsl => |w| if (w.distro) |d| alloc.free(d),
        .rooted => |r| if (r.install_root) |s| alloc.free(s),
    };
    try testing.expect(ctx == .wsl);
    try testing.expectEqualStrings("Ubuntu", ctx.wsl.distro.?);
}

test "osc7ContextFromArgs: rooted arm (MSYS2 bash)" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const alloc = testing.allocator;
    const args = [_][:0]const u8{ "C:\\msys64\\usr\\bin\\bash.exe", "-i" };
    const ctx = osc7ContextFromArgs(alloc, &args).?;
    defer switch (ctx) {
        .wsl => |w| if (w.distro) |d| alloc.free(d),
        .rooted => |r| if (r.install_root) |s| alloc.free(s),
    };
    try testing.expect(ctx == .rooted);
    try testing.expectEqualStrings("C:\\msys64", ctx.rooted.install_root.?);
}

test "osc7ContextFromArgs: non-posix shells -> null" {
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const alloc = testing.allocator;
    const pwsh = [_][:0]const u8{ "pwsh.exe", "-NoLogo" };
    try testing.expect(osc7ContextFromArgs(alloc, &pwsh) == null);
    const cmd = [_][:0]const u8{"cmd.exe"};
    try testing.expect(osc7ContextFromArgs(alloc, &cmd) == null);
    const nu = [_][:0]const u8{ "C:\\msys64\\usr\\bin\\nu.exe", "-i" };
    try testing.expect(osc7ContextFromArgs(alloc, &nu) == null);
    const empty = [_][:0]const u8{};
    try testing.expect(osc7ContextFromArgs(alloc, &empty) == null);
}
