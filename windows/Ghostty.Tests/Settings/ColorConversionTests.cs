using Ghostty.Core.Settings;
using Xunit;

namespace Ghostty.Tests.Settings;

public class ColorConversionTests
{
    // ---------- Hex parsing ----------

    [Theory]
    [InlineData("#FF6B35", 0xFF, 0x6B, 0x35)]
    [InlineData("#ff6b35", 0xFF, 0x6B, 0x35)]
    [InlineData("FF6B35", 0xFF, 0x6B, 0x35)]
    [InlineData("#000000", 0, 0, 0)]
    [InlineData("#FFFFFF", 0xFF, 0xFF, 0xFF)]
    public void TryParseHex_SixDigit_Succeeds(string input, int r, int g, int b)
    {
        Assert.True(Rgb.TryParseHex(input, out var rgb));
        Assert.Equal(new Rgb((byte)r, (byte)g, (byte)b), rgb);
    }

    [Theory]
    [InlineData("#abc", 0xAA, 0xBB, 0xCC)]
    [InlineData("abc", 0xAA, 0xBB, 0xCC)]
    [InlineData("#f0a", 0xFF, 0x00, 0xAA)]
    public void TryParseHex_ThreeDigit_ExpandsToSixDigit(string input, int r, int g, int b)
    {
        Assert.True(Rgb.TryParseHex(input, out var rgb));
        Assert.Equal(new Rgb((byte)r, (byte)g, (byte)b), rgb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("not-a-color")]
    [InlineData("#12345")]      // wrong length
    [InlineData("#1234567")]    // wrong length
    [InlineData("#xyz123")]     // bad digits
    [InlineData(null)]
    public void TryParseHex_Invalid_ReturnsFalse(string? input)
    {
        Assert.False(Rgb.TryParseHex(input!, out _));
    }

    [Fact]
    public void ToHex_EmitsUppercaseWithHash()
    {
        Assert.Equal("#FF6B35", new Rgb(0xFF, 0x6B, 0x35).ToHex());
        Assert.Equal("#000000", new Rgb(0, 0, 0).ToHex());
    }

    [Theory]
    [InlineData(0x00FF6B35u, 0xFF, 0x6B, 0x35)]
    [InlineData(0x001E1E2Eu, 0x1E, 0x1E, 0x2E)]
    [InlineData(0x00000000u, 0, 0, 0)]
    [InlineData(0x00FFFFFFu, 0xFF, 0xFF, 0xFF)]
    public void FromRgb24_UnpacksHighByteFirst(uint packed, int r, int g, int b)
    {
        Assert.Equal(new Rgb((byte)r, (byte)g, (byte)b), Rgb.FromRgb24(packed));
    }

    [Theory]
    [InlineData(0xFF, 0x6B, 0x35, 0x00FF6B35u)]
    [InlineData(0x1E, 0x1E, 0x2E, 0x001E1E2Eu)]
    [InlineData(0, 0, 0, 0x00000000u)]
    [InlineData(0xFF, 0xFF, 0xFF, 0x00FFFFFFu)]
    public void ToRgb24_RoundTripsFromRgb24(int r, int g, int b, uint expected)
    {
        var rgb = new Rgb((byte)r, (byte)g, (byte)b);
        Assert.Equal(expected, rgb.ToRgb24());
        Assert.Equal(rgb, Rgb.FromRgb24(rgb.ToRgb24()));
    }

    // ---------- HSV <-> RGB ----------

    [Theory]
    [InlineData(0xFF, 0x00, 0x00,   0.0, 1.0, 1.0)] // red
    [InlineData(0x00, 0xFF, 0x00, 120.0, 1.0, 1.0)] // green
    [InlineData(0x00, 0x00, 0xFF, 240.0, 1.0, 1.0)] // blue
    [InlineData(0xFF, 0xFF, 0xFF,   0.0, 0.0, 1.0)] // white
    [InlineData(0x00, 0x00, 0x00,   0.0, 0.0, 0.0)] // black
    [InlineData(0x80, 0x80, 0x80,   0.0, 0.0, 0.5019608)] // gray
    public void RgbToHsv_KnownColors(int r, int g, int b, double h, double s, double v)
    {
        var hsv = new Rgb((byte)r, (byte)g, (byte)b).ToHsv();
        Assert.Equal(h, hsv.H, 1);
        Assert.Equal(s, hsv.S, 3);
        Assert.Equal(v, hsv.V, 3);
    }

    [Theory]
    [InlineData(  0.0, 1.0, 1.0, 0xFF, 0x00, 0x00)]
    [InlineData(120.0, 1.0, 1.0, 0x00, 0xFF, 0x00)]
    [InlineData(240.0, 1.0, 1.0, 0x00, 0x00, 0xFF)]
    [InlineData( 60.0, 1.0, 1.0, 0xFF, 0xFF, 0x00)] // yellow
    [InlineData(180.0, 1.0, 1.0, 0x00, 0xFF, 0xFF)] // cyan
    [InlineData(300.0, 1.0, 1.0, 0xFF, 0x00, 0xFF)] // magenta
    public void HsvToRgb_KnownColors(double h, double s, double v, int r, int g, int b)
    {
        var rgb = Rgb.FromHsv(new Hsv(h, s, v));
        Assert.Equal((byte)r, rgb.R);
        Assert.Equal((byte)g, rgb.G);
        Assert.Equal((byte)b, rgb.B);
    }

    [Theory]
    [InlineData(0xFF, 0x6B, 0x35)]
    [InlineData(0xF7, 0xC9, 0x48)]
    [InlineData(0xCD, 0xD6, 0xF4)]
    [InlineData(0x1E, 0x1E, 0x2E)]
    [InlineData(0x12, 0x34, 0x56)]
    public void RgbHsvRgb_RoundTrip(int r, int g, int b)
    {
        var original = new Rgb((byte)r, (byte)g, (byte)b);
        var roundTrip = Rgb.FromHsv(original.ToHsv());
        Assert.Equal(original, roundTrip);
    }
}
