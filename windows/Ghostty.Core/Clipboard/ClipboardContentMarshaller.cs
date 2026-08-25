using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ghostty.Core.Interop;

namespace Ghostty.Core.Clipboard;

/// <summary>
/// Walks a libghostty (ghostty_clipboard_content_s*, count) array and
/// produces managed ClipboardPayload values. Extracted from the WinUI
/// bridge so the marshalling logic is unit-testable in pure net9.0,
/// without WinUI dependencies.
///
/// Memory ownership: the native pointers are owned by libghostty for
/// the duration of the callback. This method MUST be called
/// synchronously from inside that callback (or from a copy taken before
/// the callback returns) so the resulting arrays are managed copies and
/// are safe to use after the callback returns.
///
/// The native struct layout is:
///   typedef struct {
///     const char *mime;
///     const char *data;
///     size_t len;
///   } ghostty_clipboard_content_s;
/// Two pointers then a size_t, all pointer-sized and pointer-aligned on
/// every target we build for, so the stride is 3*sizeof(void*).
///
/// `len` is load-bearing, not decorative. The header states the data is
/// binary-safe and not necessarily null-terminated, so the previous
/// PtrToStringUTF8 read was only ever correct for text payloads; on an
/// image/* entry it runs strlen past the end of the buffer.
/// </summary>
public static class ClipboardContentMarshaller
{
    private const int StructSize = GhosttyClipboardLayout.ContentSize;
    private const int MimeOffset = GhosttyClipboardLayout.ContentMime;
    private const int DataOffset = GhosttyClipboardLayout.ContentData;
    private const int LenOffset = GhosttyClipboardLayout.ContentLen;

    /// <summary>
    /// Read <paramref name="count"/> entries starting at <paramref name="content"/>.
    /// Returns an empty list when content is null or count is zero.
    /// Skips entries whose mime or data pointer is null (defensive).
    /// </summary>
    public static IReadOnlyList<ClipboardPayload> Read(IntPtr content, nuint count)
    {
        if (content == IntPtr.Zero || count == 0)
            return Array.Empty<ClipboardPayload>();

        var result = new List<ClipboardPayload>((int)count);

        for (nuint i = 0; i < count; i++)
        {
            var entryAddr = IntPtr.Add(content, checked((int)(i * (nuint)StructSize)));
            var mimePtr = Marshal.ReadIntPtr(entryAddr, MimeOffset);
            var dataPtr = Marshal.ReadIntPtr(entryAddr, DataOffset);
            var len = (nuint)(nint)Marshal.ReadIntPtr(entryAddr, LenOffset);

            if (mimePtr == IntPtr.Zero || dataPtr == IntPtr.Zero)
                continue;

            // The mime name IS a C string; only the payload is binary.
            var mime = Marshal.PtrToStringUTF8(mimePtr) ?? string.Empty;

            // A zero-length entry is legal (an empty text/plain, say) and
            // must not become a strlen read of whatever dataPtr points at.
            var data = len == 0
                ? Array.Empty<byte>()
                : new byte[checked((int)len)];
            if (len != 0)
                Marshal.Copy(dataPtr, data, 0, data.Length);

            result.Add(new ClipboardPayload(mime, data));
        }

        return result;
    }
}
