using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Ghostty.Core.Interop;

namespace Ghostty.Core.Clipboard;

/// <summary>
/// Builds an unmanaged ghostty_clipboard_complete_s for
/// ghostty_surface_complete_clipboard_request and frees every allocation
/// it made on Dispose.
///
/// libghostty reads the struct and everything it points at during the
/// call and does not retain any of it, so the whole graph can be freed
/// as soon as the call returns -- but not before. Callers must keep the
/// instance alive across the call, which is what the using-block at each
/// call site is for.
///
/// Payload data is copied as raw bytes, never as a C string: the header
/// is explicit that clipboard contents are binary-safe and not
/// necessarily null-terminated. The MIME names beside them ARE C strings.
///
/// Lives in Ghostty.Core rather than beside the bridge that uses it
/// because Ghostty.Tests cannot reference the WinUI project. Pairing this
/// writer with ClipboardContentMarshaller gives a round-trip that can be
/// asserted -- build the native memory, read it back, compare the bytes --
/// which is the only cheap check that covers the stride, the field
/// offsets and the length together. It has no WinUI dependency, so the
/// move costs nothing.
/// </summary>
public sealed partial class NativeClipboardComplete : IDisposable
{
    private readonly List<IntPtr> _allocations = new();
    private IntPtr _struct;
    private bool _disposed;

    public NativeClipboardComplete(
        IReadOnlyList<ClipboardPayload> contents,
        IReadOnlyList<string> available,
        bool confirmed,
        bool remember)
    {
        // Written field-by-field rather than via StructureToPtr: this
        // assembly sets DisableRuntimeMarshalling, under which StructureToPtr
        // throws for the declared struct types. See GhosttyClipboardLayout.
        _struct = Alloc(GhosttyClipboardLayout.CompleteSize);
        Marshal.WriteIntPtr(_struct, GhosttyClipboardLayout.CompleteContents, BuildContents(contents));
        Marshal.WriteIntPtr(_struct, GhosttyClipboardLayout.CompleteContentsLen, (IntPtr)contents.Count);
        Marshal.WriteIntPtr(_struct, GhosttyClipboardLayout.CompleteAvailable, BuildStringArray(available));
        Marshal.WriteIntPtr(_struct, GhosttyClipboardLayout.CompleteAvailableLen, (IntPtr)available.Count);
        Marshal.WriteByte(_struct, GhosttyClipboardLayout.CompleteConfirmed, confirmed ? (byte)1 : (byte)0);
        Marshal.WriteByte(_struct, GhosttyClipboardLayout.CompleteRemember, remember ? (byte)1 : (byte)0);
    }

    /// <summary>Pointer to the ghostty_clipboard_complete_s. Valid until Dispose.</summary>
    public IntPtr Pointer => _struct;

    private IntPtr BuildContents(IReadOnlyList<ClipboardPayload> contents)
    {
        if (contents.Count == 0)
            return IntPtr.Zero;

        var stride = GhosttyClipboardLayout.ContentSize;
        var array = Alloc(stride * contents.Count);

        for (var i = 0; i < contents.Count; i++)
        {
            var payload = contents[i];
            var span = payload.Data.Span;

            var dataPtr = IntPtr.Zero;
            if (span.Length > 0)
            {
                dataPtr = Alloc(span.Length);
                Marshal.Copy(span.ToArray(), 0, dataPtr, span.Length);
            }
            else
            {
                // A zero-length payload still needs a non-null pointer:
                // the receiving side checks the pointer before the length.
                dataPtr = Alloc(1);
                Marshal.WriteByte(dataPtr, 0, 0);
            }

            var at = IntPtr.Add(array, i * stride);
            Marshal.WriteIntPtr(at, GhosttyClipboardLayout.ContentMime, AllocUtf8(payload.Mime));
            Marshal.WriteIntPtr(at, GhosttyClipboardLayout.ContentData, dataPtr);
            Marshal.WriteIntPtr(at, GhosttyClipboardLayout.ContentLen, (IntPtr)span.Length);
        }

        return array;
    }

    private IntPtr BuildStringArray(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return IntPtr.Zero;

        var array = Alloc(IntPtr.Size * values.Count);
        for (var i = 0; i < values.Count; i++)
            Marshal.WriteIntPtr(array, i * IntPtr.Size, AllocUtf8(values[i]));

        return array;
    }

    private IntPtr AllocUtf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var ptr = Alloc(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return ptr;
    }

    private IntPtr Alloc(int bytes)
    {
        var ptr = Marshal.AllocHGlobal(bytes);
        _allocations.Add(ptr);
        return ptr;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _struct = IntPtr.Zero;
        foreach (var ptr in _allocations)
            Marshal.FreeHGlobal(ptr);
        _allocations.Clear();
    }
}
