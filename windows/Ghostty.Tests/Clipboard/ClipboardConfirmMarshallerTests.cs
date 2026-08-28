using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Ghostty.Core.Clipboard;
using Xunit;

namespace Ghostty.Tests.Clipboard;

/// <summary>
/// Tests ClipboardConfirmMarshaller against a hand-built
/// ghostty_clipboard_confirm_s.
///
/// This is the payload the permission prompt is built from, so a
/// misreading here does not crash: it shows the user the wrong thing and
/// then asks them to approve it. That makes it worth more coverage than
/// its size suggests.
/// </summary>
public sealed class ClipboardConfirmMarshallerTests
{
    // ghostty_clipboard_confirm_s
    //   const ghostty_clipboard_content_s *contents;   0
    //   size_t contents_len;                           8
    //   const char *const *available;                 16
    //   size_t available_len;                         24
    //   const char *name;                             32
    //   bool can_remember;                            40
    private const int ContentsOffset = 0;
    private const int ContentsLenOffset = 8;
    private const int AvailableOffset = 16;
    private const int AvailableLenOffset = 24;
    private const int NameOffset = 32;
    private const int CanRememberOffset = 40;
    private const int ConfirmSize = 48;

    private const int ContentStride = 24; // 2 pointers + size_t

    private sealed class Scratch : IDisposable
    {
        private readonly List<IntPtr> _hglobal = new();
        private readonly List<IntPtr> _cotask = new();

        public IntPtr Alloc(int bytes)
        {
            var p = Marshal.AllocHGlobal(bytes);
            _hglobal.Add(p);
            return p;
        }

        public IntPtr Utf8(string value)
        {
            var p = Marshal.StringToCoTaskMemUTF8(value);
            _cotask.Add(p);
            return p;
        }

        public IntPtr Bytes(byte[] data)
        {
            var p = Alloc(Math.Max(data.Length, 1));
            if (data.Length > 0) Marshal.Copy(data, 0, p, data.Length);
            return p;
        }

        /// <summary>Builds a ghostty_clipboard_content_s array.</summary>
        public IntPtr Contents(params (string Mime, byte[] Data)[] entries)
        {
            var array = Alloc(ContentStride * Math.Max(entries.Length, 1));
            for (var i = 0; i < entries.Length; i++)
            {
                var at = IntPtr.Add(array, i * ContentStride);
                Marshal.WriteIntPtr(at, 0, Utf8(entries[i].Mime));
                Marshal.WriteIntPtr(at, 8, Bytes(entries[i].Data));
                Marshal.WriteIntPtr(at, 16, (IntPtr)entries[i].Data.Length);
            }

            return array;
        }

        /// <summary>Builds a const char* const* array. A null name yields a null slot.</summary>
        public IntPtr StringArray(params string?[] values)
        {
            var array = Alloc(IntPtr.Size * Math.Max(values.Length, 1));
            for (var i = 0; i < values.Length; i++)
            {
                Marshal.WriteIntPtr(array, i * IntPtr.Size,
                    values[i] is null ? IntPtr.Zero : Utf8(values[i]!));
            }

            return array;
        }

        public IntPtr Confirm(
            IntPtr contents, int contentsLen,
            IntPtr available, int availableLen,
            IntPtr name, bool canRemember)
        {
            var p = Alloc(ConfirmSize);
            Marshal.WriteIntPtr(p, ContentsOffset, contents);
            Marshal.WriteIntPtr(p, ContentsLenOffset, (IntPtr)contentsLen);
            Marshal.WriteIntPtr(p, AvailableOffset, available);
            Marshal.WriteIntPtr(p, AvailableLenOffset, (IntPtr)availableLen);
            Marshal.WriteIntPtr(p, NameOffset, name);
            Marshal.WriteByte(p, CanRememberOffset, canRemember ? (byte)1 : (byte)0);
            return p;
        }

        public void Dispose()
        {
            foreach (var p in _hglobal) Marshal.FreeHGlobal(p);
            foreach (var p in _cotask) Marshal.FreeCoTaskMem(p);
        }
    }

    private static byte[] Utf8Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Read_NullPointer_ReturnsEmptySnapshot()
    {
        var snapshot = ClipboardConfirmMarshaller.Read(IntPtr.Zero);

        Assert.Empty(snapshot.Contents);
        Assert.Empty(snapshot.Available);
        Assert.Null(snapshot.Name);
        Assert.False(snapshot.CanRemember);
        Assert.Equal(string.Empty, snapshot.PreviewText);
    }

    [Fact]
    public void Read_FullPayload_CopiesEveryField()
    {
        using var scratch = new Scratch();
        var contents = scratch.Contents(("text/plain", Utf8Bytes("hello")));
        var available = scratch.StringArray("text/plain", "text/html");
        var confirm = scratch.Confirm(contents, 1, available, 2, scratch.Utf8("ssh-session"), true);

        var snapshot = ClipboardConfirmMarshaller.Read(confirm);

        var entry = Assert.Single(snapshot.Contents);
        Assert.Equal("text/plain", entry.Mime);
        Assert.Equal("hello", entry.Text);
        Assert.Equal(new[] { "text/plain", "text/html" }, snapshot.Available);
        Assert.Equal("ssh-session", snapshot.Name);
        Assert.True(snapshot.CanRemember);
    }

    [Fact]
    public void Read_NullName_IsNullNotEmpty()
    {
        // The dialog shows a "who is asking" line only when a name exists.
        // Empty string and absent must stay distinguishable.
        using var scratch = new Scratch();
        var confirm = scratch.Confirm(IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, false);

        var snapshot = ClipboardConfirmMarshaller.Read(confirm);

        Assert.Null(snapshot.Name);
        Assert.False(snapshot.CanRemember);
    }

    [Fact]
    public void Read_CanRememberFalse_IsFalse()
    {
        using var scratch = new Scratch();
        var confirm = scratch.Confirm(IntPtr.Zero, 0, IntPtr.Zero, 0, scratch.Utf8("x"), false);

        Assert.False(ClipboardConfirmMarshaller.Read(confirm).CanRemember);
    }

    [Fact]
    public void Read_AvailableWithNullSlot_SkipsIt()
    {
        using var scratch = new Scratch();
        var available = scratch.StringArray("text/plain", null, "image/png");
        var confirm = scratch.Confirm(IntPtr.Zero, 0, available, 3, IntPtr.Zero, false);

        var snapshot = ClipboardConfirmMarshaller.Read(confirm);

        Assert.Equal(new[] { "text/plain", "image/png" }, snapshot.Available);
    }

    // --- preview selection ---------------------------------------------

    [Fact]
    public void PreviewText_PicksTheTextEntry_NotTheFirstEntry()
    {
        // Ordering is libghostty's, not ours. If an image/* representation
        // comes first, decoding entry 0 as UTF-8 renders binary as mojibake
        // in a security prompt.
        using var scratch = new Scratch();
        var contents = scratch.Contents(
            ("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
            ("text/plain", Utf8Bytes("the real preview")));
        var confirm = scratch.Confirm(contents, 2, IntPtr.Zero, 0, IntPtr.Zero, false);

        var snapshot = ClipboardConfirmMarshaller.Read(confirm);

        Assert.Equal("the real preview", snapshot.PreviewText);
    }

    [Fact]
    public void PreviewText_ImageOnly_IsEmptyRatherThanMojibake()
    {
        using var scratch = new Scratch();
        var contents = scratch.Contents(("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF }));
        var confirm = scratch.Confirm(contents, 1, IntPtr.Zero, 0, IntPtr.Zero, false);

        var snapshot = ClipboardConfirmMarshaller.Read(confirm);

        Assert.Equal(string.Empty, snapshot.PreviewText);
    }

    [Fact]
    public void PreviewText_MatchesTextSubtypesCaseInsensitively()
    {
        using var scratch = new Scratch();
        var contents = scratch.Contents(("TEXT/URI-LIST", Utf8Bytes("file:///c:/tmp/a.txt")));
        var confirm = scratch.Confirm(contents, 1, IntPtr.Zero, 0, IntPtr.Zero, false);

        Assert.Equal("file:///c:/tmp/a.txt", ClipboardConfirmMarshaller.Read(confirm).PreviewText);
    }

    [Fact]
    public void PreviewText_NoContents_IsEmpty()
    {
        using var scratch = new Scratch();
        var confirm = scratch.Confirm(IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, false);

        Assert.Equal(string.Empty, ClipboardConfirmMarshaller.Read(confirm).PreviewText);
    }
}
