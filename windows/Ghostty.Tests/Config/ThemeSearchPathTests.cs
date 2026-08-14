using System.IO;
using System.Linq;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

public class ThemeSearchPathTests
{
    private const string AppData = @"C:\Users\u\AppData\Roaming";

    [Fact]
    public void Config_directory_is_searched_before_the_fallbacks()
    {
        // It is the only entry that reflects XDG_CONFIG_HOME or a
        // redirected APPDATA, because libghostty resolved it.
        var dirs = ThemeSearchPath.UserDirectories(@"D:\dotfiles\wintty", AppData).ToList();

        Assert.Equal(Path.Combine(@"D:\dotfiles\wintty", "themes"), dirs[0]);
        Assert.Equal(3, dirs.Count);
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
    public void Config_directory_matching_a_fallback_is_not_searched_twice()
    {
        var dirs = ThemeSearchPath.UserDirectories(Path.Combine(AppData, "ghostty"), AppData).ToList();

        Assert.Equal(3 - 1, dirs.Count);
        Assert.Equal(Path.Combine(AppData, "ghostty", "themes"), dirs[0]);
        Assert.Equal(Path.Combine(AppData, "wintty", "themes"), dirs[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_app_data_still_yields_the_config_directory(string? appData)
    {
        var dirs = ThemeSearchPath.UserDirectories(@"D:\cfg", appData).ToList();

        Assert.Equal(Path.Combine(@"D:\cfg", "themes"), Assert.Single(dirs));
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
    [InlineData(@"C:\themes\Mocha")]
    [InlineData("")]
    public void Names_libghostty_refuses_are_not_searchable(string name)
    {
        // theme.zig rejects a relative name with a separator, and routes an
        // absolute one down a different path entirely. Resolving either
        // here would load a theme the terminal never applied.
        Assert.False(ThemeSearchPath.IsSearchableName(name));
    }
}
