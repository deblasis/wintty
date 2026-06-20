using Ghostty.Core.Accessibility;
using Xunit;

namespace Ghostty.Tests.Accessibility;

public sealed class HighContrastConfigWriterTests
{
    [Fact]
    public void FormatColor_ByteSwapsColorRefToRgbHex()
    {
        // COLORREF is 0x00BBGGRR. 0x00123456 => B=0x12 G=0x34 R=0x56 => #563412.
        Assert.Equal("#563412", HighContrastConfigWriter.FormatColor(0x00123456u));
    }

    [Fact]
    public void FormatColor_PadsAndLowercases()
    {
        // Pure white COLORREF 0x00FFFFFF => #ffffff; black => #000000.
        Assert.Equal("#ffffff", HighContrastConfigWriter.FormatColor(0x00FFFFFFu));
        Assert.Equal("#000000", HighContrastConfigWriter.FormatColor(0x00000000u));
        // COLORREF low byte is R: 0x0000000A => R=0x0A => #0a0000 (zero-pad each channel).
        Assert.Equal("#0a0000", HighContrastConfigWriter.FormatColor(0x0000000Au));
    }

    [Fact]
    public void Render_EmitsAllFiveLinesInOrder()
    {
        var colors = new HighContrastColors(
            Background: 0x00000000u,        // black -> #000000
            Foreground: 0x00FFFFFFu,        // white -> #ffffff
            SelectionBackground: 0x00FF0000u, // blue COLORREF -> #0000ff
            SelectionForeground: 0x0000FFFFu); // yellow COLORREF -> #ffff00

        var body = HighContrastConfigWriter.Render(colors);

        var expected =
            "background = #000000\n" +
            "foreground = #ffffff\n" +
            "selection-background = #0000ff\n" +
            "selection-foreground = #ffff00\n" +
            "minimum-contrast = 7\n";
        Assert.Equal(expected, body);
    }

    [Fact]
    public void Render_AllowsCustomMinimumContrast()
    {
        var colors = new HighContrastColors(0, 0, 0, 0);
        var body = HighContrastConfigWriter.Render(colors, minimumContrast: 21);
        Assert.Contains("minimum-contrast = 21\n", body);
    }
}
