using System;

namespace Ghostty.Core.Clipboard;

/// <summary>
/// The Windows-side clipboard formats we know how to read and write. This
/// is the analogue of macOS NSPasteboard.PasteboardType. Anything else
/// libghostty hands us is silently skipped (forward-compatible with
/// future MIMEs).
/// </summary>
public enum WindowsClipboardFormat
{
    Text,
    Html,
    UriList,
    Image,
}

public static class WindowsClipboardFormatMap
{
    /// <summary>
    /// Map a libghostty MIME string to the Windows format we will use.
    /// Returns null when the MIME is unknown, null, or empty. Callers
    /// should treat that as "skip this entry".
    ///
    /// Matched case-insensitively: RFC 2045 makes the type and subtype
    /// case-insensitive, and the MIME names on this boundary come from
    /// terminal programs rather than from us.
    /// </summary>
    public static WindowsClipboardFormat? FromMime(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return null;

        if (Eq(mime, ClipboardMime.TextPlain)) return WindowsClipboardFormat.Text;
        if (Eq(mime, ClipboardMime.TextHtml)) return WindowsClipboardFormat.Html;
        if (Eq(mime, ClipboardMime.TextUriList)) return WindowsClipboardFormat.UriList;
        if (Eq(mime, ClipboardMime.ImagePng)) return WindowsClipboardFormat.Image;

        return null;
    }

    /// <summary>
    /// The subset we can put ON the clipboard, as opposed to take off it.
    ///
    /// Reading and writing are deliberately not the same set. We can read
    /// StorageItems and serve them as text/uri-list, but writing files back
    /// means materialising StorageFile objects, and we can read a bitmap but
    /// not yet encode one. Collapsing the two sets is not a cosmetic
    /// mistake: the write path drops entries it cannot handle, so a payload
    /// that passes the filter and is then dropped leaves an EMPTY
    /// DataPackage, and handing SetContent an empty package clears the
    /// user's clipboard instead of leaving it alone.
    /// </summary>
    public static WindowsClipboardFormat? FromMimeForWrite(string? mime) => FromMime(mime) switch
    {
        WindowsClipboardFormat.Text => WindowsClipboardFormat.Text,
        WindowsClipboardFormat.Html => WindowsClipboardFormat.Html,
        _ => null,
    };

    /// <summary>The canonical MIME name for a format.</summary>
    public static string ToMime(WindowsClipboardFormat format) => format switch
    {
        WindowsClipboardFormat.Text => ClipboardMime.TextPlain,
        WindowsClipboardFormat.Html => ClipboardMime.TextHtml,
        WindowsClipboardFormat.UriList => ClipboardMime.TextUriList,
        WindowsClipboardFormat.Image => ClipboardMime.ImagePng,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static bool Eq(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
