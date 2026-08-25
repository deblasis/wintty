using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ghostty.Core.Interop;

namespace Ghostty.Core.Clipboard;

/// <summary>
/// Everything a ghostty_clipboard_confirm_s points at, copied into managed
/// memory.
///
/// A snapshot rather than a view because the native graph belongs to
/// libghostty for the duration of the callback only, while the dialog it
/// feeds is shown asynchronously long after that callback has returned.
/// </summary>
public sealed record ClipboardConfirmSnapshot(
    IReadOnlyList<ClipboardPayload> Contents,
    IReadOnlyList<string> Available,
    string? Name,
    bool CanRemember)
{
    /// <summary>
    /// The text representation to show as the dialog preview, or empty when
    /// the payload carries none. Deliberately not "first entry decoded as
    /// UTF-8": on an image-only read that would render a wall of mojibake.
    /// </summary>
    public string PreviewText
    {
        get
        {
            foreach (var payload in Contents)
            {
                if (payload.Mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                    return payload.Text;
            }

            return string.Empty;
        }
    }
}

/// <summary>
/// Reads a ghostty_clipboard_confirm_s into a managed snapshot.
///
/// MUST be called synchronously from inside confirm_read_clipboard_cb.
/// </summary>
public static class ClipboardConfirmMarshaller
{
    public static ClipboardConfirmSnapshot Read(IntPtr confirm)
    {
        if (confirm == IntPtr.Zero)
        {
            return new ClipboardConfirmSnapshot(
                Array.Empty<ClipboardPayload>(), Array.Empty<string>(), null, false);
        }

        // Field-by-field rather than PtrToStructure: this assembly sets
        // DisableRuntimeMarshalling, under which PtrToStructure throws for
        // types declared here. See GhosttyClipboardLayout.
        var contentsPtr = Marshal.ReadIntPtr(confirm, GhosttyClipboardLayout.ConfirmContents);
        var contentsLen = (nuint)(nint)Marshal.ReadIntPtr(confirm, GhosttyClipboardLayout.ConfirmContentsLen);
        var availablePtr = Marshal.ReadIntPtr(confirm, GhosttyClipboardLayout.ConfirmAvailable);
        var availableLen = (nuint)(nint)Marshal.ReadIntPtr(confirm, GhosttyClipboardLayout.ConfirmAvailableLen);
        var namePtr = Marshal.ReadIntPtr(confirm, GhosttyClipboardLayout.ConfirmName);
        var canRemember = Marshal.ReadByte(confirm, GhosttyClipboardLayout.ConfirmCanRemember) != 0;

        var contents = ClipboardContentMarshaller.Read(contentsPtr, contentsLen);
        var available = ReadStringArray(availablePtr, availableLen);
        var name = namePtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(namePtr);

        return new ClipboardConfirmSnapshot(
            contents, available, name, canRemember);
    }

    /// <summary>
    /// Copies a (const char* const*, len) array into managed strings.
    /// Null entries are skipped rather than dereferenced.
    /// </summary>
    public static IReadOnlyList<string> ReadStringArray(IntPtr array, nuint len)
    {
        if (array == IntPtr.Zero || len == 0)
            return Array.Empty<string>();

        var count = checked((int)len);
        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var ptr = Marshal.ReadIntPtr(array, i * IntPtr.Size);
            if (ptr == IntPtr.Zero) continue;

            var value = Marshal.PtrToStringUTF8(ptr);
            if (!string.IsNullOrEmpty(value)) result.Add(value);
        }

        return result;
    }
}
