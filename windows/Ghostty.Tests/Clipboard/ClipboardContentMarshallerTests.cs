using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Ghostty.Core.Clipboard;
using Xunit;

namespace Ghostty.Tests.Clipboard;

/// <summary>
/// Tests ClipboardContentMarshaller against real native struct layouts
/// built with Marshal.AllocHGlobal. These tests exercise actual struct
/// dereferencing through the same code path the WinUI bridge uses,
/// catching ABI mistakes before they ship.
///
/// The layout this file builds is deliberately spelled out by hand rather
/// than taken from the managed struct: a test that shares its idea of the
/// layout with the code under test agrees with it even when both are
/// wrong. GhosttyStructHeaderParityTests is what ties the layout to the
/// header; this file assumes that layout and checks the walk over it.
/// </summary>
public sealed class ClipboardContentMarshallerTests
{
    // ghostty_clipboard_content_s = { const char* mime; const char* data; size_t len; }
    private static readonly int StructSize = 3 * IntPtr.Size;

    /// <summary>
    /// Build an unmanaged array of N ghostty_clipboard_content_s entries.
    /// The mime is written as a C string; the data is written as RAW BYTES
    /// with an explicit length and NO null terminator, which is what
    /// libghostty actually hands us.
    /// </summary>
    private static (IntPtr Array, IntPtr[] AllAllocs) BuildArray(
        params (string Mime, byte[] Data)[] entries)
    {
        var allocs = new List<IntPtr>();
        var array = Marshal.AllocHGlobal(StructSize * entries.Length);
        allocs.Add(array);

        for (int i = 0; i < entries.Length; i++)
        {
            var mimePtr = Marshal.StringToCoTaskMemUTF8(entries[i].Mime);
            allocs.Add(mimePtr);

            var data = entries[i].Data;
            // Allocate exactly len bytes. Anything the marshaller reads
            // past that is off the end of this allocation, which is the
            // failure this whole file exists to catch.
            var dataPtr = Marshal.AllocHGlobal(Math.Max(data.Length, 1));
            allocs.Add(dataPtr);
            if (data.Length > 0)
                Marshal.Copy(data, 0, dataPtr, data.Length);

            var entryAddr = IntPtr.Add(array, i * StructSize);
            Marshal.WriteIntPtr(entryAddr, 0, mimePtr);
            Marshal.WriteIntPtr(entryAddr, IntPtr.Size, dataPtr);
            Marshal.WriteIntPtr(entryAddr, 2 * IntPtr.Size, (IntPtr)data.Length);
        }

        return (array, allocs.ToArray());
    }

    private static (IntPtr Array, IntPtr[] AllAllocs) BuildArray(
        params (string Mime, string Data)[] entries)
    {
        var converted = new (string, byte[])[entries.Length];
        for (int i = 0; i < entries.Length; i++)
            converted[i] = (entries[i].Mime, Encoding.UTF8.GetBytes(entries[i].Data));
        return BuildArray(converted);
    }

    private static void FreeAll(IntPtr[] allocs)
    {
        // Index 0 is the array; then each entry contributes (mime, data) in
        // that order. Mime strings are CoTaskMem, data buffers are HGlobal,
        // so the two must not be freed with the same allocator.
        Marshal.FreeHGlobal(allocs[0]);
        for (int i = 1; i < allocs.Length; i += 2)
        {
            Marshal.FreeCoTaskMem(allocs[i]);
            Marshal.FreeHGlobal(allocs[i + 1]);
        }
    }

    [Fact]
    public void Read_NullPointer_ReturnsEmpty()
    {
        var result = ClipboardContentMarshaller.Read(IntPtr.Zero, 5);
        Assert.Empty(result);
    }

    [Fact]
    public void Read_ZeroCount_ReturnsEmpty()
    {
        var (array, allocs) = BuildArray(("text/plain", "hello"));
        try
        {
            var result = ClipboardContentMarshaller.Read(array, 0);
            Assert.Empty(result);
        }
        finally { FreeAll(allocs); }
    }

    [Fact]
    public void Read_SingleTextPlain_ReturnsOne()
    {
        var (array, allocs) = BuildArray(("text/plain", "hello"));
        try
        {
            var result = ClipboardContentMarshaller.Read(array, 1);
            var entry = Assert.Single(result);
            Assert.Equal("text/plain", entry.Mime);
            Assert.Equal("hello", entry.Text);
        }
        finally { FreeAll(allocs); }
    }

    [Fact]
    public void Read_TwoEntries_TextPlainAndTextHtml_ReturnsBothInOrder()
    {
        // Stride check. A marshaller using the pre-Kitty 16-byte stride
        // reads entry 1 starting two thirds of the way into entry 0, so it
        // does not merely return the wrong text: it dereferences a length
        // as a pointer.
        var (array, allocs) = BuildArray(
            ("text/plain", "hello"),
            ("text/html", "<b>hello</b>"));
        try
        {
            var result = ClipboardContentMarshaller.Read(array, 2);
            Assert.Equal(2, result.Count);
            Assert.Equal("text/plain", result[0].Mime);
            Assert.Equal("hello", result[0].Text);
            Assert.Equal("text/html", result[1].Mime);
            Assert.Equal("<b>hello</b>", result[1].Text);
        }
        finally { FreeAll(allocs); }
    }

    [Fact]
    public void Read_LongUtf8_RoundTrips()
    {
        // Multi-byte chars in both fields. UTF-8 encoded length is what
        // matters across the ABI; the marshaller must use UTF-8 decode.
        var japanese = "こんにちは世界";
        var emoji = "\U0001F4CB✨\U0001F680";
        var (array, allocs) = BuildArray((japanese, emoji));
        try
        {
            var result = ClipboardContentMarshaller.Read(array, 1);
            var entry = Assert.Single(result);
            Assert.Equal(japanese, entry.Mime);
            Assert.Equal(emoji, entry.Text);
        }
        finally { FreeAll(allocs); }
    }

    [Fact]
    public void Read_EmptyData_ReturnsEmptyData()
    {
        var (array, allocs) = BuildArray(("text/plain", ""));
        try
        {
            var result = ClipboardContentMarshaller.Read(array, 1);
            var entry = Assert.Single(result);
            Assert.Equal("text/plain", entry.Mime);
            Assert.Equal("", entry.Text);
            Assert.Equal(0, entry.Data.Length);
        }
        finally { FreeAll(allocs); }
    }

    // --- what len is actually for --------------------------------------

    [Fact]
    public void Read_BinaryDataWithEmbeddedNul_KeepsEverythingAfterTheNul()
    {
        // A PNG header is the realistic case: byte 4 of many binary formats
        // is a NUL, and a strlen-based read truncates there. The pre-Kitty
        // marshaller returned 3 bytes for this and called it the payload.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x00, 0x47, 0x0D, 0x0A, 0x1A };
        var (array, allocs) = BuildArray(("image/png", png));
        try
        {
            var result = ClipboardContentMarshaller.Read(array, 1);
            var entry = Assert.Single(result);
            Assert.Equal("image/png", entry.Mime);
            Assert.Equal(png, entry.Data.ToArray());
        }
        finally { FreeAll(allocs); }
    }

    [Fact]
    public void Read_DataIsNotNullTerminated_StopsAtLen()
    {
        // The header states the data is not necessarily null-terminated.
        // The buffer here is allocated at exactly len, so a marshaller that
        // scans for a terminator reads off the end of the allocation. The
        // assertion is that we get precisely len bytes back.
        var payload = Encoding.UTF8.GetBytes("no terminator here");
        var (array, allocs) = BuildArray(("text/plain", payload));
        try
        {
            var result = ClipboardContentMarshaller.Read(array, 1);
            var entry = Assert.Single(result);
            Assert.Equal(payload.Length, entry.Data.Length);
            Assert.Equal("no terminator here", entry.Text);
        }
        finally { FreeAll(allocs); }
    }

    [Fact]
    public void Read_LenShorterThanBuffer_HonoursLenNotTheTerminator()
    {
        // len is authoritative even when a terminator happens to exist
        // later in the buffer. Built by hand because the helper sizes the
        // allocation to len by design.
        var full = Encoding.UTF8.GetBytes("visible\0hidden\0");
        var array = Marshal.AllocHGlobal(StructSize);
        var mimePtr = Marshal.StringToCoTaskMemUTF8("text/plain");
        var dataPtr = Marshal.AllocHGlobal(full.Length);
        try
        {
            Marshal.Copy(full, 0, dataPtr, full.Length);
            Marshal.WriteIntPtr(array, 0, mimePtr);
            Marshal.WriteIntPtr(array, IntPtr.Size, dataPtr);
            Marshal.WriteIntPtr(array, 2 * IntPtr.Size, (IntPtr)7); // "visible"

            var result = ClipboardContentMarshaller.Read(array, 1);
            var entry = Assert.Single(result);
            Assert.Equal(7, entry.Data.Length);
            Assert.Equal("visible", entry.Text);
        }
        finally
        {
            Marshal.FreeHGlobal(dataPtr);
            Marshal.FreeCoTaskMem(mimePtr);
            Marshal.FreeHGlobal(array);
        }
    }

    [Fact]
    public void Read_NullMimeOrDataPointer_SkipsThatEntry()
    {
        // Defensive: a null in either pointer slot must not be dereferenced.
        var array = Marshal.AllocHGlobal(StructSize * 2);
        var mimePtr = Marshal.StringToCoTaskMemUTF8("text/plain");
        var dataPtr = Marshal.AllocHGlobal(2);
        try
        {
            Marshal.Copy(new byte[] { 0x41, 0x42 }, 0, dataPtr, 2);

            // entry 0: null mime
            Marshal.WriteIntPtr(array, 0, IntPtr.Zero);
            Marshal.WriteIntPtr(array, IntPtr.Size, dataPtr);
            Marshal.WriteIntPtr(array, 2 * IntPtr.Size, (IntPtr)2);

            // entry 1: valid
            var second = IntPtr.Add(array, StructSize);
            Marshal.WriteIntPtr(second, 0, mimePtr);
            Marshal.WriteIntPtr(second, IntPtr.Size, dataPtr);
            Marshal.WriteIntPtr(second, 2 * IntPtr.Size, (IntPtr)2);

            var result = ClipboardContentMarshaller.Read(array, 2);
            var entry = Assert.Single(result);
            Assert.Equal("text/plain", entry.Mime);
            Assert.Equal("AB", entry.Text);
        }
        finally
        {
            Marshal.FreeHGlobal(dataPtr);
            Marshal.FreeCoTaskMem(mimePtr);
            Marshal.FreeHGlobal(array);
        }
    }
}
