using System;
using System.Collections.Generic;
using Ghostty.Core.Clipboard;
using Xunit;

namespace Ghostty.Tests.Clipboard;

/// <summary>
/// Tests the text/uri-list body we serve for files copied in Explorer.
///
/// The case list is deliberately modelled on upstream's NSPasteboardTests,
/// which is the one part of the macOS clipboard layer that carries real
/// coverage. The interesting cases are the same on both platforms -- paths
/// needing escaping, several files at once, entries that cannot be
/// expressed -- and Windows adds a few of its own, because a legal Windows
/// filename may contain characters a URI may not.
/// </summary>
public sealed class UriListFormatterTests
{
    [Fact]
    public void SingleFile_ProducesOneFileUri()
    {
        var result = UriListFormatter.Format(new[] { @"C:\tmp\a.txt" });

        Assert.Equal("file:///C:/tmp/a.txt", result);
    }

    [Fact]
    public void MultipleFiles_AreCrlfSeparated()
    {
        // RFC 2483 is CRLF separated. A bare LF is the kind of thing that
        // works against a lenient reader and silently fails against a
        // strict one, so it is pinned rather than left to chance.
        var result = UriListFormatter.Format(new[] { @"C:\a.txt", @"C:\b.txt" });

        Assert.Equal("file:///C:/a.txt\r\nfile:///C:/b.txt", result);
    }

    [Fact]
    public void NoTrailingSeparator()
    {
        // A trailing CRLF reads as an empty final URI to a strict parser.
        var result = UriListFormatter.Format(new[] { @"C:\a.txt" });

        Assert.NotNull(result);
        Assert.False(result!.EndsWith("\r\n", StringComparison.Ordinal));
    }

    [Fact]
    public void PathWithSpaces_IsPercentEncoded()
    {
        var result = UriListFormatter.Format(new[] { @"C:\Program Files\x.txt" });

        Assert.Equal("file:///C:/Program%20Files/x.txt", result);
    }

    [Fact]
    public void PathWithHash_DoesNotTruncateAtTheFragment()
    {
        // '#' is legal in a Windows filename and starts a fragment in a URI.
        // Hand-rolled escaping is exactly where this one gets lost.
        var result = UriListFormatter.Format(new[] { @"C:\notes\draft#2.txt" });

        Assert.NotNull(result);
        Assert.DoesNotContain("#", result!);
        Assert.Contains("draft%232.txt", result!);
    }

    [Fact]
    public void PathWithPercent_IsItselfEncoded()
    {
        // Otherwise a literal '%' round-trips as the start of an escape.
        var result = UriListFormatter.Format(new[] { @"C:\tmp\100%.txt" });

        Assert.NotNull(result);
        Assert.Contains("100%25.txt", result!);
    }

    [Fact]
    public void NonAsciiPath_IsEncoded()
    {
        var result = UriListFormatter.Format(new[] { @"C:\tmp\日本語.txt" });

        Assert.NotNull(result);
        Assert.StartsWith("file:///C:/tmp/", result!);
        Assert.DoesNotContain("日", result!);
    }

    [Fact]
    public void UncPath_IsExpressedAsAFileUri()
    {
        var result = UriListFormatter.Format(new[] { @"\\server\share\a.txt" });

        Assert.Equal("file://server/share/a.txt", result);
    }

    [Fact]
    public void EmptyInput_ReturnsNull()
    {
        // Null rather than empty string so the caller omits the
        // representation instead of advertising an empty one.
        Assert.Null(UriListFormatter.Format(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void UnusablePath_IsSkipped(string? path)
    {
        Assert.Null(UriListFormatter.Format(new[] { path! }));
    }

    [Fact]
    public void RelativePath_IsSkippedRatherThanGuessed()
    {
        // Resolving it against the process working directory would invent a
        // path the user never copied.
        Assert.Null(UriListFormatter.Format(new[] { @"tmp\a.txt" }));
    }

    [Fact]
    public void UnusableEntriesDoNotSinkTheUsableOnes()
    {
        var result = UriListFormatter.Format(new[] { "", @"C:\a.txt", "   " });

        Assert.Equal("file:///C:/a.txt", result);
    }

    [Fact]
    public void ToFileUri_NonFileScheme_ReturnsNull()
    {
        // A remote URL on the clipboard is not a file, and serving it as one
        // would tell the terminal a lie about what it can open.
        Assert.Null(UriListFormatter.ToFileUri("https://example.com/a.txt"));
    }
}
