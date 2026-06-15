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
        // Pure-function contract for the System fallback: a dark terminal
        // background on a light OS resolves to LIGHT (the fallback ignores
        // the palette and tracks the OS). SettingsWindow still uses System
        // fallback. NOTE: the command palette no longer uses System
        // fallback — as of #236 it uses Palette fallback to match the
        // window chrome (see Regression_DarkPaletteOnLightOs_PaletteFallback_IsDark).
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

    [Fact]
    public void SystemAndAuto_AreNotRedundant_DivergeOnDarkBgLightOs()
    {
        // Issue #208 claimed "system" and "auto" are redundant. They are
        // not: "system" follows the OS dark-mode flag, while "auto" (the
        // Palette fallback) follows the terminal background luminance.
        // With a dark background on a light OS they MUST disagree — this
        // pins the two values as genuinely distinct so neither is
        // "simplified away" later.
        var system = ThemeResolution.ResolveIsDark(
            "system", BlackBg, ThemeFallbackStyle.Palette, isSystemDark: false);
        var auto = ThemeResolution.ResolveIsDark(
            "auto", BlackBg, ThemeFallbackStyle.Palette, isSystemDark: false);

        Assert.False(system); // OS is light → light frame
        Assert.True(auto);    // background is dark → dark frame
        Assert.NotEqual(system, auto);
    }

    // ── PreferLightForeground: readable caption/label foreground ─────────

    [Fact]
    public void PreferLightForeground_DarkBackground_PrefersLight() =>
        Assert.True(ThemeResolution.PreferLightForeground(0x000000));

    [Fact]
    public void PreferLightForeground_LightBackground_PrefersDark() =>
        Assert.False(ThemeResolution.PreferLightForeground(0xFFFFFF));

    [Fact]
    public void PreferLightForeground_MatchesIsBackgroundDark()
    {
        // Intent alias: PreferLightForeground is exactly IsBackgroundDark.
        foreach (uint c in new uint[] { 0x000000, 0xFFFFFF, 0x808080, 0x7F7F7F, 0x00FF00, 0x0000FF })
            Assert.Equal(ThemeResolution.IsBackgroundDark(c), ThemeResolution.PreferLightForeground(c));
    }

    // ── ContrastRatio: WCAG ratio ────────────────────────────────────────

    [Fact]
    public void ContrastRatio_BlackOnWhite_Is21() =>
        Assert.Equal(21.0, ThemeResolution.ContrastRatio(0x000000, 0xFFFFFF), 3);

    [Fact]
    public void ContrastRatio_SameColor_Is1() =>
        Assert.Equal(1.0, ThemeResolution.ContrastRatio(0x3B82F6, 0x3B82F6), 3);

    [Fact]
    public void ContrastRatio_IsOrderIndependent() =>
        Assert.Equal(
            ThemeResolution.ContrastRatio(0x101010, 0xEEEEEE),
            ThemeResolution.ContrastRatio(0xEEEEEE, 0x101010), 6);

    // ── EnsureReadableForeground: legible active-tab title ───────────────

    [Fact]
    public void EnsureReadable_WhiteTitleOnWhiteAccent_FallsBackToBlack()
    {
        // The reported bug: empty config → cursor-colour (selected-tab
        // background) defaults to white, while the inherited title brush is
        // also white. The active title must drop to black to stay legible.
        Assert.Equal(0x000000u,
            ThemeResolution.EnsureReadableForeground(0xFFFFFF, 0xFFFFFF));
    }

    [Fact]
    public void EnsureReadable_BlackTitleOnBlackAccent_FallsBackToWhite() =>
        Assert.Equal(0xFFFFFFu,
            ThemeResolution.EnsureReadableForeground(0x000000, 0x000000));

    [Fact]
    public void EnsureReadable_KeepsDesired_WhenItAlreadyContrasts()
    {
        // Shell-theme default palette: dark cursor-text over a light accent
        // already reads fine, so the user's chosen colour is preserved.
        Assert.Equal(0x1E1E2Eu,
            ThemeResolution.EnsureReadableForeground(0xFFFFFF, 0x1E1E2E));
        Assert.Equal(0xFFFFFFu,
            ThemeResolution.EnsureReadableForeground(0x000000, 0xFFFFFF));
    }

    [Fact]
    public void EnsureReadable_BothLight_FallsBackToBlack()
    {
        // Pathological shell palette where accent and active-text both land
        // light: contrast is too low, so fall back to a dark foreground.
        Assert.Equal(0x000000u,
            ThemeResolution.EnsureReadableForeground(0xEEEEEE, 0xFFFFFF));
    }

    [Fact]
    public void EnsureReadable_BothDark_FallsBackToWhite()
    {
        // ...and the mirror: both dark → fall back to a light foreground.
        Assert.Equal(0xFFFFFFu,
            ThemeResolution.EnsureReadableForeground(0x111111, 0x000000));
    }

    [Theory]
    [InlineData(0x7F7F7Fu)]
    [InlineData(0x808080u)]
    [InlineData(0x777777u)]
    public void EnsureReadable_MidGray_PicksHigherContrastPole(uint background)
    {
        // Mid-luminance backgrounds are the band where a plain dark/light
        // luminance split disagrees with the WCAG ratio: 0x7F7F7F reads
        // better on black than white. The fallback must return the pole that
        // actually contrasts more, never the lower-contrast one.
        var fg = ThemeResolution.EnsureReadableForeground(background, background);
        Assert.True(fg == 0xFFFFFFu || fg == 0x000000u);
        var chosen = ThemeResolution.ContrastRatio(background, fg);
        var other = ThemeResolution.ContrastRatio(
            background, fg == 0xFFFFFFu ? 0x000000u : 0xFFFFFFu);
        Assert.True(chosen >= other);
    }
}
