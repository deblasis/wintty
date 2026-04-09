namespace Ghostty.Core.Clipboard;

/// <summary>
/// MIME type strings used by libghostty when passing clipboard payloads
/// across the C ABI. Matches the values produced by src/Surface.zig.
/// </summary>
public static class ClipboardMime
{
    public const string TextPlain = "text/plain";
    public const string TextHtml = "text/html";
}
