using System;
using System.IO;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

public sealed class ConfigIniFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"wintty-ini-{Guid.NewGuid():N}");

    private string Write(string contents)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.wintty");
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void MissingFile_LoadsEmpty()
    {
        var file = ConfigIniFile.Load(Path.Combine(_dir, "does-not-exist"));
        Assert.Empty(file);
        Assert.Equal("fallback", ConfigIniFile.First(file, "anything", "fallback"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoPath_LoadsEmpty(string? path)
    {
        Assert.Empty(ConfigIniFile.Load(path));
    }

    [Fact]
    public void SkipsCommentsAndBlankLines()
    {
        var path = Write("""
            # windows-single-instance = true

              # indented comment
            windows-single-instance = false
            """);

        Assert.Equal("false", ConfigIniFile.First(ConfigIniFile.Load(path), "windows-single-instance"));
    }

    [Fact]
    public void EmptyValue_IsIgnoredEntirely()
    {
        // An empty value must not shadow the default, or clearing a key in
        // the file would read as a value rather than as "unset".
        var path = Write("windows-single-instance =\n");
        var file = ConfigIniFile.Load(path);

        Assert.Empty(file);
        Assert.Equal("", ConfigIniFile.First(file, "windows-single-instance"));
    }

    [Fact]
    public void EmptyValue_ResetsWhatAnEarlierLineSet()
    {
        // The other half of the same rule, and the shape a user actually
        // writes: blanking the line is how a key goes back to its default.
        // libghostty assigns the field default for an empty value, so the
        // parse has to stop counting the lines above it -- otherwise
        // `theme = ` after `theme = catppuccin` leaves the chrome reading a
        // palette nothing is wearing.
        var path = Write("""
            theme = catppuccin-mocha
            theme =
            """);

        var file = ConfigIniFile.Load(path);
        Assert.Empty(file);
        Assert.Equal("", ConfigIniFile.Last(file, "theme"));
        Assert.False(ConfigIniFile.TryLast(file, "theme", out _));
    }

    [Fact]
    public void AValueAfterAResetIsTheOneInForce()
    {
        var path = Write("""
            theme = catppuccin-mocha
            theme =
            theme = catppuccin-latte
            """);

        Assert.Equal(
            "catppuccin-latte",
            ConfigIniFile.Last(ConfigIniFile.Load(path), "theme"));
    }

    [Fact]
    public void AResetOfOneKeyLeavesTheOthersAlone()
    {
        var path = Write("""
            theme = catppuccin-mocha
            background = #ff0000
            theme =
            """);

        var file = ConfigIniFile.Load(path);
        Assert.Equal("#ff0000", ConfigIniFile.Last(file, "background"));
        Assert.False(file.ContainsKey("theme"));
    }

    [Fact]
    public void AResetRepeatableKeyDropsEveryEarlierValue()
    {
        // A blank value resets a repeatable key's whole list (Config.zig's
        // parseIntoField assigns the field default, which for a repeatable is
        // the empty list), so the keybind the user cleared is gone rather
        // than still bound from the line above it.
        var path = Write("""
            keybind = ctrl+a=copy
            keybind =
            keybind = ctrl+b=paste
            """);

        Assert.Equal(["ctrl+b=paste"], ConfigIniFile.Load(path)["keybind"]);
    }

    [Theory]
    [InlineData("windows-single-instance=true")]
    [InlineData("windows-single-instance =true")]
    [InlineData("windows-single-instance= true")]
    [InlineData("  windows-single-instance  =  true  ")]
    public void SpacingAroundTheSeparatorDoesNotMatter(string line)
    {
        var path = Write(line + "\n");
        Assert.Equal("true", ConfigIniFile.First(ConfigIniFile.Load(path), "windows-single-instance"));
    }

    [Fact]
    public void TrailingCommentStaysPartOfTheValue()
    {
        // Matches ghostty's own parser: # only starts a comment at the start of
        // a line. Pinned so nobody "fixes" it into a divergence.
        var path = Write("font-family = Cascadia Code # not a comment\n");
        Assert.Equal(
            "Cascadia Code # not a comment",
            ConfigIniFile.First(ConfigIniFile.Load(path), "font-family"));
    }

    [Fact]
    public void KeysAreCaseInsensitive()
    {
        var path = Write("Windows-Single-Instance = true\n");
        Assert.Equal("true", ConfigIniFile.First(ConfigIniFile.Load(path), "windows-single-instance"));
    }

    [Fact]
    public void RepeatedKey_KeepsFileOrderAndFirstWins()
    {
        var path = Write("""
            keybind = ctrl+a=copy
            keybind = ctrl+b=paste
            """);

        var file = ConfigIniFile.Load(path);
        Assert.Equal(["ctrl+a=copy", "ctrl+b=paste"], file["keybind"]);
        Assert.Equal("ctrl+a=copy", ConfigIniFile.First(file, "keybind"));
    }

    [Fact]
    public void RepeatedKey_LastWinsIsTheValueInForce()
    {
        // The defect itself, on the theme the settings UI reads: two
        // `theme` lines, and the panes wear the second one while a
        // first-wins reader hands the chrome the first.
        var path = Write("""
            theme = catppuccin-mocha
            theme = catppuccin-latte
            """);

        var file = ConfigIniFile.Load(path);
        Assert.Equal("catppuccin-latte", ConfigIniFile.Last(file, "theme"));
        Assert.True(ConfigIniFile.TryLast(file, "theme", out var value));
        Assert.Equal("catppuccin-latte", value);
    }

    [Fact]
    public void ValueMayContainEqualsSigns()
    {
        var path = Write("keybind = ctrl+shift+t=new_tab\n");
        Assert.Equal("ctrl+shift+t=new_tab", ConfigIniFile.First(ConfigIniFile.Load(path), "keybind"));
    }

    [Fact]
    public void LineWithoutSeparator_IsSkipped()
    {
        var path = Write("this-is-not-a-pair\nwindows-single-instance = true\n");
        var file = ConfigIniFile.Load(path);

        Assert.Single(file);
        Assert.Equal("true", ConfigIniFile.First(file, "windows-single-instance"));
    }

    [Fact]
    public void First_OnNullFile_ReturnsDefault()
    {
        Assert.Equal("off", ConfigIniFile.First(null, "windows-single-instance", "off"));
    }

    [Fact]
    public void Last_OnNullFileAndUnsetKey_ReturnsDefault()
    {
        Assert.Equal("off", ConfigIniFile.Last(null, "windows-single-instance", "off"));

        var file = ConfigIniFile.Load(Write("windows-single-instance = true\n"));
        Assert.Equal("off", ConfigIniFile.Last(file, "vertical-tabs", "off"));
        Assert.False(ConfigIniFile.TryLast(file, "vertical-tabs", out var value));
        Assert.Equal("", value);
    }
}
