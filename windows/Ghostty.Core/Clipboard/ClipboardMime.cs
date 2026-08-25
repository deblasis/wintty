namespace Ghostty.Core.Clipboard;

/// <summary>
/// MIME type strings used by libghostty when passing clipboard payloads
/// across the C ABI. Matches the values produced by src/Surface.zig.
/// </summary>
public static class ClipboardMime
{
    public const string TextPlain = "text/plain";
    public const string TextHtml = "text/html";

    /// <summary>
    /// Files copied in Explorer. macOS serves the NSPasteboard file URLs
    /// as this; the Windows equivalent is CF_HDROP / StorageItems.
    /// </summary>
    public const string TextUriList = "text/uri-list";

    public const string ImagePng = "image/png";
}
