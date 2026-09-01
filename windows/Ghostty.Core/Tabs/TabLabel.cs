using System;

namespace Ghostty.Core.Tabs;

/// <summary>
/// The two judgements behind a tab's label that are worth deciding (and
/// testing) on their own: whether a shell-reported title actually says
/// anything, and what a reported working directory is called.
/// Pure string work, no platform deps.
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
        var trimmed = cwd.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0) return null;
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return name.Length == 0 ? null : name;
    }

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
}
