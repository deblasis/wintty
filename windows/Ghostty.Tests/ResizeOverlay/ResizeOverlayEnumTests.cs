using Ghostty.Core.ResizeOverlay;
using Xunit;

namespace Ghostty.Tests.ResizeOverlay;

public class ResizeOverlayEnumTests
{
    [Theory]
    [InlineData("always", ResizeOverlayMode.Always)]
    [InlineData("never", ResizeOverlayMode.Never)]
    [InlineData("after-first", ResizeOverlayMode.AfterFirst)]
    public void Mode_parses_known_tags(string raw, ResizeOverlayMode expected)
    {
        Assert.Equal(expected, ResizeOverlayModeExtensions.Parse(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bogus")]
    public void Mode_falls_back_to_after_first(string? raw)
    {
        Assert.Equal(ResizeOverlayMode.AfterFirst, ResizeOverlayModeExtensions.Parse(raw));
    }

    [Theory]
    [InlineData("center", ResizeOverlayPosition.Center)]
    [InlineData("top-left", ResizeOverlayPosition.TopLeft)]
    [InlineData("top-center", ResizeOverlayPosition.TopCenter)]
    [InlineData("top-right", ResizeOverlayPosition.TopRight)]
    [InlineData("bottom-left", ResizeOverlayPosition.BottomLeft)]
    [InlineData("bottom-center", ResizeOverlayPosition.BottomCenter)]
    [InlineData("bottom-right", ResizeOverlayPosition.BottomRight)]
    public void Position_parses_known_tags(string raw, ResizeOverlayPosition expected)
    {
        Assert.Equal(expected, ResizeOverlayPositionExtensions.Parse(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bogus")]
    public void Position_falls_back_to_center(string? raw)
    {
        Assert.Equal(ResizeOverlayPosition.Center, ResizeOverlayPositionExtensions.Parse(raw));
    }
}
