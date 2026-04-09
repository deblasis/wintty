using System;
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
/// </summary>
public sealed class ClipboardContentMarshallerTests
{
    // ghostty_clipboard_content_s = { const char* mime; const char* data; }
    // sizeof(struct) = 2 * pointer size
    private static readonly int StructSize = 2 * IntPtr.Size;

    /// <summary>
    /// Build an unmanaged array of N ghostty_clipboard_content_s entries,
    /// allocating each C string separately. Returns the array pointer
    /// plus a list of every allocation so the caller can free them.
    /// </summary>
    private static (IntPtr Array, IntPtr[] AllAllocs) BuildArray(params (string Mime, string Data)[] entries)
    {
        var allocs = new System.Collections.Generic.List<IntPtr>();
        var array = Marshal.AllocHGlobal(StructSize * entries.Length);
        allocs.Add(array);

        for (int i = 0; i < entries.Length; i++)
        {
            var mimePtr = Marshal.StringToCoTaskMemUTF8(entries[i].Mime);
            var dataPtr = Marshal.StringToCoTaskMemUTF8(entries[i].Data);
            allocs.Add(mimePtr);
            allocs.Add(dataPtr);

            var entryAddr = IntPtr.Add(array, i * StructSize);
            Marshal.WriteIntPtr(entryAddr, 0, mimePtr);
            Marshal.WriteIntPtr(entryAddr, IntPtr.Size, dataPtr);
        }

        return (array, allocs.ToArray());
    }

    private static void FreeAll(IntPtr[] allocs)
    {
        // First entry is the array itself (HGlobal); the rest are
        // CoTaskMemUTF8 strings.
        Marshal.FreeHGlobal(allocs[0]);
        for (int i = 1; i < allocs.Length; i++)
            Marshal.FreeCoTaskMem(allocs[i]);
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
            Assert.Equal("hello", entry.Data);
        }
        finally { FreeAll(allocs); }
    }

    [Fact]
    public void Read_TwoEntries_TextPlainAndTextHtml_ReturnsBothInOrder()
    {
        // The bug-fix test: the current stub treats `content` as a single
        // pointer and ignores `count`. With the fix, both entries must
        // come back in declaration order.
        var (array, allocs) = BuildArray(
            ("text/plain", "hello"),
            ("text/html", "<b>hello</b>"));
        try
        {
            var result = ClipboardContentMarshaller.Read(array, 2);
            Assert.Equal(2, result.Count);
            Assert.Equal("text/plain", result[0].Mime);
            Assert.Equal("hello", result[0].Data);
            Assert.Equal("text/html", result[1].Mime);
            Assert.Equal("<b>hello</b>", result[1].Data);
        }
        finally { FreeAll(allocs); }
    }

    [Fact]
    public void Read_LongUtf8_RoundTrips()
    {
        // Multi-byte chars in both fields. UTF-8 encoded length is what
        // matters across the ABI; the marshaller must use UTF-8 decode.
        var japanese = "こんにちは世界";
        var emoji = "📋✨🚀";
        var (array, allocs) = BuildArray((japanese, emoji));
        try
        {
            var result = ClipboardContentMarshaller.Read(array, 1);
            var entry = Assert.Single(result);
            Assert.Equal(japanese, entry.Mime);
            Assert.Equal(emoji, entry.Data);
        }
        finally { FreeAll(allocs); }
    }

    [Fact]
    public void Read_EmptyDataString_ReturnsEmptyData()
    {
        var (array, allocs) = BuildArray(("text/plain", ""));
        try
        {
            var result = ClipboardContentMarshaller.Read(array, 1);
            var entry = Assert.Single(result);
            Assert.Equal("text/plain", entry.Mime);
            Assert.Equal("", entry.Data);
        }
        finally { FreeAll(allocs); }
    }
}
