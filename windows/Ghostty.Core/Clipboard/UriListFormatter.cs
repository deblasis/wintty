using System;
using System.Collections.Generic;
using System.Text;

namespace Ghostty.Core.Clipboard;

/// <summary>
/// Formats local file paths as a text/uri-list payload, per RFC 2483:
/// one URI per line, CRLF separated.
///
/// This is the Windows counterpart to what macOS serves from NSPasteboard
/// file URLs. It exists as pure logic, separate from the WinUI backend, so
/// the escaping rules can be tested without a clipboard: the cases that
/// matter are paths with spaces, non-ASCII, and characters that are legal
/// in a Windows filename but not in a URI, and those are miserable to
/// exercise through the real clipboard.
/// </summary>
public static class UriListFormatter
{
    /// <summary>
    /// Build a text/uri-list body from absolute local paths. Paths that
    /// cannot be expressed as a file URI are skipped rather than emitted
    /// malformed. Returns null when nothing usable remains, so callers can
    /// omit the representation entirely instead of offering an empty one.
    /// </summary>
    public static string? Format(IEnumerable<string> paths)
    {
        var lines = new List<string>();

        foreach (var path in paths)
        {
            var uri = ToFileUri(path);
            if (uri is not null) lines.Add(uri);
        }

        if (lines.Count == 0) return null;

        // RFC 2483 is CRLF separated. No trailing terminator: a bare CRLF
        // at the end reads as an empty final URI to strict parsers.
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// Convert one absolute Windows path to a file:// URI, percent-encoding
    /// what needs it. Returns null for a path that is empty, relative, or
    /// otherwise not expressible.
    /// </summary>
    public static string? ToFileUri(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            // Uri does the percent-encoding, including the awkward cases:
            // spaces, '#', '%', and non-ASCII, which Windows filenames
            // allow and URIs do not. Hand-rolling this is how you end up
            // with a path that truncates at the first '#'.
            var uri = new Uri(path, UriKind.Absolute);
            return uri.IsFile ? uri.AbsoluteUri : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
