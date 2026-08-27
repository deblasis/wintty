using Ghostty.Core.Shell;
using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// Unit tests for <see cref="ColorRef"/>.
///
/// The inputs here are deliberately asymmetric in red and blue. The
/// hardcoded #0C0C0C the class brush carried until now is a neutral grey,
/// which is exactly why the missing conversion rendered correctly for as
/// long as it did, and why a grey test case proves nothing: it passes
/// against a function that returns its argument.
/// </summary>
public sealed class ColorRefTests
{
    [Fact]
    public void Red_and_blue_swap_places()
    {
        Assert.Equal(0x00332211u, ColorRef.ToColorRef(0xFF112233u));
    }

    [Theory]
    [InlineData(0xFFFF0000u, 0x000000FFu)]
    [InlineData(0xFF00FF00u, 0x0000FF00u)]
    [InlineData(0xFF0000FFu, 0x00FF0000u)]
    public void Each_channel_lands_in_its_gdi_slot(uint argb, uint expected)
    {
        Assert.Equal(expected, ColorRef.ToColorRef(argb));
    }

    /// <summary>
    /// COLORREF has no alpha byte: GDI reads the top byte as a flag
    /// selecting a palette lookup instead of a literal colour, so an ARGB
    /// handed over intact is not the same brush.
    /// </summary>
    [Fact]
    public void The_alpha_byte_is_dropped()
    {
        Assert.Equal(0x00000000u, ColorRef.ToColorRef(0xFF000000u));
        Assert.Equal(ColorRef.ToColorRef(0x00112233u), ColorRef.ToColorRef(0xFF112233u));
    }

    /// <summary>
    /// The two opaque chrome colours are what actually reaches
    /// CreateSolidBrush, so pin them end to end. Both are greys, so these
    /// cases are a regression pin rather than a test of the transposition.
    /// </summary>
    [Theory]
    [InlineData(true, 0x000C0C0Cu)]
    [InlineData(false, 0x00F3F3F3u)]
    public void The_opaque_chrome_colours_survive_the_trip(bool isDesktopDark, uint expected)
    {
        Assert.Equal(
            expected,
            ColorRef.ToColorRef(RootBackgroundResolver.OpaqueChromeArgb(isDesktopDark)));
    }
}
