using Ghostty.Core.Accessibility;
using Xunit;

namespace Ghostty.Tests.Accessibility;

/// <summary>
/// What a theme row in the palette says. The name must carry "current
/// theme" (a reader cannot see the badge), the badge must never claim a
/// preview for a row the browse has not shown, and the announcement is the
/// only way a reader hears which theme the terminals switched to.
/// </summary>
public class ThemeRowPresentationTests
{
    [Fact]
    public void TheNameCarriesCurrentThemeAndNothingElse()
    {
        Assert.Equal("Nord, current theme", ThemeRowPresentation.Name("Nord", isCurrent: true));
        Assert.Equal("Nord", ThemeRowPresentation.Name("Nord", isCurrent: false));
    }

    [Theory]
    [InlineData(true, true, ThemeRowBadge.Current)]
    [InlineData(true, false, ThemeRowBadge.Current)]
    [InlineData(false, true, ThemeRowBadge.Previewing)]
    [InlineData(false, false, ThemeRowBadge.None)]
    public void CurrentBeatsPreviewingAndAnUnpreviewedRowIsUnmarked(bool current, bool previewed, ThemeRowBadge expected)
        => Assert.Equal(expected, ThemeRowPresentation.Badge(current, previewed));

    [Theory]
    [InlineData(false, true, true, "Previewing Nord, dark")]
    [InlineData(true, true, false, "Nord, current theme, light")]
    [InlineData(true, false, null, "Nord, current theme")]
    [InlineData(false, false, true, "Nord, dark")]
    [InlineData(false, false, null, "Nord")]
    public void TheAnnouncementSaysWhatTheTerminalsShow(bool current, bool previewed, bool? dark, string expected)
        => Assert.Equal(expected, ThemeRowPresentation.Announcement("Nord", current, previewed, dark));

    [Fact]
    public void HintAndHelpTextAreAbsentUntilTheSwatchIsKnown()
    {
        Assert.Null(ThemeRowPresentation.Hint(null));
        Assert.Null(ThemeRowPresentation.HelpText(null));
        Assert.Equal("Dark", ThemeRowPresentation.Hint(true));
        Assert.Equal("Light theme", ThemeRowPresentation.HelpText(false));
    }

    [Fact]
    public void TheRowAutomationIsNameAndHelpTextWithNoAcceleratorKey()
    {
        var row = ThemeRowPresentation.Automation("Nord", isCurrent: true, isDark: true);
        Assert.Equal("Nord, current theme", row.Name);
        Assert.Equal("Dark theme", row.HelpText);
        Assert.Null(row.AcceleratorKey);

        var unknown = ThemeRowPresentation.Automation("Nord", isCurrent: false, isDark: null);
        Assert.Null(unknown.HelpText);
    }
}
