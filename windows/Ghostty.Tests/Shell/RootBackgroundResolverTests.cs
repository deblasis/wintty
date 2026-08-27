using Ghostty.Core.Shell;
using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// Unit tests for <see cref="RootBackgroundResolver"/>. The resolver
/// is the single source of truth for RootGrid.Background on the main
/// window, so the decision matrix is exhaustively covered here.
/// </summary>
public sealed class RootBackgroundResolverTests
{
    private const uint ArbitraryShellBg = 0xFF8040C0u;

    [Theory]
    [InlineData(BackdropStyles.Frosted, false, false)]
    [InlineData(BackdropStyles.Frosted, false, true)]
    [InlineData(BackdropStyles.Frosted, true, false)]
    [InlineData(BackdropStyles.Frosted, true, true)]
    [InlineData(BackdropStyles.Crystal, false, false)]
    [InlineData(BackdropStyles.Crystal, false, true)]
    [InlineData(BackdropStyles.Crystal, true, false)]
    [InlineData(BackdropStyles.Crystal, true, true)]
    public void Transparent_backdrops_always_return_transparent(
        string style, bool shellThemeEnabled, bool isDesktopDark)
    {
        Assert.Equal(
            RootBackgroundResolver.TransparentArgb,
            RootBackgroundResolver.Resolve(style, shellThemeEnabled, ArbitraryShellBg, isDesktopDark));
    }

    /// <summary>
    /// A solid window with no terminal palette driving the chrome used to
    /// come up near-black whatever the desktop was doing, so a light
    /// desktop got black chrome around a near-white terminal. The dark
    /// half is deliberately the value the window has always used.
    /// </summary>
    [Theory]
    [InlineData(true, 0xFF0C0C0Cu)]
    [InlineData(false, 0xFFF3F3F3u)]
    public void Solid_backdrop_without_a_shell_theme_follows_the_desktop(
        bool isDesktopDark, uint expected)
    {
        Assert.Equal(expected, RootBackgroundResolver.OpaqueChromeArgb(isDesktopDark));
        Assert.Equal(
            expected,
            RootBackgroundResolver.Resolve(
                BackdropStyles.Solid, shellThemeEnabled: false, ArbitraryShellBg, isDesktopDark));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Solid_backdrop_with_shell_theme_ignores_the_desktop(bool isDesktopDark)
    {
        Assert.Equal(
            ArbitraryShellBg,
            RootBackgroundResolver.Resolve(
                BackdropStyles.Solid, shellThemeEnabled: true, ArbitraryShellBg, isDesktopDark));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("", false)]
    [InlineData("unknown", true)]
    [InlineData("unknown", false)]
    public void Unknown_or_empty_style_falls_through_to_solid_behavior(string style, bool isDesktopDark)
    {
        Assert.Equal(
            RootBackgroundResolver.OpaqueChromeArgb(isDesktopDark),
            RootBackgroundResolver.Resolve(style, shellThemeEnabled: false, ArbitraryShellBg, isDesktopDark));

        Assert.Equal(
            ArbitraryShellBg,
            RootBackgroundResolver.Resolve(style, shellThemeEnabled: true, ArbitraryShellBg, isDesktopDark));
    }

    /// <summary>
    /// Both halves are fully opaque. A chrome colour that let the backdrop
    /// through would defeat the one thing the solid style is for, and the
    /// value is fed to GDI as well as to XAML.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_opaque_chrome_colour_is_opaque(bool isDesktopDark)
    {
        Assert.Equal(0xFF000000u, RootBackgroundResolver.OpaqueChromeArgb(isDesktopDark) & 0xFF000000u);
    }
}
