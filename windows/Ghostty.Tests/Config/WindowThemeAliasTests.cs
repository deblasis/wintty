using System;
using System.Linq;
using Ghostty.Core.Config;
using Ghostty.Core.Shell;
using Ghostty.Core.Windows;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// "wintty" is the preferred <c>window-theme</c> spelling and "ghostty" the
/// deprecated alias libghostty still parses and hands back through the C API
/// verbatim. The two have to behave identically, and the way that stops
/// being true is a second implementation of the rule.
///
/// Equivalence is pinned over what the two spellings PRODUCE, not over the
/// predicate. <c>IsPaletteHued("ghostty") == IsPaletteHued("wintty")</c> is
/// satisfied by an implementation where both are false, which is precisely
/// the regression worth catching: an unmigrated config silently losing
/// palette-hued chrome. Every case below therefore also names the value it
/// expects, and the anchors at the bottom prove the outcomes are not
/// identical for the boring reason that nothing depends on the input.
/// </summary>
public sealed class WindowThemeAliasTests
{
    /// <summary>
    /// Asymmetric on purpose. A grey would survive a COLORREF conversion
    /// that never happened and a resolver that ignored the palette.
    /// </summary>
    private const uint PaletteBackgroundArgb = 0xFF1E2430u;

    private readonly record struct ChromeOutcome(uint RootArgb, uint ClassBrush, uint Separator);

    /// <summary>
    /// Everything the chrome derives from <c>window-theme</c>, in one value:
    /// the colour <c>RootGrid</c> is painted, the COLORREF handed to the
    /// window class brush, and the boundary stroke drawn over that ground.
    /// </summary>
    private static ChromeOutcome Chrome(string? windowTheme, string style, bool isDesktopDark)
    {
        var argb = RootBackgroundResolver.Resolve(
            style,
            WindowThemeAlias.IsPaletteHued(windowTheme),
            PaletteBackgroundArgb,
            isDesktopDark);

        return new ChromeOutcome(
            argb,
            ColorRef.ToColorRef(argb),
            ChromeSeparator.Resolve(argb & 0x00FFFFFFu));
    }

    public static TheoryData<string, bool> Backdrops()
    {
        var data = new TheoryData<string, bool>();
        foreach (var style in new[] { BackdropStyles.Solid, BackdropStyles.Frosted, BackdropStyles.Crystal })
        {
            data.Add(style, true);
            data.Add(style, false);
        }
        return data;
    }

    [Theory]
    [InlineData("wintty")]
    [InlineData("ghostty")]
    [InlineData("Wintty")]
    [InlineData("GHOSTTY")]
    public void Both_spellings_ask_for_palette_hued_chrome(string windowTheme)
    {
        Assert.True(WindowThemeAlias.IsPaletteHued(windowTheme));
    }

    /// <summary>
    /// "wintty-dark" is a theme NAME, not a window-theme value. It is in
    /// here because a prefix or substring match would take it, and the
    /// built-in theme pair puts that string in front of this predicate.
    /// </summary>
    [Theory]
    [InlineData("auto")]
    [InlineData("system")]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("wintty-dark")]
    [InlineData("")]
    [InlineData(null)]
    public void Nothing_else_does(string? windowTheme)
    {
        Assert.False(WindowThemeAlias.IsPaletteHued(windowTheme));
    }

    [Theory]
    [InlineData("ghostty")]
    [InlineData("GHOSTTY")]
    [InlineData("wintty")]
    [InlineData("Wintty")]
    public void Canonicalize_folds_both_spellings_to_the_preferred_one(string windowTheme)
    {
        Assert.Equal("wintty", WindowThemeAlias.Canonicalize(windowTheme));
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("dark", "dark")]
    [InlineData(null, "")]
    public void Canonicalize_leaves_every_other_value_alone(string? windowTheme, string expected)
    {
        Assert.Equal(expected, WindowThemeAlias.Canonicalize(windowTheme));
    }

    [Theory]
    [MemberData(nameof(Backdrops))]
    public void Both_spellings_produce_the_same_chrome(string style, bool isDesktopDark)
    {
        var preferred = Chrome("wintty", style, isDesktopDark);

        Assert.Equal(preferred, Chrome("ghostty", style, isDesktopDark));
        Assert.Equal(preferred, Chrome("GHOSTTY", style, isDesktopDark));
    }

    /// <summary>
    /// The anchor for the theory above. Without it, an implementation that
    /// answered false for both spellings would satisfy every equality
    /// assertion in this file.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Palette_hued_chrome_takes_the_palette_and_nothing_else_does(bool isDesktopDark)
    {
        var hued = Chrome("wintty", BackdropStyles.Solid, isDesktopDark);
        var plain = Chrome("system", BackdropStyles.Solid, isDesktopDark);

        Assert.Equal(PaletteBackgroundArgb, hued.RootArgb);
        Assert.Equal(RootBackgroundResolver.OpaqueChromeArgb(isDesktopDark), plain.RootArgb);
        Assert.NotEqual(hued, plain);

        // Channels reversed, alpha dropped: the class brush would render
        // the palette blue as red if the conversion went missing.
        Assert.Equal(0x0030241Eu, hued.ClassBrush);
    }

    /// <summary>
    /// <see cref="ThemeResolution"/> routes both spellings through its
    /// <c>_</c> discard arm, so neither string appears in it and a literal
    /// census has nothing to match. That is exactly why this test exists:
    /// the rule is implemented there, invisibly to the guard, and the two
    /// spellings have to arrive at the same answer regardless.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_spellings_take_the_same_route_through_ThemeResolution(bool isSystemDark)
    {
        foreach (var fallback in new[] { ThemeFallbackStyle.Palette, ThemeFallbackStyle.System })
        {
            Assert.Equal(
                ThemeResolution.ResolveIsDark("wintty", PaletteBackgroundArgb, fallback, isSystemDark),
                ThemeResolution.ResolveIsDark("ghostty", PaletteBackgroundArgb, fallback, isSystemDark));

            Assert.Equal(
                ThemeResolution.TracksSystem("wintty", fallback),
                ThemeResolution.TracksSystem("ghostty", fallback));
        }

        // Named values, so the pair above cannot agree by both being wrong.
        Assert.True(ThemeResolution.ResolveIsDark(
            "ghostty", PaletteBackgroundArgb, ThemeFallbackStyle.Palette, isSystemDark));
        Assert.False(ThemeResolution.ResolveIsDark(
            "ghostty", 0x00F3F3F3u, ThemeFallbackStyle.Palette, isSystemDark));
        Assert.Equal(isSystemDark, ThemeResolution.ResolveIsDark(
            "ghostty", PaletteBackgroundArgb, ThemeFallbackStyle.System, isSystemDark));

        Assert.False(ThemeResolution.TracksSystem("ghostty", ThemeFallbackStyle.Palette));
        Assert.True(ThemeResolution.TracksSystem("ghostty", ThemeFallbackStyle.System));
    }

    /// <summary>
    /// <c>frame-style</c> inherits from <c>background-style</c> and has no
    /// opinion about <c>window-theme</c>, which is what makes the two
    /// spellings inherit identically. Read off the source because
    /// <c>ConfigService</c> lives in the WinUI project this assembly does
    /// not reference; the point is the absence of a term, and an absence is
    /// what a syntax tree can be asked about honestly.
    /// </summary>
    [Fact]
    public void The_frame_style_read_does_not_consult_window_theme()
    {
        var read = ShellSource.Load("Services.ConfigService.cs")
            .Method("ReadFlagsCore")
            .DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "FrameStyle")
            .Right.ToString();

        Assert.Contains("BackgroundStyle", read, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowTheme", read, StringComparison.Ordinal);
    }
}
