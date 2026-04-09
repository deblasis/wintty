namespace Ghostty.Core.Clipboard;

/// <summary>
/// The Windows-side clipboard formats we know how to write. This is the
/// analogue of macOS NSPasteboard.PasteboardType. Anything else libghostty
/// hands us is silently skipped (forward-compatible with future MIMEs).
/// </summary>
public enum WindowsClipboardFormat
{
    Text,
    Html,
}

public static class WindowsClipboardFormatMap
{
    /// <summary>
    /// Map a libghostty MIME string to the Windows format we will write.
    /// Returns null when the MIME is unknown, null, or empty. Callers
    /// should treat that as "skip this entry".
    /// </summary>
    public static WindowsClipboardFormat? FromMime(string? mime) => mime switch
    {
        ClipboardMime.TextPlain => WindowsClipboardFormat.Text,
        ClipboardMime.TextHtml => WindowsClipboardFormat.Html,
        _ => null,
    };
}
