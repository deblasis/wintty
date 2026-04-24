using Ghostty.Core.Windows;
using Xunit;

namespace Ghostty.Tests.Windows;

public sealed class ThemeResolutionTests
{
    // Shared black/white backgrounds. The "backgroundColor" argument
    // only matters when fallback=Palette and the value is not explicit
    // light/dark/system, so most tests can pick either.
    private const uint BlackBg = 0x000000;
    private const uint WhiteBg = 0xFFFFFF;

    // ── ResolveIsDark: explicit values ───────────────────────────────────

    [Theory]
    [InlineData(ThemeFallbackStyle.Palette, true)]
    [InlineData(ThemeFallbackStyle.Palette, false)]
    [InlineData(ThemeFallbackStyle.System, true)]
    [InlineData(ThemeFallbackStyle.System, false)]
    public void Light_Always_ReturnsFalse(ThemeFallbackStyle fallback, bool systemDark)
    {
        Assert.False(ThemeResolution.ResolveIsDark(
            "light", BlackBg, fallback, systemDark));
    }

    [Theory]
    [InlineData(ThemeFallbackStyle.Palette, true)]
    [InlineData(ThemeFallbackStyle.Palette, false)]
    [InlineData(ThemeFallbackStyle.System, true)]
    [InlineData(ThemeFallbackStyle.System, false)]
    public void Dark_Always_ReturnsTrue(ThemeFallbackStyle fallback, bool systemDark)
    {
        Assert.True(ThemeResolution.ResolveIsDark(
            "dark", WhiteBg, fallback, systemDark));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void System_FollowsIsSystemDark_RegardlessOfFallback(bool systemDark)
    {
        // "system" always consults the OS — fallback is irrelevant here.
        Assert.Equal(systemDark, ThemeResolution.ResolveIsDark(
            "system", BlackBg, ThemeFallbackStyle.Palette, systemDark));
        Assert.Equal(systemDark, ThemeResolution.ResolveIsDark(
            "system", BlackBg, ThemeFallbackStyle.System, systemDark));
    }

    // ── ResolveIsDark: fallback-driven (auto/ghostty/unknown) ────────────

    [Theory]
    [InlineData("auto")]
    [InlineData("ghostty")]
    [InlineData("")]
    [InlineData("unknown-future-value")]
    public void NonExplicit_WithPaletteFallback_UsesBackgroundLuminance(string windowTheme)
    {
        // Dark background → dark theme.
        Assert.True(ThemeResolution.ResolveIsDark(
            windowTheme, BlackBg, ThemeFallbackStyle.Palette, isSystemDark: false));
        // Light background → light theme.
        Assert.False(ThemeResolution.ResolveIsDark(
            windowTheme, WhiteBg, ThemeFallbackStyle.Palette, isSystemDark: true));
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("auto", false)]
    [InlineData("ghostty", true)]
    [InlineData("ghostty", false)]
    [InlineData("", true)]
    [InlineData("unknown", false)]
    public void NonExplicit_WithSystemFallback_FollowsOsTheme(
        string windowTheme, bool systemDark)
    {
        // Background colour must be ignored when fallback=System — use a
        // contrasting value so a bug would surface.
        var bg = systemDark ? WhiteBg : BlackBg;
        Assert.Equal(systemDark, ThemeResolution.ResolveIsDark(
            windowTheme, bg, ThemeFallbackStyle.System, systemDark));
    }

    // ── IsBackgroundDark: luminance edges ────────────────────────────────

    [Fact]
    public void IsBackgroundDark_PureBlack_IsDark() =>
        Assert.True(ThemeResolution.IsBackgroundDark(0x000000));

    [Fact]
    public void IsBackgroundDark_PureWhite_IsLight() =>
        Assert.False(ThemeResolution.IsBackgroundDark(0xFFFFFF));

    [Fact]
    public void IsBackgroundDark_MidGrey_IsDark()
    {
        // 0x808080 → luminance ≈ 0.502? Actually (128*0.2126 + 128*0.7152 +
        // 128*0.0722) / 255 = 128/255 ≈ 0.502, so "light" at the boundary.
        Assert.False(ThemeResolution.IsBackgroundDark(0x808080));
    }

    [Fact]
    public void IsBackgroundDark_JustBelowMidGrey_IsDark()
    {
        // 0x7F7F7F → luminance ≈ 0.498, below 0.5.
        Assert.True(ThemeResolution.IsBackgroundDark(0x7F7F7F));
    }

    [Fact]
    public void IsBackgroundDark_SaturatedGreen_IsLight()
    {
        // Pure green: luminance = 0.7152, well above 0.5. Matters for
        // high-contrast palettes with a bright primary background.
        Assert.False(ThemeResolution.IsBackgroundDark(0x00FF00));
    }

    [Fact]
    public void IsBackgroundDark_SaturatedBlue_IsDark()
    {
        // Pure blue: luminance = 0.0722, solidly dark.
        Assert.True(ThemeResolution.IsBackgroundDark(0x0000FF));
    }

    [Fact]
    public void IsBackgroundDark_IgnoresAlphaByte()
    {
        // Callers pack 0x00RRGGBB, but a stray alpha byte in the top
        // octet must not affect the result — R/G/B shifts mask to 0xFF.
        Assert.True(ThemeResolution.IsBackgroundDark(0xFF000000));
        Assert.False(ThemeResolution.IsBackgroundDark(0xFFFFFFFF));
    }

    // ── TracksSystem: dispatch-skip optimisation ─────────────────────────

    [Theory]
    [InlineData("light", ThemeFallbackStyle.Palette)]
    [InlineData("light", ThemeFallbackStyle.System)]
    [InlineData("dark", ThemeFallbackStyle.Palette)]
    [InlineData("dark", ThemeFallbackStyle.System)]
    public void TracksSystem_Explicit_IsFalse(
        string windowTheme, ThemeFallbackStyle fallback)
    {
        // Explicit light/dark never consult the OS, so a system-theme
        // flip cannot change the resolved value.
        Assert.False(ThemeResolution.TracksSystem(windowTheme, fallback));
    }

    [Theory]
    [InlineData(ThemeFallbackStyle.Palette)]
    [InlineData(ThemeFallbackStyle.System)]
    public void TracksSystem_System_IsTrue(ThemeFallbackStyle fallback)
    {
        Assert.True(ThemeResolution.TracksSystem("system", fallback));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("ghostty")]
    [InlineData("")]
    public void TracksSystem_NonExplicit_PaletteFallback_IsFalse(string windowTheme)
    {
        // Palette fallback reads the background colour, not the OS theme
        // — OS flips are noise; skip the dispatch.
        Assert.False(ThemeResolution.TracksSystem(
            windowTheme, ThemeFallbackStyle.Palette));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("ghostty")]
    [InlineData("")]
    public void TracksSystem_NonExplicit_SystemFallback_IsTrue(string windowTheme)
    {
        // System fallback means OS flips matter.
        Assert.True(ThemeResolution.TracksSystem(
            windowTheme, ThemeFallbackStyle.System));
    }

    // ── Regression scenarios ─────────────────────────────────────────────

    [Fact]
    public void Regression_DarkPaletteOnLightOs_SystemFallback_IsLight()
    {
        // The bug this PR fixes: command palette was rendering dark
        // because it tracked the terminal palette luminance directly.
        // Under the new System fallback, a dark terminal background on
        // a light OS must resolve to light.
        Assert.False(ThemeResolution.ResolveIsDark(
            "ghostty", BlackBg, ThemeFallbackStyle.System, isSystemDark: false));
    }

    [Fact]
    public void Regression_LightPaletteOnDarkOs_SystemFallback_IsDark()
    {
        Assert.True(ThemeResolution.ResolveIsDark(
            "ghostty", WhiteBg, ThemeFallbackStyle.System, isSystemDark: true));
    }

    [Fact]
    public void Regression_DarkPaletteOnLightOs_PaletteFallback_IsDark()
    {
        // The MainWindow chrome keeps palette-tracking behaviour: a
        // dark terminal background renders a dark frame even when the
        // OS is light. This test pins that contract.
        Assert.True(ThemeResolution.ResolveIsDark(
            "ghostty", BlackBg, ThemeFallbackStyle.Palette, isSystemDark: false));
    }
}
