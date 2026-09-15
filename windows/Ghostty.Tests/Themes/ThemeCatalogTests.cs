using System;
using System.Collections.Generic;
using System.IO;
using Ghostty.Core.Themes;
using Xunit;

namespace Ghostty.Tests.Themes;

/// <summary>
/// The one theme list the palette and the Settings page both read.
/// </summary>
public class ThemeCatalogTests
{
    private static IEnumerable<string> Listing(string directory) => directory switch
    {
        "first" => new[] { "Zenburn", "alpha", "Shared", ".DS_Store" },
        "second" => new[] { "shared", "Beta", "bad,name" },
        _ => Array.Empty<string>(),
    };

    [Fact]
    public void EnumeratesInSearchOrderShadowingLaterDirectoriesAndSortsByName()
    {
        var themes = ThemeCatalog.Enumerate(new[] { "first", "second", "absent" }, Listing);

        // "Shared" from the first directory hides "shared" in the second,
        // which is the file libghostty would load for either spelling.
        Assert.Equal(new[] { "alpha", "Beta", "Shared", "Zenburn" }, themes);
    }

    [Theory]
    [InlineData("Catppuccin Mocha", true)]
    [InlineData("iTerm2 Solarized Dark", true)]
    [InlineData("", false)]
    [InlineData(" leading", false)]
    [InlineData("trailing ", false)]
    [InlineData("light:a,dark:b", false)]
    [InlineData("a=b", false)]
    [InlineData("quo\"te", false)]
    [InlineData("sub\\dir", false)]
    [InlineData("C:\\themes\\abs", false)]
    [InlineData("tab\tname", false)]
    public void OnlyNamesThatRoundTripAsOneThemeArePersistable(string name, bool expected)
        => Assert.Equal(expected, ThemeCatalog.IsPersistableName(name));

    [Fact]
    public void FilterMatchesSubstringsCaseInsensitivelyInCatalogOrder()
    {
        var themes = new[] { "Dracula", "GitHub Dark", "GitHub Light", "Nord" };
        Assert.Equal(new[] { "GitHub Dark", "GitHub Light" }, ThemeCatalog.Filter(themes, "github"));
        Assert.Equal(new[] { "GitHub Dark" }, ThemeCatalog.Filter(themes, " dark "));
        Assert.Same(themes, ThemeCatalog.Filter(themes, ""));
        Assert.Empty(ThemeCatalog.Filter(themes, "zzz"));
    }

    [Fact]
    public void TheHighlightStartsOnTheActiveThemeWhenItIsListed()
    {
        var themes = new[] { "Dracula", "Nord" };
        Assert.Equal("Nord", ThemeCatalog.InitialSelection(themes, "nord"));
        Assert.Null(ThemeCatalog.InitialSelection(themes, "Missing"));
        Assert.Null(ThemeCatalog.InitialSelection(themes, ""));
        Assert.Null(ThemeCatalog.InitialSelection(themes, null));
    }

    [Fact]
    public void ReadsARealDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wintty-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Probe Two"), "background = #000000\n");
            File.WriteAllText(Path.Combine(dir, "probe one"), "background = #111111\n");
            Assert.Equal(new[] { "probe one", "Probe Two" }, ThemeCatalog.Enumerate(new[] { dir }));
            Assert.Empty(ThemeCatalog.Enumerate(new[] { Path.Combine(dir, "nope") }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
