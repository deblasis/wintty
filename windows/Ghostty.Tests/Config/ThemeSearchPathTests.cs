using System.IO;
using System.Linq;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

public class ThemeSearchPathTests
{
    private const string AppData = @"C:\Users\u\AppData\Roaming";

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

    // -- Bundled themes -----------------------------------------------------

    private const string AppDir = @"C:\Program Files\Wintty";
    private static readonly string Beside = Path.Combine(AppDir, "share", "ghostty", "themes");

    [Fact]
    public void Bundled_themes_are_found_beside_the_executable()
    {
        // theme.zig's Windows fallback when there is no resources directory.
        Assert.Equal(Beside, ThemeSearchPath.BundledDirectory(null, AppDir, d => d == Beside));
        Assert.Equal(Beside, ThemeSearchPath.BundledDirectory(null, AppDir + @"\", d => d == Beside));
    }

    [Fact]
    public void No_bundled_directory_when_nothing_is_shipped()
    {
        Assert.Null(ThemeSearchPath.BundledDirectory(null, AppDir, _ => false));
        Assert.Null(ThemeSearchPath.BundledDirectory(null, null, _ => true));
    }

    [Fact]
    public void Bundled_themes_are_found_one_directory_above_a_helper_in_bin()
    {
        // theme.zig's bundledThemesDir climbs one level for exactly this: an
        // executable shipped in bin would otherwise find no bundled themes,
        // and this method documents itself as following that rule.
        var bin = Path.Combine(AppDir, "bin");
        Assert.Equal(Beside, ThemeSearchPath.BundledDirectory(null, bin, d => d == Beside));
    }

    [Fact]
    public void The_bundled_climb_does_not_reach_outside_the_install()
    {
        // Two above the executable is not part of the install, and a theme
        // file is a config file, so a tree there must be out of reach rather
        // than merely later in a list. Raise BundledThemesMaxAncestors and
        // this returns the outside tree.
        var deep = Path.Combine(AppDir, "bin", "sub");
        var outside = Path.Combine(AppDir, "share", "ghostty", "themes");
        Assert.Null(ThemeSearchPath.BundledDirectory(null, deep, d => d == outside));
    }

    [Fact]
    public void A_valid_resources_directory_replaces_the_one_beside_the_executable()
    {
        // libghostty takes the environment's directory as the resources
        // directory and then looks for themes in it and nowhere else, whether
        // or not a themes subdirectory exists there.
        const string res = @"D:\ghostty\share\ghostty";
        const string sentinel = @"D:\ghostty\share\terminfo\ghostty.terminfo";
        var dir = ThemeSearchPath.BundledDirectory(
            res, AppDir, d => d == res || d == Beside, f => f == sentinel);
        Assert.Equal(Path.Combine(res, "themes"), dir);
    }

    [Fact]
    public void A_resources_directory_without_the_terminfo_sentinel_is_ignored()
    {
        // validResourcesDir on Windows requires the same sentinel detection
        // does, so that a folder a standard user can create is not enough to
        // redirect where the app reads from. A directory that merely exists
        // falls back to the themes beside the executable.
        const string res = @"D:\planted\share\ghostty";
        var dir = ThemeSearchPath.BundledDirectory(
            res, AppDir, d => d == res || d == Beside, _ => false);
        Assert.Equal(Beside, dir);
    }

    [Theory]
    [InlineData("share\\ghostty")]
    [InlineData(@"D:\missing\share\ghostty")]
    [InlineData("")]
    public void An_unusable_resources_directory_is_ignored(string res)
    {
        // validResourcesDir: relative or missing values fall back to detection.
        var dir = ThemeSearchPath.BundledDirectory(res, AppDir, d => d == Beside, _ => true);
        Assert.Equal(Beside, dir);
    }

    [Fact]
    public void Bundled_themes_are_searched_after_every_user_directory()
    {
        var dirs = ThemeSearchPath.Directories(@"D:\dotfiles\wintty", AppData, Beside).ToList();
        Assert.Equal(
            new[]
            {
                Path.Combine(@"D:\dotfiles", "wintty", "themes"),
                Path.Combine(@"D:\dotfiles", "ghostty", "themes"),
                Beside,
            },
            dirs);
    }

    [Fact]
    public void No_bundled_directory_leaves_the_user_directories()
    {
        Assert.Equal(
            ThemeSearchPath.UserDirectories(null, AppData),
            ThemeSearchPath.Directories(null, AppData, null));
    }

    [Fact]
    public void A_bundled_directory_that_is_also_a_user_directory_is_listed_once()
    {
        var user = Path.Combine(AppData, "wintty", "themes");
        var dirs = ThemeSearchPath.Directories(null, AppData, user.ToUpperInvariant()).ToList();
        Assert.Equal(ThemeSearchPath.UserDirectories(null, AppData), dirs);
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
    public void Relative_names_with_a_directory_component_are_not_searchable(string name)
    {
        // theme.zig rejects these outright, so resolving one here would
        // load a theme the terminal never applied.
        Assert.False(ThemeSearchPath.IsSearchableName(name));
    }

    [Fact]
    public void Empty_name_is_not_searchable()
    {
        Assert.False(ThemeSearchPath.IsSearchableName(""));
    }

    [Theory]
    [InlineData(@"C:\themes\Mocha")]
    [InlineData(@"\themes\Mocha")]
    [InlineData("/themes/Mocha")]
    [InlineData(@"\\server\share\Mocha")]
    public void Absolute_names_are_used_as_is_not_searched(string name)
    {
        // theme.zig routes these through openAbsolute rather than the
        // search directories, and so does ResolveThemePath.
        Assert.True(ThemeSearchPath.IsAbsolute(name));
        Assert.False(ThemeSearchPath.IsSearchableName(name));
    }

    [Theory]
    [InlineData("C:mocha")]
    [InlineData("C:")]
    public void Drive_relative_names_are_not_absolute(string name)
    {
        // Path.IsPathRooted accepts these, std.fs.path.isAbsoluteWindows
        // does not. Treating them as absolute would theme the chrome from
        // a file resolved against the drive's working directory, while
        // libghostty finds a directory component and refuses the theme.
        Assert.False(ThemeSearchPath.IsAbsolute(name));
    }

    [Theory]
    [InlineData("Catppuccin Mocha")]
    [InlineData("3024 Night")]
    public void Plain_names_are_not_absolute(string name)
    {
        Assert.False(ThemeSearchPath.IsAbsolute(name));
    }
}
