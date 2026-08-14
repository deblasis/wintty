using System.IO;
using System.Linq;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

public class ThemeSearchPathTests
{
    private const string AppData = @"C:\Users\u\AppData\Roaming";

    [Fact]
    public void Config_directory_is_searched_first()
    {
        var dirs = ThemeSearchPath.UserDirectories(@"D:\dotfiles\wintty", AppData).ToList();

        Assert.Equal(Path.Combine(@"D:\dotfiles\wintty", "themes"), dirs[0]);
    }

    [Fact]
    public void Sibling_name_is_searched_under_the_config_root_not_app_data()
    {
        // With XDG_CONFIG_HOME set, libghostty's two user locations are both
        // under that root. Probing %APPDATA% instead would miss the sibling
        // and probe directories libghostty never looks at.
        var dirs = ThemeSearchPath.UserDirectories(@"D:\dotfiles\wintty", AppData).ToList();

        // Only two: the config directory's own themes dir and the sibling
        // under the same root are the same path when the config already
        // lives under the current name, so the dedupe collapses them.
        Assert.Equal(
            new[]
            {
                Path.Combine(@"D:\dotfiles", "wintty", "themes"),
                Path.Combine(@"D:\dotfiles", "ghostty", "themes"),
            },
            dirs);
        Assert.DoesNotContain(dirs, d => d.Contains("AppData"));
    }

    [Fact]
    public void Current_application_name_is_searched_before_the_pre_rename_one()
    {
        var dirs = ThemeSearchPath.UserDirectories(null, AppData).ToList();

        Assert.Equal(
            new[]
            {
                Path.Combine(AppData, "wintty", "themes"),
                Path.Combine(AppData, "ghostty", "themes"),
            },
            dirs);
    }

    [Fact]
    public void Config_directory_matching_a_sibling_is_not_searched_twice()
    {
        var dirs = ThemeSearchPath.UserDirectories(Path.Combine(AppData, "ghostty"), AppData).ToList();

        Assert.Equal(2, dirs.Count);
        Assert.Equal(Path.Combine(AppData, "ghostty", "themes"), dirs[0]);
        Assert.Equal(Path.Combine(AppData, "wintty", "themes"), dirs[1]);
    }

    [Fact]
    public void Dedupe_ignores_case_because_the_two_sources_are_cased_differently()
    {
        // The config directory arrives via GetFullPath of a Zig-produced
        // path; app data via the shell. They can differ only in case.
        var dirs = ThemeSearchPath.UserDirectories(@"C:\Users\u\AppData\Roaming\GHOSTTY", AppData).ToList();

        Assert.Equal(2, dirs.Count);
    }

    [Fact]
    public void Trailing_separator_on_the_config_directory_does_not_defeat_dedupe()
    {
        var dirs = ThemeSearchPath.UserDirectories(Path.Combine(AppData, "ghostty") + @"\", AppData).ToList();

        Assert.Equal(2, dirs.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_app_data_still_yields_from_the_config_directory(string? appData)
    {
        var dirs = ThemeSearchPath.UserDirectories(@"D:\cfg\wintty", appData).ToList();

        Assert.Equal(Path.Combine(@"D:\cfg\wintty", "themes"), dirs[0]);
        Assert.Contains(Path.Combine(@"D:\cfg", "ghostty", "themes"), dirs);
    }

    [Fact]
    public void No_directories_at_all_yields_nothing()
    {
        Assert.Empty(ThemeSearchPath.UserDirectories(null, null));
    }

    [Theory]
    [InlineData("Catppuccin Mocha")]
    [InlineData("3024 Night")]
    public void Plain_names_are_searchable(string name)
    {
        Assert.True(ThemeSearchPath.IsSearchableName(name));
    }

    [Theory]
    [InlineData(@"..\..\secrets.ini")]
    [InlineData("sub/theme")]
    [InlineData(@"sub\theme")]
    [InlineData("")]
    public void Relative_names_with_a_directory_component_are_not_searchable(string name)
    {
        // theme.zig rejects these outright, so resolving one here would
        // load a theme the terminal never applied.
        Assert.False(ThemeSearchPath.IsSearchableName(name));
    }

    [Fact]
    public void Absolute_names_are_not_searched_because_they_are_used_as_is()
    {
        // theme.zig routes an absolute theme through openAbsolute rather
        // than the search directories, and so does the caller here. This
        // is not a rejection.
        Assert.False(ThemeSearchPath.IsSearchableName(@"C:\themes\Mocha"));
    }
}
