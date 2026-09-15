using System;
using System.Linq;
using Ghostty.Core.Themes;
using Xunit;

namespace Ghostty.Tests.Themes;

/// <summary>
/// The palette's theme row swatch is drawn from these colours, so what the
/// parser reads is what the row shows: a wrong default here is a thumbnail
/// that disagrees with the terminal it previews.
/// </summary>
public class ThemeSwatchTests
{
    [Fact]
    public void ReadsEveryColourTheSwatchDraws()
    {
        var swatch = ThemeSwatch.Parse(
        [
            "# a comment",
            "",
            "background = #102030",
            "  foreground=#E0E0E0  ",
            "cursor-color = #FF8800",
            "palette = 0=#000001",
            "palette = 1 = #AA0000",
            "palette = 15=#FFFFFE",
        ]);

        Assert.Equal(0x102030u, swatch.Background);
        Assert.Equal(0xE0E0E0u, swatch.Foreground);
        Assert.Equal(0xFF8800u, swatch.Cursor);
        Assert.Equal(0x000001u, swatch.Palette[0]);
        Assert.Equal(0xAA0000u, swatch.Palette[1]);
        Assert.Equal(0xFFFFFEu, swatch.Palette[15]);
        // Untouched entries keep libghostty's default for that index.
        Assert.Equal(ThemeSwatch.DefaultPalette[2], swatch.Palette[2]);
    }

    [Fact]
    public void UnsetValuesAreLibghosttysDefaultsNotBlack()
    {
        var swatch = ThemeSwatch.Parse([]);

        Assert.Equal(0x282C34u, swatch.Background);
        Assert.Equal(0xFFFFFFu, swatch.Foreground);
        Assert.Equal(ThemeSwatch.DefaultPalette, swatch.Palette);
        Assert.Equal(16, swatch.Palette.Count);
    }

    [Fact]
    public void AnUnsetCursorIsTheForeground()
    {
        // ConfigService.ApplyThemeColors resolves it the same way, so the
        // swatch's cursor is the one the chrome shows.
        var swatch = ThemeSwatch.Parse(["foreground = #123456"]);
        Assert.Equal(0x123456u, swatch.Cursor);
    }

    [Fact]
    public void LaterLinesWinAndWhatIsNotAColourIsSkipped()
    {
        var swatch = ThemeSwatch.Parse(
        [
            "background = #111111",
            "background = #222222",
            "foreground = not-a-colour",
            "cursor-color = cell-foreground",
            "palette = 99=#FFFFFF",
            "palette = x",
            "palette = 3=\"#0A0B0C\"",
            "background",
            "= #333333",
        ]);

        Assert.Equal(0x222222u, swatch.Background);
        Assert.Equal(ThemeSwatch.DefaultForeground, swatch.Foreground);
        Assert.Equal(ThemeSwatch.DefaultForeground, swatch.Cursor);
        Assert.Equal(ThemeSwatch.DefaultPalette.Take(3), swatch.Palette.Take(3));
    }

    [Fact]
    public void AQuotedValueIsReadAsTheColourInsideTheQuotes()
    {
        var swatch = ThemeSwatch.Parse(["background = \"#0A0B0C\""]);
        Assert.Equal(0x0A0B0Cu, swatch.Background);
    }

    [Theory]
    [InlineData("#102030", true)]
    [InlineData("#282C34", true)]
    [InlineData("#FAF4E8", false)]
    [InlineData("#F4F6FB", false)]
    public void TheLightDarkHintFollowsTheBackground(string background, bool dark)
    {
        var swatch = ThemeSwatch.Parse(["background = " + background]);
        Assert.Equal(dark, swatch.IsDark);
    }

    [Fact]
    public void ParseRefusesNull()
        => Assert.Throws<ArgumentNullException>(() => ThemeSwatch.Parse(null!));
}
