using System;
using System.Text;

namespace Ghostty.Core.Clipboard;

/// <summary>
/// One MIME-tagged clipboard entry. Mirrors ghostty_clipboard_content_s.
///
/// Data is bytes, not a string, because the header is explicit that the
/// contents are "binary-safe with an explicit length; not necessarily
/// null-terminated". Treating it as a C string was safe only while every
/// payload was text; image/* entries from the Kitty clipboard protocol
/// are neither null-terminated nor valid UTF-8, and reading one with
/// strlen walks off the end of the buffer.
/// </summary>
public readonly record struct ClipboardPayload(string Mime, ReadOnlyMemory<byte> Data)
{
    /// <summary>
    /// The payload decoded as UTF-8. Only meaningful for text-ish MIME
    /// types; callers that handle image/* must use <see cref="Data"/>.
    /// </summary>
    public string Text => Encoding.UTF8.GetString(Data.Span);

    /// <summary>Convenience for tests and for text the app itself produces.</summary>
    public static ClipboardPayload FromText(string mime, string text) =>
        new(mime, Encoding.UTF8.GetBytes(text));
}
