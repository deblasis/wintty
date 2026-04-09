using Ghostty.Core.Clipboard;
using Xunit;

namespace Ghostty.Tests.Clipboard;

/// <summary>
/// Mirrors macos/Tests/NSPasteboardTests.swift. Verifies the MIME types
/// libghostty emits map to the Windows clipboard formats we support, and
/// that everything else maps to null (silently skipped, not an error).
/// </summary>
public sealed class WindowsClipboardFormatMapTests
{
    [Fact]
    public void FromMime_TextPlain_ReturnsText()
    {
        Assert.Equal(WindowsClipboardFormat.Text,
            WindowsClipboardFormatMap.FromMime("text/plain"));
    }

    [Fact]
    public void FromMime_TextHtml_ReturnsHtml()
    {
        Assert.Equal(WindowsClipboardFormat.Html,
            WindowsClipboardFormatMap.FromMime("text/html"));
    }

    [Fact]
    public void FromMime_ImagePng_ReturnsNull()
    {
        // Negative analogue of the macOS image/png test: macOS supports
        // image/png natively via NSPasteboard.PasteboardType. We do not.
        // Documented difference, not an oversight.
        Assert.Null(WindowsClipboardFormatMap.FromMime("image/png"));
    }

    [Fact]
    public void FromMime_UnknownMime_ReturnsNull()
    {
        Assert.Null(WindowsClipboardFormatMap.FromMime("application/x-something"));
    }

    [Fact]
    public void FromMime_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(WindowsClipboardFormatMap.FromMime(null));
        Assert.Null(WindowsClipboardFormatMap.FromMime(""));
    }
}
