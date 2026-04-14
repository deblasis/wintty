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
    [InlineData(BackdropStyles.Frosted, false)]
    [InlineData(BackdropStyles.Frosted, true)]
    [InlineData(BackdropStyles.Crystal, false)]
    [InlineData(BackdropStyles.Crystal, true)]
    public void Transparent_backdrops_always_return_transparent(string style, bool shellThemeEnabled)
    {
        Assert.Equal(
            RootBackgroundResolver.TransparentArgb,
            RootBackgroundResolver.Resolve(style, shellThemeEnabled, ArbitraryShellBg));
    }

    [Fact]
    public void Solid_backdrop_with_shell_theme_disabled_returns_opaque_chrome()
    {
        Assert.Equal(
            RootBackgroundResolver.OpaqueChromeArgb,
            RootBackgroundResolver.Resolve(BackdropStyles.Solid, shellThemeEnabled: false, ArbitraryShellBg));
    }

    [Fact]
    public void Solid_backdrop_with_shell_theme_enabled_returns_shell_theme_color()
    {
        Assert.Equal(
            ArbitraryShellBg,
            RootBackgroundResolver.Resolve(BackdropStyles.Solid, shellThemeEnabled: true, ArbitraryShellBg));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    public void Unknown_or_empty_style_falls_through_to_solid_behavior(string style)
    {
        // No shell theme -> opaque chrome.
        Assert.Equal(
            RootBackgroundResolver.OpaqueChromeArgb,
            RootBackgroundResolver.Resolve(style, shellThemeEnabled: false, ArbitraryShellBg));

        // With shell theme -> shell theme color.
        Assert.Equal(
            ArbitraryShellBg,
            RootBackgroundResolver.Resolve(style, shellThemeEnabled: true, ArbitraryShellBg));
    }
}
