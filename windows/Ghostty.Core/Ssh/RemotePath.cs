using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Ghostty.Core.Ssh;

/// <summary>
/// Remote (POSIX) path handling for transfers. Everything that reaches
/// here came off the pty -- a window title, a word under the pointer --
/// so it is treated as untrusted text: validated, never executed.
/// </summary>
public static partial class RemotePath
{
    private const int MaxLength = 4096;

    // "user@host: ~/dir", "user@host:/dir", "host: ~" -- the title the
    // default bash/zsh prompts on Debian, Ubuntu, Fedora and friends set.
    [GeneratedRegex(@"^\s*(?:[^\s@:]+@)?[^\s@:/~]+:\s*((?:~|/)\S.*?|~|/)\s*$")]
    private static partial Regex HostTitleRegex();

    /// <summary>
    /// The directory a shell title names, or null when the title does not
    /// look like <c>[user@]host: path</c> (or a bare path). A guess by
    /// construction: callers show it for confirmation, never act on it
    /// silently.
    /// </summary>
    public static string? DirectoryFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        string candidate;
        var match = HostTitleRegex().Match(title);
        if (match.Success)
        {
            candidate = match.Groups[1].Value;
        }
        else
        {
            candidate = title.Trim();
            if (candidate.Length == 0 || candidate[0] is not ('~' or '/')) return null;
            // A bare path title with spaces is more likely "~ - some app".
            if (candidate.Contains(' ')) return null;
        }
        return IsValid(candidate) ? candidate : null;
    }

    /// <summary>
    /// True when <paramref name="path"/> can be handed to scp: non-empty,
    /// bounded, and free of control characters (a newline or NUL is never
    /// part of a name we want to act on from terminal text).
    /// </summary>
    public static bool IsValid(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxLength) return false;
        foreach (var c in path)
            if (c < ' ' || c == 0x7f) return false;
        return true;
    }

    /// <summary>
    /// Resolve <paramref name="name"/> against <paramref name="directory"/>.
    /// An absolute or home-relative name stands alone; with no directory
    /// the name is returned as-is (the server resolves it against home).
    /// </summary>
    public static string Combine(string? directory, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0) return directory ?? "";
        if (name[0] is '/' or '~') return name;
        if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        if (string.IsNullOrEmpty(directory)) return name;
        return directory.EndsWith('/') ? directory + name : directory + "/" + name;
    }

    /// <summary>
    /// The form scp's SFTP mode understands. A relative path is relative
    /// to the login directory there, so <c>~</c> and <c>~/x</c> become
    /// <c>.</c> and <c>x</c>; <c>~other/x</c> is left for the server.
    /// </summary>
    public static string ForSftp(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path == "~" || path == "~/") return ".";
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            var rest = path[2..].TrimStart('/');
            return rest.Length == 0 ? "." : rest;
        }
        return path;
    }

    /// <summary>
    /// Escape glob metacharacters so a download names exactly one path.
    /// scp expands a remote source as a pattern; a file called
    /// <c>report[1].txt</c> must not be read as a character class.
    /// </summary>
    public static string EscapeGlob(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.IndexOfAny(['*', '?', '[', ']', '\\']) < 0) return path;
        var sb = new StringBuilder(path.Length + 8);
        foreach (var c in path)
        {
            if (c is '*' or '?' or '[' or ']' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The last component, for naming the local copy.</summary>
    public static string FileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>
    /// Tidy a word lifted from the terminal into a path candidate: drop
    /// the decoration <c>ls -F</c> appends (<c>*</c>, <c>@</c>, <c>|</c>,
    /// <c>=</c>) and surrounding quotes. A trailing slash is kept off too,
    /// since a directory downloads by name either way.
    /// </summary>
    public static string? CandidateFromWord(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        var s = word.Trim();
        if (s.Length >= 2 && (s[0] is '\'' or '"') && s[^1] == s[0]) s = s[1..^1];
        if (s.Length > 1 && s[^1] is '*' or '@' or '|' or '=') s = s[..^1];
        if (s.Length > 1) s = s.TrimEnd('/') is { Length: > 0 } t ? t : "/";
        if (s.Length == 0 || s is "." or "..") return null;
        return IsValid(s) ? s : null;
    }
}
