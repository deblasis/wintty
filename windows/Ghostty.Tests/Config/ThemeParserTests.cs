using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

public class ThemeParserTests
{
    // ===== ParseThemePair =====

    [Fact]
    public void ParseThemePair_Empty_ReturnsNullPair()
    {
        var (light, dark) = ThemeParser.ParseThemePair("");
        Assert.Null(light);
        Assert.Null(dark);
    }

    [Fact]
    public void ParseThemePair_Whitespace_ReturnsNullPair()
    {
        var (light, dark) = ThemeParser.ParseThemePair("   ");
        Assert.Null(light);
        Assert.Null(dark);
    }

    [Fact]
    public void ParseThemePair_SingleTheme_ReturnsNullPair()
    {
        // Single theme name (no light:/dark: prefix) is not a pair.
        var (light, dark) = ThemeParser.ParseThemePair("Tokyo Night");
        Assert.Null(light);
        Assert.Null(dark);
    }

    [Fact]
    public void ParseThemePair_FullPair_ReturnsBoth()
    {
        var (light, dark) = ThemeParser.ParseThemePair("light:Catppuccin Latte,dark:Catppuccin Mocha");
        Assert.Equal("Catppuccin Latte", light);
        Assert.Equal("Catppuccin Mocha", dark);
    }

    [Fact]
    public void ParseThemePair_OrderInsensitive_ReturnsBoth()
    {
        var (light, dark) = ThemeParser.ParseThemePair("dark:Mocha,light:Latte");
        Assert.Equal("Latte", light);
        Assert.Equal("Mocha", dark);
    }

    [Fact]
    public void ParseThemePair_TrimsWhitespace()
    {
        var (light, dark) = ThemeParser.ParseThemePair(" light : Latte , dark : Mocha ");
        Assert.Equal("Latte", light);
        Assert.Equal("Mocha", dark);
    }

    [Fact]
    public void ParseThemePair_PartialLightOnly_ReturnsNullPair()
    {
        // Match Zig: a partial pair returns InvalidValue. We return (null, null).
        var (light, dark) = ThemeParser.ParseThemePair("light:Latte");
        Assert.Null(light);
        Assert.Null(dark);
    }

    [Fact]
    public void ParseThemePair_PartialDarkOnly_ReturnsNullPair()
    {
        var (light, dark) = ThemeParser.ParseThemePair("dark:Mocha");
        Assert.Null(light);
        Assert.Null(dark);
    }

    [Fact]
    public void ParseThemePair_EqualsSeparator_AcceptedForTypoTolerance()
    {
        // Zig accepts "=" as a typo-tolerance path. We do too on the read side.
        var (light, dark) = ThemeParser.ParseThemePair("light=Latte,dark=Mocha");
        Assert.Equal("Latte", light);
        Assert.Equal("Mocha", dark);
    }

    [Fact]
    public void ParseThemePair_CaseInsensitivePrefix()
    {
        var (light, dark) = ThemeParser.ParseThemePair("LIGHT:Latte,Dark:Mocha");
        Assert.Equal("Latte", light);
        Assert.Equal("Mocha", dark);
    }

    [Fact]
    public void ParseThemePair_SameLightAndDark_ReturnsBoth()
    {
        // Caller (UI) is responsible for collapsing same-value pairs to single.
        var (light, dark) = ThemeParser.ParseThemePair("light:Tokyo,dark:Tokyo");
        Assert.Equal("Tokyo", light);
        Assert.Equal("Tokyo", dark);
    }

    [Fact]
    public void ParseThemePair_ThemeNameWithSpaces_PreservesSpaces()
    {
        var (light, dark) = ThemeParser.ParseThemePair("light:Rose Pine Dawn,dark:Rose Pine");
        Assert.Equal("Rose Pine Dawn", light);
        Assert.Equal("Rose Pine", dark);
    }

    // ===== ApplyPaletteFromLines =====

    [Fact]
    public void ApplyPaletteFromLines_EmptyInput_LeavesPaletteUnchanged()
    {
        var palette = new uint[16];
        for (uint i = 0; i < 16; i++) palette[i] = 0xABABAB;

        ThemeParser.ApplyPaletteFromLines(System.Array.Empty<string>(), palette);

        for (uint i = 0; i < 16; i++)
            Assert.Equal(0xABABABu, palette[i]);
    }

    [Fact]
    public void ApplyPaletteFromLines_SetsByIndex()
    {
        var palette = new uint[16];
        var lines = new[]
        {
            "palette = 0=#112233",
            "palette = 7=#aabbcc",
        };

        ThemeParser.ApplyPaletteFromLines(lines, palette);

        Assert.Equal(0x112233u, palette[0]);
        Assert.Equal(0xAABBCCu, palette[7]);
    }

    [Fact]
    public void ApplyPaletteFromLines_OverridesPriorValue()
    {
        var palette = new uint[16];
        palette[0] = 0xFFFFFF;

        ThemeParser.ApplyPaletteFromLines(new[] { "palette = 0=#112233" }, palette);

        Assert.Equal(0x112233u, palette[0]);
    }

    [Fact]
    public void ApplyPaletteFromLines_LayeringSimulatesUserOverridesTheme()
    {
        // Simulates: theme file sets a palette, user config overrides one entry.
        var palette = new uint[16];

        var themeLines = new[]
        {
            "palette = 0=#000000",
            "palette = 1=#cc0000",
            "palette = 2=#00cc00",
        };
        ThemeParser.ApplyPaletteFromLines(themeLines, palette);

        var userLines = new[]
        {
            "palette = 1=#ff0000", // user overrides red
        };
        ThemeParser.ApplyPaletteFromLines(userLines, palette);

        Assert.Equal(0x000000u, palette[0]);
        Assert.Equal(0xFF0000u, palette[1]); // overridden
        Assert.Equal(0x00CC00u, palette[2]);
    }

    [Fact]
    public void ApplyPaletteFromLines_IgnoresComments()
    {
        var palette = new uint[16];

        ThemeParser.ApplyPaletteFromLines(new[]
        {
            "# palette = 0=#ffffff",
            "palette = 0=#112233",
        }, palette);

        Assert.Equal(0x112233u, palette[0]);
    }

    [Fact]
    public void ApplyPaletteFromLines_IgnoresUnrelatedKeys()
    {
        var palette = new uint[16];
        var lines = new[]
        {
            "background = #1e1e2e",
            "foreground = #cdd6f4",
            "cursor-color = #f5e0dc",
            "palette = 0=#112233",
        };

        ThemeParser.ApplyPaletteFromLines(lines, palette);

        Assert.Equal(0x112233u, palette[0]);
    }

    [Fact]
    public void ApplyPaletteFromLines_SkipsOutOfRangeIndices()
    {
        var palette = new uint[16];
        var lines = new[]
        {
            "palette = -1=#112233",
            "palette = 16=#445566",
            "palette = 99=#778899",
        };

        ThemeParser.ApplyPaletteFromLines(lines, palette);

        for (uint i = 0; i < 16; i++)
            Assert.Equal(0u, palette[i]);
    }

    [Fact]
    public void ApplyPaletteFromLines_SkipsMalformedEntries()
    {
        var palette = new uint[16];
        palette[0] = 0xDEADBEEF;
        var lines = new[]
        {
            "palette",                  // no =
            "palette = ",               // no entry
            "palette = =#112233",       // no index
            "palette = abc=#112233",    // non-numeric index
            "palette = 0=notahex",      // bad color
        };

        ThemeParser.ApplyPaletteFromLines(lines, palette);

        Assert.Equal(0xDEADBEEFu, palette[0]);
    }

    [Fact]
    public void ApplyPaletteFromLines_TooSmallPalette_Throws()
    {
        var palette = new uint[8];
        Assert.Throws<System.ArgumentException>(() =>
            ThemeParser.ApplyPaletteFromLines(System.Array.Empty<string>(), palette));
    }

    // ===== TryParseHexRgb =====

    [Fact]
    public void TryParseHexRgb_ShortForm_Expands()
    {
        Assert.True(ThemeParser.TryParseHexRgb("#abc", out var rgb));
        Assert.Equal(0xAABBCCu, rgb);
    }

    [Fact]
    public void TryParseHexRgb_FullForm()
    {
        Assert.True(ThemeParser.TryParseHexRgb("#112233", out var rgb));
        Assert.Equal(0x112233u, rgb);
    }

    [Fact]
    public void TryParseHexRgb_WithAlpha_DropsAlpha()
    {
        Assert.True(ThemeParser.TryParseHexRgb("#FF112233", out var rgb));
        Assert.Equal(0x112233u, rgb);
    }

    [Fact]
    public void TryParseHexRgb_NoLeadingHash()
    {
        Assert.True(ThemeParser.TryParseHexRgb("112233", out var rgb));
        Assert.Equal(0x112233u, rgb);
    }

    [Fact]
    public void TryParseHexRgb_Empty_ReturnsFalse()
    {
        Assert.False(ThemeParser.TryParseHexRgb("", out var rgb));
        Assert.Equal(0u, rgb);
    }

    [Fact]
    public void TryParseHexRgb_Invalid_ReturnsFalse()
    {
        Assert.False(ThemeParser.TryParseHexRgb("not-a-color", out _));
        Assert.False(ThemeParser.TryParseHexRgb("#xyz", out _));
        Assert.False(ThemeParser.TryParseHexRgb("#1234", out _)); // wrong length
    }
}
