namespace Ghostty.Core.Input;

/// <summary>
/// Pure-logic helpers for the URL hover-banner UI (per-surface bottom-
/// corner overlay shown when the user hovers an OSC 8 hyperlink or
/// URL-matched text while holding Ctrl).
///
/// Lives in Ghostty.Core so the formatting + truncation rules can be
/// unit-tested without pulling WinAppSDK into Ghostty.Tests.
/// </summary>
public static class HoverLinkText
{
    /// <summary>
    /// Default character cap before mid-ellipsis truncation kicks in.
    /// Picked to fit comfortably below a typical pane width without
    /// running the banner across the whole surface.
    /// </summary>
    public const int DefaultMaxUrlChars = 80;

    /// <summary>
    /// Hint prefix shown ahead of the URL. Discoverability aid for
    /// Windows users; upstream macOS / GTK banners show URL only.
    /// </summary>
    public const string HintPrefix = "Ctrl+Click to open: ";

    /// <summary>
    /// Produce the full banner text: <c>"Ctrl+Click to open: &lt;url&gt;"</c>,
    /// with the URL mid-ellipsis-truncated if it exceeds
    /// <paramref name="maxUrlChars"/>. Returns <see cref="string.Empty"/>
    /// when <paramref name="url"/> is null or empty — caller should
    /// suppress the banner in that case.
    /// </summary>
    public static string Format(string? url, int maxUrlChars = DefaultMaxUrlChars)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        return HintPrefix + TruncateMid(url, maxUrlChars);
    }

    /// <summary>
    /// Mid-ellipsis truncation: keep the start and end of the string,
    /// replace the middle with a single Unicode horizontal ellipsis
    /// (<c>…</c>). Designed for URLs where the scheme + host (start) and
    /// the resource path tail (end) carry the most information; the
    /// middle of a deep path is the cheapest part to drop.
    ///
    /// Returns the original string unchanged if its length is &lt;=
    /// <paramref name="maxChars"/> or if <paramref name="maxChars"/>
    /// is too small for the ellipsis to fit (degrades to a hard prefix
    /// truncation in that pathological case).
    /// </summary>
    public static string TruncateMid(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (maxChars <= 0) return string.Empty;
        if (s.Length <= maxChars) return s;

        // Need at least 3 chars to fit "x…y"; below that, just hard-cut.
        if (maxChars < 3) return s[..maxChars];

        // Bias slightly toward the END of the URL: the path tail is more
        // informative than the host prefix once we're already past
        // truncation. (maxChars - 1) / 2 keeps the start, the remainder
        // goes to the end.
        int keepStart = (maxChars - 1) / 2;
        int keepEnd = maxChars - 1 - keepStart;
        return string.Concat(s.AsSpan(0, keepStart), "…", s.AsSpan(s.Length - keepEnd));
    }
}
