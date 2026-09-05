using System;
using System.Globalization;
using System.Text;
using Ghostty.Core.Profiles;

namespace Ghostty.Core.Tabs;

/// <summary>
/// The judgements behind a tab's label and its hover text that are worth
/// deciding (and testing) on their own: whether a shell-reported title
/// actually says anything, what a reported working directory is called,
/// how it reads once the user's own directory is written as "~", and what
/// the icon's tooltip calls the shell. Pure string work, no platform deps.
/// </summary>
internal static class TabLabel
{
    /// <summary>
    /// The shell's title, or null when it is really the console's default.
    /// ConPTY seeds every console with the full path of the exe it launched
    /// and that arrives as an OSC 0/2 title indistinguishable from one a
    /// shell chose to send, which is why a stock pwsh tab reads
    /// <c>C:\Program Files\PowerShell\7\pwsh.exe</c>. A title that BEGINS as
    /// a rooted Windows path is naming the interpreter -- already the tab's
    /// icon -- so we drop it and let the folder name have the label. A title
    /// that merely contains a path (<c>vim C:\src\x.zig</c>) is a real title
    /// and survives. Over-rejecting is cheap: the fallback is the folder,
    /// which is what a bare-cwd title was trying to say anyway.
    /// </summary>
    internal static string? Meaningful(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        return IsRooted(title.AsSpan().TrimStart()) ? null : title;
    }

    /// <summary>
    /// The name a reported working directory goes by: its last segment.
    /// A drive or share root has no last segment and so names itself
    /// (<c>C:\</c> reads "C:"). Null in -- or nothing left after the
    /// separators -- gives null out, so the caller falls through to the
    /// next title tier rather than rendering an empty tab.
    /// </summary>
    internal static string? FolderName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        var trimmed = cwd.AsSpan().Trim().TrimEnd(Separators);
        if (trimmed.Length == 0) return null;
        var cut = trimmed.LastIndexOfAny('\\', '/');
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return name.Length == 0 ? null : name.ToString();
    }

    /// <summary>
    /// The directory as a person would type it: the user's home collapsed to
    /// <c>~</c>, the way every shell the tab can host already writes it.
    /// Display only -- the caller keeps the reported path for anything that
    /// spawns, since cmd never expands a tilde and CreateProcess never will.
    ///
    /// The match is a whole-segment, case-insensitive prefix, so
    /// <c>C:\Users\alexandra</c> is not <c>~andra</c>, and it ignores slash
    /// direction because the shells disagree on it. A home that is a bare
    /// drive collapses nothing: a profile rooted at <c>C:\</c> would
    /// otherwise tilde the whole disk. A share (<c>\\server\profiles\alex</c>)
    /// is a home like any other. Redirected profiles, 8.3 spellings and
    /// junctions can miss the textual match; the cost is the full path
    /// showing, which is what it did before. A WSL tab's own home is
    /// <c>\\wsl.localhost\&lt;distro&gt;\home\&lt;user&gt;</c>, which this does not
    /// know, so there the tilde still means the Windows profile.
    ///
    /// A directory that is not <see cref="IsPlain"/> -- control characters,
    /// a line break, a bidi override -- is null out, and the label falls to
    /// the next tier: it is bytes off the pty, and rendering it would let a
    /// program write a second line into the tooltip.
    /// </summary>
    internal static string? Collapse(string? cwd, string? home)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !IsPlain(cwd)) return null;
        var path = StripExtendedPrefix(cwd.Trim());
        if (string.IsNullOrWhiteSpace(home)) return path;

        var root = StripExtendedPrefix(home.Trim()).AsSpan().TrimEnd(Separators);
        if (root.LastIndexOfAny('\\', '/') < 0) return path;
        if (path.Length < root.Length) return path;
        if (!SameSegments(path.AsSpan(0, root.Length), root)) return path;
        var rest = path.AsSpan(root.Length).TrimEnd(Separators);
        if (rest.Length == 0) return "~";
        return rest[0] is '\\' or '/' ? string.Concat("~", rest) : path;
    }

    /// <summary>
    /// Whether a reported directory can be shown and handed on as text.
    /// The VT parser drops C0 bytes, but an OSC 7 URL is percent-decoded
    /// after that, and OSC 9;9 admits any UTF-8, so a program in the pane
    /// can put a newline or a right-to-left override into what it reports.
    /// NTFS cannot hold a control character in a name, so nothing real is
    /// lost; only the bidi controls are refused among the format
    /// characters, since joiners appear in legitimate emoji folder names.
    /// </summary>
    internal static bool IsPlain(string s)
    {
        foreach (var rune in s.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    return false;
                case UnicodeCategory.Format
                    when rune.Value is 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069):
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The text a pointer resting on the tab is told: the whole directory,
    /// under whatever title outranks it. At home the collapsed form is the
    /// one glyph the label already shows, so the tooltip is where the real
    /// directory belongs. A long directory keeps its root and its tail
    /// (<see cref="Abbreviate"/>); the title line is never cut. When no
    /// directory is known the tooltip is the label itself.
    /// </summary>
    internal static string Tooltip(string? title, string? displayCwd, string? cwd, string effectiveTitle)
    {
        if (displayCwd is null) return Clamp(effectiveTitle);
        var path = displayCwd == "~" && cwd is not null ? StripExtendedPrefix(cwd.Trim()) : displayCwd;
        path = Abbreviate(path, TooltipPathBudget);
        return title is null ? path : Clamp(title) + "\n" + path;
    }

    /// <summary>
    /// A title line long enough to be a paragraph is one a program wrote:
    /// a shell can title a tab with anything it likes, and a tooltip is not
    /// the place to render all of it.
    /// </summary>
    private static string Clamp(string title)
        => title.Length <= TitleBudget ? title : title[..TitleBudget] + "…";

    /// <summary>
    /// Whether a label is the home glyph. The one title the strips draw
    /// rather than print, and the one word-only surfaces spell out.
    /// </summary>
    internal static bool IsHome(string title) => title == "~";

    /// <summary>
    /// The label as a word, for the window title, the taskbar and the
    /// palette, none of which can draw a glyph: <c>~</c> becomes "Home",
    /// everything else is itself.
    /// </summary>
    internal static string Word(string title) => IsHome(title) ? "Home" : title;

    /// <summary>
    /// Characters of directory a tooltip line gets before the middle goes.
    /// </summary>
    internal const int TooltipPathBudget = 60;

    /// <summary>
    /// Characters of shell-supplied title a tooltip line gets.
    /// </summary>
    internal const int TitleBudget = 80;

    /// <summary>
    /// A long path with its middle elided: the root stays (<c>~</c>,
    /// <c>C:</c>, <c>\\server\share</c>), then <c>…</c>, then as many
    /// trailing segments as fit the budget, never fewer than the last one.
    /// The tail is what a person recognises a directory by; the head is
    /// what they already know. Never applied to a path that is acted on --
    /// the clipboard and the launcher get the whole thing.
    /// </summary>
    internal static string Abbreviate(string path, int max)
    {
        // `\\?\UNC\server\share` is `\\server\share` wearing a prefix that
        // says "do not normalise me"; split as written and the root would be
        // the prefix rather than the server.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[@"\\?\UNC\".Length..];
        // A trailing separator names no segment, and counting it against the
        // budget would abbreviate a path that fits.
        path = path.TrimEnd('\\', '/');
        if (path.Length <= max) return path;
        var sep = path.Contains('/') && !path.Contains('\\') ? '/' : '\\';
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var unc = path.StartsWith(@"\\", StringComparison.Ordinal)
                  || path.StartsWith("//", StringComparison.Ordinal);
        // The root token: `~`, a drive, or `\\server\share` (two segments).
        var rootCount = unc ? 2 : 1;
        if (parts.Length <= rootCount + 1) return path;
        // A rooted POSIX path (`/home/alex`) has no root segment of its own:
        // its leading separator is the root, and dropping it would render an
        // absolute path as a relative one.
        var lead = !unc && (path[0] is '\\' or '/') ? sep.ToString() : "";
        var root = unc
            ? new string(sep, 2) + string.Join(sep, parts[..2])
            : lead + parts[0];
        var ellipsis = "…";

        for (var keep = parts.Length - rootCount - 1; keep >= 1; keep--)
        {
            var tail = string.Join(sep, parts[^keep..]);
            var candidate = root + sep + ellipsis + sep + tail;
            if (candidate.Length <= max || keep == 1) return candidate;
        }
        return path;
    }

    /// <summary>
    /// What the icon's tooltip says at the launch shell's prompt: the
    /// shell, then the profile's name beneath it when the name adds
    /// something. When one contains the other ("PowerShell 7" and
    /// "PowerShell", "Ubuntu" and "WSL: Ubuntu-24.04") the longer one says
    /// it once, and a profile named by the exe ("pwsh") adds nothing to the
    /// shell's name. A command whose first token is not a shell (MSYS2
    /// launches through winpty) keeps the profile name, which is the better
    /// answer there.
    /// </summary>
    internal static string IconTooltip(ProfileSnapshot snapshot)
    {
        var shell = ProcessDisplayName.Shell(snapshot.ResolvedCommand);
        var profile = snapshot.DisplayName;
        if (shell is null) return string.IsNullOrWhiteSpace(profile) ? "Terminal" : profile;
        if (string.IsNullOrWhiteSpace(profile)) return shell;
        if (profile.Contains(shell, StringComparison.OrdinalIgnoreCase)) return profile;
        if (shell.Contains(profile, StringComparison.OrdinalIgnoreCase)) return shell;
        var exe = System.IO.Path.GetFileNameWithoutExtension(
            ProfileOrderResolver.CommandBasename(snapshot.ResolvedCommand));
        if (string.Equals(exe, profile, StringComparison.OrdinalIgnoreCase)) return shell;
        return shell + "\n" + profile;
    }

    /// <summary>
    /// What the icon's tooltip says while another process is in front:
    /// "Vim in PowerShell". Without a known shell, the process alone.
    /// </summary>
    internal static string ForegroundTooltip(string exeBasename, string? commandLine, ProfileSnapshot? snapshot)
    {
        var process = ProcessDisplayName.For(exeBasename, commandLine);
        var shell = snapshot is null ? null : ProcessDisplayName.Shell(snapshot.ResolvedCommand);
        return shell is null ? process : $"{process} in {shell}";
    }

    private static ReadOnlySpan<char> Separators => ['\\', '/'];

    // A drive root (`C:\`, `c:/`) or a UNC root (`\\server\...`). Mirrors the
    // native side's posix_path.isWindowsAbsolute, which decides the same
    // question about the payload that produced this cwd.
    private static bool IsRooted(ReadOnlySpan<char> s)
    {
        if (s.StartsWith(@"\\")) return true;
        if (s.Length < 3) return false;
        if (!char.IsAsciiLetter(s[0]) || s[1] != ':') return false;
        return s[2] is '\\' or '/';
    }

    // `\\?\C:\...` is the same directory as `C:\...`; the prefix only tells
    // Win32 to skip path normalisation, and nobody wants to read it.
    private static string StripExtendedPrefix(string path)
        => path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? path[@"\\?\".Length..]
            : path;

    // Case-insensitive, and either separator matches the other.
    private static bool SameSegments(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            var ca = a[i] == '/' ? '\\' : a[i];
            var cb = b[i] == '/' ? '\\' : b[i];
            if (char.ToUpperInvariant(ca) != char.ToUpperInvariant(cb)) return false;
        }
        return true;
    }
}
