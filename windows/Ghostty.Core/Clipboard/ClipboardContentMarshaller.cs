using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ghostty.Core.Clipboard;

/// <summary>
/// Walks a libghostty (ghostty_clipboard_content_s*, count) array and
/// produces managed ClipboardPayload values. Extracted from the WinUI
/// bridge so the marshalling logic is unit-testable in pure net9.0,
/// without WinUI dependencies.
///
/// Memory ownership: the native pointers are owned by libghostty for
/// the duration of the write_clipboard_cb callback. This method MUST
/// be called synchronously from inside that callback (or from a copy
/// taken before the callback returns) so the resulting strings are
/// managed copies and are safe to use after the callback returns.
///
/// The native struct layout is:
///   typedef struct {
///     const char *mime;
///     const char *data;
///   } ghostty_clipboard_content_s;
/// which marshals to two pointers back-to-back, sized 2*sizeof(void*).
/// </summary>
public static class ClipboardContentMarshaller
{
    // sizeof(ghostty_clipboard_content_s) on the current platform.
    private static readonly int StructSize = 2 * IntPtr.Size;

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
            var mimePtr = Marshal.ReadIntPtr(entryAddr, 0);
            var dataPtr = Marshal.ReadIntPtr(entryAddr, IntPtr.Size);

            if (mimePtr == IntPtr.Zero || dataPtr == IntPtr.Zero)
                continue;

            var mime = Marshal.PtrToStringUTF8(mimePtr) ?? string.Empty;
            var data = Marshal.PtrToStringUTF8(dataPtr) ?? string.Empty;

            result.Add(new ClipboardPayload(mime, data));
        }

        return result;
    }
}
