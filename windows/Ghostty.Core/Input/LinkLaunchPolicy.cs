using System;

namespace Ghostty.Core.Input;

/// <summary>What Ctrl+click on a terminal link may do with it.</summary>
public enum LinkLaunchDecision
{
    /// <summary>A web or mail link: open it.</summary>
    Open,

    /// <summary>Any other scheme: show the real URL and ask first.</summary>
    Confirm,

    /// <summary>Not an absolute URI: do nothing.</summary>
    Refuse,
}

/// <summary>
/// Decides whether a clicked link opens directly. The URL comes from the
/// pty: an OSC 8 hyperlink's target, which a program or remote server
/// chooses and which need not match the text it is drawn over. Windows
/// hands any registered scheme to its handler, and several of those
/// handlers (search-ms:, ms-msdt:, ms-appinstaller:, ...) have been used to
/// run code or phish from a single click. So only the schemes a user
/// expects a link to be open without a question; everything else is
/// shown in full and needs a second, deliberate confirmation.
/// </summary>
public static class LinkLaunchPolicy
{
    private static readonly string[] DirectSchemes = ["http", "https", "mailto"];

    public static LinkLaunchDecision Decide(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return LinkLaunchDecision.Refuse;

        uri = parsed;
        foreach (var scheme in DirectSchemes)
        {
            if (string.Equals(parsed.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
                return LinkLaunchDecision.Open;
        }
        return LinkLaunchDecision.Confirm;
    }
}
