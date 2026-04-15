namespace Ghostty.Core.Config;

/// <summary>
/// Pure string helpers shared between the config file writer and anything
/// else that has to reconcile WinUI 3 TextBox quirks with libghostty's
/// expectations.
/// </summary>
public static class ConfigText
{
    /// <summary>
    /// Normalize CR and CRLF line endings to LF. WinUI 3's TextBox exposes
    /// its text with bare <c>\r</c> separators (and Windows tooling often
    /// inserts <c>\r\n</c>), but libghostty's config parser splits on
    /// <c>\n</c>; writing <c>\r</c>-only content makes the whole file
    /// parse as a single ill-formed line, silently swallowing every
    /// "unknown field" diagnostic.
    /// </summary>
    public static string NormalizeLineEndings(string content)
        => content.Replace("\r\n", "\n").Replace('\r', '\n');
}
