namespace Ghostty.Core.Profiles.Probes;

internal static class ProbeUtil
{
    /// <summary>
    /// Wrap a value in double quotes when it contains a space or an
    /// embedded double quote, so that libghostty's ArgIteratorGeneral
    /// (the parser downstream of config.command = .shell) keeps it as a
    /// single argv token. Unquoted strings with spaces produce arg
    /// vectors like ["C:\Program", "Files\Git\bin\bash.exe", ...] and
    /// CreateProcessW fails. Embedded double quotes are escaped with
    /// backslashes so they survive the wrapping; this matters for the
    /// WSL distro-name caller (PR 4 widened the helper's contract from
    /// path-only to any argv token).
    ///
    /// Already-wrapped strings (start AND end with <c>"</c>) are
    /// returned unchanged: WSL hands us bare distro names, but other
    /// callers may pre-quote.
    /// </summary>
    internal static string QuoteIfNeeded(string value)
    {
        if (value.Length == 0) return value;
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') return value;
        if (!value.Contains(' ') && !value.Contains('"')) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
