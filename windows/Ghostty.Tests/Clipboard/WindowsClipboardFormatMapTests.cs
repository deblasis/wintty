using Ghostty.Core.Clipboard;
using Xunit;

namespace Ghostty.Tests.Clipboard;

/// <summary>
/// Mirrors macos/Tests/NSPasteboardTests.swift. Verifies the MIME types
/// libghostty emits map to the Windows clipboard formats we support, and
/// that everything else maps to null (silently skipped, not an error).
///
/// Readable and writable are separate questions and this file keeps them
/// separate. Collapsing them is not cosmetic: the write path drops entries
/// it cannot handle, so a MIME that passes the write filter and is then
/// dropped leaves an empty DataPackage, and handing SetContent an empty
/// package clears the user's clipboard.
/// </summary>
public sealed class WindowsClipboardFormatMapTests
{
    [Theory]
    [InlineData("text/plain", WindowsClipboardFormat.Text)]
    [InlineData("text/html", WindowsClipboardFormat.Html)]
    [InlineData("text/uri-list", WindowsClipboardFormat.UriList)]
    [InlineData("image/png", WindowsClipboardFormat.Image)]
    public void FromMime_KnownMimes_Map(string mime, WindowsClipboardFormat expected)
    {
        Assert.Equal(expected, WindowsClipboardFormatMap.FromMime(mime));
    }

    [Theory]
    [InlineData("TEXT/PLAIN")]
    [InlineData("Text/Html")]
    [InlineData("text/URI-LIST")]
    public void FromMime_IsCaseInsensitive(string mime)
    {
        // RFC 2045 makes type and subtype case-insensitive, and these names
        // arrive from terminal programs rather than from us.
        Assert.NotNull(WindowsClipboardFormatMap.FromMime(mime));
    }

    [Theory]
    [InlineData("text/plain", WindowsClipboardFormat.Text)]
    [InlineData("text/html", WindowsClipboardFormat.Html)]
    public void FromMimeForWrite_TextFormats_Map(string mime, WindowsClipboardFormat expected)
    {
        Assert.Equal(expected, WindowsClipboardFormatMap.FromMimeForWrite(mime));
    }

    [Theory]
    [InlineData("text/uri-list")]
    [InlineData("image/png")]
    public void FromMimeForWrite_ReadOnlyFormats_ReturnNull(string mime)
    {
        // We can read StorageItems and serve them as text/uri-list, and read
        // a bitmap, but writing either back means materialising files or
        // encoding an image. Until then these must not pass the write
        // filter, or the write path builds an empty package.
        Assert.Null(WindowsClipboardFormatMap.FromMimeForWrite(mime));
        Assert.NotNull(WindowsClipboardFormatMap.FromMime(mime));
    }

    [Fact]
    public void FromMime_UnknownMime_ReturnsNull()
    {
        Assert.Null(WindowsClipboardFormatMap.FromMime("application/x-something"));
        Assert.Null(WindowsClipboardFormatMap.FromMimeForWrite("application/x-something"));
    }

    [Fact]
    public void FromMime_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(WindowsClipboardFormatMap.FromMime(null));
        Assert.Null(WindowsClipboardFormatMap.FromMime(""));
        Assert.Null(WindowsClipboardFormatMap.FromMimeForWrite(null));
        Assert.Null(WindowsClipboardFormatMap.FromMimeForWrite(""));
    }

    [Theory]
    [InlineData(WindowsClipboardFormat.Text, "text/plain")]
    [InlineData(WindowsClipboardFormat.Html, "text/html")]
    [InlineData(WindowsClipboardFormat.UriList, "text/uri-list")]
    [InlineData(WindowsClipboardFormat.Image, "image/png")]
    public void ToMime_RoundTripsFromMime(WindowsClipboardFormat format, string expected)
    {
        Assert.Equal(expected, WindowsClipboardFormatMap.ToMime(format));
        Assert.Equal(format, WindowsClipboardFormatMap.FromMime(expected));
    }
}
