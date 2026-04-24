using Ghostty.Core.Shell;
using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// Unit tests for <see cref="AcrylicTintResolver"/>. The key contract
/// is the theme-background fallback when <c>background-tint-color</c>
/// is unset (issue # 324); before that fix the resolver returned
/// transparent black and washed the frosted acrylic out.
/// </summary>
public sealed class AcrylicTintResolverTests
{
    private const uint ThemeBg = 0x001E1E2Eu;          // Catppuccin-ish dark
    private const uint ThemeBgOpaque = AcrylicTintResolver.OpaqueAlphaMask | ThemeBg;
    private const uint UserTint = 0xFFAABBCCu;

    [Fact]
    public void Tint_override_unset_falls_back_to_opaque_theme_background()
    {
        var t = AcrylicTintResolver.Resolve(
            tintOverrideArgb: null,
            themeBackgroundRgb: ThemeBg,
            tintOpacityOverride: null,
            luminosityOpacityOverride: null,
            blurFollowsOpacity: false,
            backgroundOpacity: 1.0);

        Assert.Equal(ThemeBgOpaque, t.TintArgb);
    }

    [Fact]
    public void Tint_override_set_wins_over_theme_background()
    {
        var t = AcrylicTintResolver.Resolve(
            tintOverrideArgb: UserTint,
            themeBackgroundRgb: ThemeBg,
            tintOpacityOverride: null,
            luminosityOpacityOverride: null,
            blurFollowsOpacity: false,
            backgroundOpacity: 1.0);

        Assert.Equal(UserTint, t.TintArgb);
    }

    [Fact]
    public void Opacity_overrides_applied_when_not_blur_following()
    {
        var t = AcrylicTintResolver.Resolve(
            tintOverrideArgb: null,
            themeBackgroundRgb: ThemeBg,
            tintOpacityOverride: 0.7f,
            luminosityOpacityOverride: 0.4f,
            blurFollowsOpacity: false,
            backgroundOpacity: 1.0);

        Assert.Equal(0.7f, t.TintOpacity);
        Assert.Equal(0.4f, t.LuminosityOpacity);
    }

    [Fact]
    public void Opacity_defaults_applied_when_overrides_null_and_not_blur_following()
    {
        var t = AcrylicTintResolver.Resolve(
            tintOverrideArgb: null,
            themeBackgroundRgb: ThemeBg,
            tintOpacityOverride: null,
            luminosityOpacityOverride: null,
            blurFollowsOpacity: false,
            backgroundOpacity: 1.0);

        Assert.Equal(AcrylicTintResolver.DefaultTintOpacity, t.TintOpacity);
        Assert.Equal(AcrylicTintResolver.DefaultLuminosityOpacity, t.LuminosityOpacity);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Blur_follows_opacity_slaves_both_opacities_to_background_opacity(double bg)
    {
        var t = AcrylicTintResolver.Resolve(
            tintOverrideArgb: null,
            themeBackgroundRgb: ThemeBg,
            // Overrides are deliberately non-null to confirm they get ignored.
            tintOpacityOverride: 0.9f,
            luminosityOpacityOverride: 0.9f,
            blurFollowsOpacity: true,
            backgroundOpacity: bg);

        Assert.Equal((float)bg, t.TintOpacity);
        Assert.Equal((float)bg, t.LuminosityOpacity);
    }

    [Fact]
    public void Blur_follows_opacity_with_tint_override_keeps_override_color()
    {
        var t = AcrylicTintResolver.Resolve(
            tintOverrideArgb: UserTint,
            themeBackgroundRgb: ThemeBg,
            tintOpacityOverride: null,
            luminosityOpacityOverride: null,
            blurFollowsOpacity: true,
            backgroundOpacity: 0.5);

        Assert.Equal(UserTint, t.TintArgb);
    }

    [Fact]
    public void Blur_follows_opacity_without_tint_override_still_uses_theme_background()
    {
        var t = AcrylicTintResolver.Resolve(
            tintOverrideArgb: null,
            themeBackgroundRgb: ThemeBg,
            tintOpacityOverride: null,
            luminosityOpacityOverride: null,
            blurFollowsOpacity: true,
            backgroundOpacity: 0.5);

        Assert.Equal(ThemeBgOpaque, t.TintArgb);
    }

    [Fact]
    public void Theme_background_high_byte_is_discarded_before_applying_opaque_alpha()
    {
        // ConfigService stores background as 0x00RRGGBB, but guard against
        // a caller that accidentally passes a packed ARGB anyway.
        var t = AcrylicTintResolver.Resolve(
            tintOverrideArgb: null,
            themeBackgroundRgb: 0xDEADBEEFu,
            tintOpacityOverride: null,
            luminosityOpacityOverride: null,
            blurFollowsOpacity: false,
            backgroundOpacity: 1.0);

        Assert.Equal(0xFFADBEEFu, t.TintArgb);
    }
}
